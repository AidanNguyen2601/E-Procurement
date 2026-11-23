using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

public class OrgUnitTests
{
    private static List<OrgUnit> Tree() => new()
    {
        new() { Id = 1, Code = "CT", Name = "Công ty", Type = OrgUnitType.Company },
        new() { Id = 2, Code = "KKD", Name = "Khối Kinh doanh", Type = OrgUnitType.Division, ParentId = 1, SortOrder = 20 },
        new() { Id = 3, Code = "PMH", Name = "Phòng Mua hàng", ParentId = 1, SortOrder = 10 },
        new() { Id = 4, Code = "PKD1", Name = "Phòng Kinh doanh 1", ParentId = 2 },
        new() { Id = 5, Code = "T1", Name = "Tổ bán lẻ", Type = OrgUnitType.Team, ParentId = 4 },
    };

    [Fact]
    public void Flatten_orders_parent_first_then_sort_order()
    {
        var flat = OrgUnits.Flatten(Tree());
        Assert.Equal(new[] { 1, 3, 2, 4, 5 }, flat.Select(n => n.Unit.Id));
        Assert.Equal(3, flat.Single(n => n.Unit.Id == 5).Depth);
        Assert.Equal("Công ty › Khối Kinh doanh › Phòng Kinh doanh 1 › Tổ bán lẻ", flat.Single(n => n.Unit.Id == 5).Path);
    }

    [Fact]
    public void Flatten_survives_cycles()
    {
        var t = Tree();
        t[0].ParentId = 5; // vòng 1→5→4→2→1: không còn gốc thực, không treo
        var flat = OrgUnits.Flatten(t);
        Assert.True(flat.Count <= t.Count);
    }

    [Fact]
    public void Subtree_and_parent_rules()
    {
        var t = Tree();
        Assert.Equal(new HashSet<int> { 2, 4, 5 }, OrgUnits.SubtreeIds(t, 2));
        Assert.False(OrgUnits.CanBeParent(t, 2, 5));  // không chọn cấp dưới làm cấp trên
        Assert.False(OrgUnits.CanBeParent(t, 2, 2));
        Assert.True(OrgUnits.CanBeParent(t, 4, 3));
        Assert.True(OrgUnits.CanBeParent(t, 0, 5));   // đơn vị mới
    }

    [Theory]
    [InlineData("Phòng Kỹ thuật", "PKT")]
    [InlineData("Ban Đầu tư - Xây dựng", "BDTXD")]
    [InlineData("---", "DV")]
    [InlineData("Phòng CNTT", "PCNTT")]
    public void Abbreviate_strips_diacritics(string name, string code) => Assert.Equal(code, OrgUnits.Abbreviate(name));

    [Fact]
    public async Task Ensure_builds_tree_from_existing_departments_and_is_idempotent()
    {
        using var db = TestDb.Create();
        TestDb.Seed(db); // người dùng Phòng Mua hàng
        db.Users.Add(new AppUser { Username = "tbp", FullName = "Trưởng KT", Role = AppRoles.Approver, Department = "Phòng Kế toán", PasswordHash = "x" });
        db.CatalogEntries.Add(new CatalogEntry { Type = CatalogType.Department, Name = "Ban Giám đốc" });
        await db.SaveChangesAsync();

        await OrgUnits.EnsureAsync(db);
        var units = await db.OrgUnits.ToListAsync();
        var root = Assert.Single(units, u => u.ParentId == null);
        Assert.Equal(OrgUnitType.Company, root.Type);
        Assert.Equal(4, units.Count);
        Assert.Equal(OrgUnitType.Division, units.Single(u => u.Name == "Ban Giám đốc").Type);
        Assert.Equal(db.Users.Single(u => u.Username == "tbp").Id, units.Single(u => u.Name == "Phòng Kế toán").ManagerId);
        Assert.Equal(units.Count, units.Select(u => u.Code).Distinct().Count());
        // danh mục Phòng ban đồng bộ theo cây
        Assert.True(await db.CatalogEntries.CountAsync(e => e.Type == CatalogType.Department) >= 4);

        await OrgUnits.EnsureAsync(db);
        Assert.Equal(4, await db.OrgUnits.CountAsync());
    }

    [Fact]
    public async Task SubtreeNames_includes_descendants_or_falls_back()
    {
        using var db = TestDb.Create();
        db.OrgUnits.AddRange(Tree());
        await db.SaveChangesAsync();
        var names = await OrgUnits.SubtreeNamesAsync(db, "Khối Kinh doanh");
        Assert.Equal(new HashSet<string> { "Khối Kinh doanh", "Phòng Kinh doanh 1", "Tổ bán lẻ" }, names.ToHashSet());
        Assert.Equal(new[] { "Phòng X" }, await OrgUnits.SubtreeNamesAsync(db, "Phòng X"));
    }
}
