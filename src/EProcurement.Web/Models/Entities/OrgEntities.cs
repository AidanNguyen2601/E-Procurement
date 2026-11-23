using System.ComponentModel.DataAnnotations;

namespace EProcurement.Web.Models.Entities;

// =====================================================================
//  CƠ CẤU TỔ CHỨC
// =====================================================================
public enum OrgUnitType
{
    [Display(Name = "Công ty")] Company = 1,
    [Display(Name = "Khối")] Division = 2,
    [Display(Name = "Chi nhánh / đơn vị thành viên")] Branch = 3,
    [Display(Name = "Phòng ban")] Department = 4,
    [Display(Name = "Tổ / nhóm")] Team = 5
}

/// <summary>
/// Đơn vị trong cơ cấu tổ chức (cây). Tên đơn vị là giá trị "phòng ban" dùng trên người dùng, đề nghị mua hàng, ngân sách,
/// kế hoạch, hạn mức duyệt – danh mục Phòng ban được đồng bộ từ cây này. Luồng duyệt vẫn theo phòng ban.
/// </summary>
public class OrgUnit
{
    public int Id { get; set; }
    [Required(ErrorMessage = "Nhập mã đơn vị"), StringLength(30)][Display(Name = "Mã đơn vị")] public string Code { get; set; } = string.Empty;
    [Required(ErrorMessage = "Nhập tên đơn vị"), StringLength(100)][Display(Name = "Tên đơn vị")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Loại đơn vị")] public OrgUnitType Type { get; set; } = OrgUnitType.Department;
    [Display(Name = "Trực thuộc")] public int? ParentId { get; set; }
    public OrgUnit? Parent { get; set; }
    [Display(Name = "Trưởng đơn vị")] public int? ManagerId { get; set; }
    public AppUser? Manager { get; set; }
    [StringLength(30)][Display(Name = "Mã trung tâm chi phí")] public string? CostCenter { get; set; }
    [StringLength(20)][Display(Name = "Điện thoại")] public string? Phone { get; set; }
    [StringLength(300)][Display(Name = "Địa chỉ / vị trí")] public string? Address { get; set; }
    [StringLength(500)][Display(Name = "Chức năng, nhiệm vụ / ghi chú")] public string? Note { get; set; }
    [Display(Name = "Thứ tự")] public int SortOrder { get; set; }
    [Display(Name = "Đang hoạt động")] public bool IsActive { get; set; } = true;
    public ICollection<OrgUnit> Children { get; set; } = new List<OrgUnit>();
}
