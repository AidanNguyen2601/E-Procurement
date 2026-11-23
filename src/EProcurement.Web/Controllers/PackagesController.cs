using System.ComponentModel.DataAnnotations;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class PackageForm
{
    public int Id { get; set; }
    public string Number { get; set; } = "(tự sinh khi lưu)";
    [Required(ErrorMessage = "Nhập tên gói"), StringLength(200)][Display(Name = "Tên gói mua sắm")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Đề nghị mua hàng trong gói")] public List<int> PrIds { get; set; } = new();
    [Range(0, 1_000_000_000_000)][Display(Name = "Giá gói (trước VAT)")] public decimal Budget { get; set; }
    [StringLength(200)][Display(Name = "Nguồn vốn")] public string? FundingSource { get; set; } = "Ngân sách hoạt động";
    [Display(Name = "Hình thức lựa chọn NCC")] public ProcurementMethod Method { get; set; } = ProcurementMethod.CompetitiveQuotation;
    [StringLength(1000)][Display(Name = "Lý do chọn hình thức")] public string? MethodReason { get; set; }
    [Display(Name = "Phương thức")] public EnvelopeMethod EnvelopeMethod { get; set; } = EnvelopeMethod.OneEnvelope;
    [StringLength(100)][Display(Name = "Loại hợp đồng")] public string? ContractType { get; set; } = "Trọn gói";
    [Range(1, 3650)][Display(Name = "Thời gian thực hiện HĐ (ngày)")] public int ContractDurationDays { get; set; } = 30;
    [DataType(DataType.Date)][Display(Name = "Thời gian bắt đầu tổ chức LCNT")] public DateTime PlannedStart { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(200)][Display(Name = "Tổ triển khai")] public string? Team { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }

    // hiển thị
    public List<PurchaseRequest> Available { get; set; } = new();
    public decimal Estimate { get; set; }
    public ProcurementMethod? Suggested { get; set; }
    public bool HasReference { get; set; }
}

/// <summary>Quản lý gói mua sắm: gom PR đã duyệt → gói (giá gói, hình thức, phương thức…) → đưa vào KHLCNT → lập hồ sơ lựa chọn NCC</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class PackagesController : ProcControllerBase
{
    public PackagesController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(PackageStatus? status, ProcurementMethod? method, string? q)
    {
        var query = Db.ProcurementPackages.AsNoTracking().Include(p => p.SelectionPlan).Include(p => p.Buyer).Include(p => p.PurchaseRequests).AsQueryable();
        if (status.HasValue) query = query.Where(p => p.Status == status);
        if (method.HasValue) query = query.Where(p => p.Method == method);
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(p => p.Number.Contains(k) || p.Title.Contains(k) || p.PurchaseRequests.Any(r => r.Number.Contains(k))); }
        ViewBag.Status = status; ViewBag.Method = method; ViewBag.Q = q;
        ViewBag.Waiting = await AvailablePrs().CountAsync();
        return View(await query.OrderByDescending(p => p.CreatedAt).ToListAsync());
    }

    /// <summary>PR đã duyệt, chưa thuộc gói, không đặt theo hợp đồng khung, chưa có hồ sơ lựa chọn NCC</summary>
    private IQueryable<PurchaseRequest> AvailablePrs(int? packageId = null) =>
        Db.PurchaseRequests.Include(p => p.Lines).Include(p => p.Requester)
            .Where(p => p.ContractId == null && (p.PackageId == null || p.PackageId == packageId)
                        && (p.Status == PrStatus.Approved || (packageId != null && p.PackageId == packageId))
                        && !Db.Rfqs.Any(r => r.PurchaseRequestId == p.Id && r.Status != RfqStatus.Cancelled)
                        && !Db.Tenders.Any(r => r.PurchaseRequestId == p.Id && r.Status != TenderStatus.Cancelled)
                        && !Db.DirectAwards.Any(r => r.PurchaseRequestId == p.Id && r.Status != DirectAwardStatus.Cancelled)
                        && !Db.PurchaseOrders.Any(r => r.PurchaseRequestId == p.Id && r.Status != PoStatus.Cancelled));

    private async Task FillAsync(PackageForm vm)
    {
        vm.Available = await AvailablePrs(vm.Id == 0 ? null : vm.Id).OrderByDescending(p => p.Priority).ThenBy(p => p.NeededDate).ToListAsync();
        var chosen = vm.Available.Where(p => vm.PrIds.Contains(p.Id)).ToList();
        vm.Estimate = Packages.Estimate(chosen);
        if (chosen.Count > 0)
        {
            var temp = new ProcurementPackage { Title = vm.Title, Budget = vm.Budget > 0 ? vm.Budget : vm.Estimate };
            foreach (var p in chosen) temp.PurchaseRequests.Add(p);
            var synthetic = Packages.Synthetic(temp);
            var refs = await ProcurementRules.FindReferencesAsync(Db, synthetic, Settings);
            vm.HasReference = refs.Any(r => r.CoversRequest);
            vm.Suggested = ProcurementRules.Suggest(synthetic, vm.HasReference, Settings);
        }
        ViewBag.Settings = Settings;
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(List<int>? prIds, int? prId)
    {
        var vm = new PackageForm { PrIds = prIds ?? new() };
        if (prId.HasValue) vm.PrIds.Add(prId.Value);
        await FillAsync(vm);
        vm.PrIds = vm.PrIds.Where(id => vm.Available.Any(p => p.Id == id)).Distinct().ToList();
        var first = vm.Available.FirstOrDefault(p => vm.PrIds.Contains(p.Id));
        if (first != null)
        {
            vm.Title = vm.PrIds.Count == 1 ? first.Title : $"{first.Title} và {vm.PrIds.Count - 1} đề nghị khác";
            vm.Budget = vm.Estimate;
            if (vm.Suggested.HasValue) vm.Method = vm.Suggested.Value;
            vm.ContractDurationDays = Math.Max(1, (vm.Available.Where(p => vm.PrIds.Contains(p.Id)).Min(p => p.NeededDate) - DateTime.Today).Days);
        }
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.ProcurementPackages.Include(x => x.PurchaseRequests).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PackageStatus.Draft) { TempData["Error"] = "Chỉ sửa gói ở trạng thái Nháp (chưa trình / bị trả lại)."; return RedirectToAction(nameof(Details), new { id }); }
        var vm = new PackageForm
        {
            Id = p.Id, Number = p.Number, Title = p.Title, PrIds = p.PurchaseRequests.Select(r => r.Id).ToList(), Budget = p.Budget, FundingSource = p.FundingSource,
            Method = p.Method, MethodReason = p.MethodReason, EnvelopeMethod = p.EnvelopeMethod, ContractType = p.ContractType,
            ContractDurationDays = p.ContractDurationDays, PlannedStart = p.PlannedStart, Note = p.Note, Team = p.Team
        };
        await FillAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PackageForm vm, bool refresh = false)
    {
        ProcurementPackage? p = null;
        if (vm.Id != 0)
        {
            p = await Db.ProcurementPackages.Include(x => x.PurchaseRequests).FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (p == null) return NotFound();
            if (p.Status != PackageStatus.Draft) return BadRequest();
        }
        vm.PrIds = vm.PrIds.Distinct().ToList();
        await FillAsync(vm);
        if (refresh) { ModelState.Clear(); if (vm.Budget <= 0) vm.Budget = vm.Estimate; return View("Form", vm); }
        var chosen = vm.Available.Where(r => vm.PrIds.Contains(r.Id)).ToList();
        if (chosen.Count == 0) ModelState.AddModelError(nameof(vm.PrIds), "Chọn ít nhất một đề nghị mua hàng đã duyệt.");
        if (chosen.Count != vm.PrIds.Count) ModelState.AddModelError(nameof(vm.PrIds), "Có đề nghị không còn hợp lệ để đưa vào gói (đã thuộc gói khác hoặc đã có hồ sơ).");
        if (vm.Budget <= 0) vm.Budget = vm.Estimate;
        if (chosen.Count > 0 && vm.Suggested.HasValue)
            foreach (var e in Packages.MethodProblems(vm.Method, vm.Suggested.Value, vm.MethodReason, vm.HasReference, vm.Budget, vm.Estimate, Settings))
                ModelState.AddModelError(nameof(vm.Method), e);
        if (!ModelState.IsValid) return View("Form", vm);

        if (p == null)
        {
            p = new ProcurementPackage { Number = await DocNumbers.NextAsync(Db.ProcurementPackages.Select(x => x.Number), "GM"), BuyerId = Me, CreatedAt = DateTime.Now };
            Db.ProcurementPackages.Add(p);
        }
        p.Title = vm.Title.Trim(); p.Budget = vm.Budget; p.FundingSource = vm.FundingSource; p.Method = vm.Method;
        p.MethodReason = string.IsNullOrWhiteSpace(vm.MethodReason) ? null : vm.MethodReason.Trim();
        p.EnvelopeMethod = vm.Method == ProcurementMethod.OpenTender ? vm.EnvelopeMethod : EnvelopeMethod.OneEnvelope;
        p.ContractType = vm.ContractType; p.ContractDurationDays = vm.ContractDurationDays; p.PlannedStart = vm.PlannedStart; p.Note = vm.Note;
        p.Team = string.IsNullOrWhiteSpace(vm.Team) ? null : vm.Team.Trim();
        foreach (var r in p.PurchaseRequests.Where(r => !vm.PrIds.Contains(r.Id)).ToList()) { r.PackageId = null; r.Method = null; r.MethodReason = null; }
        var tracked = await Db.PurchaseRequests.Where(r => vm.PrIds.Contains(r.Id)).ToListAsync();
        foreach (var r in tracked) { r.Package = p; r.Method = p.Method; r.MethodReason = p.MethodReason; }
        await Db.SaveChangesAsync();
        var created = vm.Id == 0;
        Log(DocType.Package, p.Id, created ? "Lập gói mua sắm" : "Cập nhật gói mua sắm",
            $"{string.Join(", ", tracked.Select(r => r.Number))} · {p.Method.GetDisplayName()} · giá gói {p.Budget.ToMoney()} đ");
        foreach (var r in tracked) if (created) Log(DocType.PR, r.Id, $"Đưa vào gói mua sắm {p.Number}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu {p.Number}. Đưa gói vào kế hoạch lựa chọn NCC để trình duyệt.";
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await Packages.LoadAsync(Db, id);
        if (p == null) return NotFound();
        ViewBag.Rfqs = await Db.Rfqs.AsNoTracking().Include(r => r.AwardedVendor).Where(r => r.PackageId == id).ToListAsync();
        ViewBag.Tenders = await Db.Tenders.AsNoTracking().Include(r => r.AwardedVendor).Where(r => r.PackageId == id).ToListAsync();
        ViewBag.DirectAwards = await Db.DirectAwards.AsNoTracking().Include(r => r.Vendor).Where(r => r.PackageId == id).ToListAsync();
        ViewBag.Orders = await Db.PurchaseOrders.AsNoTracking().Include(r => r.Vendor).Where(r => r.PackageId == id).ToListAsync();
        ViewBag.Active = await Packages.HasActiveDocumentAsync(Db, id);
        ViewBag.History = await HistoryAsync(DocType.Package, id);
        ViewBag.OpenPlans = User.IsPurchasing() && p.Status == PackageStatus.Draft
            ? await Db.SelectionPlans.Where(x => x.Status == SelectionPlanStatus.Draft || x.Status == SelectionPlanStatus.Returned).OrderByDescending(x => x.CreatedAt).ToListAsync()
            : new List<SelectionPlan>();
        return View(p);
    }

    /// <summary>Hủy gói (không còn hồ sơ đang xử lý): giải phóng các PR để gom lại</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var p = await Db.ProcurementPackages.Include(x => x.PurchaseRequests).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (p.Status is PackageStatus.Completed or PackageStatus.Cancelled or PackageStatus.InPlan) return Fail("Không hủy được gói ở trạng thái hiện tại.");
        if (await Packages.HasActiveDocumentAsync(Db, id)) return Fail("Gói còn hồ sơ lựa chọn NCC / đơn hàng đang xử lý – hủy các chứng từ đó trước.");
        if (string.IsNullOrWhiteSpace(reason)) return Fail("Nhập lý do hủy gói.");
        var prs = p.PurchaseRequests.ToList();
        foreach (var r in prs)
        {
            r.PackageId = null; r.Method = null; r.MethodReason = null;
            if (r.Status == PrStatus.Processing) r.Status = PrStatus.Approved;
            Log(DocType.PR, r.Id, $"Gói {p.Number} bị hủy – đề nghị chờ gom lại vào gói mới");
        }
        p.Status = PackageStatus.Cancelled;
        p.Note = (p.Note + $"\nĐã hủy: {reason.Trim()}. Đề nghị đã giải phóng: {string.Join(", ", prs.Select(r => r.Number))}").Trim();
        Log(DocType.Package, id, "Hủy gói mua sắm", reason.Trim());
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã hủy {p.Number}. Các đề nghị có thể gom vào gói mới.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
