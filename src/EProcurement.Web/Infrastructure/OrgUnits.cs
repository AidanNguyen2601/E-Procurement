using System.Globalization;
using System.Text;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Một dòng của cây tổ chức đã duỗi phẳng (theo thứ tự hiển thị) kèm độ sâu</summary>
public record OrgNode(OrgUnit Unit, int Depth, string Path);

/// <summary>
/// Cơ cấu tổ chức: duyệt cây, lấy đơn vị cấp dưới, khởi tạo từ phòng ban đang dùng, đồng bộ danh mục Phòng ban,
/// đổi tên đơn vị lan sang dữ liệu đang lưu tên phòng ban.
/// </summary>
public static class OrgUnits
{
    /// <summary>Duỗi cây theo thứ tự (cha trước con; cùng cấp theo thứ tự rồi tên). Đơn vị có cha không tồn tại coi là gốc.</summary>
    public static List<OrgNode> Flatten(IReadOnlyCollection<OrgUnit> all)
    {
        var ids = all.Select(u => u.Id).ToHashSet();
        var byParent = all.ToLookup(u => u.ParentId is int p && ids.Contains(p) ? p : (int?)null);
        var result = new List<OrgNode>();
        var seen = new HashSet<int>();
        void Walk(int? parent, int depth, string path)
        {
            foreach (var u in byParent[parent].OrderBy(u => u.SortOrder).ThenBy(u => u.Name))
            {
                if (!seen.Add(u.Id)) continue; // chống vòng lặp dữ liệu
                var p = path.Length == 0 ? u.Name : path + " › " + u.Name;
                result.Add(new OrgNode(u, depth, p));
                Walk(u.Id, depth + 1, p);
            }
        }
        Walk(null, 0, "");
        return result;
    }

