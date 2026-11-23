using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure.Reporting;

/// <summary>Báo cáo quản lý nguồn cung: xếp loại NCC, giá thực tế so với giá tiêu chuẩn, kết quả sơ tuyển</summary>
public partial class StandardReports
{
    /// <summary>Báo cáo thuộc nhóm "Quản lý nguồn cung" (menu Quản lý nguồn cung → Báo cáo)</summary>
    public static readonly string[] SourcingKeys = { "supplier-grades", "price-vs-standard", "prequal", "vendors", "delivery", "prices" };

    // ------------------------------------------------------------------ Xếp loại nhà cung cấp
    private async Task<ReportResult> SupplierGradesAsync(ReportFilter f)
    {
        var r = New("supplier-grades");
        var rows = await SupplierScorecard.BuildAsync(_db, f.From, f.To, f.VendorId);
        if (f.CategoryId.HasValue)
        {
            var ids = await _db.VendorCategories.Where(c => c.CategoryId == f.CategoryId).Select(c => c.VendorId).ToListAsync();
            rows = rows.Where(x => ids.Contains(x.Vendor.Id)).ToList();
        }
        foreach (var g in new[] { "A", "B", "C", "D" })
            r.Kpis.Add(new($"Loại {g}", rows.Count(x => x.Grade == g).ToString(), SupplierScorecard.GradeText(g), g == "D" && rows.Any(x => x.Grade == "D")));
        r.Tables.Add(new ReportTable
        {
            Title = "Xếp loại nhà cung cấp", Chart = ChartKind.Bar, ChartValue = 8,
            Columns = { new("Nhà cung cấp"), new("Số PO", ColumnKind.Number), new("Giá trị mua", ColumnKind.Money), new("% đúng hạn", ColumnKind.Percent),
                        new("% không đạt", ColumnKind.Percent), new("Số phiếu", ColumnKind.Number), new("Chất lượng", ColumnKind.Number), new("Tiến độ", ColumnKind.Number),
                        new("Điểm (5)", ColumnKind.Number), new("Xếp loại"), new("Khuyến nghị") },
            Rows = rows.Select(x => new object?[] { x.Vendor.Name, x.Orders, x.Value, x.OnTimePercent, x.RejectPercent, x.Evaluations, x.Quality, x.Delivery, x.Score, x.Grade, SupplierScorecard.GradeText(x.Grade) }).ToList()
        });
        return r;
    }

    // ------------------------------------------------------------------ Giá thực tế so với bảng giá tiêu chuẩn
    private async Task<ReportResult> PriceVsStandardAsync(ReportFilter f)
    {
        var r = New("price-vs-standard");
        var std = await StandardPrices.CurrentAsync(_db, null, f.To);
        var q = _db.PurchaseOrderLines.AsNoTracking().Where(l => l.ItemId != null && l.PurchaseOrder!.Status >= PoStatus.Approved && l.PurchaseOrder.Status != PoStatus.Cancelled
            && l.PurchaseOrder.OrderDate >= f.From && l.PurchaseOrder.OrderDate < f.ToExclusive);
        if (f.VendorId.HasValue) q = q.Where(l => l.PurchaseOrder!.VendorId == f.VendorId);
        if (f.CategoryId.HasValue) q = q.Where(l => l.Item!.CategoryId == f.CategoryId);
        var lines = await q.Select(l => new { ItemId = l.ItemId!.Value, Item = l.Item!.Code + " – " + l.Item.Name, l.Quantity, l.UnitPrice, l.PurchaseOrder!.Number, Vendor = l.PurchaseOrder.Vendor!.Name, l.PurchaseOrder.StandardPriceReason })
            .ToListAsync();
        var withStd = lines.Where(l => std.ContainsKey(l.ItemId)).ToList();
        var over = withStd.Where(l => l.UnitPrice > std[l.ItemId].Ceiling).ToList();
        var excess = over.Sum(l => (l.UnitPrice - std[l.ItemId].Price) * l.Quantity);
        var saving = withStd.Where(l => l.UnitPrice < std[l.ItemId].Price).Sum(l => (std[l.ItemId].Price - l.UnitPrice) * l.Quantity);
        r.Kpis.Add(new("Mặt hàng có giá tiêu chuẩn", std.Count.ToString(), $"{withStd.Select(l => l.ItemId).Distinct().Count()} mặt hàng phát sinh mua trong kỳ"));
        r.Kpis.Add(new("Dòng mua vượt giá trần", over.Count.ToString(), $"trên {withStd.Count} dòng có giá tiêu chuẩn", over.Count > 0));
        r.Kpis.Add(new("Chi vượt giá tiêu chuẩn", excess.ToShortMoney() + " đ", "phần vượt giá chuẩn của các dòng vượt trần", excess > 0));
        r.Kpis.Add(new("Mua thấp hơn giá tiêu chuẩn", saving.ToShortMoney() + " đ", "tiết kiệm so với giá chuẩn"));
        r.Tables.Add(new ReportTable
        {
            Title = "Theo mặt hàng", Chart = ChartKind.Bar, ChartValue = 6,
            Columns = { new("Mặt hàng"), new("Giá tiêu chuẩn", ColumnKind.Money), new("Giá trần", ColumnKind.Money), new("Số dòng mua", ColumnKind.Number),
                        new("Đơn giá TB", ColumnKind.Money), new("Cao nhất", ColumnKind.Money), new("% TB so với chuẩn", ColumnKind.Percent), new("Dòng vượt trần", ColumnKind.Number) },
            Rows = withStd.GroupBy(l => l.ItemId).Select(g =>
            {
                var s = std[g.Key]; var avg = Math.Round(g.Sum(x => x.UnitPrice * x.Quantity) / Math.Max(1, g.Sum(x => x.Quantity)), 0);
                return new object?[] { g.First().Item, s.Price, s.Ceiling, g.Count(), avg, g.Max(x => x.UnitPrice), Pct(avg - s.Price, s.Price), g.Count(x => x.UnitPrice > s.Ceiling) };
            }).OrderByDescending(x => (decimal)x[6]!).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Các dòng mua vượt giá trần",
            Columns = { new("Đơn hàng"), new("Nhà cung cấp"), new("Mặt hàng"), new("SL", ColumnKind.Number), new("Đơn giá", ColumnKind.Money), new("Giá trần", ColumnKind.Money), new("Lý do") },
            Rows = over.OrderByDescending(l => (l.UnitPrice - std[l.ItemId].Ceiling) * l.Quantity)
                .Select(l => new object?[] { l.Number, l.Vendor, l.Item, l.Quantity, l.UnitPrice, std[l.ItemId].Ceiling, l.StandardPriceReason }).ToList()
        });
        return r;
    }

