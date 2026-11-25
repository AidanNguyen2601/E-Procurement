using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Xunit;

namespace EProcurement.Tests;

public class BoqAcceptanceTests
{
    private static List<ContractLine> Lines() => new()
    {
        new() { Id = 1, Description = "Bê tông", Unit = "m3", Quantity = 100, UnitPrice = 1_000_000, BoqCode = "1.1", BoqPath = "1 Móng", SortOrder = 1 },
        new() { Id = 2, Description = "Thép", Unit = "kg", Quantity = 500.5m, UnitPrice = 20_000, BoqCode = "1.2", BoqPath = "1 Móng", SortOrder = 2 },
        new() { Id = 3, Description = "Sơn", Unit = "m2", Quantity = 50, UnitPrice = 100_000, BoqCode = "2", BoqPath = null, SortOrder = 3 }
    };

    private static BoqAcceptance Acc(int id, AcceptanceStatus st, params (int line, decimal q)[] l) =>
        new() { Id = id, Status = st, Lines = l.Select(x => new BoqAcceptanceLine { ContractLineId = x.line, Quantity = x.q }).ToList() };

    [Fact]
    public void Cumulative_counts_only_confirmed_and_can_exclude_one()
    {
        var accs = new[] { Acc(1, AcceptanceStatus.Confirmed, (1, 30), (2, 100)), Acc(2, AcceptanceStatus.Confirmed, (1, 20)), Acc(3, AcceptanceStatus.Draft, (1, 99)), Acc(4, AcceptanceStatus.Cancelled, (1, 99)) };
        var cum = BoqAcceptances.Cumulative(accs);
        Assert.Equal(50m, cum[1]); Assert.Equal(100m, cum[2]); Assert.False(cum.ContainsKey(3));
        Assert.Equal(30m, BoqAcceptances.Cumulative(accs, excludeId: 2)[1]);
    }

    [Fact]
    public void Validate_blocks_overrun_negative_and_empty()
    {
        var lines = Lines();
        var cum = new Dictionary<int, decimal> { [1] = 80 };
        Assert.Empty(BoqAcceptances.Validate(lines, new Dictionary<int, decimal> { [1] = 20 }, cum));          // đúng bằng phần còn lại
        Assert.Single(BoqAcceptances.Validate(lines, new Dictionary<int, decimal> { [1] = 20.001m }, cum));    // vượt 0,001
        Assert.NotEmpty(BoqAcceptances.Validate(lines, new Dictionary<int, decimal> { [1] = -1 }, cum));
        Assert.NotEmpty(BoqAcceptances.Validate(lines, new Dictionary<int, decimal>(), cum));                  // chưa nhập gì
        Assert.NotEmpty(BoqAcceptances.Validate(lines, new Dictionary<int, decimal> { [99] = 1 }, cum));       // dòng không thuộc hợp đồng
    }

    [Fact]
    public void SubTotal_and_gross_use_contract_prices()
    {
        var sub = BoqAcceptances.SubTotal(Lines(), new Dictionary<int, decimal> { [1] = 10, [2] = 100.5m });
        Assert.Equal(10 * 1_000_000m + 100.5m * 20_000m, sub);
        Assert.Equal(Math.Round(sub * 1.1m), BoqAcceptances.Gross(sub, 10));
    }

    [Fact]
    public void AdvanceDeduction_is_proportional_and_capped_by_outstanding()
    {
        // tạm ứng 30% giá trị HĐ 1.000: đợt thanh toán 200 → thu hồi 60
        Assert.Equal(60m, BoqAcceptances.AdvanceDeduction(200, 1000, 300, 0));
        // chỉ còn phải thu hồi 40
        Assert.Equal(40m, BoqAcceptances.AdvanceDeduction(200, 1000, 300, 260));
        Assert.Equal(0m, BoqAcceptances.AdvanceDeduction(200, 1000, 300, 300));
        Assert.Equal(0m, BoqAcceptances.AdvanceDeduction(200, 1000, 0, 0));
        Assert.Equal(0m, BoqAcceptances.AdvanceDeduction(200, 0, 300, 0));
    }

    [Fact]
    public void Progress_rolls_up_groups_and_percent()
    {
        var rows = BoqAcceptances.Progress(Lines(), new Dictionary<int, decimal> { [1] = 50, [2] = 500.5m });
        Assert.Equal(new[] { "1", "1.1", "1.2", "2" }, rows.Select(r => r.Code));
        Assert.Equal(new[] { true, false, false, false }, rows.Select(r => r.IsGroup));
        Assert.Equal(100m * 1_000_000 + 500.5m * 20_000, rows[0].ContractAmount);
        Assert.Equal(50m * 1_000_000 + 500.5m * 20_000, rows[0].AcceptedAmount);
        Assert.Equal(50m, rows[1].Percent);
        Assert.Equal(100m, rows[2].Percent);
        Assert.Equal(0m, rows[3].Percent);
        Assert.Equal(50m, rows[1].Remaining);
    }

    [Fact]
    public void Progress_handles_lines_without_boq_as_flat_rows()
    {
        var lines = new List<ContractLine> { new() { Id = 1, Description = "Máy", Quantity = 2, UnitPrice = 100, SortOrder = 1 } };
        var rows = BoqAcceptances.Progress(lines, new Dictionary<int, decimal> { [1] = 1 });
        Assert.Single(rows);
        Assert.False(rows[0].IsGroup);
        Assert.Equal(50m, rows[0].Percent);
    }
}
