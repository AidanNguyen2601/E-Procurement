using System.ComponentModel.DataAnnotations;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.ViewModels;

public class LoginViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập tên đăng nhập")][Display(Name = "Tên đăng nhập")]
    public string Username { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu")][DataType(DataType.Password)][Display(Name = "Mật khẩu")]
    public string Password { get; set; } = string.Empty;
    [Display(Name = "Ghi nhớ đăng nhập")] public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class ChangePasswordViewModel
{
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu hiện tại")][DataType(DataType.Password)][Display(Name = "Mật khẩu hiện tại")]
    public string CurrentPassword { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng nhập mật khẩu mới")][StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự")]
    [DataType(DataType.Password)][Display(Name = "Mật khẩu mới")]
    public string NewPassword { get; set; } = string.Empty;
    [Required(ErrorMessage = "Vui lòng xác nhận mật khẩu mới")][Compare(nameof(NewPassword), ErrorMessage = "Xác nhận mật khẩu không khớp")]
    [DataType(DataType.Password)][Display(Name = "Xác nhận mật khẩu mới")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class DashboardViewModel
{
    public int MyPendingApprovals { get; set; }
    public int PrWaitingPurchase { get; set; }
    public int OpenRfqs { get; set; }
    public int LatePos { get; set; }
    public decimal SpendThisMonth { get; set; }
    public decimal SpendYtd { get; set; }
    public decimal Savings { get; set; }
    public List<(string Month, decimal Amount)> SpendByMonth { get; set; } = new();
    public List<(string Name, decimal Amount)> SpendByCategory { get; set; } = new();
    public List<(string Name, decimal Amount, decimal Rating)> TopVendors { get; set; } = new();
    public List<PurchaseRequest> MyRequests { get; set; } = new();
    public List<PurchaseOrder> OpenOrders { get; set; } = new();
}

/// <summary>Dòng chứng từ dùng chung cho form PR / RFQ / PO</summary>
public class LineInput
{
    public int? ItemId { get; set; }
    [StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Range(0, 10_000_000)] public decimal Quantity { get; set; } = 1;
    [Range(0, 100_000_000_000)] public decimal Price { get; set; }
}

public class PrFormViewModel
{
    public int Id { get; set; }
    [Display(Name = "Số PR")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập nội dung đề nghị"), StringLength(200)][Display(Name = "Nội dung đề nghị")]
    public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày cần hàng")] public DateTime NeededDate { get; set; } = DateTime.Today.AddDays(14);
    [Display(Name = "Mức độ")] public PrPriority Priority { get; set; } = PrPriority.Normal;
    [StringLength(1000)][Display(Name = "Mục đích sử dụng")] public string? Purpose { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class PoFormViewModel
{
    public int Id { get; set; }
    [Display(Name = "Số PO")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn nhà cung cấp")][Display(Name = "Nhà cung cấp")] public int? VendorId { get; set; }
    public int? PurchaseRequestId { get; set; }
    public int? RfqId { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày đặt hàng")] public DateTime OrderDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày giao hàng")] public DateTime DeliveryDate { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(300)][Display(Name = "Địa điểm giao hàng")] public string? DeliveryAddress { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [StringLength(2000)][Display(Name = "Ghi chú / điều khoản khác")] public string? Notes { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class RfqCreateViewModel
{
    public int? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    [Required(ErrorMessage = "Nhập tiêu đề"), StringLength(200)][Display(Name = "Tiêu đề")] public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Hạn nộp báo giá")] public DateTime Deadline { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(2000)][Display(Name = "Yêu cầu / điều kiện")] public string? Requirements { get; set; } = "Hàng mới 100%, có CO/CQ. Giá đã bao gồm vận chuyển đến kho. Ghi rõ thời gian giao hàng và bảo hành.";
    [Display(Name = "Mời nhà cung cấp")] public List<int> VendorIds { get; set; } = new();
    public List<LineInput> Lines { get; set; } = new();
    public List<Vendor> SuggestedVendors { get; set; } = new();
    public List<Vendor> OtherVendors { get; set; } = new();
}

public class QuoteEntryViewModel
{
    public int RfqVendorId { get; set; }
    public RfqVendor? RfqVendor { get; set; }
    [Range(0, 365)][Display(Name = "Thời gian giao (ngày)")] public int? LeadTimeDays { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [StringLength(200)][Display(Name = "Bảo hành")] public string? Warranty { get; set; }
    [Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public List<QuoteEntryLine> Lines { get; set; } = new();
}

public class QuoteEntryLine
{
    public int RfqLineId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public decimal Quantity { get; set; }
    [Range(0, 100_000_000_000)] public decimal UnitPrice { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public class ComparisonViewModel
{
    public Rfq Rfq { get; set; } = null!;
    public List<RfqVendor> Responded { get; set; } = new();
    /// <summary>Giá thấp nhất của từng dòng RFQ</summary>
    public Dictionary<int, decimal> LowestByLine { get; set; } = new();
    public int? LowestTotalVendorId { get; set; }
    public decimal ReferenceTotal { get; set; }
}

public class ReceiptFormViewModel
{
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày nhận")] public DateTime ReceiptDate { get; set; } = DateTime.Today;
    [StringLength(50)][Display(Name = "Số phiếu giao hàng NCC")] public string? DeliveryNoteNo { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public List<ReceiptLineInput> Lines { get; set; } = new();
}

public class ReceiptLineInput
{
    public int PurchaseOrderLineId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? Unit { get; set; }
    public decimal Ordered { get; set; }
    public decimal Received { get; set; }
    [Range(0, 10_000_000)] public decimal QuantityAccepted { get; set; }
    [Range(0, 10_000_000)] public decimal QuantityRejected { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public class ApprovalInboxViewModel
{
    public List<PurchaseRequest> Requests { get; set; } = new();
    public List<PurchaseOrder> Orders { get; set; } = new();
}

public class UserFormViewModel
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên đăng nhập"), StringLength(50, MinimumLength = 3)]
    [RegularExpression(@"^[a-zA-Z0-9_.]+$", ErrorMessage = "Chỉ dùng chữ không dấu, số, dấu chấm, gạch dưới")]
    [Display(Name = "Tên đăng nhập")] public string Username { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập họ tên"), StringLength(150)][Display(Name = "Họ và tên")] public string FullName { get; set; } = string.Empty;
    [EmailAddress, StringLength(150)][Display(Name = "Email")] public string? Email { get; set; }
    [Required(ErrorMessage = "Nhập phòng ban"), StringLength(100)][Display(Name = "Phòng ban")] public string Department { get; set; } = string.Empty;
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự")][DataType(DataType.Password)][Display(Name = "Mật khẩu")]
    public string? Password { get; set; }
    [Required][Display(Name = "Vai trò")] public string Role { get; set; } = AppRoles.Requester;
    [Display(Name = "Kích hoạt")] public bool IsActive { get; set; } = true;
}
