using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Thông báo của người dùng hiện tại</summary>
[Authorize]
public class NotificationsController : Controller
{
    private readonly EProcDbContext _db;
    public NotificationsController(EProcDbContext db) => _db = db;
    private int Me => User.GetUserId();

    public async Task<IActionResult> Index(bool unread = false, int page = 1)
    {
        const int size = 30;
        var q = _db.Notifications.AsNoTracking().Where(n => n.UserId == Me);
        if (unread) q = q.Where(n => n.ReadAt == null);
        ViewBag.Total = await q.CountAsync();
        ViewBag.Page = Math.Max(1, page); ViewBag.Size = size; ViewBag.Unread = unread;
        ViewBag.EmailOn = await _db.Users.Where(u => u.Id == Me).Select(u => u.EmailNotifications).FirstOrDefaultAsync();
        ViewBag.Email = await _db.Users.Where(u => u.Id == Me).Select(u => u.Email).FirstOrDefaultAsync();
        ViewData["Title"] = "Thông báo";
        return View(await q.OrderByDescending(n => n.CreatedAt).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync());
    }

    /// <summary>Mở thông báo: đánh dấu đã đọc rồi chuyển tới chứng từ</summary>
    public async Task<IActionResult> Open(int id)
    {
        var n = await _db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == Me);
        if (n == null) return RedirectToAction(nameof(Index));
        n.ReadAt ??= DateTime.Now;
        await _db.SaveChangesAsync();
        return !string.IsNullOrEmpty(n.Url) && Url.IsLocalUrl(n.Url) ? LocalRedirect(n.Url) : RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        await _db.Notifications.Where(n => n.UserId == Me && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTime.Now));
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EmailPreference(bool enabled)
    {
        await _db.Users.Where(u => u.Id == Me).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailNotifications, enabled));
        TempData["Success"] = enabled ? "Đã bật nhận thông báo qua email." : "Đã tắt nhận thông báo qua email.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Quét nhắc hạn ngay (Quản trị) – bình thường chạy nền theo chu kỳ</summary>
    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> RunNow([FromServices] NotificationService svc)
    {
        var n = await svc.RunRemindersAsync();
        var m = await svc.SendEmailsAsync();
        TempData["Success"] = $"Đã quét nhắc hạn: {n} thông báo mới, {m} email đã gửi.";
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Chuông thông báo trên thanh trên cùng</summary>
public class NotificationBellViewComponent : ViewComponent
{
    private readonly EProcDbContext _db;
    public NotificationBellViewComponent(EProcDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var me = UserClaimsPrincipal.GetUserId();
        var q = _db.Notifications.AsNoTracking().Where(n => n.UserId == me && n.ReadAt == null);
        ViewBag.Count = await q.CountAsync();
        return View(await q.OrderByDescending(n => n.CreatedAt).Take(6).ToListAsync());
    }
}
