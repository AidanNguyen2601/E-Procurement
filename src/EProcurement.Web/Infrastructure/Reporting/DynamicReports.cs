using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure.Reporting;

public enum FieldType { Text, Number, Money, Date, Bool, Enum }

/// <summary>Trường được phép dùng trong báo cáo động. Sql chỉ do lập trình viên định nghĩa – người dùng chỉ chọn Key.</summary>
public class FieldDef
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public FieldType Type { get; init; }
    /// <summary>Biểu thức SQL gốc (dùng để lọc)</summary>
    public string Sql { get; init; } = "";
    /// <summary>Enum: kiểu enum để dịch giá trị → tên hiển thị</summary>
    [JsonIgnore] public Type? EnumType { get; init; }
    /// <summary>Biểu thức hiển thị (enum → CASE … tên tiếng Việt; bool → Có/Không)</summary>
    public string DisplaySql => Type switch
    {
        FieldType.Enum when EnumType != null => "CASE " + Sql + string.Concat(Enum.GetValues(EnumType).Cast<Enum>()
            .Select(e => $" WHEN {Convert.ToInt32(e)} THEN N'{e.GetDisplayName().Replace("'", "''")}'")) + " END",
        FieldType.Bool => $"CASE WHEN {Sql} = 1 THEN N'Có' WHEN {Sql} = 0 THEN N'Không' END",
        _ => Sql
    };
    public bool Numeric => Type is FieldType.Number or FieldType.Money;
    /// <summary>Danh sách giá trị cho bộ lọc enum / bool</summary>
    public List<KeyValuePair<string, string>>? Options => Type switch
    {
        FieldType.Enum when EnumType != null => Enum.GetValues(EnumType).Cast<Enum>().Select(e => new KeyValuePair<string, string>(Convert.ToInt32(e).ToString(), e.GetDisplayName())).ToList(),
        FieldType.Bool => new() { new("1", "Có"), new("0", "Không") },
        _ => null
    };
}

public class DatasetDef
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>FROM … JOIN … (cố định)</summary>
    public string From { get; init; } = "";
    /// <summary>Điều kiện cố định luôn áp dụng (vd. bỏ chứng từ hủy) – có thể rỗng</summary>
    public string? BaseWhere { get; init; }
    public List<FieldDef> Fields { get; init; } = new();
    public FieldDef? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);
}

// ------------------------------------------------------------------ Thiết kế báo cáo (lưu / xuất / nhập dạng JSON)
public class ReportDesign
{
    public int Version { get; set; } = 1;
    public string Dataset { get; set; } = "";
    public List<DesignColumn> Columns { get; set; } = new();
    public List<DesignFilter> Filters { get; set; } = new();
    public List<DesignSort> Sorts { get; set; } = new();
    [Range(1, 5000)] public int Top { get; set; } = 500;
    /// <summary>none | bar | column</summary>
    public string Chart { get; set; } = "none";
    /// <summary>Chỉ số cột làm nhãn / giá trị biểu đồ</summary>
    public int ChartLabel { get; set; }
    public int ChartValue { get; set; } = 1;
}

public class DesignColumn
{
    public string Field { get; set; } = "";
    /// <summary>none | sum | count | countd | avg | min | max</summary>
    public string Agg { get; set; } = "none";
    public string? Label { get; set; }
}

public class DesignFilter
{
    public string Field { get; set; } = "";
    /// <summary>eq | ne | gt | ge | lt | le | between | contains | starts | in | null | notnull</summary>
    public string Op { get; set; } = "eq";
    public string? Value { get; set; }
    public string? Value2 { get; set; }
    /// <summary>Cho toán tử "in"</summary>
    public List<string>? Values { get; set; }
}

public class DesignSort
{
    /// <summary>Chỉ số cột trong Columns</summary>
    public int Column { get; set; }
    public bool Desc { get; set; }
}

