using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EProcurement.Web.Models.Entities;

// =====================================================================
//  GÓI MUA SẮM & KẾ HOẠCH LỰA CHỌN NHÀ CUNG CẤP (KHLCNT)
// =====================================================================
/// <summary>
/// Gói mua sắm: gom một hoặc nhiều đề nghị mua hàng đã duyệt thành một gói, xác định giá gói, hình thức, phương thức,
/// loại hợp đồng, thời gian. Gói phải nằm trong KHLCNT được Giám đốc duyệt mới được lập hồ sơ lựa chọn NCC
/// (hồ sơ chào giá chỉ định / chào giá cạnh tranh / HSMT / đơn mua sắm trực tiếp).
/// </summary>
public class ProcurementPackage
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số gói")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên gói"), StringLength(200)][Display(Name = "Tên gói mua sắm")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Trạng thái")] public PackageStatus Status { get; set; } = PackageStatus.Draft;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá gói (trước VAT)")] public decimal Budget { get; set; }
    [StringLength(200)][Display(Name = "Nguồn vốn")] public string? FundingSource { get; set; } = "Ngân sách hoạt động";
    /// <summary>Lĩnh vực của gói (danh mục "Lĩnh vực gói"): Hàng hóa, Xây lắp, Dịch vụ… – quyết định bộ hạn mức & thời gian áp dụng</summary>
    [StringLength(100)][Display(Name = "Lĩnh vực")] public string? Field { get; set; }
    [Display(Name = "Hình thức lựa chọn NCC")] public ProcurementMethod Method { get; set; } = ProcurementMethod.CompetitiveQuotation;
    [StringLength(1000)][Display(Name = "Lý do chọn hình thức")] public string? MethodReason { get; set; }
    /// <summary>Phương thức (chỉ áp dụng đấu thầu)</summary>
    [Display(Name = "Phương thức")] public EnvelopeMethod EnvelopeMethod { get; set; } = EnvelopeMethod.OneEnvelope;
    [StringLength(100)][Display(Name = "Loại hợp đồng")] public string? ContractType { get; set; } = "Trọn gói";
    [Range(1, 3650)][Display(Name = "Thời gian thực hiện HĐ (ngày)")] public int ContractDurationDays { get; set; } = 30;
    [DataType(DataType.Date)][Display(Name = "Thời gian bắt đầu tổ chức LCNT")] public DateTime PlannedStart { get; set; } = DateTime.Today.AddDays(7);
    [StringLength(200)][Display(Name = "Tổ triển khai")] public string? Team { get; set; }
    /// <summary>Cơ cấu chi phí BOQ: chi phí chung, thu nhập chịu thuế tính trước (lợi nhuận), dự phòng, VAT – tính trên chi phí trực tiếp</summary>
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "Chi phí chung (%)")] public decimal BoqOverheadPercent { get; set; }
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "Lợi nhuận / thu nhập chịu thuế tính trước (%)")] public decimal BoqProfitPercent { get; set; }
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "Dự phòng (%)")] public decimal BoqContingencyPercent { get; set; }
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "VAT (%)")] public decimal BoqVatPercent { get; set; } = 10;

    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }

    [Display(Name = "KHLCNT")] public int? SelectionPlanId { get; set; }
    public SelectionPlan? SelectionPlan { get; set; }
    [Display(Name = "Người phụ trách")] public int BuyerId { get; set; }
    public AppUser? Buyer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<PurchaseRequest> PurchaseRequests { get; set; } = new List<PurchaseRequest>();
}

