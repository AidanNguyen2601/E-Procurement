using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EProcurement.Web.Models.Entities;

// =====================================================================
//  ĐẤU THẦU RỘNG RÃI
// =====================================================================
/// <summary>
/// Gói thầu: kế hoạch lựa chọn NCC + hồ sơ mời thầu (HSMT) → mời thầu công khai → mở thầu
/// → tổ chuyên gia đánh giá → duyệt kết quả → thời gian kiến nghị → ký hợp đồng.
/// </summary>
public class Tender
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số gói thầu")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên gói thầu"), StringLength(200)][Display(Name = "Tên gói thầu")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Từ đề nghị")] public int PurchaseRequestId { get; set; }
    public PurchaseRequest? PurchaseRequest { get; set; }
    [Display(Name = "Gói mua sắm")] public int? PackageId { get; set; }
    public ProcurementPackage? Package { get; set; }
    [Display(Name = "Bên mời thầu")] public int BuyerId { get; set; }
    public AppUser? Buyer { get; set; }
    [Display(Name = "Trạng thái")] public TenderStatus Status { get; set; } = TenderStatus.Draft;

    // ----- Kế hoạch lựa chọn NCC -----
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá gói thầu (trước VAT)")] public decimal PackageBudget { get; set; }
    [Display(Name = "Phương thức")] public EnvelopeMethod EnvelopeMethod { get; set; } = EnvelopeMethod.OneEnvelope;
    [StringLength(100)][Display(Name = "Loại hợp đồng")] public string? ContractType { get; set; } = "Trọn gói";
    [Range(1, 3650)][Display(Name = "Thời gian thực hiện HĐ (ngày)")] public int ContractDurationDays { get; set; } = 30;
    [Display(Name = "Hợp đồng khung (đặt hàng nhiều đợt)")] public bool IsFramework { get; set; }

    // ----- Hồ sơ mời thầu -----
    [Required(ErrorMessage = "Nhập yêu cầu kỹ thuật"), StringLength(4000)][Display(Name = "Yêu cầu kỹ thuật")] public string TechnicalRequirements { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tiêu chí năng lực, kinh nghiệm"), StringLength(4000)][Display(Name = "Tiêu chí năng lực, kinh nghiệm")] public string QualificationCriteria { get; set; } = string.Empty;
    [StringLength(4000)][Display(Name = "Điều kiện hợp đồng")] public string? ContractTerms { get; set; }
    [Display(Name = "Phương pháp đánh giá")] public TenderEvaluationMethod EvaluationMethod { get; set; } = TenderEvaluationMethod.LowestEvaluatedPrice;
    [Range(0, 100)][Display(Name = "Điểm kỹ thuật tối thiểu")] public int MinTechnicalScore { get; set; } = 70;
    [Range(0, 100)][Display(Name = "Trọng số kỹ thuật (%)")] public int TechnicalWeight { get; set; } = 30;
    [Range(0, 100)][Display(Name = "Trọng số giá (%)")] public int PriceWeight { get; set; } = 70;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Bảo đảm dự thầu (đ)")] public decimal BidSecurityAmount { get; set; }
    [Range(1, 365)][Display(Name = "Hiệu lực HSDT (ngày)")] public int BidValidityDays { get; set; } = 60;
    [Column(TypeName = "decimal(5,2)")][Range(0, 30)][Display(Name = "Bảo đảm thực hiện HĐ (%)")] public decimal PerformanceSecurityPercent { get; set; } = 5;

    // ----- Lịch -----
    [Display(Name = "Ngày phát hành HSMT")] public DateTime? PublishedAt { get; set; }
    [Display(Name = "Thời điểm đóng thầu")] public DateTime ClosingAt { get; set; } = DateTime.Today.AddDays(15).AddHours(9);
    [Display(Name = "Thời điểm mở thầu")] public DateTime? OpenedAt { get; set; }
    /// <summary>Hai túi hồ sơ: thời điểm mở hồ sơ tài chính (sau khi chấm xong kỹ thuật). Trước đó giá dự thầu được niêm phong.</summary>
    [Display(Name = "Mở hồ sơ tài chính lúc")] public DateTime? FinancialOpenedAt { get; set; }

    // ----- Kết quả -----
    [Display(Name = "Nhà thầu trúng thầu")] public int? AwardedVendorId { get; set; }
    public Vendor? AwardedVendor { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trúng thầu (trước VAT)")] public decimal? AwardPrice { get; set; }
    [StringLength(1000)][Display(Name = "Lý do / kết luận")] public string? AwardReason { get; set; }
    [Display(Name = "Hạn kiến nghị kết quả")] public DateTime? ComplaintDeadline { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<TenderLine> Lines { get; set; } = new List<TenderLine>();
    public ICollection<TenderBidder> Bidders { get; set; } = new List<TenderBidder>();
    public ICollection<TenderCommitteeMember> Committee { get; set; } = new List<TenderCommitteeMember>();
    public ICollection<TenderClarification> Clarifications { get; set; } = new List<TenderClarification>();
    public ICollection<TenderComplaint> Complaints { get; set; } = new List<TenderComplaint>();
    public ICollection<TenderQuestion> Questions { get; set; } = new List<TenderQuestion>();
    public ICollection<TenderNegotiation> Negotiations { get; set; } = new List<TenderNegotiation>();
    public ICollection<TenderAppraisal> Appraisals { get; set; } = new List<TenderAppraisal>();
}

public class TenderLine
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal Quantity { get; set; } = 1;
    [Column(TypeName = "decimal(18,0)")] public decimal EstimatedPrice { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Dòng sinh từ BOQ của gói: mã BOQ (1.2.3) và đường dẫn các hạng mục cha ("1 Hạ tầng › 1.2 Mạng")</summary>
    [StringLength(30)] public string? BoqCode { get; set; }
    [StringLength(500)] public string? BoqPath { get; set; }
}

/// <summary>Nhà thầu: nhận HSMT → nộp HSDT → được đánh giá theo từng bước</summary>
public class TenderBidder
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Display(Name = "Nhận HSMT lúc")] public DateTime RegisteredAt { get; set; } = DateTime.Now;

    // ----- Hồ sơ dự thầu -----
    [Display(Name = "Đã nộp HSDT")] public bool Submitted { get; set; }
    [Display(Name = "Thời điểm nộp")] public DateTime? SubmittedAt { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá dự thầu (trước VAT)")] public decimal BidPrice { get; set; }
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "Giảm giá (%)")] public decimal DiscountPercent { get; set; }
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [Range(0, 3650)][Display(Name = "Thời gian giao (ngày)")] public int? LeadTimeDays { get; set; }
    [Range(0, 365)][Display(Name = "Hiệu lực HSDT (ngày)")] public int? ValidityDays { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Bảo đảm dự thầu (đ)")] public decimal BidSecurityAmount { get; set; }
    [StringLength(100)][Display(Name = "Số bảo lãnh / chứng từ")] public string? BidSecurityRef { get; set; }
    [DataType(DataType.Date)][Display(Name = "Bảo lãnh hiệu lực đến")] public DateTime? BidSecurityExpiry { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }

    // ----- Đánh giá -----
    [Display(Name = "Hợp lệ")] public bool? Valid { get; set; }
    [StringLength(500)] public string? ValidNote { get; set; }
    [Display(Name = "Đạt năng lực, kinh nghiệm")] public bool? Qualified { get; set; }
    [StringLength(500)] public string? QualifiedNote { get; set; }

    public ICollection<TenderBidLine> Lines { get; set; } = new List<TenderBidLine>();
    public ICollection<TenderScore> Scores { get; set; } = new List<TenderScore>();

    /// <summary>Giá đánh giá = giá dự thầu − giảm giá, cộng VAT</summary>
    [NotMapped] public decimal EvaluatedPrice => Math.Round(BidPrice * (1 - DiscountPercent / 100m) * (1 + VatPercent / 100m), 0);
    [NotMapped] public decimal NetPrice => Math.Round(BidPrice * (1 - DiscountPercent / 100m), 0);
}

public class TenderBidLine
{
    public int Id { get; set; }
    public int TenderBidderId { get; set; }
    public TenderBidder? TenderBidder { get; set; }
    public int TenderLineId { get; set; }
    public TenderLine? TenderLine { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal UnitPrice { get; set; }
}

/// <summary>Thành viên tổ chuyên gia; phải ký cam kết không xung đột lợi ích trước khi chấm</summary>
public class TenderCommitteeMember
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    [Display(Name = "Tổ trưởng")] public bool IsLeader { get; set; }
    [Display(Name = "Cam kết không xung đột lợi ích lúc")] public DateTime? ConflictDeclaredAt { get; set; }
    [Display(Name = "Hoàn thành chấm lúc")] public DateTime? ScoringCompletedAt { get; set; }
}

/// <summary>Điểm kỹ thuật của từng thành viên cho từng HSDT (chấm độc lập)</summary>
public class TenderScore
{
    public int Id { get; set; }
    public int TenderBidderId { get; set; }
    public TenderBidder? TenderBidder { get; set; }
    public int MemberUserId { get; set; }
    public AppUser? MemberUser { get; set; }
    [Range(0, 100)] public int TechnicalScore { get; set; }
    [StringLength(500)] public string? Comment { get; set; }
    public DateTime ScoredAt { get; set; } = DateTime.Now;
}

/// <summary>Hỏi đáp làm rõ HSMT hoặc sửa đổi HSMT – công khai cho mọi nhà thầu</summary>
public class TenderClarification
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    [Display(Name = "Là sửa đổi HSMT")] public bool IsAmendment { get; set; }
    [StringLength(2000)][Display(Name = "Câu hỏi của nhà thầu")] public string? Question { get; set; }
    [Required, StringLength(4000)][Display(Name = "Trả lời / nội dung sửa đổi")] public string Answer { get; set; } = string.Empty;
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Đề nghị làm rõ HSMT nhà thầu gửi qua cổng NCC. Bên mời thầu trả lời bằng một TenderClarification công khai
/// (không nêu tên nhà thầu hỏi); câu hỏi chỉ nhà thầu hỏi và bên mời thầu thấy.
/// </summary>
public class TenderQuestion
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    public int VendorUserId { get; set; }
    public VendorUser? VendorUser { get; set; }
    [Required, StringLength(2000)][Display(Name = "Nội dung đề nghị làm rõ")] public string Content { get; set; } = string.Empty;
    public DateTime AskedAt { get; set; } = DateTime.Now;
    /// <summary>Nội dung làm rõ đã đăng tải để trả lời câu hỏi này</summary>
    public int? ClarificationId { get; set; }
    public TenderClarification? Clarification { get; set; }
}

