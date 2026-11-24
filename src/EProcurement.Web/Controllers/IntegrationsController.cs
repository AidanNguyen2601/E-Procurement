using System.Security.Cryptography;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Quản lý API key cho hệ thống ERP / kế toán (chỉ Quản trị) + tài liệu endpoint</summary>
[Authorize(Roles = AppRoles.Admin)]
public class IntegrationsController : Controller
{
    private readonly EProcDbContext _db;
    public IntegrationsController(EProcDbContext db) => _db = db;

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Tích hợp ERP – REST API";
        return View(await _db.ApiClients.AsNoTracking().Include(c => c.CreatedBy).OrderByDescending(c => c.CreatedAt).ToListAsync());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string? name, List<string> scopes)
    {
        scopes = scopes.Where(s => ApiScopes.All.Contains(s)).Distinct().ToList();
        if (string.IsNullOrWhiteSpace(name) || scopes.Count == 0) { TempData["Error"] = I18n.T("Nhập tên hệ thống và chọn ít nhất một phạm vi."); return RedirectToAction(nameof(Index)); }
        // Key ngẫu nhiên 32 byte; chỉ hiển thị một lần, lưu SHA-256
        var key = "ep_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _db.ApiClients.Add(new ApiClient { Name = name.Trim()[..Math.Min(100, name.Trim().Length)], KeyHash = ApiScopes.Hash(key), KeyPrefix = key[..11], Scopes = string.Join(",", scopes), CreatedById = User.GetUserId() });
        await _db.SaveChangesAsync();
        TempData["NewKey"] = key;
        TempData["Success"] = I18n.T("Đã tạo API key. Sao chép ngay – key chỉ hiển thị một lần.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int id)
    {
        var c = await _db.ApiClients.FindAsync(id);
        if (c == null) return NotFound();
        c.IsActive = false; c.RevokedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã thu hồi key của {0}.", c.Name);
        return RedirectToAction(nameof(Index));
    }
}
