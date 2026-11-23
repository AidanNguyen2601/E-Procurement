using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.DataProtection;

namespace EProcurement.Tests;

/// <summary>Gói mua sắm, KHLCNT, đàm phán hợp đồng, thẩm định kết quả, báo giá chỉ định qua cổng</summary>
public class PackageAndSelectionTests
{
    private static readonly ProcurementSettings S = new() { CompetitiveQuotationMax = 5_000_000_000, DirectAwardMax = 100_000_000 };

    // ---------------------------------------------------------------- gói mua sắm
    [Fact]
    public void Package_LinesAndLead_ComeFromAllPrsInOrder()
    {
        var p = new ProcurementPackage();
        var pr2 = new PurchaseRequest { Id = 2, EstimatedTotal = 30 };
        pr2.Lines.Add(new PurchaseRequestLine { Description = "B1", SortOrder = 1 });
        var pr1 = new PurchaseRequest { Id = 1, EstimatedTotal = 70 };
        pr1.Lines.Add(new PurchaseRequestLine { Description = "A2", SortOrder = 2 });
        pr1.Lines.Add(new PurchaseRequestLine { Description = "A1", SortOrder = 1 });
        p.PurchaseRequests.Add(pr2); p.PurchaseRequests.Add(pr1);
        Assert.Equal(1, Packages.Lead(p).Id);
        Assert.Equal(new[] { "A1", "A2", "B1" }, Packages.Lines(p).Select(l => l.Description));
        Assert.Equal(100, Packages.Estimate(p.PurchaseRequests));
    }

    [Fact]
    public void MethodProblems_Valid_WhenFollowingSuggestion() =>
        Assert.Empty(Packages.MethodProblems(ProcurementMethod.CompetitiveQuotation, ProcurementMethod.CompetitiveQuotation, null, false, 90_000_000, 90_000_000, S));

    [Fact]
    public void MethodProblems_DirectAward_NeedsReason()
    {
        var p = Packages.MethodProblems(ProcurementMethod.DirectAward, ProcurementMethod.CompetitiveQuotation, " ", false, 50_000_000, 50_000_000, S);
        Assert.Contains(p, x => x.Contains("lý do"));
    }

    [Fact]
    public void MethodProblems_DirectPurchase_NeedsReference_AndCqHasCeiling()
    {
        Assert.Contains(Packages.MethodProblems(ProcurementMethod.DirectPurchase, ProcurementMethod.CompetitiveQuotation, null, false, 10, 10, S), x => x.Contains("PO tham chiếu"));
        Assert.Contains(Packages.MethodProblems(ProcurementMethod.CompetitiveQuotation, ProcurementMethod.OpenTender, "lý do", false, 6_000_000_000, 6_000_000_000, S), x => x.Contains("vượt hạn mức"));
    }

    [Fact]
    public void MethodProblems_FlagsBudgetFarBelowEstimate() =>
        Assert.Contains(Packages.MethodProblems(ProcurementMethod.CompetitiveQuotation, ProcurementMethod.CompetitiveQuotation, null, false, 40, 100, S), x => x.Contains("thấp bất thường"));

    [Theory]
    [InlineData(PackageStatus.Draft, false)]
    [InlineData(PackageStatus.InPlan, false)]
    [InlineData(PackageStatus.Approved, true)]
    [InlineData(PackageStatus.Executing, true)]
    [InlineData(PackageStatus.Cancelled, false)]
    public void ExecuteProblem_OnlyApprovedPackagesInPlan(PackageStatus status, bool ok)
    {
        var p = new ProcurementPackage { Number = "GM-1", Status = status, Method = ProcurementMethod.OpenTender };
        p.PurchaseRequests.Add(new PurchaseRequest { Id = 1 });
        Assert.Equal(ok, Packages.ExecuteProblem(p, ProcurementMethod.OpenTender) == null);
    }

    [Fact]
    public void ExecuteProblem_WrongMethod()
    {
        var p = new ProcurementPackage { Number = "GM-1", Status = PackageStatus.Approved, Method = ProcurementMethod.OpenTender };
        p.PurchaseRequests.Add(new PurchaseRequest { Id = 1 });
        Assert.NotNull(Packages.ExecuteProblem(p, ProcurementMethod.CompetitiveQuotation));
    }

    // ---------------------------------------------------------------- đàm phán & thẩm định
    private static List<BidResult> Ranked(params (int BidderId, int Rank)[] x) =>
        x.Select(r => new BidResult { Bid = new TenderBidder { Id = r.BidderId }, Rank = r.Rank }).ToList();

    [Fact]
    public void NegotiationCandidate_StartsWithRankOne_ThenNextAfterFailure()
    {
        var results = Ranked((10, 2), (20, 1), (30, 3));
        var negs = new List<TenderNegotiation>();
        Assert.Equal(20, TenderEvaluation.NegotiationCandidate(results, negs)!.Bid.Id);
        negs.Add(new TenderNegotiation { TenderBidderId = 20, Success = false });
        Assert.Equal(10, TenderEvaluation.NegotiationCandidate(results, negs)!.Bid.Id);
        negs.Add(new TenderNegotiation { TenderBidderId = 10, Success = true, NegotiatedPrice = 90 });
        Assert.Null(TenderEvaluation.NegotiationCandidate(results, negs));
        Assert.Equal(10, TenderEvaluation.SuccessfulNegotiation(negs)!.TenderBidderId);
    }

