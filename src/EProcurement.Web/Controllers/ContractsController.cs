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
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class ContractsController : ProcControllerBase
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
            ModelState.AddModelError(nameof(vm.PerformanceSecurityAmount), $"Bảo đảm thực hiện tối thiểu {t.PerformanceSecurityPercent:0.##}% = {minSecurity.ToMoney()} đ.");
        if (vm.EndDate < vm.StartDate) ModelState.AddModelError(nameof(vm.EndDate), "Ngày hết hiệu lực phải sau ngày bắt đầu.");
        if (vm.PerformanceSecurityExpiry < vm.EndDate) ModelState.AddModelError(nameof(vm.PerformanceSecurityExpiry), "Bảo lãnh thực hiện phải còn hiệu lực đến hết hợp đồng.");
        if (!ModelState.IsValid) { vm.Tender = t; return View(vm); }

        var bid = t.Bidders.First(b => b.VendorId == t.AwardedVendorId);
        var factor = 1 - bid.DiscountPercent / 100m;
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
            c.Lines.Add(new ContractLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = Math.Round(price * factor, 0), SortOrder = l.SortOrder });
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
            foreach (var l in c.Lines) po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.UnitPrice, SortOrder = l.SortOrder });
            po.Recalculate();
            Db.PurchaseOrders.Add(po);
            await Db.SaveChangesAsync();
            Log(DocType.PO, po.Id, $"Tạo theo hợp đồng {c.Number}");
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = po != null ? $"Đã ký {c.Number} và tạo đơn mua hàng nháp {po.Number}." : $"Đã ký hợp đồng khung {c.Number}. Đặt hàng từng đợt từ trang hợp đồng.";
        return RedirectToAction(nameof(Details), new { id = c.Id });
    }

    // ------------------------------------------------------------------ Đặt hàng theo hợp đồng khung
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> CreatePo(int id)
    {
        var c = await LoadAsync(id);
        if (c == null) return NotFound();
        if (c.Status != ContractStatus.Active || !c.IsFramework) { TempData["Error"] = "Chỉ đặt hàng từng đợt với hợp đồng khung đang thực hiện."; return RedirectToAction(nameof(Details), new { id }); }
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
        if (vm.Lines.All(l => l.Quantity <= 0)) ModelState.AddModelError(string.Empty, "Nhập số lượng đặt cho ít nhất một hạng mục.");
        if (DateTime.Today > c.EndDate) ModelState.AddModelError(string.Empty, $"Hợp đồng đã hết hiệu lực ngày {c.EndDate.ToDate()}.");
        foreach (var l in vm.Lines.Where(l => l.Quantity > 0))
        {
            var cl = c.Lines.FirstOrDefault(x => x.Id == l.ContractLineId);
            if (cl == null) { ModelState.AddModelError(string.Empty, "Hạng mục không thuộc hợp đồng."); continue; }
            if (l.Quantity > remaining[cl.Id]) ModelState.AddModelError(string.Empty, $"\"{cl.Description}\": đặt {l.Quantity.ToQty()} vượt khối lượng còn lại {remaining[cl.Id].ToQty()}.");
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
        TempData["Success"] = $"Đã tạo đơn mua hàng nháp {po.Number} theo hợp đồng {c.Number}.";
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id, bool terminate, string? reason)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        if (c.Status != ContractStatus.Active) return BadRequest();
        if (string.IsNullOrWhiteSpace(reason)) { TempData["Error"] = "Nhập lý do / nội dung thanh lý."; return RedirectToAction(nameof(Details), new { id }); }
        c.Status = terminate ? ContractStatus.Terminated : ContractStatus.Completed;
        Log(DocType.Contract, id, terminate ? "Chấm dứt hợp đồng" : "Thanh lý – hoàn thành hợp đồng", reason);
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

    private async Task<Contract?> LoadAsync(int id)
    {
        var c = await Db.Contracts.Include(x => x.Vendor).Include(x => x.Tender).Include(x => x.CreatedBy).Include(x => x.Lines)
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
