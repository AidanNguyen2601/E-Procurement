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
