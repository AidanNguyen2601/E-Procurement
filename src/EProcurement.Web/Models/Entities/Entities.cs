using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EProcurement.Web.Models.Entities;

// =====================================================================
//  NGƯỜI DÙNG & PHÂN QUYỀN
// =====================================================================
public class AppUser
{
    public int Id { get; set; }

    [Required, StringLength(50)]
    [Display(Name = "Tên đăng nhập")]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(150)]
    [Display(Name = "Họ và tên")]
    public string FullName { get; set; } = string.Empty;

    [StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [Required, StringLength(100)]
    [Display(Name = "Phòng ban")]
    public string Department { get; set; } = string.Empty;

    [Required, StringLength(20)]
    [Display(Name = "Vai trò")]
    public string Role { get; set; } = AppRoles.Requester;

    [Required, StringLength(500)]
    public string PasswordHash { get; set; } = string.Empty;

    [Display(Name = "Kích hoạt")]
    public bool IsActive { get; set; } = true;

    public DateTime? LastLoginAt { get; set; }
    [Display(Name = "Nhận thông báo qua email")] public bool EmailNotifications { get; set; } = true;
    public int FailedLogins { get; set; }
    [Display(Name = "Tạm khóa đến")] public DateTime? LockoutEnd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string Director = "Director";
    public const string Approver = "Approver";
    public const string Buyer = "Buyer";
    public const string Requester = "Requester";
    public const string Accountant = "Accountant";
    /// <summary>Nhận hóa đơn, đối chiếu, đề nghị và ghi nhận thanh toán</summary>
    public const string Finance = Admin + "," + Accountant;

    /// <summary>Bộ phận mua hàng (quản lý NCC, RFQ, PO, nhận hàng)</summary>
    public const string Purchasing = Admin + "," + Buyer;
    /// <summary>Người duyệt chứng từ</summary>
    public const string Approvers = Admin + "," + Director + "," + Approver;

    public static readonly string[] All = { Admin, Director, Approver, Buyer, Accountant, Requester };

    public static string DisplayName(string role) => role switch
    {
        Admin => "Quản trị hệ thống",
        Director => "Ban Giám đốc",
        Approver => "Trưởng bộ phận",
        Buyer => "Nhân viên mua hàng",
        Accountant => "Kế toán",
        _ => "Người đề nghị"
    };
}

