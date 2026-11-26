using System.Security.Claims;
using EProcurement.Web.Controllers;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

/// <summary>Lịch sử giá: vật tư / thi công – dịch vụ, thuế suất, tiền tệ, gói thầu / hợp đồng liên quan, vùng</summary>
public class PriceRecordTests
{
    [Fact]
    public void Form_computes_pre_tax_and_tax_inclusive_prices_and_maps_to_entity()
    {
        var f = new PriceRecordForm { ItemId = 3, VendorId = 7, Source = PriceSource.Awarded, Currency = "usd", Unit = " Gói ", SubCode = "A-01", Region = "Miền Nam",
            MaterialPrice = 1_000, MaterialVatPercent = 10, ServicePrice = 853_059_300, ServiceVatPercent = 8, PriceDate = new DateTime(2026, 11, 23), TenderId = 4, ContractId = 5, Note = " ghi chú " };
        Assert.Equal(853_060_300m, f.UnitPrice);
        Assert.Equal(1_100m + 921_304_044m, f.PriceWithTax);
        var r = new PriceRecord(); f.ApplyTo(r);
        Assert.Equal(853_060_300m, r.UnitPrice); Assert.Equal("USD", r.Currency); Assert.Equal("Gói", r.Unit); Assert.Equal("ghi chú", r.Note);
        Assert.Equal(4, r.TenderId); Assert.Equal(5, r.ContractId); Assert.False(r.IsVnd);
        Assert.Null(r.SupplierName);                                   // có NCC thì không lưu tên đơn vị ngoài danh sách
        var back = PriceRecordForm.From(r);
        Assert.Equal(f.ServicePrice, back.ServicePrice); Assert.Equal("USD", back.Currency); Assert.Equal(PriceSource.Awarded, back.Source);
    }

    [Fact]
    public void Legacy_single_price_record_opens_as_material_price()
    {
        var r = new PriceRecord { UnitPrice = 120_000, Currency = null };
        var f = PriceRecordForm.From(r);
        Assert.Equal(120_000m, f.MaterialPrice); Assert.Equal(0m, f.ServicePrice); Assert.Equal("VND", f.Currency);
        Assert.True(r.IsVnd);
    }

    [Fact]
    public async Task Observations_include_vnd_records_with_combined_price_but_skip_foreign_currency()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, laptop, _) = TestDb.Seed(db);
        db.PriceRecords.AddRange(
            new PriceRecord { ItemId = laptop.Id, VendorId = vendor.Id, Source = PriceSource.Survey, Currency = "VND", MaterialPrice = 18_000_000, ServicePrice = 500_000, UnitPrice = 18_500_000, PriceDate = DateTime.Today.AddDays(-3), CreatedById = buyer.Id, Region = "Hà Nội" },
            new PriceRecord { ItemId = laptop.Id, VendorId = vendor.Id, Source = PriceSource.Survey, Currency = "USD", MaterialPrice = 700, UnitPrice = 700, PriceDate = DateTime.Today.AddDays(-2), CreatedById = buyer.Id });
        db.SaveChanges();
        var obs = await PriceBank.ObservationsAsync(db, new[] { laptop.Id }, DateTime.Today.AddMonths(-1), DateTime.Today);
        var o = Assert.Single(obs);
        Assert.Equal(18_500_000m, o.UnitPrice);
        Assert.Contains("Hà Nội", o.DocNumber);
        Assert.StartsWith("/PriceBank/Record/", o.Url);
    }

    private class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static PriceBankController As(Web.Data.EProcDbContext db, int userId)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, AppRoles.Buyer) }, "t")) };
        return new PriceBankController(db, TestDb.Settings()) { ControllerContext = new ControllerContext { HttpContext = http }, TempData = new TempDataDictionary(http, new NoTempData()) };
    }

    [Fact]
    public async Task Save_validates_required_fields_then_creates_and_updates()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, laptop, _) = TestDb.Seed(db);
        db.CatalogEntries.Add(new CatalogEntry { Type = CatalogType.Currency, Code = "USD", Name = "Đô la Mỹ", IsActive = true });
        db.SaveChanges();
        var c = As(db, buyer.Id);

        // thiếu NCC, hàng hóa, đơn giá; mốc thời gian tương lai; tiền tệ ngoài danh mục
        var bad = await c.SaveRecord(new PriceRecordForm { ItemId = 0, Currency = "XYZ", PriceDate = DateTime.Today.AddDays(1) });
        Assert.IsType<ViewResult>(bad);
        foreach (var k in new[] { "ItemId", "VendorId", "MaterialPrice", "PriceDate", "Currency" }) Assert.True(c.ModelState.ContainsKey(k), k);
        Assert.Equal(0, await db.PriceRecords.CountAsync());

        c = As(db, buyer.Id);
        var ok = await c.SaveRecord(new PriceRecordForm { ItemId = laptop.Id, VendorId = vendor.Id, Source = PriceSource.Contract, Currency = "USD", ServicePrice = 1250.5m, ServiceVatPercent = 10, PriceDate = DateTime.Today, Region = "Đà Nẵng" });
        Assert.IsType<RedirectToActionResult>(ok);
        var r = await db.PriceRecords.SingleAsync();
        Assert.Equal(1250.5m, r.UnitPrice); Assert.Equal("USD", r.Currency); Assert.Equal(buyer.Id, r.CreatedById); Assert.Null(r.UpdatedAt);

        c = As(db, buyer.Id);
        await c.SaveRecord(new PriceRecordForm { Id = r.Id, ItemId = laptop.Id, VendorId = vendor.Id, Source = PriceSource.Contract, Currency = "VND", MaterialPrice = 30_000_000, PriceDate = DateTime.Today });
        r = await db.PriceRecords.SingleAsync();
        Assert.Equal(30_000_000m, r.UnitPrice); Assert.Equal("VND", r.Currency); Assert.NotNull(r.UpdatedAt);
    }
}
