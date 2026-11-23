using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class PlanForm
{
    public int Year { get; set; } = DateTime.Today.Year + 1;
    public string? Note { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

/// <summary>Một nhóm nhu cầu gộp từ kế hoạch các phòng ban (theo mặt hàng)</summary>
public record PlanGroup(int? ItemId, string Description, string? Unit, int? CategoryId, string Category, decimal Quantity, decimal Value,
    List<string> Departments, string Quarters, List<int> LineIds);

/// <summary>
/// Kế hoạch mua sắm năm: phòng ban lập (trưởng bộ phận trình), Giám đốc duyệt; mua hàng gom nhu cầu cùng mặt hàng / nhóm hàng
/// của các phòng ban thành một đề nghị mua hàng để tổ chức một lần (tăng quy mô gói, tránh chia nhỏ).
/// </summary>
[Authorize]
public class PlansController : ProcControllerBase
{
    public PlansController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    private bool SeesAll => User.IsPurchasing() || User.IsInRole(AppRoles.Director);

    public async Task<IActionResult> Index(int? year)
    {
        var y = year ?? DateTime.Today.Year + (DateTime.Today.Month >= 10 ? 1 : 0);
        var q = Db.ProcurementPlans.AsNoTracking().Include(p => p.Lines).Include(p => p.ApprovedBy).Where(p => p.Year == y);
        if (!SeesAll) { var d = User.GetDepartment(); q = q.Where(p => p.Department == d); }
        ViewBag.Year = y; ViewBag.SeesAll = SeesAll;
        ViewBag.HasOwn = await Db.ProcurementPlans.AnyAsync(p => p.Year == y && p.Department == User.GetDepartment());
        ViewData["Title"] = $"Kế hoạch mua sắm năm {y}";
        return View(await q.OrderBy(p => p.Department).ToListAsync());
    }

    private bool CanEditOwn => User.IsInRole(AppRoles.Approver) || User.IsInRole(AppRoles.Requester) || User.IsAdmin();

    public async Task<IActionResult> Edit(int year)
    {
        if (!CanEditOwn) return Forbid();
        var dept = User.GetDepartment();
        var p = await Db.ProcurementPlans.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Year == year && x.Department == dept);
        if (p != null && p.Status is PlanStatus.Submitted or PlanStatus.Approved) return RedirectToAction(nameof(Details), new { id = p.Id });
        await LoadItemsJsonAsync();
        ViewData["Title"] = $"Kế hoạch mua sắm {year} – {dept}";
        var vm = new PlanForm { Year = year, Note = p?.Note, Lines = p?.Lines.Select(l => new LineInput { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = l.EstimatedPrice, Quarter = l.Quarter }).ToList() ?? new() };
        if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
        ViewBag.Plan = p;
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PlanForm vm, bool submit = false)
    {
        if (!CanEditOwn) return Forbid();
        if (vm.Year is < 2000 or > 2100) return BadRequest();
        var lines = CleanLines(vm.Lines);
        if (!ModelState.IsValid) { await LoadItemsJsonAsync(); ViewBag.Plan = null; if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput()); return View(nameof(Edit), vm); }
        var dept = User.GetDepartment();
        var p = await Db.ProcurementPlans.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Year == vm.Year && x.Department == dept);
        if (p != null && p.Status is PlanStatus.Submitted or PlanStatus.Approved) { TempData["Error"] = "Kế hoạch đã trình / đã duyệt – không sửa được."; return RedirectToAction(nameof(Details), new { id = p.Id }); }
        if (p == null) { p = new ProcurementPlan { Year = vm.Year, Department = dept, CreatedById = Me }; Db.ProcurementPlans.Add(p); }
        Db.ProcurementPlanLines.RemoveRange(p.Lines);
        p.Lines.Clear();
        foreach (var l in lines)
            p.Lines.Add(new ProcurementPlanLine { ItemId = l.ItemId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.Price, Quarter = Math.Clamp(l.Quarter, 1, 4) });
        p.Note = vm.Note?.Trim();
        if (submit)
        {
            if (!(User.IsInRole(AppRoles.Approver) || User.IsAdmin())) { TempData["Error"] = "Trưởng bộ phận trình kế hoạch – bạn đã lưu nháp."; submit = false; }
            else { p.Status = PlanStatus.Submitted; p.SubmittedAt = DateTime.Now; }
        }
        await Db.SaveChangesAsync();
        Log(DocType.Plan, p.Id, submit ? "Trình Giám đốc duyệt kế hoạch mua sắm" : "Lưu kế hoạch", $"{p.Lines.Count} dòng · {p.Total.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = submit ? "Đã trình kế hoạch – chờ Giám đốc duyệt." : (TempData["Error"] == null ? "Đã lưu kế hoạch." : null);
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await Db.ProcurementPlans.AsNoTracking().Include(x => x.Lines).ThenInclude(l => l.Item).ThenInclude(i => i!.Category)
            .Include(x => x.Lines).ThenInclude(l => l.PurchaseRequest).Include(x => x.CreatedBy).Include(x => x.ApprovedBy).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (!SeesAll && p.Department != User.GetDepartment()) return Forbid();
        ViewBag.History = await HistoryAsync(DocType.Plan, id);
        ViewBag.Budget = await Db.Budgets.Where(b => b.Year == p.Year && b.Department == p.Department).SumAsync(b => (decimal?)b.Amount);
        ViewData["Title"] = $"Kế hoạch mua sắm {p.Year} – {p.Department}";
        return View(p);
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Plan)]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment)
    {
        var p = await Db.ProcurementPlans.FindAsync(id);
        if (p == null) return NotFound();
        if (p.Status != PlanStatus.Submitted) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = "Nhập lý do trả lại."; return RedirectToAction(nameof(Details), new { id }); }
        p.Status = approve ? PlanStatus.Approved : PlanStatus.Returned; p.DecisionNote = comment?.Trim();
        if (approve) { p.ApprovedById = Me; p.ApprovedAt = DateTime.Now; }
        Log(DocType.Plan, id, approve ? "Giám đốc duyệt kế hoạch mua sắm" : "Giám đốc trả lại kế hoạch", comment);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? "Đã duyệt kế hoạch." : "Đã trả lại kế hoạch.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Gom nhu cầu
    private async Task<List<PlanGroup>> GroupsAsync(int year)
    {
        var lines = await Db.ProcurementPlanLines.AsNoTracking().Include(l => l.Plan).Include(l => l.Item).ThenInclude(i => i!.Category)
            .Where(l => l.Plan!.Year == year && l.Plan.Status == PlanStatus.Approved && l.PurchaseRequestId == null).ToListAsync();
        return lines.GroupBy(l => l.ItemId.HasValue ? "i" + l.ItemId : "d" + l.Description.Trim().ToLowerInvariant())
            .Select(g => new PlanGroup(g.First().ItemId, g.First().Item?.Name ?? g.First().Description, g.First().Unit ?? g.First().Item?.Unit,
                g.First().Item?.CategoryId, g.First().Item?.Category?.Name ?? "Ngoài danh mục", g.Sum(x => x.Quantity), g.Sum(x => x.Quantity * x.EstimatedPrice),
                g.Select(x => x.Plan!.Department).Distinct().OrderBy(x => x).ToList(), string.Join(", ", g.Select(x => "Q" + x.Quarter).Distinct().OrderBy(x => x)), g.Select(x => x.Id).ToList()))
            .OrderByDescending(g => g.Value).ToList();
    }

    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
    public async Task<IActionResult> Consolidate(int year)
    {
        ViewBag.Year = year;
        ViewBag.CqMax = Settings.CompetitiveQuotationMax;
        ViewBag.Pending = await Db.ProcurementPlans.Where(p => p.Year == year && p.Status != PlanStatus.Approved).Select(p => p.Department + " (" + (p.Status == PlanStatus.Submitted ? "chờ duyệt" : "chưa trình") + ")").ToListAsync();
        ViewData["Title"] = $"Gom nhu cầu kế hoạch năm {year}";
        return View(await GroupsAsync(year));
    }

    /// <summary>Tạo một đề nghị mua hàng (đã duyệt theo kế hoạch) từ các nhóm nhu cầu được chọn</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePr(int year, List<string> keys, string? title)
    {
        var groups = (await GroupsAsync(year)).Where(g => keys.Contains(Key(g))).ToList();
        if (groups.Count == 0) { TempData["Error"] = "Chọn ít nhất một nhóm nhu cầu."; return RedirectToAction(nameof(Consolidate), new { year }); }
        var depts = groups.SelectMany(g => g.Departments).Distinct().OrderBy(x => x).ToList();
        var pr = new PurchaseRequest
        {
            Number = await DocNumbers.NextAsync(Db.PurchaseRequests.Select(p => p.Number), "PR"),
            Title = string.IsNullOrWhiteSpace(title) ? $"Mua sắm tập trung theo kế hoạch năm {year}: {string.Join(", ", groups.Select(g => g.Category).Distinct())}" : title.Trim(),
            RequesterId = Me, Department = User.GetDepartment(), Status = PrStatus.Approved, RequestDate = DateTime.Today, NeededDate = new DateTime(year, 1, 1).AddMonths(3 * (groups.Min(g => int.Parse(g.Quarters.Split(',')[0].Trim()[1..])) - 1)) is var nd && nd > DateTime.Today ? nd : DateTime.Today.AddDays(30),
            Purpose = $"Gom nhu cầu trong kế hoạch mua sắm năm {year} đã được Giám đốc duyệt của: {string.Join(", ", depts)}.", CreatedAt = DateTime.Now, SubmittedAt = DateTime.Now
        };
        if (pr.Title.Length > 200) pr.Title = pr.Title[..200];
        var s = 1;
        foreach (var g in groups)
            pr.Lines.Add(new PurchaseRequestLine { ItemId = g.ItemId, Description = g.Description, Unit = g.Unit, Quantity = g.Quantity, EstimatedPrice = g.Quantity == 0 ? 0 : Math.Round(g.Value / g.Quantity, 0), SortOrder = s++ });
        pr.Recalculate();
        Db.PurchaseRequests.Add(pr);
        await Db.SaveChangesAsync();
        var ids = groups.SelectMany(g => g.LineIds).ToList();
        await Db.ProcurementPlanLines.Where(l => ids.Contains(l.Id)).ExecuteUpdateAsync(x => x.SetProperty(l => l.PurchaseRequestId, pr.Id));
        Log(DocType.PR, pr.Id, $"Tạo từ kế hoạch mua sắm năm {year} đã duyệt (gom {depts.Count} phòng ban)", string.Join("; ", groups.Select(g => $"{g.Description}: {g.Quantity.ToQty()} {g.Unit} ({string.Join(", ", g.Departments)})")));
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã tạo {pr.Number} – {pr.EstimatedTotal.ToMoney()} đ. Chọn hình thức lựa chọn NCC để tiếp tục.";
        return RedirectToAction("Details", "PurchaseRequests", new { id = pr.Id });
    }

    public static string Key(PlanGroup g) => g.ItemId.HasValue ? "i" + g.ItemId : "d" + g.Description.Trim().ToLowerInvariant();
}
