using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EProcurement.Web.Models.Entities;

// =====================================================================
//  QUẢN LÝ NGUỒN CUNG: SƠ TUYỂN NCC, NGÂN HÀNG GIÁ, BẢNG GIÁ TIÊU CHUẨN
// =====================================================================

/// <summary>
/// Đợt sơ tuyển nhà cung cấp theo nhóm hàng: thông báo mời sơ tuyển (tiêu chí bắt buộc + chấm điểm, hạn nộp)
/// → NCC nộp hồ sơ dự sơ tuyển trên cổng → chấm theo tiêu chí → Giám đốc phê duyệt danh sách đạt (có thời hạn hiệu lực).
/// </summary>
public class Prequalification
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số đợt")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên đợt sơ tuyển"), StringLength(200)][Display(Name = "Tên đợt sơ tuyển")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Nhóm hàng")] public int CategoryId { get; set; }
    public Category? Category { get; set; }
    [StringLength(2000)][Display(Name = "Phạm vi, yêu cầu chung")] public string? Description { get; set; }
    [Display(Name = "Hạn nộp hồ sơ")] public DateTime Deadline { get; set; } = DateTime.Today.AddDays(14).AddHours(17);
    [Range(1, 60)][Display(Name = "Hiệu lực kết quả (tháng)")] public int ValidityMonths { get; set; } = 12;
    [Range(0, 100)][Display(Name = "Điểm tối thiểu để đạt")] public int MinScore { get; set; } = 60;
    [Display(Name = "Trạng thái")] public PrequalificationStatus Status { get; set; } = PrequalificationStatus.Draft;
    public DateTime? PublishedAt { get; set; }
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int? DecidedById { get; set; }
    public AppUser? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(1000)] public string? DecisionNote { get; set; }
    public ICollection<PrequalificationCriterion> Criteria { get; set; } = new List<PrequalificationCriterion>();
    public ICollection<PrequalificationApplication> Applications { get; set; } = new List<PrequalificationApplication>();
}