// =====================================================================
//  DANH MỤC
// =====================================================================
/// <summary>Nhóm hàng hóa / dịch vụ</summary>
public class Category
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Nhập mã nhóm"), StringLength(20)]
    [Display(Name = "Mã nhóm")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập tên nhóm"), StringLength(150)]
    [Display(Name = "Tên nhóm")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>Hàng hóa / vật tư / dịch vụ</summary>
public class Item
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Nhập mã hàng"), StringLength(30)]
    [Display(Name = "Mã hàng")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập tên hàng"), StringLength(200)]
    [Display(Name = "Tên hàng hóa / dịch vụ")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Chọn nhóm hàng")]
    [Display(Name = "Nhóm hàng")]
    public int? CategoryId { get; set; }
    public Category? Category { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Đơn vị tính")]
    public string Unit { get; set; } = "Cái";

    [Column(TypeName = "decimal(18,0)")]
    [Range(0, 100_000_000_000)]
    [Display(Name = "Giá tham khảo")]
    public decimal ReferencePrice { get; set; }

    [StringLength(1000)]
    [Display(Name = "Quy cách / thông số")]
    public string? Specification { get; set; }

    [Display(Name = "Đang sử dụng")]
    public bool IsActive { get; set; } = true;
}

/// <summary>Nhà cung cấp</summary>
public class Vendor
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Nhập mã NCC"), StringLength(20)]
    [Display(Name = "Mã NCC")]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập tên NCC"), StringLength(200)]
    [Display(Name = "Tên nhà cung cấp")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập mã số thuế"), StringLength(20)]
    [Display(Name = "Mã số thuế")]
    public string TaxCode { get; set; } = string.Empty;

    [StringLength(300)]
    [Display(Name = "Địa chỉ")]
    public string? Address { get; set; }

    [StringLength(20)]
    [Display(Name = "Điện thoại")]
    public string? Phone { get; set; }

    [EmailAddress(ErrorMessage = "Email không hợp lệ"), StringLength(150)]
    [Display(Name = "Email")]
    public string? Email { get; set; }

    [StringLength(150)]
    [Display(Name = "Người liên hệ")]
    public string? ContactPerson { get; set; }

    [StringLength(20)]
    [Display(Name = "SĐT người liên hệ")]
    public string? ContactPhone { get; set; }

    [StringLength(30)]
    [Display(Name = "Số tài khoản")]
    public string? BankAccount { get; set; }

    [StringLength(100)]
    [Display(Name = "Ngân hàng")]
    public string? BankName { get; set; }

    [StringLength(200)]
    [Display(Name = "Điều khoản thanh toán")]
    public string? PaymentTerms { get; set; }

    [Display(Name = "Trạng thái")]
    public VendorStatus Status { get; set; } = VendorStatus.Pending;

    [Column(TypeName = "decimal(3,2)")]
    [Display(Name = "Điểm đánh giá")]
    public decimal Rating { get; set; }

    [StringLength(1000)]
    [Display(Name = "Ghi chú")]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<VendorCategory> Categories { get; set; } = new List<VendorCategory>();
}

/// <summary>Nhóm hàng mà NCC cung cấp (n-n)</summary>
public class VendorCategory
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}

// =====================================================================
//  ĐỀ NGHỊ MUA HÀNG (PR)
// =====================================================================
public class PurchaseRequest
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số PR")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập nội dung đề nghị"), StringLength(200)]
    [Display(Name = "Nội dung đề nghị")]
    public string Title { get; set; } = string.Empty;

    [Display(Name = "Người đề nghị")]
    public int RequesterId { get; set; }
    public AppUser? Requester { get; set; }

    [Required, StringLength(100)]
    [Display(Name = "Phòng ban")]
    public string Department { get; set; } = string.Empty;

    [DataType(DataType.Date)]
    [Display(Name = "Ngày đề nghị")]
    public DateTime RequestDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Ngày cần hàng")]
    public DateTime NeededDate { get; set; } = DateTime.Today.AddDays(14);

    [Display(Name = "Mức độ")]
    public PrPriority Priority { get; set; } = PrPriority.Normal;

    [StringLength(1000)]
    [Display(Name = "Mục đích sử dụng")]
    public string? Purpose { get; set; }

    [Display(Name = "Trạng thái")]
    public PrStatus Status { get; set; } = PrStatus.Draft;

    [Column(TypeName = "decimal(18,0)")]
    [Display(Name = "Tổng dự toán")]
    public decimal EstimatedTotal { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SubmittedAt { get; set; }

    [Display(Name = "Hình thức lựa chọn NCC")]
    public ProcurementMethod? Method { get; set; }

    [StringLength(1000)]
    [Display(Name = "Lý do chọn hình thức")]
    public string? MethodReason { get; set; }

    public ICollection<PurchaseRequestLine> Lines { get; set; } = new List<PurchaseRequestLine>();

    public void Recalculate()
    {
        foreach (var l in Lines) l.Amount = Math.Round(l.Quantity * l.EstimatedPrice, 0);
        EstimatedTotal = Lines.Sum(l => l.Amount);
    }
}

public class PurchaseRequestLine
{
    public int Id { get; set; }
    public int PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Quantity { get; set; } = 1;
    [Column(TypeName = "decimal(18,0)")] public decimal EstimatedPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Amount { get; set; }
    public int SortOrder { get; set; }
}

// =====================================================================
//  YÊU CẦU BÁO GIÁ (RFQ) & BÁO GIÁ NCC
// =====================================================================
public class Rfq
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số RFQ")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập tiêu đề"), StringLength(200)]
    [Display(Name = "Tiêu đề")]
    public string Title { get; set; } = string.Empty;

    [Display(Name = "Từ đề nghị")]
    public int? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }

    [Display(Name = "Người phụ trách")]
    public int BuyerId { get; set; }
    public AppUser? Buyer { get; set; }

    [DataType(DataType.Date)]
    [Display(Name = "Ngày phát hành")]
    public DateTime IssueDate { get; set; } = DateTime.Today;

    [DataType(DataType.Date)]
    [Display(Name = "Hạn nộp báo giá")]
    public DateTime Deadline { get; set; } = DateTime.Today.AddDays(7);

    [Display(Name = "Trạng thái")]
    public RfqStatus Status { get; set; } = RfqStatus.Draft;

    [StringLength(2000)]
    [Display(Name = "Yêu cầu / điều kiện")]
    public string? Requirements { get; set; }

    [Display(Name = "NCC trúng")]
    public int? AwardedVendorId { get; set; }
    public Vendor? AwardedVendor { get; set; }

    [StringLength(1000)]
    [Display(Name = "Lý do chọn")]
    public string? AwardReason { get; set; }

    // ----- Đánh giá -----
    [Display(Name = "Phương pháp đánh giá")]
    public EvaluationMethod EvaluationMethod { get; set; } = EvaluationMethod.LowestPrice;
    [Range(0, 100)][Display(Name = "Trọng số giá (%)")] public int PriceWeight { get; set; } = 70;
    [Range(0, 100)][Display(Name = "Trọng số kỹ thuật (%)")] public int TechnicalWeight { get; set; } = 20;
    [Range(0, 100)][Display(Name = "Trọng số tiến độ (%)")] public int DeliveryWeight { get; set; } = 10;

    /// <summary>Thời điểm mở báo giá. Trước khi mở, giá chào được niêm phong (không xem / so sánh được)</summary>
    [Display(Name = "Mở báo giá lúc")] public DateTime? OpenedAt { get; set; }

    /// <summary>NCC được đề xuất trúng, đang chờ duyệt kết quả</summary>
    [Display(Name = "NCC đề xuất")] public int? ProposedVendorId { get; set; }
    public Vendor? ProposedVendor { get; set; }
    /// <summary>Lý do kết quả phải trình duyệt (không chọn hạng 1, thiếu báo giá hợp lệ…)</summary>
    [StringLength(1000)][Display(Name = "Lý do trình duyệt")] public string? ResultApprovalReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<RfqLine> Lines { get; set; } = new List<RfqLine>();
    public ICollection<RfqVendor> Vendors { get; set; } = new List<RfqVendor>();
}