    [Fact]
    public void NegotiationCandidate_IgnoresVoidedNegotiations_AndUnrankedBids()
    {
        var results = Ranked((20, 1));
        results.Add(new BidResult { Bid = new TenderBidder { Id = 99 } }); // không đạt kỹ thuật
        var negs = new List<TenderNegotiation> { new() { TenderBidderId = 20, Success = true, IsVoid = true } };
        Assert.Null(TenderEvaluation.SuccessfulNegotiation(negs));
        Assert.Equal(20, TenderEvaluation.NegotiationCandidate(results, negs)!.Bid.Id);
        negs.Add(new TenderNegotiation { TenderBidderId = 20, Success = false });
        Assert.Null(TenderEvaluation.NegotiationCandidate(results, negs)); // còn lại không xếp hạng → không mời
    }

    [Fact]
    public void ApprovalBlocker_RequiresCompletedAgreeingAppraisal()
    {
        var t = new Tender();
        Assert.Null(TenderEvaluation.ApprovalBlocker(t, appraisalRequired: false));
        Assert.Contains("Chưa giao", TenderEvaluation.ApprovalBlocker(t, true));
        t.Appraisals.Add(new TenderAppraisal { AssignedAt = DateTime.Now.AddHours(-2) });
        Assert.Contains("Đang chờ", TenderEvaluation.ApprovalBlocker(t, true));
        t.Appraisals.First().CompletedAt = DateTime.Now; t.Appraisals.First().Agree = false;
        Assert.Contains("không thống nhất", TenderEvaluation.ApprovalBlocker(t, true));
        t.Appraisals.First().IsVoid = true;
        t.Appraisals.Add(new TenderAppraisal { AssignedAt = DateTime.Now, CompletedAt = DateTime.Now, Agree = true });
        Assert.Null(TenderEvaluation.ApprovalBlocker(t, true));
    }

    // ---------------------------------------------------------------- báo giá chỉ định qua cổng
    [Fact]
    public async Task DirectQuote_Unseal_FillsProposedAndCapsNegotiatedPrice()
    {
        var db = TestDb.Create();
        var (u, v, _, _, laptop, paper) = TestDb.Seed(db);
        var pr = new PurchaseRequest { Number = "PR-1", Title = "x", Department = "d", RequesterId = u.Id };
        db.Add(pr); db.SaveChanges();
        var d = new DirectAward { Number = "DA-1", Title = "Chỉ định", PurchaseRequestId = pr.Id, VendorId = v.Id, BuyerId = u.Id, Justification = "j", QuoteRequestedAt = DateTime.Now };
        d.Lines.Add(new DirectAwardLine { ItemId = laptop.Id, Description = "Laptop", Quantity = 2, SortOrder = 1, NegotiatedPrice = 25_000_000 });
        d.Lines.Add(new DirectAwardLine { ItemId = paper.Id, Description = "Giấy", Quantity = 10, SortOrder = 2 });
        db.Add(d);
        var vu = new VendorUser { VendorId = v.Id, Email = "a@b.c", FullName = "NCC", PasswordHash = "x" };
        db.Add(vu); db.SaveChanges();
        var seal = new SealingService(new EphemeralDataProtectionProvider(), db);
        var payload = new DirectQuotePayload(d.Id, 7, "30 ngày", "12 tháng", 8, "Hàng có sẵn",
            new() { new(1, 20_000_000, null), new(2, 80_000, null) }, null, null, null);
        var sub = seal.Seal(payload, SubmissionKind.DirectQuote, vu.Id, null, null, 1, null, 0);
        sub.DirectAwardId = d.Id;
        db.Add(sub); db.SaveChanges();

        var note = await seal.UnsealDirectQuoteAsync(d, u.Id);
        Assert.Contains("khớp", note);
        Assert.Equal(20_000_000, d.Lines.First(l => l.SortOrder == 1).ProposedPrice);
        Assert.Equal(20_000_000, d.Lines.First(l => l.SortOrder == 1).NegotiatedPrice); // giá thương thảo cũ cao hơn giá chào → hạ về giá chào
        Assert.Equal(80_000, d.Lines.First(l => l.SortOrder == 2).NegotiatedPrice);
        Assert.Equal(40_800_000, d.ProposedTotal);
        Assert.Equal(8, d.VatPercent);
        Assert.NotNull(d.QuoteOpenedAt);
        db.SaveChanges(); // như OpenQuote: lưu sau khi mở
        Assert.Null(await seal.UnsealDirectQuoteAsync(d, u.Id)); // đã mở → không mở lại
    }
}
