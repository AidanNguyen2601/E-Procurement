using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Bộ tiêu chí mẫu cho thư viện còn trống (gọi lại an toàn)</summary>
public static class CriteriaSeed
{
    public static async Task EnsureAsync(EProcDbContext db)
    {
        if (await db.CriteriaSets.AnyAsync()) return;
        var owner = await db.Users.Where(u => u.Role == AppRoles.Admin || u.Role == AppRoles.Buyer).OrderBy(u => u.Role == AppRoles.Buyer ? 0 : 1).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (owner == null) return;
        CriteriaSet S(string name, CriteriaKind kind, string desc, params (string Name, string Req, bool Mand, int Score, VendorDocumentType? Doc)[] items)
        {
            var s = new CriteriaSet { Name = name, Kind = kind, Description = desc, CreatedById = owner.Value };
            var i = 0;
            foreach (var it in items) s.Items.Add(new CriteriaItem { Name = it.Name, Requirement = it.Req, IsMandatory = it.Mand, MaxScore = it.Mand ? 0 : it.Score, DocumentType = it.Doc, SortOrder = ++i });
            return s;
        }
        db.CriteriaSets.AddRange(
            S("Sơ tuyển NCC hàng hóa – mẫu chung", CriteriaKind.Prequalification, "Tư cách pháp lý, năng lực tài chính, kinh nghiệm, chất lượng",
                ("Tư cách pháp lý", "Có giấy chứng nhận đăng ký doanh nghiệp còn hiệu lực, ngành nghề phù hợp", true, 0, VendorDocumentType.BusinessLicense),
                ("Không trong danh sách cấm / nợ thuế", "Không đang bị cấm tham gia, không nợ đọng thuế", true, 0, null),
                ("Năng lực tài chính", "Doanh thu bình quân 3 năm gần nhất ≥ 1,5 lần giá trị dự kiến mua hằng năm", false, 30, VendorDocumentType.Financial),
                ("Kinh nghiệm", "Đã thực hiện ≥ 2 hợp đồng tương tự trong 3 năm gần nhất", false, 30, VendorDocumentType.Experience),
                ("Hệ thống quản lý chất lượng", "Có chứng chỉ ISO 9001 hoặc tương đương", false, 20, VendorDocumentType.Quality),
                ("Dịch vụ sau bán hàng", "Có trung tâm bảo hành / đội hỗ trợ tại khu vực, cam kết thời gian phản hồi", false, 20, null)),
            S("Năng lực, kinh nghiệm – gói mua sắm hàng hóa", CriteriaKind.TenderQualification, "Đánh giá đạt / không đạt bước 2 của HSDT",
                ("Hợp đồng tương tự", "Đã hoàn thành ≥ 2 hợp đồng cung cấp hàng hóa tương tự, mỗi hợp đồng ≥ 70% giá gói thầu, trong 3 năm gần nhất", true, 0, null),
                ("Doanh thu", "Doanh thu bình quân 3 năm ≥ 1,5 lần giá gói thầu", true, 0, VendorDocumentType.Financial),
                ("Tình hình tài chính", "Không lỗ trong 2 năm gần nhất; giá trị tài sản ròng dương", true, 0, VendorDocumentType.Financial),
                ("Ủy quyền nhà sản xuất", "Có giấy ủy quyền bán hàng / cam kết hỗ trợ kỹ thuật của hãng sản xuất (với thiết bị chính)", true, 0, VendorDocumentType.Authorization)),
            S("Kỹ thuật – thiết bị CNTT", CriteriaKind.TenderTechnical, "Chấm điểm kỹ thuật thang 100",
                ("Đáp ứng thông số kỹ thuật", "Đáp ứng toàn bộ thông số yêu cầu; vượt yêu cầu được cộng điểm", false, 40, null),
                ("Xuất xứ, chất lượng", "Hàng mới 100%, có CO/CQ, xuất xứ rõ ràng", false, 15, null),
                ("Bảo hành, bảo trì", "Thời gian bảo hành ≥ 24 tháng, phản hồi sự cố trong 4 giờ", false, 20, null),
                ("Tiến độ giao hàng", "Giao hàng, lắp đặt trong thời hạn yêu cầu; có kế hoạch triển khai chi tiết", false, 15, null),
                ("Đào tạo, chuyển giao", "Đào tạo vận hành cho người dùng, tài liệu hướng dẫn tiếng Việt", false, 10, null)),
            S("Đánh giá báo giá – chào hàng cạnh tranh", CriteriaKind.Quotation, "Kiểm tra tính hợp lệ của báo giá",
                ("Hiệu lực báo giá", "Báo giá còn hiệu lực ≥ 30 ngày", true, 0, null),
                ("Đáp ứng yêu cầu kỹ thuật", "Hàng hóa chào đáp ứng mô tả, số lượng yêu cầu", true, 0, null),
                ("Điều kiện thương mại", "Điều kiện giao hàng, thanh toán, bảo hành phù hợp yêu cầu", true, 0, null)));
        await db.SaveChangesAsync();
    }
}
