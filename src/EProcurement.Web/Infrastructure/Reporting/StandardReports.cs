using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure.Reporting;

/// <summary>Bộ báo cáo chuẩn về mua sắm. Chi tiêu = giá trị trước VAT của PO đã duyệt trở lên (không tính nháp / chờ duyệt / hủy).</summary>
public partial class StandardReports
{
    public record Definition(string Key, string Title, string Description, string Icon);

    public static readonly Definition[] All =
    {
        new("spend", "Tổng hợp chi tiêu mua sắm", "Chi tiêu theo tháng, phòng ban, nhóm hàng, nhà cung cấp; so sánh với dự toán.", "coins"),
        new("methods", "Chi tiêu theo hình thức lựa chọn NCC", "Tỷ trọng mua sắm trực tiếp / chỉ định / chào hàng cạnh tranh / đấu thầu; tiết kiệm so với dự toán.", "scale"),
        new("requests", "Tiến độ xử lý đề nghị mua hàng", "Số PR theo trạng thái, thời gian duyệt, thời gian từ duyệt đến đặt hàng theo phòng ban.", "clock"),
        new("delivery", "Tiến độ giao hàng & chất lượng", "Tỷ lệ giao đúng hạn, số ngày trễ, tỷ lệ hàng không đạt theo nhà cung cấp.", "truck"),
        new("vendors", "Đánh giá nhà cung cấp", "Điểm đánh giá, giá trị mua, tỷ lệ phản hồi báo giá và trúng thầu.", "building"),
        new("competition", "Hiệu quả cạnh tranh", "Số báo giá / HSDT bình quân, tỷ lệ không chọn giá thấp nhất, giá trúng so với giá gói thầu, chỉ định thầu.", "trophy"),
        new("contracts", "Tình hình thực hiện hợp đồng", "Giá trị thực hiện, hợp đồng sắp hết hạn, bảo lãnh thực hiện sắp hết hiệu lực.", "file-check"),
        new("budget", "Ngân sách – thực hiện", "Ngân sách năm theo phòng ban × nhóm hàng: đã đặt hàng, giữ chỗ, còn lại, dòng vượt ngân sách.", "wallet"),
        new("prices", "Phân tích giá mua", "Biến động đơn giá theo mặt hàng, so với giá tham khảo; các dòng mua giá cao bất thường và ước tính chênh lệch.", "coins"),
        new("supplier-grades", "Xếp loại nhà cung cấp", "Điểm đánh giá, % giao đúng hạn, % hàng không đạt, xếp loại A/B/C/D và khuyến nghị.", "trophy"),
        new("price-vs-standard", "Giá mua so với bảng giá tiêu chuẩn", "Đơn giá thực tế so với giá tiêu chuẩn / giá trần đang hiệu lực, các dòng vượt trần và lý do.", "tag"),
        new("prequal", "Kết quả sơ tuyển nhà cung cấp", "Các đợt sơ tuyển, tỷ lệ đạt, danh sách NCC đạt còn hiệu lực theo nhóm hàng.", "shield-check"),
        new("risk", "Cảnh báo rủi ro mua sắm", "Nghi chia nhỏ gói, NCC chi phối nhóm hàng, trúng liên tục, báo giá sát nhau, gói thầu một hồ sơ, mua không cạnh tranh.", "alert"),
        new("bidding", "Tình hình thực hiện công tác lựa chọn nhà thầu", "Báo cáo kỳ / năm: số gói, giá gói, giá trúng thầu, tiết kiệm theo hình thức và lĩnh vực; đấu thầu: số HSDT, liên danh, kiến nghị, tuân thủ mốc thời gian theo luật; gói hủy.", "gavel"),
        new("green", "Mua sắm xanh – ESG", "Tỷ trọng chi tiêu với nhà cung cấp có chứng nhận môi trường / xã hội, chứng nhận sắp hết hạn, bộ tiêu chí có tiêu chí xanh.", "shield-check"),
    };

    private readonly EProcDbContext _db;
    private readonly ProcurementSettings _settings;
    public StandardReports(EProcDbContext db, Microsoft.Extensions.Options.IOptions<ProcurementSettings> settings) { _db = db; _settings = settings.Value; }

    public async Task<ReportResult> BuildAsync(string key, ReportFilter f)
    {
        // Lọc theo một khối / chi nhánh: gộp cả các đơn vị cấp dưới trong cơ cấu tổ chức
        if (!string.IsNullOrEmpty(f.Department) && f.Departments == null) f.Departments = await OrgUnits.SubtreeNamesAsync(_db, f.Department);
        return await (key switch
    {
        "spend" => SpendAsync(f),
        "methods" => MethodsAsync(f),
        "requests" => RequestsAsync(f),
        "delivery" => DeliveryAsync(f),
        "vendors" => VendorsAsync(f),
        "competition" => CompetitionAsync(f),
        "contracts" => ContractsAsync(f),
        "budget" => BudgetAsync(f),
        "prices" => PricesAsync(f),
        "risk" => RiskAsync(f),
        "bidding" => BiddingAsync(f),
        "green" => GreenAsync(f),
        "supplier-grades" => SupplierGradesAsync(f),
        "price-vs-standard" => PriceVsStandardAsync(f),
        "prequal" => PrequalAsync(f),
        _ => throw new KeyNotFoundException(key)
        });
    }

