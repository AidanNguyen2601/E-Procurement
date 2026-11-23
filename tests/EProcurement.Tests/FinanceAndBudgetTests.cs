using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

public class FinanceTests
{
    [Theory]
    [InlineData("Thanh toán 45 ngày sau khi nhận hàng", 45)]
    [InlineData("Tạm ứng 30%, thanh toán 70% sau nghiệm thu", 30)]
    [InlineData(null, 30)]
    [InlineData("Thanh toán 999 ngày", 30)]
    public void DueDate_FromPaymentTerms(string? terms, int days) =>
        Assert.Equal(new DateTime(2026, 1, 1).AddDays(days), FinanceService.DueDate(new DateTime(2026, 1, 1), terms));

    private static (Web.Data.EProcDbContext Db, PurchaseOrder Po, PurchaseOrderLine Line) PoReceived(decimal ordered, decimal received, decimal price)
    {
        var db = TestDb.Create();
        var (u, v, _, _, laptop, _) = TestDb.Seed(db);
        var po = new PurchaseOrder { Number = "PO-1", VendorId = v.Id, BuyerId = u.Id, Status = PoStatus.PartiallyReceived, VatPercent = 10 };
        var line = new PurchaseOrderLine { ItemId = laptop.Id, Description = "Laptop", Quantity = ordered, UnitPrice = price, ReceivedQuantity = received };
        po.Lines.Add(line); po.Recalculate();
        db.PurchaseOrders.Add(po); db.SaveChanges();
        return (db, po, line);
    }

    private static Invoice Inv(PurchaseOrder po, PurchaseOrderLine l, decimal qty, decimal price, decimal? vat = null)
    {
        var i = new Invoice { Number = "HDV-1", VendorId = po.VendorId!.Value, PurchaseOrderId = po.Id, InvoiceNo = "1" };
        i.Lines.Add(new InvoiceLine { PurchaseOrderLineId = l.Id, Quantity = qty, UnitPrice = price, Amount = qty * price });
        i.SubTotal = qty * price; i.VatAmount = vat ?? Math.Round(i.SubTotal * 0.1m, 0); i.Total = i.SubTotal + i.VatAmount;
        return i;
    }

    [Fact]
    public async Task ThreeWayMatch_OkWhenWithinReceivedAndPrice()
    {
        var (db, po, l) = PoReceived(10, 6, 1_000_000);
        var m = await new FinanceService(db).MatchAsync(Inv(po, l, 6, 1_000_000), po);
        Assert.True(m.Ok, m.Summary);
    }

    [Fact]
    public async Task ThreeWayMatch_FlagsQuantityPriceAndVat()
    {
        var (db, po, l) = PoReceived(10, 6, 1_000_000);
        var m = await new FinanceService(db).MatchAsync(Inv(po, l, 8, 1_050_000, vat: 1), po);
        Assert.False(m.Ok);
        Assert.Contains("SL đã nhận", m.Summary);
        Assert.Contains("giá PO", m.Summary);
        Assert.Contains("Thuế GTGT", m.Summary);
    }

    [Fact]
    public async Task ThreeWayMatch_CountsEarlierInvoices()
    {
        var (db, po, l) = PoReceived(10, 6, 1_000_000);
        var first = Inv(po, l, 5, 1_000_000); first.Status = InvoiceStatus.Paid;
        db.Invoices.Add(first); db.SaveChanges();
        var second = Inv(po, l, 2, 1_000_000); second.Number = "HDV-2";
        var m = await new FinanceService(db).MatchAsync(second, po);
        Assert.False(m.Ok); // 5 + 2 > 6 đã nhận
    }

    [Fact]
    public async Task Proposal_AppliesLatePenaltyWithCapAndAdvanceDeduction()
    {
        var (db, po, l) = PoReceived(10, 10, 1_000_000);
        var c = new Contract { Number = "HD-1", Title = "HĐ", VendorId = po.VendorId!.Value, Value = 100_000_000, VatPercent = 10, LatePenaltyPercentPerDay = 0.5m, LatePenaltyCapPercent = 8, CreatedById = po.BuyerId };
        db.Contracts.Add(c); db.SaveChanges();
        po.ContractId = c.Id; po.DeliveryDate = new DateTime(2026, 1, 10); po.Status = PoStatus.Received;
        db.GoodsReceipts.Add(new GoodsReceipt { Number = "GRN-1", PurchaseOrderId = po.Id, ReceiptDate = new DateTime(2026, 1, 30), ReceivedById = po.BuyerId });
        db.Payments.Add(new Payment { Number = "TT-1", Type = PaymentType.Advance, VendorId = c.VendorId, ContractId = c.Id, Amount = 33_000_000, NetAmount = 33_000_000, Status = PaymentStatus.Paid, RequestedById = po.BuyerId });
        db.SaveChanges();
        var inv = Inv(po, l, 10, 1_000_000);
        var p = await new FinanceService(db).ProposeAsync(inv, po);
        Assert.Equal(20, p.LateDays);
        Assert.Equal(800_000, p.Penalty);               // 20 ngày × 0,5% = 10% → trần 8% × 10.000.000
        Assert.Equal(3_300_000, p.AdvanceDeduction);    // tạm ứng 30% gross × 11.000.000
        Assert.Equal(11_000_000 - 800_000 - 3_300_000, p.Net);
    }
}

