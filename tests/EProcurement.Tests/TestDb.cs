using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Tests;

/// <summary>Database InMemory riêng cho từng test + dữ liệu nền tối thiểu</summary>
public static class TestDb
{
    public static EProcDbContext Create()
    {
        var opts = new DbContextOptionsBuilder<EProcDbContext>().UseInMemoryDatabase("t" + Guid.NewGuid().ToString("N")).Options;
        return new EProcDbContext(opts);
    }

    public static IOptions<ProcurementSettings> Settings(Action<ProcurementSettings>? change = null)
    {
        var s = new ProcurementSettings();
        change?.Invoke(s);
        return Options.Create(s);
    }

    public static (AppUser User, Vendor Vendor, Category It, Category Vpp, Item Laptop, Item Paper) Seed(EProcDbContext db)
    {
        var u = new AppUser { Username = "buyer", FullName = "Mua hàng", Role = AppRoles.Buyer, Department = "Phòng Mua hàng", PasswordHash = "x" };
        var v = new Vendor { Code = "NCC001", Name = "NCC thử", TaxCode = "0100000001", Status = VendorStatus.Approved, PaymentTerms = "Thanh toán 45 ngày sau khi nhận hàng" };
        var it = new Category { Code = "IT", Name = "Thiết bị CNTT" };
        var vpp = new Category { Code = "VPP", Name = "Văn phòng phẩm" };
        db.AddRange(u, v, it, vpp);
        db.SaveChanges();
        var laptop = new Item { Code = "LAP", Name = "Laptop", CategoryId = it.Id, Unit = "Cái", ReferencePrice = 20_000_000 };
        var paper = new Item { Code = "A4", Name = "Giấy A4", CategoryId = vpp.Id, Unit = "Ram", ReferencePrice = 80_000 };
        db.AddRange(laptop, paper);
        db.SaveChanges();
        return (u, v, it, vpp, laptop, paper);
    }
}
