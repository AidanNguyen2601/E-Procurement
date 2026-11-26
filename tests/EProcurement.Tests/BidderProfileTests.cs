using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

public class BidderProfileTests
{
    private static TenderRequirement Req(int id, QualificationKind k, int count = 0, decimal value = 0, int years = 0) =>
        new() { Id = id, Kind = k, Name = k.ToString(), MinCount = count, MinValue = value, MinYears = years, SortOrder = id };

    [Fact]
    public void SimilarContracts_CountsOnlyContractsAboveValue()
    {
        var r = Req(1, QualificationKind.SimilarContracts, 2, 5_000_000_000);
        var caps = new[] { new TenderBidderCapability { RequirementId = 1, Value = 6_000_000_000 }, new TenderBidderCapability { RequirementId = 1, Value = 3_000_000_000 } };
        Assert.False(BidderProfile.Check(r, caps).Pass);
        // liên danh: thành viên kê khai thêm 1 hợp đồng đạt → cộng gộp đủ 2
        Assert.True(BidderProfile.Check(r, caps.Append(new TenderBidderCapability { RequirementId = 1, PartnerId = 9, Value = 5_000_000_000 })).Pass);
    }

    [Fact]
    public void Personnel_Equipment_Revenue_AndSuggestion()
    {
        var t = new Tender { Requirements = { Req(1, QualificationKind.KeyPersonnel, 2, years: 5), Req(2, QualificationKind.Equipment, 3), Req(3, QualificationKind.AverageRevenue, value: 50_000_000_000), Req(4, QualificationKind.Other) } };
        var b = new TenderBidder
        {
            Capabilities =
            {
                new() { RequirementId = 1, Years = 7 }, new() { RequirementId = 1, Years = 3 },
                new() { RequirementId = 2, Count = 2 }, new() { RequirementId = 2, Count = 1 },
                new() { RequirementId = 3, Value = 60_000_000_000 }
            }
        };
        var res = BidderProfile.Check(t, b);
        Assert.False(res[0].Pass);  // chỉ 1 người ≥ 5 năm
        Assert.True(res[1].Pass);   // 3 thiết bị
        Assert.True(res[2].Pass);
        Assert.Null(res[3].Pass);   // yêu cầu khác: tổ chuyên gia tự đánh giá
        Assert.False(BidderProfile.Suggest(res));
        b.Capabilities.Add(new() { RequirementId = 1, Years = 10 });
        Assert.Null(BidderProfile.Suggest(BidderProfile.Check(t, b))); // còn yêu cầu đánh giá thủ công
    }

    [Fact]
    public void Partners_JointVentureRulesAndSubcontractCap()
    {
        var t = new Tender { AllowJointVenture = true, MaxSubcontractPercent = 20 };
        var b = new TenderBidder
        {
            VendorId = 1,
            Partners =
            {
                new() { Name = "A", VendorId = 1, Role = BidPartnerRole.Lead, SharePercent = 40 },
                new() { Name = "B", VendorId = 2, Role = BidPartnerRole.Member, SharePercent = 50 },
                new() { Name = "C", VendorId = 3, Role = BidPartnerRole.Subcontractor, SharePercent = 25 }
            }
        };
        var p = BidderProfile.PartnerProblems(t, b, new HashSet<int> { 3 });
        Assert.Equal(4, p.Count); // tổng 90%, đứng đầu ít hơn thành viên, thầu phụ 25% > 20%, C bị cấm
        Assert.True(BidderProfile.IsJointVenture(b));
        t.AllowJointVenture = false;
        Assert.Equal(5, BidderProfile.PartnerProblems(t, b, new HashSet<int> { 3 }).Count);
    }

    [Fact]
    public void DomesticPreference_ChangesRanking()
    {
        TenderBidder Bid(int id, decimal price, bool pref) => new()
        {
            Id = id, VendorId = id, Vendor = new Vendor { Name = "V" + id }, Submitted = true, Valid = true, Qualified = true, BidPrice = price, VatPercent = 0, DomesticPreferred = pref,
            Scores = { new TenderScore { TechnicalScore = 80 } }
        };
        var t = new Tender
        {
            EvaluationMethod = TenderEvaluationMethod.LowestEvaluatedPrice, MinTechnicalScore = 70, DomesticPreferencePercent = 7.5m,
            Committee = { new TenderCommitteeMember() },
            Bidders = { Bid(1, 100_000_000, false), Bid(2, 105_000_000, true) }
        };
        var res = TenderEvaluation.Evaluate(t);
        var foreign = res.Single(r => r.Bid.Id == 1);
        Assert.True(foreign.PreferenceApplied);
        Assert.Equal(107_500_000, foreign.RankingPrice);
        Assert.Equal(1, res.Single(r => r.Bid.Id == 2).Rank); // 105 tr < 107,5 tr sau ưu đãi
        t.DomesticPreferencePercent = 0;
        Assert.Equal(1, TenderEvaluation.Evaluate(t).Single(r => r.Bid.Id == 1).Rank);
    }

    [Fact]
    public void Norms_ComponentsAndRoundTrip()
    {
        var lines = new List<BoqNormLine>
        {
            new() { Kind = 1, Name = "Xi măng PCB40", Unit = "kg", Norm = 350.55m, Price = 1_650 },
            new() { Kind = 1, Name = "Cát vàng", Unit = "m3", Norm = 0.48m, Price = 420_000 },
            new() { Kind = 2, Name = "Nhân công 3,5/7", Unit = "công", Norm = 1.97m, Price = 285_000 },
            new() { Kind = 3, Name = "Máy trộn 250l", Unit = "ca", Norm = 0.095m, Price = 310_000 }
        };
        var (m, l, mc) = BoqNorms.Components(lines);
        Assert.Equal(578_408 + 201_600, m);
        Assert.Equal(561_450, l);
        Assert.Equal(29_450, mc);
        var back = BoqNorms.Parse(BoqNorms.Serialize(lines));
        Assert.Equal(4, back.Count); Assert.Equal(350.55m, back[0].Norm);
        Assert.NotEmpty(BoqNorms.Validate(new[] { new BoqNormLine { Kind = 1, Name = "", Norm = 1 } }));
    }
}