    /// <summary>Id của đơn vị và mọi đơn vị cấp dưới</summary>
    public static HashSet<int> SubtreeIds(IReadOnlyCollection<OrgUnit> all, int id)
    {
        var set = new HashSet<int> { id };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var u in all.Where(u => u.ParentId is int p && set.Contains(p) && !set.Contains(u.Id))) { set.Add(u.Id); changed = true; }
        }
        return set;
    }

    /// <summary>Tên đơn vị và mọi đơn vị cấp dưới (dùng lọc báo cáo / ngân sách gộp theo khối); không có trong cây → chỉ tên đó</summary>
    public static async Task<List<string>> SubtreeNamesAsync(EProcDbContext db, string name)
    {
        var all = await db.OrgUnits.AsNoTracking().ToListAsync();
        var u = all.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (u == null) return new() { name };
        var ids = SubtreeIds(all, u.Id);
        return all.Where(x => ids.Contains(x.Id)).Select(x => x.Name).ToList();
    }

    /// <summary>Có thể chọn làm cấp trên của đơn vị <paramref name="unitId"/>: không phải chính nó hoặc cấp dưới của nó</summary>
    public static bool CanBeParent(IReadOnlyCollection<OrgUnit> all, int unitId, int? parentId) =>
        parentId == null || unitId == 0 || !SubtreeIds(all, unitId).Contains(parentId.Value);

    /// <summary>Mã viết tắt từ tên: "Phòng Kỹ thuật" → "PKT"</summary>
    public static string Abbreviate(string name)
    {
        var plain = new string(name.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd').Replace('Đ', 'D');
        var code = string.Concat(plain.Split(' ', '-', '–', '/', '&', ',').Where(w => w.Length > 0 && char.IsLetterOrDigit(w[0]))
            .Select(w => w.Length > 1 && w.All(char.IsUpper) ? w : char.ToUpperInvariant(w[0]).ToString())); // giữ nguyên từ viết tắt: CNTT
        return code.Length == 0 ? "DV" : code.Length > 20 ? code[..20] : code;
    }

    /// <summary>
    /// Khởi tạo / bổ sung cây: chưa có đơn vị nào → tạo gốc "Công ty"; mọi phòng ban đang dùng (danh mục, người dùng, đề nghị)
    /// chưa có trong cây → thêm làm phòng ban trực thuộc gốc, trưởng đơn vị = Trưởng bộ phận của phòng (nếu có). Gọi lại an toàn.
    /// </summary>
    public static async Task EnsureAsync(EProcDbContext db)
    {
        var units = await db.OrgUnits.ToListAsync();
        var root = units.FirstOrDefault(u => u.ParentId == null && u.Type == OrgUnitType.Company) ?? units.FirstOrDefault(u => u.ParentId == null);
        var names = (await db.CatalogEntries.Where(e => e.Type == CatalogType.Department).Select(e => e.Name).ToListAsync())
            .Concat(await db.Users.Select(u => u.Department).Distinct().ToListAsync())
            .Concat(await db.PurchaseRequests.Select(p => p.Department).Distinct().ToListAsync())
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var missing = names.Where(n => units.All(u => !string.Equals(u.Name, n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (root != null && missing.Count == 0) return;
        if (root == null && names.Count == 0 && !await db.Users.AnyAsync()) return; // database trống
        var codes = units.Select(u => u.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string NewCode(string name)
        {
            var b = Abbreviate(name); var c = b; var i = 2;
            while (!codes.Add(c)) c = b + i++;
            return c;
        }
        if (root == null)
        {
            var director = await db.Users.Where(u => u.Role == AppRoles.Director && u.IsActive).Select(u => (int?)u.Id).FirstOrDefaultAsync();
            root = new OrgUnit { Name = "Công ty", Code = NewCode("Cong ty"), Type = OrgUnitType.Company, ManagerId = director, Note = "Đơn vị gốc – đổi tên theo tên công ty" };
            db.OrgUnits.Add(root);
            await db.SaveChangesAsync();
        }
        var heads = await db.Users.Where(u => u.Role == AppRoles.Approver && u.IsActive).Select(u => new { u.Id, u.Department }).ToListAsync();
        var order = units.Count * 10;
        foreach (var n in missing)
        {
            db.OrgUnits.Add(new OrgUnit
            {
                Name = n, Code = NewCode(n), Type = n.StartsWith("Ban ", StringComparison.OrdinalIgnoreCase) ? OrgUnitType.Division : OrgUnitType.Department,
                ParentId = root.Id, ManagerId = heads.FirstOrDefault(h => string.Equals(h.Department, n, StringComparison.OrdinalIgnoreCase))?.Id, SortOrder = order += 10
            });
        }
        await db.SaveChangesAsync();
        await SyncCatalogAsync(db);
    }

    /// <summary>Danh mục Phòng ban = các đơn vị trong cây (mã, trạng thái hoạt động theo đơn vị)</summary>
    public static async Task SyncCatalogAsync(EProcDbContext db)
    {
        var units = await db.OrgUnits.AsNoTracking().ToListAsync();
        var entries = await db.CatalogEntries.Where(e => e.Type == CatalogType.Department).ToListAsync();
        var flat = Flatten(units);
        for (var i = 0; i < flat.Count; i++)
        {
            var u = flat[i].Unit;
            var e = entries.FirstOrDefault(x => string.Equals(x.Name, u.Name, StringComparison.OrdinalIgnoreCase));
            if (e == null) { e = new CatalogEntry { Type = CatalogType.Department, Name = u.Name }; db.CatalogEntries.Add(e); entries.Add(e); }
            e.Code = u.Code; e.IsActive = u.IsActive; e.SortOrder = (i + 1) * 10;
            e.Note = u.Type.GetDisplayName() + (flat[i].Depth > 0 ? " · " + flat[i].Path : "");
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Đổi tên đơn vị: cập nhật người dùng, đề nghị mua hàng, ngân sách, kế hoạch mua sắm, hạn mức duyệt đang lưu tên cũ</summary>
    public static async Task<int> CascadeRenameAsync(EProcDbContext db, string from, string to)
    {
        var n = await db.Users.Where(u => u.Department == from).ExecuteUpdateAsync(s => s.SetProperty(u => u.Department, to));
        n += await db.PurchaseRequests.Where(p => p.Department == from).ExecuteUpdateAsync(s => s.SetProperty(p => p.Department, to));
        n += await db.Budgets.Where(b => b.Department == from).ExecuteUpdateAsync(s => s.SetProperty(b => b.Department, to));
        n += await db.ProcurementPlans.Where(p => p.Department == from).ExecuteUpdateAsync(s => s.SetProperty(p => p.Department, to));
        n += await db.ApprovalLimits.Where(l => l.Department == from).ExecuteUpdateAsync(s => s.SetProperty(l => l.Department, to));
        await db.CatalogEntries.Where(e => e.Type == CatalogType.Department && e.Name == from).ExecuteUpdateAsync(s => s.SetProperty(e => e.Name, to));
        return n;
    }

    /// <summary>Số bản ghi đang dùng tên đơn vị (người dùng, đề nghị, ngân sách, kế hoạch, hạn mức)</summary>
    public static async Task<int> UsageAsync(EProcDbContext db, string name) =>
        await db.Users.CountAsync(u => u.Department == name) + await db.PurchaseRequests.CountAsync(p => p.Department == name)
        + await db.Budgets.CountAsync(b => b.Department == name) + await db.ProcurementPlans.CountAsync(p => p.Department == name)
        + await db.ApprovalLimits.CountAsync(l => l.Department == name);
}
