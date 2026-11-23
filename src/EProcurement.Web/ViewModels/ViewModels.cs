using System.ComponentModel.DataAnnotations;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Infrastructure.Reporting;

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
    [Range(1, 4)] public int Quarter { get; set; } = 1;
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
    /// <summary>Gói mua sắm (đã duyệt trong KHLCNT) – hồ sơ lập theo gói</summary>
    public int? PackageId { get; set; }
    public int? RfqId { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày đặt hàng")] public DateTime OrderDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày giao hàng")] public DateTime DeliveryDate { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(300)][Display(Name = "Địa điểm giao hàng")] public string? DeliveryAddress { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [StringLength(2000)][Display(Name = "Ghi chú / điều khoản khác")] public string? Notes { get; set; }
    /// <summary>Mua sắm trực tiếp: PO tham chiếu (chỉ dùng khi tạo mới; khi sửa lấy theo PO đã lưu)</summary>
    public int? ReferencePoId { get; set; }
    [StringLength(1000)][Display(Name = "Lý do đơn giá cao hơn PO tham chiếu")] public string? PriceDeviationReason { get; set; }
    [StringLength(1000)][Display(Name = "Lý do vượt giá trần bảng giá tiêu chuẩn")] public string? StandardPriceReason { get; set; }
    public List<LineInput> Lines { get; set; } = new();
}

public class RfqCreateViewModel
{
    public int? PurchaseRequestId { get; set; }
    /// <summary>Gói mua sắm (đã duyệt trong KHLCNT) – hồ sơ lập theo gói</summary>
    public int? PackageId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    [Required(ErrorMessage = "Nhập tiêu đề"), StringLength(200)][Display(Name = "Tiêu đề")] public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Hạn nộp báo giá")] public DateTime Deadline { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(2000)][Display(Name = "Yêu cầu / điều kiện")] public string? Requirements { get; set; } = "Hàng mới 100%, có CO/CQ. Giá đã bao gồm vận chuyển đến kho. Ghi rõ thời gian giao hàng và bảo hành.";
    [Display(Name = "Phương pháp đánh giá")] public EvaluationMethod EvaluationMethod { get; set; } = EvaluationMethod.LowestPrice;
    [Range(0, 100)][Display(Name = "Trọng số giá (%)")] public int PriceWeight { get; set; } = 70;
    [Range(0, 100)][Display(Name = "Trọng số kỹ thuật (%)")] public int TechnicalWeight { get; set; } = 20;
    [Range(0, 100)][Display(Name = "Trọng số tiến độ (%)")] public int DeliveryWeight { get; set; } = 10;
    [Display(Name = "Mời nhà cung cấp")] public List<int> VendorIds { get; set; } = new();
    public List<LineInput> Lines { get; set; } = new();
    public List<Vendor> SuggestedVendors { get; set; } = new();
    public List<Vendor> OtherVendors { get; set; } = new();
}

public class QuoteEntryViewModel
{
    public int RfqVendorId { get; set; }
    public RfqVendor? RfqVendor { get; set; }
    /// <summary>Ngày NCC gửi báo giá (theo email / dấu bưu điện) – phải trong hạn nộp</summary>
    [DataType(DataType.Date)][Display(Name = "Ngày NCC gửi báo giá")] public DateTime ReceivedDate { get; set; } = DateTime.Today;
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
    /// <summary>Kết quả đánh giá, xếp hạng (theo thứ tự hạng)</summary>
    public List<QuoteScore> Scores { get; set; } = new();
    public int MinValidQuotes { get; set; }
}

/// <summary>Đánh giá kỹ thuật các báo giá sau khi mở</summary>
public class TechnicalEvaluationViewModel
{
    public int RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    public List<TechnicalEvaluationLine> Lines { get; set; } = new();
}

