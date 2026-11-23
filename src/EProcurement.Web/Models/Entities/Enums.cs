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
    [Display(Name = "Hợp đồng")] Contract = 6,
    [Display(Name = "Hóa đơn")] Invoice = 7,
    [Display(Name = "Đề nghị thanh toán")] Payment = 8,
    [Display(Name = "Kế hoạch mua sắm")] Plan = 9,
    [Display(Name = "Gói mua sắm")] Package = 10,
    [Display(Name = "Kế hoạch lựa chọn NCC")] SelectionPlan = 11,
    [Display(Name = "Sơ tuyển nhà cung cấp")] Prequalification = 12,
    [Display(Name = "Bảng giá tiêu chuẩn")] StandardPriceList = 13
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
    [Display(Name = "Thời hạn bảo hành")] Warranty = 7,
    [Display(Name = "Đơn vị tiền tệ")] Currency = 8,
    [Display(Name = "Nguồn vốn")] FundingSource = 9,
    [Display(Name = "Loại đơn hàng")] OrderType = 10,
    [Display(Name = "Tổ triển khai")] ImplementationTeam = 11,
    [Display(Name = "Quyết định")] Decision = 12,
    [Display(Name = "Lĩnh vực kinh doanh")] BusinessSector = 13,
    [Display(Name = "Xếp loại nhà cung cấp")] SupplierGrade = 14,
    [Display(Name = "Xuất xứ")] Origin = 15,
    [Display(Name = "Thương hiệu")] Brand = 16
}

public enum NotificationKind
{
    [Display(Name = "Cần xử lý")] Action = 1,
    [Display(Name = "Thông tin")] Info = 2,
    [Display(Name = "Nhắc hạn")] Reminder = 3,
    [Display(Name = "Cảnh báo")] Warning = 4
}

/// <summary>Loại hạn mức trong ma trận phê duyệt</summary>
public enum ApprovalLimitType
{
    [Display(Name = "PR vượt mức này cần Giám đốc duyệt (sau trưởng bộ phận)")] PrDirector = 1,
    [Display(Name = "PO vượt mức này (gồm VAT) cần Giám đốc duyệt")] PoDirector = 2
}

public enum VendorDocumentType
{
    [Display(Name = "Giấy chứng nhận đăng ký doanh nghiệp")] BusinessLicense = 1,
    [Display(Name = "Giấy phép / chứng chỉ hành nghề")] Permit = 2,
    [Display(Name = "Chứng chỉ ISO / chất lượng")] Quality = 3,
    [Display(Name = "Ủy quyền / giấy phép bán hàng của hãng")] Authorization = 4,
    [Display(Name = "Báo cáo tài chính")] Financial = 5,
    [Display(Name = "Hồ sơ năng lực / hợp đồng tương tự")] Experience = 6,
    [Display(Name = "Khác")] Other = 9
}

public enum VendorDocumentStatus
{
    [Display(Name = "Chờ xác minh")] Pending = 1,
    [Display(Name = "Đã xác minh")] Verified = 2,
    [Display(Name = "Không chấp nhận")] Rejected = 3
}

public enum SubmissionKind
{
    [Display(Name = "Báo giá")] Quote = 1,
    [Display(Name = "Hồ sơ dự thầu")] Bid = 2,
    [Display(Name = "Báo giá chỉ định")] DirectQuote = 3
}

/// <summary>Thành phần file của HSDT nộp online</summary>
public enum BidFileKind
{
    [Display(Name = "Đề xuất kỹ thuật")] Technical = 1,
    [Display(Name = "Đề xuất tài chính")] Financial = 2,
    [Display(Name = "Bảo đảm dự thầu (bản scan)")] BidSecurity = 3,
    [Display(Name = "Năng lực, kinh nghiệm")] Qualification = 4,
    [Display(Name = "Tài liệu khác")] Other = 9
}

public enum InvoiceStatus
{
    [Display(Name = "Chờ đối chiếu")] Received = 1,
    [Display(Name = "Khớp 3 bên – chờ đề nghị thanh toán")] Matched = 2,
    [Display(Name = "Lệch – chờ xử lý")] Exception = 3,
    [Display(Name = "Đã đề nghị thanh toán")] PaymentRequested = 4,
    [Display(Name = "Đã thanh toán")] Paid = 5,
    [Display(Name = "Từ chối")] Rejected = 6
}

public enum PaymentType
{
    [Display(Name = "Tạm ứng hợp đồng")] Advance = 1,
    [Display(Name = "Thanh toán theo hóa đơn")] Invoice = 2,
    [Display(Name = "Thanh toán theo đợt hợp đồng")] Milestone = 3
}

public enum PaymentStatus
{
    [Display(Name = "Chờ duyệt")] Requested = 1,
    [Display(Name = "Đã duyệt – chờ chi")] Approved = 2,
    [Display(Name = "Đã chi")] Paid = 3,
    [Display(Name = "Từ chối")] Rejected = 4
}

public enum GuaranteeType
{
    [Display(Name = "Bảo lãnh tạm ứng")] Advance = 1,
    [Display(Name = "Bảo lãnh bảo hành")] Warranty = 2,
    [Display(Name = "Bảo lãnh thực hiện (bổ sung / gia hạn)")] Performance = 3
}

public enum AmendmentStatus
{
    [Display(Name = "Chờ Giám đốc duyệt")] Pending = 1,
    [Display(Name = "Đã duyệt – có hiệu lực")] Approved = 2,
    [Display(Name = "Không duyệt")] Rejected = 3
}

public enum PlanStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ Giám đốc duyệt")] Submitted = 2,
    [Display(Name = "Đã duyệt")] Approved = 3,
    [Display(Name = "Trả lại")] Returned = 4
}

public enum PackageStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Trong KHLCNT chờ duyệt")] InPlan = 2,
    [Display(Name = "Đã duyệt – chờ lựa chọn NCC")] Approved = 3,
    [Display(Name = "Đang lựa chọn / thực hiện")] Executing = 4,
    [Display(Name = "Hoàn tất")] Completed = 5,
    [Display(Name = "Đã hủy")] Cancelled = 6
}

public enum SelectionPlanStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ Giám đốc duyệt")] Submitted = 2,
    [Display(Name = "Đã phê duyệt")] Approved = 3,
    [Display(Name = "Trả lại")] Returned = 4
}

public enum PrequalificationStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Đang nhận hồ sơ")] Open = 2,
    [Display(Name = "Đang đánh giá")] Evaluating = 3,
    [Display(Name = "Chờ Giám đốc duyệt kết quả")] PendingApproval = 4,
    [Display(Name = "Đã có kết quả")] Completed = 5,
    [Display(Name = "Đã hủy")] Cancelled = 6
}

public enum PrequalificationResult
{
    [Display(Name = "Đã nộp")] Submitted = 1,
    [Display(Name = "Đạt")] Passed = 2,
    [Display(Name = "Không đạt")] Failed = 3,
    [Display(Name = "Đã rút")] Withdrawn = 4
}

public enum PriceSource
{
    [Display(Name = "Khảo sát / báo giá tham khảo")] Survey = 1,
    [Display(Name = "Website / sàn thương mại")] Website = 2,
    [Display(Name = "Công bố giá / thông báo giá")] Published = 3,
    [Display(Name = "Khác")] Other = 9
}

public enum StandardPriceStatus
{
    [Display(Name = "Nháp")] Draft = 1,
    [Display(Name = "Chờ Giám đốc duyệt")] Submitted = 2,
    [Display(Name = "Đang hiệu lực / đã duyệt")] Approved = 3,
    [Display(Name = "Trả lại")] Returned = 4
}
