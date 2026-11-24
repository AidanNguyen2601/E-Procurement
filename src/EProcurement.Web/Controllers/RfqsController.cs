using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Chào hàng cạnh tranh (RFQ): mời ≥ N NCC → nhận báo giá trong hạn (niêm phong) → mở báo giá
/// → đánh giá kỹ thuật → so sánh, xếp hạng → đề xuất NCC → (nếu cần) Giám đốc duyệt kết quả → sinh PO → thông báo kết quả.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class RfqsController : ProcControllerBase
{
    public RfqsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(RfqStatus? status)
    {
        var q = Db.Rfqs.Include(r => r.Buyer).Include(r => r.Vendors).Include(r => r.AwardedVendor).Include(r => r.ProposedVendor).Include(r => r.PurchaseRequest).AsNoTracking();
        if (status.HasValue) q = q.Where(r => r.Status == status);
        ViewBag.Status = status;
        return View(await q.OrderByDescending(r => r.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.RFQ, id);
        var rvIds = r.Vendors.Select(v => v.Id).ToList();
        ViewBag.Online = await Db.VendorSubmissions.Where(s => s.RfqVendorId != null && rvIds.Contains(s.RfqVendorId.Value) && s.IsLatest && !s.IsWithdrawn)
            .ToDictionaryAsync(s => s.RfqVendorId!.Value, s => $"Nộp online lần {s.Revision} lúc {s.SubmittedAt:dd/MM HH:mm} · mã {s.Sha256.Substring(0, 10)}…" + (s.UnsealedAt == null ? " · niêm phong" : ""));
        ViewBag.Po = await Db.PurchaseOrders.FirstOrDefaultAsync(p => p.RfqId == id && p.Status != PoStatus.Cancelled);
        return View(r);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int? prId, int? packageId)
    {
        var vm = new RfqCreateViewModel();
        // Lập theo gói mua sắm đã duyệt trong KHLCNT (RFQ không gắn đề nghị = hỏi giá tham khảo, vẫn cho phép)
        var (pkg, err) = await PackageForAsync(packageId, prId, ProcurementMethod.CompetitiveQuotation);
        if (err != null) return BackToPackage(pkg, prId, err);
        if (pkg != null)
        {
            var lead = Packages.Lead(pkg);
            vm.PackageId = pkg.Id; vm.PurchaseRequestId = lead.Id; vm.PurchaseRequest = lead;
            vm.Title = pkg.Title;
            vm.Lines = Packages.Lines(pkg)
                .Select(l => new LineInput { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = l.EstimatedPrice }).ToList();
        }
        if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
        await LoadVendorChoicesAsync(vm);
        vm.VendorIds = vm.SuggestedVendors.Take(Settings.MinVendorsPerRfq).Select(v => v.Id).ToList();
        await LoadItemsJsonAsync();
        return View(vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RfqCreateViewModel vm, bool publish = false)
    {
        vm.Lines = CleanLines(vm.Lines);
        vm.VendorIds = vm.VendorIds.Distinct().ToList();
        if (vm.VendorIds.Count < Settings.MinVendorsPerRfq)
            ModelState.AddModelError(nameof(vm.VendorIds), I18n.T("Cần mời tối thiểu {0} nhà cung cấp để đảm bảo cạnh tranh.", Settings.MinVendorsPerRfq));
        if (vm.Deadline < DateTime.Today) ModelState.AddModelError(nameof(vm.Deadline), I18n.T("Hạn nộp báo giá không được ở quá khứ"));
        if (vm.EvaluationMethod == EvaluationMethod.WeightedScore && vm.PriceWeight + vm.TechnicalWeight + vm.DeliveryWeight != 100)
            ModelState.AddModelError(nameof(vm.PriceWeight), I18n.T("Tổng trọng số giá + kỹ thuật + tiến độ phải bằng 100%."));
        // Có đề nghị → bắt buộc theo gói đã duyệt; đề nghị / gói lấy lại từ gói, không tin form
        ProcurementPackage? pkg = null;
        if (vm.PackageId.HasValue || vm.PurchaseRequestId.HasValue)
        {
            (pkg, var perr) = await PackageForAsync(vm.PackageId, vm.PurchaseRequestId, ProcurementMethod.CompetitiveQuotation);
            if (perr != null) ModelState.AddModelError(string.Empty, perr);
            if (pkg != null) { vm.PackageId = pkg.Id; vm.PurchaseRequestId = Packages.Lead(pkg).Id; }
        }
        var invalid = await Db.Vendors.Where(v => vm.VendorIds.Contains(v.Id) && v.Status != VendorStatus.Approved).Select(v => v.Name).ToListAsync();
        if (invalid.Count > 0) ModelState.AddModelError(nameof(vm.VendorIds), I18n.T("NCC chưa được phê duyệt: ") + string.Join(", ", invalid));
        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
            if (vm.PurchaseRequestId.HasValue) vm.PurchaseRequest = await Db.PurchaseRequests.FindAsync(vm.PurchaseRequestId);
            await LoadVendorChoicesAsync(vm);
            await LoadItemsJsonAsync();
            return View(vm);
        }

        var rfq = new Rfq
        {
            Number = await DocNumbers.NextAsync(Db.Rfqs.Select(r => r.Number), "RFQ"), Title = vm.Title, PurchaseRequestId = vm.PurchaseRequestId, PackageId = vm.PackageId,
            BuyerId = Me, Deadline = vm.Deadline, Requirements = vm.Requirements, IssueDate = DateTime.Today,
            Status = publish ? RfqStatus.Open : RfqStatus.Draft, CreatedAt = DateTime.Now,
            EvaluationMethod = vm.EvaluationMethod, PriceWeight = vm.PriceWeight, TechnicalWeight = vm.TechnicalWeight, DeliveryWeight = vm.DeliveryWeight
        };
        var s = 1;
        foreach (var l in vm.Lines)
            rfq.Lines.Add(new RfqLine { ItemId = l.ItemId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, SortOrder = s++ });
        foreach (var vid in vm.VendorIds) rfq.Vendors.Add(new RfqVendor { VendorId = vid });
        Db.Rfqs.Add(rfq);
        await Db.SaveChangesAsync();

        Log(DocType.RFQ, rfq.Id, publish ? "Phát hành RFQ" : "Tạo RFQ nháp");
        if (pkg != null)
        {
            foreach (var pr in pkg.PurchaseRequests.Where(r => r.Status == PrStatus.Approved)) Log(DocType.PR, pr.Id, $"Mua hàng lập {rfq.Number} (gói {pkg.Number})");
            Log(DocType.Package, pkg.Id, $"Lập hồ sơ chào giá cạnh tranh {rfq.Number}");
            await Packages.StartAsync(Db, pkg.Id);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo {0}, mời {1} nhà cung cấp.", rfq.Number, vm.VendorIds.Count);
        return RedirectToAction(nameof(Details), new { id = rfq.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id)
    {
        var r = await Db.Rfqs.FindAsync(id);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.Draft) return BadRequest();
        if (r.Deadline < DateTime.Today)
        {
            TempData["Error"] = I18n.T("Hạn nộp báo giá đã qua. Gia hạn trước khi phát hành.");
            return RedirectToAction(nameof(Details), new { id });
        }
        r.Status = RfqStatus.Open;
        r.IssueDate = DateTime.Today;
        Log(DocType.RFQ, id, "Phát hành RFQ", $"Hạn nộp báo giá {r.Deadline.ToDate()}");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã phát hành {0}. In thư mời gửi NCC từ trang chi tiết.", r.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Gia hạn nộp báo giá (bắt buộc lý do; chỉ khi chưa mở báo giá)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExtendDeadline(int id, DateTime newDeadline, string? reason)
    {
        var r = await Db.Rfqs.FindAsync(id);
        if (r == null) return NotFound();
        if (r.Status is not (RfqStatus.Draft or RfqStatus.Open)) return BadRequest();
        if (newDeadline.Date <= r.Deadline || newDeadline.Date < DateTime.Today)
            TempData["Error"] = I18n.T("Hạn mới phải sau hạn hiện tại và không ở quá khứ.");
        else if (string.IsNullOrWhiteSpace(reason))
            TempData["Error"] = I18n.T("Nhập lý do gia hạn.");
        else
        {
            Log(DocType.RFQ, id, $"Gia hạn nộp báo giá {r.Deadline.ToDate()} → {newDeadline.ToDate()}", reason);
            r.Deadline = newDeadline.Date;
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Đã gia hạn {0} đến {1}. Thông báo lại cho các NCC được mời.", r.Number, r.Deadline.ToDate());
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Mở báo giá: sau hạn nộp (hoặc khi tất cả NCC đã gửi). Từ đây giá chào được công khai và không sửa được nữa.</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenQuotes(int id)
    {
        var r = await LoadRfqAsync(id, track: true);
        if (r == null) return NotFound();
        if (!RfqEvaluation.CanOpen(r))
        {
            TempData["Error"] = r.Vendors.Any(v => v.Responded)
                ? $"Chưa đến lúc mở: hạn nộp là hết ngày {r.Deadline.ToDate()} và vẫn còn NCC chưa gửi báo giá."
                : "Chưa có báo giá nào. Gia hạn hoặc hủy RFQ.";
            return RedirectToAction(nameof(Details), new { id });
        }
        // Báo giá nộp online: giải niêm phong, kiểm tra mã băm
        var online = await HttpContext.RequestServices.GetRequiredService<SealingService>().UnsealQuotesAsync(r, Me);
        r.Status = RfqStatus.Opened;
        r.OpenedAt = DateTime.Now;
        var summary = string.Join("; ", r.Vendors.Where(v => v.Responded).OrderBy(v => v.GrandTotal)
            .Select(v => $"{v.Vendor?.Name}: {v.GrandTotal.ToMoney()} đ (gồm VAT)"))
            + (online.Count > 0 ? " | Niêm phong online: " + string.Join("; ", online) : "");
        var missing = r.Vendors.Count(v => !v.Responded);
        Log(DocType.RFQ, id, $"Mở báo giá – {r.Vendors.Count(v => v.Responded)} báo giá" + (missing > 0 ? $", {missing} NCC không nộp" : ""), summary);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã mở báo giá. Tiếp theo: đánh giá kỹ thuật từng báo giá.");
        return RedirectToAction(nameof(Evaluate), new { id });
    }

    /// <summary>Đánh giá kỹ thuật: đạt / không đạt (+ điểm khi chấm điểm có trọng số)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Evaluate(int id)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.Opened)
        {
            TempData["Error"] = I18n.T("Chỉ đánh giá kỹ thuật sau khi mở báo giá và trước khi đề xuất kết quả.");
            return RedirectToAction(nameof(Details), new { id });
        }
        return View(new TechnicalEvaluationViewModel
        {
            RfqId = id, Rfq = r,
            Lines = r.Vendors.Where(v => v.Responded).OrderBy(v => v.Vendor!.Name).Select(v => new TechnicalEvaluationLine
                { RfqVendorId = v.Id, VendorName = v.Vendor!.Name, Pass = v.TechnicalPass, Score = v.TechnicalScore, Note = v.TechnicalNote }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(TechnicalEvaluationViewModel vm)
    {
        var r = await LoadRfqAsync(vm.RfqId, track: true);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.Opened) return BadRequest();
        foreach (var l in vm.Lines)
        {
            if (l.Pass == null) ModelState.AddModelError(string.Empty, I18n.T("{0}: chọn Đạt hoặc Không đạt.", l.VendorName));
            if (l.Pass == false && string.IsNullOrWhiteSpace(l.Note)) ModelState.AddModelError(string.Empty, I18n.T("{0}: nhập lý do không đạt kỹ thuật.", l.VendorName));
            if (l.Pass == true && r.EvaluationMethod == EvaluationMethod.WeightedScore && l.Score == null)
                ModelState.AddModelError(string.Empty, I18n.T("{0}: nhập điểm kỹ thuật (0–100).", l.VendorName));
        }
        if (!ModelState.IsValid) { vm.Rfq = r; return View(vm); }

        foreach (var l in vm.Lines)
        {
            var rv = r.Vendors.FirstOrDefault(v => v.Id == l.RfqVendorId && v.Responded);
            if (rv == null) continue;
            rv.TechnicalPass = l.Pass;
            rv.TechnicalScore = l.Pass == true ? l.Score : null;
            rv.TechnicalNote = string.IsNullOrWhiteSpace(l.Note) ? null : l.Note.Trim();
        }
        var failed = r.Vendors.Where(v => v.TechnicalPass == false).Select(v => v.Vendor?.Name).ToList();
        Log(DocType.RFQ, r.Id, "Đánh giá kỹ thuật", failed.Count == 0 ? "Tất cả báo giá đạt kỹ thuật" : "Không đạt: " + string.Join(", ", failed));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu đánh giá kỹ thuật. Xem bảng so sánh & xếp hạng để đề xuất NCC.");
        return RedirectToAction(nameof(Compare), new { id = r.Id });
    }

    /// <summary>Thư mời báo giá (in / gửi NCC)</summary>
    public async Task<IActionResult> Invitation(int id, int vendorId)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        var rv = r.Vendors.FirstOrDefault(v => v.VendorId == vendorId);
        if (rv == null) return NotFound();
        ViewBag.Vendor = rv.Vendor;
        return View(r);
    }

    /// <summary>Nhập báo giá nhận được từ một NCC</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> EnterQuote(int rfqVendorId)
    {
        var rv = await Db.RfqVendors.Include(x => x.Vendor).Include(x => x.QuoteLines).Include(x => x.Rfq).ThenInclude(r => r!.Lines)
            .FirstOrDefaultAsync(x => x.Id == rfqVendorId);
        if (rv == null) return NotFound();
        if (rv.Rfq!.Status != RfqStatus.Open)
        {
            TempData["Error"] = I18n.T("Chỉ nhập báo giá khi RFQ đang mở.");
            return RedirectToAction(nameof(Details), new { id = rv.RfqId });
        }
        if (await Db.VendorSubmissions.AnyAsync(s => s.RfqVendorId == rv.Id && s.IsLatest && !s.IsWithdrawn))
        {
            TempData["Error"] = I18n.T("{0} đã nộp báo giá online (niêm phong) – không nhập thay.", rv.Vendor?.Name);
            return RedirectToAction(nameof(Details), new { id = rv.RfqId });
        }
        return View(new QuoteEntryViewModel
        {
            RfqVendorId = rv.Id, RfqVendor = rv, LeadTimeDays = rv.LeadTimeDays,
            ReceivedDate = rv.RespondedAt?.Date ?? (DateTime.Today > rv.Rfq.Deadline ? rv.Rfq.Deadline : DateTime.Today), PaymentTerms = rv.PaymentTerms ?? rv.Vendor?.PaymentTerms,
            Warranty = rv.Warranty, VatPercent = Math.Round(rv.VatPercent, 0), Note = rv.Note,
            Lines = rv.Rfq.Lines.OrderBy(l => l.SortOrder).Select(l => new QuoteEntryLine
            {
                RfqLineId = l.Id, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = rv.QuoteLines.FirstOrDefault(q => q.RfqLineId == l.Id)?.UnitPrice ?? 0,
                Note = rv.QuoteLines.FirstOrDefault(q => q.RfqLineId == l.Id)?.Note
            }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnterQuote(QuoteEntryViewModel vm)
    {
        var rv = await Db.RfqVendors.Include(x => x.QuoteLines).Include(x => x.Rfq).ThenInclude(r => r!.Lines)
            .FirstOrDefaultAsync(x => x.Id == vm.RfqVendorId);
        if (rv == null) return NotFound();
        if (rv.Rfq!.Status != RfqStatus.Open) return BadRequest();
        if (await Db.VendorSubmissions.AnyAsync(s => s.RfqVendorId == rv.Id && s.IsLatest && !s.IsWithdrawn)) return BadRequest();
        if (vm.Lines.All(l => l.UnitPrice <= 0)) ModelState.AddModelError(string.Empty, I18n.T("Nhập đơn giá cho ít nhất một dòng."));
        if (vm.ReceivedDate.Date > rv.Rfq.Deadline)
            ModelState.AddModelError(nameof(vm.ReceivedDate), I18n.T("Báo giá gửi sau hạn {0} – không được chấp nhận.", rv.Rfq.Deadline.ToDate()));
        if (vm.ReceivedDate.Date > DateTime.Today || vm.ReceivedDate.Date < rv.Rfq.IssueDate)
            ModelState.AddModelError(nameof(vm.ReceivedDate), I18n.T("Ngày gửi báo giá phải từ ngày phát hành RFQ đến hôm nay."));
        if (!ModelState.IsValid)
        {
            vm.RfqVendor = await Db.RfqVendors.Include(x => x.Vendor).Include(x => x.Rfq).FirstOrDefaultAsync(x => x.Id == vm.RfqVendorId);
            return View(vm);
        }

        Db.VendorQuoteLines.RemoveRange(rv.QuoteLines);
        rv.QuoteLines.Clear();
        foreach (var l in vm.Lines.Where(l => l.UnitPrice > 0))
        {
            var line = rv.Rfq.Lines.FirstOrDefault(x => x.Id == l.RfqLineId);
            if (line == null) continue;
            rv.QuoteLines.Add(new VendorQuoteLine { RfqLineId = line.Id, UnitPrice = l.UnitPrice, Amount = Math.Round(l.UnitPrice * line.Quantity, 0), Note = l.Note });
        }
        rv.TotalAmount = rv.QuoteLines.Sum(q => q.Amount);
        rv.Responded = true;
        rv.RespondedAt = vm.ReceivedDate.Date;
        rv.LeadTimeDays = vm.LeadTimeDays; rv.PaymentTerms = vm.PaymentTerms; rv.Warranty = vm.Warranty;
        rv.VatPercent = vm.VatPercent; rv.Note = vm.Note;
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu báo giá của nhà cung cấp.");
        return RedirectToAction(nameof(Details), new { id = rv.RfqId });
    }

    /// <summary>Bảng so sánh & xếp hạng báo giá – chỉ xem được sau khi mở báo giá (niêm phong)</summary>
    public async Task<IActionResult> Compare(int id)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        if (!RfqEvaluation.IsUnsealed(r))
        {
            TempData["Error"] = I18n.T("Báo giá đang niêm phong – chỉ so sánh được sau khi mở báo giá.");
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new ComparisonViewModel { Rfq = r, MinValidQuotes = Settings.MinValidQuotes, Scores = RfqEvaluation.Evaluate(r) };
        vm.Responded = vm.Scores.Select(s => s.Quote).ToList();
        var validIds = vm.Scores.Where(s => s.Valid).Select(s => s.Quote.Id).ToHashSet();
        foreach (var line in r.Lines)
        {
            var prices = vm.Responded.Where(v => validIds.Contains(v.Id)).SelectMany(v => v.QuoteLines)
                .Where(q => q.RfqLineId == line.Id && q.UnitPrice > 0).Select(q => q.UnitPrice).ToList();
            if (prices.Count > 0) vm.LowestByLine[line.Id] = prices.Min();
        }
        vm.LowestTotalVendorId = vm.Scores.FirstOrDefault(s => s.Rank == 1)?.Quote.VendorId;
        if (r.PurchaseRequestId.HasValue)
            vm.ReferenceTotal = (await Db.PurchaseRequests.FindAsync(r.PurchaseRequestId))?.EstimatedTotal ?? 0;
        else
            vm.ReferenceTotal = r.Lines.Sum(l => l.Quantity * (l.Item?.ReferencePrice ?? 0));
        return View(vm);
    }

    /// <summary>
    /// Đề xuất NCC trúng. Chỉ chọn trong các báo giá hợp lệ (đủ dòng, đạt kỹ thuật).
    /// Không chọn hạng 1 hoặc số báo giá hợp lệ ít hơn mức tối thiểu → trình Giám đốc duyệt kết quả;
    /// ngược lại kết quả được duyệt ngay và sinh PO nháp.
    /// </summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Award(int id, int vendorId, string? reason)
    {
        var r = await LoadRfqAsync(id, track: true);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.Opened) return BadRequest();
        IActionResult Fail(string msg) { TempData["Error"] = msg; return RedirectToAction(nameof(Compare), new { id }); }
        if (string.IsNullOrWhiteSpace(reason)) return Fail(I18n.T("Nhập lý do chọn nhà cung cấp."));
        if (!RfqEvaluation.AllTechnicallyEvaluated(r)) return Fail(I18n.T("Đánh giá kỹ thuật tất cả báo giá trước khi đề xuất kết quả."));
        var scores = RfqEvaluation.Evaluate(r);
        var chosen = scores.FirstOrDefault(s => s.Quote.VendorId == vendorId);
        if (chosen == null || !chosen.Valid) return Fail(I18n.T("Chỉ chọn được báo giá hợp lệ (chào đủ các dòng và đạt kỹ thuật)."));

        var approvalReasons = new List<string>();
        if (chosen.Rank != 1) approvalReasons.Add($"Không chọn NCC xếp hạng 1 (chọn hạng {chosen.Rank})");
        var validCount = scores.Count(s => s.Valid);
        if (validCount < Settings.MinValidQuotes) approvalReasons.Add($"Chỉ có {validCount}/{Settings.MinValidQuotes} báo giá hợp lệ");

        r.ProposedVendorId = vendorId;
        r.AwardReason = reason.Trim();
        Log(DocType.RFQ, id, $"Đề xuất NCC trúng: {chosen.Quote.Vendor!.Name} (hạng {chosen.Rank}, {chosen.Quote.GrandTotal.ToMoney()} đ gồm VAT)", reason);

        if (approvalReasons.Count > 0 && !User.IsAdmin())
        {
            r.Status = RfqStatus.PendingApproval;
            r.ResultApprovalReason = string.Join("; ", approvalReasons);
            Log(DocType.RFQ, id, "Trình Giám đốc duyệt kết quả", r.ResultApprovalReason);
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Đã đề xuất {0}. Kết quả cần Giám đốc duyệt: {1}.", chosen.Quote.Vendor.Name, r.ResultApprovalReason);
            return RedirectToAction(nameof(Details), new { id });
        }
        r.ResultApprovalReason = approvalReasons.Count > 0 ? string.Join("; ", approvalReasons) : null;
        Log(DocType.RFQ, id, approvalReasons.Count > 0 ? "Quản trị duyệt kết quả" : "Duyệt kết quả (NCC hạng 1, đủ báo giá hợp lệ)");
        var po = await FinalizeAwardAsync(r);
        TempData["Success"] = I18n.T("Đã chọn {0} và tạo đơn mua hàng nháp {1}. Kiểm tra rồi trình duyệt / gửi NCC.", chosen.Quote.Vendor.Name, po.Number);
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    /// <summary>Giám đốc duyệt / trả lại kết quả lựa chọn NCC</summary>
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.RFQ)]
    public async Task<IActionResult> DecideResult(int id, bool approve, string? comment, string? returnUrl)
    {
        var r = await LoadRfqAsync(id, track: true);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.PendingApproval || r.ProposedVendorId == null) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            TempData["Error"] = I18n.T("Nhập lý do trả lại.");
            return RedirectToAction(nameof(Details), new { id });
        }
        IActionResult Back() => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
        if (!approve)
        {
            Log(DocType.RFQ, id, "Giám đốc trả lại kết quả", comment);
            r.Status = RfqStatus.Opened;
            r.ProposedVendorId = null;
            r.ResultApprovalReason = null;
            await Db.SaveChangesAsync();
            TempData["Success"] = I18n.T("Đã trả lại kết quả {0} để đánh giá lại.", r.Number);
            return Back();
        }
        Log(DocType.RFQ, id, "Giám đốc duyệt kết quả", comment);
        var po = await FinalizeAwardAsync(r);
        TempData["Success"] = I18n.T("Đã duyệt kết quả {0}, tạo đơn mua hàng nháp {1}.", r.Number, po.Number);
        return Back();
    }

    /// <summary>Biên bản mở & đánh giá báo giá (in)</summary>
    public async Task<IActionResult> Report(int id)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        if (!RfqEvaluation.IsUnsealed(r)) return RedirectToAction(nameof(Details), new { id });
        ViewBag.Scores = RfqEvaluation.Evaluate(r);
        ViewBag.History = await HistoryAsync(DocType.RFQ, id);
        ViewBag.MinValidQuotes = Settings.MinValidQuotes;
        return View(r);
    }

    /// <summary>Thông báo kết quả lựa chọn NCC gửi từng NCC tham dự (in)</summary>
    public async Task<IActionResult> ResultNotice(int id, int vendorId)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        var rv = r.Vendors.FirstOrDefault(v => v.VendorId == vendorId && v.Responded);
        if (r.Status != RfqStatus.Awarded || rv == null) return NotFound();
        ViewBag.Quote = rv;
        ViewBag.Score = RfqEvaluation.Evaluate(r).FirstOrDefault(s => s.Quote.VendorId == vendorId);
        return View(r);
    }

    /// <summary>Chốt kết quả: RFQ → Đã chọn NCC, sinh PO nháp theo giá đã chào</summary>
    private async Task<PurchaseOrder> FinalizeAwardAsync(Rfq r)
    {
        var rv = r.Vendors.First(v => v.VendorId == r.ProposedVendorId);
        r.Status = RfqStatus.Awarded;
        r.AwardedVendorId = rv.VendorId;
        var po = new PurchaseOrder
        {
            Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(p => p.Number), "PO"),
            VendorId = rv.VendorId, RfqId = r.Id, PurchaseRequestId = r.PurchaseRequestId, PackageId = r.PackageId, BuyerId = r.BuyerId, Method = ProcurementMethod.CompetitiveQuotation,
            DeliveryDate = DateTime.Today.AddDays(rv.LeadTimeDays ?? 7), PaymentTerms = rv.PaymentTerms ?? rv.Vendor?.PaymentTerms,
            DeliveryAddress = Settings.DefaultDeliveryAddress, VatPercent = rv.VatPercent,
            Notes = string.IsNullOrEmpty(rv.Warranty) ? null : "Bảo hành: " + rv.Warranty, CreatedAt = DateTime.Now
        };
        var s = 1;
        foreach (var line in r.Lines.OrderBy(l => l.SortOrder))
        {
            var q = rv.QuoteLines.FirstOrDefault(x => x.RfqLineId == line.Id);
            if (q == null || q.UnitPrice <= 0) continue;
            po.Lines.Add(new PurchaseOrderLine { ItemId = line.ItemId, Description = line.Description, Unit = line.Unit, Quantity = line.Quantity, UnitPrice = q.UnitPrice, SortOrder = s++ });
        }
        po.Recalculate();
        Db.PurchaseOrders.Add(po);
        await Db.SaveChangesAsync();
        Log(DocType.PO, po.Id, $"Tạo từ {r.Number}");
        await Db.SaveChangesAsync();
        return po;
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var r = await Db.Rfqs.FindAsync(id);
        if (r == null) return NotFound();
        if (r.Status is RfqStatus.Awarded or RfqStatus.Cancelled) return BadRequest();
        r.Status = RfqStatus.Cancelled;
        Log(DocType.RFQ, id, "Hủy RFQ", reason);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã hủy {0}.", r.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<Rfq?> LoadRfqAsync(int id, bool track = false)
    {
        var q = Db.Rfqs.Include(r => r.Buyer).Include(r => r.PurchaseRequest).Include(r => r.AwardedVendor).Include(r => r.ProposedVendor)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Vendors).ThenInclude(v => v.Vendor)
            .Include(r => r.Vendors).ThenInclude(v => v.QuoteLines);
        var r = track ? await q.FirstOrDefaultAsync(x => x.Id == id) : await q.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r != null) r.Lines = r.Lines.OrderBy(l => l.SortOrder).ToList();
        return r;
    }

    private async Task LoadVendorChoicesAsync(RfqCreateViewModel vm)
    {
        var itemIds = vm.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();
        var catIds = await Db.Items.Where(i => itemIds.Contains(i.Id) && i.CategoryId != null).Select(i => i.CategoryId!.Value).Distinct().ToListAsync();
        var approved = await Db.Vendors.Include(v => v.Categories).Where(v => v.Status == VendorStatus.Approved)
            .OrderByDescending(v => v.Rating).ToListAsync();
        // NCC còn hiệu lực sơ tuyển đạt cho nhóm hàng của RFQ được ưu tiên mời (xếp trước, theo điểm)
        var prequalified = (await PrequalificationRules.PrequalifiedVendorIds(Db, null, DateTime.Today)
            .Join(Db.PrequalificationApplications.Where(a => catIds.Contains(a.Prequalification!.CategoryId)), id => id, a => a.VendorId, (id, a) => id).Distinct().ToListAsync()).ToHashSet();
        ViewBag.Prequalified = prequalified;
        vm.SuggestedVendors = approved.Where(v => v.Categories.Any(c => catIds.Contains(c.CategoryId)) || prequalified.Contains(v.Id))
            .OrderByDescending(v => prequalified.Contains(v.Id)).ThenByDescending(v => v.Rating).ToList();
        vm.OtherVendors = approved.Except(vm.SuggestedVendors).ToList();
    }
}
