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
    [Display(Name = "Đã hủy")] Cancelled = 4,
    [Display(Name = "Đã mở báo giá – đang đánh giá")] Opened = 5,
    [Display(Name = "Chờ duyệt kết quả")] PendingApproval = 6
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
    [Display(Name = "Yêu cầu báo giá")] RFQ = 3,
    [Display(Name = "Tờ trình chỉ định thầu")] DirectAward = 4,
    [Display(Name = "Gói thầu")] Tender = 5,
    [Display(Name = "Hợp đồng")] Contract = 6
}

/// <summary>Hình thức lựa chọn nhà cung cấp (xếp theo mức độ cạnh tranh tăng dần)</summary>
public enum ProcurementMethod
{
    [Display(Name = "Mua sắm trực tiếp")] DirectPurchase = 1,
    [Display(Name = "Chỉ định thầu")] DirectAward = 2,
    [Display(Name = "Chào hàng cạnh tranh")] CompetitiveQuotation = 3,
    [Display(Name = "Đấu thầu rộng rãi")] OpenTender = 4
}

/// <summary>Phương pháp đánh giá báo giá trong chào hàng cạnh tranh</summary>
public enum EvaluationMethod
{
    [Display(Name = "Giá thấp nhất (trong các báo giá đạt kỹ thuật)")] LowestPrice = 1,
    [Display(Name = "Chấm điểm có trọng số (giá – kỹ thuật – tiến độ)")] WeightedScore = 2
}

/// <summary>Căn cứ chỉ định thầu</summary>
public enum DirectAwardReason
{
    [Display(Name = "Độc quyền – chỉ một NCC đáp ứng")] SoleSource = 1,
    [Display(Name = "Khẩn cấp (sự cố, thiên tai, an toàn)")] Emergency = 2,
    [Display(Name = "Giá trị nhỏ, dưới hạn mức chỉ định")] UnderThreshold = 3,
    [Display(Name = "Tương thích / đồng bộ với thiết bị sẵn có")] Compatibility = 4,
    [Display(Name = "Lý do khác")] Other = 5
}

public enum DirectAwardStatus
{
    [Display(Name = "Nháp – đang thương thảo")] Draft = 1,
    [Display(Name = "Chờ Giám đốc duyệt")] PendingApproval = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Đã hủy")] Cancelled = 4
}

public enum TenderStatus
{
    [Display(Name = "Nháp – lập KH & HSMT")] Draft = 1,
    [Display(Name = "Chờ duyệt KH & HSMT")] PendingPlanApproval = 2,
    [Display(Name = "Đang mời thầu")] Published = 3,
    [Display(Name = "Đã mở thầu – đang đánh giá")] Opened = 4,
    [Display(Name = "Chờ duyệt kết quả")] PendingResultApproval = 5,
    [Display(Name = "Đã có kết quả")] Awarded = 6,
    [Display(Name = "Đã ký hợp đồng")] Contracted = 7,
    [Display(Name = "Đã hủy")] Cancelled = 8
}

/// <summary>Phương thức nộp hồ sơ dự thầu</summary>
public enum EnvelopeMethod
{
    [Display(Name = "Một giai đoạn một túi hồ sơ")] OneEnvelope = 1,
    [Display(Name = "Một giai đoạn hai túi hồ sơ (kỹ thuật trước, tài chính sau)")] TwoEnvelope = 2
}

public enum TenderEvaluationMethod
{
    [Display(Name = "Giá đánh giá thấp nhất (trong các HSDT đạt kỹ thuật)")] LowestEvaluatedPrice = 1,
    [Display(Name = "Kết hợp kỹ thuật và giá (chấm điểm)")] Combined = 2
}

public enum ContractStatus
{
    [Display(Name = "Đang thực hiện")] Active = 1,
    [Display(Name = "Đã hoàn thành")] Completed = 2,
    [Display(Name = "Đã chấm dứt")] Terminated = 3
}

/// <summary>Các danh mục dùng chung (dữ liệu tham chiếu được chọn trên chứng từ)</summary>
public enum CatalogType
{
    [Display(Name = "Phòng ban")] Department = 1,
    [Display(Name = "Đơn vị tính")] Unit = 2,
    [Display(Name = "Điều khoản thanh toán")] PaymentTerm = 3,
    [Display(Name = "Loại hợp đồng")] ContractType = 4,
    [Display(Name = "Địa điểm giao hàng")] DeliveryLocation = 5,
    [Display(Name = "Ngân hàng")] Bank = 6,
    [Display(Name = "Thời hạn bảo hành")] Warranty = 7
}

public enum NotificationKind
{
    [Display(Name = "Cần xử lý")] Action = 1,
    [Display(Name = "Thông tin")] Info = 2,
    [Display(Name = "Nhắc hạn")] Reminder = 3,
    [Display(Name = "Cảnh báo")] Warning = 4
}
