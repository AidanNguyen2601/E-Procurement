using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure.Reporting;

/// <summary>Phân tích giá mua và cảnh báo rủi ro (dấu hiệu chia nhỏ gói, thông thầu, chi phối NCC…)</summary>
public partial class StandardReports
{
    /// <summary>Ngưỡng chênh so với đơn giá bình quân để coi là bất thường</summary>
    private const decimal PriceOutlier = 0.15m;

    // ------------------------------------------------------------------ Phân tích giá mua
    private async Task<ReportResult> PricesAsync(ReportFilter f)
    {
        var r = New("prices");
        var q = _db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.ItemId != null && l.PurchaseOrder!.Status >= PoStatus.Approved && l.PurchaseOrder.Status != PoStatus.Cancelled
                        && l.PurchaseOrder.OrderDate >= f.From && l.PurchaseOrder.OrderDate < f.ToExclusive);
        if (f.VendorId.HasValue) q = q.Where(l => l.PurchaseOrder!.VendorId == f.VendorId);
        if (f.CategoryId.HasValue) q = q.Where(l => l.Item!.CategoryId == f.CategoryId);
        if (!string.IsNullOrEmpty(f.Department)) q = q.Where(l => l.PurchaseOrder!.PurchaseRequest != null && l.PurchaseOrder.PurchaseRequest.Department == f.Department);
        var lines = await q.Select(l => new
        {
            l.ItemId, Item = l.Item!.Code + " – " + l.Item.Name, l.Item.ReferencePrice, l.Quantity, l.UnitPrice, l.PurchaseOrder!.Number,
            l.PurchaseOrder.OrderDate, Vendor = l.PurchaseOrder.Vendor!.Name, l.PurchaseOrder.Method
        }).ToListAsync();

        var stats = lines.GroupBy(l => l.ItemId).Select(g =>
        {
            var qty = g.Sum(x => x.Quantity);
            var avg = qty == 0 ? 0 : Math.Round(g.Sum(x => x.Quantity * x.UnitPrice) / qty, 0);
            var last = g.OrderByDescending(x => x.OrderDate).First();
            var min = g.Min(x => x.UnitPrice); var max = g.Max(x => x.UnitPrice);
            return new { g.Key, g.First().Item, g.First().ReferencePrice, N = g.Count(), Qty = qty, Min = min, Avg = avg, Max = max, Last = last.UnitPrice,
                Spread = min == 0 ? 0 : Math.Round((max - min) * 100 / min, 1), VsRef = g.First().ReferencePrice == 0 ? (decimal?)null : Math.Round((avg - g.First().ReferencePrice) * 100 / g.First().ReferencePrice, 1) };
        }).ToList();
        var avgById = stats.ToDictionary(s => s.Key, s => s.Avg);
        var outliers = lines.Where(l => avgById[l.ItemId] > 0 && l.UnitPrice > avgById[l.ItemId] * (1 + PriceOutlier))
            .Select(l => new { l, Avg = avgById[l.ItemId], Over = (l.UnitPrice - avgById[l.ItemId]) * l.Quantity }).OrderByDescending(x => x.Over).ToList();

