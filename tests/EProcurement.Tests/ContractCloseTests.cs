using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

public class LegalTimelineTests
{
    private static readonly LegalTimelineSettings S = new();

    [Theory]
    [InlineData("Xây lắp", 15_000_000_000, false, 9)]        // xây lắp ≤ 20 tỷ: gói nhỏ
    [InlineData("Xây lắp", 25_000_000_000, false, 18)]
    [InlineData("Xây lắp", 25_000_000_000, true, 35)]
    [InlineData("Hàng hóa", 10_000_000_000, false, 9)]       // hàng hóa ≤ 10 tỷ
    [InlineData("Hàng hóa", 10_000_000_001, false, 18)]
    [InlineData("Dịch vụ phi tư vấn", 5_000_000_000, true, 18)]
    [InlineData("Tư vấn thiết kế", 1_000_000_000, false, 18)] // tư vấn: không có mức rút gọn
    [InlineData(null, 2_000_000_000, false, 9)]
    public void MinBidding_ByFieldScaleAndScope(string? field, decimal budget, bool intl, int days) =>
        Assert.Equal(days, LegalTimelines.MinBidding(budget, field, intl, S).Days);

    [Fact]
    public void Amendment_SmallPackage_UsesWorkingDays()
    {
        var small = LegalTimelines.MinAmendment(5_000_000_000, "Hàng hóa", S);
        Assert.True(small.WorkingDays); Assert.Equal(3, small.Days);
        // đóng thầu thứ Hai 2026-12-07 → hạn sửa đổi muộn nhất thứ Tư 2026-12-02 (lùi 3 ngày làm việc)
        Assert.Equal(new DateTime(2026, 12, 2), small.LatestBefore(new DateTime(2026, 12, 7, 9, 0, 0)));
        var big = LegalTimelines.MinAmendment(50_000_000_000, "Hàng hóa", S);
        Assert.False(big.WorkingDays); Assert.Equal(10, big.Days);
    }

    [Fact]
    public void WorkingDays_SkipWeekend()
    {
        Assert.Equal(new DateTime(2026, 12, 4), LegalTimelines.AddWorkingDays(new DateTime(2026, 11, 27), 5)); // thứ Sáu + 5 ngày làm việc
        Assert.Equal(5, LegalTimelines.Between(new DateTime(2026, 11, 27), new DateTime(2026, 12, 4), true));
    }

    [Fact]
    public void CheckTender_FlagsShortBiddingAndLateAmendment()
    {
        var t = new Tender { PackageBudget = 30_000_000_000, PublishedAt = new DateTime(2026, 11, 2), ClosingAt = new DateTime(2026, 11, 15, 9, 0, 0), BidValidityDays = 90 };
        var amend = new TenderClarification { IsAmendment = true, CreatedAt = new DateTime(2026, 11, 10), Answer = "x" };
        var res = LegalTimelines.CheckTender(t, "Hàng hóa", new[] { amend }, S, new DateTime(2026, 11, 12));
        Assert.False(res[0].Ok);   // 13 ngày < 18
        Assert.False(res[1].Ok);   // sửa đổi cách 5 ngày < 10
        t.ClosingAt = new DateTime(2026, 11, 25, 9, 0, 0);
        res = LegalTimelines.CheckTender(t, "Hàng hóa", new[] { amend }, S, new DateTime(2026, 11, 12));
        Assert.True(res[0].Ok); Assert.True(res[1].Ok);
    }
}

public class ContractCloseTests
{
    private static Contract C(decimal value = 1_000_000_000, decimal ret = 0, decimal start = 0, decimal end = 100) =>
        new() { Id = 1, Value = value, VatPercent = 0, RetentionPercent = ret, RetentionCapPercent = 5, AdvanceRecoveryStartPercent = start, AdvanceRecoveryEndPercent = end };

    private static ContractLedger L(decimal work, decimal advPaid, decimal recovered = 0, decimal retained = 0) =>
        new() { ContractGross = 1_000_000_000, WorkGross = work, AdvancePaid = advPaid, AdvanceRecovered = recovered, Retained = retained };

    [Fact]
    public void Advance_ProRata_ByDefault()
    {
        // tạm ứng 20%, đợt 300 tr → thu hồi 20% × 300 = 60 tr (giống cách tính cũ)
        var d = ContractClose.Deductions(C(), 300_000_000, L(0, 200_000_000));
        Assert.Equal(60_000_000, d.Advance); Assert.Equal(0, d.Retention);
    }

