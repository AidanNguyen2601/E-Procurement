using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Chụp phiên bản BOQ của gói (gọi trước khi thay thế / khôi phục / mời thầu để luôn có bản quay lại)</summary>
public static class BoqVersions
{
    /// <summary>Chụp BOQ hiện tại; trả về null nếu gói chưa có dòng BOQ nào (không tạo bản rỗng). Chưa SaveChanges.</summary>
    public static async Task<BoqVersion?> SnapshotAsync(EProcDbContext db, int packageId, string label, int userId)
    {
        var items = await db.BoqItems.AsNoTracking().Where(i => i.PackageId == packageId).ToListAsync();
        if (items.Count == 0) return null;
        var next = (await db.BoqVersions.Where(v => v.PackageId == packageId).MaxAsync(v => (int?)v.Number) ?? 0) + 1;
        var v = new BoqVersion
        {
            PackageId = packageId, Number = next, Label = label.Length > 200 ? label[..200] : label, ItemCount = items.Count,
            Total = Boq.Total(items), Snapshot = BoqCost.ToSnapshot(items), CreatedById = userId, CreatedAt = DateTime.Now
        };
        db.BoqVersions.Add(v);
        return v;
    }
}
