using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Cơ cấu tổ chức: cây đơn vị (công ty → khối / chi nhánh → phòng ban → tổ), trưởng đơn vị, nhân sự, sơ đồ tổ chức.
/// Mọi người dùng nội bộ xem được; Quản trị hệ thống sửa. Tên đơn vị = phòng ban dùng trong toàn hệ thống.
/// </summary>
public class OrgUnitsController : Controller
{
    private readonly EProcDbContext _db;
    public OrgUnitsController(EProcDbContext db) => _db = db;

    public class OrgPage
    {
        public List<OrgNode> Nodes { get; set; } = new();
        public Dictionary<string, int> Headcount { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string View { get; set; } = "tree";
    }

    public async Task<IActionResult> Index(string? view)
    {
        var units = await _db.OrgUnits.AsNoTracking().Include(u => u.Manager).ToListAsync();
        var vm = new OrgPage
        {
            Nodes = OrgUnits.Flatten(units), View = view == "chart" ? "chart" : "tree",
            Headcount = (await _db.Users.Where(u => u.IsActive).GroupBy(u => u.Department).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                .ToDictionary(x => x.Key, x => x.N, StringComparer.OrdinalIgnoreCase)
        };
        ViewData["Title"] = "Cơ cấu tổ chức";
        return View(vm);
    }

    public async Task<IActionResult> Details(int id)
    {
        var all = await _db.OrgUnits.AsNoTracking().Include(u => u.Manager).ToListAsync();
        var u = all.FirstOrDefault(x => x.Id == id);
        if (u == null) return NotFound();
        var subtree = OrgUnits.SubtreeIds(all, id);
        var names = all.Where(x => subtree.Contains(x.Id)).Select(x => x.Name).ToList();
        ViewBag.Path = OrgUnits.Flatten(all).FirstOrDefault(n => n.Unit.Id == id)?.Path;
        ViewBag.Children = all.Where(x => x.ParentId == id).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).ToList();
        ViewBag.Members = await _db.Users.AsNoTracking().Where(x => x.Department == u.Name).OrderByDescending(x => x.IsActive).ThenBy(x => x.FullName).ToListAsync();
        ViewBag.SubtreeHeadcount = await _db.Users.CountAsync(x => x.IsActive && names.Contains(x.Department));
        ViewBag.Usage = await OrgUnits.UsageAsync(_db, u.Name);
        ViewBag.Stats = new
        {
            Prs = await _db.PurchaseRequests.CountAsync(p => names.Contains(p.Department)),
            Budget = await _db.Budgets.Where(b => b.Year == DateTime.Today.Year && names.Contains(b.Department)).SumAsync(b => (decimal?)b.Amount) ?? 0,
            Spend = await _db.PurchaseOrders.Where(p => p.Status >= PoStatus.Approved && p.Status != PoStatus.Cancelled && p.OrderDate.Year == DateTime.Today.Year
                                                        && p.PurchaseRequest != null && names.Contains(p.PurchaseRequest.Department)).SumAsync(p => (decimal?)p.SubTotal) ?? 0
        };
        if (User.IsAdmin())
            ViewBag.Users = new SelectList(await _db.Users.Where(x => x.IsActive && x.Department != u.Name).OrderBy(x => x.FullName)
                .Select(x => new { x.Id, Name = x.FullName + " – " + x.Department }).ToListAsync(), "Id", "Name");
        ViewData["Title"] = u.Name;
        return View(u);
    }

