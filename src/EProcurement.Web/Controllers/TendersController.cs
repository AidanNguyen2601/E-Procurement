using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Đấu thầu rộng rãi:
/// Lập kế hoạch + HSMT + tổ chuyên gia → Giám đốc duyệt → Đăng tải công khai, phát hành HSMT
/// → Hỏi đáp / sửa đổi HSMT → Nhận HSDT đến thời điểm đóng thầu (niêm phong, từ chối nộp muộn)
/// → Mở thầu → Đánh giá hợp lệ, năng lực → Tổ chuyên gia chấm kỹ thuật độc lập → (2 túi: mở hồ sơ tài chính)
/// → Xếp hạng → Giám đốc duyệt kết quả → Thông báo, thời gian kiến nghị → Ký hợp đồng (ContractsController).
/// </summary>
public class TendersController : ProcControllerBase
{
    public TendersController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    private bool IsDirector => User.IsInRole(AppRoles.Director) || User.IsAdmin();
    private static bool IsMember(Tender t, int userId) => t.Committee.Any(m => m.UserId == userId);
    private bool CanView(Tender t) => User.IsPurchasing() || IsDirector || IsMember(t, Me);
    private bool IsLeaderOrBuyer(Tender t) => User.IsPurchasing() || t.Committee.Any(m => m.UserId == Me && m.IsLeader);

    // ------------------------------------------------------------------ Danh sách & chi tiết
    public async Task<IActionResult> Index(TenderStatus? status)
    {
        var q = Db.Tenders.Include(t => t.PurchaseRequest).Include(t => t.AwardedVendor).Include(t => t.Bidders).Include(t => t.Committee).AsNoTracking();
        if (!User.IsPurchasing() && !IsDirector) q = q.Where(t => t.Committee.Any(m => m.UserId == Me));
        if (status.HasValue) q = q.Where(t => t.Status == status);
        ViewBag.Status = status;
        return View(await q.OrderByDescending(t => t.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!CanView(t)) return Forbid();
        var bIds = t.Bidders.Select(b => b.Id).ToList();
        ViewBag.Online = await Db.VendorSubmissions.Where(s => s.TenderBidderId != null && bIds.Contains(s.TenderBidderId.Value) && s.IsLatest && !s.IsWithdrawn)
            .ToDictionaryAsync(s => s.TenderBidderId!.Value, s => $"Nộp online lần {s.Revision} lúc {s.SubmittedAt:dd/MM HH:mm} · mã {s.Sha256.Substring(0, 10)}…" + (s.UnsealedAt == null ? " · niêm phong" : ""));
        ViewBag.History = await HistoryAsync(DocType.Tender, id);
        ViewBag.Results = TenderEvaluation.Evaluate(t);
        ViewBag.Contract = await Db.Contracts.FirstOrDefaultAsync(c => c.TenderId == id);
        ViewBag.Attachments = await AttachmentPanelAsync(DocType.Tender, id, User.IsPurchasing() && t.Status is TenderStatus.Draft or TenderStatus.Published);
        ViewBag.Settings = Settings;
        if (User.IsPurchasing() && t.Status == TenderStatus.Draft)
            ViewBag.Users = new SelectList(await Db.Users.Where(u => u.IsActive && !t.Committee.Select(m => m.UserId).Contains(u.Id)).OrderBy(u => u.FullName)
                .Select(u => new { u.Id, Name = u.FullName + " – " + u.Department }).ToListAsync(), "Id", "Name");
        if (User.IsPurchasing() && t.Status == TenderStatus.Published)
            ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status != VendorStatus.Blacklisted && v.Status != VendorStatus.Suspended
                    && !t.Bidders.Select(b => b.VendorId).Contains(v.Id)).OrderBy(v => v.Name)
                .Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name");
        if (User.IsPurchasing() && t.Status == TenderStatus.Awarded)
            ViewBag.ComplaintVendors = new SelectList(t.Bidders.Where(b => b.Submitted).Select(b => new { b.VendorId, Name = b.Vendor!.Name }), "VendorId", "Name");
        return View(t);
    }

    // ------------------------------------------------------------------ Thông báo mời thầu công khai
    /// <summary>Danh sách gói thầu đang mời thầu – công khai, không cần đăng nhập</summary>
    [AllowAnonymous]
    public async Task<IActionResult> Public() =>
        View(await Db.Tenders.AsNoTracking().Where(t => t.Status != TenderStatus.Draft && t.Status != TenderStatus.PendingPlanApproval && t.Status != TenderStatus.Cancelled)
            .OrderByDescending(t => t.PublishedAt).Take(50).ToListAsync());

    /// <summary>Thông báo mời thầu + HSMT + hỏi đáp / sửa đổi + kết quả – công khai</summary>
    [AllowAnonymous]
    public async Task<IActionResult> Notice(int id)
    {
        var t = await LoadAsync(id);
        if (t == null || t.Status is TenderStatus.Draft or TenderStatus.PendingPlanApproval or TenderStatus.Cancelled) return NotFound();
        return View(t);
    }