public class DynamicResult
{
    public List<ReportColumn> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    public bool Truncated { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Báo cáo động: người dùng chọn nguồn dữ liệu, cột, phép tổng hợp, bộ lọc, sắp xếp từ danh mục định nghĩa sẵn.
/// SQL được sinh hoàn toàn từ danh mục (whitelist) – giá trị lọc luôn truyền bằng tham số → không có SQL injection.
/// </summary>
public static class DynamicReports
{
    public static readonly Dictionary<string, (string Label, bool NeedsNumeric)> Aggregates = new()
    {
        ["none"] = ("(giá trị)", false), ["sum"] = ("Tổng", true), ["count"] = ("Đếm", false), ["countd"] = ("Đếm khác nhau", false),
        ["avg"] = ("Trung bình", true), ["min"] = ("Nhỏ nhất", false), ["max"] = ("Lớn nhất", false)
    };

    public static readonly Dictionary<string, string> Operators = new()
    {
        ["eq"] = "=", ["ne"] = "≠", ["gt"] = ">", ["ge"] = "≥", ["lt"] = "<", ["le"] = "≤", ["between"] = "trong khoảng",
        ["contains"] = "chứa", ["starts"] = "bắt đầu bằng", ["in"] = "thuộc", ["null"] = "để trống", ["notnull"] = "có giá trị"
    };

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    /// <summary>Chỉ dùng cho file tải về: giữ nguyên tiếng Việt cho dễ đọc. KHÔNG dùng để nhúng vào HTML / script.</summary>
    public static readonly JsonSerializerOptions FileJson = new(Json) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static FieldDef T(string k, string l, string sql) => new() { Key = k, Label = l, Type = FieldType.Text, Sql = sql };
    private static FieldDef N(string k, string l, string sql) => new() { Key = k, Label = l, Type = FieldType.Number, Sql = sql };
    private static FieldDef M(string k, string l, string sql) => new() { Key = k, Label = l, Type = FieldType.Money, Sql = sql };
    private static FieldDef D(string k, string l, string sql) => new() { Key = k, Label = l, Type = FieldType.Date, Sql = $"CAST({sql} AS date)" };
    private static FieldDef B(string k, string l, string sql) => new() { Key = k, Label = l, Type = FieldType.Bool, Sql = sql };
    private static FieldDef E<TEnum>(string k, string l, string sql) where TEnum : Enum => new() { Key = k, Label = l, Type = FieldType.Enum, Sql = sql, EnumType = typeof(TEnum) };
    private static FieldDef[] Period(string prefix, string l, string sql) => new[]
    {
        T(prefix + "_month", l + " – tháng (yyyy-MM)", $"CONVERT(char(7), {sql}, 126)"),
        T(prefix + "_quarter", l + " – quý", $"CONCAT(YEAR({sql}), '-Q', DATEPART(quarter, {sql}))"),
        N(prefix + "_year", l + " – năm", $"YEAR({sql})")
    };

    /// <summary>Danh mục nguồn dữ liệu cho báo cáo động</summary>
    public static readonly List<DatasetDef> Datasets = new()
    {
        new DatasetDef
        {
            Key = "po_lines", Label = "Dòng đơn mua hàng (chi tiêu)", Description = "Mỗi dòng hàng trên PO, kèm NCC, nhóm hàng, phòng ban, hình thức. Phân tích chi tiêu chi tiết.",
            From = "PurchaseOrderLines l JOIN PurchaseOrders po ON po.Id = l.PurchaseOrderId LEFT JOIN Vendors v ON v.Id = po.VendorId " +
                   "LEFT JOIN Items i ON i.Id = l.ItemId LEFT JOIN Categories c ON c.Id = i.CategoryId LEFT JOIN PurchaseRequests pr ON pr.Id = po.PurchaseRequestId " +
                   "LEFT JOIN Users b ON b.Id = po.BuyerId",
            Fields = new List<FieldDef>
            {
                T("po_number", "Số PO", "po.Number"), D("order_date", "Ngày đặt hàng", "po.OrderDate"), E<PoStatus>("po_status", "Trạng thái PO", "po.Status"),
                E<ProcurementMethod>("method", "Hình thức lựa chọn NCC", "po.Method"), T("vendor", "Nhà cung cấp", "v.Name"), T("vendor_code", "Mã NCC", "v.Code"),
                T("category", "Nhóm hàng", "c.Name"), T("item_code", "Mã hàng", "i.Code"), T("description", "Diễn giải", "l.Description"), T("unit", "ĐVT", "l.Unit"),
                T("department", "Phòng ban", "pr.Department"), T("pr_number", "Số PR", "pr.Number"), T("buyer", "Người lập PO", "b.FullName"),
                N("quantity", "Số lượng", "l.Quantity"), M("unit_price", "Đơn giá", "l.UnitPrice"), M("amount", "Thành tiền (trước VAT)", "l.Amount"),
                N("received", "SL đã nhận", "l.ReceivedQuantity"), D("delivery_date", "Ngày giao cam kết", "po.DeliveryDate"), N("vat", "VAT (%)", "po.VatPercent")
            }.Concat(Period("order", "Ngày đặt hàng", "po.OrderDate")).ToList()
        },
        new DatasetDef
        {
            Key = "purchase_orders", Label = "Đơn mua hàng", Description = "Mỗi PO một dòng: giá trị, trạng thái, tiến độ giao hàng.",
            From = "PurchaseOrders po LEFT JOIN Vendors v ON v.Id = po.VendorId LEFT JOIN PurchaseRequests pr ON pr.Id = po.PurchaseRequestId LEFT JOIN Users b ON b.Id = po.BuyerId",
            Fields = new List<FieldDef>
            {
                T("po_number", "Số PO", "po.Number"), D("order_date", "Ngày đặt hàng", "po.OrderDate"), D("delivery_date", "Ngày giao cam kết", "po.DeliveryDate"),
                E<PoStatus>("po_status", "Trạng thái", "po.Status"), E<ProcurementMethod>("method", "Hình thức", "po.Method"), T("vendor", "Nhà cung cấp", "v.Name"),
                T("department", "Phòng ban", "pr.Department"), T("pr_number", "Số PR", "pr.Number"), T("buyer", "Người lập", "b.FullName"),
                M("subtotal", "Cộng tiền hàng", "po.SubTotal"), M("vat_amount", "Thuế GTGT", "po.VatAmount"), M("total", "Tổng giá trị", "po.Total"),
                B("late", "Đang trễ hạn", "CASE WHEN po.Status IN (4,5) AND po.DeliveryDate < CAST(GETDATE() AS date) THEN 1 ELSE 0 END"),
                B("from_contract", "Theo hợp đồng", "CASE WHEN po.ContractId IS NULL THEN 0 ELSE 1 END")
            }.Concat(Period("order", "Ngày đặt hàng", "po.OrderDate")).ToList()
        },
        new DatasetDef
        {
            Key = "purchase_requests", Label = "Đề nghị mua hàng", Description = "Mỗi PR một dòng: phòng ban, dự toán, trạng thái, hình thức.",
            From = "PurchaseRequests pr LEFT JOIN Users u ON u.Id = pr.RequesterId",
            Fields = new List<FieldDef>
            {
                T("pr_number", "Số PR", "pr.Number"), T("title", "Nội dung", "pr.Title"), T("department", "Phòng ban", "pr.Department"), T("requester", "Người đề nghị", "u.FullName"),
                D("request_date", "Ngày đề nghị", "pr.RequestDate"), D("needed_date", "Ngày cần hàng", "pr.NeededDate"), E<PrStatus>("status", "Trạng thái", "pr.Status"),
                E<PrPriority>("priority", "Mức độ", "pr.Priority"), E<ProcurementMethod>("method", "Hình thức", "pr.Method"), M("estimate", "Tổng dự toán", "pr.EstimatedTotal")
            }.Concat(Period("request", "Ngày đề nghị", "pr.RequestDate")).ToList()
        },
        new DatasetDef
        {
            Key = "rfq_quotes", Label = "Báo giá chào hàng cạnh tranh", Description = "Mỗi NCC được mời trong một RFQ: phản hồi, giá chào, kết quả kỹ thuật, trúng / không.",
            From = "RfqVendors rv JOIN Rfqs r ON r.Id = rv.RfqId JOIN Vendors v ON v.Id = rv.VendorId LEFT JOIN PurchaseRequests pr ON pr.Id = r.PurchaseRequestId",
            Fields = new List<FieldDef>
            {
                T("rfq_number", "Số RFQ", "r.Number"), T("rfq_title", "Tiêu đề RFQ", "r.Title"), D("issue_date", "Ngày phát hành", "r.IssueDate"), E<RfqStatus>("rfq_status", "Trạng thái RFQ", "r.Status"),
                E<EvaluationMethod>("eval_method", "Phương pháp đánh giá", "r.EvaluationMethod"), T("vendor", "Nhà cung cấp", "v.Name"), T("department", "Phòng ban", "pr.Department"),
                B("responded", "Đã báo giá", "rv.Responded"), M("quote_total", "Giá chào (trước VAT)", "rv.TotalAmount"), N("lead_time", "Thời gian giao (ngày)", "rv.LeadTimeDays"),
                B("tech_pass", "Đạt kỹ thuật", "rv.TechnicalPass"), N("tech_score", "Điểm kỹ thuật", "rv.TechnicalScore"),
                B("awarded", "Trúng", "CASE WHEN r.AwardedVendorId = rv.VendorId THEN 1 ELSE 0 END")
            }.Concat(Period("issue", "Ngày phát hành", "r.IssueDate")).ToList()
        },
        new DatasetDef
        {
            Key = "tender_bids", Label = "Hồ sơ dự thầu", Description = "Mỗi nhà thầu trong một gói thầu: nộp HSDT, giá, kết quả đánh giá, trúng thầu.",
            From = "TenderBidders tb JOIN Tenders t ON t.Id = tb.TenderId JOIN Vendors v ON v.Id = tb.VendorId LEFT JOIN PurchaseRequests pr ON pr.Id = t.PurchaseRequestId",
            Fields = new List<FieldDef>
            {
                T("tender_number", "Số gói thầu", "t.Number"), T("tender_title", "Tên gói thầu", "t.Title"), E<TenderStatus>("tender_status", "Trạng thái gói thầu", "t.Status"),
                E<EnvelopeMethod>("envelope", "Phương thức", "t.EnvelopeMethod"), D("published", "Ngày phát hành", "t.PublishedAt"), M("budget", "Giá gói thầu", "t.PackageBudget"),
                T("vendor", "Nhà thầu", "v.Name"), T("department", "Phòng ban", "pr.Department"), B("submitted", "Đã nộp HSDT", "tb.Submitted"),
                M("bid_price", "Giá dự thầu", "CASE WHEN t.OpenedAt IS NOT NULL AND (t.EnvelopeMethod = 1 OR t.FinancialOpenedAt IS NOT NULL) THEN tb.BidPrice END"),
                N("discount", "Giảm giá (%)", "CASE WHEN t.OpenedAt IS NOT NULL AND (t.EnvelopeMethod = 1 OR t.FinancialOpenedAt IS NOT NULL) THEN tb.DiscountPercent END"),
                B("valid", "Hợp lệ", "tb.Valid"), B("qualified", "Đạt năng lực", "tb.Qualified"),
                N("tech_avg", "Điểm KT trung bình", "(SELECT AVG(CAST(s.TechnicalScore AS decimal(9,2))) FROM TenderScores s WHERE s.TenderBidderId = tb.Id)"),
                B("awarded", "Trúng thầu", "CASE WHEN t.AwardedVendorId = tb.VendorId AND t.Status IN (6,7) THEN 1 ELSE 0 END"),
                M("award_price", "Giá trúng thầu", "CASE WHEN t.AwardedVendorId = tb.VendorId AND t.Status IN (6,7) THEN t.AwardPrice END")
            }.Concat(Period("published", "Ngày phát hành", "t.PublishedAt")).ToList()
        },
        new DatasetDef
        {
            Key = "direct_awards", Label = "Tờ trình chỉ định thầu", Description = "Tờ trình chỉ định: căn cứ, giá chào / thương thảo, cảnh báo chia nhỏ gói.",
            From = "DirectAwards d JOIN Vendors v ON v.Id = d.VendorId LEFT JOIN PurchaseRequests pr ON pr.Id = d.PurchaseRequestId",
            Fields = new List<FieldDef>
            {
                T("number", "Số tờ trình", "d.Number"), T("title", "Tiêu đề", "d.Title"), T("vendor", "Nhà cung cấp", "v.Name"), T("department", "Phòng ban", "pr.Department"),
                E<DirectAwardReason>("reason", "Căn cứ", "d.Reason"), E<DirectAwardStatus>("status", "Trạng thái", "d.Status"), D("created", "Ngày lập", "d.CreatedAt"),
                D("approved", "Ngày duyệt", "d.ApprovedAt"), M("proposed", "Giá NCC chào", "d.ProposedTotal"), M("negotiated", "Giá sau thương thảo", "d.NegotiatedTotal"),
                B("split_warning", "Có cảnh báo chia nhỏ", "CASE WHEN d.SplitWarning IS NULL THEN 0 ELSE 1 END")
            }.Concat(Period("created", "Ngày lập", "d.CreatedAt")).ToList()
        },
        new DatasetDef
        {
            Key = "contracts", Label = "Hợp đồng", Description = "Hợp đồng ký từ đấu thầu: giá trị, đã thực hiện, hiệu lực, bảo lãnh.",
            From = "Contracts k JOIN Vendors v ON v.Id = k.VendorId LEFT JOIN Tenders t ON t.Id = k.TenderId",
            Fields = new List<FieldDef>
            {
                T("number", "Số HĐ", "k.Number"), T("title", "Tên hợp đồng", "k.Title"), T("vendor", "Nhà cung cấp", "v.Name"), T("tender", "Gói thầu", "t.Number"),
                B("framework", "Hợp đồng khung", "k.IsFramework"), E<ContractStatus>("status", "Trạng thái", "k.Status"), D("signed", "Ngày ký", "k.SignedDate"),
                D("end_date", "Hiệu lực đến", "k.EndDate"), D("security_expiry", "Bảo lãnh đến", "k.PerformanceSecurityExpiry"), M("value", "Giá trị HĐ", "k.Value"),
                M("executed", "Đã thực hiện", "(SELECT ISNULL(SUM(p.SubTotal), 0) FROM PurchaseOrders p WHERE p.ContractId = k.Id AND p.Status <> 7)")
            }.Concat(Period("signed", "Ngày ký", "k.SignedDate")).ToList()
        },
        new DatasetDef
        {
            Key = "receipts", Label = "Nhận hàng", Description = "Mỗi dòng phiếu nhận hàng: số lượng đạt / không đạt, NCC, giao đúng hạn hay trễ.",
            From = "GoodsReceiptLines gl JOIN GoodsReceipts g ON g.Id = gl.GoodsReceiptId JOIN PurchaseOrderLines pl ON pl.Id = gl.PurchaseOrderLineId " +
                   "JOIN PurchaseOrders po ON po.Id = g.PurchaseOrderId LEFT JOIN Vendors v ON v.Id = po.VendorId",
            Fields = new List<FieldDef>
            {
                T("receipt_number", "Số phiếu nhận", "g.Number"), D("receipt_date", "Ngày nhận", "g.ReceiptDate"), T("po_number", "Số PO", "po.Number"),
                T("vendor", "Nhà cung cấp", "v.Name"), T("description", "Hàng hóa", "pl.Description"), N("accepted", "SL đạt", "gl.QuantityAccepted"),
                N("rejected", "SL không đạt", "gl.QuantityRejected"), B("late", "Giao trễ", "CASE WHEN g.ReceiptDate > po.DeliveryDate THEN 1 ELSE 0 END"),
                N("days_late", "Số ngày trễ", "CASE WHEN g.ReceiptDate > po.DeliveryDate THEN DATEDIFF(day, po.DeliveryDate, g.ReceiptDate) ELSE 0 END")
            }.Concat(Period("receipt", "Ngày nhận", "g.ReceiptDate")).ToList()
        },
        new DatasetDef
        {
            Key = "vendors", Label = "Nhà cung cấp", Description = "Danh sách NCC: trạng thái, điểm đánh giá, tổng giá trị mua.",
            From = "Vendors v",
            Fields = new List<FieldDef>
            {
                T("code", "Mã NCC", "v.Code"), T("name", "Tên NCC", "v.Name"), T("tax_code", "MST", "v.TaxCode"), E<VendorStatus>("status", "Trạng thái", "v.Status"),
                N("rating", "Điểm đánh giá", "v.Rating"),
                M("spend", "Tổng giá trị mua (PO đã duyệt)", "(SELECT ISNULL(SUM(p.SubTotal), 0) FROM PurchaseOrders p WHERE p.VendorId = v.Id AND p.Status BETWEEN 3 AND 6)"),
                N("po_count", "Số PO", "(SELECT COUNT(*) FROM PurchaseOrders p WHERE p.VendorId = v.Id AND p.Status BETWEEN 3 AND 6)"),
                D("created", "Ngày tạo", "v.CreatedAt")
            }
        }
    };

    public static DatasetDef? Dataset(string key) => Datasets.FirstOrDefault(d => d.Key == key);

    public static string ColumnLabel(DatasetDef ds, DesignColumn c)
    {
        if (!string.IsNullOrWhiteSpace(c.Label)) return c.Label.Trim();
        var f = ds.Field(c.Field);
        return c.Agg == "none" ? f?.Label ?? c.Field : $"{Aggregates.GetValueOrDefault(c.Agg).Label} {f?.Label ?? c.Field}";
    }

    // ------------------------------------------------------------------ Kiểm tra & sinh SQL
    /// <summary>Kiểm tra thiết kế theo danh mục; trả về danh sách lỗi (rỗng = hợp lệ)</summary>
    public static List<string> Validate(ReportDesign d)
    {
        var e = new List<string>();
        var ds = Dataset(d.Dataset);
        if (ds == null) { e.Add("Chọn nguồn dữ liệu."); return e; }
        if (d.Columns.Count == 0) e.Add("Chọn ít nhất một cột.");
        if (d.Columns.Count > 30) e.Add("Tối đa 30 cột.");
        foreach (var c in d.Columns)
        {
            var f = ds.Field(c.Field);
            if (f == null) { e.Add($"Trường \"{c.Field}\" không thuộc nguồn dữ liệu."); continue; }
            if (!Aggregates.TryGetValue(c.Agg, out var a)) e.Add($"Phép tổng hợp \"{c.Agg}\" không hợp lệ.");
            else if (a.NeedsNumeric && !f.Numeric) e.Add($"\"{f.Label}\": {a.Label.ToLower()} chỉ áp dụng cho trường số.");
            if (c.Label?.Length > 100) e.Add("Tên cột tối đa 100 ký tự.");
        }
        foreach (var fl in d.Filters)
        {
            var f = ds.Field(fl.Field);
            if (f == null) { e.Add($"Bộ lọc: trường \"{fl.Field}\" không hợp lệ."); continue; }
            if (!Operators.ContainsKey(fl.Op)) { e.Add($"Bộ lọc \"{f.Label}\": toán tử không hợp lệ."); continue; }
            if (fl.Op is "contains" or "starts" && f.Type != FieldType.Text) e.Add($"Bộ lọc \"{f.Label}\": \"{Operators[fl.Op]}\" chỉ dùng cho trường văn bản.");
            if (fl.Op is "gt" or "ge" or "lt" or "le" or "between" && f.Type is FieldType.Text or FieldType.Bool or FieldType.Enum)
                e.Add($"Bộ lọc \"{f.Label}\": \"{Operators[fl.Op]}\" chỉ dùng cho số / ngày.");
            if (fl.Op is "null" or "notnull") continue;
            var values = fl.Op == "in" ? fl.Values ?? new() : fl.Op == "between" ? new List<string?> { fl.Value, fl.Value2 }.Select(v => v ?? "").ToList() : new List<string> { fl.Value ?? "" };
            if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace)) { e.Add($"Bộ lọc \"{f.Label}\": nhập giá trị."); continue; }
            if (values.Count > 100) e.Add($"Bộ lọc \"{f.Label}\": tối đa 100 giá trị.");
            foreach (var v in values) if (!TryParse(f, v, out _)) e.Add($"Bộ lọc \"{f.Label}\": giá trị \"{v}\" không đúng kiểu {TypeName(f.Type)}.");
        }
        foreach (var s in d.Sorts) if (s.Column < 0 || s.Column >= d.Columns.Count) e.Add("Sắp xếp theo cột không tồn tại.");
        if (d.Top is < 1 or > 5000) e.Add("Số dòng tối đa từ 1 đến 5.000.");
        if (d.Chart is not ("none" or "bar" or "column")) e.Add("Kiểu biểu đồ không hợp lệ.");
        if (d.Chart != "none")
        {
            if (d.ChartLabel < 0 || d.ChartLabel >= d.Columns.Count || d.ChartValue < 0 || d.ChartValue >= d.Columns.Count) e.Add("Biểu đồ: chọn cột nhãn và cột giá trị.");
            else
            {
                var vc = d.Columns[d.ChartValue]; var vf = ds.Field(vc.Field);
                if (vf != null && !(vf.Numeric || vc.Agg is "count" or "countd")) e.Add("Biểu đồ: cột giá trị phải là số (hoặc phép đếm).");
            }
        }
        return e;
    }

