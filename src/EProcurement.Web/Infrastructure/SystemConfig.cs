using System.Text.RegularExpressions;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

// =====================================================================================================
// Các nhóm cấu hình chỉnh trên web (lưu bằng SettingsStore). Mỗi lớp có giá trị mặc định = hành vi trước đây.
// =====================================================================================================

/// <summary>Một loại cảnh báo nhắc hạn: bật / tắt + ngưỡng</summary>
public class AlertRule
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Ngưỡng (ngày hoặc %, tùy loại)</summary>
    public int Value { get; set; }
}

public record AlertDef(string Key, string Label, string Description, string Unit, int Default, int Min, int Max);

public class AlertSettings
{
    public static readonly AlertDef[] Defs =
    {
        new("rfq-deadline", "Hạn nộp báo giá", "Nhắc người mua khi RFQ sắp hết hạn nộp báo giá", "ngày trước hạn", 1, 0, 30),
        new("tender-closing", "Đóng thầu", "Nhắc tổ chuyên gia và bên mời thầu trước giờ đóng thầu", "ngày trước hạn", 2, 0, 30),
        new("po-late", "Giao hàng trễ", "Cảnh báo đơn hàng quá hạn giao (sau số ngày ân hạn)", "ngày ân hạn", 0, 0, 60),
        new("contract-expiry", "Hợp đồng sắp hết hạn", "Cảnh báo hợp đồng và bảo lãnh thực hiện sắp hết hiệu lực", "ngày trước hạn", 30, 1, 365),
        new("guarantee-expiry", "Bảo lãnh tạm ứng / bảo hành", "Cảnh báo bảo lãnh sắp hết hiệu lực", "ngày trước hạn", 30, 1, 365),
        new("invoice-due", "Hạn thanh toán hóa đơn", "Nhắc kế toán hóa đơn sắp đến hạn / quá hạn thanh toán", "ngày trước hạn", 3, 0, 60),
        new("vendor-doc", "Hồ sơ pháp lý NCC", "Cảnh báo giấy tờ của NCC đang hợp tác sắp hết hiệu lực", "ngày trước hạn", 30, 1, 365),
        new("budget", "Ngân sách", "Cảnh báo khi ngân sách phòng ban đã dùng tới ngưỡng", "% đã dùng", 90, 50, 100),
        new("stale-approval", "Chứng từ chờ duyệt lâu", "Nhắc người duyệt khi đề nghị / đơn hàng chờ duyệt quá số ngày (nhắc lại mỗi tuần)", "ngày", 2, 1, 30),
        new("package-due-soon", "Gói sắp đến hạn giai đoạn", "Nhắc người phụ trách gói trước hạn của giai đoạn hiện tại (theo hạn mức & thời gian gói)", "ngày trước hạn", 2, 0, 30),
        new("package-overdue", "Gói quá hạn giai đoạn", "Cảnh báo người phụ trách gói và Giám đốc khi gói quá thời gian của giai đoạn hiện tại (nhắc lại mỗi tuần)", "ngày ân hạn", 0, 0, 60),
        new("rfq-open", "Đủ điều kiện mở báo giá", "Báo người mua khi RFQ đã đủ điều kiện mở báo giá", "", 0, 0, 0),
        new("tender-open", "Đến giờ mở thầu", "Báo bên mời thầu khi đã đến giờ đóng thầu để mở thầu", "", 0, 0, 0),
    };

    public List<AlertRule> Rules { get; set; } = new();

    public AlertRule Get(string key)
    {
        var def = Defs.First(d => d.Key == key);
        var r = Rules.FirstOrDefault(x => x.Key == key);
        return r == null ? new AlertRule { Key = key, Enabled = true, Value = def.Default }
                         : new AlertRule { Key = key, Enabled = r.Enabled, Value = def.Max == 0 ? 0 : Math.Clamp(r.Value, def.Min, def.Max) };
    }
    public bool On(string key) => Get(key).Enabled;
    public int Val(string key) => Get(key).Value;

