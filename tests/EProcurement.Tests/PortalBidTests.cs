using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.DataProtection;

namespace EProcurement.Tests;

/// <summary>Quy tắc dự thầu online trên cổng NCC: kiểm tra HSDT, làm rõ, kiến nghị, niêm phong nhiều file</summary>
public class PortalBidTests
{
    private static Tender T(decimal security = 10_000_000) => new()
    {
        Id = 1, Number = "TB-1", Title = "Gói thử", ClosingAt = new DateTime(2026, 12, 10, 9, 0, 0), BidValidityDays = 60, BidSecurityAmount = security
    };

    [Fact]
    public void BidProblems_None_WhenSecurityAndValidityMeetTender()
    {
        var t = T();
        Assert.Empty(TenderEvaluation.BidProblems(t, 10_000_000, "BL-01", t.ClosingAt.Date.AddDays(90), 60));
    }

    [Fact]
    public void BidProblems_ReportsEachShortfall()
    {
        var t = T();
        var p = TenderEvaluation.BidProblems(t, 5_000_000, " ", t.ClosingAt.Date.AddDays(89), 30);
        Assert.Equal(4, p.Count); // số tiền, hiệu lực bảo lãnh, hiệu lực HSDT, thiếu số bảo lãnh
    }

    [Fact]
    public void BidProblems_NoSecurityRequired_OnlyChecksValidity()
    {
        var t = T(security: 0);
        Assert.Empty(TenderEvaluation.BidProblems(t, 0, null, null, 60));
        Assert.Single(TenderEvaluation.BidProblems(t, 0, null, null, null));
    }

    [Fact]
    public void QuestionDeadline_IsMinDaysBeforeClosing() =>
        Assert.Equal(new DateTime(2026, 12, 7, 9, 0, 0), TenderEvaluation.QuestionDeadline(T(), 3));

    [Fact]
    public void AmendmentsAfter_OnlyAmendmentsPostedAfterSubmission()
    {
        var t = T();
        var at = new DateTime(2026, 12, 1, 10, 0, 0);
        t.Clarifications.Add(new TenderClarification { IsAmendment = true, CreatedAt = at.AddHours(-1), Answer = "a" });
        t.Clarifications.Add(new TenderClarification { IsAmendment = false, CreatedAt = at.AddHours(1), Answer = "b" });
        t.Clarifications.Add(new TenderClarification { IsAmendment = true, CreatedAt = at.AddHours(2), Answer = "c" });
        Assert.Equal("c", Assert.Single(TenderEvaluation.AmendmentsAfter(t, at)).Answer);
        Assert.Empty(TenderEvaluation.AmendmentsAfter(t, null));
    }

    [Fact]
    public void CanComplain_OnlySubmittedBidder_WithinDeadline()
    {
        var t = T(); t.Status = TenderStatus.Awarded; t.ComplaintDeadline = new DateTime(2026, 12, 20);
        var submitted = new TenderBidder { Submitted = true };
        Assert.True(TenderEvaluation.CanComplain(t, submitted, new DateTime(2026, 12, 19)));
        Assert.False(TenderEvaluation.CanComplain(t, submitted, new DateTime(2026, 12, 21)));
        Assert.False(TenderEvaluation.CanComplain(t, new TenderBidder(), new DateTime(2026, 12, 19)));
        t.Status = TenderStatus.Contracted;
        Assert.False(TenderEvaluation.CanComplain(t, submitted, new DateTime(2026, 12, 19)));
    }

    // ---------------------------------------------------------------- niêm phong
    private static (Web.Data.EProcDbContext Db, SealingService Seal, Tender T) SealedBid(EnvelopeMethod method, BidPayload? payload = null)
    {
        var db = TestDb.Create();
        var (u, v, _, _, _, _) = TestDb.Seed(db);
        var pr = new PurchaseRequest { Number = "PR-1", Title = "x", Department = "d", RequesterId = u.Id };
        db.Add(pr); db.SaveChanges();
        var t = new Tender { Number = "TB-1", Title = "Gói", PurchaseRequestId = pr.Id, BuyerId = u.Id, EnvelopeMethod = method, Status = TenderStatus.Published,
            TechnicalRequirements = "k", QualificationCriteria = "n" };
        t.Lines.Add(new TenderLine { Description = "Laptop", Quantity = 2 });
        var b = new TenderBidder { VendorId = v.Id };
        t.Bidders.Add(b);
        db.Add(t); db.SaveChanges();
        var vu = new VendorUser { VendorId = v.Id, Email = "a@b.c", FullName = "NCC", PasswordHash = "x" };
        db.Add(vu); db.SaveChanges();
        var seal = new SealingService(new EphemeralDataProtectionProvider(), db);
        payload ??= new BidPayload(b.Id, 0, 10, 30, 60, 0, null, null, null,
            new() { new BidLinePayload(t.Lines.First().Id, 1_000_000) }, null, null, null,
            new()
            {
                new(BidFileKind.Technical, "kt.pdf", "application/pdf", Convert.ToBase64String(new byte[] { 1 })),
                new(BidFileKind.Financial, "tc.pdf", "application/pdf", Convert.ToBase64String(new byte[] { 2 }))
            });
        db.Add(seal.Seal(payload, SubmissionKind.Bid, vu.Id, null, b.Id, 1, "kt.pdf; tc.pdf", 2));
        db.SaveChanges();
        return (db, seal, t);
    }

    private static string[] Unsealed(Web.Data.EProcDbContext db) =>
        db.ChangeTracker.Entries<Attachment>().Select(e => e.Entity.FileName).OrderBy(x => x).ToArray();

    [Fact]
    public async Task TwoEnvelope_FinancialFile_StaysSealedUntilFinancialOpening()
    {
        var (db, seal, t) = SealedBid(EnvelopeMethod.TwoEnvelope);
        await seal.UnsealBidsAsync(t, 1, includePrices: false);
        Assert.Equal(new[] { "kt.pdf" }, Unsealed(db));
        Assert.Equal(0, t.Bidders.First().BidPrice);

        await seal.UnsealBidsAsync(t, 1, includePrices: true, pricesOnly: true);
        Assert.Equal(new[] { "kt.pdf", "tc.pdf" }, Unsealed(db));
        Assert.Equal(2_000_000, t.Bidders.First().BidPrice);
    }

    [Fact]
    public async Task OneEnvelope_AllFilesOpenedOnce()
    {
        var (db, seal, t) = SealedBid(EnvelopeMethod.OneEnvelope);
        await seal.UnsealBidsAsync(t, 1, includePrices: true);
        Assert.Equal(new[] { "kt.pdf", "tc.pdf" }, Unsealed(db));
    }

    [Fact]
    public async Task LegacySingleFilePayload_StillOpens()
    {
        var (db, seal, t) = SealedBid(EnvelopeMethod.OneEnvelope, new BidPayload(0, 0, 10, 30, 60, 0, null, null, null,
            new(), "hsdt.zip", "application/zip", Convert.ToBase64String(new byte[] { 3 })));
        // payload trỏ TenderBidderId = 0 không ảnh hưởng: submission gắn với bidder qua TenderBidderId của bản ghi
        await seal.UnsealBidsAsync(t, 1, includePrices: true);
        Assert.Equal(new[] { "hsdt.zip" }, Unsealed(db));
    }
}
