using EProcurement.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EProcurement.Web.Controllers;

/// <summary>Chọn ngôn ngữ hiển thị (cờ Việt Nam / Anh) – dùng cho cả trang nội bộ, cổng NCC và trang đăng nhập</summary>
[AllowAnonymous]
public class LangController : Controller
{
    public IActionResult Set(string lang, string? returnUrl)
    {
        Response.Cookies.Append(I18n.CookieName, lang == I18n.EnCode ? I18n.EnCode : I18n.Vi,
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = Request.IsHttps });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