    // ------------------------------------------------------------------ 8. Ngân sách – thực hiện (theo năm của ngày kết thúc kỳ lọc)
    private async Task<ReportResult> BudgetAsync(ReportFilter f)
    {
        var r = New("budget");
        var year = f.To.Year;
        var rows = await new BudgetService(_db).StatusAsync(year);
        if (!string.IsNullOrEmpty(f.Department)) rows = rows.Where(x => f.Depts.Contains(x.Budget.Department)).ToList();
        if (f.CategoryId.HasValue) rows = rows.Where(x => x.Budget.CategoryId == f.CategoryId).ToList();
        var total = rows.Sum(x => x.Budget.Amount); var used = rows.Sum(x => x.Used);
        r.Description = $"Năm {year} (theo ngày kết thúc kỳ lọc). " + r.Description;
        r.Kpis.Add(new("Ngân sách năm " + year, total.ToShortMoney() + " đ", $"{rows.Count} dòng ngân sách"));
        r.Kpis.Add(new("Đã dùng", Pc(Pct(used, total)), $"đặt hàng {rows.Sum(x => x.Ordered).ToShortMoney()} + giữ chỗ {rows.Sum(x => x.Reserved).ToShortMoney()} đ", total > 0 && used > total));
        r.Kpis.Add(new("Còn lại", (total - used).ToShortMoney() + " đ", null, total - used < 0));
        r.Kpis.Add(new("Dòng vượt ngân sách", rows.Count(x => x.Available < 0).ToString(), null, rows.Any(x => x.Available < 0)));
        r.Tables.Add(new ReportTable
        {
            Title = "Thực hiện ngân sách", Chart = ChartKind.Bar, ChartLabel = 0, ChartValue = 6,
            Note = "Giá trị trước VAT. Đã dùng = đã đặt hàng (PO đã duyệt) + giữ chỗ (PR đã duyệt chưa lập PO). PR chờ duyệt chưa trừ ngân sách.",
            Columns = { new("Dòng ngân sách"), new("Ngân sách", ColumnKind.Money), new("Đã đặt hàng", ColumnKind.Money), new("Giữ chỗ", ColumnKind.Money),
                        new("Còn lại", ColumnKind.Money), new("Chờ duyệt", ColumnKind.Money), new("% đã dùng", ColumnKind.Percent) },
            Rows = rows.OrderByDescending(x => x.UsedPercent).Select(x => new object?[] { x.Pool, x.Budget.Amount, x.Ordered, x.Reserved, x.Available, x.Pending, x.UsedPercent }).ToList(),
            Totals = new object?[] { "Tổng", total, rows.Sum(x => x.Ordered), rows.Sum(x => x.Reserved), total - used, rows.Sum(x => x.Pending), Pct(used, total) }
        });
        return r;
    }

    private static ReportResult New(string key)
    {
        var d = All.First(x => x.Key == key);
        return new ReportResult { Key = key, Title = d.Title, Description = d.Description };
    }

    private static decimal Pct(decimal part, decimal whole) => whole == 0 ? 0 : Math.Round(part * 100 / whole, 1);
    private static readonly System.Globalization.CultureInfo ViCulture = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");
    private static string Pc(decimal v) => ReportFormat.Cell(v, ColumnKind.Percent);

    // ------------------------------------------------------------------ Dữ liệu chi tiêu dùng chung
    private record SpendLine(int PoId, string PoNumber, DateTime OrderDate, int? VendorId, string Vendor, string Department,
        int? CategoryId, string Category, ProcurementMethod? Method, decimal Amount, int? PrId);

