using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

/// <summary>Quản lý nguồn cung: sơ tuyển, xếp loại NCC, ngân hàng giá, bảng giá tiêu chuẩn</summary>
public class SourcingTests
{
    // ---------------------------------------------------------------- sơ tuyển
    private static Prequalification Round(int minScore = 60)
    {
        var p = new Prequalification { MinScore = minScore };
        p.Criteria.Add(new PrequalificationCriterion { Id = 1, Name = "Pháp lý", IsMandatory = true });
        p.Criteria.Add(new PrequalificationCriterion { Id = 2, Name = "Kinh nghiệm", MaxScore = 40 });
        p.Criteria.Add(new PrequalificationCriterion { Id = 3, Name = "Chất lượng", MaxScore = 60 });
        return p;
    }

    [Fact]
    public void Prequal_NotEvaluatedUntilAllCriteriaScored() =>
        Assert.Null(PrequalificationRules.Evaluate(Round(), new[] { new PrequalificationScore { CriterionId = 1, Pass = true }, new PrequalificationScore { CriterionId = 2, Score = 30 } }));

    [Fact]
    public void Prequal_PassesWhenMandatoryOkAndScoreAboveMinimum()
    {
        var r = PrequalificationRules.Evaluate(Round(), new[] { new PrequalificationScore { CriterionId = 1, Pass = true },
            new PrequalificationScore { CriterionId = 2, Score = 30 }, new PrequalificationScore { CriterionId = 3, Score = 40 } });
        Assert.Equal((true, 70m), r);
    }

    [Fact]
    public void Prequal_FailsOnMandatoryEvenWithFullScore()
    {
        var r = PrequalificationRules.Evaluate(Round(), new[] { new PrequalificationScore { CriterionId = 1, Pass = false },
            new PrequalificationScore { CriterionId = 2, Score = 40 }, new PrequalificationScore { CriterionId = 3, Score = 60 } });
        Assert.False(r!.Value.Passed);
        Assert.Equal(100, r.Value.Total);
    }

    [Fact]
    public void Prequal_ScoreCappedAtMax_AndBelowMinimumFails()
    {
        var r = PrequalificationRules.Evaluate(Round(minScore: 80), new[] { new PrequalificationScore { CriterionId = 1, Pass = true },
            new PrequalificationScore { CriterionId = 2, Score = 99 }, new PrequalificationScore { CriterionId = 3, Score = 30 } });
        Assert.Equal((false, 70m), r); // 40 (cap) + 30 = 70 < 80
    }

    [Fact]
    public void Prequal_OnlyMandatoryCriteria_PassWhenAllPass()
    {
        var p = new Prequalification { MinScore = 60 };
        p.Criteria.Add(new PrequalificationCriterion { Id = 1, IsMandatory = true });
        Assert.Equal((true, 100m), PrequalificationRules.Evaluate(p, new[] { new PrequalificationScore { CriterionId = 1, Pass = true } }));
    }

    // ---------------------------------------------------------------- xếp loại NCC
    [Theory]
    [InlineData(4.6, "A")] [InlineData(4.5, "A")] [InlineData(3.5, "B")] [InlineData(3.49, "C")] [InlineData(2.4, "D")]
    public void Grade_Thresholds(double score, string grade) => Assert.Equal(grade, SupplierScorecard.Grade((decimal)score));

    [Fact]
    public void Grade_NoData() => Assert.Equal("—", SupplierScorecard.Grade(null));

    [Theory]
    [InlineData(100, 5)] [InlineData(90, 4)] [InlineData(70, 3)] [InlineData(55, 2)] [InlineData(10, 1)]
    public void DeliveryScore_FromOnTimePercent(int pct, int score) => Assert.Equal(score, SupplierScorecard.DeliveryScore(pct));

    [Theory]
    [InlineData(0, 5)] [InlineData(1.5, 4)] [InlineData(5, 3)] [InlineData(8, 2)] [InlineData(20, 1)]
    public void QualityScore_FromRejectPercent(double pct, int score) => Assert.Equal(score, SupplierScorecard.QualityScore((decimal)pct));

