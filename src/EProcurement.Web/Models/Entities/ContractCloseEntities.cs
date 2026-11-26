using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EProcurement.Web.Models.Entities;

public enum SettlementStatus
{
    [Display(Name = "Chờ duyệt")] Pending = 1,
    [Display(Name = "Đã duyệt")] Approved = 2,
    [Display(Name = "Trả lại")] Rejected = 3
}

/// <summary>
/// Quyết toán hợp đồng: chốt giá trị cuối cùng (theo khối lượng nghiệm thu / phụ lục / điều chỉnh giá), trừ phạt và các khoản đã chi,
/// ra số còn phải thanh toán (gồm tiền giữ lại chờ hết bảo hành) hoặc số nhà thầu phải hoàn trả. Giám đốc duyệt.
/// Sau khi hoàn tất thanh toán, hoàn trả tiền giữ lại, thu hồi hết tạm ứng và giải tỏa bảo lãnh → thanh lý hợp đồng.
/// </summary>
public class ContractSettlement
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số quyết toán")] public string Number { get; set; } = string.Empty;
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày quyết toán")] public DateTime SettlementDate { get; set; } = DateTime.Today;
    [Display(Name = "Trạng thái")] public SettlementStatus Status { get; set; } = SettlementStatus.Pending;
    /// <summary>Giá trị hợp đồng hiện hành (trước VAT, gồm phụ lục đã duyệt) tại thời điểm quyết toán</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị HĐ (trước VAT)")] public decimal ContractValue { get; set; }
    /// <summary>Giá trị quyết toán (trước VAT): khối lượng hoàn thành được nghiệm thu × đơn giá HĐ</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị quyết toán (trước VAT)")] public decimal FinalValue { get; set; }
    [Column(TypeName = "decimal(5,2)")][Display(Name = "VAT (%)")] public decimal VatPercent { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị quyết toán (gồm VAT)")] public decimal FinalGross { get; set; }
    /// <summary>Điều chỉnh giá đã duyệt (gồm VAT, có thể âm)</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Điều chỉnh giá")] public decimal PriceAdjustment { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Phạt hợp đồng đã trừ")] public decimal PenaltyTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giảm trừ khác")] public decimal OtherDeduction { get; set; }
    [StringLength(500)][Display(Name = "Nội dung giảm trừ")] public string? OtherDeductionNote { get; set; }
    /// <summary>Tổng tiền đã chi cho nhà thầu (gồm tạm ứng)</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Đã thanh toán")] public decimal PaidTotal { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tiền giữ lại chưa hoàn trả")] public decimal RetentionHeld { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tạm ứng chưa thu hồi")] public decimal AdvanceOutstanding { get; set; }
    /// <summary>Còn phải trả nhà thầu (âm: nhà thầu phải hoàn trả) = quyết toán + điều chỉnh − phạt − giảm trừ − đã chi</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Còn phải thanh toán")] public decimal Balance { get; set; }
    [DataType(DataType.Date)][Display(Name = "Hết hạn bảo hành")] public DateTime? WarrantyEndDate { get; set; }
    [StringLength(2000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int? DecidedById { get; set; }
    public AppUser? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(1000)] public string? DecisionNote { get; set; }
    /// <summary>Thời điểm thanh lý hợp đồng (đã hoàn tất mọi nghĩa vụ)</summary>
    [Display(Name = "Thanh lý lúc")] public DateTime? LiquidatedAt { get; set; }
    [StringLength(1000)] public string? LiquidationNote { get; set; }

    /// <summary>Phần thanh toán ngay khi quyết toán: số còn phải trả trừ tiền giữ lại chờ hết bảo hành</summary>
    [NotMapped] public decimal PayNow => Balance - RetentionHeld;
}

/// <summary>Thành phần của công thức điều chỉnh giá (vật liệu, nhân công, máy…) với tỷ trọng và chỉ số giá gốc</summary>
public class ContractPriceIndex
{
    public int Id { get; set; }
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [Required, StringLength(100)][Display(Name = "Thành phần")] public string Name { get; set; } = string.Empty;
    /// <summary>Tỷ trọng trong giá (0–1); phần cố định a = 1 − tổng tỷ trọng</summary>
    [Column(TypeName = "decimal(6,4)")][Display(Name = "Tỷ trọng")] public decimal Weight { get; set; }
    [Column(TypeName = "decimal(18,4)")][Display(Name = "Chỉ số gốc")] public decimal BaseIndex { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Điều chỉnh giá một kỳ (hợp đồng đơn giá điều chỉnh): Pn = P0 × (a + Σ bi × Ii/I0i). Số tiền điều chỉnh = giá trị kỳ × (hệ số − 1).
/// Giám đốc duyệt; phần tăng được đề nghị thanh toán, phần giảm trừ khi quyết toán.
/// </summary>
public class ContractPriceAdjustment
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số")] public string Number { get; set; } = string.Empty;
    public int ContractId { get; set; }
    public Contract? Contract { get; set; }
    [Required, StringLength(100)][Display(Name = "Kỳ điều chỉnh")] public string Period { get; set; } = string.Empty;
    public int? AcceptanceId { get; set; }
    public BoqAcceptance? Acceptance { get; set; }
    /// <summary>Giá trị khối lượng của kỳ theo đơn giá hợp đồng (trước VAT)</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị kỳ (trước VAT)")] public decimal BaseAmount { get; set; }
    [Column(TypeName = "decimal(12,6)")][Display(Name = "Hệ số điều chỉnh")] public decimal Factor { get; set; } = 1;
    /// <summary>Số tiền điều chỉnh trước VAT (âm: giảm)</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tiền điều chỉnh (trước VAT)")] public decimal Amount { get; set; }
    /// <summary>Chỉ số kỳ điều chỉnh theo từng thành phần: "Tên=giá trị;…" (lưu lại để đối chiếu)</summary>
    [StringLength(2000)] public string? Indices { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú / nguồn chỉ số")] public string? Note { get; set; }
    public SettlementStatus Status { get; set; } = SettlementStatus.Pending;
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int? DecidedById { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(1000)] public string? DecisionNote { get; set; }
}
