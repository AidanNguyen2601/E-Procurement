using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

public class BoqActualsTests
{
    private static (Contract, List<TenderLine>) Setup()
    {
        var tl = new List<TenderLine>
        {
            new() { Id = 1, BoqCode = "1.1", BoqPath = "1 Móng", Description = "Bê tông", Unit = "m3", Quantity = 10, EstimatedPrice = 1_000_000, SortOrder = 1 },
            new() { Id = 2, BoqCode = "1.2", BoqPath = "1 Móng", Description = "Thép", Unit = "tấn", Quantity = 2, EstimatedPrice = 15_000_000, SortOrder = 2 },
            new() { Id = 3, BoqCode = "2", Description = "Sơn", Unit = "m2", Quantity = 100, EstimatedPrice = 50_000, SortOrder = 3 }
        };
        var c = new Contract { Id = 5 };
        // hợp đồng chào rẻ hơn dự toán 10%; thép tăng 2 → 3 tấn bằng phụ lục; có dòng phát sinh
        c.Lines.Add(new ContractLine { Id = 11, BoqCode = "1.1", BoqPath = "1 Móng", Description = "Bê tông", Unit = "m3", Quantity = 10, UnitPrice = 900_000, SortOrder = 1 });
        c.Lines.Add(new ContractLine { Id = 12, BoqCode = "1.2", BoqPath = "1 Móng", Description = "Thép", Unit = "tấn", Quantity = 3, OriginalQuantity = 2, UnitPrice = 13_500_000, SortOrder = 2 });
        c.Lines.Add(new ContractLine { Id = 13, BoqCode = "1.3", BoqPath = "1 Móng", Description = "Cọc tre", Unit = "m", Quantity = 50, OriginalQuantity = 0, UnitPrice = 80_000, AddedByAmendment = "PL01", SortOrder = 3 });
        c.Lines.Add(new ContractLine { Id = 14, BoqCode = "2", Description = "Sơn", Unit = "m2", Quantity = 100, UnitPrice = 55_000, SortOrder = 4 });
        return (c, tl);
    }

    [Fact]
    public void Totals_compare_estimate_contract_and_actual()
    {
        var (c, tl) = Setup();
        var cum = new Dictionary<int, decimal> { [11] = 10, [12] = 1.5m, [14] = 40 };
        var r = BoqActuals.Build(c, tl, cum, Array.Empty<Payment>());
        Assert.Equal(10 * 1_000_000m + 2 * 15_000_000m + 100 * 50_000m, r.EstTotal);                          // 45.000.000
        Assert.Equal(10 * 900_000m + 3 * 13_500_000m + 50 * 80_000m + 100 * 55_000m, r.ContractCurrent);     // 9.000.000 + 40.500.000 + 4.000.000 + 5.500.000
        Assert.Equal(10 * 900_000m + 2 * 13_500_000m + 0 + 100 * 55_000m, r.ContractOriginal);               // khối lượng ban đầu, không có dòng phát sinh
        Assert.Equal(4_000_000m, r.AddedTotal);
        Assert.Equal(10 * 900_000m + 1.5m * 13_500_000m + 40 * 55_000m, r.ActualTotal);
        // dự toán của phần đã làm: 10 m3 × 1tr + 1,5 tấn × 15tr + 40 m2 × 50k
        Assert.Equal(10_000_000m + 22_500_000m + 2_000_000m, r.EstForActualTotal);
    }

