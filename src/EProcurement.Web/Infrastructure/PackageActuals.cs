using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Dự toán – giá chọn – hợp đồng / đơn hàng – thực tế của một gói mua sắm (mọi hình thức)</summary>
public class PackageActualsView
{
    public ProcurementPackage Package { get; set; } = null!;
    public BoqActualsResult Result { get; set; } = new();
    /// <summary>Đã chọn được nhà cung cấp (có kết quả lựa chọn hoặc đã đặt hàng)</summary>
    public bool HasSelection { get; set; }
    /// <summary>Nhãn giá chọn theo hình thức: giá trúng thầu / báo giá được chọn / giá chỉ định sau thương thảo / giá đơn hàng</summary>
    public string SelectedLabel { get; set; } = "";
    public string? Vendor { get; set; }
    /// <summary>Có cam kết (hợp đồng hoặc đơn hàng) để tính thực tế</summary>
    public bool HasCommitment { get; set; }
    /// <summary>"Giá hợp đồng hiện hành" hoặc "Giá trị đơn hàng"</summary>
    public string CommitmentLabel { get; set; } = "";
    public Contract? Contract { get; set; }
    public Tender? Tender { get; set; }
    public int? RfqId { get; set; }
    public int? DirectAwardId { get; set; }
    public List<PurchaseOrder> Orders { get; set; } = new();
    public decimal SelectedVsEstimate => Result.AwardPrice - Result.EstTotal;
    public decimal? SelectedVsEstimatePct => Result.EstTotal > 0 && Result.AwardPrice > 0 ? Math.Round((Result.AwardPrice / Result.EstTotal - 1) * 100, 1) : null;
}

/// <summary>
/// Tính dự toán – thực tế theo gói mua sắm:
/// đấu thầu → dòng gói thầu + hợp đồng (nghiệm thu khối lượng / nhận hàng); các hình thức khác → dòng dự toán (BOQ hoặc đề nghị mua hàng)
/// + các đơn mua hàng của gói (hàng đã nhận đạt × đơn giá đơn hàng, thanh toán theo hóa đơn).
/// </summary>
public class PackageActualsService
{
    private readonly EProcDbContext _db;
    public PackageActualsService(EProcDbContext db) => _db = db;

    /// <summary>Dòng dự toán của gói khi không có gói thầu: lá BOQ nếu có, không thì dòng các đề nghị mua hàng</summary>
    public static List<TenderLine> EstimateLines(ProcurementPackage p, IReadOnlyCollection<BoqItem> boq)
    {
        var leaves = Boq.Leaves(boq);
        var s = 0;
        if (leaves.Count > 0)
            return leaves.Select(l => new TenderLine { Id = -(++s), Description = l.Node.Item.Name, Unit = l.Node.Item.Unit, Quantity = l.Node.Item.Quantity, EstimatedPrice = l.Node.Item.UnitPrice, BoqCode = l.Node.Code, BoqPath = l.Path, SortOrder = s }).ToList();
        return p.PurchaseRequests.OrderBy(r => r.Id).SelectMany(r => r.Lines.OrderBy(l => l.SortOrder))
            .Select(l => new TenderLine { Id = -(++s), ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice, SortOrder = s }).ToList();
    }

    /// <summary>Gộp các dòng đơn hàng của gói thành "dòng cam kết" (theo hàng hóa / mô tả): khối lượng cộng dồn, đơn giá bình quân gia quyền</summary>
    public static (Contract Synthetic, Dictionary<int, int> PoLineToSynthetic) AggregateOrders(IEnumerable<PurchaseOrder> orders)
    {
        var c = new Contract { Id = 0 };
        var map = new Dictionary<int, int>();
        var groups = orders.SelectMany(o => o.Lines).GroupBy(l => l.ItemId.HasValue ? "i" + l.ItemId : "d" + l.Description.Trim().ToLowerInvariant());
        var id = 0;
        foreach (var g in groups.OrderBy(g => g.Min(l => l.SortOrder)))
        {
            id++;
            var first = g.First(); var qty = g.Sum(l => l.Quantity); var amount = g.Sum(l => l.Quantity * l.UnitPrice);
            c.Lines.Add(new ContractLine { Id = id, ItemId = first.ItemId, Description = first.Description, Unit = first.Unit, Quantity = qty, UnitPrice = qty > 0 ? Math.Round(amount / qty, 0) : first.UnitPrice, SortOrder = id });
            foreach (var l in g) map[l.Id] = id;
        }
        c.Value = c.Lines.Sum(l => Math.Round(l.Quantity * l.UnitPrice, 0));
        return (c, map);
    }

