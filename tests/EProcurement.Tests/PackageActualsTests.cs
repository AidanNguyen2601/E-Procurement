using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

/// <summary>Dự toán – giá chọn – thực tế theo gói mua sắm cho các hình thức không qua đấu thầu</summary>
public class PackageActualsTests
{
    private record Ctx(EProcDbContext Db, AppUser Buyer, Vendor Vendor, Item Laptop, Item Paper);

    private static Ctx Setup()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, laptop, paper) = TestDb.Seed(db);
        return new Ctx(db, buyer, vendor, laptop, paper);
    }

    /// <summary>Gói có 1 đề nghị: 10 laptop × 20tr + 100 ram giấy × 70k (dự toán 207tr)</summary>
    private static (ProcurementPackage Pkg, PurchaseRequest Pr) Package(Ctx c, ProcurementMethod method)
    {
        var pr = new PurchaseRequest { Number = "PR-" + method, Title = "Mua thiết bị", RequesterId = c.Buyer.Id, Department = "Phòng Mua hàng", Status = PrStatus.Processing, Method = method, EstimatedTotal = 207_000_000 };
        pr.Lines.Add(new PurchaseRequestLine { ItemId = c.Laptop.Id, Description = "Laptop", Unit = "Cái", Quantity = 10, EstimatedPrice = 20_000_000, Amount = 200_000_000, SortOrder = 1 });
        pr.Lines.Add(new PurchaseRequestLine { ItemId = c.Paper.Id, Description = "Giấy A4", Unit = "Ram", Quantity = 100, EstimatedPrice = 70_000, Amount = 7_000_000, SortOrder = 2 });
        var p = new ProcurementPackage { Number = "GM-" + method, Title = "Gói " + method, BuyerId = c.Buyer.Id, Status = PackageStatus.Executing, Method = method, Budget = 210_000_000, Field = "Hàng hóa" };
        p.PurchaseRequests.Add(pr);
        c.Db.ProcurementPackages.Add(p); c.Db.SaveChanges();
        return (p, pr);
    }

    /// <summary>Đơn hàng: laptop 19tr, giấy 65k; nhận đạt 6 laptop + 100 giấy; hóa đơn đã thanh toán</summary>
    private static PurchaseOrder Order(Ctx c, ProcurementPackage p, PurchaseRequest pr, decimal laptopPrice = 19_000_000)
    {
        var po = new PurchaseOrder { Number = "PO-" + p.Number, VendorId = c.Vendor.Id, BuyerId = c.Buyer.Id, PurchaseRequestId = pr.Id, PackageId = p.Id, Status = PoStatus.PartiallyReceived, OrderDate = DateTime.Today.AddDays(-20), DeliveryDate = DateTime.Today };
        po.Lines.Add(new PurchaseOrderLine { ItemId = c.Laptop.Id, Description = "Laptop", Unit = "Cái", Quantity = 10, UnitPrice = laptopPrice, SortOrder = 1 });
        po.Lines.Add(new PurchaseOrderLine { ItemId = c.Paper.Id, Description = "Giấy A4", Unit = "Ram", Quantity = 100, UnitPrice = 65_000, SortOrder = 2 });
        po.Recalculate();
        c.Db.PurchaseOrders.Add(po); c.Db.SaveChanges();
        var gr = new GoodsReceipt { Number = "PN-" + p.Number, PurchaseOrderId = po.Id, ReceivedById = c.Buyer.Id };
        gr.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = po.Lines.First(l => l.ItemId == c.Laptop.Id).Id, QuantityAccepted = 6 });
        gr.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = po.Lines.First(l => l.ItemId == c.Paper.Id).Id, QuantityAccepted = 100 });
        c.Db.GoodsReceipts.Add(gr);
        var inv = new Invoice { Number = "HD-" + p.Number, VendorId = c.Vendor.Id, PurchaseOrderId = po.Id, InvoiceNo = "0001" + p.Id, Series = "1C26", Status = InvoiceStatus.Paid };
        c.Db.Invoices.Add(inv); c.Db.SaveChanges();
        c.Db.Payments.Add(new Payment { Number = "TT-" + p.Number, Type = PaymentType.Invoice, VendorId = c.Vendor.Id, InvoiceId = inv.Id, Amount = 50_000_000, NetAmount = 50_000_000, Status = PaymentStatus.Paid, RequestedById = c.Buyer.Id, PaidAt = DateTime.Today });
        c.Db.SaveChanges();
        return po;
    }

    [Fact]
    public async Task Competitive_quotation_uses_the_winning_quote_and_orders_for_actuals()
    {
        var c = Setup();
        var (p, pr) = Package(c, ProcurementMethod.CompetitiveQuotation);
        var rfq = new Rfq { Number = "RFQ-1", Title = "Chào giá", PurchaseRequestId = pr.Id, PackageId = p.Id, BuyerId = c.Buyer.Id, Status = RfqStatus.Awarded, AwardedVendorId = c.Vendor.Id };
        rfq.Vendors.Add(new RfqVendor { VendorId = c.Vendor.Id, Responded = true, TotalAmount = 196_500_000 });
        c.Db.Rfqs.Add(rfq); c.Db.SaveChanges();
        Order(c, p, pr);
        var v = (await new PackageActualsService(c.Db).BuildAsync(p.Id))!;
        Assert.True(v.HasSelection); Assert.True(v.HasCommitment);
        Assert.Equal("Giá báo giá được chọn", v.SelectedLabel); Assert.Equal(rfq.Id, v.RfqId);
        Assert.Equal(207_000_000m, v.Result.EstTotal);
        Assert.Equal(196_500_000m, v.Result.AwardPrice); Assert.Equal(-10_500_000m, v.SelectedVsEstimate);
        Assert.Equal(10 * 19_000_000m + 100 * 65_000m, v.Result.ContractCurrent);
        Assert.Equal(6 * 19_000_000m + 100 * 65_000m, v.Result.ActualTotal);
        Assert.Equal(6 * 20_000_000m + 100 * 70_000m, v.Result.EstForActualTotal);
        Assert.Equal(50_000_000m, v.Result.PaidNet);
        Assert.All(v.Result.Rows, r => Assert.False(r.Added));        // dòng đơn hàng khớp dòng dự toán theo hàng hóa
    }

    [Fact]
    public async Task Direct_award_uses_negotiated_price()
    {
        var c = Setup();
        var (p, pr) = Package(c, ProcurementMethod.DirectAward);
        c.Db.DirectAwards.Add(new DirectAward { Number = "CD-1", Title = "Chỉ định", PurchaseRequestId = pr.Id, PackageId = p.Id, VendorId = c.Vendor.Id, BuyerId = c.Buyer.Id, Justification = "Độc quyền", Status = DirectAwardStatus.Approved, ProposedTotal = 215_000_000, NegotiatedTotal = 205_000_000 });
        c.Db.SaveChanges();
        var v = (await new PackageActualsService(c.Db).BuildAsync(p.Id))!;
        Assert.True(v.HasSelection); Assert.False(v.HasCommitment);       // chưa đặt hàng
        Assert.Equal("Giá chỉ định sau thương thảo", v.SelectedLabel);
        Assert.Equal(205_000_000m, v.Result.AwardPrice); Assert.Equal(-2_000_000m, v.SelectedVsEstimate);
        Assert.Equal(0m, v.Result.ActualTotal);
    }

    [Fact]
    public async Task Direct_purchase_uses_the_order_value_as_selected_price()
    {
        var c = Setup();
        var (p, pr) = Package(c, ProcurementMethod.DirectPurchase);
        var po = Order(c, p, pr, laptopPrice: 21_000_000);                 // mua đắt hơn dự toán
        var v = (await new PackageActualsService(c.Db).BuildAsync(p.Id))!;
        Assert.Equal("Giá đơn hàng", v.SelectedLabel);
        Assert.Equal(po.SubTotal, v.Result.AwardPrice);
        Assert.True(v.SelectedVsEstimate > 0);
        Assert.True(v.Result.ActualTotal > v.Result.EstForActualTotal);
    }

    [Fact]
    public async Task Package_without_selection_shows_estimate_only_and_is_left_out_of_the_summary()
    {
        var c = Setup();
        var (p, _) = Package(c, ProcurementMethod.CompetitiveQuotation);
        var v = (await new PackageActualsService(c.Db).BuildAsync(p.Id))!;
        Assert.False(v.HasSelection); Assert.False(v.HasCommitment);
        Assert.Equal(207_000_000m, v.Result.EstTotal); Assert.Equal(2, v.Result.Rows.Count);
        Assert.Empty(await new PackageActualsService(c.Db).SummaryAsync(null, null));
    }

    [Fact]
    public async Task Summary_filters_by_method_and_field()
    {
        var c = Setup();
        var (p1, pr1) = Package(c, ProcurementMethod.DirectPurchase); Order(c, p1, pr1);
        var (p2, pr2) = Package(c, ProcurementMethod.CompetitiveQuotation); Order(c, p2, pr2);
        var svc = new PackageActualsService(c.Db);
        Assert.Equal(2, (await svc.SummaryAsync(null, null)).Count);
        Assert.Single(await svc.SummaryAsync(ProcurementMethod.DirectPurchase, null));
        Assert.Equal(2, (await svc.SummaryAsync(null, "Hàng hóa")).Count);
        Assert.Empty(await svc.SummaryAsync(null, "Xây lắp"));
    }

    [Fact]
    public void Orders_are_aggregated_by_item_with_weighted_unit_price()
    {
        var o1 = new PurchaseOrder(); o1.Lines.Add(new PurchaseOrderLine { Id = 1, ItemId = 5, Description = "Laptop", Quantity = 4, UnitPrice = 20_000_000, SortOrder = 1 });
        var o2 = new PurchaseOrder(); o2.Lines.Add(new PurchaseOrderLine { Id = 2, ItemId = 5, Description = "Laptop 14\"", Quantity = 6, UnitPrice = 18_000_000, SortOrder = 1 });
        o2.Lines.Add(new PurchaseOrderLine { Id = 3, Description = "Lắp đặt", Quantity = 1, UnitPrice = 500_000, SortOrder = 2 });
        var (c, map) = PackageActualsService.AggregateOrders(new[] { o1, o2 });
        Assert.Equal(2, c.Lines.Count);
        var lap = c.Lines.First(l => l.ItemId == 5);
        Assert.Equal(10m, lap.Quantity); Assert.Equal(18_800_000m, lap.UnitPrice);   // (4×20 + 6×18) / 10
        Assert.Equal(map[1], map[2]); Assert.NotEqual(map[1], map[3]);
    }

    [Fact]
    public void Estimate_lines_prefer_boq_leaves_over_requisition_lines()
    {
        var p = new ProcurementPackage();
        var pr = new PurchaseRequest(); pr.Lines.Add(new PurchaseRequestLine { Description = "Xây dựng", Quantity = 1, EstimatedPrice = 1_000 });
        p.PurchaseRequests.Add(pr);
        Assert.Single(PackageActualsService.EstimateLines(p, new List<BoqItem>()));
        var boq = new List<BoqItem> { new() { Id = 1, Name = "Móng", SortOrder = 1 }, new() { Id = 2, ParentId = 1, Name = "Bê tông", Quantity = 10, UnitPrice = 100, SortOrder = 1 }, new() { Id = 3, ParentId = 1, Name = "Thép", Quantity = 2, UnitPrice = 50, SortOrder = 2 } };
        var est = PackageActualsService.EstimateLines(p, boq);
        Assert.Equal(new[] { "1.1", "1.2" }, est.Select(e => e.BoqCode));
        Assert.All(est, e => Assert.True(e.Id < 0));
    }
}
