using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

public class PackageRiskTests
{
    private static readonly ProcurementSettings S = new() { DirectAwardMax = 100_000_000, CompetitiveQuotationMax = 5_000_000_000, DirectAwardSplitWindowDays = 90 };

    private static PackageRisk.Facts F(decimal budget, ProcurementMethod m, bool boq = true, int priced = 3, decimal? dev = 0, int vendors = 5, decimal related = 0, int relatedN = 0, params string[] manual) =>
        new(budget, m, null, boq, 3, priced, dev, vendors, related, relatedN, manual.ToHashSet());

    [Fact]
    public void CleanPackage_IsLow()
    {
        var r = PackageRisk.Score(F(2_000_000_000, ProcurementMethod.CompetitiveQuotation), S);
        Assert.Equal(0, r.Score); Assert.Equal("Thấp", r.Level);
    }

    [Fact]
    public void NearThreshold_SplitAndNonCompetitive_IsHigh()
    {
        // 95 tr sát hạn mức chỉ định 100 tr, gói cùng lĩnh vực 60 tr → cộng dồn vượt; chỉ định thầu
        var r = PackageRisk.Score(F(95_000_000, ProcurementMethod.DirectAward, related: 60_000_000, relatedN: 1), S);
        Assert.Contains(r.Factors, f => f.Key == "threshold" && f.Hit);
        Assert.Contains(r.Factors, f => f.Key == "split" && f.Hit);
        Assert.Contains(r.Factors, f => f.Key == "method" && f.Hit);
        Assert.Equal(50, r.Score); Assert.True(r.IsHigh);
    }

    [Fact]
    public void BasisDeviationVendorsAndManualFactors()
    {
        var r = PackageRisk.Score(F(2_000_000_000, ProcurementMethod.OpenTender, boq: false, priced: 0, dev: 35, vendors: 2, manual: new[] { "brand", "urgent" }), S);
        Assert.Equal(10 + 10 + 10 + 15 + 10, r.Score); // căn cứ, lệch giá, ít NCC, nhãn hiệu, khẩn cấp
        Assert.True(r.IsHigh);
        Assert.Equal(new[] { "brand", "urgent" }, PackageRisk.ParseChecklist(" brand,urgent ,").OrderBy(x => x));
    }
}
