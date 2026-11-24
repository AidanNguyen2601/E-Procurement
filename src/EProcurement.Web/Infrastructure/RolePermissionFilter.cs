using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace EProcurement.Web.Infrastructure;

/// <summary>
/// Thiết lập phân quyền: chặn vai trò khỏi chức năng (controller) đã bị Quản trị tắt.
/// Chỉ thu hẹp quyền mặc định; Quản trị hệ thống luôn có toàn quyền.
/// </summary>
public class RolePermissionFilter : IAsyncActionFilter
{
    /// <summary>Chức năng chung không áp phân quyền (đăng nhập, trang chủ, thông báo, giao diện, tệp đính kèm…)</summary>
    public static readonly HashSet<string> Exempt = new(StringComparer.OrdinalIgnoreCase)
        { "Account", "Home", "Notifications", "Appearance", "Attachments", "Settings", "Walkthrough" };

    private readonly SettingsStore _store;
    public RolePermissionFilter(SettingsStore store) => _store = store;

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var user = ctx.HttpContext.User;
        var ctrl = ctx.RouteData.Values["controller"]?.ToString();
        if (user.Identity?.IsAuthenticated == true && ctrl != null && !Exempt.Contains(ctrl)
            && !ctrl.StartsWith("Portal", StringComparison.Ordinal) && !ctrl.StartsWith("Api", StringComparison.Ordinal))
        {
            var role = user.FindFirst(ClaimTypes.Role)?.Value ?? "";
            var perms = await _store.GetAsync(SettingsStore.Permissions, () => new PermissionSettings());
            if (perms.IsDenied(ctrl, role)) { ctx.Result = new ForbidResult(); return; }
        }
        await next();
    }
}