        r.Kpis.Add(new("Mặt hàng phân tích", stats.Count.ToString(), $"{lines.Count} dòng PO"));
        r.Kpis.Add(new("Biến động giá > 20%", stats.Count(s => s.Spread > 20).ToString(), "chênh giá cao nhất – thấp nhất", stats.Any(s => s.Spread > 20)));
        r.Kpis.Add(new("Dòng mua giá cao bất thường", outliers.Count.ToString(), $"> bình quân {PriceOutlier * 100:0}%", outliers.Any()));
        r.Kpis.Add(new("Ước tính chênh lệch", outliers.Sum(x => x.Over).ToShortMoney() + " đ", "so với mua ở giá bình quân", outliers.Sum(x => x.Over) > 0));
        r.Tables.Add(new ReportTable
        {
            Title = "Đơn giá theo mặt hàng", Chart = ChartKind.Bar, ChartLabel = 0, ChartValue = 6,
            Note = "Bình quân gia quyền theo số lượng, trước VAT. Biến động = (cao nhất − thấp nhất) / thấp nhất.",
            Columns = { new("Mặt hàng"), new("Số dòng", ColumnKind.Number), new("Thấp nhất", ColumnKind.Money), new("Bình quân", ColumnKind.Money), new("Cao nhất", ColumnKind.Money),
                        new("Gần nhất", ColumnKind.Money), new("Biến động", ColumnKind.Percent), new("Giá tham khảo", ColumnKind.Money), new("BQ so với tham khảo", ColumnKind.Percent) },
            Rows = stats.OrderByDescending(s => s.Spread).Select(s => new object?[] { s.Item, s.N, s.Min, s.Avg, s.Max, s.Last, s.Spread, s.ReferencePrice, s.VsRef }).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Dòng mua giá cao bất thường",
            Note = $"Đơn giá vượt bình quân kỳ của cùng mặt hàng hơn {PriceOutlier * 100:0}%. Chênh lệch = (đơn giá − bình quân) × số lượng.",
            Columns = { new("PO"), new("Ngày"), new("Nhà cung cấp"), new("Hình thức"), new("Mặt hàng"), new("SL", ColumnKind.Number), new("Đơn giá", ColumnKind.Money),
                        new("Bình quân", ColumnKind.Money), new("Vượt", ColumnKind.Percent), new("Chênh lệch", ColumnKind.Money) },
            Rows = outliers.Take(200).Select(x => new object?[] { x.l.Number, x.l.OrderDate, x.l.Vendor, x.l.Method?.GetDisplayName() ?? "—", x.l.Item, x.l.Quantity, x.l.UnitPrice, x.Avg,
                Math.Round((x.l.UnitPrice - x.Avg) * 100 / x.Avg, 1), x.Over }).ToList(),
            Totals = outliers.Any() ? new object?[] { "Tổng", null, null, null, null, null, null, null, null, outliers.Sum(x => x.Over) } : null
        });
        return r;
    }

    // ------------------------------------------------------------------ Cảnh báo rủi ro
    private record Flag(string Type, string Subject, string Detail, decimal? Value, string Severity);

    private async Task<ReportResult> RiskAsync(ReportFilter f)
    {
        var r = New("risk");
        var flags = new List<Flag>();
        var lines = await SpendLinesAsync(f);

        // 1. Nghi chia nhỏ gói: cùng phòng ban + nhóm hàng, các PO không cạnh tranh trong 30 ngày, từng PO dưới hạn mức chỉ định nhưng cộng dồn vượt
        var limit = _settings.DirectAwardMax;
        var nonComp = lines.Where(l => l.Method is null or ProcurementMethod.DirectPurchase or ProcurementMethod.DirectAward)
            .GroupBy(l => new { l.PoId, Number = l.PoNumber, Date = l.OrderDate, l.Department, l.CategoryId, l.Category, l.Method })
            .Select(g => new { g.Key.PoId, g.Key.Number, g.Key.Date, g.Key.Department, g.Key.CategoryId, g.Key.Category, g.Key.Method, Sum = g.Sum(x => x.Amount) }).ToList();
        foreach (var grp in nonComp.Where(p => p.Sum <= limit && p.Method != ProcurementMethod.DirectPurchase).GroupBy(p => new { p.Department, p.CategoryId, p.Category }))
        {
            var list = grp.OrderBy(p => p.Date).ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var window = list.Skip(i).TakeWhile(p => (p.Date - list[i].Date).TotalDays <= 30).ToList();
                if (window.Count >= 2 && window.Sum(p => p.Sum) > limit)
                {
                    flags.Add(new("Nghi chia nhỏ gói", $"{grp.Key.Department} · {grp.Key.Category}",
                        $"{window.Count} PO không qua cạnh tranh trong 30 ngày ({string.Join(", ", window.Select(p => p.Number))}), cộng dồn vượt hạn mức chỉ định {limit.ToShortMoney()} đ",
                        window.Sum(p => p.Sum), "Cao"));
                    i += window.Count - 1;
                }
            }
        }

        // 2. NCC chi phối nhóm hàng: > 70% giá trị nhóm hàng, ≥ 3 PO
        foreach (var cat in lines.GroupBy(l => l.Category))
        {
            var total = cat.Sum(x => x.Amount);
            var top = cat.GroupBy(x => x.Vendor).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount), Pos = g.Select(x => x.PoId).Distinct().Count() }).OrderByDescending(x => x.Sum).First();
            if (total > 0 && top.Sum / total > 0.7m && top.Pos >= 3 && cat.Select(x => x.VendorId).Distinct().Count() >= 1)
                flags.Add(new("NCC chi phối nhóm hàng", cat.Key, $"{top.Key} chiếm {Pc(Math.Round(top.Sum * 100 / total, 1))} giá trị nhóm ({top.Pos} PO)", top.Sum, "Trung bình"));
        }

        // 3. Trúng liên tục trong chào hàng cạnh tranh: một NCC thắng ≥ 3 RFQ và ≥ 75% số RFQ có kết quả của nhóm hàng
        var rfqs = await _db.Rfqs.AsNoTracking().Include(x => x.Vendors).ThenInclude(v => v.QuoteLines).Include(x => x.Vendors).ThenInclude(v => v.Vendor)
            .Include(x => x.Lines).ThenInclude(l => l.Item).ThenInclude(i => i!.Category).Include(x => x.AwardedVendor)
            .Where(x => x.Status == RfqStatus.Awarded && x.IssueDate >= f.From && x.IssueDate < f.ToExclusive).AsSplitQuery().ToListAsync();
        foreach (var g in rfqs.GroupBy(x => x.Lines.Select(l => l.Item?.Category?.Name).FirstOrDefault(n => n != null) ?? "(Ngoài danh mục)"))
        {
            var top = g.GroupBy(x => x.AwardedVendor?.Name ?? "?").Select(w => new { w.Key, N = w.Count() }).OrderByDescending(w => w.N).First();
            if (top.N >= 3 && top.N * 100 / g.Count() >= 75)
                flags.Add(new("Trúng liên tục", $"{g.Key} · {top.Key}", $"Thắng {top.N}/{g.Count()} RFQ của nhóm hàng trong kỳ – rà soát danh sách mời, luân phiên NCC", null, "Trung bình"));
        }

        // 4. Báo giá sát nhau bất thường (≥ 3 báo giá hợp lệ, chênh < 2%) hoặc trùng tổng giá
        foreach (var x in rfqs)
        {
            var totals = x.Vendors.Where(v => v.Responded && v.TotalAmount > 0 && v.TechnicalPass != false).Select(v => v.TotalAmount).OrderBy(t => t).ToList();
            if (totals.Count < 3) continue;
            var spread = (totals.Last() - totals.First()) * 100 / totals.First();
            if (spread < 2 || totals.Distinct().Count() < totals.Count)
                flags.Add(new("Báo giá sát nhau bất thường", $"{x.Number} – {x.Title}",
                    totals.Distinct().Count() < totals.Count ? "Có báo giá trùng tổng giá – dấu hiệu thông đồng" : $"{totals.Count} báo giá chênh nhau chỉ {Pc(Math.Round(spread, 2))}", totals.First(), "Cao"));
        }

        // 5. Gói thầu chỉ một HSDT
        foreach (var t in await _db.Tenders.AsNoTracking().Include(x => x.Bidders)
                     .Where(x => x.OpenedAt != null && x.OpenedAt >= f.From && x.OpenedAt < f.ToExclusive).ToListAsync())
            if (t.Bidders.Count(b => b.Submitted) <= 1)
                flags.Add(new("Đấu thầu không cạnh tranh", $"{t.Number} – {t.Title}", $"Chỉ {t.Bidders.Count(b => b.Submitted)} HSDT / {t.Bidders.Count} nhà thầu nhận HSMT – xem lại tiêu chí HSMT, thời gian mời thầu", t.PackageBudget, "Cao"));

        // 6. Tỷ trọng mua không qua cạnh tranh theo phòng ban > 30%
        foreach (var d in lines.GroupBy(l => l.Department))
        {
            var total = d.Sum(x => x.Amount);
            var nc = d.Where(x => x.Method is null or ProcurementMethod.DirectAward).Sum(x => x.Amount);
            if (total > 50_000_000 && nc / total > 0.3m)
                flags.Add(new("Tỷ trọng không cạnh tranh cao", d.Key, $"{Pc(Math.Round(nc * 100 / total, 1))} giá trị qua chỉ định thầu / PO lẻ", nc, "Trung bình"));
        }

        // 7. Hóa đơn lệch đối chiếu được chấp nhận
        var accepted = await _db.Invoices.AsNoTracking().Include(i => i.Vendor)
            .Where(i => i.DecisionNote != null && i.Status != InvoiceStatus.Rejected && i.MatchNote != null && !i.MatchNote.StartsWith("Khớp")
                        && i.InvoiceDate >= f.From && i.InvoiceDate < f.ToExclusive).ToListAsync();
        foreach (var i in accepted)
            flags.Add(new("Chấp nhận hóa đơn lệch", $"{i.Vendor?.Name} – HĐ {i.InvoiceNo}", $"{i.MatchNote} · lý do: {i.DecisionNote}", i.Total, "Thấp"));

        var order = new Dictionary<string, int> { ["Cao"] = 0, ["Trung bình"] = 1, ["Thấp"] = 2 };
        flags = flags.OrderBy(x => order[x.Severity]).ThenByDescending(x => x.Value ?? 0).ToList();
        r.Kpis.Add(new("Cảnh báo mức cao", flags.Count(x => x.Severity == "Cao").ToString(), null, flags.Any(x => x.Severity == "Cao")));
        r.Kpis.Add(new("Cảnh báo trung bình", flags.Count(x => x.Severity == "Trung bình").ToString()));
        r.Kpis.Add(new("Cảnh báo thấp", flags.Count(x => x.Severity == "Thấp").ToString()));
        r.Kpis.Add(new("Giá trị liên quan", flags.Sum(x => x.Value ?? 0).ToShortMoney() + " đ"));
        r.Tables.Add(new ReportTable
        {
            Title = "Cảnh báo theo loại", Width = 5, Chart = ChartKind.Bar,
            Columns = { new("Loại cảnh báo"), new("Số cảnh báo", ColumnKind.Number) },
            Rows = flags.GroupBy(x => x.Type).Select(g => new object?[] { g.Key, g.Count() }).OrderByDescending(x => (int)x[1]!).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Danh sách cảnh báo", Width = 12,
            Note = "Dấu hiệu cần rà soát, không phải kết luận vi phạm. Ngưỡng: chia nhỏ gói theo hạn mức chỉ định thầu, chi phối > 70%, trúng ≥ 75%, báo giá chênh < 2%.",
            Columns = { new("Mức"), new("Loại"), new("Đối tượng"), new("Chi tiết"), new("Giá trị", ColumnKind.Money) },
            Rows = flags.Select(x => new object?[] { x.Severity, x.Type, x.Subject, x.Detail, x.Value }).ToList()
        });
        return r;
    }
}