/// <summary>Tiêu chí sơ tuyển: bắt buộc (đạt / không đạt) hoặc chấm điểm (tối đa MaxScore)</summary>
public class PrequalificationCriterion
{
    public int Id { get; set; }
    public int PrequalificationId { get; set; }
    public Prequalification? Prequalification { get; set; }
    [Required, StringLength(200)][Display(Name = "Tiêu chí")] public string Name { get; set; } = string.Empty;
    [StringLength(1000)][Display(Name = "Yêu cầu / cách đánh giá")] public string? Requirement { get; set; }
    [Display(Name = "Bắt buộc (đạt / không đạt)")] public bool IsMandatory { get; set; }
    [Range(0, 100)][Display(Name = "Điểm tối đa")] public int MaxScore { get; set; }
    /// <summary>Loại hồ sơ pháp lý cần nộp để chứng minh (nếu có)</summary>
    [Display(Name = "Hồ sơ chứng minh")] public VendorDocumentType? DocumentType { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Hồ sơ dự sơ tuyển của một NCC</summary>
public class PrequalificationApplication
{
    public int Id { get; set; }
    public int PrequalificationId { get; set; }
    public Prequalification? Prequalification { get; set; }
    public int VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    /// <summary>Người nộp trên cổng NCC (null = mua hàng tiếp nhận hồ sơ giấy)</summary>
    public int? VendorUserId { get; set; }
    public VendorUser? VendorUser { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.Now;
    [StringLength(2000)][Display(Name = "Thuyết minh năng lực")] public string? Note { get; set; }
    [Display(Name = "Trạng thái")] public PrequalificationResult Status { get; set; } = PrequalificationResult.Submitted;
    [Column(TypeName = "decimal(6,2)")] public decimal? TotalScore { get; set; }
    public int? EvaluatedById { get; set; }
    public AppUser? EvaluatedBy { get; set; }
    public DateTime? EvaluatedAt { get; set; }
    [StringLength(1000)][Display(Name = "Kết luận")] public string? ResultNote { get; set; }
    [Display(Name = "Hiệu lực đến")] public DateTime? ValidUntil { get; set; }
    public ICollection<PrequalificationScore> Scores { get; set; } = new List<PrequalificationScore>();
    public ICollection<PrequalificationDocument> Documents { get; set; } = new List<PrequalificationDocument>();
}

/// <summary>Kết quả chấm một tiêu chí cho một hồ sơ</summary>
public class PrequalificationScore
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public PrequalificationApplication? Application { get; set; }
    public int CriterionId { get; set; }
    public PrequalificationCriterion? Criterion { get; set; }
    public bool? Pass { get; set; }
    [Column(TypeName = "decimal(6,2)")] public decimal? Score { get; set; }
    [StringLength(500)] public string? Comment { get; set; }
}

/// <summary>Hồ sơ pháp lý của NCC đính kèm vào hồ sơ dự sơ tuyển</summary>
public class PrequalificationDocument
{
    public int Id { get; set; }
    public int ApplicationId { get; set; }
    public PrequalificationApplication? Application { get; set; }
    public int VendorDocumentId { get; set; }
    public VendorDocument? VendorDocument { get; set; }
}

/// <summary>Ngân hàng giá: giá thu thập ngoài chứng từ mua hàng (khảo sát thị trường, công bố giá, website…)</summary>
public class PriceRecord
{
    public int Id { get; set; }
    [Display(Name = "Hàng hóa")] public int ItemId { get; set; }
    public Item? Item { get; set; }
    [Display(Name = "Nhà cung cấp")] public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [StringLength(200)][Display(Name = "Đơn vị cung cấp (ngoài danh sách NCC)")] public string? SupplierName { get; set; }
    [Display(Name = "Nguồn giá")] public PriceSource Source { get; set; } = PriceSource.Survey;
    [StringLength(300)][Display(Name = "Diễn giải nguồn")] public string? SourceNote { get; set; }
    /// <summary>Đơn giá trước thuế = đơn giá vật tư + đơn giá thi công / dịch vụ</summary>
    [Column(TypeName = "decimal(18,2)")][Display(Name = "Đơn giá (trước VAT)")] public decimal UnitPrice { get; set; }
    [DataType(DataType.Date)][Display(Name = "Mốc thời gian")] public DateTime PriceDate { get; set; } = DateTime.Today;
    [StringLength(30)][Display(Name = "Đơn vị tính")] public string? Unit { get; set; }
    [StringLength(50)][Display(Name = "Mã phụ")] public string? SubCode { get; set; }
    /// <summary>Mã tiền tệ (danh mục Đơn vị tiền tệ); null / VND = Việt Nam đồng</summary>
    [StringLength(10)][Display(Name = "Loại tiền tệ")] public string? Currency { get; set; } = "VND";
    [Column(TypeName = "decimal(18,2)")][Display(Name = "Đơn giá vật tư")] public decimal MaterialPrice { get; set; }
    [Column(TypeName = "decimal(5,2)")][Display(Name = "Thuế suất vật tư %")] public decimal MaterialVatPercent { get; set; }
    [Column(TypeName = "decimal(18,2)")][Display(Name = "Đơn giá thi công / dịch vụ")] public decimal ServicePrice { get; set; }
    [Column(TypeName = "decimal(5,2)")][Display(Name = "Thuế suất thi công / dịch vụ %")] public decimal ServiceVatPercent { get; set; }
    [Display(Name = "Gói thầu liên quan")] public int? TenderId { get; set; }
    public Tender? Tender { get; set; }
    [Display(Name = "Hợp đồng liên quan")] public int? ContractId { get; set; }
    public Contract? Contract { get; set; }
    [StringLength(100)][Display(Name = "Vùng")] public string? Region { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Đơn giá gồm thuế = vật tư × (1 + thuế vật tư) + thi công / dịch vụ × (1 + thuế thi công / dịch vụ)</summary>
    [NotMapped] public decimal PriceWithTax => Math.Round(MaterialPrice * (1 + MaterialVatPercent / 100m) + ServicePrice * (1 + ServiceVatPercent / 100m), 2);
    [NotMapped] public bool IsVnd => string.IsNullOrEmpty(Currency) || Currency.Equals("VND", StringComparison.OrdinalIgnoreCase);
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Bảng giá tiêu chuẩn hàng hóa, vật tư: giá chuẩn + giá trần theo thời kỳ, Giám đốc phê duyệt</summary>
public class StandardPriceList
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số bảng giá")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên bảng giá"), StringLength(200)][Display(Name = "Tên bảng giá")] public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực từ")] public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime? EffectiveTo { get; set; }
    [StringLength(1000)][Display(Name = "Căn cứ xây dựng giá")] public string? Basis { get; set; }
    [Display(Name = "Trạng thái")] public StandardPriceStatus Status { get; set; } = StandardPriceStatus.Draft;
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SubmittedAt { get; set; }
    public int? DecidedById { get; set; }
    public AppUser? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(1000)] public string? DecisionNote { get; set; }
    public ICollection<StandardPriceLine> Lines { get; set; } = new List<StandardPriceLine>();
}

public class StandardPriceLine
{
    public int Id { get; set; }
    public int ListId { get; set; }
    public StandardPriceList? List { get; set; }
    public int ItemId { get; set; }
    public Item? Item { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá tiêu chuẩn")] public decimal StandardPrice { get; set; }
    /// <summary>Giá trần: mua vượt giá này phải nêu lý do và Giám đốc duyệt</summary>
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trần")] public decimal CeilingPrice { get; set; }
    [StringLength(300)][Display(Name = "Căn cứ")] public string? Basis { get; set; }
}
