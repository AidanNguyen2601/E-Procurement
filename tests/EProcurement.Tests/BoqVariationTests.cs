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

public class BoqVariationTests
{
    private static List<ContractLine> Lines() => new()
    {
        new() { Id = 1, Description = "Bê tông", Unit = "m3", Quantity = 10, UnitPrice = 1_000_000, BoqCode = "1.1.1", BoqPath = "1 Móng › 1.1 Bê tông", SortOrder = 1 },
        new() { Id = 2, Description = "Thép", Unit = "tấn", Quantity = 2, UnitPrice = 15_000_000, BoqCode = "1.1.2", BoqPath = "1 Móng › 1.1 Bê tông", SortOrder = 2 },
        new() { Id = 3, Description = "Sơn", Unit = "m2", Quantity = 100, UnitPrice = 50_000, BoqCode = "2.1", BoqPath = "2 Hoàn thiện", SortOrder = 3 }
    };

    [Fact]
    public void NextCode_continues_numbering_under_a_group_or_at_root()
    {
        var codes = new[] { "1.1.1", "1.1.2", "1.1.10", "2.1" };
        Assert.Equal("1.1.11", BoqVariations.NextCode(codes, "1 Móng › 1.1 Bê tông"));
        Assert.Equal("2.2", BoqVariations.NextCode(codes, "2 Hoàn thiện"));
        Assert.Equal("3", BoqVariations.NextCode(codes, null));
        Assert.Equal("3.1", BoqVariations.NextCode(new[] { "3" }, "3 Mới"));
    }

    [Fact]
    public void Plan_computes_value_change_for_adjusted_and_added_lines()
    {
        var plan = BoqVariations.Plan(Lines(), new Dictionary<int, decimal> { [1] = 4 },
            new Dictionary<int, decimal> { [1] = 12, [2] = 2, [3] = 80 },                                        // thép không đổi → bỏ qua
            new[] { new VariationAddition("Đào bổ sung", "m3", 5, 200_000, "1 Móng › 1.1 Bê tông"), new VariationAddition("", null, 0, 0, null) });
        Assert.Empty(plan.Errors);
        Assert.Equal(3, plan.Lines.Count);
        Assert.Equal(2 * 1_000_000m - 20 * 50_000m + 5 * 200_000m, plan.ValueChange);   // +2.000.000 − 1.000.000 + 1.000.000
        var added = plan.Lines.Single(l => l.ContractLineId == null);
        Assert.Equal("1.1.3", added.BoqCode); Assert.Equal(0m, added.OldQuantity);
    }

    [Fact]
    public void Plan_rejects_quantity_below_accepted_negative_and_bad_additions()
    {
        var cum = new Dictionary<int, decimal> { [1] = 6 };
        Assert.NotEmpty(BoqVariations.Plan(Lines(), cum, new Dictionary<int, decimal> { [1] = 5.999m }, Array.Empty<VariationAddition>()).Errors);
        Assert.Empty(BoqVariations.Plan(Lines(), cum, new Dictionary<int, decimal> { [1] = 6 }, Array.Empty<VariationAddition>()).Errors);   // đúng bằng đã nghiệm thu: được
        Assert.NotEmpty(BoqVariations.Plan(Lines(), cum, new Dictionary<int, decimal> { [1] = -1 }, Array.Empty<VariationAddition>()).Errors);
        Assert.NotEmpty(BoqVariations.Plan(Lines(), cum, new Dictionary<int, decimal>(), new[] { new VariationAddition("X", null, 0, 100, null) }).Errors);
        Assert.NotEmpty(BoqVariations.Plan(Lines(), cum, new Dictionary<int, decimal>(), Array.Empty<VariationAddition>()).Errors);       // không có thay đổi nào
        Assert.NotEmpty(BoqVariations.Plan(Lines(), cum, new Dictionary<int, decimal> { [99] = 1 }, Array.Empty<VariationAddition>()).Errors);
    }

    [Fact]
    public void Apply_keeps_original_quantity_and_inserts_new_line_after_its_group()
    {
        var c = new Contract { Id = 7, Lines = Lines() };
        var a = new ContractAmendment { Number = "PL01" };
        a.Lines.Add(new ContractAmendmentLine { ContractLineId = 1, OldQuantity = 10, NewQuantity = 14 });
        a.Lines.Add(new ContractAmendmentLine { ContractLineId = null, Description = "Đào bổ sung", Unit = "m3", NewQuantity = 5, UnitPrice = 200_000, BoqCode = "1.1.3", BoqPath = "1 Móng › 1.1 Bê tông" });
        Assert.Empty(BoqVariations.Apply(c, a, new Dictionary<int, decimal>()));
        var ordered = c.Lines.OrderBy(l => l.SortOrder).ToList();
        Assert.Equal(new[] { "1.1.1", "1.1.2", "1.1.3", "2.1" }, ordered.Select(l => l.BoqCode));   // dòng mới nằm trong nhóm Móng, trước Hoàn thiện
        Assert.Equal(new[] { 1, 2, 3, 4 }, ordered.Select(l => l.SortOrder));
        Assert.Equal(14m, ordered[0].Quantity); Assert.Equal(10m, ordered[0].OriginalQuantity);
        Assert.Equal("PL01", ordered[2].AddedByAmendment); Assert.Equal(0m, ordered[2].OriginalQuantity);
        // phụ lục thứ hai trên cùng dòng không ghi đè khối lượng gốc
        var b = new ContractAmendment { Number = "PL02" }; b.Lines.Add(new ContractAmendmentLine { ContractLineId = 1, OldQuantity = 14, NewQuantity = 9 });
        BoqVariations.Apply(c, b, new Dictionary<int, decimal>());
        Assert.Equal(10m, c.Lines.First(l => l.Id == 1).OriginalQuantity); Assert.Equal(9m, c.Lines.First(l => l.Id == 1).Quantity);
    }

