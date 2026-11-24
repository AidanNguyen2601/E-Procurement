using System.ComponentModel.DataAnnotations;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class SelectionPlanForm
{
    public int Id { get; set; }
    public string Number { get; set; } = "(tự sinh khi lưu)";
    [Required(ErrorMessage = "Nhập tên kế hoạch"), StringLength(200)][Display(Name = "Tên kế hoạch")] public string Title { get; set; } = string.Empty;
    [StringLength(2000)][Display(Name = "Căn cứ lập kế hoạch")] public string? Basis { get; set; }
    public List<int> PackageIds { get; set; } = new();
    public List<ProcurementPackage> Available { get; set; } = new();
}

/// <summary>
/// Kế hoạch lựa chọn nhà cung cấp (KHLCNT): tập hợp các gói mua sắm (giá gói, hình thức, phương thức, thời gian, loại HĐ…)
/// → trình Giám đốc phê duyệt (ký) → các gói được lập hồ sơ lựa chọn NCC.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class SelectionPlansController : ProcControllerBase
{
    public SelectionPlansController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    private bool IsDirector => User.IsInRole(AppRoles.Director) || User.IsAdmin();

    public async Task<IActionResult> Index(SelectionPlanStatus? status)
    {
        var q = Db.SelectionPlans.AsNoTracking().Include(x => x.CreatedBy).Include(x => x.Packages).AsQueryable();
        if (status.HasValue) q = q.Where(x => x.Status == status);
        ViewBag.Status = status;
        ViewBag.Unplanned = await Db.ProcurementPackages.CountAsync(p => p.Status == PackageStatus.Draft && p.SelectionPlanId == null);
        return View(await q.OrderByDescending(x => x.CreatedAt).ToListAsync());
    }

    private async Task FillAsync(SelectionPlanForm vm) =>
        vm.Available = await Db.ProcurementPackages.AsNoTracking().Include(p => p.PurchaseRequests)
            .Where(p => p.Status == PackageStatus.Draft && (p.SelectionPlanId == null || p.SelectionPlanId == vm.Id)).OrderBy(p => p.PlannedStart).ToListAsync();

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(List<int>? packageIds)
    {
        var vm = new SelectionPlanForm { Title = $"Kế hoạch lựa chọn nhà cung cấp đợt {DateTime.Today:MM/yyyy}", PackageIds = packageIds ?? new() };
        await FillAsync(vm);
        if (vm.PackageIds.Count == 0) vm.PackageIds = vm.Available.Where(p => p.SelectionPlanId == null).Select(p => p.Id).ToList();
        vm.Basis = "Các đề nghị mua hàng đã được phê duyệt; kế hoạch mua sắm năm đã duyệt; quy chế mua sắm của công ty.";
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var plan = await Db.SelectionPlans.Include(x => x.Packages).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (plan == null) return NotFound();
        if (plan.Status is not (SelectionPlanStatus.Draft or SelectionPlanStatus.Returned)) { TempData["Error"] = I18n.T("Chỉ sửa kế hoạch nháp hoặc bị trả lại."); return RedirectToAction(nameof(Details), new { id }); }
        var vm = new SelectionPlanForm { Id = id, Number = plan.Number, Title = plan.Title, Basis = plan.Basis, PackageIds = plan.Packages.Select(p => p.Id).ToList() };
        await FillAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SelectionPlanForm vm, bool submit = false)
    {
        SelectionPlan? plan = null;
        if (vm.Id != 0)
        {
            plan = await Db.SelectionPlans.Include(x => x.Packages).FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (plan == null) return NotFound();
            if (plan.Status is not (SelectionPlanStatus.Draft or SelectionPlanStatus.Returned)) return BadRequest();
        }
        await FillAsync(vm);
        vm.PackageIds = vm.PackageIds.Distinct().Where(id => vm.Available.Any(p => p.Id == id)).ToList();
        if (vm.PackageIds.Count == 0) ModelState.AddModelError(nameof(vm.PackageIds), I18n.T("Chọn ít nhất một gói mua sắm (trạng thái Nháp)."));
        if (!ModelState.IsValid) return View("Form", vm);

        if (plan == null)
        {
            plan = new SelectionPlan { Number = await DocNumbers.NextAsync(Db.SelectionPlans.Select(x => x.Number), "KH"), CreatedById = Me, CreatedAt = DateTime.Now };
            Db.SelectionPlans.Add(plan);
        }
        plan.Title = vm.Title.Trim(); plan.Basis = vm.Basis;
        foreach (var p in plan.Packages.Where(p => !vm.PackageIds.Contains(p.Id)).ToList()) p.SelectionPlanId = null;
        foreach (var p in await Db.ProcurementPackages.Where(p => vm.PackageIds.Contains(p.Id)).ToListAsync()) p.SelectionPlan = plan;
        await Db.SaveChangesAsync();
        if (vm.Id == 0) { Log(DocType.SelectionPlan, plan.Id, "Lập kế hoạch lựa chọn NCC", $"{vm.PackageIds.Count} gói"); await Db.SaveChangesAsync(); }
        if (submit) return await Submit(plan.Id);
        TempData["Success"] = I18n.T("Đã lưu {0}.", plan.Number);
        return RedirectToAction(nameof(Details), new { id = plan.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var plan = await LoadAsync(id);
        if (plan == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.SelectionPlan, id);
        ViewBag.CanDecide = IsDirector && plan.Status == SelectionPlanStatus.Submitted;
        return View(plan);
    }

    /// <summary>Bản in: tờ trình / quyết định phê duyệt KHLCNT</summary>
    public async Task<IActionResult> Print(int id)
    {
        var plan = await LoadAsync(id);
        if (plan == null) return NotFound();
        return View(plan);
    }

    private Task<SelectionPlan?> LoadAsync(int id) =>
        Db.SelectionPlans.AsNoTracking().AsSplitQuery().Include(x => x.CreatedBy).Include(x => x.DecidedBy)
            .Include(x => x.Packages).ThenInclude(p => p.PurchaseRequests).Include(x => x.Packages).ThenInclude(p => p.Buyer)
            .FirstOrDefaultAsync(x => x.Id == id);

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var plan = await Db.SelectionPlans.Include(x => x.Packages).ThenInclude(p => p.PurchaseRequests).FirstOrDefaultAsync(x => x.Id == id);
        if (plan == null) return NotFound();
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (plan.Status is not (SelectionPlanStatus.Draft or SelectionPlanStatus.Returned)) return Fail(I18n.T("Kế hoạch đã được trình."));
        if (plan.Packages.Count == 0) return Fail(I18n.T("Kế hoạch chưa có gói mua sắm nào."));
        if (plan.Packages.Any(p => p.Status != PackageStatus.Draft)) return Fail(I18n.T("Chỉ trình các gói ở trạng thái Nháp."));
        if (plan.Packages.FirstOrDefault(p => p.PurchaseRequests.Count == 0) is { } empty) return Fail(I18n.T("Gói {0} không còn đề nghị mua hàng nào.", empty.Number));
        plan.Status = SelectionPlanStatus.Submitted; plan.SubmittedAt = DateTime.Now;
        foreach (var p in plan.Packages) { p.Status = PackageStatus.InPlan; Log(DocType.Package, p.Id, $"Trình duyệt trong {plan.Number}"); }
        Log(DocType.SelectionPlan, id, "Trình Giám đốc phê duyệt KHLCNT", $"{plan.Packages.Count} gói · tổng giá gói {plan.Packages.Sum(p => p.Budget).ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã trình {0} cho Giám đốc phê duyệt.", plan.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.SelectionPlan)]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment, string? returnUrl)
    {
        var plan = await Db.SelectionPlans.Include(x => x.Packages).ThenInclude(p => p.PurchaseRequests).FirstOrDefaultAsync(x => x.Id == id);
        if (plan == null) return NotFound();
        if (plan.Status != SelectionPlanStatus.Submitted) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = I18n.T("Nhập lý do trả lại."); return RedirectToAction(nameof(Details), new { id }); }
        plan.Status = approve ? SelectionPlanStatus.Approved : SelectionPlanStatus.Returned;
        plan.DecidedById = Me; plan.DecidedAt = DateTime.Now; plan.DecisionNote = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        foreach (var p in plan.Packages)
        {
            p.Status = approve ? PackageStatus.Approved : PackageStatus.Draft;
            foreach (var r in p.PurchaseRequests) { r.Method = p.Method; r.MethodReason = p.MethodReason; }
            Log(DocType.Package, p.Id, approve ? $"Được phê duyệt trong {plan.Number} – lập hồ sơ lựa chọn NCC" : $"{plan.Number} bị trả lại", plan.DecisionNote);
        }
        Log(DocType.SelectionPlan, id, approve ? "Giám đốc phê duyệt KHLCNT" : "Giám đốc trả lại KHLCNT", plan.DecisionNote);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? I18n.T("Đã phê duyệt {0}. Mua hàng lập hồ sơ lựa chọn NCC cho từng gói.", plan.Number) : I18n.T("Đã trả lại {0}.", plan.Number);
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
    }
}