    // ------------------------------------------------------------------ Lập kế hoạch & HSMT
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int prId)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == prId);
        if (pr == null) return NotFound();
        IActionResult BackToPr(string msg) { TempData["Error"] = msg; return RedirectToAction("Details", "PurchaseRequests", new { id = prId }); }
        if (pr.Status is not (PrStatus.Approved or PrStatus.Processing)) return BackToPr("Chỉ lập gói thầu cho đề nghị đã được duyệt.");
        if (pr.Method != ProcurementMethod.OpenTender) return BackToPr("Chọn hình thức \"Đấu thầu rộng rãi\" cho đề nghị trước khi lập gói thầu.");
        if (await Db.Tenders.AnyAsync(t => t.PurchaseRequestId == prId && t.Status != TenderStatus.Cancelled)) return BackToPr("Đề nghị đã có gói thầu đang xử lý.");
        var vm = new TenderFormViewModel
        {
            Number = "(tự sinh khi lưu)", PurchaseRequestId = prId, PurchaseRequest = pr, Title = pr.Title, PackageBudget = pr.EstimatedTotal,
            BidSecurityAmount = Math.Round(pr.EstimatedTotal * 0.01m / 1000) * 1000, // gợi ý 1% giá gói thầu
            ClosingAt = DateTime.Today.AddDays(Settings.TenderMinBiddingDays + 1).AddHours(9),
            TechnicalRequirements = pr.Purpose ?? string.Empty
        };
        ViewBag.Settings = Settings;
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var t = await Db.Tenders.Include(x => x.PurchaseRequest).ThenInclude(p => p!.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Draft) { TempData["Error"] = "Chỉ sửa KH & HSMT khi gói thầu ở trạng thái Nháp."; return RedirectToAction(nameof(Details), new { id }); }
        ViewBag.Settings = Settings;
        return View("Form", new TenderFormViewModel
        {
            Id = t.Id, Number = t.Number, PurchaseRequestId = t.PurchaseRequestId, PurchaseRequest = t.PurchaseRequest, Title = t.Title,
            PackageBudget = t.PackageBudget, EnvelopeMethod = t.EnvelopeMethod, ContractType = t.ContractType, ContractDurationDays = t.ContractDurationDays,
            IsFramework = t.IsFramework, TechnicalRequirements = t.TechnicalRequirements, QualificationCriteria = t.QualificationCriteria,
            ContractTerms = t.ContractTerms, EvaluationMethod = t.EvaluationMethod, MinTechnicalScore = t.MinTechnicalScore,
            TechnicalWeight = t.TechnicalWeight, PriceWeight = t.PriceWeight, BidSecurityAmount = t.BidSecurityAmount,
            BidValidityDays = t.BidValidityDays, PerformanceSecurityPercent = t.PerformanceSecurityPercent, ClosingAt = t.ClosingAt
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(TenderFormViewModel vm)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == vm.PurchaseRequestId);
        if (pr == null) return NotFound();
        Tender? t = null;
        if (vm.Id != 0)
        {
            t = await Db.Tenders.FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (t == null) return NotFound();
            if (t.Status != TenderStatus.Draft || t.PurchaseRequestId != pr.Id) return BadRequest();
        }
        else
        {
            if (pr.Method != ProcurementMethod.OpenTender || pr.Status is not (PrStatus.Approved or PrStatus.Processing))
                ModelState.AddModelError(string.Empty, "Đề nghị chưa được duyệt hoặc không theo hình thức Đấu thầu rộng rãi.");
            if (await Db.Tenders.AnyAsync(x => x.PurchaseRequestId == pr.Id && x.Status != TenderStatus.Cancelled))
                ModelState.AddModelError(string.Empty, "Đề nghị đã có gói thầu đang xử lý.");
        }
        if (vm.EvaluationMethod == TenderEvaluationMethod.Combined && vm.TechnicalWeight + vm.PriceWeight != 100)
            ModelState.AddModelError(nameof(vm.TechnicalWeight), "Trọng số kỹ thuật + giá phải bằng 100%.");
        if (vm.ClosingAt <= DateTime.Now) ModelState.AddModelError(nameof(vm.ClosingAt), "Thời điểm đóng thầu phải ở tương lai.");
        if (!ModelState.IsValid) { vm.PurchaseRequest = pr; ViewBag.Settings = Settings; return View("Form", vm); }

        if (t == null)
        {
            t = new Tender { Number = await DocNumbers.NextAsync(Db.Tenders.Select(x => x.Number), "TD"), PurchaseRequestId = pr.Id, BuyerId = Me, CreatedAt = DateTime.Now };
            var s = 1;
            foreach (var l in pr.Lines.OrderBy(l => l.SortOrder))
                t.Lines.Add(new TenderLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice, SortOrder = s++ });
            Db.Tenders.Add(t);
        }
        t.Title = vm.Title; t.PackageBudget = vm.PackageBudget; t.EnvelopeMethod = vm.EnvelopeMethod; t.ContractType = vm.ContractType;
        t.ContractDurationDays = vm.ContractDurationDays; t.IsFramework = vm.IsFramework; t.TechnicalRequirements = vm.TechnicalRequirements;
        t.QualificationCriteria = vm.QualificationCriteria; t.ContractTerms = vm.ContractTerms; t.EvaluationMethod = vm.EvaluationMethod;
        t.MinTechnicalScore = vm.MinTechnicalScore; t.TechnicalWeight = vm.TechnicalWeight; t.PriceWeight = vm.PriceWeight;
        t.BidSecurityAmount = vm.BidSecurityAmount; t.BidValidityDays = vm.BidValidityDays; t.PerformanceSecurityPercent = vm.PerformanceSecurityPercent;
        t.ClosingAt = vm.ClosingAt;
        await Db.SaveChangesAsync();
        if (vm.Id == 0)
        {
            Log(DocType.Tender, t.Id, "Lập kế hoạch lựa chọn NCC & HSMT", $"Giá gói thầu {t.PackageBudget.ToMoney()} đ");
            if (pr.Status == PrStatus.Approved) { pr.Status = PrStatus.Processing; Log(DocType.PR, pr.Id, $"Mua hàng lập gói thầu {t.Number}"); }
            await Db.SaveChangesAsync();
        }
        TempData["Success"] = $"Đã lưu {t.Number}. Thành lập tổ chuyên gia rồi trình duyệt KH & HSMT.";
        return RedirectToAction(nameof(Details), new { id = t.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(int id, int userId, bool isLeader = false)
    {
        var t = await Db.Tenders.Include(x => x.Committee).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Draft) return BadRequest();
        if (t.Committee.All(m => m.UserId != userId) && await Db.Users.AnyAsync(u => u.Id == userId && u.IsActive))
        {
            if (isLeader) foreach (var m in t.Committee) m.IsLeader = false;
            t.Committee.Add(new TenderCommitteeMember { UserId = userId, IsLeader = isLeader || t.Committee.Count == 0 });
            var name = await Db.Users.Where(u => u.Id == userId).Select(u => u.FullName).FirstAsync();
            Log(DocType.Tender, id, "Bổ sung tổ chuyên gia", name + (isLeader ? " (tổ trưởng)" : ""));
            await Db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveMember(int id, int memberId)
    {
        var t = await Db.Tenders.Include(x => x.Committee).ThenInclude(m => m.User).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Draft) return BadRequest();
        var m = t.Committee.FirstOrDefault(x => x.Id == memberId);
        if (m != null)
        {
            Db.TenderCommitteeMembers.Remove(m);
            Log(DocType.Tender, id, "Rút khỏi tổ chuyên gia", m.User?.FullName);
            await Db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitPlan(int id)
    {
        var t = await Db.Tenders.Include(x => x.Committee).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Draft) return BadRequest();
        var problems = PlanProblems(t);
        if (problems.Count > 0) { TempData["Error"] = "Chưa trình được: " + string.Join(" ", problems); return RedirectToAction(nameof(Details), new { id }); }
        t.Status = TenderStatus.PendingPlanApproval;
        Log(DocType.Tender, id, "Trình duyệt kế hoạch & HSMT", $"Đóng thầu {t.ClosingAt.ToDateTime()}, tổ chuyên gia {t.Committee.Count} người");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã trình {t.Number} lên Giám đốc.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Giám đốc duyệt KH & HSMT → phát hành, đăng tải thông báo mời thầu</summary>
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Tender)]
    public async Task<IActionResult> DecidePlan(int id, bool approve, string? comment, string? returnUrl)
    {
        var t = await Db.Tenders.Include(x => x.Committee).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.PendingPlanApproval) return BadRequest();
        IActionResult Back() => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
        if (!approve)
        {
            if (string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = "Nhập lý do trả lại."; return RedirectToAction(nameof(Details), new { id }); }
            t.Status = TenderStatus.Draft;
            Log(DocType.Tender, id, "Giám đốc trả lại KH & HSMT", comment);
        }
        else
        {
            var problems = PlanProblems(t);
            if (problems.Count > 0) { TempData["Error"] = "Không phát hành được: " + string.Join(" ", problems) + " Trả lại để bên mời thầu điều chỉnh."; return RedirectToAction(nameof(Details), new { id }); }
            t.Status = TenderStatus.Published;
            t.PublishedAt = DateTime.Now;
            Log(DocType.Tender, id, "Giám đốc duyệt KH & HSMT – phát hành, đăng tải thông báo mời thầu", comment);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? $"Đã phát hành {t.Number}. Thông báo mời thầu đã đăng công khai." : $"Đã trả lại {t.Number}.";
        return Back();
    }

    private List<string> PlanProblems(Tender t)
    {
        var p = new List<string>();
        if (t.Committee.Count < Settings.TenderMinCommittee) p.Add($"Tổ chuyên gia cần tối thiểu {Settings.TenderMinCommittee} thành viên.");
        if (!t.Committee.Any(m => m.IsLeader)) p.Add("Chỉ định tổ trưởng tổ chuyên gia.");
        if (t.ClosingAt < DateTime.Now.AddDays(Settings.TenderMinBiddingDays))
            p.Add($"Thời điểm đóng thầu phải sau ngày phát hành tối thiểu {Settings.TenderMinBiddingDays} ngày (sớm nhất {DateTime.Now.AddDays(Settings.TenderMinBiddingDays).ToDateTime()}).");
        if (t.EvaluationMethod == TenderEvaluationMethod.Combined && t.TechnicalWeight + t.PriceWeight != 100) p.Add("Trọng số kỹ thuật + giá phải bằng 100%.");
        return p;
    }

    // ------------------------------------------------------------------ Trong thời gian mời thầu
    /// <summary>Ghi nhận nhà thầu nhận / mua HSMT</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterBidder(int id, int vendorId)
    {
        var t = await Db.Tenders.Include(x => x.Bidders).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Published || DateTime.Now > t.ClosingAt) { TempData["Error"] = "Đã đóng thầu."; return RedirectToAction(nameof(Details), new { id }); }
        var v = await Db.Vendors.FindAsync(vendorId);
        if (v == null || v.Status is VendorStatus.Blacklisted or VendorStatus.Suspended) { TempData["Error"] = "NCC bị tạm ngưng / trong danh sách đen, không được dự thầu."; return RedirectToAction(nameof(Details), new { id }); }
        if (t.Bidders.All(b => b.VendorId != vendorId))
        {
            t.Bidders.Add(new TenderBidder { VendorId = vendorId, RegisteredAt = DateTime.Now });
            Log(DocType.Tender, id, "Nhà thầu nhận HSMT", v.Name);
            await Db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Trả lời làm rõ / sửa đổi HSMT (công khai). Sửa đổi sát hạn → bắt buộc gia hạn đóng thầu.</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddClarification(int id, string? question, string? answer, bool isAmendment, DateTime? newClosingAt)
    {
        var t = await Db.Tenders.FindAsync(id);
        if (t == null) return NotFound();
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (t.Status != TenderStatus.Published || DateTime.Now > t.ClosingAt) return Fail("Chỉ làm rõ / sửa đổi HSMT trước thời điểm đóng thầu.");
        if (string.IsNullOrWhiteSpace(answer)) return Fail("Nhập nội dung trả lời / sửa đổi.");
        var oldClosing = t.ClosingAt;
        if (newClosingAt.HasValue && newClosingAt > t.ClosingAt) t.ClosingAt = newClosingAt.Value;
        if (isAmendment && t.ClosingAt < DateTime.Now.AddDays(Settings.TenderAmendmentMinDays))
            return Fail($"Sửa đổi HSMT phải trước thời điểm đóng thầu tối thiểu {Settings.TenderAmendmentMinDays} ngày – gia hạn đóng thầu đến sau {DateTime.Now.AddDays(Settings.TenderAmendmentMinDays).ToDateTime()}.");
        Db.TenderClarifications.Add(new TenderClarification
        {
            TenderId = id, IsAmendment = isAmendment, Question = string.IsNullOrWhiteSpace(question) ? null : question.Trim(),
            Answer = answer.Trim(), CreatedById = Me, CreatedAt = DateTime.Now
        });
        Log(DocType.Tender, id, isAmendment ? "Sửa đổi HSMT" : "Làm rõ HSMT", answer.Trim());
        if (t.ClosingAt != oldClosing) Log(DocType.Tender, id, $"Gia hạn đóng thầu {oldClosing.ToDateTime()} → {t.ClosingAt.ToDateTime()}");
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã đăng tải nội dung làm rõ / sửa đổi trên thông báo mời thầu. Gửi thông báo tới tất cả nhà thầu đã nhận HSMT.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Tiếp nhận HSDT của nhà thầu (trước thời điểm đóng thầu)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> EnterBid(int bidderId)
    {
        var b = await Db.TenderBidders.Include(x => x.Vendor).Include(x => x.Lines).Include(x => x.Tender).ThenInclude(t => t!.Lines).FirstOrDefaultAsync(x => x.Id == bidderId);
        if (b == null) return NotFound();
        if (b.Tender!.Status != TenderStatus.Published || DateTime.Now > b.Tender.ClosingAt)
        { TempData["Error"] = "Đã quá thời điểm đóng thầu – không tiếp nhận HSDT."; return RedirectToAction(nameof(Details), new { id = b.TenderId }); }
        if (await Db.VendorSubmissions.AnyAsync(s => s.TenderBidderId == b.Id && s.IsLatest && !s.IsWithdrawn))
        { TempData["Error"] = $"{b.Vendor?.Name} đã nộp HSDT online (niêm phong) – không nhập thay."; return RedirectToAction(nameof(Details), new { id = b.TenderId }); }
        return View(new BidEntryViewModel
        {
            TenderBidderId = b.Id, Bidder = b, DiscountPercent = b.DiscountPercent, VatPercent = Math.Round(b.VatPercent, 0), LeadTimeDays = b.LeadTimeDays,
            ValidityDays = b.ValidityDays ?? b.Tender.BidValidityDays, BidSecurityAmount = b.Submitted ? b.BidSecurityAmount : b.Tender.BidSecurityAmount,
            BidSecurityRef = b.BidSecurityRef, BidSecurityExpiry = b.BidSecurityExpiry ?? b.Tender.ClosingAt.Date.AddDays(b.Tender.BidValidityDays + 30), Note = b.Note,
            Lines = b.Tender.Lines.OrderBy(l => l.SortOrder).Select(l => new QuoteEntryLine
            { RfqLineId = l.Id, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = b.Lines.FirstOrDefault(x => x.TenderLineId == l.Id)?.UnitPrice ?? 0 }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnterBid(BidEntryViewModel vm)
    {
        var b = await Db.TenderBidders.Include(x => x.Vendor).Include(x => x.Lines).Include(x => x.Tender).ThenInclude(t => t!.Lines).FirstOrDefaultAsync(x => x.Id == vm.TenderBidderId);
        if (b == null) return NotFound();
        var t = b.Tender!;
        if (await Db.VendorSubmissions.AnyAsync(s => s.TenderBidderId == b.Id && s.IsLatest && !s.IsWithdrawn)) return BadRequest();
        if (t.Status != TenderStatus.Published || DateTime.Now > t.ClosingAt)
        {
            Log(DocType.Tender, t.Id, "Từ chối HSDT nộp muộn", $"{b.Vendor?.Name} nộp lúc {DateTime.Now.ToDateTime()}, đóng thầu {t.ClosingAt.ToDateTime()}");
            await Db.SaveChangesAsync();
            TempData["Error"] = $"HSDT của {b.Vendor?.Name} nộp sau thời điểm đóng thầu {t.ClosingAt.ToDateTime()} – không được tiếp nhận.";
            return RedirectToAction(nameof(Details), new { id = t.Id });
        }
        var lines = t.Lines.ToDictionary(l => l.Id);
        if (vm.Lines.Any(l => !lines.ContainsKey(l.RfqLineId)) || vm.Lines.Count != lines.Count || vm.Lines.Any(l => l.UnitPrice <= 0))
            ModelState.AddModelError(string.Empty, "HSDT phải chào giá đủ tất cả các hạng mục.");
        if (!ModelState.IsValid) { vm.Bidder = b; return View(vm); }

        Db.TenderBidLines.RemoveRange(b.Lines);
        b.Lines.Clear();
        foreach (var l in vm.Lines) b.Lines.Add(new TenderBidLine { TenderLineId = l.RfqLineId, UnitPrice = l.UnitPrice });
        b.BidPrice = vm.Lines.Sum(l => Math.Round(l.UnitPrice * lines[l.RfqLineId].Quantity, 0));
        b.DiscountPercent = vm.DiscountPercent; b.VatPercent = vm.VatPercent; b.LeadTimeDays = vm.LeadTimeDays; b.ValidityDays = vm.ValidityDays;
        b.BidSecurityAmount = vm.BidSecurityAmount; b.BidSecurityRef = vm.BidSecurityRef; b.BidSecurityExpiry = vm.BidSecurityExpiry; b.Note = vm.Note;
        var first = !b.Submitted;
        b.Submitted = true;
        b.SubmittedAt = DateTime.Now;
        Log(DocType.Tender, t.Id, first ? "Tiếp nhận HSDT" : "Nhà thầu nộp HSDT thay thế", $"{b.Vendor?.Name} – {DateTime.Now.ToDateTime()}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã tiếp nhận HSDT của {b.Vendor?.Name}. HSDT được niêm phong đến khi mở thầu.";
        return RedirectToAction(nameof(Details), new { id = t.Id });
    }

    // ------------------------------------------------------------------ Mở thầu & đánh giá
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenBids(int id)
    {
        var t = await LoadAsync(id, track: true);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Published) return BadRequest();
        if (DateTime.Now < t.ClosingAt) { TempData["Error"] = $"Chưa đến thời điểm đóng thầu {t.ClosingAt.ToDateTime()}."; return RedirectToAction(nameof(Details), new { id }); }
        // HSDT nộp online: mở niêm phong (hai túi: giá còn niêm phong đến khi mở hồ sơ tài chính)
        var online = await HttpContext.RequestServices.GetRequiredService<SealingService>()
            .UnsealBidsAsync(t, Me, includePrices: t.EnvelopeMethod == EnvelopeMethod.OneEnvelope);
        var bids = t.Bidders.Where(b => b.Submitted).ToList();
        if (bids.Count == 0) { TempData["Error"] = "Không có HSDT nào. Gia hạn bằng sửa đổi HSMT hoặc hủy gói thầu."; return RedirectToAction(nameof(Details), new { id }); }
        t.Status = TenderStatus.Opened;
        t.OpenedAt = DateTime.Now;
        var record = t.EnvelopeMethod == EnvelopeMethod.OneEnvelope
            ? string.Join("; ", bids.OrderBy(b => b.Vendor?.Name).Select(b => $"{b.Vendor?.Name}: {b.BidPrice.ToMoney()} đ, giảm {b.DiscountPercent:0.##}%, BĐDT {b.BidSecurityAmount.ToMoney()} đ"))
            : string.Join("; ", bids.OrderBy(b => b.Vendor?.Name).Select(b => $"{b.Vendor?.Name}: BĐDT {b.BidSecurityAmount.ToMoney()} đ")) + " (hồ sơ tài chính còn niêm phong)";
        Log(DocType.Tender, id, $"Mở thầu – {bids.Count} HSDT", record + (online.Count > 0 ? " | Nộp online: " + string.Join("; ", online) : ""));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã mở thầu. Tiếp theo: đánh giá tính hợp lệ và năng lực từng HSDT.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đánh giá hợp lệ + năng lực (bên mời thầu / tổ trưởng)</summary>
    public async Task<IActionResult> Screen(int id)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        if (!IsLeaderOrBuyer(t)) return Forbid();
        if (t.Status != TenderStatus.Opened) return RedirectToAction(nameof(Details), new { id });
        return View(new BidScreeningViewModel
        {
            TenderId = id, Tender = t,
            Lines = t.Bidders.Where(b => b.Submitted).OrderBy(b => b.Vendor!.Name).Select(b => new BidScreeningLine
            { TenderBidderId = b.Id, VendorName = b.Vendor!.Name, Valid = b.Valid, ValidNote = b.ValidNote, Qualified = b.Qualified, QualifiedNote = b.QualifiedNote }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Screen(BidScreeningViewModel vm)
    {
        var t = await LoadAsync(vm.TenderId, track: true);
        if (t == null) return NotFound();
        if (!IsLeaderOrBuyer(t)) return Forbid();
        if (t.Status != TenderStatus.Opened) return BadRequest();
        // Dựng lại danh sách theo các HSDT đã nộp (không tin mã / tên từ form); thiếu dòng nào coi như chưa đánh giá
        var posted = vm.Lines.Where(l => l.TenderBidderId > 0).GroupBy(l => l.TenderBidderId).ToDictionary(g => g.Key, g => g.First());
        vm.Lines = t.Bidders.Where(b => b.Submitted).OrderBy(b => b.Vendor!.Name).Select(b => posted.TryGetValue(b.Id, out var p)
            ? new BidScreeningLine { TenderBidderId = b.Id, VendorName = b.Vendor!.Name, Valid = p.Valid, ValidNote = p.ValidNote, Qualified = p.Qualified, QualifiedNote = p.QualifiedNote }
            : new BidScreeningLine { TenderBidderId = b.Id, VendorName = b.Vendor!.Name }).ToList();
        ModelState.Clear();
        foreach (var l in vm.Lines)
        {
            if (l.Valid == null) ModelState.AddModelError(string.Empty, $"{l.VendorName}: chọn Hợp lệ / Không hợp lệ.");
            if (l.Valid == false && string.IsNullOrWhiteSpace(l.ValidNote)) ModelState.AddModelError(string.Empty, $"{l.VendorName}: ghi lý do không hợp lệ.");
            if (l.Valid == true && l.Qualified == null) ModelState.AddModelError(string.Empty, $"{l.VendorName}: chọn Đạt / Không đạt năng lực.");
            if (l.Valid == true && l.Qualified == false && string.IsNullOrWhiteSpace(l.QualifiedNote)) ModelState.AddModelError(string.Empty, $"{l.VendorName}: ghi lý do không đạt năng lực.");
        }
        if (!ModelState.IsValid) { vm.Tender = t; return View(vm); }
        foreach (var l in vm.Lines)
        {
            var b = t.Bidders.FirstOrDefault(x => x.Id == l.TenderBidderId && x.Submitted);
            if (b == null) continue;
            b.Valid = l.Valid; b.ValidNote = Cut(l.ValidNote, 500);
            b.Qualified = l.Valid == true ? l.Qualified : null; b.QualifiedNote = l.Valid == true ? Cut(l.QualifiedNote, 500) : null;
            if (b.Valid != true || b.Qualified != true)
            {
                Db.TenderScores.RemoveRange(b.Scores); // HSDT bị loại không chấm kỹ thuật
                b.Scores.Clear();
            }
        }
        var removed = t.Bidders.Where(b => b.Submitted && (b.Valid == false || b.Qualified == false)).Select(b => b.Vendor?.Name).ToList();
        Log(DocType.Tender, t.Id, "Đánh giá hợp lệ & năng lực", removed.Count == 0 ? "Tất cả HSDT đạt" : "Bị loại: " + string.Join(", ", removed));
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã lưu đánh giá hợp lệ & năng lực. Các thành viên tổ chuyên gia tiến hành chấm kỹ thuật.";
        return RedirectToAction(nameof(Details), new { id = t.Id });
    }

    /// <summary>Thành viên tổ chuyên gia cam kết không xung đột lợi ích với các nhà thầu</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeclareConflict(int id)
    {
        var m = await Db.TenderCommitteeMembers.Include(x => x.Tender).FirstOrDefaultAsync(x => x.TenderId == id && x.UserId == Me);
        if (m == null) return Forbid();
        if (m.ConflictDeclaredAt == null)
        {
            m.ConflictDeclaredAt = DateTime.Now;
            Log(DocType.Tender, id, "Cam kết không xung đột lợi ích", User.GetFullName());
            await Db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Score), new { id });
    }

    /// <summary>Chấm điểm kỹ thuật – mỗi thành viên chỉ thấy điểm của mình</summary>
    public async Task<IActionResult> Score(int id)
    {
        var t = await LoadAsync(id);
        if (t == null) return NotFound();
        var me = t.Committee.FirstOrDefault(m => m.UserId == Me);
        if (me == null) return Forbid();
        ViewBag.Member = me;
        var responsive = t.Bidders.Where(b => b.Submitted && b.Valid == true && b.Qualified == true).OrderBy(b => b.Vendor!.Name).ToList();
        return View(new TenderScoringViewModel
        {
            TenderId = id, Tender = t, Complete = me.ScoringCompletedAt.HasValue,
            Lines = responsive.Select(b => { var s = b.Scores.FirstOrDefault(x => x.MemberUserId == Me);
                return new TenderScoringLine { TenderBidderId = b.Id, VendorName = b.Vendor!.Name, Score = s?.TechnicalScore, Comment = s?.Comment }; }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Score(TenderScoringViewModel vm, bool complete = false)
    {
        var t = await LoadAsync(vm.TenderId, track: true);
        if (t == null) return NotFound();
        var me = t.Committee.FirstOrDefault(m => m.UserId == Me);
        if (me == null) return Forbid();
        if (t.Status != TenderStatus.Opened || me.ScoringCompletedAt.HasValue) { TempData["Error"] = "Không còn chấm điểm được."; return RedirectToAction(nameof(Details), new { id = t.Id }); }
        if (me.ConflictDeclaredAt == null) { TempData["Error"] = "Ký cam kết không xung đột lợi ích trước khi chấm."; return RedirectToAction(nameof(Score), new { id = t.Id }); }
        if (t.Bidders.Any(b => b.Submitted && b.Valid == null)) { TempData["Error"] = "Chờ bên mời thầu đánh giá hợp lệ & năng lực trước."; return RedirectToAction(nameof(Score), new { id = t.Id }); }
        // Chỉ nhận dòng của HSDT đạt hợp lệ + năng lực; dựng lại danh sách theo dữ liệu thật (không tin form)
        var responsive = t.Bidders.Where(b => b.Submitted && b.Valid == true && b.Qualified == true).OrderBy(b => b.Vendor!.Name).ToList();
        var posted = vm.Lines.Where(l => l.TenderBidderId > 0).GroupBy(l => l.TenderBidderId).ToDictionary(g => g.Key, g => g.First());
        vm.Lines = responsive.Select(b => posted.TryGetValue(b.Id, out var p)
            ? new TenderScoringLine { TenderBidderId = b.Id, VendorName = b.Vendor!.Name, Score = p.Score, Comment = p.Comment }
            : new TenderScoringLine { TenderBidderId = b.Id, VendorName = b.Vendor!.Name }).ToList();
        ModelState.Clear();
        if (vm.Lines.Any(l => l.Score is < 0 or > 100)) ModelState.AddModelError(string.Empty, "Điểm kỹ thuật từ 0 đến 100.");
        if (complete && vm.Lines.Any(l => l.Score == null)) ModelState.AddModelError(string.Empty, "Chấm đủ điểm tất cả HSDT trước khi hoàn thành.");
        if (!ModelState.IsValid) { vm.Tender = t; ViewBag.Member = me; return View(vm); }
        foreach (var l in vm.Lines)
        {
            var b = t.Bidders.FirstOrDefault(x => x.Id == l.TenderBidderId && x.Valid == true && x.Qualified == true);
            if (b == null) continue;
            var s = b.Scores.FirstOrDefault(x => x.MemberUserId == Me);
            if (l.Score == null) { if (s != null) { Db.TenderScores.Remove(s); } continue; }
            if (s == null) { s = new TenderScore { MemberUserId = Me }; b.Scores.Add(s); }
            s.TechnicalScore = l.Score.Value; s.Comment = Cut(l.Comment, 500); s.ScoredAt = DateTime.Now;
        }
        if (complete)
        {
            me.ScoringCompletedAt = DateTime.Now;
            Log(DocType.Tender, t.Id, "Thành viên hoàn thành chấm kỹ thuật", User.GetFullName());
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = complete ? "Đã nộp phiếu chấm. Điểm của các thành viên chỉ được công khai khi cả tổ hoàn thành." : "Đã lưu nháp phiếu chấm.";
        return RedirectToAction(complete ? nameof(Details) : nameof(Score), new { id = t.Id });
    }

    /// <summary>Hai túi hồ sơ: mở hồ sơ tài chính của các HSDT đạt kỹ thuật</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> OpenFinancial(int id)
    {
        var t = await LoadAsync(id, track: true);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Opened || t.EnvelopeMethod != EnvelopeMethod.TwoEnvelope || t.FinancialOpenedAt.HasValue) return BadRequest();
        if (!TenderEvaluation.ScoringComplete(t)) { TempData["Error"] = "Tổ chuyên gia chưa hoàn thành đánh giá kỹ thuật."; return RedirectToAction(nameof(Details), new { id }); }
        t.FinancialOpenedAt = DateTime.Now;
        await HttpContext.RequestServices.GetRequiredService<SealingService>().UnsealBidsAsync(t, Me, includePrices: true, pricesOnly: true);
        var passed = TenderEvaluation.Evaluate(t).Where(r => r.TechnicalPass).ToList();
        Log(DocType.Tender, id, $"Mở hồ sơ tài chính – {passed.Count} HSDT đạt kỹ thuật",
            string.Join("; ", passed.Select(r => $"{r.Bid.Vendor?.Name}: {r.Bid.BidPrice.ToMoney()} đ, giảm {r.Bid.DiscountPercent:0.##}%")));
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đề xuất trúng thầu: bắt buộc nhà thầu xếp hạng 1</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ProposeAward(int id, string? reason)
    {
        var t = await LoadAsync(id, track: true);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Opened) return BadRequest();
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (!TenderEvaluation.ScoringComplete(t)) return Fail("Tổ chuyên gia chưa hoàn thành đánh giá.");
        if (!TenderEvaluation.PricesVisible(t)) return Fail("Mở hồ sơ tài chính trước khi xếp hạng.");
        var first = TenderEvaluation.Evaluate(t).FirstOrDefault(r => r.Rank == 1);
        if (first == null) return Fail("Không có HSDT nào đạt kỹ thuật – cân nhắc hủy gói thầu và tổ chức lại.");
        t.AwardedVendorId = first.Bid.VendorId;
        t.AwardPrice = first.Bid.NetPrice;
        t.AwardReason = string.IsNullOrWhiteSpace(reason) ? "Xếp hạng thứ nhất theo phương pháp đánh giá của HSMT" : reason.Trim();
        t.Status = TenderStatus.PendingResultApproval;
        Log(DocType.Tender, id, $"Trình duyệt kết quả: {first.Bid.Vendor?.Name} – {first.Bid.NetPrice.ToMoney()} đ (trước VAT)", t.AwardReason);
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã trình Giám đốc phê duyệt kết quả lựa chọn nhà thầu.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Tender)]
    public async Task<IActionResult> DecideResult(int id, bool approve, string? comment, string? returnUrl)
    {
        var t = await Db.Tenders.FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.PendingResultApproval) return BadRequest();
        IActionResult Back() => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
        if (!approve)
        {
            if (string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = "Nhập lý do trả lại."; return RedirectToAction(nameof(Details), new { id }); }
            t.Status = TenderStatus.Opened; t.AwardedVendorId = null; t.AwardPrice = null;
            Log(DocType.Tender, id, "Giám đốc trả lại kết quả", comment);
        }
        else
        {
            t.Status = TenderStatus.Awarded;
            t.ComplaintDeadline = DateTime.Now.AddDays(Settings.TenderComplaintDays);
            Log(DocType.Tender, id, "Giám đốc phê duyệt kết quả lựa chọn nhà thầu – công khai kết quả", comment);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? $"Đã phê duyệt kết quả {t.Number}. Gửi thông báo kết quả cho các nhà thầu; hạn kiến nghị {t.ComplaintDeadline.ToDateTime()}." : "Đã trả lại kết quả.";
        return Back();
    }

    // ------------------------------------------------------------------ Kiến nghị
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComplaint(int id, int vendorId, string? content)
    {
        var t = await Db.Tenders.Include(x => x.Bidders).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (t.Status != TenderStatus.Awarded) return Fail("Chỉ nhận kiến nghị sau khi công bố kết quả.");
        if (DateTime.Now > t.ComplaintDeadline) return Fail($"Đã hết thời hạn kiến nghị ({t.ComplaintDeadline.ToDateTime()}).");
        if (t.Bidders.All(b => b.VendorId != vendorId || !b.Submitted)) return Fail("Chỉ nhà thầu đã nộp HSDT được kiến nghị.");
        if (string.IsNullOrWhiteSpace(content)) return Fail("Nhập nội dung kiến nghị.");
        Db.TenderComplaints.Add(new TenderComplaint { TenderId = id, VendorId = vendorId, Content = content.Trim(), ReceivedAt = DateTime.Now });
        var name = await Db.Vendors.Where(v => v.Id == vendorId).Select(v => v.Name).FirstAsync();
        Log(DocType.Tender, id, "Tiếp nhận kiến nghị của nhà thầu", $"{name}: {content.Trim()}");
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã ghi nhận kiến nghị. Chưa được ký hợp đồng cho tới khi Giám đốc giải quyết.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Giám đốc giải quyết kiến nghị. Kiến nghị đúng → hủy kết quả, quay lại đánh giá.</summary>
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveComplaint(int id, int complaintId, bool upheld, string? response, string? returnUrl)
    {
        var c = await Db.TenderComplaints.Include(x => x.Tender).Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == complaintId && x.TenderId == id);
        if (c == null) return NotFound();
        if (c.ResolvedAt.HasValue || c.Tender!.Status != TenderStatus.Awarded) return BadRequest();
        if (string.IsNullOrWhiteSpace(response)) { TempData["Error"] = "Nhập nội dung trả lời kiến nghị."; return RedirectToAction(nameof(Details), new { id }); }
        c.Upheld = upheld; c.Response = response.Trim(); c.ResolvedAt = DateTime.Now; c.ResolvedById = Me;
        Log(DocType.Tender, id, upheld ? "Chấp nhận kiến nghị – hủy kết quả, đánh giá lại" : "Bác kiến nghị", $"{c.Vendor?.Name}: {c.Response}");
        if (upheld)
        {
            var t = c.Tender;
            t.Status = TenderStatus.Opened; t.AwardedVendorId = null; t.AwardPrice = null; t.ComplaintDeadline = null;
            // Mở lại phiếu chấm để tổ chuyên gia xem xét lại
            foreach (var m in await Db.TenderCommitteeMembers.Where(m => m.TenderId == id).ToListAsync()) m.ScoringCompletedAt = null;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = upheld ? "Đã chấp nhận kiến nghị. Gói thầu quay lại bước đánh giá." : "Đã trả lời kiến nghị.";
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Bản in
    /// <summary>Biên bản mở thầu + báo cáo đánh giá HSDT</summary>
    public async Task<IActionResult> Report(int id)
    {
        var t = await LoadAsync(id);
        if (t == null || !t.OpenedAt.HasValue) return NotFound();
        if (!CanView(t)) return Forbid();
        ViewBag.Results = TenderEvaluation.Evaluate(t);
        ViewBag.History = await HistoryAsync(DocType.Tender, id);
        return View(t);
    }

    /// <summary>Thông báo kết quả lựa chọn nhà thầu gửi từng nhà thầu</summary>
    public async Task<IActionResult> ResultNotice(int id, int vendorId)
    {
        var t = await LoadAsync(id);
        if (t == null || t.Status is not (TenderStatus.Awarded or TenderStatus.Contracted)) return NotFound();
        if (!CanView(t)) return Forbid();
        var r = TenderEvaluation.Evaluate(t).FirstOrDefault(x => x.Bid.VendorId == vendorId);
        if (r == null) return NotFound();
        ViewBag.Result = r;
        return View(t);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var t = await Db.Tenders.FindAsync(id);
        if (t == null) return NotFound();
        if (t.Status is TenderStatus.Contracted or TenderStatus.Cancelled) return BadRequest();
        if (string.IsNullOrWhiteSpace(reason)) { TempData["Error"] = "Nhập lý do hủy."; return RedirectToAction(nameof(Details), new { id }); }
        t.Status = TenderStatus.Cancelled;
        Log(DocType.Tender, id, "Hủy gói thầu", reason);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã hủy {t.Number}. Thông báo hủy thầu tới các nhà thầu và hoàn trả bảo đảm dự thầu.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private static string? Cut(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var x && x.Length > max ? x[..max] : s.Trim();

    private async Task<Tender?> LoadAsync(int id, bool track = false)
    {
        var q = Db.Tenders.Include(t => t.PurchaseRequest).Include(t => t.Buyer).Include(t => t.AwardedVendor)
            .Include(t => t.Lines)
            .Include(t => t.Bidders).ThenInclude(b => b.Vendor)
            .Include(t => t.Bidders).ThenInclude(b => b.Lines)
            .Include(t => t.Bidders).ThenInclude(b => b.Scores).ThenInclude(s => s.MemberUser)
            .Include(t => t.Committee).ThenInclude(m => m.User)
            .Include(t => t.Clarifications)
            .Include(t => t.Complaints).ThenInclude(c => c.Vendor)
            .AsSplitQuery();
        var t = track ? await q.FirstOrDefaultAsync(x => x.Id == id) : await q.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t != null) t.Lines = t.Lines.OrderBy(l => l.SortOrder).ToList();
        return t;
    }
}