public class RfqLine
{
    public int Id { get; set; }
    public int RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Quantity { get; set; } = 1;
    public int SortOrder { get; set; }
}

/// <summary>NCC được mời tham gia RFQ + thông tin báo giá chung</summary>
public class RfqVendor
{
    public int Id { get; set; }
    public int RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    [Display(Name = "Đã báo giá")] public bool Responded { get; set; }
    [Display(Name = "Ngày báo giá")] public DateTime? RespondedAt { get; set; }
    [Range(0, 365)][Display(Name = "Thời gian giao (ngày)")] public int? LeadTimeDays { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [StringLength(200)][Display(Name = "Bảo hành")] public string? Warranty { get; set; }
    [Column(TypeName = "decimal(5,2)")][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng trước thuế")] public decimal TotalAmount { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }

    // ----- Đánh giá kỹ thuật (sau khi mở báo giá) -----
    /// <summary>null = chưa đánh giá; false = loại, không xét giá</summary>
    [Display(Name = "Đạt kỹ thuật")] public bool? TechnicalPass { get; set; }
    [Range(0, 100)][Display(Name = "Điểm kỹ thuật")] public int? TechnicalScore { get; set; }
    [StringLength(500)][Display(Name = "Nhận xét kỹ thuật")] public string? TechnicalNote { get; set; }

    [NotMapped] public decimal GrandTotal => Math.Round(TotalAmount * (1 + VatPercent / 100m), 0);

    public ICollection<VendorQuoteLine> QuoteLines { get; set; } = new List<VendorQuoteLine>();
}

public class VendorQuoteLine
{
    public int Id { get; set; }
    public int RfqVendorId { get; set; }
    public RfqVendor? RfqVendor { get; set; }
    public int RfqLineId { get; set; }
    public RfqLine? RfqLine { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Amount { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

// =====================================================================
//  ĐƠN MUA HÀNG (PO)
// =====================================================================
public class PurchaseOrder
{
    public int Id { get; set; }

    [Required, StringLength(30)]
    [Display(Name = "Số PO")]
    public string Number { get; set; } = string.Empty;

    [Required(ErrorMessage = "Chọn nhà cung cấp")]
    [Display(Name = "Nhà cung cấp")]
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    [Display(Name = "Từ RFQ")] public int? RfqId { get; set; }
    public Rfq? Rfq { get; set; }
    [Display(Name = "Từ đề nghị")] public int? PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }

    [Display(Name = "Người lập")] public int BuyerId { get; set; }
    public AppUser? Buyer { get; set; }

    [Display(Name = "Hình thức")] public ProcurementMethod? Method { get; set; }
    /// <summary>Mua sắm trực tiếp: PO gốc (đã nhận đủ) làm căn cứ giá và khối lượng</summary>
    [Display(Name = "PO tham chiếu")] public int? ReferencePoId { get; set; }
    public PurchaseOrder? ReferencePo { get; set; }
    /// <summary>Mua sắm trực tiếp: lý do khi đơn giá cao hơn PO tham chiếu (bắt buộc Giám đốc duyệt)</summary>
    [StringLength(1000)][Display(Name = "Lý do vượt giá tham chiếu")] public string? PriceDeviationReason { get; set; }
    /// <summary>Chỉ định thầu: tờ trình đã được Giám đốc duyệt</summary>
    [Display(Name = "Tờ trình chỉ định")] public int? DirectAwardId { get; set; }
    public DirectAward? DirectAward { get; set; }
    /// <summary>Đấu thầu: PO đặt hàng theo hợp đồng đã ký</summary>
    [Display(Name = "Hợp đồng")] public int? ContractId { get; set; }
    public Contract? Contract { get; set; }

    [DataType(DataType.Date)][Display(Name = "Ngày đặt hàng")] public DateTime OrderDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Ngày giao hàng")] public DateTime DeliveryDate { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(300)][Display(Name = "Địa điểm giao hàng")] public string? DeliveryAddress { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }

    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Cộng tiền hàng")] public decimal SubTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Thuế GTGT")] public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng giá trị")] public decimal Total { get; set; }

    [Display(Name = "Trạng thái")] public PoStatus Status { get; set; } = PoStatus.Draft;
    [StringLength(2000)][Display(Name = "Ghi chú / điều khoản khác")] public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? IssuedAt { get; set; }

    // ----- Phản hồi của NCC trên cổng -----
    [Display(Name = "NCC xác nhận lúc")] public DateTime? VendorConfirmedAt { get; set; }
    [DataType(DataType.Date)][Display(Name = "NCC cam kết giao")] public DateTime? VendorPromisedDate { get; set; }
    [Display(Name = "NCC từ chối lúc")] public DateTime? VendorRejectedAt { get; set; }
    [StringLength(1000)][Display(Name = "Phản hồi của NCC")] public string? VendorResponse { get; set; }

    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();

    [NotMapped] public bool IsLate => Status is PoStatus.Issued or PoStatus.PartiallyReceived && DeliveryDate < DateTime.Today;

    public void Recalculate()
    {
        foreach (var l in Lines) l.Amount = Math.Round(l.Quantity * l.UnitPrice, 0);
        SubTotal = Lines.Sum(l => l.Amount);
        VatAmount = Math.Round(SubTotal * VatPercent / 100m, 0);
        Total = SubTotal + VatAmount;
    }
}

public class PurchaseOrderLine
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Quantity { get; set; } = 1;
    [Column(TypeName = "decimal(18,0)")] public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Amount { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal ReceivedQuantity { get; set; }
    public int SortOrder { get; set; }

    [NotMapped] public decimal RemainingQuantity => Math.Max(0, Quantity - ReceivedQuantity);
}

// =====================================================================
//  CHỈ ĐỊNH THẦU
// =====================================================================
/// <summary>Tờ trình chỉ định thầu: chọn 1 NCC không qua cạnh tranh, có căn cứ + thương thảo giá, luôn cần Giám đốc duyệt</summary>
public class DirectAward
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số tờ trình")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tiêu đề"), StringLength(200)][Display(Name = "Tiêu đề")] public string Title { get; set; } = string.Empty;

    [Display(Name = "Từ đề nghị")] public int PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    [Display(Name = "Nhà cung cấp được chỉ định")] public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Display(Name = "Người lập")] public int BuyerId { get; set; }
    public AppUser? Buyer { get; set; }

    [Display(Name = "Căn cứ chỉ định")] public DirectAwardReason Reason { get; set; } = DirectAwardReason.SoleSource;
    [Required(ErrorMessage = "Thuyết minh lý do chỉ định"), StringLength(2000)][Display(Name = "Thuyết minh")] public string Justification { get; set; } = string.Empty;
    [Display(Name = "Trạng thái")] public DirectAwardStatus Status { get; set; } = DirectAwardStatus.Draft;

    // ----- Thương thảo -----
    [DataType(DataType.Date)][Display(Name = "Ngày thương thảo")] public DateTime? NegotiationDate { get; set; }
    [StringLength(2000)][Display(Name = "Nội dung thương thảo")] public string? NegotiationNote { get; set; }
    [Range(0, 365)][Display(Name = "Thời gian giao (ngày)")] public int? LeadTimeDays { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [StringLength(200)][Display(Name = "Bảo hành")] public string? Warranty { get; set; }
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá NCC chào (trước VAT)")] public decimal ProposedTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá sau thương thảo (trước VAT)")] public decimal NegotiatedTotal { get; set; }

    /// <summary>Cảnh báo nghi chia nhỏ gói để né hạn mức (ghi lại lúc trình duyệt)</summary>
    [StringLength(1000)][Display(Name = "Cảnh báo chia nhỏ gói")] public string? SplitWarning { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public ICollection<DirectAwardLine> Lines { get; set; } = new List<DirectAwardLine>();

    [NotMapped] public decimal NegotiatedGrandTotal => Math.Round(NegotiatedTotal * (1 + VatPercent / 100m), 0);
    [NotMapped] public decimal SavingPercent => ProposedTotal == 0 ? 0 : Math.Round((ProposedTotal - NegotiatedTotal) * 100 / ProposedTotal, 1);

    public void Recalculate()
    {
        ProposedTotal = Lines.Sum(l => Math.Round(l.Quantity * l.ProposedPrice, 0));
        NegotiatedTotal = Lines.Sum(l => Math.Round(l.Quantity * l.NegotiatedPrice, 0));
    }
}