    private async Task<Dictionary<int, decimal>> ContractActualQuantitiesAsync(Contract c)
    {
        var accs = await _db.BoqAcceptances.AsNoTracking().Include(a => a.Lines).Where(a => a.ContractId == c.Id && a.Status == AcceptanceStatus.Confirmed).ToListAsync();
        var cum = BoqAcceptances.Cumulative(accs);
        if (cum.Count > 0 || c.Lines.Any(l => l.BoqCode != null)) return cum;
        var recv = await _db.GoodsReceiptLines.AsNoTracking().Include(g => g.PurchaseOrderLine).Include(g => g.GoodsReceipt).ThenInclude(g => g!.PurchaseOrder)
            .Where(g => g.GoodsReceipt!.PurchaseOrder!.ContractId == c.Id && g.GoodsReceipt.PurchaseOrder.Status != PoStatus.Cancelled).ToListAsync();
        var map = new Dictionary<int, decimal>();
        foreach (var r in recv)
        {
            var pl = r.PurchaseOrderLine!;
            var cl = c.Lines.FirstOrDefault(l => (pl.ItemId != null && l.ItemId == pl.ItemId) || l.Description == pl.Description);
            if (cl != null) map[cl.Id] = map.GetValueOrDefault(cl.Id) + r.QuantityAccepted;
        }
        return map;
    }

    public async Task<PackageActualsView?> BuildAsync(int packageId)
    {
        var p = await _db.ProcurementPackages.AsNoTracking().Include(x => x.PurchaseRequests).ThenInclude(r => r.Lines).Include(x => x.Buyer).FirstOrDefaultAsync(x => x.Id == packageId);
        return p == null ? null : await BuildAsync(p);
    }