    [Fact]
    public void Rows_are_grouped_flag_added_lines_and_have_no_estimate_for_them()
    {
        var (c, tl) = Setup();
        var r = BoqActuals.Build(c, tl, new Dictionary<int, decimal>(), Array.Empty<Payment>());
        Assert.Equal(new[] { "1", "1.1", "1.2", "1.3", "2" }, r.Rows.Select(x => x.Code));
        var added = r.Rows.Single(x => x.Code == "1.3");
        Assert.True(added.Added); Assert.Equal(0m, added.EstAmount); Assert.Equal(4_000_000m, added.ContractAmount);
        var group = r.Rows[0];
        Assert.True(group.IsGroup);
        Assert.Equal(25_000_000m - 5_000_000m + 25_000_000m - 5_000_000m + 0, 40_000_000m);   // sanity cho dự toán: 10tr + 30tr
        Assert.Equal(10_000_000m + 30_000_000m, group.EstAmount);
        Assert.Equal(9_000_000m + 40_500_000m + 4_000_000m, group.ContractAmount);
        var concrete = r.Rows.Single(x => x.Code == "1.1");
        Assert.Equal(-1_000_000m, concrete.ContractVsEstimate); Assert.Equal(-10m, concrete.ContractVsEstimatePct);
    }

    [Fact]
    public void Goods_contract_lines_match_tender_lines_by_item_or_description_without_boq()
    {
        var tl = new List<TenderLine>
        {
            new() { Id = 1, ItemId = 7, Description = "Laptop văn phòng", Unit = "Cái", Quantity = 10, EstimatedPrice = 20_000_000, SortOrder = 1 },
            new() { Id = 2, Description = "Phụ kiện", Unit = "Bộ", Quantity = 5, EstimatedPrice = 1_000_000, SortOrder = 2 }
        };
        var c = new Contract { Id = 1 };
        c.Lines.Add(new ContractLine { Id = 21, ItemId = 7, Description = "Laptop văn phòng 14 inch", Unit = "Cái", Quantity = 10, UnitPrice = 18_500_000, SortOrder = 5 });   // khớp theo ItemId dù khác mô tả / thứ tự
        c.Lines.Add(new ContractLine { Id = 22, Description = "Phụ kiện", Unit = "Bộ", Quantity = 5, UnitPrice = 950_000, SortOrder = 6 });                                  // khớp theo mô tả
        var r = BoqActuals.Build(c, tl, new Dictionary<int, decimal> { [21] = 4, [22] = 5 }, Array.Empty<Payment>());
        Assert.All(r.Rows, x => Assert.False(x.Added));
        Assert.Equal(10 * 20_000_000m + 5 * 1_000_000m, r.EstTotal);
        Assert.Equal(10 * 18_500_000m + 5 * 950_000m, r.ContractCurrent);
        Assert.Equal(4 * 18_500_000m + 5 * 950_000m, r.ActualTotal);
        Assert.Equal(4 * 20_000_000m + 5 * 1_000_000m, r.EstForActualTotal);
        Assert.True(r.ActualTotal < r.EstForActualTotal);              // thực tế rẻ hơn dự toán
    }

    [Fact]
    public void Contract_line_without_any_estimate_is_flagged_as_added_when_tender_has_lines()
    {
        var tl = new List<TenderLine> { new() { Id = 1, Description = "A", Quantity = 1, EstimatedPrice = 100 } };
        var c = new Contract { Id = 1 };
        c.Lines.Add(new ContractLine { Id = 1, Description = "A", Quantity = 1, UnitPrice = 90 });
        c.Lines.Add(new ContractLine { Id = 2, Description = "Hạng mục lạ", Quantity = 1, UnitPrice = 50 });
        var r = BoqActuals.Build(c, tl, new Dictionary<int, decimal>(), Array.Empty<Payment>());
        Assert.False(r.Rows[0].Added); Assert.True(r.Rows[1].Added); Assert.Equal(50m, r.AddedTotal);
    }

