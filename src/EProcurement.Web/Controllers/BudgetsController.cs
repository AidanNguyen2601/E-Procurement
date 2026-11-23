using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Ngân sách mua sắm theo phòng ban × nhóm hàng × năm. Ban Giám đốc / Quản trị lập; mua hàng xem toàn bộ; trưởng bộ phận xem phòng mình.</summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Buyer + "," + AppRoles.Approver)]
public class BudgetsController : Controller
{
    private readonly EProcDbContext _db;
    private readonly BudgetService _svc;
    private readonly ProcurementSettings _s;
    public BudgetsController(EProcDbContext db, BudgetService svc, IOptions<ProcurementSettings> s) { _db = db; _svc = svc; _s = s.Value; }

    private bool CanEdit => User.IsAdmin() || User.IsInRole(AppRoles.Director);
    private string? OwnDeptOnly => User.IsInRole(AppRoles.Approver) ? User.GetDepartment() : null;

    public class BudgetPage
    {
        public int Year { get; set; }
        public List<BudgetStatus> Rows { get; set; } = new();
        public bool CanEdit { get; set; }
        public Budget? Editing { get; set; }
        public string Mode { get; set; } = "Warn";
        public int WarnPercent { get; set; }
        public List<int> Years { get; set; } = new();
    }

    public async Task<IActionResult> Index(int? year, int? edit)
    {
        var y = year ?? DateTime.Today.Year;
        var rows = await _svc.StatusAsync(y);
        if (OwnDeptOnly is string d) rows = rows.Where(r => r.Budget.Department == d).ToList();
        var years = await _db.Budgets.Select(b => b.Year).Distinct().ToListAsync();
        years.AddRange(new[] { DateTime.Today.Year - 1, DateTime.Today.Year, DateTime.Today.Year + 1 });
        var vm = new BudgetPage
        {
            Year = y, Rows = rows, CanEdit = CanEdit, Mode = _s.BudgetControl, WarnPercent = _s.BudgetWarnPercent,
            Editing = CanEdit && edit.HasValue ? rows.FirstOrDefault(r => r.Budget.Id == edit)?.Budget : null,
            Years = years.Distinct().OrderBy(x => x).ToList()
        };
        ViewBag.Departments = await HttpContext.RequestServices.GetRequiredService<CatalogService>().SelectAsync(CatalogType.Department, vm.Editing?.Department);
        ViewBag.Categories = new SelectList(await _db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", vm.Editing?.CategoryId);
        ViewData["Title"] = "Ngân sách mua sắm";
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> Save(Budget model)
    {
        IActionResult Back(string? err = null) { if (err != null) TempData["Error"] = err; return RedirectToAction(nameof(Index), new { year = model.Year, edit = err != null && model.Id > 0 ? model.Id : (int?)null }); }
        if (model.Year is < 2000 or > 2100) return Back("Năm không hợp lệ.");
        if (model.Amount < 0) return Back("Ngân sách không được âm.");
        var catalog = HttpContext.RequestServices.GetRequiredService<CatalogService>();
        var old = model.Id > 0 ? await _db.Budgets.FindAsync(model.Id) : null;
        if (!await catalog.IsValidAsync(CatalogType.Department, model.Department, old?.Department)) return Back("Chọn phòng ban trong danh mục.");
        if (await _db.Budgets.AnyAsync(b => b.Year == model.Year && b.Department == model.Department && b.CategoryId == model.CategoryId && b.Id != model.Id))
            return Back("Đã có dòng ngân sách cho phòng ban / nhóm hàng này trong năm – sửa dòng đó.");
        var b = old ?? _db.Budgets.Add(new Budget { CreatedAt = DateTime.Now }).Entity;
        b.Year = model.Year; b.Department = model.Department.Trim(); b.CategoryId = model.CategoryId; b.Amount = model.Amount;
        b.Note = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim(); b.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu ngân sách {b.Department} năm {b.Year}: {b.Amount.ToMoney()} đ.";
        return RedirectToAction(nameof(Index), new { year = model.Year });
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await _db.Budgets.FindAsync(id);
        if (b == null) return NotFound();
        _db.Budgets.Remove(b);
        await _db.SaveChangesAsync();
        TempData["Success"] = "Đã xóa dòng ngân sách.";
        return RedirectToAction(nameof(Index), new { year = b.Year });
    }

    /// <summary>Lập ngân sách năm mới từ năm trước, điều chỉnh theo % (chỉ thêm dòng chưa có)</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    public async Task<IActionResult> CopyYear(int from, int to, decimal percent = 0)
    {
        if (from == to || to is < 2000 or > 2100) { TempData["Error"] = "Năm không hợp lệ."; return RedirectToAction(nameof(Index), new { year = to }); }
        var src = await _db.Budgets.AsNoTracking().Where(b => b.Year == from).ToListAsync();
        var have = await _db.Budgets.Where(b => b.Year == to).Select(b => new { b.Department, b.CategoryId }).ToListAsync();
        var n = 0;
        foreach (var b in src.Where(b => !have.Any(h => h.Department == b.Department && h.CategoryId == b.CategoryId)))
        {
            _db.Budgets.Add(new Budget { Year = to, Department = b.Department, CategoryId = b.CategoryId, Amount = Math.Round(b.Amount * (1 + percent / 100m) / 1_000_000m) * 1_000_000m, Note = b.Note });
            n++;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = n == 0 ? $"Năm {to} đã có đủ các dòng của năm {from}." : $"Đã tạo {n} dòng ngân sách năm {to} từ năm {from} ({percent:+0.#;-0.#;0}%).";
        return RedirectToAction(nameof(Index), new { year = to });
    }
}