    [Fact]
    public void Advance_RecoveredBetween10And80Percent()
    {
        var c = C(start: 10, end: 80);
        // lũy kế 0 → 30%: phần trong khoảng 10–30 = 20/70 × 200 tr
        Assert.Equal(57_142_857, ContractClose.Deductions(c, 300_000_000, L(0, 200_000_000)).Advance);
        // chưa tới 10%: không thu hồi
        Assert.Equal(0, ContractClose.Deductions(c, 50_000_000, L(0, 200_000_000)).Advance);
        // vượt 80%: thu hồi hết phần còn lại
        Assert.Equal(120_000_000, ContractClose.Deductions(c, 300_000_000, L(600_000_000, 200_000_000, 80_000_000)).Advance);
    }

    [Fact]
    public void Retention_CappedByContractPercent()
    {
        var c = C(ret: 10); // giữ lại 10% mỗi đợt, tối đa 5% HĐ = 50 tr
        Assert.Equal(30_000_000, ContractClose.Deductions(c, 300_000_000, L(0, 0)).Retention);
        Assert.Equal(20_000_000, ContractClose.Deductions(c, 300_000_000, L(300_000_000, 0, 0, 30_000_000)).Retention);
        Assert.Equal(0, ContractClose.Deductions(c, 300_000_000, L(600_000_000, 0, 0, 50_000_000)).Retention);
    }

    [Fact]
    public void PriceFactor_FixedShareAndIndices()
    {
        var idx = new List<ContractPriceIndex> { new() { Id = 1, Name = "Thép", Weight = 0.3m, BaseIndex = 100 }, new() { Id = 2, Name = "Nhân công", Weight = 0.2m, BaseIndex = 200 } };
        Assert.Equal(0.5m, ContractClose.FixedShare(idx));
        // a 0.5 + 0.3 × 110/100 + 0.2 × 210/200 = 0.5 + 0.33 + 0.21 = 1.04
        Assert.Equal(1.04m, ContractClose.Factor(idx, new Dictionary<int, decimal> { [1] = 110, [2] = 210 }));
        Assert.Single(ContractClose.ValidateIndices(new List<ContractPriceIndex> { new() { Name = "A", Weight = 0.7m, BaseIndex = 1 }, new() { Name = "B", Weight = 0.4m, BaseIndex = 1 } }));
    }

    [Fact]
    public async Task Settlement_BalanceEqualsRetentionWhenAllWorkPaid()
    {
        var db = TestDb.Create();
        var (u, v, _, _, _, _) = TestDb.Seed(db);
        var c = new Contract { Number = "HD-1", Title = "HĐ", VendorId = v.Id, Value = 100_000_000, VatPercent = 0, RetentionPercent = 5, CreatedById = u.Id };
        db.Contracts.Add(c); db.SaveChanges();
        db.Payments.AddRange(
            new Payment { Number = "TT-1", Type = PaymentType.Advance, VendorId = v.Id, ContractId = c.Id, Amount = 20_000_000, NetAmount = 20_000_000, Status = PaymentStatus.Paid },
            new Payment { Number = "TT-2", Type = PaymentType.Milestone, VendorId = v.Id, ContractId = c.Id, Amount = 100_000_000, PenaltyAmount = 2_000_000, AdvanceDeduction = 20_000_000, RetentionAmount = 5_000_000, NetAmount = 73_000_000, Status = PaymentStatus.Paid });
        db.SaveChanges();
        var s = await ContractClose.DraftAsync(db, c, null, 0);
        Assert.Equal(100_000_000, s.FinalGross);
        Assert.Equal(93_000_000, s.PaidTotal);
        Assert.Equal(5_000_000, s.Balance);          // 100 − 2 phạt − 93 đã chi = 5 = tiền giữ lại
        Assert.Equal(5_000_000, s.RetentionHeld);
        Assert.Equal(0, s.PayNow);
        var l = await ContractClose.LedgerAsync(db, c);
        c.Status = ContractStatus.Completed;
        Assert.NotNull(ContractClose.RetentionReleaseProblem(c, l, Array.Empty<ContractGuarantee>(), DateTime.Today));
        c.WarrantyEndDate = DateTime.Today.AddDays(-1);
        Assert.Null(ContractClose.RetentionReleaseProblem(c, l, Array.Empty<ContractGuarantee>(), DateTime.Today));
    }
}