public class DirectAwardLine
{
    public int Id { get; set; }
    public int DirectAwardId { get; set; }
    public DirectAward? DirectAward { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Quantity { get; set; } = 1;
    /// <summary>Đơn giá dự toán trên PR</summary>
    [Column(TypeName = "decimal(18,0)")] public decimal EstimatedPrice { get; set; }
    /// <summary>Đơn giá NCC chào ban đầu</summary>
    [Column(TypeName = "decimal(18,0)")] public decimal ProposedPrice { get; set; }
    /// <summary>Đơn giá sau thương thảo (≤ giá chào)</summary>
    [Column(TypeName = "decimal(18,0)")] public decimal NegotiatedPrice { get; set; }
    public int SortOrder { get; set; }
}

// =====================================================================
//  FILE ĐÍNH KÈM (dùng chung cho mọi chứng từ)
// =====================================================================
public class Attachment
{
    public int Id { get; set; }
    public DocType DocumentType { get; set; }
    public int DocumentId { get; set; }
    [Required, StringLength(260)] public string FileName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    [StringLength(300)] public string? Note { get; set; }
    public int UploadedById { get; set; }
    public AppUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  NHẬN HÀNG (GRN) & ĐÁNH GIÁ NCC
// =====================================================================
public class GoodsReceipt
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số phiếu nhận")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Đơn mua hàng")] public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày nhận")] public DateTime ReceiptDate { get; set; } = DateTime.Today;
    [StringLength(50)][Display(Name = "Số phiếu giao hàng NCC")] public string? DeliveryNoteNo { get; set; }
    [Display(Name = "Người nhận")] public int ReceivedById { get; set; }
    public AppUser? ReceivedBy { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<GoodsReceiptLine> Lines { get; set; } = new List<GoodsReceiptLine>();
}

