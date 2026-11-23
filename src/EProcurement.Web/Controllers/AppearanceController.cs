using EProcurement.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Giao diện: mỗi người dùng tự chọn vị trí menu, màu chủ đạo, kiểu menu, font, cỡ chữ.
/// Quản trị đặt giao diện mặc định cho người dùng chưa tự chọn.
/// </summary>
public class AppearanceController : Controller
{
    private readonly UiThemeService _themes;
    public AppearanceController(UiThemeService themes) => _themes = themes;

    public async Task<IActionResult> Index()
    {
        var own = await _themes.OwnAsync(User.GetUserId());
        ViewBag.HasOwn = own != null;
        ViewBag.Default = await _themes.SystemDefaultAsync();
        ViewData["Title"] = "Giao diện";
        return View(own ?? await _themes.SystemDefaultAsync());
    }

    /// <summary>scope: me = lưu cho tôi; default = mặc định toàn hệ thống (Quản trị); reset = bỏ tùy chọn riêng</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UiPrefs prefs, string scope = "me")
    {
        var p = UiTheme.Sanitize(prefs);
        switch (scope)
        {
            case "default":
                if (!User.IsAdmin()) return Forbid();
                await _themes.SaveDefaultAsync(p);
                TempData["Success"] = "Đã lưu giao diện mặc định cho người dùng chưa tự chọn giao diện riêng.";
                break;
            case "reset":
                await _themes.SaveOwnAsync(User.GetUserId(), null);
                TempData["Success"] = "Đã chuyển về giao diện mặc định của hệ thống.";
                break;
            default:
                await _themes.SaveOwnAsync(User.GetUserId(), p);
                TempData["Success"] = "Đã lưu giao diện của bạn.";
                break;
        }
        return RedirectToAction(nameof(Index));
    }
}