    // ------------------------------------------------------------------ Kết quả sơ tuyển
    private async Task<ReportResult> PrequalAsync(ReportFilter f)
    {
        var r = New("prequal");
        var q = _db.Prequalifications.AsNoTracking().Where(p => p.Status != PrequalificationStatus.Draft && p.Deadline >= f.From && p.Deadline < f.ToExclusive);
        if (f.CategoryId.HasValue) q = q.Where(p => p.CategoryId == f.CategoryId);
        var rounds = await q.Select(p => new { p.Number, p.Title, Cat = p.Category!.Name, p.Status, p.Deadline,
            Apps = p.Applications.Count(a => a.Status != PrequalificationResult.Withdrawn), Passed = p.Applications.Count(a => a.Status == PrequalificationResult.Passed) }).ToListAsync();
        var valid = await _db.PrequalificationApplications.AsNoTracking().Where(a => a.Status == PrequalificationResult.Passed && a.Prequalification!.Status == PrequalificationStatus.Completed
                && (a.ValidUntil == null || a.ValidUntil >= DateTime.Today) && (f.CategoryId == null || a.Prequalification.CategoryId == f.CategoryId))
            .Select(a => new { Vendor = a.Vendor!.Name, a.Vendor.TaxCode, Cat = a.Prequalification!.Category!.Name, a.Prequalification.Number, a.TotalScore, a.ValidUntil }).ToListAsync();
        r.Kpis.Add(new("Đợt sơ tuyển", rounds.Count.ToString()));
        r.Kpis.Add(new("Hồ sơ dự sơ tuyển", rounds.Sum(x => x.Apps).ToString()));
        r.Kpis.Add(new("Tỷ lệ đạt", Pc(Pct(rounds.Sum(x => x.Passed), rounds.Sum(x => x.Apps)))));
        r.Kpis.Add(new("NCC đang còn hiệu lực sơ tuyển", valid.Select(v => v.Vendor).Distinct().Count().ToString(), "đến hôm nay"));
        r.Tables.Add(new ReportTable
        {
            Title = "Các đợt sơ tuyển",
            Columns = { new("Số"), new("Đợt sơ tuyển"), new("Nhóm hàng"), new("Hạn nộp", ColumnKind.Date), new("Hồ sơ", ColumnKind.Number), new("Đạt", ColumnKind.Number), new("Trạng thái") },
            Rows = rounds.OrderByDescending(x => x.Deadline).Select(x => new object?[] { x.Number, x.Title, x.Cat, x.Deadline, x.Apps, x.Passed, x.Status.GetDisplayName() }).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Danh sách NCC đạt sơ tuyển còn hiệu lực",
            Columns = { new("Nhà cung cấp"), new("MST"), new("Nhóm hàng"), new("Đợt"), new("Điểm", ColumnKind.Number), new("Hiệu lực đến", ColumnKind.Date) },
            Rows = valid.OrderBy(x => x.Cat).ThenByDescending(x => x.TotalScore).Select(x => new object?[] { x.Vendor, x.TaxCode, x.Cat, x.Number, x.TotalScore, x.ValidUntil }).ToList()
        });
        return r;
    }
}
