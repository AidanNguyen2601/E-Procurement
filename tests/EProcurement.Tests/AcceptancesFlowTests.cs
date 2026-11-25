using System.Security.Claims;
using EProcurement.Web.Controllers;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

/// <summary>Luồng nghiệm thu khối lượng BOQ: lập → xác nhận (khác người lập) → đề nghị thanh toán (khấu trừ tạm ứng) → chặn trùng / vượt</summary>
public class AcceptancesFlowTests
{
    private class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static AcceptancesController As(EProcDbContext db, int userId, string role)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "t")) };
        return new AcceptancesController(db, TestDb.Settings(), new FinanceService(db))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new NoTempData())
        };
    }

    private static (EProcDbContext Db, Contract C, AppUser Buyer, AppUser Director, AppUser Acc) Setup()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        var director = new AppUser { Username = "dir", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" };
        var acc = new AppUser { Username = "kt", FullName = "KT", Role = AppRoles.Accountant, Department = "KT", PasswordHash = "x" };
        db.Users.AddRange(director, acc); db.SaveChanges();
        var c = new Contract { Number = "HD-1", Title = "Xây lắp", VendorId = vendor.Id, Value = 1_000_000, VatPercent = 10, CreatedById = buyer.Id, Status = ContractStatus.Active };
        c.Lines.Add(new ContractLine { Description = "Bê tông", Unit = "m3", Quantity = 10, UnitPrice = 50_000, BoqCode = "1.1", BoqPath = "1 Móng", SortOrder = 1 });
        c.Lines.Add(new ContractLine { Description = "Thép", Unit = "kg", Quantity = 100, UnitPrice = 5_000, BoqCode = "1.2", BoqPath = "1 Móng", SortOrder = 2 });
        db.Contracts.Add(c); db.SaveChanges();
        // tạm ứng 30% giá trị HĐ (gồm VAT = 1.100.000) đã chi
        db.Payments.Add(new Payment { Number = "TT-0", Type = PaymentType.Advance, VendorId = vendor.Id, ContractId = c.Id, Amount = 330_000, NetAmount = 330_000, Status = PaymentStatus.Paid, RequestedById = buyer.Id });
        db.SaveChanges();
        return (db, c, buyer, director, acc);
    }

    [Fact]
    public async Task Full_flow_create_confirm_pay_with_advance_deduction()
    {
        var (db, c, buyer, director, acc) = Setup();
        var l1 = c.Lines.First(l => l.BoqCode == "1.1").Id; var l2 = c.Lines.First(l => l.BoqCode == "1.2").Id;

        // 1. Lập biên bản đợt 1: 5 m3 bê tông + 40 kg thép = 250.000 + 200.000 = 450.000
        var r = await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, "Đợt 1", new Dictionary<int, decimal> { [l1] = 5, [l2] = 40 });
        var a = await db.BoqAcceptances.Include(x => x.Lines).SingleAsync();
        Assert.IsType<RedirectToActionResult>(r);
        Assert.Equal(AcceptanceStatus.Draft, a.Status); Assert.Equal(450_000m, a.SubTotal); Assert.Equal(1, a.Period); Assert.Equal(2, a.Lines.Count);

        // 2. Người lập không tự xác nhận; Giám đốc xác nhận được
        var self = As(db, buyer.Id, AppRoles.Buyer);
        await self.Confirm(a.Id);
        Assert.Equal(AcceptanceStatus.Draft, (await db.BoqAcceptances.FindAsync(a.Id))!.Status);
        Assert.NotNull(self.TempData["Error"]);
        await As(db, director.Id, AppRoles.Director).Confirm(a.Id);
        Assert.Equal(AcceptanceStatus.Confirmed, (await db.BoqAcceptances.FindAsync(a.Id))!.Status);

        // 3. Kế toán đề nghị thanh toán: 450.000 × 1,1 = 495.000; khấu trừ tạm ứng 330.000/1.100.000 × 495.000 = 148.500
        await As(db, acc.Id, AppRoles.Accountant).RequestPayment(a.Id);
        var pay = await db.Payments.SingleAsync(p => p.AcceptanceId == a.Id);
        Assert.Equal(PaymentType.Acceptance, pay.Type);
        Assert.Equal(495_000m, pay.Amount); Assert.Equal(148_500m, pay.AdvanceDeduction); Assert.Equal(346_500m, pay.NetAmount);

        // 4. Không đề nghị lần hai, không hủy khi đã có đề nghị thanh toán
        await As(db, acc.Id, AppRoles.Accountant).RequestPayment(a.Id);
        Assert.Equal(1, await db.Payments.CountAsync(p => p.AcceptanceId == a.Id));
        var cancel = As(db, director.Id, AppRoles.Director);
        await cancel.Cancel(a.Id, "thử");
        Assert.Equal(AcceptanceStatus.Confirmed, (await db.BoqAcceptances.FindAsync(a.Id))!.Status);
    }

    [Fact]
    public async Task Cumulative_quantity_cannot_exceed_contract_quantity_across_rounds()
    {
        var (db, c, buyer, director, _) = Setup();
        var l1 = c.Lines.First(l => l.BoqCode == "1.1").Id;
        await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, null, new Dictionary<int, decimal> { [l1] = 8 });
        var a1 = await db.BoqAcceptances.SingleAsync();
        await As(db, director.Id, AppRoles.Director).Confirm(a1.Id);

        // đợt 2: 3 m3 → lũy kế 11 > 10 → bị từ chối, không tạo biên bản mới
        var res = await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, null, new Dictionary<int, decimal> { [l1] = 3 });
        Assert.IsType<ViewResult>(res);
        Assert.Equal(1, await db.BoqAcceptances.CountAsync());
        // đúng phần còn lại thì được
        await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, null, new Dictionary<int, decimal> { [l1] = 2 });
        Assert.Equal(2, await db.BoqAcceptances.CountAsync());
        Assert.Equal(2, (await db.BoqAcceptances.OrderBy(x => x.Id).LastAsync()).Period);
    }

    [Fact]
    public async Task Draft_acceptances_do_not_count_and_edit_replaces_lines()
    {
        var (db, c, buyer, _, _) = Setup();
        var l1 = c.Lines.First(l => l.BoqCode == "1.1").Id; var l2 = c.Lines.First(l => l.BoqCode == "1.2").Id;
        await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, null, new Dictionary<int, decimal> { [l1] = 10 });
        // biên bản nháp thứ hai vẫn nhập được đủ 10 vì nháp chưa tính lũy kế
        await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, null, new Dictionary<int, decimal> { [l1] = 10 });
        Assert.Equal(2, await db.BoqAcceptances.CountAsync());
        // sửa biên bản đầu: đổi sang thép
        var first = await db.BoqAcceptances.OrderBy(x => x.Id).FirstAsync();
        await As(db, buyer.Id, AppRoles.Buyer).Save(first.Id, c.Id, DateTime.Today, "sửa", new Dictionary<int, decimal> { [l1] = 0, [l2] = 20 });
        var lines = await db.BoqAcceptanceLines.Where(x => x.AcceptanceId == first.Id).ToListAsync();
        Assert.Single(lines); Assert.Equal(l2, lines[0].ContractLineId); Assert.Equal(20m, lines[0].Quantity);
        Assert.Equal(100_000m, (await db.BoqAcceptances.FindAsync(first.Id))!.SubTotal);
    }

    [Fact]
    public async Task Payment_cannot_exceed_contract_value_including_other_requests()
    {
        var (db, c, buyer, director, acc) = Setup();
        var l1 = c.Lines.First(l => l.BoqCode == "1.1").Id;
        // một đề nghị thanh toán hóa đơn đã chiếm 800.000 / 1.100.000
        db.Payments.Add(new Payment { Number = "TT-9", Type = PaymentType.Invoice, VendorId = c.VendorId, ContractId = c.Id, Amount = 800_000, NetAmount = 800_000, Status = PaymentStatus.Requested, RequestedById = buyer.Id });
        await db.SaveChangesAsync();
        await As(db, buyer.Id, AppRoles.Buyer).Save(0, c.Id, DateTime.Today, null, new Dictionary<int, decimal> { [l1] = 10 }); // 500.000 + VAT = 550.000
        var a = await db.BoqAcceptances.SingleAsync();
        await As(db, director.Id, AppRoles.Director).Confirm(a.Id);
        var ctl = As(db, acc.Id, AppRoles.Accountant);
        await ctl.RequestPayment(a.Id);
        Assert.False(await db.Payments.AnyAsync(p => p.AcceptanceId == a.Id));
        var msg = ctl.TempData["Error"]!.ToString()!;
        Assert.True(msg.Contains("vượt") || msg.Contains("exceed"), msg); // theo ngôn ngữ hiện hành
    }
}
