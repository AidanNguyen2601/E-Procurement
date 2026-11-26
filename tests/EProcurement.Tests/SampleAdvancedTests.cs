using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Tests;

public class SampleAdvancedTests
{
    private static async Task<EProcDbContext> LoadedAsync()
    {
        var db = TestDb.Create();
        TestDb.Seed(db);
        db.Users.AddRange(
            new AppUser { Username = "admin", FullName = "QT", Role = AppRoles.Admin, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "director", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" },
            new AppUser { Username = "requester", FullName = "NV", Role = AppRoles.Requester, Department = "Kỹ thuật", PasswordHash = "x" },
            new AppUser { Username = "ketoan.tt", FullName = "KT", Role = AppRoles.Accountant, Department = "Kế toán", PasswordHash = "x" },
            new AppUser { Username = "approver", FullName = "TP1", Role = AppRoles.Approver, Department = "Kỹ thuật", PasswordHash = "x" },
            new AppUser { Username = "approver2", FullName = "TP2", Role = AppRoles.Approver, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "buyer2", FullName = "MH2", Role = AppRoles.Buyer, Department = "Mua hàng", PasswordHash = "x" });
        db.Vendors.AddRange(
            new Vendor { Code = "NCC002", Name = "NCC hai", TaxCode = "0100000002", Status = VendorStatus.Approved },
            new Vendor { Code = "NCC003", Name = "NCC ba", TaxCode = "0100000003", Status = VendorStatus.Approved },
            new Vendor { Code = "NCC004", Name = "NCC bốn", TaxCode = "0100000004", Status = VendorStatus.Approved });
        db.SaveChanges();
        await SampleConstruction.LoadAsync(db);
        var done = await SampleAdvanced.LoadAsync(db);
        Assert.NotEmpty(done);
        Assert.Empty(await SampleAdvanced.LoadAsync(db)); // nạp lại: bỏ qua
        return db;
    }

    [Fact]
    public async Task Liquidated_contract_is_settled_and_closed()
    {
        var db = await LoadedAsync();
        var c = await db.Contracts.Include(x => x.Guarantees).SingleAsync(x => x.Number == SampleAdvanced.Marker);
        Assert.Equal(ContractStatus.Liquidated, c.Status);
        var s = await db.ContractSettlements.SingleAsync(x => x.ContractId == c.Id);
        Assert.Equal(SettlementStatus.Approved, s.Status);
        Assert.Equal(s.RetentionHeld, s.Balance);   // quyết toán: chỉ còn tiền giữ lại
        Assert.True(s.RetentionHeld > 0);
        var l = await ContractClose.LedgerAsync(db, c);
        Assert.Equal(0, l.AdvanceOutstanding);
        Assert.Equal(0, l.RetentionOutstanding);
        Assert.All(ContractClose.LiquidationChecks(c, s, l, c.Guarantees), x => Assert.True(x.Ok, x.Text));
        Assert.Equal(c.Value * 1.1m, l.WorkGross);
    }

    [Fact]
    public async Task Tender_in_evaluation_has_profiles_preference_and_problems()
    {
        var db = await LoadedAsync();
        var t = await db.Tenders.Include(x => x.Requirements).Include(x => x.Bidders).ThenInclude(b => b.Partners).Include(x => x.Bidders).ThenInclude(b => b.Capabilities)
            .Include(x => x.Clarifications).Include(x => x.Package).SingleAsync(x => x.Number == "MAU-TD-XD-002");
        Assert.Equal(7, t.Requirements.Count);
        var none = new HashSet<int>();
        var byPrice = t.Bidders.OrderBy(b => b.BidPrice).ToList(); // B (thấp nhất), A (liên danh), C (ưu đãi)
        Assert.Contains(BidderProfile.PartnerProblems(t, byPrice[0], none), p => p.Contains("30"));
        Assert.Equal(false, BidderProfile.Suggest(BidderProfile.Check(t, byPrice[0])));
        Assert.True(BidderProfile.IsJointVenture(byPrice[1]));
        Assert.Empty(BidderProfile.PartnerProblems(t, byPrice[1], none));
        Assert.DoesNotContain(BidderProfile.Check(t, byPrice[1]), q => q.Pass == false);
        Assert.True(byPrice[2].DomesticPreferred);
        Assert.All(LegalTimelines.CheckTender(t, t.Package!.Field, t.Clarifications, new LegalTimelineSettings(), DateTime.Now), c => Assert.NotEqual(false, c.Ok));

        var draft = await db.Tenders.Include(x => x.Clarifications).Include(x => x.Package).SingleAsync(x => x.Number == "MAU-TD-XD-003");
        Assert.False(LegalTimelines.CheckTender(draft, draft.Package!.Field, draft.Clarifications, new LegalTimelineSettings(), DateTime.Now)[0].Ok);
    }

    [Fact]
    public async Task Risky_packages_norms_esg_and_adjustments()
    {
        var db = await LoadedAsync();
        var s = new ProcurementSettings { DirectAwardMax = 100_000_000, CompetitiveQuotationMax = 5_000_000_000, DirectAwardSplitWindowDays = 90 };
        foreach (var no in new[] { "MAU-GM-XD-006", "MAU-GM-XD-007" })
        {
            var p = (await Packages.LoadAsync(db, (await db.ProcurementPackages.SingleAsync(x => x.Number == no)).Id))!;
            Assert.True((await PackageRisk.AssessAsync(db, p, s)).IsHigh, no);
        }
        Assert.Equal(3, await db.BoqItems.CountAsync(i => i.Norms != null));
        Assert.Equal(5, await db.VendorDocuments.CountAsync(d => d.Type == VendorDocumentType.Environmental || d.Type == VendorDocumentType.SocialResponsibility));
        var adj = await db.ContractPriceAdjustments.OrderBy(a => a.Number).ToListAsync();
        Assert.Equal(2, adj.Count); Assert.Equal(1.048m, adj[0].Factor);
        Assert.True(await db.Attachments.AnyAsync());
    }
}
