using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Phân cấp hàng hóa dịch vụ: cây nhóm hàng nhiều cấp (mọi người xem; mua hàng / Quản trị sửa)</summary>
public class CategoriesController : Controller
{
    private readonly EProcDbContext _db;
    public CategoriesController(EProcDbContext db) => _db = db;

    public record CatNode(Category Cat, int Depth, string Path);

    /// <summary>Duỗi cây theo thứ tự hiển thị; chống vòng lặp</summary>
    public static List<CatNode> Flatten(IReadOnlyCollection<Category> all)
    {
        var ids = all.Select(c => c.Id).ToHashSet();
        var byParent = all.ToLookup(c => c.ParentId is int p && ids.Contains(p) ? p : (int?)null);
        var res = new List<CatNode>(); var seen = new HashSet<int>();
        void Walk(int? parent, int depth, string path)
        {
            foreach (var c in byParent[parent].OrderBy(c => c.SortOrder).ThenBy(c => c.Code))
            {
                if (!seen.Add(c.Id)) continue;
                var p = path.Length == 0 ? c.Name : path + " › " + c.Name;
                res.Add(new CatNode(c, depth, p));
                Walk(c.Id, depth + 1, p);
            }
        }
        Walk(null, 0, "");
        return res;
    }

    /// <summary>Id nhóm và mọi nhóm con</summary>
    public static HashSet<int> SubtreeIds(IReadOnlyCollection<Category> all, int id)
    {
        var set = new HashSet<int> { id }; var changed = true;
        while (changed) { changed = false; foreach (var c in all.Where(c => c.ParentId is int p && set.Contains(p) && !set.Contains(c.Id))) { set.Add(c.Id); changed = true; } }
        return set;
    }

    /// <summary>Danh sách chọn nhóm hàng thụt lề theo cấp</summary>
    public static List<SelectListItem> Options(IReadOnlyCollection<Category> all, int? selected, ISet<int>? exclude = null) =>
        Flatten(all).Where(n => exclude == null || !exclude.Contains(n.Cat.Id))
            .Select(n => new SelectListItem(new string(' ', n.Depth * 4) + n.Cat.Code + " – " + n.Cat.Name, n.Cat.Id.ToString(), n.Cat.Id == selected)).ToList();

    public async Task<IActionResult> Index(int? edit, int? parentId)
    {
        var all = await _db.Categories.AsNoTracking().ToListAsync();
        ViewBag.Nodes = Flatten(all);
        ViewBag.ItemCounts = await _db.Items.Where(i => i.CategoryId != null).GroupBy(i => i.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        ViewBag.VendorCounts = await _db.VendorCategories.GroupBy(v => v.CategoryId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        var editing = edit.HasValue ? all.FirstOrDefault(c => c.Id == edit) : null;
        ViewBag.Editing = editing;
        ViewBag.Parents = Options(all, editing?.ParentId ?? parentId, editing == null ? null : SubtreeIds(all, editing.Id));
        ViewData["Title"] = "Phân cấp hàng hóa dịch vụ";
        return View();
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, string code, string name, int? parentId, int sortOrder, string? description, bool isActive = true)
    {
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { edit = id == 0 ? (int?)null : id }); }
        code = code?.Trim().ToUpperInvariant() ?? ""; name = name?.Trim() ?? "";
        if (code.Length == 0 || name.Length == 0) return Fail("Nhập mã và tên nhóm hàng.");
        if (code.Length > 20 || name.Length > 150) return Fail("Mã tối đa 20, tên tối đa 150 ký tự.");
        var all = await _db.Categories.AsNoTracking().ToListAsync();
        if (all.Any(c => c.Id != id && string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase))) return Fail("Mã nhóm hàng đã tồn tại.");
        if (parentId.HasValue && (all.All(c => c.Id != parentId) || (id != 0 && SubtreeIds(all, id).Contains(parentId.Value))))
            return Fail("Không chọn chính nhóm hoặc nhóm con của nó làm nhóm cấp trên.");
        Category c;
        if (id == 0) { c = new Category(); _db.Categories.Add(c); }
        else c = await _db.Categories.FindAsync(id) ?? throw new KeyNotFoundException();
        c.Code = code; c.Name = name; c.ParentId = parentId; c.SortOrder = sortOrder; c.IsActive = isActive;
        c.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim()[..Math.Min(500, description.Trim().Length)];
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu nhóm hàng {c.Code} – {c.Name}.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.Categories.FindAsync(id);
        if (c == null) return NotFound();
        var used = await _db.Categories.CountAsync(x => x.ParentId == id) + await _db.Items.CountAsync(i => i.CategoryId == id)
                   + await _db.VendorCategories.CountAsync(v => v.CategoryId == id) + await _db.CriteriaSets.CountAsync(s => s.CategoryId == id)
                   + await _db.Prequalifications.CountAsync(p => p.CategoryId == id);
        if (used > 0) { TempData["Error"] = $"Nhóm {c.Name} đang có nhóm con / hàng hóa / NCC / đợt sơ tuyển – chọn \"Ngừng sử dụng\" thay vì xóa."; return RedirectToAction(nameof(Index)); }
        _db.Categories.Remove(c);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã xóa nhóm {c.Name}.";
        return RedirectToAction(nameof(Index));
    }
}