    [Fact]
    public async Task Scorecard_ComputesOnTimeAndRejectRate()
    {
        var db = TestDb.Create();
        var (u, v, _, _, laptop, _) = TestDb.Seed(db);
        PurchaseOrder Po(string no, DateTime delivery) => new() { Number = no, VendorId = v.Id, BuyerId = u.Id, Status = PoStatus.Received, OrderDate = DateTime.Today.AddDays(-30), DeliveryDate = delivery };
        var onTime = Po("PO-1", DateTime.Today.AddDays(-10)); var late = Po("PO-2", DateTime.Today.AddDays(-20));
        onTime.Lines.Add(new PurchaseOrderLine { ItemId = laptop.Id, Description = "L", Quantity = 10, UnitPrice = 1 });
        late.Lines.Add(new PurchaseOrderLine { ItemId = laptop.Id, Description = "L", Quantity = 10, UnitPrice = 1 });
        db.AddRange(onTime, late); db.SaveChanges();
        GoodsReceipt Gr(string no, PurchaseOrder po, DateTime d, decimal acc, decimal rej)
        {
            var g = new GoodsReceipt { Number = no, PurchaseOrderId = po.Id, ReceiptDate = d, ReceivedById = u.Id };
            g.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = po.Lines.First().Id, QuantityAccepted = acc, QuantityRejected = rej });
            return g;
        }
        db.AddRange(Gr("G1", onTime, DateTime.Today.AddDays(-12), 10, 0), Gr("G2", late, DateTime.Today.AddDays(-15), 9, 1));
        db.SaveChanges();
        var s = Assert.Single(await SupplierScorecard.BuildAsync(db, DateTime.Today.AddMonths(-3), DateTime.Today));
        Assert.Equal(2, s.Orders);
        Assert.Equal(50, s.OnTimePercent);
        Assert.Equal(5, s.RejectPercent);
        Assert.Equal(2.5m, s.Score); // (giao hàng 2 + chất lượng 3) / 2, chưa có phiếu đánh giá
        Assert.Equal("C", s.Grade);
    }

    // ---------------------------------------------------------------- ngân hàng giá & giá tiêu chuẩn
    [Fact]
    public void PriceStats_MedianAndLast()
    {
        var obs = new[] { 100m, 300m, 200m, 400m }.Select((p, i) => new PriceObservation(1, DateTime.Today.AddDays(-i), p, 1, null, "x", null, null)).ToList();
        var s = PriceStats.From(1, obs)!;
        Assert.Equal(250, s.Median);
        Assert.Equal(100, s.Last); // ngày gần nhất
        Assert.Equal(100, s.Min); Assert.Equal(400, s.Max); Assert.Equal(250, s.Avg);
        Assert.Null(PriceStats.From(1, new List<PriceObservation>()));
    }

    [Fact]
    public void StandardPrice_SuggestAndCeiling()
    {
        var s = new PriceStats(1, 3, 1, 1, 21_450_600, 1, 1, DateTime.Today);
        Assert.Equal(21_451_000, StandardPrices.Suggest(s, 5));
        Assert.Equal(5, StandardPrices.Suggest(null, 5));
        Assert.Equal(23_597_000, StandardPrices.DefaultCeiling(21_451_000, 10)); // 23.596.100 → làm tròn lên nghìn
    }

    [Fact]
    public async Task StandardPrice_Current_UsesLatestApprovedEffectiveList()
    {
        var db = TestDb.Create();
        var (u, _, _, _, laptop, paper) = TestDb.Seed(db);
        StandardPriceList L(string no, DateTime from, DateTime? to, StandardPriceStatus st, decimal price)
        {
            var l = new StandardPriceList { Number = no, Title = no, EffectiveFrom = from, EffectiveTo = to, Status = st, CreatedById = u.Id };
            l.Lines.Add(new StandardPriceLine { ItemId = laptop.Id, StandardPrice = price, CeilingPrice = price + 1000 });
            return l;
        }
        db.AddRange(L("BG-1", DateTime.Today.AddMonths(-6), null, StandardPriceStatus.Approved, 20_000_000),
                    L("BG-2", DateTime.Today.AddMonths(-1), null, StandardPriceStatus.Approved, 21_000_000),
                    L("BG-3", DateTime.Today.AddDays(10), null, StandardPriceStatus.Approved, 30_000_000),   // chưa tới ngày
                    L("BG-4", DateTime.Today.AddDays(-1), null, StandardPriceStatus.Submitted, 1),           // chưa duyệt
                    L("BG-5", DateTime.Today.AddMonths(-12), DateTime.Today.AddMonths(-7), StandardPriceStatus.Approved, 9)); // hết hạn
        db.SaveChanges();
        var cur = await StandardPrices.CurrentAsync(db, null, DateTime.Today);
        Assert.Equal(21_000_000, cur[laptop.Id].Price);
        Assert.Equal("BG-2", cur[laptop.Id].ListNumber);
        Assert.False(cur.ContainsKey(paper.Id));
        Assert.Equal(30_000_000, (await StandardPrices.CurrentAsync(db, null, DateTime.Today.AddDays(11)))[laptop.Id].Price);

        Assert.Equal(1, await StandardPrices.SyncReferenceAsync(db));
        Assert.Equal(21_000_000, db.Items.First(i => i.Id == laptop.Id).ReferencePrice);
    }

    [Fact]
    public void Exceeding_OnlyLinesAboveCeiling()
    {
        var std = new Dictionary<int, StandardPrice> { [1] = new(1, 100, 110, "BG-1", 1) };
        var over = StandardPrices.Exceeding(new (int?, string, decimal)[] { (1, "A", 110), (1, "B", 111), (2, "C", 999), (null, "D", 999) }, std);
        Assert.Equal("B", Assert.Single(over).Description);
    }
}