    /// <summary>Mặc định lấy từ appsettings (ExpiryWarningDays, StaleApprovalDays, BudgetWarnPercent) để giữ hành vi cũ</summary>
    public static AlertSettings FromLegacy(int expiryDays, int staleDays, int budgetPercent) => new()
    {
        Rules =
        {
            new() { Key = "contract-expiry", Value = expiryDays }, new() { Key = "guarantee-expiry", Value = expiryDays },
            new() { Key = "vendor-doc", Value = expiryDays }, new() { Key = "stale-approval", Value = staleDays }, new() { Key = "budget", Value = budgetPercent }
        }
    };
}

/// <summary>Máy chủ gửi email (ghi đè appsettings khi đã lưu trên web)</summary>
public class EmailConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "eprocurement@company.local";
    public string FromName { get; set; } = "E-Procurement";
    /// <summary>Địa chỉ trang web để tạo liên kết trong email</summary>
    public string BaseUrl { get; set; } = "http://localhost:8110";

    public static EmailConfig FromLegacy(NotificationSettings s) => new()
    {
        Enabled = s.Email.Enabled, Host = s.Email.Host, Port = s.Email.Port, EnableSsl = s.Email.EnableSsl, UserName = s.Email.UserName,
        Password = s.Email.Password, From = s.Email.From, FromName = s.Email.FromName, BaseUrl = s.BaseUrl
    };
}

/// <summary>Bật / tắt thông báo theo loại chứng từ</summary>
public class NotifyChannel
{
    public string Key { get; set; } = "";
    public bool InApp { get; set; } = true;
    public bool Email { get; set; } = true;
}

/// <summary>Mẫu email thông báo + kênh theo loại chứng từ</summary>
public class NotifyTemplates
{
    public const string DefaultSubject = "[E-Procurement] {TieuDe}";
    public const string DefaultBody =
        "<p>Kính gửi {NguoiNhan},</p>\n<p><b>{TieuDe}</b></p>\n<p>{NoiDung}</p>\n<p><a href=\"{LienKet}\">Mở trên E-Procurement</a></p>\n" +
        "<p style=\"color:#888;font-size:12px\">Email tự động lúc {ThoiGian} – tắt nhận email trong trang Thông báo.</p>";

    public static readonly (string Token, string Meaning)[] Tokens =
    {
        ("{TieuDe}", "Tiêu đề thông báo"), ("{NoiDung}", "Nội dung chi tiết"), ("{NguoiNhan}", "Họ tên người nhận"),
        ("{LienKet}", "Đường dẫn tới chứng từ"), ("{ThoiGian}", "Thời điểm tạo thông báo"), ("{CongTy}", "Tên công ty (thiết lập biểu mẫu)")
    };

    /// <summary>Nhóm loại thông báo: khóa = tên DocType, "reminder" = nhắc hạn không gắn chứng từ</summary>
    public static readonly (string Key, string Label)[] Groups =
    {
        ("PR", "Đề nghị mua hàng"), ("PO", "Đơn mua hàng"), ("RFQ", "Chào giá cạnh tranh"), ("DirectAward", "Chỉ định / mua sắm trực tiếp"),
        ("Tender", "Đấu thầu"), ("Contract", "Hợp đồng"), ("Invoice", "Hóa đơn"), ("Payment", "Thanh toán"), ("Prequalification", "Sơ tuyển NCC"),
        ("StandardPriceList", "Bảng giá tiêu chuẩn"), ("SelectionPlan", "Kế hoạch lựa chọn NCC"), ("Plan", "Kế hoạch mua sắm năm"),
        ("other", "Nhắc hạn khác (ngân sách, hồ sơ NCC…)")
    };

    public string Subject { get; set; } = DefaultSubject;
    public string Body { get; set; } = DefaultBody;
    public List<NotifyChannel> Channels { get; set; } = new();