/// <summary>Kế hoạch lựa chọn nhà cung cấp: danh sách gói mua sắm trình Giám đốc phê duyệt</summary>
public class SelectionPlan
{
    public int Id { get; set; }
    [Required, StringLength(30)][Display(Name = "Số KHLCNT")] public string Number { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên kế hoạch"), StringLength(200)][Display(Name = "Tên kế hoạch")] public string Title { get; set; } = string.Empty;
    [StringLength(2000)][Display(Name = "Căn cứ lập kế hoạch")] public string? Basis { get; set; }
    [Display(Name = "Trạng thái")] public SelectionPlanStatus Status { get; set; } = SelectionPlanStatus.Draft;
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? SubmittedAt { get; set; }
    public int? DecidedById { get; set; }
    public AppUser? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    [StringLength(1000)][Display(Name = "Ý kiến phê duyệt")] public string? DecisionNote { get; set; }
    public ICollection<ProcurementPackage> Packages { get; set; } = new List<ProcurementPackage>();
}

// =====================================================================
//  LỰA CHỌN NHÀ THẦU: ĐÀM PHÁN HỢP ĐỒNG, THẨM ĐỊNH KẾT QUẢ
// =====================================================================
/// <summary>
/// Đàm phán (thương thảo) hợp đồng với nhà thầu xếp hạng cao nhất. Không thành công → mời nhà thầu xếp hạng kế tiếp.
/// Đề xuất trúng thầu theo kết quả đàm phán thành công; giá trúng thầu = giá sau đàm phán.
/// </summary>
public class TenderNegotiation
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    public int TenderBidderId { get; set; }
    public TenderBidder? TenderBidder { get; set; }
    [Display(Name = "Xếp hạng khi đàm phán")] public int Rank { get; set; }
    [DataType(DataType.Date)][Display(Name = "Ngày đàm phán")] public DateTime NegotiatedOn { get; set; } = DateTime.Today;
    [Required, StringLength(1000)][Display(Name = "Thành phần tham dự")] public string Participants { get; set; } = string.Empty;
    [Required, StringLength(4000)][Display(Name = "Nội dung đàm phán")] public string Content { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá dự thầu sau giảm giá (trước VAT)")] public decimal BidPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá sau đàm phán (trước VAT)")] public decimal NegotiatedPrice { get; set; }
    [Display(Name = "Kết quả")] public bool Success { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú / lý do không thành công")] public string? Note { get; set; }
    /// <summary>Bị hủy hiệu lực khi kết quả bị trả lại / kiến nghị được chấp nhận</summary>
    public bool IsVoid { get; set; }
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>Báo cáo thẩm định kết quả lựa chọn nhà thầu – người thẩm định độc lập (không thuộc bên mời thầu / tổ chuyên gia)</summary>
public class TenderAppraisal
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    [Display(Name = "Người thẩm định")] public int AppraiserId { get; set; }
    public AppUser? Appraiser { get; set; }
    public int AssignedById { get; set; }
    public AppUser? AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; } = DateTime.Now;
    [Display(Name = "Hoàn thành lúc")] public DateTime? CompletedAt { get; set; }
    /// <summary>true: thống nhất với đề xuất của bên mời thầu</summary>
    [Display(Name = "Kết luận")] public bool? Agree { get; set; }
    [StringLength(4000)][Display(Name = "Nội dung thẩm định")] public string? Content { get; set; }
    [StringLength(2000)][Display(Name = "Kiến nghị")] public string? Recommendation { get; set; }
    public bool IsVoid { get; set; }
}

/// <summary>
/// Dòng BOQ (bảng khối lượng) phân cấp của gói mua sắm. Dòng có dòng con là NHÓM (hạng mục): thành tiền = tổng các dòng con;
/// dòng lá có thành tiền = khối lượng × đơn giá. Mã (1, 1.1, 1.1.1…) tự đánh theo thứ tự cây.
/// </summary>
public class BoqItem
{
    public int Id { get; set; }
    public int PackageId { get; set; }
    public ProcurementPackage? Package { get; set; }
    public int? ParentId { get; set; }
    public BoqItem? Parent { get; set; }
    public ICollection<BoqItem> Children { get; set; } = new List<BoqItem>();
    [StringLength(30)][Display(Name = "Mã")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên hạng mục / công việc"), StringLength(250)][Display(Name = "Hạng mục / công việc")] public string Name { get; set; } = string.Empty;
    [StringLength(30)][Display(Name = "ĐVT")] public string? Unit { get; set; }
    [Column(TypeName = "decimal(18,3)")][Range(0, 1_000_000_000)][Display(Name = "Khối lượng")] public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,0)")][Range(0, 1_000_000_000_000)][Display(Name = "Đơn giá (trước VAT)")] public decimal UnitPrice { get; set; }
    /// <summary>Cơ cấu đơn giá (nếu nhập): đơn giá = vật liệu + nhân công + máy thi công</summary>
    [Column(TypeName = "decimal(18,0)")][Range(0, 1_000_000_000_000)][Display(Name = "Vật liệu")] public decimal MaterialPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")][Range(0, 1_000_000_000_000)][Display(Name = "Nhân công")] public decimal LaborPrice { get; set; }
    [Column(TypeName = "decimal(18,0)")][Range(0, 1_000_000_000_000)][Display(Name = "Máy thi công")] public decimal MachinePrice { get; set; }
    [Display(Name = "Thứ tự")] public int SortOrder { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
    /// <summary>Phân tích đơn giá theo định mức: JSON danh sách hao phí vật liệu / nhân công / máy (xem BoqNorms)</summary>
    public string? Norms { get; set; }
}

/// <summary>Phiên bản (ảnh chụp) của BOQ gói mua sắm: lưu trước khi thay thế / khôi phục / mời thầu để so sánh và quay lại</summary>
public class BoqVersion
{
    public int Id { get; set; }
    public int PackageId { get; set; }
    public ProcurementPackage? Package { get; set; }
    [Display(Name = "Phiên bản")] public int Number { get; set; }
    [Required, StringLength(200)][Display(Name = "Tên phiên bản")] public string Label { get; set; } = string.Empty;
    [Display(Name = "Số dòng")] public int ItemCount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Tổng chi phí trực tiếp")] public decimal Total { get; set; }
    /// <summary>JSON danh sách dòng BOQ tại thời điểm chụp</summary>
    public string Snapshot { get; set; } = "[]";
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