public class GoodsReceiptLine
{
    public int Id { get; set; }
    public int GoodsReceiptId { get; set; }
    public GoodsReceipt? GoodsReceipt { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal QuantityAccepted { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal QuantityRejected { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}

public class VendorEvaluation
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public int? PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    public int EvaluatorId { get; set; }
    public AppUser? Evaluator { get; set; }
    [Range(1, 5)][Display(Name = "Chất lượng")] public int Quality { get; set; } = 4;
    [Range(1, 5)][Display(Name = "Tiến độ giao hàng")] public int Delivery { get; set; } = 4;
    [Range(1, 5)][Display(Name = "Giá cả")] public int Price { get; set; } = 4;
    [Range(1, 5)][Display(Name = "Dịch vụ hỗ trợ")] public int Service { get; set; } = 4;
    [StringLength(1000)][Display(Name = "Nhận xét")] public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    [NotMapped] public decimal Average => (Quality + Delivery + Price + Service) / 4m;
}

/// <summary>Nhật ký phê duyệt / thao tác trên chứng từ</summary>
public class ApprovalHistory
{
    public int Id { get; set; }
    public DocType DocumentType { get; set; }
    public int DocumentId { get; set; }
    [Required, StringLength(100)] public string Action { get; set; } = string.Empty;
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    [StringLength(1000)] public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  BÁO CÁO ĐỘNG – thiết kế đã lưu
// =====================================================================
public class ReportDefinition
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Đặt tên báo cáo"), StringLength(150)][Display(Name = "Tên báo cáo")] public string Name { get; set; } = string.Empty;
    [StringLength(500)][Display(Name = "Mô tả")] public string? Description { get; set; }
    [Required, StringLength(50)] public string Dataset { get; set; } = string.Empty;
    /// <summary>Thiết kế dạng JSON (ReportDesign)</summary>
    [Required] public string DesignJson { get; set; } = "{}";
    [Display(Name = "Chia sẻ cho mọi người")] public bool IsShared { get; set; }
    public int OwnerId { get; set; }
    public AppUser? Owner { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  DANH MỤC DÙNG CHUNG
// =====================================================================
public class CatalogEntry
{
    public int Id { get; set; }
    [Display(Name = "Danh mục")] public CatalogType Type { get; set; }
    [StringLength(30)][Display(Name = "Mã")] public string? Code { get; set; }
    [Required(ErrorMessage = "Nhập tên / giá trị"), StringLength(300)][Display(Name = "Tên / giá trị")] public string Name { get; set; } = string.Empty;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [Display(Name = "Thứ tự")] public int SortOrder { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
}

// =====================================================================
//  THÔNG BÁO
// =====================================================================
public class Notification
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public NotificationKind Kind { get; set; } = NotificationKind.Info;
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    [StringLength(1000)] public string? Message { get; set; }
    [StringLength(300)] public string? Url { get; set; }
    public DocType? DocumentType { get; set; }
    public int? DocumentId { get; set; }
    /// <summary>Khóa chống trùng: cùng người + cùng khóa chỉ tạo một lần</summary>
    [StringLength(150)] public string? Key { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? ReadAt { get; set; }
    public DateTime? EmailedAt { get; set; }
}

// =====================================================================
//  NGÂN SÁCH MUA SẮM
// =====================================================================
/// <summary>Ngân sách năm của một phòng ban (cho một nhóm hàng, hoặc mọi nhóm hàng khi CategoryId = null). Giá trị trước VAT.</summary>
public class Budget
{
    public int Id { get; set; }
    [Range(2000, 2100)][Display(Name = "Năm")] public int Year { get; set; } = DateTime.Today.Year;
    [Required(ErrorMessage = "Chọn phòng ban"), StringLength(100)][Display(Name = "Phòng ban")] public string Department { get; set; } = string.Empty;
    [Display(Name = "Nhóm hàng")] public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    [Column(TypeName = "decimal(18,0)")][Range(0, 1_000_000_000_000)][Display(Name = "Ngân sách (trước VAT)")] public decimal Amount { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  MA TRẬN PHÊ DUYỆT & ỦY QUYỀN
// =====================================================================
/// <summary>Hạn mức duyệt; Department = null là mức mặc định toàn công ty (ghi đè cấu hình appsettings)</summary>
public class ApprovalLimit
{
    public int Id { get; set; }
    [Display(Name = "Loại hạn mức")] public ApprovalLimitType Type { get; set; }
    [StringLength(100)][Display(Name = "Phòng ban")] public string? Department { get; set; }
    [Column(TypeName = "decimal(18,0)")][Range(0, 1_000_000_000_000)][Display(Name = "Hạn mức (đ)")] public decimal Amount { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>Ủy quyền phê duyệt tạm thời: người nhận có thêm vai trò duyệt của người ủy quyền trong khoảng thời gian</summary>
public class ApprovalDelegation
{
    public int Id { get; set; }
    [Display(Name = "Người ủy quyền")] public int FromUserId { get; set; }
    public AppUser? FromUser { get; set; }
    [Display(Name = "Người nhận ủy quyền")] public int ToUserId { get; set; }
    public AppUser? ToUser { get; set; }
    [DataType(DataType.Date)][Display(Name = "Từ ngày")] public DateTime StartDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Đến ngày")] public DateTime EndDate { get; set; } = DateTime.Today.AddDays(7);
    [Required(ErrorMessage = "Nhập lý do"), StringLength(500)][Display(Name = "Lý do")] public string Reason { get; set; } = string.Empty;
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? RevokedAt { get; set; }
    [NotMapped] public bool IsActive => RevokedAt == null && StartDate.Date <= DateTime.Today && EndDate.Date >= DateTime.Today;
}

// =====================================================================
//  CỔNG NHÀ CUNG CẤP
// =====================================================================
/// <summary>Tài khoản đăng nhập cổng NCC (tách biệt tài khoản nội bộ)</summary>
public class VendorUser
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Required, StringLength(150), EmailAddress][Display(Name = "Email đăng nhập")] public string Email { get; set; } = string.Empty;
    [Required, StringLength(150)][Display(Name = "Họ và tên")] public string FullName { get; set; } = string.Empty;
    [StringLength(20)][Display(Name = "Điện thoại")] public string? Phone { get; set; }
    [StringLength(100)][Display(Name = "Chức vụ")] public string? Position { get; set; }
    [Required, StringLength(500)] public string PasswordHash { get; set; } = string.Empty;
    /// <summary>Tài khoản đăng ký vào NCC đã có trên hệ thống phải được mua hàng xác nhận</summary>
    [Display(Name = "Đã được xác nhận")] public bool IsApproved { get; set; }
    [Display(Name = "Kích hoạt")] public bool IsActive { get; set; } = true;
    public int FailedLogins { get; set; }
    public DateTime? LockoutEnd { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Hồ sơ pháp lý / năng lực NCC tự tải lên, mua hàng xác minh; cảnh báo khi hết hạn</summary>
public class VendorDocument
{
    public int Id { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Display(Name = "Loại hồ sơ")] public VendorDocumentType Type { get; set; }
    [StringLength(100)][Display(Name = "Số hiệu")] public string? Number { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày cấp")] public DateTime? IssueDate { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime? ExpiryDate { get; set; }
    [Required, StringLength(260)] public string FileName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    [Display(Name = "Trạng thái")] public VendorDocumentStatus Status { get; set; } = VendorDocumentStatus.Pending;
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int? UploadedByVendorUserId { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
    public int? VerifiedById { get; set; }
    public AppUser? VerifiedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
}

/// <summary>
/// Báo giá / HSDT nộp online: nội dung được mã hóa (niêm phong) đến thời điểm mở; lưu mã băm SHA-256 làm bằng chứng
/// thời điểm và nội dung nộp. Mỗi lần nộp lại tạo bản mới, chỉ bản cuối cùng trước hạn có hiệu lực.
/// </summary>
public class VendorSubmission
{
    public int Id { get; set; }
    public SubmissionKind Kind { get; set; }
    public int? RfqVendorId { get; set; }
    public RfqVendor? RfqVendor { get; set; }
    public int? TenderBidderId { get; set; }
    public TenderBidder? TenderBidder { get; set; }
    public int VendorUserId { get; set; }
    public VendorUser? VendorUser { get; set; }
    public int Revision { get; set; } = 1;
    public DateTime SubmittedAt { get; set; } = DateTime.Now;
    /// <summary>Nội dung niêm phong (Data Protection)</summary>
    [Required] public string SealedPayload { get; set; } = string.Empty;
    [Required, StringLength(64)] public string Sha256 { get; set; } = string.Empty;
    [StringLength(260)] public string? FileName { get; set; }
    public long FileSize { get; set; }
    public bool IsLatest { get; set; } = true;
    public bool IsWithdrawn { get; set; }
    public DateTime? UnsealedAt { get; set; }
}

/// <summary>Thông báo giao hàng của NCC cho một PO</summary>
public class PoShipment
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày xuất hàng")] public DateTime ShipDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Dự kiến giao")] public DateTime ExpectedDate { get; set; } = DateTime.Today.AddDays(1);
    [StringLength(100)][Display(Name = "Số phiếu xuất / vận đơn")] public string? Reference { get; set; }
    [StringLength(1000)][Display(Name = "Nội dung / ghi chú")] public string? Note { get; set; }
    public int VendorUserId { get; set; }
    public VendorUser? VendorUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

// =====================================================================
//  HÓA ĐƠN – ĐỐI CHIẾU 3 BÊN – THANH TOÁN
// =====================================================================
public class Invoice
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số chứng từ")] public string Number { get; set; } = string.Empty;
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Display(Name = "Đơn mua hàng")] public int PurchaseOrderId { get; set; }
    public PurchaseOrder? PurchaseOrder { get; set; }
    [Required(ErrorMessage = "Nhập số hóa đơn"), StringLength(30)][Display(Name = "Số hóa đơn")] public string InvoiceNo { get; set; } = string.Empty;
    [StringLength(20)][Display(Name = "Ký hiệu")] public string? Series { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày hóa đơn")] public DateTime InvoiceDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hạn thanh toán")] public DateTime DueDate { get; set; } = DateTime.Today.AddDays(30);
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tiền hàng")] public decimal SubTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Thuế GTGT")] public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng thanh toán")] public decimal Total { get; set; }
    [Display(Name = "Trạng thái")] public InvoiceStatus Status { get; set; } = InvoiceStatus.Received;
    [StringLength(2000)][Display(Name = "Kết quả đối chiếu")] public string? MatchNote { get; set; }
    [StringLength(1000)][Display(Name = "Lý do chấp nhận / từ chối")] public string? DecisionNote { get; set; }
    [Display(Name = "NCC gửi qua cổng")] public bool FromPortal { get; set; }
    [StringLength(260)] public string? FileName { get; set; }
    [StringLength(100)] public string? ContentType { get; set; }
    public byte[]? FileData { get; set; }
    public int? CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public int? VendorUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
}

public class InvoiceLine
{
    public int Id { get; set; }
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public int PurchaseOrderLineId { get; set; }
    public PurchaseOrderLine? PurchaseOrderLine { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal Amount { get; set; }
}

/// <summary>Đề nghị thanh toán / phiếu chi (gồm VAT): tạm ứng, theo hóa đơn, theo đợt hợp đồng</summary>
public class Payment
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số đề nghị")] public string Number { get; set; } = string.Empty;
    [Display(Name = "Loại")] public PaymentType Type { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? MilestoneId { get; set; }
    public ContractMilestone? Milestone { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Số tiền đề nghị")] public decimal Amount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Phạt chậm giao")] public decimal PenaltyAmount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Khấu trừ tạm ứng")] public decimal AdvanceDeduction { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Thực chi")] public decimal NetAmount { get; set; }
    [StringLength(1000)][Display(Name = "Diễn giải")] public string? Note { get; set; }
    [Display(Name = "Trạng thái")] public PaymentStatus Status { get; set; } = PaymentStatus.Requested;
    public int RequestedById { get; set; }
    public AppUser? RequestedBy { get; set; }
    public DateTime RequestedAt { get; set; } = DateTime.Now;
    public int? ApprovedById { get; set; }
    public AppUser? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public int? PaidById { get; set; }
    public AppUser? PaidBy { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày chi")] public DateTime? PaidAt { get; set; }
    [StringLength(100)][Display(Name = "Số chứng từ chi / UNC")] public string? PaymentRef { get; set; }
    [StringLength(1000)] public string? RejectReason { get; set; }
}

// =====================================================================
//  HỢP ĐỒNG NÂNG CAO
// =====================================================================
/// <summary>Phụ lục hợp đồng: điều chỉnh giá trị / gia hạn – có hiệu lực khi Giám đốc duyệt</summary>
public class ContractAmendment
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [Required, StringLength(30)][Display(Name = "Số phụ lục")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập nội dung"), StringLength(2000)][Display(Name = "Nội dung điều chỉnh")] public string Description { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tăng / giảm giá trị (trước VAT)")] public decimal ValueChange { get; set; }
    [DataType(DataType.Date)][Display(Name = "Gia hạn đến")] public DateTime? NewEndDate { get; set; }
    [Display(Name = "Trạng thái")] public AmendmentStatus Status { get; set; } = AmendmentStatus.Pending;
    public int CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int? DecidedById { get; set; }
    public AppUser? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(1000)] public string? DecisionNote { get; set; }
}

