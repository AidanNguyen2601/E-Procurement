using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Xunit;

namespace EProcurement.Tests;

public class BoqBidTests
{
    private static BoqItem I(int id, int? parent, int order, string name, decimal q = 0, decimal p = 0) =>
        new() { Id = id, PackageId = 1, ParentId = parent, SortOrder = order, Name = name, Quantity = q, UnitPrice = p };

    [Fact]
    public void Leaves_carry_code_and_ancestor_path_and_skip_zero_quantity()
    {
        var items = new List<BoqItem>
        {
            I(1, null, 1, "Phần móng"), I(2, 1, 1, "Bê tông"), I(3, 2, 1, "Bê tông lót M100", 12.345m, 900_000),
            I(4, 2, 2, "Ván khuôn", 0, 5000), I(5, null, 2, "Hoàn thiện", 1, 10_000_000)
        };
        var leaves = Boq.Leaves(items);
        Assert.Equal(new[] { "1.1.1", "2" }, leaves.Select(l => l.Node.Code));
        Assert.Equal("1 Phần móng › 1.1 Bê tông", leaves[0].Path);
        Assert.Equal("", leaves[1].Path);
    }

    private static (Tender, List<TenderBidder>) Setup()
    {
        var t = new Tender { Id = 1 };
        t.Lines.Add(new TenderLine { Id = 11, Description = "Bê tông lót", Unit = "m3", Quantity = 10, EstimatedPrice = 1000, BoqCode = "1.1.1", BoqPath = "1 Móng › 1.1 Bê tông", SortOrder = 1 });
        t.Lines.Add(new TenderLine { Id = 12, Description = "Thép", Unit = "kg", Quantity = 100, EstimatedPrice = 20, BoqCode = "1.1.2", BoqPath = "1 Móng › 1.1 Bê tông", SortOrder = 2 });
        t.Lines.Add(new TenderLine { Id = 13, Description = "Sơn", Unit = "m2", Quantity = 50, EstimatedPrice = 100, BoqCode = "2", BoqPath = null, SortOrder = 3 });
        TenderBidder B(int id, string name, params (int line, decimal price)[] p)
        {
            var b = new TenderBidder { Id = id, Vendor = new Vendor { Name = name }, Submitted = true };
            foreach (var (line, price) in p) b.Lines.Add(new TenderBidLine { TenderLineId = line, UnitPrice = price });
            return b;
        }
        return (t, new List<TenderBidder>
        {
            B(1, "A", (11, 900), (12, 30), (13, 120)),
            B(2, "B", (11, 950), (12, 10), (13, 0))   // thép thấp bất thường (<70%), thiếu giá sơn
        });
    }

    [Fact]
    public void Build_groups_rows_and_rolls_up_amounts()
    {
        var (t, bidders) = Setup();
        var r = BoqBid.Build(t, bidders);
        Assert.True(r.HasBoq);
        Assert.Equal(new[] { "1", "1.1", "1.1.1", "1.1.2", "2" }, r.Rows.Select(x => x.Code));
        Assert.Equal(new[] { true, true, false, false, false }, r.Rows.Select(x => x.IsGroup));
        Assert.Equal(new[] { 0, 1, 2, 2, 0 }, r.Rows.Select(x => x.Depth));
        // A: 10*900 + 100*30 = 12.000 ; B: 10*950 + 100*10 = 10.500
        Assert.Equal(12_000m, r.Rows[1].Cells[1].Amount);
        Assert.Equal(10_500m, r.Rows[1].Cells[2].Amount);
        Assert.Equal(10 * 1000m + 100 * 20m, r.Rows[1].EstimatedAmount);
        Assert.Equal(12_000m + 6_000m, r.Totals[1]);
        Assert.Equal(10_500m, r.Totals[2]);
        Assert.Equal(12_000m + 5_000m, r.EstimatedTotal);
    }

    [Fact]
    public void Build_marks_lowest_flags_and_deviation()
    {
        var (t, bidders) = Setup();
        var r = BoqBid.Build(t, bidders);
        var concrete = r.Rows.First(x => x.Code == "1.1.1");
        Assert.True(concrete.Cells[1].Lowest); Assert.False(concrete.Cells[2].Lowest);
        Assert.Equal(-10m, concrete.Cells[1].DeviationPct);
        var steel = r.Rows.First(x => x.Code == "1.1.2");
        Assert.Equal("high", steel.Cells[1].Flag);     // 30 > 130% của 20
        Assert.Equal("low", steel.Cells[2].Flag);      // 10 < 70% của 20
        Assert.Equal("missing", r.Rows.First(x => x.Code == "2").Cells[2].Flag);
        Assert.Equal(2, r.FlagCounts[2]);
        Assert.Equal(1, r.FlagCounts[1]);
        Assert.False(r.Rows.First(x => x.Code == "2").Cells[1].Lowest); // chỉ một giá → không đánh dấu thấp nhất
    }

    [Fact]
    public void Lines_without_boq_are_flat_and_csv_has_two_columns_per_bidder()
    {
        var t = new Tender { Id = 1 };
        t.Lines.Add(new TenderLine { Id = 1, Description = "Máy", Unit = "Cái", Quantity = 2, EstimatedPrice = 100, SortOrder = 1 });
        var b = new TenderBidder { Id = 9, Vendor = new Vendor { Name = "X" }, Submitted = true };
        b.Lines.Add(new TenderBidLine { TenderLineId = 1, UnitPrice = 90 });
        var r = BoqBid.Build(t, new[] { b });
        Assert.False(r.HasBoq);
        Assert.Single(r.Rows);
        Assert.Equal("1", r.Rows[0].Code);
        var csv = BoqBid.ToCsv(r).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, csv.Length);
        Assert.Equal(6 + 2, csv[0].Split(';').Length);
        Assert.EndsWith(";90;180", csv[1]);
    }
}