    [Fact]
    public void EstimateOnly_sums_lines_and_groups_by_boq_path()
    {
        var tl = new List<TenderLine>
        {
            new() { Id = 1, BoqCode = "1.1", BoqPath = "1 Móng", Description = "Bê tông", Unit = "m3", Quantity = 2, EstimatedPrice = 1000, SortOrder = 1 },
            new() { Id = 2, BoqCode = "2", Description = "Sơn", Unit = "m2", Quantity = 3, EstimatedPrice = 100, SortOrder = 2 }
        };
        var r = BoqActuals.EstimateOnly(tl);
        Assert.Equal(2_300m, r.EstTotal); Assert.Equal(0m, r.ContractCurrent);
        Assert.Equal(new[] { "1", "1.1", "2" }, r.Rows.Select(x => x.Code));
        Assert.Equal(2_000m, r.Rows[0].EstAmount);
    }

    [Fact]
    public void Payments_split_paid_requested_and_exclude_rejected()
    {
        var (c, tl) = Setup();
        var pays = new[]
        {
            new Payment { Type = PaymentType.Advance, Status = PaymentStatus.Paid, Amount = 100, NetAmount = 100 },
            new Payment { Type = PaymentType.Acceptance, Status = PaymentStatus.Paid, Amount = 500, AdvanceDeduction = 150, NetAmount = 350 },
            new Payment { Type = PaymentType.Acceptance, Status = PaymentStatus.Requested, Amount = 300, NetAmount = 300 },
            new Payment { Type = PaymentType.Invoice, Status = PaymentStatus.Rejected, Amount = 999, NetAmount = 999 }
        };
        var r = BoqActuals.Build(c, tl, new Dictionary<int, decimal>(), pays);
        Assert.Equal(500m, r.PaidGross); Assert.Equal(450m, r.PaidNet); Assert.Equal(300m, r.RequestedGross);
    }

    [Fact]
    public void Contract_without_estimate_still_builds()
    {
        var c = new Contract { Id = 1 };
        c.Lines.Add(new ContractLine { Id = 1, Description = "Dịch vụ", Quantity = 12, UnitPrice = 1_000_000, SortOrder = 1 });
        var r = BoqActuals.Build(c, new List<TenderLine>(), new Dictionary<int, decimal> { [1] = 3 }, Array.Empty<Payment>());
        Assert.Equal(0m, r.EstTotal); Assert.Equal(12_000_000m, r.ContractCurrent); Assert.Equal(3_000_000m, r.ActualTotal);
        Assert.False(r.Rows[0].Added);
    }

    [Fact]
    public async Task Sample_construction_contract_actuals_are_plausible()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        db.Users.AddRange(
            new AppUser { Username = "admin", FullName = "QT", Role = AppRoles.Admin, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "director", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" },
            new AppUser { Username = "requester", FullName = "NV", Role = AppRoles.Requester, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "ketoan.tt", FullName = "KT", Role = AppRoles.Accountant, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "approver", FullName = "A", Role = AppRoles.Approver, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "approver2", FullName = "B", Role = AppRoles.Approver, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "buyer2", FullName = "C", Role = AppRoles.Buyer, Department = "MH", PasswordHash = "x" });
        db.Vendors.AddRange(new Vendor { Code = "N2", Name = "N2", TaxCode = "2", Status = VendorStatus.Approved }, new Vendor { Code = "N3", Name = "N3", TaxCode = "3", Status = VendorStatus.Approved });
        db.SaveChanges();
        await Web.Data.SampleConstruction.LoadAsync(db);
        var c = await db.Contracts.Include(x => x.Lines).SingleAsync();
        var t = await db.Tenders.Include(x => x.Lines).SingleAsync();
        var cum = BoqAcceptances.Cumulative(await db.BoqAcceptances.Include(a => a.Lines).ToListAsync());
        var r = BoqActuals.Build(c, t.Lines.ToList(), cum, await db.Payments.ToListAsync());
        Assert.True(r.EstTotal > 0); Assert.True(r.ContractCurrent > 0); Assert.True(r.ActualTotal > 0 && r.ActualTotal < r.ContractCurrent);
        Assert.True(r.AddedTotal > 0);                                 // dòng cọc tre phát sinh
        Assert.True(r.PaidNet > 0 && r.RequestedGross > 0);
    }
}
