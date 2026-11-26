using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EProcurement.Web.Models.Entities;

/// <summary>Vai trò của đối tác trong HSDT: thành viên liên danh hoặc nhà thầu phụ</summary>
public enum BidPartnerRole
{
    [Display(Name = "Thành viên đứng đầu liên danh")] Lead = 1,
    [Display(Name = "Thành viên liên danh")] Member = 2,
    [Display(Name = "Nhà thầu phụ")] Subcontractor = 3,
    [Display(Name = "Nhà thầu phụ đặc biệt")] SpecialSubcontractor = 4
}

/// <summary>Thành viên liên danh / nhà thầu phụ được kê khai trong HSDT (nhà thầu nộp HSDT là thành viên đứng đầu nếu có liên danh)</summary>
public class TenderBidderPartner
{
    public int Id { get; set; }
    public int TenderBidderId { get; set; }
    public TenderBidder? TenderBidder { get; set; }
    /// <summary>Đối tác là NCC trong hệ thống (để kiểm tra danh sách đen); null = nhập tên tự do</summary>
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }
    [Required, StringLength(200)][Display(Name = "Tên đối tác")] public string Name { get; set; } = string.Empty;
    [StringLength(20)][Display(Name = "Mã số thuế")] public string? TaxCode { get; set; }
    [Display(Name = "Vai trò")] public BidPartnerRole Role { get; set; } = BidPartnerRole.Member;
    /// <summary>Tỷ lệ % giá trị công việc đảm nhận (liên danh: phần việc; thầu phụ: giá trị thầu phụ / giá dự thầu)</summary>
    [Column(TypeName = "decimal(5,2)")][Range(0, 100)][Display(Name = "Tỷ lệ công việc (%)")] public decimal SharePercent { get; set; }
    [StringLength(500)][Display(Name = "Phạm vi công việc")] public string? WorkScope { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Loại yêu cầu năng lực, kinh nghiệm trong HSMT</summary>
public enum QualificationKind
{
    [Display(Name = "Hợp đồng tương tự")] SimilarContracts = 1,
    [Display(Name = "Doanh thu bình quân hằng năm")] AverageRevenue = 2,
    [Display(Name = "Nguồn lực tài chính")] FinancialResources = 3,
    [Display(Name = "Nhân sự chủ chốt")] KeyPersonnel = 4,
    [Display(Name = "Thiết bị chủ yếu")] Equipment = 5,
    [Display(Name = "Yêu cầu khác")] Other = 6
}

/// <summary>
/// Yêu cầu năng lực, kinh nghiệm có cấu trúc của gói thầu. Ý nghĩa các ngưỡng theo loại:
/// hợp đồng tương tự: ≥ MinCount hợp đồng, mỗi hợp đồng giá trị ≥ MinValue; doanh thu / tài chính: ≥ MinValue;
/// nhân sự: ≥ MinCount người, mỗi người ≥ MinYears năm kinh nghiệm; thiết bị: ≥ MinCount chiếc; khác: tổ chuyên gia tự đánh giá.
/// </summary>
public class TenderRequirement
{
    public int Id { get; set; }
    public int TenderId { get; set; }
    public Tender? Tender { get; set; }
    [Display(Name = "Loại yêu cầu")] public QualificationKind Kind { get; set; }
    [Required, StringLength(300)][Display(Name = "Nội dung yêu cầu")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Số lượng tối thiểu")] public int MinCount { get; set; }
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị tối thiểu")] public decimal MinValue { get; set; }
    [Display(Name = "Số năm kinh nghiệm tối thiểu")] public int MinYears { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>
/// Kê khai năng lực của nhà thầu theo một yêu cầu: mỗi dòng là một hợp đồng / một nhân sự / một loại thiết bị / số liệu tài chính.
/// Liên danh: dòng ghi rõ thành viên kê khai (PartnerId), năng lực được cộng gộp.
/// </summary>
public class TenderBidderCapability
{
    public int Id { get; set; }
    public int TenderBidderId { get; set; }
    public TenderBidder? TenderBidder { get; set; }
    public int RequirementId { get; set; }
    public TenderRequirement? Requirement { get; set; }
    /// <summary>Thành viên liên danh kê khai; null = nhà thầu nộp HSDT</summary>
    public int? PartnerId { get; set; }
    public TenderBidderPartner? Partner { get; set; }
    [Display(Name = "Số lượng")] public int Count { get; set; } = 1;
    [Column(TypeName = "decimal(18,0)")][Display(Name = "Giá trị")] public decimal Value { get; set; }
    [Display(Name = "Số năm")] public int Years { get; set; }
    [StringLength(1000)][Display(Name = "Mô tả / chứng minh")] public string? Detail { get; set; }
}