/// <summary>Kiến nghị của nhà thầu về kết quả lựa chọn</summary>
public class TenderComplaint
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Required, StringLength(2000)][Display(Name = "Nội dung kiến nghị")] public string Content { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.Now;
    [StringLength(2000)][Display(Name = "Trả lời")] public string? Response { get; set; }
    /// <summary>true = kiến nghị đúng → hủy kết quả, đánh giá lại</summary>
    public bool? Upheld { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int? ResolvedById { get; set; }
    public AppUser? ResolvedBy { get; set; }
}

// =====================================================================
//  HỢP ĐỒNG
// =====================================================================
public class Contract
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số hợp đồng")] public string Number { get; set; } = string.Empty;
    [Required, StringLength(200)][Display(Name = "Tên hợp đồng")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Gói thầu")] public int? TenderId { get; set; }
    public Tender? Tender { get; set; }
    [Display(Name = "Nhà cung cấp")] public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Display(Name = "Hợp đồng khung")] public bool IsFramework { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị HĐ (trước VAT)")] public decimal Value { get; set; }
    [Column(TypeName = "decimal(5,2)")][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; } = 10;
    [DataType(DataType.Date)][Display(Name = "Ngày ký")] public DateTime SignedDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực từ")] public DateTime StartDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime EndDate { get; set; } = DateTime.Today.AddMonths(1);
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Bảo đảm thực hiện (đ)")] public decimal PerformanceSecurityAmount { get; set; }
    [StringLength(100)][Display(Name = "Số bảo lãnh thực hiện")] public string? PerformanceSecurityRef { get; set; }
    [DataType(DataType.Date)][Display(Name = "Bảo lãnh hiệu lực đến")] public DateTime? PerformanceSecurityExpiry { get; set; }
    [StringLength(200)][Display(Name = "Điều khoản thanh toán")] public string? PaymentTerms { get; set; }
    [StringLength(4000)][Display(Name = "Điều khoản khác")] public string? Terms { get; set; }
    [Display(Name = "Trạng thái")] public ContractStatus Status { get; set; } = ContractStatus.Active;
    [Column(TypeName = "decimal(5,3)")][Range(0, 5)][Display(Name = "Phạt chậm giao (%/ngày)")] public decimal LatePenaltyPercentPerDay { get; set; } = 0.1m;
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "Mức phạt tối đa (%)")] public decimal LatePenaltyCapPercent { get; set; } = 8;
    public ICollection<ContractAmendment> Amendments { get; set; } = new List<ContractAmendment>();
    public ICollection<ContractMilestone> Milestones { get; set; } = new List<ContractMilestone>();
    public ICollection<ContractGuarantee> Guarantees { get; set; } = new List<ContractGuarantee>();
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ICollection<ContractLine> Lines { get; set; } = new List<ContractLine>();
}