    public NotifyChannel Channel(DocType? t)
    {
        var key = t?.ToString() ?? "other";
        if (Groups.All(g => g.Key != key)) key = "other";
        return Channels.FirstOrDefault(c => c.Key == key) ?? new NotifyChannel { Key = key };
    }

    public static string Render(string template, IDictionary<string, string> values, bool html)
    {
        var s = template;
        foreach (var (k, v) in values) s = s.Replace(k, html ? System.Net.WebUtility.HtmlEncode(v) : v);
        return s;
    }
}

/// <summary>Mẫu đánh số chứng từ</summary>
public class NumberRule
{
    public string Key { get; set; } = "";
    public string Pattern { get; set; } = NumberingSettings.DefaultPattern;
    public int Digits { get; set; } = 4;
}

public record NumberDef(string Key, string Label);

public class NumberingSettings
{
    public const string DefaultPattern = "{MA}-{YYYY}-{STT}";
    /// <summary>Khóa = tiền tố mặc định đang dùng trong mã nguồn</summary>
    public static readonly NumberDef[] Defs =
    {
        new("PR", "Đề nghị mua hàng"), new("GM", "Gói mua sắm"), new("KH", "Kế hoạch lựa chọn NCC"), new("RFQ", "Hồ sơ chào giá cạnh tranh"),
        new("DA", "Hồ sơ chào giá chỉ định"), new("TD", "Gói thầu / hồ sơ mời thầu"), new("HD", "Hợp đồng"), new("PO", "Đơn mua hàng"),
        new("GRN", "Phiếu nhận hàng"), new("HDV", "Hóa đơn"), new("TT", "Đề nghị thanh toán"), new("ST", "Đợt sơ tuyển"), new("BG", "Bảng giá tiêu chuẩn"),
    };
    public static readonly (string Token, string Meaning)[] Tokens =
    {
        ("{MA}", "Mã viết tắt (cột Mã)"), ("{YYYY}", "Năm 4 số"), ("{YY}", "Năm 2 số"), ("{MM}", "Tháng"), ("{DD}", "Ngày"), ("{STT}", "Số thứ tự – bắt buộc, đặt cuối"),
    };

    public List<NumberRule> Rules { get; set; } = new();
    /// <summary>Mã viết tắt thay cho tiền tố mặc định (khóa → mã)</summary>
    public Dictionary<string, string> Codes { get; set; } = new();

    private static readonly Regex Allowed = new(@"^[A-Za-z0-9\-_/.{}]+$", RegexOptions.Compiled);

    /// <summary>Kiểm tra mẫu: có đúng một {STT} ở cuối, chỉ ký tự cho phép, độ dài mã ≤ 30</summary>
    public static string? Validate(string pattern, string code, int digits)
    {
        if (string.IsNullOrWhiteSpace(pattern) || !Allowed.IsMatch(pattern)) return "Mẫu chỉ gồm chữ, số, - _ / . và các mã {…}.";
        if (Regex.Matches(pattern, @"\{STT\}").Count != 1 || !pattern.EndsWith("{STT}")) return "Mẫu phải có đúng một {STT} và đặt ở cuối.";
        var unknown = Regex.Matches(pattern, @"\{[^}]*\}").Select(m => m.Value).Where(t => Tokens.All(k => k.Token != t)).ToList();
        if (unknown.Count > 0) return "Mã không hợp lệ: " + string.Join(", ", unknown);
        if (string.IsNullOrWhiteSpace(code) || !Regex.IsMatch(code, "^[A-Za-z0-9]{1,10}$")) return "Mã viết tắt 1–10 chữ / số.";
        if (digits is < 2 or > 8) return "Số chữ số STT từ 2 đến 8.";
        if (Head(pattern, code, new DateTime(2026, 12, 31)).Length + digits > 30) return "Số chứng từ vượt 30 ký tự.";
        return null;
    }

