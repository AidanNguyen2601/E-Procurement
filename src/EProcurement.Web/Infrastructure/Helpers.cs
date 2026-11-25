using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Ngưỡng phê duyệt – cấu hình tại appsettings.json, mục "Procurement"</summary>
public class ProcurementSettings
{
    /// <summary>PR có tổng dự toán vượt ngưỡng này cần thêm Giám đốc duyệt (sau trưởng bộ phận)</summary>
    public decimal PrDirectorThreshold { get; set; } = 50_000_000;
    /// <summary>PO có tổng giá trị vượt ngưỡng này phải được Giám đốc duyệt trước khi gửi NCC</summary>
    public decimal PoApprovalThreshold { get; set; } = 100_000_000;
    /// <summary>Kiểm soát ngân sách khi gửi / duyệt PR: Off = tắt, Warn = cảnh báo (người duyệt phải ghi ý kiến khi duyệt vượt), Block = chặn</summary>
    public string BudgetControl { get; set; } = "Warn";
    /// <summary>Cảnh báo khi ngân sách đã dùng từ mức này (%)</summary>
    public int BudgetWarnPercent { get; set; } = 90;
    /// <summary>Quyết định phê duyệt phải ký xác nhận bằng mật khẩu (chữ ký điện tử nội bộ)</summary>
    public bool RequireSignature { get; set; } = true;
    /// <summary>Số NCC tối thiểu mời báo giá cho mỗi RFQ</summary>
    public int MinVendorsPerRfq { get; set; } = 3;
    /// <summary>Số báo giá hợp lệ (đủ dòng, đạt kỹ thuật) tối thiểu; ít hơn → kết quả phải trình Giám đốc duyệt</summary>
    public int MinValidQuotes { get; set; } = 3;
    /// <summary>Địa chỉ giao hàng mặc định trên PO</summary>
    public string DefaultDeliveryAddress { get; set; } = "Kho công ty – Tầng 1, tòa nhà văn phòng";

    // ----- Hình thức lựa chọn NCC -----
    /// <summary>Dự toán ≤ mức này: chào hàng cạnh tranh; vượt: đấu thầu rộng rãi</summary>
    public decimal CompetitiveQuotationMax { get; set; } = 5_000_000_000;
    /// <summary>Mua sắm trực tiếp: PO tham chiếu phải đặt trong vòng N tháng</summary>
    public int DirectPurchaseMonths { get; set; } = 12;
    /// <summary>Mua sắm trực tiếp: tổng khối lượng mua thêm không vượt % khối lượng PO tham chiếu</summary>
    public decimal DirectPurchaseMaxQuantityPercent { get; set; } = 130;
    /// <summary>Chỉ định thầu: giá trị sau thương thảo (trước VAT) tối đa; vượt chỉ được khi khẩn cấp</summary>
    public decimal DirectAwardMax { get; set; } = 100_000_000;
    /// <summary>Chỉ định thầu: cửa sổ (ngày) cộng dồn các tờ trình cùng NCC / cùng nhóm hàng để phát hiện chia nhỏ gói</summary>
    public int DirectAwardSplitWindowDays { get; set; } = 90;
    /// <summary>Dung lượng tối đa mỗi file đính kèm (MB)</summary>
    public int MaxAttachmentMb { get; set; } = 10;
    // ----- Đấu thầu rộng rãi -----
    /// <summary>Thời gian tối thiểu từ phát hành HSMT đến đóng thầu (ngày)</summary>
    public int TenderMinBiddingDays { get; set; } = 10;
    /// <summary>Sửa đổi HSMT phải trước thời điểm đóng thầu tối thiểu N ngày; nếu không phải gia hạn</summary>
    public int TenderAmendmentMinDays { get; set; } = 3;
    /// <summary>Bảng giá tiêu chuẩn: giá trần mặc định = giá tiêu chuẩn + N%</summary>
    public decimal StandardPriceCeilingPercent { get; set; } = 10;
    /// <summary>Đấu thầu: bắt buộc báo cáo thẩm định (đồng ý) trước khi Giám đốc phê duyệt kết quả lựa chọn nhà thầu</summary>
    public bool TenderAppraisalRequired { get; set; } = true;
    /// <summary>Nhà thầu gửi đề nghị làm rõ HSMT qua cổng NCC chậm nhất N ngày trước thời điểm đóng thầu</summary>
    public int TenderQuestionMinDays { get; set; } = 3;
    /// <summary>Số thành viên tổ chuyên gia tối thiểu</summary>
    public int TenderMinCommittee { get; set; } = 3;
    /// <summary>Thời gian nhà thầu được kiến nghị kết quả (ngày) – chưa hết hạn / còn kiến nghị chưa giải quyết thì chưa ký HĐ</summary>
    public double TenderComplaintDays { get; set; } = 5;
}

