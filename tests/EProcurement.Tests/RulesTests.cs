using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

public class ProcurementRulesTests
{
    private static readonly ProcurementSettings S = new() { CompetitiveQuotationMax = 5_000_000_000 };

    [Fact]
    public void Suggest_DirectPurchase_WhenValidReferenceExists() =>
        Assert.Equal(ProcurementMethod.DirectPurchase, ProcurementRules.Suggest(new PurchaseRequest { EstimatedTotal = 9_000_000_000 }, true, S));

    [Theory]
    [InlineData(5_000_000_000, ProcurementMethod.CompetitiveQuotation)]
    [InlineData(5_000_000_001, ProcurementMethod.OpenTender)]
    [InlineData(10_000_000, ProcurementMethod.CompetitiveQuotation)]
    public void Suggest_ByValueThreshold(decimal total, ProcurementMethod expected) =>
        Assert.Equal(expected, ProcurementRules.Suggest(new PurchaseRequest { EstimatedTotal = total }, false, S));

    [Fact]
    public void DirectAward_AlwaysNeedsReason() =>
        Assert.True(ProcurementRules.NeedsReason(ProcurementMethod.DirectAward, ProcurementMethod.DirectAward));

    [Fact]
    public void LessCompetitiveThanSuggested_NeedsReason() =>
        Assert.True(ProcurementRules.NeedsReason(ProcurementMethod.CompetitiveQuotation, ProcurementMethod.OpenTender));

    [Fact]
    public void MoreCompetitiveOrDirectPurchase_NoReason()
    {
        Assert.False(ProcurementRules.NeedsReason(ProcurementMethod.OpenTender, ProcurementMethod.CompetitiveQuotation));
        Assert.False(ProcurementRules.NeedsReason(ProcurementMethod.DirectPurchase, ProcurementMethod.OpenTender));
    }

    [Theory]
    [InlineData(DirectAwardReason.SoleSource, true)]
    [InlineData(DirectAwardReason.Emergency, true)]
    [InlineData(DirectAwardReason.Compatibility, true)]
    [InlineData(DirectAwardReason.UnderThreshold, false)]
    public void DirectAward_EvidenceRequirement(DirectAwardReason r, bool expected) => Assert.Equal(expected, ProcurementRules.RequiresEvidence(r));
}

public class RfqEvaluationTests
{
    private static Rfq Build(EvaluationMethod m, params (int vendor, decimal price, int lead, bool? tech, int score)[] quotes)
    {
        var r = new Rfq { EvaluationMethod = m, PriceWeight = 60, TechnicalWeight = 30, DeliveryWeight = 10, Status = RfqStatus.Opened, Deadline = DateTime.Today.AddDays(-1) };
        var line = new RfqLine { Id = 1, Quantity = 10, Description = "Hàng" };
        r.Lines.Add(line);
        foreach (var q in quotes)
        {
            var rv = new RfqVendor { VendorId = q.vendor, Vendor = new Vendor { Id = q.vendor, Name = "V" + q.vendor }, Responded = true, LeadTimeDays = q.lead, TechnicalPass = q.tech, TechnicalScore = q.score, VatPercent = 10 };
            rv.QuoteLines.Add(new VendorQuoteLine { RfqLineId = 1, UnitPrice = q.price, Amount = q.price * 10 });
            rv.TotalAmount = q.price * 10;
            r.Vendors.Add(rv);
        }
        return r;
    }

    [Fact]
    public void LowestPrice_RanksCheapestTechnicallyPassing()
    {
        var res = RfqEvaluation.Evaluate(Build(EvaluationMethod.LowestPrice, (1, 100, 5, true, 0), (2, 90, 5, false, 0), (3, 95, 5, true, 0)));
        Assert.Equal(3, res.First(x => x.Rank == 1).Quote.VendorId);           // NCC 2 rẻ nhất nhưng không đạt kỹ thuật
        Assert.Null(res.First(x => x.Quote.VendorId == 2).Rank);
    }

    [Fact]
    public void WeightedScore_CanPickNonLowestPrice()
    {
        var res = RfqEvaluation.Evaluate(Build(EvaluationMethod.WeightedScore, (1, 100, 2, true, 95), (2, 96, 20, true, 70)));
        Assert.Equal(1, res.First(x => x.Rank == 1).Quote.VendorId);
    }

    [Fact]
    public void IncompleteQuote_IsInvalid()
    {
        var r = Build(EvaluationMethod.LowestPrice, (1, 100, 5, true, 0));
        r.Lines.Add(new RfqLine { Id = 2, Quantity = 1, Description = "Hàng 2" });
        var res = RfqEvaluation.Evaluate(r);
        Assert.False(res[0].Valid);
        Assert.Contains(res[0].Issues, i => i.Contains("đủ các dòng"));
    }