    [Fact]
    public void Apply_refuses_when_accepted_quantity_grew_after_the_amendment_was_filed()
    {
        var c = new Contract { Id = 7, Lines = Lines() };
        var a = new ContractAmendment { Number = "PL01" };
        a.Lines.Add(new ContractAmendmentLine { ContractLineId = 1, OldQuantity = 10, NewQuantity = 5 });
        var errors = BoqVariations.Apply(c, a, new Dictionary<int, decimal> { [1] = 7 });
        Assert.NotEmpty(errors);
        Assert.Equal(10m, c.Lines.First(l => l.Id == 1).Quantity);   // chưa đổi gì
    }

    // ----- luồng qua controller -----
    private class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static ContractsController As(EProcDbContext db, int userId, string role)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, role) }, "t")) };
        return new ContractsController(db, TestDb.Settings()) { ControllerContext = new ControllerContext { HttpContext = http }, TempData = new TempDataDictionary(http, new NoTempData()) };
    }

    [Fact]
    public async Task Flow_file_approve_then_acceptance_limits_follow_the_new_quantity()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        var director = new AppUser { Username = "dir", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" };
        db.Users.Add(director); db.SaveChanges();
        var c = new Contract { Number = "HD-1", Title = "Xây lắp", VendorId = vendor.Id, Value = 10_000_000, VatPercent = 10, CreatedById = buyer.Id, Status = ContractStatus.Active };
        c.Lines.Add(new ContractLine { Description = "Bê tông", Unit = "m3", Quantity = 10, UnitPrice = 1_000_000, BoqCode = "1.1", BoqPath = "1 Móng", SortOrder = 1 });
        db.Contracts.Add(c); db.SaveChanges();
        var lineId = c.Lines.Single().Id;

        // lập phụ lục: tăng 10 → 15 m3 và thêm một dòng phát sinh
        var ctl = As(db, buyer.Id, AppRoles.Buyer);
        await ctl.AddBoqAmendment(c.Id, "Tăng khối lượng", new Dictionary<int, decimal?> { [lineId] = 15 },
            new List<string?> { "Ép cọc bổ sung" }, new List<string?> { "m" }, new List<decimal?> { 20 }, new List<decimal?> { 300_000 }, new List<string?> { "1 Móng" });
        var am = await db.ContractAmendments.Include(a => a.Lines).SingleAsync();
        Assert.Equal(AmendmentStatus.Pending, am.Status); Assert.Equal(2, am.Lines.Count);
        Assert.Equal(5 * 1_000_000m + 20 * 300_000m, am.ValueChange);                 // 11.000.000
        Assert.Equal(10m, (await db.ContractLines.FindAsync(lineId))!.Quantity);       // chưa đổi trước khi duyệt

        // đang chờ duyệt thì không lập thêm
        await ctl.AddBoqAmendment(c.Id, null, new Dictionary<int, decimal?> { [lineId] = 16 }, null, null, null, null, null);
        Assert.Equal(1, await db.ContractAmendments.CountAsync());

        // Giám đốc duyệt → áp dụng
        await As(db, director.Id, AppRoles.Director).DecideAmendment(c.Id, am.Id, true, "ok");
        var fresh = await db.Contracts.Include(x => x.Lines).SingleAsync();
        Assert.Equal(21_000_000m, fresh.Value);
        Assert.Equal(15m, fresh.Lines.First(l => l.Id == lineId).Quantity); Assert.Equal(10m, fresh.Lines.First(l => l.Id == lineId).OriginalQuantity);
        var added = fresh.Lines.Single(l => l.AddedByAmendment != null);
        Assert.Equal("1.1", fresh.Lines.First(l => l.Id == lineId).BoqCode); Assert.Equal("1.2", added.BoqCode); Assert.Equal(20m, added.Quantity);

        // nghiệm thu giờ nhập được tới 15 m3 (trước đó chỉ 10)
        var errs = BoqAcceptances.Validate(fresh.Lines.ToList(), new Dictionary<int, decimal> { [lineId] = 15 }, new Dictionary<int, decimal>());
        Assert.Empty(errs);
        Assert.NotEmpty(BoqAcceptances.Validate(fresh.Lines.ToList(), new Dictionary<int, decimal> { [lineId] = 15.001m }, new Dictionary<int, decimal>()));
    }

    [Fact]
    public async Task Non_purchasing_cannot_file_and_rejected_amendment_changes_nothing()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        var c = new Contract { Number = "HD-2", Title = "X", VendorId = vendor.Id, Value = 1_000_000, CreatedById = buyer.Id, Status = ContractStatus.Active };
        c.Lines.Add(new ContractLine { Description = "A", Quantity = 10, UnitPrice = 100_000, BoqCode = "1", SortOrder = 1 });
        db.Contracts.Add(c); db.SaveChanges();
        var lid = c.Lines.Single().Id;
        var ctl = As(db, buyer.Id, AppRoles.Buyer);
        await ctl.AddBoqAmendment(c.Id, null, new Dictionary<int, decimal?> { [lid] = 20 }, null, null, null, null, null);
        var am = await db.ContractAmendments.SingleAsync();
        await As(db, 99, AppRoles.Director).DecideAmendment(c.Id, am.Id, false, "không đồng ý");
        Assert.Equal(AmendmentStatus.Rejected, (await db.ContractAmendments.FindAsync(am.Id))!.Status);
        Assert.Equal(10m, (await db.ContractLines.FindAsync(lid))!.Quantity);
        Assert.Equal(1_000_000m, (await db.Contracts.FindAsync(c.Id))!.Value);
    }
}
