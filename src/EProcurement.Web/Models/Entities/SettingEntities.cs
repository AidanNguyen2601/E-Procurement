using System.ComponentModel.DataAnnotations;

namespace EProcurement.Web.Models.Entities;

/// <summary>Cấu hình hệ thống dạng khóa – giá trị (vd: giao diện mặc định)</summary>
public class AppSetting
{
    [Key, StringLength(100)] public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public enum CriteriaKind
{
    [Display(Name = "Sơ tuyển nhà cung cấp")] Prequalification = 1,
    [Display(Name = "Đánh giá HSDT – năng lực, kinh nghiệm")] TenderQualification = 2,
    [Display(Name = "Đánh giá HSDT – kỹ thuật")] TenderTechnical = 3,
    [Display(Name = "Đánh giá báo giá / chào hàng")] Quotation = 4
}

/// <summary>Bộ tiêu chí đánh giá dùng lại: chèn vào đợt sơ tuyển, hồ sơ mời thầu…</summary>
public class CriteriaSet
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên bộ tiêu chí"), StringLength(200)][Display(Name = "Tên bộ tiêu chí")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Dùng cho")] public CriteriaKind Kind { get; set; } = CriteriaKind.Prequalification;
    [Display(Name = "Nhóm hàng áp dụng")] public int? CategoryId { get; set; }
    public Category? Category { get; set; }
    [StringLength(1000)][Display(Name = "Mô tả")] public string? Description { get; set; }
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
    public int CreatedById { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public ICollection<CriteriaItem> Items { get; set; } = new List<CriteriaItem>();
}

public class CriteriaItem
{
    public int Id { get; set; }
    public int CriteriaSetId { get; set; }
    public CriteriaSet? CriteriaSet { get; set; }
    [Required, StringLength(200)][Display(Name = "Tiêu chí")] public string Name { get; set; } = string.Empty;
    [StringLength(1000)][Display(Name = "Yêu cầu / cách đánh giá")] public string? Requirement { get; set; }
    [Display(Name = "Bắt buộc (đạt / không đạt)")] public bool IsMandatory { get; set; }
    [Range(0, 100)][Display(Name = "Điểm tối đa")] public int MaxScore { get; set; }
    [Display(Name = "Hồ sơ chứng minh")] public VendorDocumentType? DocumentType { get; set; }
    public int SortOrder { get; set; }
}

/// <summary>Thư viện biểu mẫu (file mẫu hồ sơ, hợp đồng, biên bản…) cho người dùng tải về</summary>
public class FormTemplate
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập tên biểu mẫu"), StringLength(200)][Display(Name = "Tên biểu mẫu")] public string Name { get; set; } = string.Empty;
    [StringLength(30)][Display(Name = "Mã biểu mẫu")] public string? Code { get; set; }
    [Required, StringLength(100)][Display(Name = "Nhóm")] public string Group { get; set; } = "Khác";
    [StringLength(1000)][Display(Name = "Hướng dẫn sử dụng")] public string? Description { get; set; }
    [StringLength(20)][Display(Name = "Phiên bản")] public string? Version { get; set; }
    [Required, StringLength(260)] public string FileName { get; set; } = string.Empty;
    [Required, StringLength(100)] public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    [Display(Name = "Đang sử dụng")] public bool IsActive { get; set; } = true;
    public int Downloads { get; set; }
    public int UploadedById { get; set; }
    public AppUser? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;
}

public enum PackageRole
{
    [Display(Name = "Phụ trách")] Lead = 1,
    [Display(Name = "Thành viên")] Member = 2,
    [Display(Name = "Chỉ xem")] Viewer = 3
}

/// <summary>Quyền theo gói mua sắm / gói thầu: ai được sửa, ai chỉ được xem</summary>
public class PackageMember
{
    public int Id { get; set; }
    public int PackageId { get; set; }
    public ProcurementPackage? Package { get; set; }
    public int UserId { get; set; }
    public AppUser? User { get; set; }
    public PackageRole Role { get; set; } = PackageRole.Member;
    public DateTime AddedAt { get; set; } = DateTime.Now;
}
