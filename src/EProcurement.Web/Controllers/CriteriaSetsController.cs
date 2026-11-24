using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Bộ tiêu chí đánh giá: thư viện tiêu chí dùng lại cho sơ tuyển NCC, hồ sơ mời thầu (năng lực / kỹ thuật), đánh giá báo giá.
/// Nhân viên mua hàng và Quản trị quản lý.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing)]
public class CriteriaSetsController : Controller
{
    private readonly EProcDbContext _db;
    public CriteriaSetsController(EProcDbContext db) => _db = db;

    public static readonly CriteriaKind[] TenderKinds = { CriteriaKind.TenderQualification, CriteriaKind.TenderTechnical };

    public class CriteriaForm
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public CriteriaKind Kind { get; set; } = CriteriaKind.Prequalification;
        public int? CategoryId { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        public List<CriteriaItem> Items { get; set; } = new();
    }

    public async Task<IActionResult> Index(string? group, CriteriaKind? kind, bool inactive = false)
    {
        var q = _db.CriteriaSets.AsNoTracking().Include(s => s.Items).Include(s => s.Category).Include(s => s.CreatedBy).AsQueryable();
        if (group == "tender") q = q.Where(s => TenderKinds.Contains(s.Kind));
        if (kind.HasValue) q = q.Where(s => s.Kind == kind);
        if (!inactive) q = q.Where(s => s.IsActive);
        ViewBag.Group = group; ViewBag.Kind = kind; ViewBag.Inactive = inactive;
        ViewData["Title"] = group == "tender" ? "Tiêu chí đánh giá HSDT" : "Bộ tiêu chí đánh giá";
        return View(await q.OrderBy(s => s.Kind).ThenBy(s => s.Name).ToListAsync());
    }

    private async Task LoadAsync(int? categoryId) =>
        ViewBag.Categories = new SelectList(await _db.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(), "Id", "Name", categoryId);

    public async Task<IActionResult> Create(CriteriaKind? kind, int? copyFrom)
    {
        var f = new CriteriaForm { Kind = kind ?? CriteriaKind.Prequalification };
        if (copyFrom.HasValue && await _db.CriteriaSets.AsNoTracking().Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == copyFrom) is { } src)
        {
            f.Name = src.Name + " (bản sao)"; f.Kind = src.Kind; f.CategoryId = src.CategoryId; f.Description = src.Description;
            f.Items = src.Items.OrderBy(i => i.SortOrder).Select(i => new CriteriaItem { Name = i.Name, Requirement = i.Requirement, IsMandatory = i.IsMandatory, MaxScore = i.MaxScore, DocumentType = i.DocumentType }).ToList();
        }
        if (f.Items.Count == 0) f.Items.Add(new CriteriaItem());
        await LoadAsync(f.CategoryId);
        ViewData["Title"] = "Thêm bộ tiêu chí";
        return View("Form", f);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var s = await _db.CriteriaSets.AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        var f = new CriteriaForm { Id = s.Id, Name = s.Name, Kind = s.Kind, CategoryId = s.CategoryId, Description = s.Description, IsActive = s.IsActive, Items = s.Items.OrderBy(i => i.SortOrder).ToList() };
        await LoadAsync(f.CategoryId);
        ViewData["Title"] = $"Sửa bộ tiêu chí {s.Name}";
        return View("Form", f);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CriteriaForm f)
    {
        f.Items = f.Items.Where(i => !string.IsNullOrWhiteSpace(i.Name)).ToList();
        if (string.IsNullOrWhiteSpace(f.Name)) ModelState.AddModelError(nameof(f.Name), "Nhập tên bộ tiêu chí.");
        if (f.Items.Count == 0) ModelState.AddModelError(nameof(f.Items), "Nhập ít nhất một tiêu chí.");
        if (f.Items.Any(i => i.MaxScore is < 0 or > 100)) ModelState.AddModelError(nameof(f.Items), "Điểm tối đa từ 0 đến 100.");
        foreach (var k in ModelState.Keys.Where(k => k.StartsWith("Items[")).ToList()) ModelState.Remove(k);
        if (!ModelState.IsValid) { if (f.Items.Count == 0) f.Items.Add(new CriteriaItem()); await LoadAsync(f.CategoryId); return View("Form", f); }

        CriteriaSet s;
        if (f.Id == 0) { s = new CriteriaSet { CreatedById = User.GetUserId() }; _db.CriteriaSets.Add(s); }
        else
        {
            s = await _db.CriteriaSets.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == f.Id) ?? throw new KeyNotFoundException();
            _db.CriteriaItems.RemoveRange(s.Items);
        }
        static string? T(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(max, v.Trim().Length)];
        s.Name = T(f.Name, 200)!; s.Kind = Enum.IsDefined(f.Kind) ? f.Kind : CriteriaKind.Prequalification; s.CategoryId = f.CategoryId;
        s.Description = T(f.Description, 1000); s.IsActive = f.IsActive; s.UpdatedAt = DateTime.Now;
        var order = 0;
        foreach (var i in f.Items)
            s.Items.Add(new CriteriaItem { Name = T(i.Name, 200)!, Requirement = T(i.Requirement, 1000), IsMandatory = i.IsMandatory, MaxScore = i.IsMandatory ? 0 : i.MaxScore, DocumentType = i.DocumentType, SortOrder = ++order });
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu bộ tiêu chí \"{s.Name}\" ({s.Items.Count} tiêu chí).";
        return RedirectToAction(nameof(Index), new { group = TenderKinds.Contains(s.Kind) ? "tender" : null });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.CriteriaSets.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();
        _db.CriteriaItems.RemoveRange(s.Items);
        _db.CriteriaSets.Remove(s);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã xóa bộ tiêu chí \"{s.Name}\".";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Danh sách bộ tiêu chí (JSON) để chèn vào đợt sơ tuyển / hồ sơ mời thầu</summary>
    public async Task<IActionResult> Pick(string kinds)
    {
        var ks = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(k => Enum.TryParse<CriteriaKind>(k, out var v) ? v : (CriteriaKind?)null).Where(k => k != null).Select(k => k!.Value).ToList();
        var sets = await _db.CriteriaSets.AsNoTracking().Include(s => s.Items).Where(s => s.IsActive && ks.Contains(s.Kind)).OrderBy(s => s.Name).ToListAsync();
        return Json(sets.Select(s => new
        {
            s.Id, s.Name, Kind = s.Kind.GetDisplayName(),
            Items = s.Items.OrderBy(i => i.SortOrder).Select(i => new { i.Name, i.Requirement, i.IsMandatory, i.MaxScore, DocumentType = (int?)i.DocumentType })
        }));
    }

    /// <summary>Văn bản tiêu chí (chèn vào ô tiêu chí năng lực / yêu cầu kỹ thuật của HSMT)</summary>
    public static string AsText(IEnumerable<CriteriaItem> items) => string.Join("\n", items.Select((i, n) =>
        $"{n + 1}. {i.Name}" + (i.IsMandatory ? " (bắt buộc – đạt / không đạt)" : i.MaxScore > 0 ? $" ({i.MaxScore} điểm)" : "") + (string.IsNullOrWhiteSpace(i.Requirement) ? "" : ": " + i.Requirement)));
}