    public async Task<PackageActualsView> BuildAsync(ProcurementPackage p)
    {
        var v = new PackageActualsView { Package = p };
        var tender = await _db.Tenders.AsNoTracking().Include(t => t.Lines).Include(t => t.AwardedVendor)
            .Where(t => t.PackageId == p.Id && t.Status != TenderStatus.Cancelled).OrderByDescending(t => t.Id).FirstOrDefaultAsync();

        if (tender != null)
        {
            v.Tender = tender;
            var contract = await _db.Contracts.AsNoTracking().Include(c => c.Vendor).Include(c => c.Lines).FirstOrDefaultAsync(c => c.TenderId == tender.Id);
            if (contract != null)
            {
                contract.Lines = contract.Lines.OrderBy(l => l.SortOrder).ToList();
                var pays = await _db.Payments.AsNoTracking().Where(x => x.ContractId == contract.Id).ToListAsync();
                v.Result = BoqActuals.Build(contract, tender.Lines.ToList(), await ContractActualQuantitiesAsync(contract), pays);
                v.Result.OrderedTotal = await _db.PurchaseOrders.AsNoTracking().Where(o => o.ContractId == contract.Id && o.Status >= PoStatus.Approved && o.Status != PoStatus.Cancelled).SumAsync(o => (decimal?)o.SubTotal) ?? 0;
                v.Contract = contract; v.HasCommitment = true; v.CommitmentLabel = "Giá hợp đồng hiện hành";
            }
            else v.Result = BoqActuals.EstimateOnly(tender.Lines.ToList());
            v.HasSelection = tender.Status >= TenderStatus.Awarded;
            v.SelectedLabel = "Giá trúng thầu"; v.Vendor = tender.AwardedVendor?.Name;
            v.Result.AwardPrice = v.HasSelection ? tender.AwardPrice ?? 0 : 0;
        }
        else
        {
            var boq = await _db.BoqItems.AsNoTracking().Where(b => b.PackageId == p.Id).ToListAsync();
            var est = EstimateLines(p, boq);
            var orders = await _db.PurchaseOrders.AsNoTracking().Include(o => o.Lines).Include(o => o.Vendor)
                .Where(o => o.PackageId == p.Id && o.Status >= PoStatus.Approved && o.Status != PoStatus.Cancelled).OrderBy(o => o.OrderDate).ToListAsync();
            v.Orders = orders;
            if (orders.Count > 0)
            {
                var (synthetic, map) = AggregateOrders(orders);
                var poLineIds = map.Keys.ToList();
                var received = await _db.GoodsReceiptLines.AsNoTracking().Where(g => poLineIds.Contains(g.PurchaseOrderLineId)).Select(g => new { g.PurchaseOrderLineId, g.QuantityAccepted }).ToListAsync();
                var actual = new Dictionary<int, decimal>();
                foreach (var r in received) actual[map[r.PurchaseOrderLineId]] = actual.GetValueOrDefault(map[r.PurchaseOrderLineId]) + r.QuantityAccepted;
                var poIds = orders.Select(o => o.Id).ToList();
                var invoiceIds = await _db.Invoices.AsNoTracking().Where(i => poIds.Contains(i.PurchaseOrderId)).Select(i => i.Id).ToListAsync();
                var pays = await _db.Payments.AsNoTracking().Where(x => x.InvoiceId != null && invoiceIds.Contains(x.InvoiceId.Value)).ToListAsync();
                v.Result = BoqActuals.Build(synthetic, est, actual, pays);
                v.Result.OrderedTotal = orders.Sum(o => o.SubTotal);
                v.HasCommitment = true; v.CommitmentLabel = "Giá trị đơn hàng";
            }
            else v.Result = BoqActuals.EstimateOnly(est);

            switch (p.Method)
            {
                case ProcurementMethod.CompetitiveQuotation:
                {
                    var rfq = await _db.Rfqs.AsNoTracking().Include(r => r.Vendors).ThenInclude(x => x.Vendor).Where(r => r.PackageId == p.Id && r.Status == RfqStatus.Awarded).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
                    v.SelectedLabel = "Giá báo giá được chọn";
                    if (rfq != null)
                    {
                        var win = rfq.Vendors.FirstOrDefault(x => x.VendorId == rfq.AwardedVendorId);
                        v.RfqId = rfq.Id; v.HasSelection = true; v.Vendor = win?.Vendor?.Name; v.Result.AwardPrice = win?.TotalAmount ?? 0;
                    }
                    break;
                }
                case ProcurementMethod.DirectAward:
                {
                    var da = await _db.DirectAwards.AsNoTracking().Include(d => d.Vendor).Where(d => d.PackageId == p.Id && d.Status == DirectAwardStatus.Approved).OrderByDescending(d => d.Id).FirstOrDefaultAsync();
                    v.SelectedLabel = "Giá chỉ định sau thương thảo";
                    if (da != null) { v.DirectAwardId = da.Id; v.HasSelection = true; v.Vendor = da.Vendor?.Name; v.Result.AwardPrice = da.NegotiatedTotal; }
                    break;
                }
                default:
                    v.SelectedLabel = "Giá đơn hàng";
                    if (orders.Count > 0) { v.HasSelection = true; v.Vendor = string.Join(", ", orders.Select(o => o.Vendor?.Name).Where(n => n != null).Distinct()); v.Result.AwardPrice = orders.Sum(o => o.SubTotal); }
                    break;
            }
            // đã đặt hàng mà chưa ghi nhận kết quả lựa chọn (dữ liệu cũ): coi như đã chọn theo đơn hàng
            if (!v.HasSelection && orders.Count > 0) { v.HasSelection = true; v.Vendor = string.Join(", ", orders.Select(o => o.Vendor?.Name).Where(n => n != null).Distinct()); v.Result.AwardPrice = orders.Sum(o => o.SubTotal); }
        }
        v.Result.PackageBudget = p.Budget;
        return v;
    }

    /// <summary>Tổng hợp các gói đã chọn được nhà cung cấp (lọc theo hình thức / lĩnh vực)</summary>
    public async Task<List<PackageActualsView>> SummaryAsync(ProcurementMethod? method, string? field, int take = 300)
    {
        var q = _db.ProcurementPackages.AsNoTracking().Include(x => x.PurchaseRequests).ThenInclude(r => r.Lines).Include(x => x.Buyer)
            .Where(p => p.Status != PackageStatus.Cancelled && p.Status != PackageStatus.Draft && p.Status != PackageStatus.InPlan);
        if (method.HasValue) q = q.Where(p => p.Method == method);
        if (!string.IsNullOrEmpty(field)) q = q.Where(p => p.Field == field);
        var list = new List<PackageActualsView>();
        foreach (var p in await q.OrderByDescending(p => p.CreatedAt).Take(take).AsSplitQuery().ToListAsync())
        {
            var v = await BuildAsync(p);
            if (v.HasSelection) list.Add(v);
        }
        return list;
    }
}