    private async Task<List<SpendLine>> SpendLinesAsync(ReportFilter f)
    {
        var q = _db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.PurchaseOrder!.Status >= PoStatus.Approved && l.PurchaseOrder.Status != PoStatus.Cancelled
                        && l.PurchaseOrder.OrderDate >= f.From && l.PurchaseOrder.OrderDate < f.ToExclusive);
        if (f.VendorId.HasValue) q = q.Where(l => l.PurchaseOrder!.VendorId == f.VendorId);
        if (f.CategoryId.HasValue) q = q.Where(l => l.Item != null && l.Item.CategoryId == f.CategoryId);
        if (!string.IsNullOrEmpty(f.Department)) q = q.Where(l => l.PurchaseOrder!.PurchaseRequest != null && f.Depts.Contains(l.PurchaseOrder.PurchaseRequest.Department));
        return await q.Select(l => new SpendLine(l.PurchaseOrderId, l.PurchaseOrder!.Number, l.PurchaseOrder.OrderDate, l.PurchaseOrder.VendorId,
                l.PurchaseOrder.Vendor != null ? l.PurchaseOrder.Vendor.Name : "—",
                l.PurchaseOrder.PurchaseRequest != null ? l.PurchaseOrder.PurchaseRequest.Department : "(Không qua đề nghị)",
                l.Item != null ? l.Item.CategoryId : null,
                l.Item != null && l.Item.Category != null ? l.Item.Category.Name : "(Ngoài danh mục)",
                l.PurchaseOrder.Method, l.Amount, l.PurchaseOrder.PurchaseRequestId))
            .ToListAsync();
    }

    private static ReportTable Group(string title, string dim, IEnumerable<SpendLine> lines, Func<SpendLine, string> key, decimal total, int top = 0, int width = 6)
    {
        var rows = lines.GroupBy(key).Select(g => new { g.Key, Sum = g.Sum(x => x.Amount), Pos = g.Select(x => x.PoId).Distinct().Count() })
            .OrderByDescending(x => x.Sum).ToList();
        var shown = top > 0 ? rows.Take(top).ToList() : rows;
        var t = new ReportTable
        {
            Title = title, Width = width, Chart = ChartKind.Bar, ChartLabel = 0, ChartValue = 1,
            Columns = { new(dim), new("Giá trị (trước VAT)", ColumnKind.Money), new("Tỷ trọng", ColumnKind.Percent), new("Số PO", ColumnKind.Number) },
            Rows = shown.Select(r => new object?[] { r.Key, r.Sum, Pct(r.Sum, total), r.Pos }).ToList(),
            Totals = new object?[] { "Tổng", total, total == 0 ? 0m : 100m, lines.Select(x => x.PoId).Distinct().Count() }
        };
        if (top > 0 && rows.Count > top) t.Note = $"Top {top}/{rows.Count}; dòng tổng tính trên toàn bộ.";
        return t;
    }

    // ------------------------------------------------------------------ 1. Chi tiêu
    private async Task<ReportResult> SpendAsync(ReportFilter f)
    {
        var r = New("spend");
        var lines = await SpendLinesAsync(f);
        var total = lines.Sum(x => x.Amount);
        var poCount = lines.Select(x => x.PoId).Distinct().Count();
        var prIds = lines.Where(x => x.PrId.HasValue).Select(x => x.PrId!.Value).Distinct().ToList();
        var estimate = await _db.PurchaseRequests.Where(p => prIds.Contains(p.Id)).SumAsync(p => (decimal?)p.EstimatedTotal) ?? 0;
        var spendWithPr = lines.Where(x => x.PrId.HasValue).Sum(x => x.Amount);
        r.Kpis.Add(new("Tổng chi tiêu", total.ToShortMoney() + " đ", "trước VAT"));
        r.Kpis.Add(new("Số đơn hàng", poCount.ToString(), $"{lines.Select(x => x.VendorId).Distinct().Count()} nhà cung cấp"));
        r.Kpis.Add(new("Giá trị bình quân / PO", (poCount == 0 ? 0 : total / poCount).ToShortMoney() + " đ"));
        r.Kpis.Add(new("Tiết kiệm so với dự toán", (estimate - spendWithPr).ToShortMoney() + " đ", $"{Pc(Pct(estimate - spendWithPr, estimate))} trên PO có đề nghị", estimate - spendWithPr < 0));

        // Theo tháng (đủ các tháng trong kỳ, kể cả tháng 0 đồng)
        var months = new List<DateTime>();
        for (var m = new DateTime(f.From.Year, f.From.Month, 1); m <= f.To; m = m.AddMonths(1)) months.Add(m);
        if (months.Count > 36) months = months.TakeLast(36).ToList();
        r.Tables.Add(new ReportTable
        {
            Title = "Chi tiêu theo tháng", Chart = ChartKind.Column,
            Columns = { new("Tháng"), new("Giá trị (trước VAT)", ColumnKind.Money), new("Số PO", ColumnKind.Number) },
            Rows = months.Select(m =>
            {
                var g = lines.Where(x => x.OrderDate.Year == m.Year && x.OrderDate.Month == m.Month).ToList();
                return new object?[] { m.ToString("MM/yyyy"), g.Sum(x => x.Amount), g.Select(x => x.PoId).Distinct().Count() };
            }).ToList(),
            Totals = new object?[] { "Tổng", total, poCount }
        });
        r.Tables.Add(Group("Theo phòng ban", "Phòng ban", lines, x => x.Department, total));
        r.Tables.Add(Group("Theo nhóm hàng", "Nhóm hàng", lines, x => x.Category, total));
        r.Tables.Add(Group("Top nhà cung cấp", "Nhà cung cấp", lines, x => x.Vendor, total, top: 15, width: 12));
        return r;
    }

    // ------------------------------------------------------------------ 2. Hình thức lựa chọn NCC
    private async Task<ReportResult> MethodsAsync(ReportFilter f)
    {
        var r = New("methods");
        var lines = await SpendLinesAsync(f);
        var total = lines.Sum(x => x.Amount);
        string Name(ProcurementMethod? m) => m?.GetDisplayName() ?? "(Chưa phân loại / lập PO lẻ)";
        var prIds = lines.Where(x => x.PrId.HasValue).Select(x => x.PrId!.Value).Distinct().ToList();
        var estimates = await _db.PurchaseRequests.Where(p => prIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.EstimatedTotal);

        var groups = lines.GroupBy(x => x.Method).Select(g =>
        {
            var withPr = g.Where(x => x.PrId.HasValue).ToList();
            var est = withPr.Select(x => x.PrId!.Value).Distinct().Sum(id => estimates.GetValueOrDefault(id));
            var spent = withPr.Sum(x => x.Amount);
            return new { g.Key, Sum = g.Sum(x => x.Amount), Pos = g.Select(x => x.PoId).Distinct().Count(), Saving = est - spent, SavingPct = Pct(est - spent, est) };
        }).OrderBy(x => x.Key.HasValue ? (int)x.Key : 99).ToList();

        var competitive = groups.Where(g => g.Key is ProcurementMethod.CompetitiveQuotation or ProcurementMethod.OpenTender).Sum(g => g.Sum);
        var direct = groups.Where(g => g.Key is ProcurementMethod.DirectAward).Sum(g => g.Sum);
        r.Kpis.Add(new("Tổng chi tiêu", total.ToShortMoney() + " đ"));
        r.Kpis.Add(new("Qua cạnh tranh", Pc(Pct(competitive, total)), "chào hàng cạnh tranh + đấu thầu"));
        r.Kpis.Add(new("Chỉ định thầu", Pc(Pct(direct, total)), direct.ToShortMoney() + " đ", Pct(direct, total) > 20));
        r.Kpis.Add(new("Chưa phân loại", Pc(Pct(groups.Where(g => g.Key == null).Sum(g => g.Sum), total)), "PO lập lẻ / dữ liệu cũ"));
        r.Tables.Add(new ReportTable
        {
            Title = "Cơ cấu theo hình thức", Chart = ChartKind.Bar,
            Columns = { new("Hình thức"), new("Giá trị (trước VAT)", ColumnKind.Money), new("Tỷ trọng", ColumnKind.Percent), new("Số PO", ColumnKind.Number),
                        new("Tiết kiệm so dự toán", ColumnKind.Money), new("% tiết kiệm", ColumnKind.Percent) },
            Rows = groups.Select(g => new object?[] { Name(g.Key), g.Sum, Pct(g.Sum, total), g.Pos, g.Saving, g.SavingPct }).ToList(),
            Totals = new object?[] { "Tổng", total, total == 0 ? 0m : 100m, groups.Sum(g => g.Pos), groups.Sum(g => g.Saving), null }
        });
        // Theo phòng ban × hình thức
        var methods = groups.Select(g => g.Key).ToList();
        var byDept = new ReportTable { Title = "Phòng ban × hình thức (giá trị trước VAT)", Columns = { new("Phòng ban") } };
        foreach (var m in methods) byDept.Columns.Add(new(Name(m), ColumnKind.Money));
        byDept.Columns.Add(new("Tổng", ColumnKind.Money));
        foreach (var d in lines.GroupBy(x => x.Department).OrderByDescending(g => g.Sum(x => x.Amount)))
        {
            var row = new List<object?> { d.Key };
            row.AddRange(methods.Select(m => (object?)d.Where(x => x.Method == m).Sum(x => x.Amount)));
            row.Add(d.Sum(x => x.Amount));
            byDept.Rows.Add(row.ToArray());
        }
        r.Tables.Add(byDept);
        return r;
    }

    // ------------------------------------------------------------------ 3. Tiến độ xử lý đề nghị
    private async Task<ReportResult> RequestsAsync(ReportFilter f)
    {
        var r = New("requests");
        var q = _db.PurchaseRequests.AsNoTracking().Where(p => p.RequestDate >= f.From && p.RequestDate < f.ToExclusive);
        if (!string.IsNullOrEmpty(f.Department)) q = q.Where(p => f.Depts.Contains(p.Department));
        var prs = await q.Select(p => new { p.Id, p.Department, p.Status, p.EstimatedTotal, p.SubmittedAt }).ToListAsync();
        var ids = prs.Select(p => p.Id).ToList();
        var approvals = await _db.ApprovalHistories.AsNoTracking()
            .Where(h => h.DocumentType == DocType.PR && ids.Contains(h.DocumentId)
                        && (h.Action == "Trưởng bộ phận duyệt" || h.Action == "Giám đốc duyệt" || h.Action.StartsWith("Tự động duyệt")))
            .GroupBy(h => h.DocumentId).Select(g => new { Id = g.Key, At = g.Max(h => h.CreatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At);
        var firstPo = await _db.PurchaseOrders.AsNoTracking().Where(p => p.PurchaseRequestId != null && ids.Contains(p.PurchaseRequestId.Value) && p.Status != PoStatus.Cancelled)
            .GroupBy(p => p.PurchaseRequestId!.Value).Select(g => new { Id = g.Key, At = g.Min(p => p.CreatedAt) }).ToDictionaryAsync(x => x.Id, x => x.At);

        decimal? Days(DateTime? a, DateTime? b) => a.HasValue && b.HasValue && b >= a ? (decimal)(b.Value - a.Value).TotalDays : null;
        var rows = prs.Select(p => new
        {
            p.Department, p.Status,
            Approve = Days(p.SubmittedAt, approvals.TryGetValue(p.Id, out var a) ? a : null),
            ToPo = Days(approvals.TryGetValue(p.Id, out var a2) ? a2 : null, firstPo.TryGetValue(p.Id, out var po) ? po : null)
        }).ToList();
        decimal? Avg(IEnumerable<decimal?> v) { var l = v.Where(x => x.HasValue).Select(x => x!.Value).ToList(); return l.Count == 0 ? null : Math.Round(l.Average(), 1); }

        r.Kpis.Add(new("Số đề nghị", prs.Count.ToString(), prs.Sum(p => p.EstimatedTotal).ToShortMoney() + " đ dự toán"));
        r.Kpis.Add(new("Đang chờ duyệt", prs.Count(p => p.Status is PrStatus.Submitted or PrStatus.PendingDirector).ToString()));
        r.Kpis.Add(new("TB thời gian duyệt", (Avg(rows.Select(x => x.Approve))?.ToString("0.#", ViCulture) ?? "—") + " ngày", "gửi duyệt → duyệt cuối"));
        r.Kpis.Add(new("TB duyệt → đặt hàng", (Avg(rows.Select(x => x.ToPo))?.ToString("0.#", ViCulture) ?? "—") + " ngày", "duyệt → lập PO đầu tiên"));
        r.Tables.Add(new ReportTable
        {
            Title = "Đề nghị theo trạng thái", Chart = ChartKind.Bar, Width = 6,
            Columns = { new("Trạng thái"), new("Số PR", ColumnKind.Number), new("Dự toán", ColumnKind.Money) },
            Rows = prs.GroupBy(p => p.Status).OrderBy(g => g.Key).Select(g => new object?[] { g.Key.GetDisplayName(), g.Count(), g.Sum(x => x.EstimatedTotal) }).ToList(),
            Totals = new object?[] { "Tổng", prs.Count, prs.Sum(p => p.EstimatedTotal) }
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Thời gian xử lý theo phòng ban", Width = 6, Chart = ChartKind.Bar, ChartValue = 2,
            Note = "Số ngày bình quân. Duyệt: từ lúc gửi duyệt đến cấp duyệt cuối; đặt hàng: từ lúc duyệt đến khi lập PO đầu tiên.",
            Columns = { new("Phòng ban"), new("Số PR", ColumnKind.Number), new("TB ngày duyệt", ColumnKind.Days), new("TB ngày đến PO", ColumnKind.Days), new("Bị từ chối", ColumnKind.Number) },
            Rows = rows.GroupBy(x => x.Department).OrderBy(g => g.Key).Select(g => new object?[]
                { g.Key, g.Count(), Avg(g.Select(x => x.Approve)), Avg(g.Select(x => x.ToPo)), g.Count(x => x.Status == PrStatus.Rejected) }).ToList()
        });
        return r;
    }

    // ------------------------------------------------------------------ 4. Giao hàng & chất lượng
    private async Task<ReportResult> DeliveryAsync(ReportFilter f)
    {
        var r = New("delivery");
        var q = _db.PurchaseOrders.AsNoTracking().Where(p => p.Status >= PoStatus.Issued && p.Status != PoStatus.Cancelled
                                                             && p.OrderDate >= f.From && p.OrderDate < f.ToExclusive);
        if (f.VendorId.HasValue) q = q.Where(p => p.VendorId == f.VendorId);
        if (!string.IsNullOrEmpty(f.Department)) q = q.Where(p => p.PurchaseRequest != null && f.Depts.Contains(p.PurchaseRequest.Department));
        var pos = await q.Select(p => new { p.Id, p.Number, Vendor = p.Vendor != null ? p.Vendor.Name : "—", p.DeliveryDate, p.Status, p.SubTotal }).ToListAsync();
        var ids = pos.Select(p => p.Id).ToList();
        var receipts = await _db.GoodsReceipts.AsNoTracking().Where(g => ids.Contains(g.PurchaseOrderId))
            .Select(g => new { g.PurchaseOrderId, g.ReceiptDate, Acc = g.Lines.Sum(l => l.QuantityAccepted), Rej = g.Lines.Sum(l => l.QuantityRejected) }).ToListAsync();
        var today = DateTime.Today;
        var rows = pos.Select(p =>
        {
            var rc = receipts.Where(x => x.PurchaseOrderId == p.Id).ToList();
            DateTime? done = p.Status == PoStatus.Received && rc.Any() ? rc.Max(x => x.ReceiptDate) : null;
            var late = done.HasValue ? (done.Value - p.DeliveryDate).TotalDays : (today - p.DeliveryDate).TotalDays;
            return new { p.Vendor, p.Number, Done = done.HasValue, OnTime = done.HasValue && done <= p.DeliveryDate, OpenLate = !done.HasValue && p.DeliveryDate < today,
                LateDays = late > 0 ? (decimal)late : 0, Acc = rc.Sum(x => x.Acc), Rej = rc.Sum(x => x.Rej), p.DeliveryDate };
        }).ToList();
        var done = rows.Where(x => x.Done).ToList();
        r.Kpis.Add(new("PO đã gửi NCC", rows.Count.ToString(), $"{done.Count} đã nhận đủ"));
        r.Kpis.Add(new("Giao đúng hạn", Pc(Pct(done.Count(x => x.OnTime), done.Count)), "trên các PO đã nhận đủ", done.Count > 0 && Pct(done.Count(x => x.OnTime), done.Count) < 80));
        r.Kpis.Add(new("Đang trễ hạn", rows.Count(x => x.OpenLate).ToString(), "chưa nhận đủ, quá ngày giao", rows.Any(x => x.OpenLate)));
        r.Kpis.Add(new("Tỷ lệ hàng không đạt", Pc(Pct(rows.Sum(x => x.Rej), rows.Sum(x => x.Acc + x.Rej))), "số lượng bị trả lại / đã giao"));
        r.Tables.Add(new ReportTable
        {
            Title = "Theo nhà cung cấp", Chart = ChartKind.Bar, ChartValue = 3,
            Columns = { new("Nhà cung cấp"), new("Số PO", ColumnKind.Number), new("Đã nhận đủ", ColumnKind.Number), new("% đúng hạn", ColumnKind.Percent),
                        new("TB ngày trễ (PO trễ)", ColumnKind.Days), new("Đang trễ", ColumnKind.Number), new("% không đạt", ColumnKind.Percent) },
            Rows = rows.GroupBy(x => x.Vendor).OrderBy(g => g.Key).Select(g =>
            {
                var d = g.Where(x => x.Done).ToList(); var lateOnes = g.Where(x => x.LateDays > 0).ToList();
                return new object?[] { g.Key, g.Count(), d.Count, Pct(d.Count(x => x.OnTime), d.Count),
                    lateOnes.Count == 0 ? 0m : Math.Round(lateOnes.Average(x => x.LateDays), 1), g.Count(x => x.OpenLate), Pct(g.Sum(x => x.Rej), g.Sum(x => x.Acc + x.Rej)) };
            }).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Đơn hàng đang trễ hạn",
            Columns = { new("Số PO"), new("Nhà cung cấp"), new("Ngày giao cam kết", ColumnKind.Date), new("Số ngày trễ", ColumnKind.Days) },
            Rows = rows.Where(x => x.OpenLate).OrderByDescending(x => x.LateDays).Select(x => new object?[] { x.Number, x.Vendor, x.DeliveryDate, Math.Round(x.LateDays, 0) }).ToList()
        });
        return r;
    }

    // ------------------------------------------------------------------ 5. Đánh giá NCC
    private async Task<ReportResult> VendorsAsync(ReportFilter f)
    {
        var r = New("vendors");
        var vq = _db.Vendors.AsNoTracking().AsQueryable();
        if (f.VendorId.HasValue) vq = vq.Where(v => v.Id == f.VendorId);
        if (f.CategoryId.HasValue) vq = vq.Where(v => v.Categories.Any(c => c.CategoryId == f.CategoryId));
        var vendors = await vq.Select(v => new { v.Id, v.Name, v.Status, v.Rating }).ToListAsync();
        var lines = await SpendLinesAsync(f);
        var evals = await _db.VendorEvaluations.AsNoTracking().Where(e => e.CreatedAt >= f.From && e.CreatedAt < f.ToExclusive)
            .GroupBy(e => e.VendorId).Select(g => new { g.Key, N = g.Count(), Q = g.Average(e => (decimal)e.Quality), D = g.Average(e => (decimal)e.Delivery) })
            .ToDictionaryAsync(x => x.Key);
        var rfq = await _db.RfqVendors.AsNoTracking().Where(x => x.Rfq!.IssueDate >= f.From && x.Rfq.IssueDate < f.ToExclusive && x.Rfq.Status != RfqStatus.Draft)
            .GroupBy(x => x.VendorId).Select(g => new { g.Key, Invited = g.Count(), Resp = g.Count(x => x.Responded), Won = g.Count(x => x.Rfq!.AwardedVendorId == g.Key) })
            .ToDictionaryAsync(x => x.Key);
        var tender = await _db.TenderBidders.AsNoTracking().Where(x => x.Submitted && x.Tender!.PublishedAt >= f.From && x.Tender.PublishedAt < f.ToExclusive)
            .GroupBy(x => x.VendorId).Select(g => new { g.Key, Bids = g.Count(), Won = g.Count(x => x.Tender!.AwardedVendorId == g.Key && x.Tender.Status >= TenderStatus.Awarded && x.Tender.Status != TenderStatus.Cancelled) })
            .ToDictionaryAsync(x => x.Key);
        var rows = vendors.Select(v =>
        {
            var sp = lines.Where(l => l.VendorId == v.Id).ToList();
            evals.TryGetValue(v.Id, out var e); rfq.TryGetValue(v.Id, out var q); tender.TryGetValue(v.Id, out var t);
            return new { v, Spend = sp.Sum(x => x.Amount), Pos = sp.Select(x => x.PoId).Distinct().Count(), e, q, t };
        }).Where(x => x.Spend > 0 || x.q != null || x.t != null || x.e != null).OrderByDescending(x => x.Spend).ToList();
        r.Kpis.Add(new("NCC có giao dịch", rows.Count.ToString()));
        r.Kpis.Add(new("Điểm bình quân", (rows.Where(x => x.v.Rating > 0).Select(x => x.v.Rating).DefaultIfEmpty().Average()).ToString("0.00") + "/5"));
        r.Kpis.Add(new("NCC điểm < 3", rows.Count(x => x.v.Rating > 0 && x.v.Rating < 3).ToString(), "cần xem xét", rows.Any(x => x.v.Rating > 0 && x.v.Rating < 3)));
        r.Kpis.Add(new("Tỷ lệ phản hồi báo giá", Pc(Pct(rows.Sum(x => x.q?.Resp ?? 0), rows.Sum(x => x.q?.Invited ?? 0)))));
        r.Tables.Add(new ReportTable
        {
            Title = "Bảng đánh giá nhà cung cấp", Chart = ChartKind.Bar, ChartValue = 3,
            Columns = { new("Nhà cung cấp"), new("Trạng thái"), new("Điểm (5)", ColumnKind.Number), new("Giá trị mua", ColumnKind.Money), new("Số PO", ColumnKind.Number),
                        new("Lượt đánh giá", ColumnKind.Number), new("Mời báo giá", ColumnKind.Number), new("% phản hồi", ColumnKind.Percent), new("Trúng RFQ", ColumnKind.Number),
                        new("Dự thầu", ColumnKind.Number), new("Trúng thầu", ColumnKind.Number) },
            Rows = rows.Select(x => new object?[] { x.v.Name, x.v.Status.GetDisplayName(), x.v.Rating, x.Spend, x.Pos, x.e?.N ?? 0,
                x.q?.Invited ?? 0, Pct(x.q?.Resp ?? 0, x.q?.Invited ?? 0), x.q?.Won ?? 0, x.t?.Bids ?? 0, x.t?.Won ?? 0 }).ToList()
        });
        return r;
    }

    // ------------------------------------------------------------------ 6. Hiệu quả cạnh tranh
    private async Task<ReportResult> CompetitionAsync(ReportFilter f)
    {
        var r = New("competition");
        var rfqs = await _db.Rfqs.AsNoTracking().Include(x => x.AwardedVendor).Include(x => x.Lines).Include(x => x.Vendors).ThenInclude(v => v.QuoteLines).Include(x => x.Vendors).ThenInclude(v => v.Vendor)
            .Include(x => x.PurchaseRequest)
            .Where(x => x.Status == RfqStatus.Awarded && x.IssueDate >= f.From && x.IssueDate < f.ToExclusive).AsSplitQuery().ToListAsync();
        if (!string.IsNullOrEmpty(f.Department)) rfqs = rfqs.Where(x => f.Depts.Contains(x.PurchaseRequest?.Department ?? "")).ToList();
        var rfqRows = rfqs.Select(x =>
        {
            var scores = RfqEvaluation.Evaluate(x);
            var won = scores.FirstOrDefault(s => s.Quote.VendorId == x.AwardedVendorId);
            var lowest = scores.Where(s => s.Valid).OrderBy(s => s.Quote.GrandTotal).FirstOrDefault();
            return new object?[] { x.Number, x.Title, x.Vendors.Count, x.Vendors.Count(v => v.Responded), scores.Count(s => s.Valid),
                x.AwardedVendor?.Name ?? x.Vendors.FirstOrDefault(v => v.VendorId == x.AwardedVendorId)?.Vendor?.Name, won?.Rank,
                won != null && lowest != null && won.Quote.Id != lowest.Quote.Id ? "Không" : "Có",
                x.PurchaseRequest?.EstimatedTotal, won?.Quote.TotalAmount, x.PurchaseRequest == null || won == null ? null : Pct(x.PurchaseRequest.EstimatedTotal - won.Quote.TotalAmount, x.PurchaseRequest.EstimatedTotal) };
        }).ToList();
        var tenders = await _db.Tenders.AsNoTracking().Include(t => t.Bidders).Include(t => t.AwardedVendor).Include(t => t.PurchaseRequest)
            .Where(t => t.PublishedAt >= f.From && t.PublishedAt < f.ToExclusive && t.Status != TenderStatus.Cancelled).ToListAsync();
        if (!string.IsNullOrEmpty(f.Department)) tenders = tenders.Where(t => f.Depts.Contains(t.PurchaseRequest?.Department ?? "")).ToList();
        var awards = await _db.DirectAwards.AsNoTracking().Include(d => d.PurchaseRequest).Include(d => d.Vendor)
            .Where(d => d.Status == DirectAwardStatus.Approved && d.ApprovedAt >= f.From && d.ApprovedAt < f.ToExclusive).ToListAsync();
        if (!string.IsNullOrEmpty(f.Department)) awards = awards.Where(d => f.Depts.Contains(d.PurchaseRequest?.Department ?? "")).ToList();

        var avgResp = rfqRows.Count == 0 ? 0 : Math.Round((decimal)rfqRows.Average(x => (int)x[3]!), 1);
        var notLowest = rfqRows.Count(x => (string?)x[7] == "Không");
        r.Kpis.Add(new("Báo giá / RFQ (TB)", avgResp.ToString("0.#", ViCulture), $"{rfqRows.Count} RFQ đã chọn NCC", rfqRows.Count > 0 && avgResp < 3));
        r.Kpis.Add(new("Không chọn giá thấp nhất", $"{notLowest}/{rfqRows.Count}", "RFQ", notLowest > 0));
        r.Kpis.Add(new("HSDT / gói thầu (TB)", tenders.Count == 0 ? "—" : Math.Round((decimal)tenders.Average(t => t.Bidders.Count(b => b.Submitted)), 1).ToString("0.#", ViCulture), $"{tenders.Count} gói thầu"));
        r.Kpis.Add(new("Chỉ định thầu", awards.Count.ToString(), awards.Sum(d => d.NegotiatedTotal).ToShortMoney() + " đ", awards.Any(d => !string.IsNullOrEmpty(d.SplitWarning))));

        r.Tables.Add(new ReportTable
        {
            Title = "Chào hàng cạnh tranh",
            Columns = { new("Số RFQ"), new("Nội dung"), new("Mời", ColumnKind.Number), new("Phản hồi", ColumnKind.Number), new("Hợp lệ", ColumnKind.Number),
                        new("NCC trúng"), new("Hạng", ColumnKind.Number), new("Giá thấp nhất?"), new("Dự toán", ColumnKind.Money), new("Giá chọn (trước VAT)", ColumnKind.Money), new("% tiết kiệm", ColumnKind.Percent) },
            Rows = rfqRows
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Đấu thầu rộng rãi",
            Columns = { new("Số gói"), new("Tên gói thầu"), new("Trạng thái"), new("Nhận HSMT", ColumnKind.Number), new("Nộp HSDT", ColumnKind.Number), new("Hợp lệ", ColumnKind.Number),
                        new("Giá gói thầu", ColumnKind.Money), new("Giá trúng", ColumnKind.Money), new("% tiết kiệm", ColumnKind.Percent), new("Nhà thầu trúng") },
            Rows = tenders.OrderBy(t => t.PublishedAt).Select(t => new object?[] { t.Number, t.Title, t.Status.GetDisplayName(), t.Bidders.Count, t.Bidders.Count(b => b.Submitted),
                t.Bidders.Count(b => b.Valid == true && b.Qualified == true), t.PackageBudget, t.AwardPrice,
                t.AwardPrice.HasValue ? Pct(t.PackageBudget - t.AwardPrice.Value, t.PackageBudget) : null, t.Status >= TenderStatus.Awarded ? t.AwardedVendor?.Name : null }).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Chỉ định thầu đã duyệt",
            Columns = { new("Số tờ trình"), new("Phòng ban"), new("Nhà cung cấp"), new("Căn cứ"), new("Giá chào", ColumnKind.Money), new("Sau thương thảo", ColumnKind.Money),
                        new("% giảm", ColumnKind.Percent), new("Cảnh báo chia nhỏ") },
            Rows = awards.OrderBy(d => d.ApprovedAt).Select(d => new object?[] { d.Number, d.PurchaseRequest?.Department, d.Vendor?.Name, d.Reason.GetDisplayName(),
                d.ProposedTotal, d.NegotiatedTotal, d.SavingPercent, string.IsNullOrEmpty(d.SplitWarning) ? "" : "Có" }).ToList()
        });
        return r;
    }

    // ------------------------------------------------------------------ 7. Hợp đồng
    private async Task<ReportResult> ContractsAsync(ReportFilter f)
    {
        var r = New("contracts");
        var q = _db.Contracts.AsNoTracking().Where(c => c.Status == ContractStatus.Active || (c.SignedDate >= f.From && c.SignedDate < f.ToExclusive));
        if (f.VendorId.HasValue) q = q.Where(c => c.VendorId == f.VendorId);
        var list = await q.Select(c => new { c.Id, c.Number, c.Title, Vendor = c.Vendor!.Name, c.IsFramework, c.Value, c.SignedDate, c.EndDate, c.Status,
            c.PerformanceSecurityExpiry, Used = _db.PurchaseOrders.Where(p => p.ContractId == c.Id && p.Status != PoStatus.Cancelled).Sum(p => (decimal?)p.SubTotal) ?? 0 }).ToListAsync();
        var today = DateTime.Today; var soon = today.AddDays(30);
        var active = list.Where(c => c.Status == ContractStatus.Active).ToList();
        r.Kpis.Add(new("Hợp đồng đang thực hiện", active.Count.ToString(), active.Sum(c => c.Value).ToShortMoney() + " đ"));
        r.Kpis.Add(new("Đã thực hiện", Pc(Pct(active.Sum(c => c.Used), active.Sum(c => c.Value))), active.Sum(c => c.Used).ToShortMoney() + " đ"));
        r.Kpis.Add(new("Hết hạn trong 30 ngày", active.Count(c => c.EndDate <= soon).ToString(), null, active.Any(c => c.EndDate <= soon)));
        r.Kpis.Add(new("Bảo lãnh sắp / đã hết hạn", active.Count(c => c.PerformanceSecurityExpiry <= soon).ToString(), "trong 30 ngày", active.Any(c => c.PerformanceSecurityExpiry <= soon)));
        r.Tables.Add(new ReportTable
        {
            Title = "Danh sách hợp đồng", Chart = ChartKind.Bar, ChartLabel = 0, ChartValue = 7,
            Columns = { new("Số HĐ"), new("Tên hợp đồng"), new("Nhà cung cấp"), new("Loại"), new("Hiệu lực đến", ColumnKind.Date), new("Giá trị", ColumnKind.Money),
                        new("Đã thực hiện", ColumnKind.Money), new("% thực hiện", ColumnKind.Percent), new("Bảo lãnh đến", ColumnKind.Date), new("Trạng thái"), new("Cảnh báo") },
            Rows = list.OrderBy(c => c.EndDate).Select(c => new object?[] { c.Number, c.Title, c.Vendor, c.IsFramework ? "Khung" : "Trọn gói", c.EndDate, c.Value, c.Used, Pct(c.Used, c.Value),
                c.PerformanceSecurityExpiry, c.Status.GetDisplayName(),
                c.Status != ContractStatus.Active ? "" : string.Join("; ", new[] { c.EndDate < today ? "Đã hết hiệu lực" : c.EndDate <= soon ? "Sắp hết hiệu lực" : null,
                    c.PerformanceSecurityExpiry <= soon ? "Bảo lãnh sắp hết" : null }.Where(x => x != null)) }).ToList(),
            Totals = new object?[] { "Tổng", null, null, null, null, list.Sum(c => c.Value), list.Sum(c => c.Used), Pct(list.Sum(c => c.Used), list.Sum(c => c.Value)), null, null, null }
        });
        return r;
    }
}