    /// <summary>Phần đầu số chứng từ (trước STT) tại ngày cho trước</summary>
    public static string Head(string pattern, string code, DateTime d) =>
        pattern.Replace("{MA}", code).Replace("{YYYY}", d.ToString("yyyy")).Replace("{YY}", d.ToString("yy"))
               .Replace("{MM}", d.ToString("MM")).Replace("{DD}", d.ToString("dd")).Replace("{STT}", "");

    public static string Sample(string pattern, string code, int digits) => Head(pattern, code, DateTime.Today) + 1.ToString().PadLeft(digits, '0');
}

/// <summary>Quyền theo vai trò: chức năng (controller) bị chặn với vai trò nào. Chỉ thu hẹp quyền mặc định, không mở rộng.</summary>
public class PermissionSettings
{
    /// <summary>Khóa chức năng → danh sách vai trò bị chặn</summary>
    public Dictionary<string, List<string>> Denied { get; set; } = new();
    public bool IsDenied(string feature, string role) =>
        role != AppRoles.Admin && Denied.TryGetValue(feature, out var roles) && roles.Contains(role);
}

/// <summary>Tiêu chí đánh giá NCC định kỳ: tên hiển thị, trọng số, hướng dẫn chấm (4 tiêu chí cố định)</summary>
public class SupplierCriterion
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public int Weight { get; set; } = 25;
    public string? Guide { get; set; }
}

public class SupplierCriteriaSettings
{
    public List<SupplierCriterion> Items { get; set; } = Defaults();

    public static List<SupplierCriterion> Defaults() => new()
    {
        new() { Key = "Quality", Label = "Chất lượng", Weight = 25, Guide = "5: không lỗi · 3: lỗi nhỏ, khắc phục ngay · 1: lỗi nghiêm trọng / trả hàng" },
        new() { Key = "Delivery", Label = "Tiến độ giao hàng", Weight = 25, Guide = "5: đúng / sớm hạn · 3: trễ ≤ 3 ngày · 1: trễ > 7 ngày" },
        new() { Key = "Price", Label = "Giá cả", Weight = 25, Guide = "5: thấp hơn thị trường · 3: ngang thị trường · 1: cao hơn đáng kể" },
        new() { Key = "Service", Label = "Dịch vụ hỗ trợ", Weight = 25, Guide = "5: phản hồi nhanh, hỗ trợ tốt · 1: khó liên lạc" },
    };

    public SupplierCriterion Of(string key) => Items.FirstOrDefault(i => i.Key == key) ?? Defaults().First(i => i.Key == key);

    /// <summary>Điểm tổng hợp có trọng số (thang 1–5)</summary>
    public decimal Score(decimal quality, decimal delivery, decimal price, decimal service)
    {
        var w = Items.Sum(i => Math.Max(0, i.Weight));
        if (w == 0) return (quality + delivery + price + service) / 4;
        return (quality * Of("Quality").Weight + delivery * Of("Delivery").Weight + price * Of("Price").Weight + service * Of("Service").Weight) / w;
    }
}

/// <summary>Thông tin công ty in trên đầu biểu mẫu</summary>
public class PrintSettings
{
    public string CompanyName { get; set; } = "";
    public string? Address { get; set; }
    public string? TaxCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    /// <summary>Dòng chữ nhỏ cuối trang in (vd: mã biểu mẫu ISO)</summary>
    public string? Footer { get; set; }
    public bool ShowNationalHeader { get; set; } = true;
}

public static class SupplierCriteriaLoader
{
    /// <summary>Đọc trực tiếp từ database (dùng trong tính toán tĩnh không có SettingsStore)</summary>
    public static async Task<SupplierCriteriaSettings> LoadAsync(EProcurement.Web.Data.EProcDbContext db)
    {
        var json = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
            db.AppSettings.Where(s => s.Key == SettingsStore.SupplierCriteria).Select(s => s.Value));
        if (string.IsNullOrWhiteSpace(json)) return new SupplierCriteriaSettings();
        try { return System.Text.Json.JsonSerializer.Deserialize<SupplierCriteriaSettings>(json) ?? new(); } catch (System.Text.Json.JsonException) { return new(); }
    }
}