public class TechnicalEvaluationLine
{
    public int RfqVendorId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public bool? Pass { get; set; }
    [Range(0, 100, ErrorMessage = "Điểm kỹ thuật từ 0 đến 100")] public int? Score { get; set; }
    [StringLength(500)] public string? Note { get; set; }
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
    public List<Rfq> Rfqs { get; set; } = new();
    public List<DirectAward> DirectAwards { get; set; } = new();
    public List<Tender> TenderPlans { get; set; } = new();
    public List<Tender> TenderResults { get; set; } = new();
    public List<TenderComplaint> Complaints { get; set; } = new();
    public List<SelectionPlan> SelectionPlans { get; set; } = new();
    /// <summary>Thẩm định kết quả LCNT được giao cho người dùng</summary>
    public List<TenderAppraisal> Appraisals { get; set; } = new();
    public List<Prequalification> Prequalifications { get; set; } = new();
    public List<StandardPriceList> StandardPriceLists { get; set; } = new();
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

public class DirectAwardFormViewModel
{
    public int Id { get; set; }
    [Display(Name = "Số tờ trình")] public string Number { get; set; } = string.Empty;
    public int PurchaseRequestId { get; set; }
    /// <summary>Gói mua sắm (đã duyệt trong KHLCNT) – hồ sơ lập theo gói</summary>
    public int? PackageId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    [Required(ErrorMessage = "Nhập tiêu đề"), StringLength(200)][Display(Name = "Tiêu đề")] public string Title { get; set; } = string.Empty;
    [Required(ErrorMessage = "Chọn nhà cung cấp")][Display(Name = "Nhà cung cấp được chỉ định")] public int? VendorId { get; set; }
    [Display(Name = "Căn cứ chỉ định")] public DirectAwardReason Reason { get; set; } = DirectAwardReason.SoleSource;
    [Required(ErrorMessage = "Thuyết minh lý do chỉ định"), StringLength(2000)][Display(Name = "Thuyết minh (vì sao không thể / không cần cạnh tranh)")]
    public string Justification { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày thương thảo")] public DateTime? NegotiationDate { get; set; }
    [StringLength(2000)][Display(Name = "Nội dung thương thảo")] public string? NegotiationNote { get; set; }
    [Range(0, 365)][Display(Name = "Thời gian giao (ngày)")] public int? LeadTimeDays { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [StringLength(200)][Display(Name = "Bảo hành")] public string? Warranty { get; set; }
    [Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    public List<DirectAwardLineInput> Lines { get; set; } = new();
}

public class DirectAwardLineInput
{
    public int? ItemId { get; set; }
    [StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal EstimatedPrice { get; set; }
    [Range(0, 100_000_000_000)] public decimal ProposedPrice { get; set; }
    [Range(0, 100_000_000_000)] public decimal NegotiatedPrice { get; set; }
}

/// <summary>File đính kèm hiển thị (không nạp nội dung file)</summary>
public record AttachmentInfo(int Id, string FileName, long Size, string? Note, string UploadedBy, DateTime UploadedAt, bool IsPublic = false);

/// <summary>Model cho partial _Attachments</summary>
public record AttachmentPanel(DocType DocumentType, int DocumentId, bool CanEdit, List<AttachmentInfo> Files, string ReturnUrl);

// ===================== ĐẤU THẦU RỘNG RÃI =====================
public class TenderFormViewModel
{
    public int Id { get; set; }
    [Display(Name = "Số gói thầu")] public string Number { get; set; } = string.Empty;
    public int PurchaseRequestId { get; set; }
    /// <summary>Gói mua sắm (đã duyệt trong KHLCNT) – hồ sơ lập theo gói</summary>
    public int? PackageId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    [Required(ErrorMessage = "Nhập tên gói thầu"), StringLength(200)][Display(Name = "Tên gói thầu")] public string Title { get; set; } = string.Empty;
    [Range(1, 1_000_000_000_000, ErrorMessage = "Nhập giá gói thầu")][Display(Name = "Giá gói thầu (trước VAT)")] public decimal PackageBudget { get; set; }
    [Display(Name = "Phương thức")] public EnvelopeMethod EnvelopeMethod { get; set; } = EnvelopeMethod.OneEnvelope;
    [StringLength(100)][Display(Name = "Loại hợp đồng")] public string? ContractType { get; set; } = "Trọn gói";
    [Range(1, 3650)][Display(Name = "Thời gian thực hiện HĐ (ngày)")] public int ContractDurationDays { get; set; } = 30;
    [Display(Name = "Hợp đồng khung (đặt hàng nhiều đợt)")] public bool IsFramework { get; set; }
    [Required(ErrorMessage = "Nhập yêu cầu kỹ thuật"), StringLength(4000)][Display(Name = "Yêu cầu kỹ thuật")] public string TechnicalRequirements { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tiêu chí năng lực, kinh nghiệm"), StringLength(4000)][Display(Name = "Tiêu chí năng lực, kinh nghiệm")] public string QualificationCriteria { get; set; } = string.Empty;
    [StringLength(4000)][Display(Name = "Điều kiện hợp đồng")] public string? ContractTerms { get; set; }
    [Display(Name = "Phương pháp đánh giá")] public TenderEvaluationMethod EvaluationMethod { get; set; } = TenderEvaluationMethod.LowestEvaluatedPrice;
    [Range(0, 100)][Display(Name = "Điểm kỹ thuật tối thiểu")] public int MinTechnicalScore { get; set; } = 70;
    [Range(0, 100)][Display(Name = "Trọng số kỹ thuật (%)")] public int TechnicalWeight { get; set; } = 30;
    [Range(0, 100)][Display(Name = "Trọng số giá (%)")] public int PriceWeight { get; set; } = 70;
    [Range(0, 100_000_000_000)][Display(Name = "Bảo đảm dự thầu (đ)")] public decimal BidSecurityAmount { get; set; }
    [Range(1, 365)][Display(Name = "Hiệu lực HSDT (ngày)")] public int BidValidityDays { get; set; } = 60;
    [Range(0, 30)][Display(Name = "Bảo đảm thực hiện HĐ (%)")] public decimal PerformanceSecurityPercent { get; set; } = 5;
    [Display(Name = "Thời điểm đóng thầu")] public DateTime ClosingAt { get; set; } = DateTime.Today.AddDays(15).AddHours(9);
}

public class BidEntryViewModel
{
    public int TenderBidderId { get; set; }
    public TenderBidder? Bidder { get; set; }
    [Range(0, 100)][Display(Name = "Giảm giá (%)")] public decimal DiscountPercent { get; set; }
    [Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [Range(0, 3650)][Display(Name = "Thời gian giao (ngày)")] public int? LeadTimeDays { get; set; }
    [Required(ErrorMessage = "Nhập hiệu lực HSDT")][Range(0, 365)][Display(Name = "Hiệu lực HSDT (ngày)")] public int? ValidityDays { get; set; }
    [Range(0, 100_000_000_000)][Display(Name = "Bảo đảm dự thầu (đ)")] public decimal BidSecurityAmount { get; set; }
    [StringLength(100)][Display(Name = "Số bảo lãnh / chứng từ")] public string? BidSecurityRef { get; set; }
    [DataType(DataType.Date)][Display(Name = "Bảo lãnh hiệu lực đến")] public DateTime? BidSecurityExpiry { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public List<QuoteEntryLine> Lines { get; set; } = new();
}

/// <summary>Đánh giá hợp lệ + năng lực từng HSDT</summary>
public class BidScreeningViewModel
{
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public List<BidScreeningLine> Lines { get; set; } = new();
}

public class BidScreeningLine
{
    public int TenderBidderId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    public bool? Valid { get; set; }
    [StringLength(500)] public string? ValidNote { get; set; }
    public bool? Qualified { get; set; }
    [StringLength(500)] public string? QualifiedNote { get; set; }
}

/// <summary>Thành viên tổ chuyên gia chấm kỹ thuật (độc lập)</summary>
public class TenderScoringViewModel
{
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public bool Complete { get; set; }
    public List<TenderScoringLine> Lines { get; set; } = new();
}

public class TenderScoringLine
{
    public int TenderBidderId { get; set; }
    public string VendorName { get; set; } = string.Empty;
    [Range(0, 100, ErrorMessage = "Điểm từ 0 đến 100")] public int? Score { get; set; }
    [StringLength(500)] public string? Comment { get; set; }
}

public class ContractFormViewModel
{
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    [Required, StringLength(200)][Display(Name = "Tên hợp đồng")] public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Ngày ký")] public DateTime SignedDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực từ")] public DateTime StartDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime EndDate { get; set; } = DateTime.Today.AddMonths(1);
    [Range(0, 100_000_000_000)][Display(Name = "Bảo đảm thực hiện (đ)")] public decimal PerformanceSecurityAmount { get; set; }
    [Required(ErrorMessage = "Nhập số bảo lãnh thực hiện HĐ"), StringLength(100)][Display(Name = "Số bảo lãnh thực hiện")] public string? PerformanceSecurityRef { get; set; }
    [Required(ErrorMessage = "Nhập hạn bảo lãnh")][DataType(DataType.Date)][Display(Name = "Bảo lãnh hiệu lực đến")] public DateTime? PerformanceSecurityExpiry { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [StringLength(4000)][Display(Name = "Điều khoản khác")] public string? Terms { get; set; }
}

/// <summary>Đặt hàng một đợt theo hợp đồng khung</summary>
public class ContractPoViewModel
{
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày giao hàng")] public DateTime DeliveryDate { get; set; } = DateTime.Today.AddDays(7);
    public List<ContractPoLine> Lines { get; set; } = new();
}

public class ContractPoLine
{
    public int ContractLineId { get; set; }
    [Range(0, 10_000_000)] public decimal Quantity { get; set; }
}

/// <summary>Trình thiết kế báo cáo động</summary>
public class DesignerViewModel
{
    public int? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsShared { get; set; }
    /// <summary>Người xem là chủ sở hữu (hoặc quản trị) – được ghi đè thiết kế đã lưu</summary>
    public bool CanEdit { get; set; } = true;
    public ReportDesign Design { get; set; } = new();
    public DynamicResult? Result { get; set; }
    public List<string> Errors { get; set; } = new();
    public string? Sql { get; set; }
}
