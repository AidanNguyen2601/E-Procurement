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
/// Hợp đồng ký với nhà thầu trúng thầu. Hợp đồng thường → sinh 1 PO toàn bộ; hợp đồng khung → đặt hàng nhiều đợt,
/// mỗi đợt không vượt khối lượng còn lại. Theo dõi bảo đảm thực hiện hợp đồng và giá trị đã thực hiện.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public partial class ContractsController : ProcControllerBase
{
    public ContractsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(ContractStatus? status)
    {
        var q = Db.Contracts.Include(c => c.Vendor).Include(c => c.Tender).AsNoTracking();
        if (status.HasValue) q = q.Where(c => c.Status == status);
        var list = await q.OrderByDescending(c => c.SignedDate).ToListAsync();
        var ids = list.Select(c => c.Id).ToList();
        ViewBag.Used = await Db.PurchaseOrders.Where(p => p.ContractId != null && ids.Contains(p.ContractId.Value) && p.Status != PoStatus.Cancelled)
            .GroupBy(p => p.ContractId!.Value).Select(g => new { g.Key, Sum = g.Sum(p => p.SubTotal) }).ToDictionaryAsync(x => x.Key, x => x.Sum);
        ViewBag.Status = status;
        return View(list);
    }

    public async Task<IActionResult> Details(int id)
    {
        var c = await LoadAsync(id);
        if (c == null) return NotFound();
        ViewBag.Orders = await Db.PurchaseOrders.Include(p => p.Lines).Where(p => p.ContractId == id).OrderBy(p => p.OrderDate).ToListAsync();
        ViewBag.Remaining = await RemainingAsync(c);
        ViewBag.History = await HistoryAsync(DocType.Contract, id);
        ViewBag.Attachments = await AttachmentPanelAsync(DocType.Contract, id, User.IsPurchasing() && c.Status == ContractStatus.Active);
        ViewBag.Payments = await Db.Payments.AsNoTracking().Where(p => p.ContractId == id).OrderBy(p => p.RequestedAt).ToListAsync();
        ViewBag.PaySummary = await HttpContext.RequestServices.GetRequiredService<FinanceService>().ContractPaymentsAsync(id);
        var accs = await Db.BoqAcceptances.AsNoTracking().Include(a => a.Lines).Where(a => a.ContractId == id && a.Status != AcceptanceStatus.Cancelled).ToListAsync();
        ViewBag.Acceptances = accs;
        ViewBag.BoqProgress = !c.IsFramework && c.Lines.Any() ? BoqAcceptances.Progress(c.Lines, BoqAcceptances.Cumulative(accs)) : new List<BoqProgressRow>();
        ViewBag.AcceptancePaid = (await Db.Payments.AsNoTracking().Where(p => p.ContractId == id && p.AcceptanceId != null && p.Status != PaymentStatus.Rejected).Select(p => p.AcceptanceId!.Value).ToListAsync()).ToHashSet();
        return View(c);
    }

    // ------------------------------------------------------------------ Ký hợp đồng từ gói thầu
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int tenderId)
    {
        var t = await LoadTenderAsync(tenderId);
        if (t == null) return NotFound();
        var problems = await SignProblemsAsync(t);
        if (problems.Count > 0) { TempData["Error"] = string.Join(" ", problems); return RedirectToAction("Details", "Tenders", new { id = tenderId }); }
        var value = t.AwardPrice ?? 0;
        return View(new ContractFormViewModel
        {
            TenderId = tenderId, Tender = t, Title = t.Title, EndDate = DateTime.Today.AddDays(t.ContractDurationDays),
            PerformanceSecurityAmount = Math.Ceiling(value * t.PerformanceSecurityPercent / 100m / 1000) * 1000,
            PerformanceSecurityExpiry = DateTime.Today.AddDays(t.ContractDurationDays + 30),
            PaymentTerms = t.AwardedVendor?.PaymentTerms, Terms = t.ContractTerms
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ContractFormViewModel vm)
    {
        var t = await LoadTenderAsync(vm.TenderId, track: true);
        if (t == null) return NotFound();
        foreach (var p in await SignProblemsAsync(t)) ModelState.AddModelError(string.Empty, p);
        var value = t.AwardPrice ?? 0;
        var minSecurity = Math.Round(value * t.PerformanceSecurityPercent / 100m, 0);
        if (vm.PerformanceSecurityAmount < minSecurity)
            ModelState.AddModelError(nameof(vm.PerformanceSecurityAmount), I18n.T("Bảo đảm thực hiện tối thiểu {0:0.##}% = {1} đ.", t.PerformanceSecurityPercent, minSecurity.ToMoney()));
        if (vm.EndDate < vm.StartDate) ModelState.AddModelError(nameof(vm.EndDate), I18n.T("Ngày hết hiệu lực phải sau ngày bắt đầu."));
        if (vm.PerformanceSecurityExpiry < vm.EndDate) ModelState.AddModelError(nameof(vm.PerformanceSecurityExpiry), I18n.T("Bảo lãnh thực hiện phải còn hiệu lực đến hết hợp đồng."));
        if (!ModelState.IsValid) { vm.Tender = t; return View(vm); }

        var bid = t.Bidders.First(b => b.VendorId == t.AwardedVendorId);
        // Đơn giá hợp đồng = đơn giá dự thầu quy đổi theo giá trúng thầu (đã gồm giảm giá và kết quả đàm phán)
        var factor = bid.BidPrice > 0 && value > 0 ? value / bid.BidPrice : 1 - bid.DiscountPercent / 100m;
        var c = new Contract
        {
            Number = await DocNumbers.NextAsync(Db.Contracts.Select(x => x.Number), "HD"), Title = vm.Title, TenderId = t.Id, VendorId = bid.VendorId,
            IsFramework = t.IsFramework, Value = value, VatPercent = bid.VatPercent, SignedDate = vm.SignedDate, StartDate = vm.StartDate, EndDate = vm.EndDate,
            PerformanceSecurityAmount = vm.PerformanceSecurityAmount, PerformanceSecurityRef = vm.PerformanceSecurityRef, PerformanceSecurityExpiry = vm.PerformanceSecurityExpiry,
            PaymentTerms = vm.PaymentTerms, Terms = vm.Terms, CreatedById = Me, CreatedAt = DateTime.Now
        };
        foreach (var l in t.Lines.OrderBy(l => l.SortOrder))
        {
            var price = bid.Lines.First(x => x.TenderLineId == l.Id).UnitPrice;
            c.Lines.Add(new ContractLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = Math.Round(price * factor, 0), SortOrder = l.SortOrder, BoqCode = l.BoqCode, BoqPath = l.BoqPath });
        }
        Db.Contracts.Add(c);
        t.Status = TenderStatus.Contracted;
        await Db.SaveChangesAsync();
        Log(DocType.Contract, c.Id, "Ký hợp đồng", $"Từ gói thầu {t.Number} – {c.Value.ToMoney()} đ (trước VAT)");
        Log(DocType.Tender, t.Id, $"Ký hợp đồng {c.Number}");

        PurchaseOrder? po = null;
        if (!c.IsFramework)
        {
            po = await NewPoAsync(c, t.PurchaseRequestId, c.StartDate.AddDays(bid.LeadTimeDays ?? t.ContractDurationDays));
            po.PackageId = t.PackageId;
            foreach (var l in c.Lines) po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, SortOrder = l.SortOrder });
            po.Recalculate();
            Db.PurchaseOrders.Add(po);
            await Db.SaveChangesAsync();
            Log(DocType.PO, po.Id, $"Tạo theo hợp đồng {c.Number}");
        }
        // Hợp đồng khung: gói đã hoàn tất lựa chọn NCC; các đợt đặt hàng đi theo đề nghị riêng
        if (c.IsFramework && t.PackageId.HasValue)
        {
            var pkg = await Db.ProcurementPackages.Include(x => x.PurchaseRequests).FirstAsync(x => x.Id == t.PackageId);
            pkg.Status = PackageStatus.Completed;
            foreach (var r in pkg.PurchaseRequests.Where(r => r.Status == PrStatus.Processing)) r.Status = PrStatus.Completed;
            Log(DocType.Package, pkg.Id, $"Ký hợp đồng khung {c.Number} – hoàn tất gói");
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = po != null ? I18n.T("Đã ký {0} và tạo đơn mua hàng nháp {1}.", c.Number, po.Number) : I18n.T("Đã ký hợp đồng khung {0}. Đặt hàng từng đợt từ trang hợp đồng.", c.Number);
        return RedirectToAction(nameof(Details), new { id = c.Id });
    }

    // ------------------------------------------------------------------ Đặt hàng theo hợp đồng khung
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> CreatePo(int id)
    {
        var c = await LoadAsync(id);
        if (c == null) return NotFound();
        if (c.Status != ContractStatus.Active || !c.IsFramework) { TempData["Error"] = I18n.T("Chỉ đặt hàng từng đợt với hợp đồng khung đang thực hiện."); return RedirectToAction(nameof(Details), new { id }); }
        ViewBag.Remaining = await RemainingAsync(c);
        return View(new ContractPoViewModel { ContractId = id, Contract = c, Lines = c.Lines.Select(l => new ContractPoLine { ContractLineId = l.Id }).ToList() });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePo(ContractPoViewModel vm)
    {
        var c = await LoadAsync(vm.ContractId);
        if (c == null) return NotFound();
        if (c.Status != ContractStatus.Active || !c.IsFramework) return BadRequest();
        var remaining = await RemainingAsync(c);
        if (vm.Lines.All(l => l.Quantity <= 0)) ModelState.AddModelError(string.Empty, I18n.T("Nhập số lượng đặt cho ít nhất một hạng mục."));
        if (DateTime.Today > c.EndDate) ModelState.AddModelError(string.Empty, I18n.T("Hợp đồng đã hết hiệu lực ngày {0}.", c.EndDate.ToDate()));
        foreach (var l in vm.Lines.Where(l => l.Quantity > 0))
        {
            var cl = c.Lines.FirstOrDefault(x => x.Id == l.ContractLineId);
            if (cl == null) { ModelState.AddModelError(string.Empty, I18n.T("Hạng mục không thuộc hợp đồng.")); continue; }
            if (l.Quantity > remaining[cl.Id]) ModelState.AddModelError(string.Empty, I18n.T("\"{0}\": đặt {1} vượt khối lượng còn lại {2}.", cl.Description, l.Quantity.ToQty(), remaining[cl.Id].ToQty()));
        }
        if (!ModelState.IsValid) { vm.Contract = c; ViewBag.Remaining = remaining; return View(vm); }

        var prId = c.Tender?.PurchaseRequestId;
        var po = await NewPoAsync(c, prId, vm.DeliveryDate);
        var s = 1;
        foreach (var l in vm.Lines.Where(l => l.Quantity > 0))
        {
            var cl = c.Lines.First(x => x.Id == l.ContractLineId);
            po.Lines.Add(new PurchaseOrderLine { ItemId = cl.ItemId, Description = cl.Description, Unit = cl.Unit, Quantity = l.Quantity, UnitPrice = cl.UnitPrice, SortOrder = s++ });
        }
        po.Recalculate();
        Db.PurchaseOrders.Add(po);
        await Db.SaveChangesAsync();
        Log(DocType.PO, po.Id, $"Đặt hàng theo hợp đồng khung {c.Number}");
        Log(DocType.Contract, c.Id, $"Đặt hàng đợt {po.Number}", $"{po.SubTotal.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo đơn mua hàng nháp {0} theo hợp đồng {1}.", po.Number, c.Number);
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, bool terminate, string? reason)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        if (c.Status != ContractStatus.Active) return BadRequest();
        if (string.IsNullOrWhiteSpace(reason)) { TempData["Error"] = I18n.T("Nhập nội dung hoàn thành / lý do chấm dứt."); return RedirectToAction(nameof(Details), new { id }); }
        c.Status = terminate ? ContractStatus.Terminated : ContractStatus.Completed;
        Log(DocType.Contract, id, terminate ? "Chấm dứt hợp đồng" : "Hoàn thành hợp đồng – nghiệm thu bàn giao (chờ quyết toán, thanh lý)", reason);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"{c.Number}: {c.Status.GetDisplayName()}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------
    /// <summary>Điều kiện ký hợp đồng: đã có kết quả, hết thời hạn kiến nghị, không còn kiến nghị chưa giải quyết</summary>
    private async Task<List<string>> SignProblemsAsync(Tender t)
    {
        var p = new List<string>();
        if (t.Status != TenderStatus.Awarded) p.Add("Gói thầu chưa có kết quả được phê duyệt (hoặc đã ký hợp đồng).");
        if (t.ComplaintDeadline.HasValue && DateTime.Now <= t.ComplaintDeadline) p.Add($"Chưa hết thời hạn kiến nghị kết quả ({t.ComplaintDeadline.ToDateTime()}).");
        if (t.Complaints.Any(c => c.ResolvedAt == null)) p.Add("Còn kiến nghị chưa giải quyết.");
        if (t.AwardedVendorId.HasValue && await Db.Vendors.AnyAsync(v => v.Id == t.AwardedVendorId && (v.Status == VendorStatus.Blacklisted || v.Status == VendorStatus.Suspended)))
            p.Add("Nhà thầu trúng thầu đang bị tạm ngưng / danh sách đen.");
        return p;
    }

    /// <summary>Khối lượng còn lại theo từng dòng hợp đồng (trừ các PO chưa hủy)</summary>
    private async Task<Dictionary<int, decimal>> RemainingAsync(Contract c)
    {
        var used = await Db.PurchaseOrderLines.Where(l => l.PurchaseOrder!.ContractId == c.Id && l.PurchaseOrder.Status != PoStatus.Cancelled)
            .Select(l => new { l.ItemId, l.Description, l.Quantity }).ToListAsync();
        return c.Lines.ToDictionary(l => l.Id, l => Math.Max(0, l.Quantity - used.Where(u => u.ItemId == l.ItemId && u.Description == l.Description).Sum(u => u.Quantity)));
    }

    private async Task<PurchaseOrder> NewPoAsync(Contract c, int? prId, DateTime delivery) => new()
    {
        Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(p => p.Number), "PO"),
        VendorId = c.VendorId, ContractId = c.Id, PurchaseRequestId = prId, BuyerId = Me, Method = ProcurementMethod.OpenTender,
        OrderDate = DateTime.Today, DeliveryDate = delivery < DateTime.Today ? DateTime.Today.AddDays(7) : delivery,
        PaymentTerms = c.PaymentTerms, DeliveryAddress = Settings.DefaultDeliveryAddress, VatPercent = c.VatPercent,
        Notes = $"Theo hợp đồng {c.Number} ký ngày {c.SignedDate.ToDate()}", CreatedAt = DateTime.Now
    };

    /// <summary>Lập PO đặt hàng đợt từ PR đã duyệt tạo trong catalog hợp đồng khung</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePoFromPr(int prId)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == prId);
        if (pr?.ContractId == null) return NotFound();
        IActionResult Back(string e) { TempData["Error"] = e; return RedirectToAction("Details", "PurchaseRequests", new { id = prId }); }
        if (pr.Status != PrStatus.Approved) return Back(I18n.T("Đề nghị chưa được duyệt hoặc đã lập đơn hàng."));
        var c = await LoadAsync(pr.ContractId.Value);
        if (c == null || c.Status != ContractStatus.Active || !c.IsFramework || c.EndDate < DateTime.Today) return Back(I18n.T("Hợp đồng khung không còn hiệu lực – chọn hình thức mua khác."));
        var avail = await OrderingController.AvailabilityAsync(Db, c, excludePrId: prId);
        var po = await NewPoAsync(c, prId, pr.NeededDate);
        var s = 1;
        foreach (var l in pr.Lines.OrderBy(x => x.SortOrder))
        {
            var cl = c.Lines.FirstOrDefault(x => x.ItemId == l.ItemId && x.Description == l.Description);
            if (cl == null) return Back(I18n.T("\"{0}\" không có trong hợp đồng.", l.Description));
            if (l.Quantity > avail[cl.Id].Available) return Back(I18n.T("\"{0}\": vượt khối lượng còn lại của hợp đồng ({1}).", l.Description, avail[cl.Id].Available.ToQty()));
            po.Lines.Add(new PurchaseOrderLine { ItemId = cl.ItemId, Description = cl.Description, Unit = cl.Unit, Quantity = l.Quantity, UnitPrice = cl.UnitPrice, SortOrder = s++ });
        }
        po.Recalculate();
        Db.PurchaseOrders.Add(po);
        pr.Status = PrStatus.Processing; pr.Method = ProcurementMethod.OpenTender; pr.MethodReason = $"Đặt hàng theo hợp đồng khung {c.Number}";
        await Db.SaveChangesAsync();
        Log(DocType.PO, po.Id, $"Đặt hàng theo hợp đồng khung {c.Number}", $"Từ đề nghị {pr.Number}");
        Log(DocType.Contract, c.Id, $"Đặt hàng đợt {po.Number}", $"{po.SubTotal.ToMoney()} đ – đề nghị {pr.Number} ({pr.Department})");
        Log(DocType.PR, pr.Id, $"Mua hàng lập {po.Number} theo hợp đồng khung {c.Number}");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo đơn mua hàng nháp {0} theo hợp đồng {1} – trình duyệt / gửi NCC.", po.Number, c.Number);
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    // ------------------------------------------------------------------ Phụ lục hợp đồng
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAmendment(int id, string? description, decimal valueChange, DateTime? newEndDate)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        IActionResult Back(string? e = null) { if (e != null) TempData["Error"] = e; return RedirectToAction(nameof(Details), new { id }); }
        if (c.Status != ContractStatus.Active) return Back(I18n.T("Chỉ lập phụ lục cho hợp đồng đang thực hiện."));
        if (string.IsNullOrWhiteSpace(description)) return Back(I18n.T("Nhập nội dung phụ lục."));
        if (valueChange == 0 && newEndDate == null) return Back(I18n.T("Phụ lục cần điều chỉnh giá trị hoặc gia hạn."));
        if (c.Value + valueChange <= 0) return Back(I18n.T("Giá trị hợp đồng sau điều chỉnh phải lớn hơn 0."));
        if (newEndDate.HasValue && newEndDate.Value.Date <= c.StartDate) return Back(I18n.T("Ngày gia hạn không hợp lệ."));
        if (await Db.ContractAmendments.AnyAsync(a => a.ContractId == id && a.Status == AmendmentStatus.Pending)) return Back(I18n.T("Đang có phụ lục chờ duyệt."));
        var n = await Db.ContractAmendments.CountAsync(a => a.ContractId == id) + 1;
        var a = new ContractAmendment { ContractId = id, Number = $"PL{n:00}", Description = description.Trim()[..Math.Min(2000, description.Trim().Length)], ValueChange = Math.Round(valueChange, 0), NewEndDate = newEndDate?.Date, CreatedById = Me };
        Db.ContractAmendments.Add(a);
        Log(DocType.Contract, id, $"Trình duyệt phụ lục {a.Number}",
            $"{a.Description}" + (a.ValueChange != 0 ? $" · giá trị {(a.ValueChange > 0 ? "+" : "")}{a.ValueChange.ToMoney()} đ" : "") + (a.NewEndDate.HasValue ? $" · gia hạn đến {a.NewEndDate:dd/MM/yyyy}" : ""));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã trình phụ lục {0} – chờ Giám đốc duyệt.", a.Number);
        return Back();
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Contract)]
    public async Task<IActionResult> DecideAmendment(int id, int amendmentId, bool approve, string? comment)
    {
        var c = await Db.Contracts.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        var a = await Db.ContractAmendments.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == amendmentId && x.ContractId == id && x.Status == AmendmentStatus.Pending);
        if (c == null || a == null) return NotFound();
        if (!approve && string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = I18n.T("Nhập lý do không duyệt."); return RedirectToAction(nameof(Details), new { id }); }
        if (approve && a.Lines.Count > 0)
        {
            // điều chỉnh khối lượng BOQ: kiểm tra lại với khối lượng đã nghiệm thu tại thời điểm duyệt rồi áp dụng
            var cum = BoqAcceptances.Cumulative(await Db.BoqAcceptances.AsNoTracking().Include(x => x.Lines).Where(x => x.ContractId == id && x.Status == AcceptanceStatus.Confirmed).ToListAsync());
            var applyErrors = BoqVariations.Apply(c, a, cum);
            if (applyErrors.Count > 0) { TempData["Error"] = string.Join(" ", applyErrors); return RedirectToAction(nameof(Details), new { id }); }
        }
        a.Status = approve ? AmendmentStatus.Approved : AmendmentStatus.Rejected; a.DecidedById = Me; a.DecidedAt = DateTime.Now; a.DecisionNote = comment?.Trim();
        if (approve)
        {
            c.Value += a.ValueChange;
            if (a.NewEndDate.HasValue) { c.EndDate = a.NewEndDate.Value; if (c.PerformanceSecurityExpiry < c.EndDate) Log(DocType.Contract, id, "Cảnh báo: bảo lãnh thực hiện hết hạn trước ngày gia hạn – yêu cầu NCC gia hạn bảo lãnh"); }
        }
        Log(DocType.Contract, id, approve ? $"Giám đốc duyệt phụ lục {a.Number} – giá trị HĐ {c.Value.ToMoney()} đ, hiệu lực đến {c.EndDate:dd/MM/yyyy}" : $"Giám đốc không duyệt phụ lục {a.Number}", comment);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? I18n.T("Phụ lục {0} có hiệu lực.", a.Number) : I18n.T("Đã trả lại phụ lục {0}.", a.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Giá dự toán – giá hợp đồng – giá thực tế: tính theo gói thầu (TendersController.Actuals)
    public async Task<IActionResult> Actuals(int id)
    {
        var tenderId = await Db.Contracts.AsNoTracking().Where(c => c.Id == id).Select(c => c.TenderId).FirstOrDefaultAsync();
        return tenderId.HasValue ? RedirectToAction("Actuals", "Tenders", new { id = tenderId }) : RedirectToAction(nameof(Details), new { id });
    }

    public IActionResult ActualsSummary() => RedirectToAction("ActualsSummary", "Tenders");

    // ------------------------------------------------------------------ Phụ lục điều chỉnh khối lượng BOQ
    public async Task<IActionResult> Variation(int id)
    {
        var c = await LoadAsync(id);
        if (c == null) return NotFound();
        ViewBag.Cumulative = BoqAcceptances.Cumulative(await Db.BoqAcceptances.AsNoTracking().Include(x => x.Lines).Where(x => x.ContractId == id && x.Status == AcceptanceStatus.Confirmed).ToListAsync());
        ViewBag.Amendments = await Db.ContractAmendments.AsNoTracking().Include(x => x.Lines).Where(x => x.ContractId == id && x.Lines.Any()).OrderBy(x => x.CreatedAt).ToListAsync();
        ViewBag.HasPending = await Db.ContractAmendments.AnyAsync(x => x.ContractId == id && x.Status == AmendmentStatus.Pending);
        return View(c);
    }

    /// <summary>Trình phụ lục điều chỉnh khối lượng: q[lineId] = khối lượng MỚI; dòng phát sinh qua các mảng add*. Giá trị chênh lệch tự tính theo đơn giá hợp đồng.</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBoqAmendment(int id, string? description, Dictionary<int, decimal?>? q, List<string?>? addDesc, List<string?>? addUnit, List<decimal?>? addQty, List<decimal?>? addPrice, List<string?>? addGroup)
    {
        var c = await Db.Contracts.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        IActionResult Back(string e) { TempData["Error"] = e; return RedirectToAction(nameof(Variation), new { id }); }
        if (c.Status != ContractStatus.Active) return Back(I18n.T("Chỉ lập phụ lục cho hợp đồng đang thực hiện."));
        if (c.IsFramework) return Back(I18n.T("Hợp đồng khung không điều chỉnh khối lượng theo BOQ."));
        if (await Db.ContractAmendments.AnyAsync(a => a.ContractId == id && a.Status == AmendmentStatus.Pending)) return Back(I18n.T("Đang có phụ lục chờ duyệt."));
        var cum = BoqAcceptances.Cumulative(await Db.BoqAcceptances.AsNoTracking().Include(x => x.Lines).Where(x => x.ContractId == id && x.Status == AcceptanceStatus.Confirmed).ToListAsync());
        var adds = new List<VariationAddition>();
        for (var i = 0; i < (addDesc?.Count ?? 0); i++)
            adds.Add(new VariationAddition(addDesc![i] ?? "", addUnit?.ElementAtOrDefault(i), addQty?.ElementAtOrDefault(i) ?? 0, addPrice?.ElementAtOrDefault(i) ?? 0, addGroup?.ElementAtOrDefault(i)));
        var newQty = (q ?? new()).Where(kv => kv.Value.HasValue).ToDictionary(kv => kv.Key, kv => kv.Value!.Value);
        var plan = BoqVariations.Plan(c.Lines.ToList(), cum, newQty, adds);
        if (plan.Errors.Count > 0) return Back(string.Join(" ", plan.Errors));
        if (c.Value + plan.ValueChange <= 0) return Back(I18n.T("Giá trị hợp đồng sau điều chỉnh phải lớn hơn 0."));
        var n = await Db.ContractAmendments.CountAsync(a => a.ContractId == id) + 1;
        var desc = string.IsNullOrWhiteSpace(description) ? I18n.T("Điều chỉnh khối lượng BOQ") : description.Trim();
        var amendment = new ContractAmendment { ContractId = id, Number = $"PL{n:00}", Description = desc[..Math.Min(2000, desc.Length)], ValueChange = plan.ValueChange, CreatedById = Me, CreatedAt = DateTime.Now, Lines = plan.Lines };
        Db.ContractAmendments.Add(amendment);
        Log(DocType.Contract, id, I18n.T("Trình duyệt phụ lục {0}", amendment.Number), $"{amendment.Description} · " + I18n.T("{0} dòng điều chỉnh", plan.Lines.Count) + $" · {(plan.ValueChange > 0 ? "+" : "")}{plan.ValueChange.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã trình phụ lục {0} – chờ Giám đốc duyệt.", amendment.Number);
        return RedirectToAction(nameof(Variation), new { id });
    }

    // ------------------------------------------------------------------ Lịch thanh toán, bảo lãnh, điều khoản phạt
    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Accountant)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMilestone(int id, string? name, PaymentType type, decimal percent, DateTime? dueDate, string? condition)
    {
        var c = await Db.Contracts.Include(x => x.Milestones).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        IActionResult Back(string? e = null) { if (e != null) TempData["Error"] = e; return RedirectToAction(nameof(Details), new { id }); }
        if (string.IsNullOrWhiteSpace(name) || percent is <= 0 or > 100 || type == PaymentType.Invoice) return Back(I18n.T("Nhập tên đợt và tỷ lệ (0–100%)."));
        if (c.Milestones.Sum(m => m.Percent) + percent > 100) return Back(I18n.T("Tổng tỷ lệ các đợt vượt 100% (đã có {0:0.#}%).", c.Milestones.Sum(m => m.Percent)));
        var gross = Math.Round(c.Value * (1 + c.VatPercent / 100m), 0);
        c.Milestones.Add(new ContractMilestone { Name = name.Trim(), Type = type, Percent = percent, Amount = Math.Round(gross * percent / 100m, 0), DueDate = dueDate?.Date, Condition = condition?.Trim(), SortOrder = c.Milestones.Count + 1 });
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã thêm đợt thanh toán.");
        return Back();
    }

    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Accountant)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMilestone(int id, int milestoneId)
    {
        if (await Db.Payments.AnyAsync(p => p.MilestoneId == milestoneId && p.Status != PaymentStatus.Rejected)) { TempData["Error"] = I18n.T("Đợt đã có đề nghị thanh toán."); return RedirectToAction(nameof(Details), new { id }); }
        await Db.ContractMilestones.Where(m => m.Id == milestoneId && m.ContractId == id).ExecuteDeleteAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đề nghị thanh toán một đợt / tạm ứng. Tạm ứng bắt buộc có bảo lãnh tạm ứng còn hiệu lực, đủ giá trị.</summary>
    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestMilestonePayment(int id, int milestoneId)
    {
        var c = await Db.Contracts.FindAsync(id);
        var m = await Db.ContractMilestones.FirstOrDefaultAsync(x => x.Id == milestoneId && x.ContractId == id);
        if (c == null || m == null) return NotFound();
        IActionResult Back(string? e = null) { if (e != null) TempData["Error"] = e; return RedirectToAction(nameof(Details), new { id }); }
        if (c.Status != ContractStatus.Active) return Back(I18n.T("Hợp đồng không còn hiệu lực."));
        if (await Db.Payments.AnyAsync(p => p.MilestoneId == m.Id && p.Status != PaymentStatus.Rejected)) return Back(I18n.T("Đợt này đã có đề nghị thanh toán."));
        if (m.Type == PaymentType.Advance)
        {
            var cover = await Db.ContractGuarantees.Where(g => g.ContractId == id && g.Type == GuaranteeType.Advance && g.ReleasedAt == null && g.ExpiryDate >= DateTime.Today).SumAsync(g => (decimal?)g.Amount) ?? 0;
            if (cover < m.Amount) return Back(I18n.T("Tạm ứng {0} đ cần bảo lãnh tạm ứng còn hiệu lực tương ứng (hiện có {1} đ).", m.Amount.ToMoney(), cover.ToMoney()));
        }
        var ded = m.Type == PaymentType.Advance ? new PaymentDeduction(0, 0, "") : ContractClose.Deductions(c, m.Amount, await ContractClose.LedgerAsync(Db, c));
        var pay = new Payment
        {
            Number = await DocNumbers.NextAsync(Db.Payments.Select(x => x.Number), "TT"), Type = m.Type, VendorId = c.VendorId, ContractId = id, MilestoneId = m.Id,
            Amount = m.Amount, AdvanceDeduction = ded.Advance, RetentionAmount = ded.Retention, NetAmount = m.Amount - ded.Advance - ded.Retention,
            Note = $"{m.Name} ({m.Percent:0.#}% HĐ {c.Number})" + (string.IsNullOrEmpty(m.Condition) ? "" : " – " + m.Condition) + (string.IsNullOrEmpty(ded.Note) ? "" : " · " + ded.Note),
            RequestedById = Me, RequestedAt = DateTime.Now
        };
        Db.Payments.Add(pay);
        await Db.SaveChangesAsync();
        Log(DocType.Payment, pay.Id, $"Đề nghị {m.Type.GetDisplayName().ToLower()} {pay.NetAmount.ToMoney()} đ", pay.Note);
        Log(DocType.Contract, id, $"Lập đề nghị thanh toán {pay.Number} – {m.Name}");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lập {0} – chờ Giám đốc duyệt.", pay.Number);
        return RedirectToAction("Details", "Payments", new { id = pay.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddGuarantee(int id, GuaranteeType type, decimal amount, string? reference, string? bank, DateTime expiryDate, string? note)
    {
        if (!await Db.Contracts.AnyAsync(c => c.Id == id)) return NotFound();
        if (amount <= 0 || expiryDate.Date < DateTime.Today) { TempData["Error"] = I18n.T("Giá trị và ngày hiệu lực bảo lãnh không hợp lệ."); return RedirectToAction(nameof(Details), new { id }); }
        Db.ContractGuarantees.Add(new ContractGuarantee { ContractId = id, Type = type, Amount = Math.Round(amount, 0), Reference = reference?.Trim(), Bank = bank?.Trim(), ExpiryDate = expiryDate.Date, Note = note?.Trim() });
        Log(DocType.Contract, id, $"Tiếp nhận {type.GetDisplayName().ToLower()} {amount.ToMoney()} đ", $"{reference} – {bank}, hiệu lực đến {expiryDate:dd/MM/yyyy}");
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReleaseGuarantee(int id, int guaranteeId)
    {
        var g = await Db.ContractGuarantees.FirstOrDefaultAsync(x => x.Id == guaranteeId && x.ContractId == id && x.ReleasedAt == null);
        if (g == null) return NotFound();
        g.ReleasedAt = DateTime.Now;
        Log(DocType.Contract, id, $"Giải tỏa {g.Type.GetDisplayName().ToLower()} {g.Reference}");
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PenaltyTerms(int id, decimal perDay, decimal cap)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        if (perDay is < 0 or > 5 || cap is < 0 or > 100) { TempData["Error"] = I18n.T("Mức phạt không hợp lệ."); return RedirectToAction(nameof(Details), new { id }); }
        c.LatePenaltyPercentPerDay = perDay; c.LatePenaltyCapPercent = cap;
        Log(DocType.Contract, id, $"Cập nhật điều khoản phạt chậm giao: {perDay:0.###}%/ngày, tối đa {cap:0.#}%");
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<Contract?> LoadAsync(int id)
    {
        var c = await Db.Contracts.Include(x => x.Vendor).Include(x => x.Tender).Include(x => x.CreatedBy).Include(x => x.Lines)
            .Include(x => x.Amendments).ThenInclude(a => a.Lines).Include(x => x.Milestones).Include(x => x.Guarantees).AsSplitQuery()
            .AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c != null) c.Lines = c.Lines.OrderBy(l => l.SortOrder).ToList();
        return c;
    }

    private Task<Tender?> LoadTenderAsync(int id, bool track = false)
    {
        var q = Db.Tenders.Include(t => t.AwardedVendor).Include(t => t.Lines).Include(t => t.Complaints)
            .Include(t => t.Bidders).ThenInclude(b => b.Lines).AsSplitQuery();
        return track ? q.FirstOrDefaultAsync(t => t.Id == id) : q.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }
}
