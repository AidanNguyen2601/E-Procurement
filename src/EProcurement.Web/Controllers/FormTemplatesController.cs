using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Biểu mẫu: thư viện file mẫu (hồ sơ mời thầu, hợp đồng, biên bản…) – mọi người tải; mua hàng / Quản trị quản lý</summary>
public class FormTemplatesController : Controller
{
    private readonly EProcDbContext _db;
    public FormTemplatesController(EProcDbContext db) => _db = db;

    public const long MaxSize = 20 * 1024 * 1024;
    public static readonly string[] Groups =
        { "Kế hoạch & đề nghị", "Hồ sơ mời thầu / yêu cầu", "Chào giá", "Đánh giá & thẩm định", "Hợp đồng", "Giao nhận & nghiệm thu", "Thanh toán", "Nhà cung cấp", "Khác" };
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".docx", ".xls", ".xlsx", ".pdf", ".ppt", ".pptx", ".odt", ".ods", ".txt", ".zip", ".rar", ".7z" };

    public async Task<IActionResult> Index(string? group, string? q, bool inactive = false)
    {
        var manage = User.IsPurchasing();
        var list = _db.FormTemplates.AsNoTracking().Include(f => f.UploadedBy).AsQueryable();
        if (!manage || !inactive) list = list.Where(f => f.IsActive);
        if (!string.IsNullOrWhiteSpace(group)) list = list.Where(f => f.Group == group);
        if (!string.IsNullOrWhiteSpace(q)) list = list.Where(f => f.Name.Contains(q) || (f.Code != null && f.Code.Contains(q)) || (f.Description != null && f.Description.Contains(q)));
        ViewBag.Group = group; ViewBag.Q = q; ViewBag.Inactive = inactive; ViewBag.Manage = manage;
        ViewBag.Counts = await _db.FormTemplates.Where(f => f.IsActive).GroupBy(f => f.Group).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        ViewData["Title"] = "Biểu mẫu";
        // Không tải nội dung file khi liệt kê
        return View(await list.OrderBy(f => f.Group).ThenBy(f => f.Code).ThenBy(f => f.Name)
            .Select(f => new FormTemplate { Id = f.Id, Name = f.Name, Code = f.Code, Group = f.Group, Description = f.Description, Version = f.Version, FileName = f.FileName,
                Size = f.Size, IsActive = f.IsActive, Downloads = f.Downloads, UploadedAt = f.UploadedAt, UploadedBy = f.UploadedBy }).ToListAsync());
    }

    public async Task<IActionResult> Download(int id)
    {
        var f = await _db.FormTemplates.FirstOrDefaultAsync(x => x.Id == id);
        if (f == null || (!f.IsActive && !User.IsPurchasing())) return NotFound();
        f.Downloads++;
        await _db.SaveChangesAsync();
        return File(f.Data, f.ContentType, f.FileName);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(MaxSize + 1024 * 1024)]
    public async Task<IActionResult> Save(int id, string name, string? code, string group, string? description, string? version, bool isActive, IFormFile? file)
    {
        static string? T(string? v, int max) => string.IsNullOrWhiteSpace(v) ? null : v.Trim()[..Math.Min(max, v.Trim().Length)];
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { edit = id == 0 ? (int?)null : id }); }
        if (string.IsNullOrWhiteSpace(name)) return Fail("Nhập tên biểu mẫu.");
        FormTemplate f;
        if (id == 0)
        {
            if (file == null || file.Length == 0) return Fail("Chọn file biểu mẫu.");
            f = new FormTemplate { UploadedById = User.GetUserId() };
            _db.FormTemplates.Add(f);
        }
        else f = await _db.FormTemplates.FirstOrDefaultAsync(x => x.Id == id) ?? throw new KeyNotFoundException();
        if (file != null && file.Length > 0)
        {
            if (file.Length > MaxSize) return Fail("File tối đa 20 MB.");
            var ext = Path.GetExtension(file.FileName);
            if (!AllowedExt.Contains(ext)) return Fail("Chỉ nhận file Word, Excel, PDF, PowerPoint, văn bản hoặc file nén.");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            f.Data = ms.ToArray(); f.Size = file.Length; f.FileName = Path.GetFileName(file.FileName)[..Math.Min(260, Path.GetFileName(file.FileName).Length)];
            f.ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType[..Math.Min(100, file.ContentType.Length)];
            f.UploadedById = User.GetUserId(); f.UploadedAt = DateTime.Now;
        }
        f.Name = T(name, 200)!; f.Code = T(code, 30); f.Group = Groups.Contains(group) ? group : "Khác";
        f.Description = T(description, 1000); f.Version = T(version, 20); f.IsActive = isActive;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu biểu mẫu \"{f.Name}\".";
        return RedirectToAction(nameof(Index), new { group = f.Group });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var f = await _db.FormTemplates.FindAsync(id);
        if (f == null) return NotFound();
        _db.FormTemplates.Remove(f);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã xóa biểu mẫu \"{f.Name}\".";
        return RedirectToAction(nameof(Index));
    }
}
