using System.ComponentModel.DataAnnotations;

namespace EProcurement.Web.Models.Entities;

public enum VendorStatus
{
    [Display(Name = "Chờ thẩm định")] Pending = 1,
    [Display(Name = "Đã phê duyệt")] Approved = 2,
    [Display(Name = "Tạm ngưng")] Suspended = 3,
    [Display(Name = "Danh sách đen")] Blacklisted = 4
}

public enum PrStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ trưởng bộ phận duyệt")] Submitted = 2,
    [Display(Name = "Chờ Giám đốc duyệt")] PendingDirector = 3,
    [Display(Name = "Đã duyệt")] Approved = 4,
    [Display(Name = "Đang mua hàng")] Processing = 5,
    [Display(Name = "Hoàn tất")] Completed = 6,
    [Display(Name = "Từ chối")] Rejected = 7,
    [Display(Name = "Đã hủy")] Cancelled = 8
}

public enum PrPriority
{
    [Display(Name = "Bình thường")] Normal = 1,
    [Display(Name = "Gấp")] Urgent = 2,
    [Display(Name = "Khẩn cấp")] Critical = 3
}

public enum RfqStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đang nhận báo giá")] Open = 2,
    [Display(Name = "Đã chọn NCC")] Awarded = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4
}

public enum PoStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ Giám đốc duyệt")] PendingApproval = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Đã gửi NCC")] Issued = 4,
    [Display(Name = "Nhận một phần")] PartiallyReceived = 5,
    [Display(Name = "Đã nhận đủ")] Received = 6,
    [Display(Name = "Đã hủy")] Cancelled = 7
}

public enum DocType
{
    [Display(Name = "Đề nghị mua hàng")] PR = 1,
    [Display(Name = "Đơn mua hàng")] PO = 2,
    [Display(Name = "Yêu cầu báo giá")] RFQ = 3
}