    [Fact]
    public void CanOpen_OnlyAfterDeadlineOrAllResponded()
    {
        var r = Build(EvaluationMethod.LowestPrice, (1, 100, 5, null, 0));
        r.Status = RfqStatus.Open; r.Deadline = DateTime.Today.AddDays(3);
        r.Vendors.Add(new RfqVendor { VendorId = 2, Responded = false });
        Assert.False(RfqEvaluation.CanOpen(r));
        r.Deadline = DateTime.Today.AddDays(-1);
        Assert.True(RfqEvaluation.CanOpen(r));
    }
}

public class TenderEvaluationTests
{
    private static Tender Build(TenderEvaluationMethod m, params (int vendor, decimal price, bool valid, bool qualified, int[] scores)[] bids)
    {
        var t = new Tender { EvaluationMethod = m, MinTechnicalScore = 70, TechnicalWeight = 30, PriceWeight = 70, BidValidityDays = 60, ClosingAt = DateTime.Today };
        t.Committee.Add(new TenderCommitteeMember { UserId = 1 });
        t.Committee.Add(new TenderCommitteeMember { UserId = 2 });
        foreach (var b in bids)
        {
            var x = new TenderBidder { Id = b.vendor, VendorId = b.vendor, Vendor = new Vendor { Name = "V" + b.vendor }, Submitted = true, BidPrice = b.price, VatPercent = 10, Valid = b.valid, Qualified = b.qualified };
            foreach (var (s, i) in b.scores.Select((s, i) => (s, i))) x.Scores.Add(new TenderScore { MemberUserId = i + 1, TechnicalScore = s });
            t.Bidders.Add(x);
        }
        return t;
    }

    [Fact]
    public void LowestEvaluatedPrice_AmongTechnicallyPassing()
    {
        var res = TenderEvaluation.Evaluate(Build(TenderEvaluationMethod.LowestEvaluatedPrice,
            (1, 1000, true, true, new[] { 80, 80 }), (2, 900, true, true, new[] { 60, 65 }), (3, 950, true, true, new[] { 75, 75 }), (4, 800, false, false, Array.Empty<int>())));
        Assert.Equal(3, res.First(r => r.Rank == 1).Bid.VendorId); // NCC 2 dưới điểm tối thiểu, NCC 4 không hợp lệ
        Assert.False(res.First(r => r.Bid.VendorId == 2).TechnicalPass);
    }

    [Fact]
    public void MissingCommitteeScore_NotPassed()
    {
        var res = TenderEvaluation.Evaluate(Build(TenderEvaluationMethod.LowestEvaluatedPrice, (1, 1000, true, true, new[] { 90 })));
        Assert.False(res[0].TechnicalPass);
        Assert.Contains(res[0].Issues, i => i.Contains("1/2"));
    }

    [Fact]
    public void Combined_WeighsTechnicalAndPrice()
    {
        var res = TenderEvaluation.Evaluate(Build(TenderEvaluationMethod.Combined, (1, 1000, true, true, new[] { 95, 95 }), (2, 980, true, true, new[] { 71, 71 })));
        Assert.Equal(1, res.First(r => r.Rank == 1).Bid.VendorId);
    }

    [Fact]
    public void TwoEnvelope_PricesHiddenUntilFinancialOpening()
    {
        var t = new Tender { EnvelopeMethod = EnvelopeMethod.TwoEnvelope, OpenedAt = DateTime.Now };
        Assert.False(TenderEvaluation.PricesVisible(t));
        t.FinancialOpenedAt = DateTime.Now;
        Assert.True(TenderEvaluation.PricesVisible(t));
    }

    [Fact]
    public void BidSecurity_ChecksAmountAndValidity()
    {
        var t = new Tender { BidSecurityAmount = 10_000_000, BidValidityDays = 60, ClosingAt = new DateTime(2026, 1, 1) };
        var b = new TenderBidder { BidSecurityAmount = 5_000_000, BidSecurityExpiry = new DateTime(2026, 2, 1), ValidityDays = 30 };
        var p = TenderEvaluation.SecurityProblems(t, b);
        Assert.Equal(3, p.Count);
    }
}

public class SecurityTests
{
    [Fact]
    public void LoginGuard_LocksAfterMaxFailures()
    {
        int failed = 0; DateTime? until = null;
        for (var i = 1; i < LoginGuard.MaxFailed; i++) Assert.False(LoginGuard.Fail(ref failed, ref until));
        Assert.True(LoginGuard.Fail(ref failed, ref until));
        Assert.True(LoginGuard.IsLocked(until));
        Assert.Equal(0, failed);
    }

    [Fact]
    public void SealHash_IndependentOfDateTimeKind()
    {
        var local = new DateTime(2026, 11, 23, 13, 44, 52, 123, DateTimeKind.Local).AddTicks(4567);
        var fromDb = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        Assert.Equal(SealingService.Hash("{\"a\":1}", local), SealingService.Hash("{\"a\":1}", fromDb));
        Assert.NotEqual(SealingService.Hash("{\"a\":1}", local), SealingService.Hash("{\"a\":2}", local));
    }

    [Fact]
    public void ApiKeyHash_IsStableSha256() =>
        Assert.Equal(64, EProcurement.Web.Controllers.ApiScopes.Hash("ep_test").Length);
}
