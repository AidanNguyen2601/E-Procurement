using System.Security.Claims;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>
/// Quyền theo gói mua sắm / gói thầu. Gói chưa phân thành viên → mọi nhân viên mua hàng được sửa như trước.
/// Gói đã phân thành viên → chỉ người phụ trách gói, thành viên (Phụ trách / Thành viên), Ban Giám đốc và Quản trị được sửa;
/// người "Chỉ xem" và nhân viên mua hàng khác chỉ xem.
/// </summary>
public static class PackageAccess
{
    public static async Task<bool> CanEditAsync(EProcDbContext db, ClaimsPrincipal user, int packageId)
    {
        if (user.IsAdmin() || user.IsInRole(AppRoles.Director)) return true;
        if (!user.IsPurchasing()) return false;
        var me = user.GetUserId();
        var members = await db.PackageMembers.AsNoTracking().Where(m => m.PackageId == packageId).ToListAsync();
        if (members.Count == 0) return true;
        if (await db.ProcurementPackages.AnyAsync(p => p.Id == packageId && p.BuyerId == me)) return true;
        return members.Any(m => m.UserId == me && m.Role != PackageRole.Viewer);
    }

    public static string Explain => "Gói đã phân quyền: chỉ người phụ trách và thành viên của gói được chỉnh sửa (Thiết lập → Quyền theo gói thầu).";
}