public static class FormatExtensions
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");
    public static string ToMoney(this decimal value) => value.ToString("N0", Vi);
    public static string ToQty(this decimal value) => value.ToString("0.###", Vi);
    public static string ToShortMoney(this decimal value) =>
        value >= 1_000_000_000 ? (value / 1_000_000_000m).ToString("0.##", Vi) + I18n.T(" tỷ")
        : value >= 1_000_000 ? (value / 1_000_000m).ToString("0.#", Vi) + I18n.T(" tr")
        : value.ToString("N0", Vi);
    public static string ToDate(this DateTime value) => value.ToString("dd/MM/yyyy");
    public static string ToDate(this DateTime? value) => value?.ToString("dd/MM/yyyy") ?? "";
    public static string ToDateTime(this DateTime value) => value.ToString("dd/MM/yyyy HH:mm");
    public static string ToDateTime(this DateTime? value) => value?.ToString("dd/MM/yyyy HH:mm") ?? "";
    public static string GetDisplayName(this Enum value)
    {
        var member = value.GetType().GetMember(value.ToString()).FirstOrDefault();
        return I18n.T(member?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? value.ToString());
    }
    public static string Stars(this decimal rating) =>
        rating <= 0 ? I18n.T("Chưa đánh giá") : new string('★', (int)Math.Round(rating)) + new string('☆', 5 - (int)Math.Round(rating)) + $" {rating:0.0}";
}

public static class ClaimsExtensions
{
    public const string FullNameClaim = "FullName";
    public const string DepartmentClaim = "Department";
    public static int GetUserId(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");
    public static string GetFullName(this ClaimsPrincipal u) => u.FindFirstValue(FullNameClaim) ?? u.Identity?.Name ?? "";
    public static string GetDepartment(this ClaimsPrincipal u) => u.FindFirstValue(DepartmentClaim) ?? "";
    public static bool IsPurchasing(this ClaimsPrincipal u) => u.IsInRole(AppRoles.Admin) || u.IsInRole(AppRoles.Buyer);
    public static bool IsAdmin(this ClaimsPrincipal u) => u.IsInRole(AppRoles.Admin);
}

public class PagedList<T>
{
    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
    public PagedList(IReadOnlyList<T> items, int totalCount, int page, int pageSize)
    { Items = items; TotalCount = totalCount; Page = page; PageSize = pageSize; }
}

/// <summary>Chống dò mật khẩu: khóa tạm sau N lần sai liên tiếp (dùng cho tài khoản nội bộ và tài khoản NCC)</summary>
public static class LoginGuard
{
    public const int MaxFailed = 5;
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(15);

    public static bool IsLocked(DateTime? lockoutEnd) => lockoutEnd.HasValue && lockoutEnd > DateTime.Now;

    /// <summary>Ghi nhận một lần sai; trả về true nếu tài khoản vừa bị khóa</summary>
    public static bool Fail(ref int failed, ref DateTime? lockoutEnd)
    {
        failed++;
        if (failed < MaxFailed) return false;
        failed = 0; lockoutEnd = DateTime.Now.Add(LockFor);
        return true;
    }

    public static string LockedMessage(DateTime lockoutEnd) =>
        $"Tài khoản tạm khóa do nhập sai mật khẩu {MaxFailed} lần, thử lại sau {lockoutEnd:HH:mm}.";
}