    private async Task LoadFormAsync(OrgUnit u)
    {
        var all = await _db.OrgUnits.AsNoTracking().ToListAsync();
        var blocked = u.Id == 0 ? new HashSet<int>() : OrgUnits.SubtreeIds(all, u.Id);
        ViewBag.Parents = OrgUnits.Flatten(all).Where(n => !blocked.Contains(n.Unit.Id))
            .Select(n => new SelectListItem(new string(' ', n.Depth * 4) + n.Unit.Name, n.Unit.Id.ToString(), n.Unit.Id == u.ParentId)).ToList();
        ViewBag.Managers = new SelectList(await _db.Users.Where(x => x.IsActive || x.Id == u.ManagerId).OrderBy(x => x.FullName)
            .Select(x => new { x.Id, Name = x.FullName + " – " + x.Department }).ToListAsync(), "Id", "Name", u.ManagerId);
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Create(int? parentId)
    {
        var u = new OrgUnit { ParentId = parentId };
        if (parentId.HasValue && await _db.OrgUnits.FindAsync(parentId) is { } p)
            u.Type = p.Type switch { OrgUnitType.Company => OrgUnitType.Division, OrgUnitType.Division or OrgUnitType.Branch => OrgUnitType.Department, _ => OrgUnitType.Team };
        await LoadFormAsync(u);
        ViewData["Title"] = "Thêm đơn vị";
        return View("Form", u);
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Edit(int id)
    {
        var u = await _db.OrgUnits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return NotFound();
        await LoadFormAsync(u);
        ViewData["Title"] = $"Sửa đơn vị {u.Name}";
        return View("Form", u);
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(OrgUnit model)
    {
        model.Name = model.Name?.Trim() ?? ""; model.Code = model.Code?.Trim().ToUpperInvariant() ?? "";
        if (string.IsNullOrEmpty(model.Code) && model.Name.Length > 0) { model.Code = OrgUnits.Abbreviate(model.Name); ModelState.Remove(nameof(model.Code)); }
        var all = await _db.OrgUnits.AsNoTracking().ToListAsync();
        if (all.Any(x => x.Id != model.Id && string.Equals(x.Name, model.Name, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(model.Name), I18n.T("Đã có đơn vị cùng tên (tên đơn vị là phòng ban dùng trong toàn hệ thống)."));
        if (all.Any(x => x.Id != model.Id && string.Equals(x.Code, model.Code, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(model.Code), I18n.T("Mã đơn vị đã được dùng."));
        if (!OrgUnits.CanBeParent(all, model.Id, model.ParentId)) ModelState.AddModelError(nameof(model.ParentId), I18n.T("Không chọn chính đơn vị hoặc đơn vị cấp dưới làm cấp trên."));
        if (model.ParentId == null && all.Any(x => x.ParentId == null && x.Id != model.Id))
            ModelState.AddModelError(nameof(model.ParentId), I18n.T("Chọn đơn vị cấp trên (chỉ có một đơn vị gốc)."));
        if (!ModelState.IsValid) { await LoadFormAsync(model); ViewData["Title"] = model.Id == 0 ? "Thêm đơn vị" : $"Sửa đơn vị {model.Name}"; return View("Form", model); }

        OrgUnit u;
        string? renamedFrom = null;
        if (model.Id == 0) { u = new OrgUnit(); _db.OrgUnits.Add(u); }
        else
        {
            u = await _db.OrgUnits.FirstAsync(x => x.Id == model.Id);
            if (!string.Equals(u.Name, model.Name, StringComparison.Ordinal)) renamedFrom = u.Name;
        }
        u.Name = model.Name; u.Code = model.Code; u.Type = model.Type; u.ParentId = model.ParentId; u.ManagerId = model.ManagerId;
        u.CostCenter = model.CostCenter?.Trim(); u.Phone = model.Phone?.Trim(); u.Address = model.Address?.Trim(); u.Note = model.Note?.Trim();
        u.SortOrder = model.SortOrder; u.IsActive = model.IsActive;
        await using var tx = await _db.Database.BeginTransactionAsync();
        var cascaded = renamedFrom != null ? await OrgUnits.CascadeRenameAsync(_db, renamedFrom, u.Name) : 0;
        await _db.SaveChangesAsync();
        await OrgUnits.SyncCatalogAsync(_db);
        await tx.CommitAsync();
        TempData["Success"] = I18n.T("Đã lưu đơn vị {0}.", u.Name) + (renamedFrom != null ? I18n.T(" Đổi tên từ \"{0}\": cập nhật {1} bản ghi (người dùng, đề nghị, ngân sách, kế hoạch, hạn mức).", renamedFrom, cascaded) : "");
        return RedirectToAction(nameof(Details), new { id = u.Id });
    }

    /// <summary>Chuyển nhân sự vào đơn vị (cập nhật phòng ban của người dùng)</summary>
    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddMember(int id, int userId, bool makeManager = false)
    {
        var u = await _db.OrgUnits.FindAsync(id);
        var user = await _db.Users.FindAsync(userId);
        if (u == null || user == null) return NotFound();
        var from = user.Department;
        user.Department = u.Name;
        if (makeManager) u.ManagerId = user.Id;
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã chuyển {0} từ {1} sang {2}.", user.FullName, from, u.Name) + (makeManager ? I18n.T(" Đặt làm trưởng đơn vị.") : "");
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var u = await _db.OrgUnits.Include(x => x.Children).FirstOrDefaultAsync(x => x.Id == id);
        if (u == null) return NotFound();
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (u.Children.Any()) return Fail(I18n.T("Đơn vị còn đơn vị cấp dưới – chuyển hoặc xóa các đơn vị đó trước."));
        if (u.ParentId == null) return Fail(I18n.T("Không xóa đơn vị gốc."));
        var used = await OrgUnits.UsageAsync(_db, u.Name);
        if (used > 0) return Fail(I18n.T("\"{0}\" đang được dùng ở {1} bản ghi (người dùng, đề nghị, ngân sách…) – chọn \"Ngừng hoạt động\" thay vì xóa.", u.Name, used));
        _db.OrgUnits.Remove(u);
        await _db.CatalogEntries.Where(e => e.Type == CatalogType.Department && e.Name == u.Name).ExecuteDeleteAsync();
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa đơn vị {0}.", u.Name);
        return RedirectToAction(nameof(Index));
    }
}