public class BudgetTests
{
    [Fact]
    public async Task Budget_CategoryPoolThenDepartmentPool()
    {
        var db = TestDb.Create();
        var (u, v, it, _, laptop, paper) = TestDb.Seed(db);
        db.Budgets.AddRange(new Budget { Year = 2026, Department = "CNTT", CategoryId = it.Id, Amount = 100_000_000 },
                            new Budget { Year = 2026, Department = "CNTT", Amount = 10_000_000 });
        var pr = new PurchaseRequest { Number = "PR-1", Title = "x", Department = "CNTT", RequesterId = u.Id, Status = PrStatus.Approved, RequestDate = new DateTime(2026, 3, 1) };
        pr.Lines.Add(new PurchaseRequestLine { ItemId = laptop.Id, Description = "Laptop", Quantity = 2, EstimatedPrice = 20_000_000 });
        pr.Lines.Add(new PurchaseRequestLine { ItemId = paper.Id, Description = "Giấy", Quantity = 10, EstimatedPrice = 80_000 });
        pr.Recalculate();
        db.PurchaseRequests.Add(pr); db.SaveChanges();
        var st = await new BudgetService(db).StatusAsync(2026);
        Assert.Equal(40_000_000, st.Single(s => s.Budget.CategoryId == it.Id).Reserved);
        Assert.Equal(800_000, st.Single(s => s.Budget.CategoryId == null).Reserved); // giấy không có ngân sách riêng → vào "tất cả nhóm hàng"
    }

    [Fact]
    public async Task Budget_OrderedReplacesReservation_AndCheckDetectsOverrun()
    {
        var db = TestDb.Create();
        var (u, v, it, _, laptop, _) = TestDb.Seed(db);
        db.Budgets.Add(new Budget { Year = 2026, Department = "CNTT", Amount = 50_000_000 });
        var pr = new PurchaseRequest { Number = "PR-1", Title = "x", Department = "CNTT", RequesterId = u.Id, Status = PrStatus.Processing, RequestDate = new DateTime(2026, 3, 1) };
        pr.Lines.Add(new PurchaseRequestLine { ItemId = laptop.Id, Description = "Laptop", Quantity = 2, EstimatedPrice = 20_000_000 });
        pr.Recalculate(); db.PurchaseRequests.Add(pr); db.SaveChanges();
        var po = new PurchaseOrder { Number = "PO-1", VendorId = v.Id, BuyerId = u.Id, PurchaseRequestId = pr.Id, Status = PoStatus.Issued, OrderDate = new DateTime(2026, 3, 5) };
        po.Lines.Add(new PurchaseOrderLine { ItemId = laptop.Id, Description = "Laptop", Quantity = 1, UnitPrice = 19_000_000 });
        po.Recalculate(); db.PurchaseOrders.Add(po); db.SaveChanges();

        var svc = new BudgetService(db);
        var s = (await svc.StatusAsync(2026)).Single();
        Assert.Equal(19_000_000, s.Ordered);
        Assert.Equal(21_000_000, s.Reserved); // 40tr dự toán − 19tr đã đặt
        var next = new PurchaseRequest { Department = "CNTT", RequestDate = new DateTime(2026, 4, 1) };
        next.Lines.Add(new PurchaseRequestLine { ItemId = laptop.Id, Description = "Laptop", Quantity = 1, EstimatedPrice = 20_000_000, Amount = 20_000_000 });
        var check = await svc.CheckAsync(next);
        Assert.True(check.AnyOver); // còn 10tr < 20tr
        Assert.NotNull(BudgetService.Problem(check));
    }

    [Fact]
    public async Task Budget_YearWithoutBudgets_NotControlled()
    {
        var db = TestDb.Create();
        TestDb.Seed(db);
        var check = await new BudgetService(db).CheckAsync(new PurchaseRequest { Department = "CNTT", RequestDate = new DateTime(2030, 1, 1) });
        Assert.False(check.Active);
        Assert.Null(BudgetService.Problem(check));
    }
}

public class ApprovalMatrixTests
{
    [Fact]
    public async Task Limit_DepartmentOverridesGlobalOverridesSettings()
    {
        var db = TestDb.Create();
        var settings = TestDb.Settings(s => s.PrDirectorThreshold = 50_000_000);
        Assert.Equal(50_000_000, await new ApprovalMatrix(db, settings).PrDirectorAsync("CNTT"));
        db.ApprovalLimits.Add(new ApprovalLimit { Type = ApprovalLimitType.PrDirector, Amount = 80_000_000 });
        db.SaveChanges();
        Assert.Equal(80_000_000, await new ApprovalMatrix(db, settings).PrDirectorAsync("CNTT"));
        db.ApprovalLimits.Add(new ApprovalLimit { Type = ApprovalLimitType.PrDirector, Department = "CNTT", Amount = 10_000_000 });
        db.SaveChanges();
        Assert.Equal(10_000_000, await new ApprovalMatrix(db, settings).PrDirectorAsync("CNTT"));
        Assert.Equal(80_000_000, await new ApprovalMatrix(db, settings).PrDirectorAsync("Kế toán"));
    }
}