public class ContractLine
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    public int? ItemId { get; set; }
    public Item? Item { get; set; }
    [Required, StringLength(300)] public string Description { get; set; } = string.Empty;
    [StringLength(30)] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,0)")] public decimal UnitPrice { get; set; }
    /// <summary>Dòng từ BOQ: mã (1.2.3) và đường dẫn hạng mục cha</summary>
    [StringLength(30)] public string? BoqCode { get; set; }
    [StringLength(500)] public string? BoqPath { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Trạng thái biên bản nghiệm thu khối lượng</summary>
public enum AcceptanceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đã xác nhận")] Confirmed = 2,
    [Display(Name = "Đã hủy")] Cancelled = 3
}

/// <summary>
/// Biên bản nghiệm thu khối lượng theo BOQ hợp đồng: mỗi đợt ghi khối lượng hoàn thành của từng dòng. Khối lượng lũy kế
/// (các đợt đã xác nhận) không vượt khối lượng hợp đồng. Biên bản đã xác nhận là cơ sở đề nghị thanh toán theo khối lượng hoàn thành.
/// </summary>
public class BoqAcceptance
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số biên bản")] public string Number { get; set; } = string.Empty;
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [Display(Name = "Đợt nghiệm thu")] public int Period { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày nghiệm thu")] public DateTime AcceptanceDate { get; set; } = DateTime.Today;
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    [Display(Name = "Trạng thái")] public AcceptanceStatus Status { get; set; } = AcceptanceStatus.Draft;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị nghiệm thu (trước VAT)")] public decimal SubTotal { get; set; }
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int? ConfirmedById { get; set; }
    public AppUser? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public ICollection<BoqAcceptanceLine> Lines { get; set; } = new List<BoqAcceptanceLine>();
}

public class BoqAcceptanceLine
{
    public int Id { get; set; }
    public int AcceptanceId { get; set; }
    public BoqAcceptance? Acceptance { get; set; }
    public int ContractLineId { get; set; }
    public ContractLine? ContractLine { get; set; }
    [Column(TypeName = "decimal(18,3)")][Display(Name = "Khối lượng nghiệm thu kỳ này")] public decimal Quantity { get; set; }
    [StringLength(300)] public string? Note { get; set; }
}