/// <summary>Lịch thanh toán của hợp đồng (tạm ứng, các đợt)</summary>
public class ContractMilestone
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [Required, StringLength(200)][Display(Name = "Đợt")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Loại")] public PaymentType Type { get; set; } = PaymentType.Milestone;
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "% giá trị HĐ")] public decimal Percent { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Số tiền (gồm VAT)")] public decimal Amount { get; set; }
    [DataType(DataType.Date)][Display(Name = "Dự kiến")] public DateTime? DueDate { get; set; }
    [StringLength(500)][Display(Name = "Điều kiện thanh toán")] public string? Condition { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Bảo lãnh hợp đồng ngoài bảo lãnh thực hiện chính (tạm ứng, bảo hành…)</summary>
public class ContractGuarantee
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [Display(Name = "Loại bảo lãnh")] public GuaranteeType Type { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị")] public decimal Amount { get; set; }
    [StringLength(100)][Display(Name = "Số bảo lãnh")] public string? Reference { get; set; }
    [StringLength(100)][Display(Name = "Ngân hàng phát hành")] public string? Bank { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime ExpiryDate { get; set; }
    [Display(Name = "Đã giải tỏa")] public DateTime? ReleasedAt { get; set; }
    [StringLength(500)] public string? Note { get; set; }
}

// =====================================================================
//  TÍCH HỢP – API KEY
// =====================================================================
public class ApiClient
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên hệ thống"), StringLength(100)][Display(Name = "Hệ thống tích hợp")] public string Name { get; set; } = string.Empty;
    /// <summary>SHA-256 của key (không lưu key gốc)</summary>
    [Required, StringLength(64)] public string KeyHash { get; set; } = string.Empty;
    [Required, StringLength(12)] public string KeyPrefix { get; set; } = string.Empty;
    [Required, StringLength(200)][Display(Name = "Phạm vi")] public string Scopes { get; set; } = "read";
    public bool IsActive { get; set; } = true;
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}