    private static string TypeName(FieldType t) => t switch
    {
        FieldType.Number or FieldType.Money => "số", FieldType.Date => "ngày (yyyy-MM-dd)", FieldType.Bool => "Có/Không", FieldType.Enum => "danh mục", _ => "văn bản"
    };

    private static bool TryParse(FieldDef f, string raw, out object value)
    {
        raw = raw.Trim(); value = raw;
        switch (f.Type)
        {
            case FieldType.Number: case FieldType.Money:
                if (decimal.TryParse(raw.Replace(" ", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
                return false;
            case FieldType.Date:
                if (DateTime.TryParseExact(raw, new[] { "yyyy-MM-dd", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) { value = dt.Date; return true; }
                return false;
            case FieldType.Bool:
                if (raw is "1" or "0") { value = raw == "1"; return true; }
                return false;
            case FieldType.Enum:
                if (int.TryParse(raw, out var i) && f.EnumType != null && Enum.IsDefined(f.EnumType, i)) { value = i; return true; }
                return false;
            default:
                if (raw.Length > 200) return false;
                value = raw; return true;
        }
    }

    public record BuiltSql(string Sql, List<(string Name, object Value)> Parameters, List<string> Labels, List<ColumnKind> Kinds);

    /// <summary>Sinh câu SELECT có tham số từ thiết kế đã kiểm tra</summary>
    /// <param name="probe">true: lấy dư 1 dòng để biết kết quả có bị cắt (dùng khi chạy trong app)</param>
    public static BuiltSql Build(ReportDesign d, bool probe = true)
    {
        var ds = Dataset(d.Dataset)!;
        var ps = new List<(string, object)>();
        string P(object v) { var n = "@p" + ps.Count; ps.Add((n, v)); return n; }
        var grouped = d.Columns.Any(c => c.Agg != "none");
        var select = new List<string>(); var groupBy = new List<string>(); var labels = new List<string>(); var kinds = new List<ColumnKind>();
        for (var i = 0; i < d.Columns.Count; i++)
        {
            var c = d.Columns[i]; var f = ds.Field(c.Field)!;
            var expr = c.Agg switch
            {
                "sum" => $"SUM({f.Sql})", "avg" => $"AVG(CAST({f.Sql} AS decimal(19,4)))", "min" => $"MIN({f.DisplaySql})", "max" => $"MAX({f.DisplaySql})",
                "count" => $"COUNT({f.Sql})", "countd" => $"COUNT(DISTINCT {f.Sql})", _ => f.DisplaySql
            };
            if (c.Agg == "none" && grouped) groupBy.Add(f.DisplaySql);
            select.Add($"{expr} AS [c{i}]");
            labels.Add(ColumnLabel(ds, c));
            kinds.Add(c.Agg is "count" or "countd" ? ColumnKind.Number
                : f.Type switch { FieldType.Money => ColumnKind.Money, FieldType.Number => ColumnKind.Number, FieldType.Date => ColumnKind.Date, _ => ColumnKind.Text });
        }
        var where = new List<string>();
        if (!string.IsNullOrEmpty(ds.BaseWhere)) where.Add(ds.BaseWhere);
        foreach (var fl in d.Filters)
        {
            var f = ds.Field(fl.Field)!;
            object V(string? raw) { TryParse(f, raw ?? "", out var v); return v; }
            where.Add(fl.Op switch
            {
                "eq" => $"{f.Sql} = {P(V(fl.Value))}", "ne" => $"({f.Sql} <> {P(V(fl.Value))} OR {f.Sql} IS NULL)",
                "gt" => $"{f.Sql} > {P(V(fl.Value))}", "ge" => $"{f.Sql} >= {P(V(fl.Value))}", "lt" => $"{f.Sql} < {P(V(fl.Value))}", "le" => $"{f.Sql} <= {P(V(fl.Value))}",
                "between" => $"{f.Sql} BETWEEN {P(V(fl.Value))} AND {P(V(fl.Value2))}",
                "contains" => $"{f.Sql} LIKE N'%' + {P(EscapeLike(fl.Value!))} + N'%' ESCAPE '\\'",
                "starts" => $"{f.Sql} LIKE {P(EscapeLike(fl.Value!))} + N'%' ESCAPE '\\'",
                "in" => $"{f.Sql} IN ({string.Join(", ", fl.Values!.Select(v => P(V(v))))})",
                "null" => $"{f.Sql} IS NULL", _ => $"{f.Sql} IS NOT NULL"
            });
        }
        var order = d.Sorts.Select(s => $"[c{s.Column}]" + (s.Desc ? " DESC" : "")).ToList();
        if (order.Count == 0) order.Add("[c0]");
        var sb = new StringBuilder();
        sb.Append("SELECT TOP (").Append(P(probe ? d.Top + 1 : d.Top)).Append(")\n    ").Append(string.Join(",\n    ", select));
        sb.Append("\nFROM ").Append(ds.From);
        if (where.Count > 0) sb.Append("\nWHERE ").Append(string.Join("\n  AND ", where));
        if (groupBy.Count > 0) sb.Append("\nGROUP BY ").Append(string.Join(", ", groupBy));
        sb.Append("\nORDER BY ").Append(string.Join(", ", order));
        return new BuiltSql(sb.ToString(), ps, labels, kinds);
    }

    private static string EscapeLike(string s) => s.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    /// <summary>Bản SQL để xuất ra file: khai báo tham số + câu lệnh với tên cột tiếng Việt (chạy được trên SSMS)</summary>
    public static string ExportSql(ReportDesign d, string? title)
    {
        var b = Build(d, probe: false);
        var ds = Dataset(d.Dataset)!;
        var sb = new StringBuilder();
        sb.AppendLine($"-- Báo cáo: {title ?? "(chưa đặt tên)"}");
        sb.AppendLine($"-- Nguồn dữ liệu: {ds.Label}");
        sb.AppendLine($"-- Sinh tự động bởi E-Procurement lúc {DateTime.Now:dd/MM/yyyy HH:mm}. Chạy trên database EPROC_DB.");
        sb.AppendLine("SET NOCOUNT ON;");
        foreach (var (name, value) in b.Parameters)
        {
            var (type, lit) = value switch
            {
                int i => ("int", i.ToString(CultureInfo.InvariantCulture)),
                decimal m => ("decimal(19,4)", m.ToString(CultureInfo.InvariantCulture)),
                DateTime dt => ("date", $"'{dt:yyyy-MM-dd}'"),
                bool bo => ("bit", bo ? "1" : "0"),
                _ => ("nvarchar(400)", "N'" + value.ToString()!.Replace("'", "''") + "'")
            };
            sb.AppendLine($"DECLARE {name} {type} = {lit};");
        }
        sb.AppendLine();
        var sql = b.Sql;
        for (var i = b.Labels.Count - 1; i >= 0; i--) sql = sql.Replace($"[c{i}]", "[" + b.Labels[i].Replace("]", "]]") + "]");
        sb.AppendLine(sql + ";");
        return sb.ToString();
    }

    /// <summary>Chạy câu lệnh chỉ-đọc, giới hạn số dòng và thời gian</summary>
    public static async Task<DynamicResult> RunAsync(DbConnection conn, ReportDesign d)
    {
        var b = Build(d);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var close = conn.State != ConnectionState.Open;
        if (close) await conn.OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = b.Sql;
            cmd.CommandTimeout = 30;
            foreach (var (name, value) in b.Parameters)
            {
                var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value;
                if (value is string s) { p.DbType = DbType.String; p.Size = 400; }
                cmd.Parameters.Add(p);
            }
            var result = new DynamicResult { Columns = b.Labels.Select((l, i) => new ReportColumn(l, b.Kinds[i])).ToList() };
            await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            while (await r.ReadAsync())
            {
                if (result.Rows.Count >= d.Top) { result.Truncated = true; break; }
                var row = new object?[r.FieldCount];
                for (var i = 0; i < r.FieldCount; i++) row[i] = r.IsDBNull(i) ? null : Normalize(r.GetValue(i));
                result.Rows.Add(row);
            }
            result.ElapsedMs = sw.ElapsedMilliseconds;
            return result;
        }
        finally { if (close) await conn.CloseAsync(); }
    }

    private static object Normalize(object v) => v switch
    {
        int or long or short or byte or double or float => Convert.ToDecimal(v),
        _ => v
    };

    public static ReportDesign? ParseJson(string json)
    {
        try { return JsonSerializer.Deserialize<ReportDesign>(json, Json); } catch (JsonException) { return null; }
    }
}
