using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

/// <summary>Danh mục dùng chung: nạp mặc định (kể cả dạng "MÃ|Tên"), không nạp trùng khi chạy lại</summary>
public class CatalogTests
{
    [Fact]
    public async Task Ensure_SeedsNewCatalogs_WithCodes_AndIsIdempotent()
    {
        var db = TestDb.Create();
        TestDb.Seed(db);
        await Catalogs.EnsureAsync(db);
        var vnd = db.CatalogEntries.Single(e => e.Type == CatalogType.Currency && e.Name == "Việt Nam đồng");
        Assert.Equal("VND", vnd.Code);
        Assert.True(db.CatalogEntries.Count(e => e.Type == CatalogType.FundingSource) >= 5);
        Assert.True(db.CatalogEntries.Any(e => e.Type == CatalogType.OrderType && e.Name == "Mua dịch vụ"));
        Assert.True(db.CatalogEntries.Any(e => e.Type == CatalogType.ImplementationTeam));
        Assert.False(db.CatalogEntries.Any(e => e.Type == CatalogType.Decision)); // quyết định do người dùng nhập
        var before = db.CatalogEntries.Count();
        await Catalogs.EnsureAsync(db);
        Assert.Equal(before, db.CatalogEntries.Count());
    }

    [Fact]
    public void EveryCatalogType_HasInfo() =>
        Assert.All(Enum.GetValues<CatalogType>(), t => Assert.NotNull(Catalogs.Info(t)));
}

/// <summary>Thang xếp loại NCC lấy từ danh mục "Xếp loại nhà cung cấp"</summary>
public class SupplierGradeCatalogTests
{
    [Fact]
    public async Task Levels_DefaultSeed_MatchesBuiltInScale()
    {
        var db = TestDb.Create();
        TestDb.Seed(db);
        await Catalogs.EnsureAsync(db);
        var levels = await SupplierScorecard.LevelsAsync(db);
        Assert.Equal(new[] { "A", "B", "C", "D" }, levels.Select(l => l.Code));
        Assert.Equal(4.5m, levels[0].Min);
        Assert.Equal("B", SupplierScorecard.Grade(3.6m, levels));
    }

    [Fact]
    public async Task Levels_CustomCatalog_ChangesGrading()
    {
        var db = TestDb.Create();
        db.CatalogEntries.AddRange(
            new CatalogEntry { Type = CatalogType.SupplierGrade, Code = "Xuất sắc", Name = "Đối tác chiến lược", Note = "4,8" },
            new CatalogEntry { Type = CatalogType.SupplierGrade, Code = "Đạt", Name = "Duy trì", Note = "3" },
            new CatalogEntry { Type = CatalogType.SupplierGrade, Code = "Loại", Name = "Ngừng hợp tác", Note = "0" },
            new CatalogEntry { Type = CatalogType.SupplierGrade, Code = null, Name = "Thiếu mã – bỏ qua", Note = "2" });
        db.SaveChanges();
        var levels = await SupplierScorecard.LevelsAsync(db);
        Assert.Equal(3, levels.Count);
        Assert.Equal("Xuất sắc", SupplierScorecard.Grade(4.8m, levels));
        Assert.Equal("Đạt", SupplierScorecard.Grade(4.7m, levels));
        Assert.Equal("Loại", SupplierScorecard.Grade(1m, levels));
        Assert.Equal("Duy trì", SupplierScorecard.GradeText("Đạt", levels));
        Assert.Equal("—", SupplierScorecard.Grade(null, levels));
    }

    [Fact]
    public async Task Levels_EmptyOrInvalid_FallsBackToDefault()
    {
        var db = TestDb.Create();
        db.CatalogEntries.Add(new CatalogEntry { Type = CatalogType.SupplierGrade, Code = "X", Name = "x", Note = "không phải số" });
        db.SaveChanges();
        Assert.Equal(SupplierScorecard.DefaultLevels.Select(l => l.Code), (await SupplierScorecard.LevelsAsync(db)).Select(l => l.Code));
    }
}
