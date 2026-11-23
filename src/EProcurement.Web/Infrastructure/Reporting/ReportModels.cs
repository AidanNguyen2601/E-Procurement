namespace EProcurement.Web.Infrastructure.Reporting;

public enum ColumnKind { Text, Money, Number, Percent, Date, Days }

/// <summary>Kiểu biểu đồ: Bar = thanh ngang (xếp hạng), Column = cột đứng (theo thời gian)</summary>
public enum ChartKind { None, Bar, Column }

public record ReportColumn(string Title, ColumnKind Kind = ColumnKind.Text);

/// <summary>Một bảng số liệu (kèm biểu đồ một chuỗi tùy chọn) – đơn vị hiển thị và xuất Excel</summary>
public class ReportTable
{
    public string Title { get; set; } = string.Empty;
    public string? Note { get; set; }
    public List<ReportColumn> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    /// <summary>Dòng tổng (null = không có)</summary>
    public object?[]? Totals { get; set; }
    public ChartKind Chart { get; set; }
    /// <summary>Cột làm nhãn / giá trị cho biểu đồ</summary>
    public int ChartLabel { get; set; }
    public int ChartValue { get; set; } = 1;
    /// <summary>Số cột bố cục (12 = rộng toàn trang, 6 = nửa trang)</summary>
    public int Width { get; set; } = 12;
}

public record Kpi(string Label, string Value, string? Hint = null, bool Warn = false);

public class ReportResult
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<Kpi> Kpis { get; set; } = new();
    public List<ReportTable> Tables { get; set; } = new();
}

/// <summary>Bộ lọc chung của báo cáo chuẩn</summary>
public class ReportFilter
{
    public DateTime From { get; set; } = new(DateTime.Today.Year, 1, 1);
    public DateTime To { get; set; } = DateTime.Today;
    public string? Department { get; set; }
    public int? CategoryId { get; set; }
    public int? VendorId { get; set; }
    /// <summary>Ngày kết thúc dạng [To, To+1) để so sánh DateTime có giờ</summary>
    public DateTime ToExclusive => To.Date.AddDays(1);

    public string Describe(IDictionary<int, string> categories, IDictionary<int, string> vendors)
    {
        var parts = new List<string> { $"Từ {From.ToDate()} đến {To.ToDate()}" };
        if (!string.IsNullOrEmpty(Department)) parts.Add("Phòng ban: " + Department);
        if (CategoryId.HasValue && categories.TryGetValue(CategoryId.Value, out var c)) parts.Add("Nhóm hàng: " + c);
        if (VendorId.HasValue && vendors.TryGetValue(VendorId.Value, out var v)) parts.Add("NCC: " + v);
        return string.Join(" · ", parts);
    }
}

public static class ReportFormat
{
    private static readonly System.Globalization.CultureInfo Vi = System.Globalization.CultureInfo.GetCultureInfo("vi-VN");

    public static string Cell(object? v, ColumnKind kind) => v switch
    {
        null => "",
        decimal d when kind == ColumnKind.Money => d.ToMoney(),
        decimal d when kind == ColumnKind.Percent => d.ToString("0.#", Vi) + "%",
        decimal d when kind == ColumnKind.Days => d.ToString("0.#", Vi),
        decimal d => d.ToString("#,0.##", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")),
        int i when kind == ColumnKind.Money => ((decimal)i).ToMoney(),
        int i => i.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")),
        long l => l.ToString("#,0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")),
        double f => ((decimal)f).ToString("#,0.##", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToDate() : dt.ToDateTime(),
        bool b => b ? "Có" : "Không",
        _ => v.ToString() ?? ""
    };

    public static bool IsNumeric(ColumnKind k) => k is ColumnKind.Money or ColumnKind.Number or ColumnKind.Percent or ColumnKind.Days;

    public static decimal? ToDecimal(object? v) => v switch
    {
        decimal d => d, int i => i, long l => l, double f => (decimal)f, float f => (decimal)f, short s => s, byte b => b, _ => null
    };
}
