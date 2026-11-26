using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Kịch bản nghiệp vụ: từng luồng mua sắm theo bước, mỗi bước chỉ ra ai làm (tài khoản mẫu) và mở thẳng chứng từ
/// đang ở đúng bước đó (ưu tiên chứng từ mẫu MAU-…). Mọi người dùng nội bộ xem được.
/// </summary>
public class WalkthroughController : Controller
{
    private readonly EProcDbContext _db;
    public WalkthroughController(EProcDbContext db) => _db = db;

    public record DocLink(string Label, string Url, string? Note = null);
    public record Step(string Title, string Who, string Description, List<DocLink> Links);
    public record Flow(string Key, string Title, string Icon, string Intro, List<Step> Steps);
    public record Account(string Username, string Name, string Role, string Department);

    public async Task<IActionResult> Index()
    {
        const string P = SampleDataSeeder.Prefix;
        // Ưu tiên chứng từ mẫu, không có thì lấy chứng từ thật mới nhất
        async Task<List<DocLink>> Docs<T>(IQueryable<T> q, Func<T, string> number, Func<T, string> label, Func<T, string> url, int take = 2) where T : class
        {
            var list = await q.Take(50).ToListAsync();
            return list.OrderByDescending(x => number(x).StartsWith(P)).Take(take).Select(x => new DocLink(number(x), url(x), label(x))).ToList();
        }
        Task<List<DocLink>> Pr(PrStatus s) => Docs(_db.PurchaseRequests.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.PR, x.Id));
        Task<List<DocLink>> Rfq(RfqStatus s) => Docs(_db.Rfqs.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.RFQ, x.Id));
        Task<List<DocLink>> Da(DirectAwardStatus s) => Docs(_db.DirectAwards.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.DirectAward, x.Id));
        Task<List<DocLink>> Td(params TenderStatus[] s) => Docs(_db.Tenders.AsNoTracking().Where(x => s.Contains(x.Status)).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.Tender, x.Id));
        Task<List<DocLink>> Po(PoStatus s) => Docs(_db.PurchaseOrders.AsNoTracking().Include(x => x.Vendor).Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Vendor?.Name ?? "", x => NotificationService.UrlOf(DocType.PO, x.Id));
        Task<List<DocLink>> Pkg(PackageStatus s) => Docs(_db.ProcurementPackages.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.Package, x.Id));
        Task<List<DocLink>> Sp(SelectionPlanStatus s) => Docs(_db.SelectionPlans.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.SelectionPlan, x.Id));
        Task<List<DocLink>> Pq(PrequalificationStatus s) => Docs(_db.Prequalifications.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.Prequalification, x.Id));
        Task<List<DocLink>> Bg(StandardPriceStatus s) => Docs(_db.StandardPriceLists.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.StandardPriceList, x.Id));
        Task<List<DocLink>> Inv(InvoiceStatus s) => Docs(_db.Invoices.AsNoTracking().Include(x => x.Vendor).Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Vendor?.Name ?? "", x => NotificationService.UrlOf(DocType.Invoice, x.Id));
        Task<List<DocLink>> Pay(PaymentStatus s) => Docs(_db.Payments.AsNoTracking().Include(x => x.Vendor).Where(x => x.Status == s).OrderByDescending(x => x.Id), x => x.Number, x => x.Vendor?.Name ?? "", x => NotificationService.UrlOf(DocType.Payment, x.Id));
        async Task<List<DocLink>> Plan(PlanStatus s) => (await _db.ProcurementPlans.AsNoTracking().Where(x => x.Status == s).OrderByDescending(x => x.Id).Take(2).ToListAsync())
            .Select(x => new DocLink($"KH {x.Year} – {x.Department}", NotificationService.UrlOf(DocType.Plan, x.Id))).ToList();
        static List<DocLink> L(params (string Label, string Url)[] links) => links.Select(l => new DocLink(l.Label, l.Url)).ToList();
        static List<DocLink> Join(params List<DocLink>[] lists) => lists.SelectMany(x => x).ToList();

        // Chứng từ mẫu cụ thể cho các luồng nâng cao (theo số); không có thì bước hiển thị liên kết chức năng
        var tdx = await _db.Tenders.AsNoTracking().Where(t => t.Number.StartsWith(P + "TD-XD")).Select(t => new { t.Id, t.Number, t.Title }).ToListAsync();
        var hdx = await _db.Contracts.AsNoTracking().Where(c => c.Number.StartsWith(P + "HD-XD")).Select(c => new { c.Id, c.Number, c.Title }).ToListAsync();
        var gmx = await _db.ProcurementPackages.AsNoTracking().Where(c => c.Number.StartsWith(P + "GM-XD")).Select(c => new { c.Id, c.Number, c.Title }).ToListAsync();
        var khx = await _db.SelectionPlans.AsNoTracking().Where(c => c.Number.StartsWith(P + "KH-XD")).Select(c => new { c.Id, c.Number, c.Title }).ToListAsync();
        List<DocLink> TdNo(string no, string? suffix = null, string? action = null) => tdx.Where(t => t.Number == P + no).Select(t => new DocLink(t.Number + (suffix ?? ""), $"/Tenders/{action ?? "Details"}/{t.Id}", t.Title)).ToList();
        List<DocLink> HdNo(string no, string action = "Details", string? suffix = null) => hdx.Where(t => t.Number == P + no).Select(t => new DocLink(t.Number + (suffix ?? ""), $"/Contracts/{action}/{t.Id}", t.Title)).ToList();
        List<DocLink> GmNo(string no, string action = "Details", string controller = "Packages", string? suffix = null, string param = "") =>
            gmx.Where(t => t.Number == P + no).Select(t => new DocLink(t.Number + (suffix ?? ""), param == "" ? $"/{controller}/{action}/{t.Id}" : $"/{controller}/{action}?{param}={t.Id}", t.Title)).ToList();
        List<DocLink> KhNo(string no) => khx.Where(t => t.Number == P + no).Select(t => new DocLink(t.Number, $"/SelectionPlans/Details/{t.Id}", t.Title)).ToList();
        var evalTender = tdx.FirstOrDefault(t => t.Number == P + "TD-XD-002");
        var bidderLinks = evalTender == null ? new List<DocLink>() : (await _db.TenderBidders.AsNoTracking().Include(b => b.Vendor).Where(b => b.TenderId == evalTender.Id).OrderBy(b => b.BidPrice).ToListAsync())
            .Select(b => new DocLink(b.Vendor?.Name ?? "", $"/Tenders/Bidder/{b.Id}", b.DomesticPreferred ? "ưu đãi" : null)).ToList();
        var esgVendors = (await _db.VendorDocuments.AsNoTracking().Include(d => d.Vendor)
                .Where(d => d.Type == VendorDocumentType.Environmental || d.Type == VendorDocumentType.SocialResponsibility).OrderByDescending(d => d.ExpiryDate).Take(10).ToListAsync())
            .DistinctBy(d => d.VendorId).Take(3).Select(d => new DocLink(d.Vendor?.Code ?? "", $"/Vendors/Details/{d.VendorId}", d.Number)).ToList();
        var greenSet = await _db.CriteriaSets.AsNoTracking().Where(s => s.Name == CriteriaSeed.GreenSetName).Select(s => (int?)s.Id).FirstOrDefaultAsync();

        var contracts = await Docs(_db.Contracts.AsNoTracking().Include(x => x.Vendor).Where(x => x.Status == ContractStatus.Active).OrderByDescending(x => x.Id), x => x.Number, x => x.Title, x => NotificationService.UrlOf(DocType.Contract, x.Id));
        var grn = await _db.GoodsReceipts.AsNoTracking().Include(g => g.PurchaseOrder).OrderByDescending(g => g.Id).Take(30).ToListAsync();
        var awardedTender = await _db.Tenders.AsNoTracking().Where(t => t.Status >= TenderStatus.PendingResultApproval && t.Status != TenderStatus.Cancelled && t.Negotiations.Any())
            .OrderByDescending(t => t.Number.StartsWith(P)).ThenByDescending(t => t.Id).FirstOrDefaultAsync();

        var flows = new List<Flow>
        {
            new("pr", "1. Đề nghị mua hàng & phê duyệt", "file-text",
                "Phòng ban lập đề nghị → Trưởng bộ phận duyệt → vượt ngưỡng thì Ban Giám đốc duyệt → chuyển bộ phận mua hàng.",
                new()
                {
                    new("Lập đề nghị", "requester (Người đề nghị)", "Tạo đề nghị, chọn hàng hóa trong danh mục, số lượng, ngày cần. Lưu nháp rồi gửi duyệt.", Join(await Pr(PrStatus.Draft), L(("Tạo đề nghị mới", "/PurchaseRequests/Create")))),
                    new("Trưởng bộ phận duyệt", "approver (Trưởng bộ phận CNTT)", "Vào \"Chờ tôi duyệt\", xem dự toán, ngân sách còn lại, duyệt hoặc trả lại có lý do.", await Pr(PrStatus.Submitted)),
                    new("Ban Giám đốc duyệt", "director", "Đề nghị vượt ngưỡng (mặc định 50 triệu) chuyển Giám đốc. Ký xác nhận bằng mật khẩu khi duyệt.", await Pr(PrStatus.PendingDirector)),
                    new("Đã duyệt – chờ mua", "buyer (Nhân viên mua hàng)", "Mua hàng chọn hình thức lựa chọn NCC, gom vào gói mua sắm.", await Pr(PrStatus.Approved)),
                    new("Đang mua / hoàn tất / từ chối", "—", "Theo dõi tiến độ, nhật ký duyệt, đơn hàng phát sinh từ đề nghị.", Join(await Pr(PrStatus.Processing), await Pr(PrStatus.Rejected))),
                }),
            new("plan", "2. Kế hoạch mua sắm năm & gói mua sắm", "clipboard",
                "Các phòng lập kế hoạch năm → Giám đốc duyệt → đến kỳ tạo đề nghị từ kế hoạch. Mua hàng gom đề nghị thành gói, lập KHLCNT trình duyệt.",
                new()
                {
                    new("Kế hoạch năm của phòng", "kythuat.tp / nhansu.tp", "Lập theo quý; bị trả lại thì sửa và gửi lại.", Join(await Plan(PlanStatus.Draft), await Plan(PlanStatus.Returned))),
                    new("Giám đốc duyệt kế hoạch", "director", "Duyệt hoặc trả lại kế hoạch kèm ý kiến.", Join(await Plan(PlanStatus.Submitted), await Plan(PlanStatus.Approved))),
                    new("Gom gói mua sắm", "buyer", "Chọn các đề nghị đã duyệt, hệ thống gợi ý hình thức theo giá trị gói; chọn lĩnh vực của gói (quyết định hạn mức thời gian và mốc theo luật).", Join(await Pkg(PackageStatus.Draft), await Pkg(PackageStatus.InPlan))),
                    new("Lập BOQ & phân tích đơn giá theo định mức", "buyer", "Gói xây lắp: BOQ phân cấp, cơ cấu giá vật liệu / nhân công / máy, phiên bản BOQ. Biểu tượng ⓓ trên dòng BOQ mở phân tích đơn giá theo định mức hao phí, lấy giá từ ngân hàng giá và áp dụng vào đơn giá.",
                        GmNo("GM-XD-001", "Index", "Boq", " – BOQ (ⓓ ở dòng bê tông lót, xây tường, đào móng)", "packageId")),
                    new("Đánh giá rủi ro gói", "buyer", "Hệ thống tự chấm: giá sát ngưỡng hình thức, chia nhỏ gói, dự toán thiếu căn cứ / lệch ngân hàng giá, hình thức không cạnh tranh, ít NCC; người lập gói tích thêm các yếu tố thủ công. Gói rủi ro cao (≥ 40 điểm) phải nêu biện pháp kiểm soát.",
                        Join(GmNo("GM-XD-006", "Risk", suffix: " – rủi ro cao"), GmNo("GM-XD-007", "Risk", suffix: " – chia nhỏ gói"), GmNo("GM-XD-005", "Risk", suffix: " – đã có biện pháp"))),
                    new("KHLCNT trình duyệt", "buyer → director", "Đưa gói vào kế hoạch lựa chọn NCC, trình Giám đốc phê duyệt; gói được duyệt mới được tổ chức lựa chọn. Thử trình KHLCNT có gói rủi ro cao chưa có biện pháp kiểm soát: hệ thống chặn.",
                        Join(KhNo("KH-XD-002"), await Sp(SelectionPlanStatus.Submitted), await Sp(SelectionPlanStatus.Approved), await Pkg(PackageStatus.Executing))),
                    new("Quyền theo gói", "buyer", "Phân thành viên / người chỉ xem cho từng gói (Thiết lập → Quyền theo gói thầu).", L(("Quyền theo gói thầu", "/Settings/PackageAccess"))),
                }),
            new("rfq", "3. Chào hàng cạnh tranh", "send",
                "Gói giá trị vừa: mời ≥ 3 NCC báo giá (qua cổng NCC) → mở báo giá khi đến hạn → đánh giá → Giám đốc duyệt kết quả → lập đơn hàng.",
                new()
                {
                    new("Đang nhận báo giá", "buyer; NCC qua cổng", "NCC đăng nhập cổng, nộp báo giá trước hạn; báo giá niêm phong đến giờ mở. Khi phát hành, hệ thống kiểm tra thời gian chuẩn bị báo giá tối thiểu (mặc định 5 ngày làm việc – Thiết lập › Mốc thời gian theo luật).", await Rfq(RfqStatus.Open)),
                    new("Mở & đánh giá báo giá", "buyer2 (tổ đánh giá)", "So sánh giá, đánh giá kỹ thuật, chấm điểm; báo giá không đạt bị loại.", await Rfq(RfqStatus.Opened)),
                    new("Chờ duyệt kết quả", "director", "Duyệt NCC được chọn và giá.", await Rfq(RfqStatus.PendingApproval)),
                    new("Đã chọn NCC → đơn hàng", "buyer", "Lập PO từ kết quả chào giá.", await Rfq(RfqStatus.Awarded)),
                }),
            new("da", "4. Chỉ định / mua sắm trực tiếp", "award",
                "Gói nhỏ hoặc trường hợp đặc biệt (khẩn cấp, độc quyền, tương thích): thương thảo với một NCC → tờ trình → Giám đốc duyệt → đơn hàng.",
                new()
                {
                    new("Thương thảo (nháp)", "buyer", "Nhập báo giá NCC gửi qua cổng, giá thương thảo, lý do chỉ định, file chứng minh.", await Da(DirectAwardStatus.Draft)),
                    new("Chờ Giám đốc duyệt", "director", "Hệ thống cảnh báo nếu nghi chia nhỏ gói để chỉ định.", await Da(DirectAwardStatus.PendingApproval)),
                    new("Đã duyệt → đơn hàng", "buyer", "Lập PO từ tờ trình.", await Da(DirectAwardStatus.Approved)),
                }),
            new("td", "5. Đấu thầu", "gavel",
                "Gói lớn: lập KH & HSMT → duyệt → phát hành, NCC hỏi đáp / nộp HSDT qua cổng → mở thầu → đánh giá (hợp lệ, năng lực, kỹ thuật, giá) → đàm phán → thẩm định → Giám đốc duyệt kết quả → hợp đồng.",
                new()
                {
                    new("Lập KH & HSMT, trình duyệt", "buyer → director", "Chèn tiêu chí từ bộ tiêu chí HSDT; chọn tổ chuyên gia; khai báo đấu thầu quốc tế, cho phép liên danh, thầu phụ tối đa, ưu đãi trong nước.", Join(TdNo("TD-XD-003"), await Td(TenderStatus.Draft, TenderStatus.PendingPlanApproval))),
                    new("Yêu cầu năng lực có cấu trúc", "buyer", "Khi HSMT còn nháp: khai báo hợp đồng tương tự, doanh thu, tài chính, nhân sự chủ chốt, thiết bị với ngưỡng đo được – hệ thống tự đối chiếu với kê khai của nhà thầu.",
                        Join(TdNo("TD-XD-003", " – thiết lập", "Requirements"), TdNo("TD-XD-002", " – 7 yêu cầu", "Requirements"))),
                    new("Kiểm tra mốc thời gian theo luật", "buyer → director", "Khung 'Mốc thời gian theo luật' trên gói thầu: thời gian chuẩn bị HSDT tối thiểu (trong nước / quốc tế, gói quy mô nhỏ), sửa đổi HSMT trước đóng thầu, hiệu lực HSDT. MAU-TD-XD-003 đặt đóng thầu sau 8 ngày → bấm 'Trình duyệt' sẽ bị chặn.",
                        Join(TdNo("TD-XD-003", " – vi phạm"), TdNo("TD-XD-002", " – đạt"), L(("Thiết lập mốc thời gian", "/Settings/LegalTimelines")))),
                    new("Đang mời thầu", "NCC qua cổng; buyer", "NCC tải HSMT, gửi câu hỏi làm rõ, nộp HSDT (niêm phong đến giờ mở thầu). Mua hàng nhập liên danh / thầu phụ, kê khai năng lực, ưu đãi theo HSDT (nút 'Năng lực' từng nhà thầu).", await Td(TenderStatus.Published)),
                    new("Mở thầu & đánh giá", "tổ chuyên gia", "Đánh giá hợp lệ → năng lực (có kết quả đối chiếu kê khai tự động, cảnh báo cơ cấu liên danh / thầu phụ; kết luận khác đề xuất phải giải trình) → chấm kỹ thuật → mở giá. Ưu đãi: nhà thầu không được ưu đãi bị cộng % vào giá xếp hạng.",
                        Join(TdNo("TD-XD-002", " – đánh giá năng lực", "Screen"), bidderLinks, await Td(TenderStatus.Opened), L(("Đánh giá HSDT/HSCG", "/Tenders/Evaluations")))),
                    new("Đàm phán & thẩm định", "buyer; người thẩm định (ketoan.tp)", "Đàm phán với nhà thầu xếp hạng 1, lập biên bản; Giám đốc giao thẩm định, báo cáo thẩm định bắt buộc trước khi duyệt.",
                        Join(awardedTender == null ? new() : L((awardedTender.Number + " – biên bản đàm phán", $"/Tenders/Details/{awardedTender.Id}")), L(("Đàm phán hợp đồng", "/Tenders/Negotiations"), ("Thẩm định kết quả LCNT", "/Tenders/Appraisals")))),
                    new("Duyệt kết quả → hợp đồng", "director → buyer", "Công bố kết quả, thời hạn kiến nghị; ký hợp đồng.", await Td(TenderStatus.PendingResultApproval, TenderStatus.Awarded, TenderStatus.Contracted)),
                }),
            new("exec", "6. Hợp đồng → đơn hàng → giao nhận → hóa đơn → thanh toán", "file-check",
                "Thực hiện hợp đồng: đặt hàng theo đợt, NCC báo giao hàng qua cổng, kho nhận hàng (đạt / không đạt), kế toán đối chiếu 3 bên và thanh toán.",
                new()
                {
                    new("Hợp đồng đang thực hiện", "buyer", "Theo dõi đợt thanh toán, bảo lãnh, phụ lục, cảnh báo hết hạn.", contracts),
                    new("Đơn hàng chờ duyệt / đã gửi NCC", "director; buyer", "PO vượt ngưỡng chờ Giám đốc; vượt giá trần bảng giá tiêu chuẩn phải giải trình.", Join(await Po(PoStatus.PendingApproval), await Po(PoStatus.Issued))),
                    new("Nhận hàng", "buyer / thủ kho", "Lập phiếu nhận hàng; hàng lỗi ghi số lượng không đạt, NCC giao bù.", Join(await Po(PoStatus.PartiallyReceived),
                        grn.OrderByDescending(g => g.Number.StartsWith(P)).Take(1).Select(g => new DocLink(g.Number, $"/GoodsReceipts/Details/{g.Id}", g.PurchaseOrder?.Number)).ToList())),
                    new("Hóa đơn – đối chiếu 3 bên", "ketoan.tt (Kế toán)", "Khớp PO – phiếu nhận – hóa đơn; lệch thì xử lý ngoại lệ.", Join(await Inv(InvoiceStatus.Received), await Inv(InvoiceStatus.Exception), await Inv(InvoiceStatus.Matched))),
                    new("Đề nghị thanh toán & chi", "ketoan.tt → director", "Đề nghị thanh toán theo hóa đơn / đợt hợp đồng / khối lượng nghiệm thu → duyệt → ghi nhận đã chi. Hệ thống tự trừ phạt chậm giao, thu hồi tạm ứng theo lộ trình và giữ lại bảo hành theo điều khoản hợp đồng.", Join(await Pay(PaymentStatus.Requested), await Pay(PaymentStatus.Approved), await Pay(PaymentStatus.Paid))),
                }),
            new("boq", "7. Hợp đồng xây dựng theo BOQ", "ruler",
                "Gói xây lắp: đấu thầu theo BOQ (so sánh giá từng dòng) → hợp đồng có BOQ → nghiệm thu khối lượng từng đợt → thanh toán theo khối lượng → phụ lục phát sinh → điều chỉnh giá theo chỉ số → so sánh dự toán – trúng thầu – thực tế.",
                new()
                {
                    new("So sánh giá dự thầu theo BOQ", "buyer; tổ chuyên gia", "Bảng so sánh đơn giá từng dòng BOQ giữa các nhà thầu, cảnh báo đơn giá thấp / cao bất thường.", TdNo("TD-XD-001", " – so sánh BOQ", "BoqCompare")),
                    new("Hợp đồng & tiến độ khối lượng", "buyer", "Hợp đồng có BOQ: khối lượng hợp đồng, đã nghiệm thu, còn lại theo từng dòng; điều khoản giữ lại 5%, thu hồi tạm ứng đến 80% giá trị lũy kế.", HdNo("HD-XD-001")),
                    new("Nghiệm thu khối lượng", "buyer → director", "Lập biên bản nghiệm thu từng đợt (không vượt khối lượng hợp đồng), Giám đốc xác nhận; biên bản đã xác nhận là căn cứ đề nghị thanh toán.", Join(hdx.Where(h => h.Number == P + "HD-XD-001").Select(h => new DocLink("Biên bản nghiệm thu", $"/Acceptances?contractId={h.Id}")).ToList())),
                    new("Phụ lục khối lượng phát sinh", "buyer → director", "Điều chỉnh khối lượng / thêm dòng phát sinh, giữ khối lượng gốc; áp dụng khi Giám đốc duyệt.", HdNo("HD-XD-001", "Variation", " – phát sinh")),
                    new("Điều chỉnh giá theo chỉ số", "buyer → director → ketoan.tt", "Công thức Pn = P0 × (a + Σbᵢ·Iᵢ/I₀ᵢ); mỗi kỳ theo biên bản nghiệm thu, nhập chỉ số công bố; Giám đốc duyệt; phần tăng đề nghị thanh toán, phần giảm trừ khi quyết toán. Mẫu: 1 kỳ đã duyệt & chi, 1 kỳ chờ duyệt.", HdNo("HD-XD-001", "PriceAdjustment", " – điều chỉnh giá")),
                    new("Dự toán – trúng thầu – thực tế", "buyer; director", "So sánh theo từng dòng BOQ và theo gói cho mọi hình thức.", Join(HdNo("HD-XD-001", "Actuals", " – thực tế"), L(("Dự toán – thực tế theo gói", "/Packages/ActualsSummary")))),
                }),
            new("close", "8. Quyết toán, hoàn trả giữ lại & thanh lý hợp đồng", "file-check",
                "Hoàn thành hợp đồng → quyết toán (giá trị nghiệm thu + điều chỉnh giá − phạt − giảm trừ − đã chi) → Giám đốc duyệt, hệ thống lập đề nghị thanh toán phần còn lại → hết bảo hành (hoặc có bảo lãnh bảo hành) thì hoàn trả tiền giữ lại → giải tỏa bảo lãnh → thanh lý.",
                new()
                {
                    new("Hoàn thành hợp đồng", "buyer", "Nút 'Hoàn thành' trên hợp đồng (nghiệm thu bàn giao), sau đó vào trang Quyết toán & thanh lý.", HdNo("HD-XD-001", "Settlement", " – đang thực hiện")),
                    new("Lập & duyệt quyết toán", "buyer / ketoan.tt → director", "Trang quyết toán tính sẵn số còn phải trả; còn đề nghị thanh toán chờ chi hoặc điều chỉnh giá chờ duyệt thì chưa lập được. Giám đốc duyệt (ký xác nhận).", HdNo("HD-XD-002", "Settlement", " – đã quyết toán")),
                    new("Hoàn trả tiền giữ lại", "ketoan.tt → director", "Chỉ lập được khi hết hạn bảo hành, hoặc nhà thầu nộp bảo lãnh bảo hành đủ giá trị thay thế.", HdNo("HD-XD-002", "Settlement", " – đã hoàn trả")),
                    new("Thanh lý & in biên bản", "buyer", "Danh mục điều kiện: quyết toán đã duyệt, không còn đề nghị chờ chi, đã thu hồi hết tạm ứng, đã hoàn trả giữ lại, đã giải tỏa bảo lãnh. In biên bản quyết toán / thanh lý.", HdNo("HD-XD-002", "Details", " – đã thanh lý")),
                }),
            new("comply", "9. Tuân thủ, báo cáo & lưu trữ hồ sơ", "scale",
                "Thông số pháp lý cấu hình được; báo cáo tình hình lựa chọn nhà thầu theo năm; cảnh báo rủi ro; theo dõi tiến độ gói; sổ lưu trữ hồ sơ và xuất trọn bộ hồ sơ gói.",
                new()
                {
                    new("Mốc thời gian theo luật", "admin", "Số ngày tối thiểu theo Điều 45 Luật Đấu thầu (cấu hình được khi văn bản sửa đổi), chặn hoặc chỉ cảnh báo, thời hạn lưu trữ hồ sơ.", L(("Thiết lập mốc thời gian theo luật", "/Settings/LegalTimelines"))),
                    new("Báo cáo năm về lựa chọn nhà thầu", "director; buyer", "Số gói, giá gói, giá trúng thầu, tiết kiệm theo hình thức / lĩnh vực; đấu thầu: số HSDT, liên danh, kiến nghị, tuân thủ mốc thời gian; gói hủy. Xuất Excel.", L(("Tình hình thực hiện công tác LCNT", "/Reports/Show/bidding"), ("Cảnh báo rủi ro mua sắm", "/Reports/Show/risk"))),
                    new("Tiến độ gói & quá hạn", "buyer; director", "6 giai đoạn từ lập gói đến thanh toán, hạn mức thời gian theo giá trị / lĩnh vực / hình thức, cảnh báo sắp quá hạn và quá hạn.", L(("Tiến độ gói", "/Packages/Progress"), ("Hạn mức & thời gian gói", "/Settings/PackageTiers"))),
                    new("Sổ lưu trữ & xuất hồ sơ", "buyer; ketoan.tt", "Hồ sơ đóng khi gói hoàn tất / hủy và hợp đồng đã thanh lý; theo dõi hạn lưu trữ (đang lưu / sắp hết / hết hạn). Xuất ZIP gồm mục lục, nhật ký từng chứng từ và file đính kèm.",
                        Join(L(("Lưu trữ hồ sơ", "/Archive"), ("Sắp hết thời hạn", "/Archive?state=S%E1%BA%AFp%20h%E1%BA%BFt%20th%E1%BB%9Di%20h%E1%BA%A1n")), GmNo("GM-XD-003", "Dossier", "Archive", " – xuất ZIP"))),
                }),
            new("esg", "10. Mua sắm xanh – ESG", "shield-check",
                "Tiêu chí xanh trong bộ tiêu chí đánh giá, chứng nhận môi trường / an toàn – xã hội của nhà cung cấp và báo cáo tỷ trọng chi tiêu xanh.",
                new()
                {
                    new("Bộ tiêu chí xanh", "buyer", "Đánh dấu tiêu chí 'Xanh / ESG' trong bộ tiêu chí; bộ mẫu 7 tiêu chí (môi trường, năng lượng, chất thải, an toàn lao động, trách nhiệm xã hội, liêm chính) để chèn vào chấm điểm kỹ thuật.",
                        greenSet.HasValue ? L(("Mua sắm xanh – ESG (bộ tiêu chí)", $"/CriteriaSets/Edit/{greenSet}")) : L(("Bộ tiêu chí", "/CriteriaSets?group=tender"))),
                    new("Chứng nhận ESG của NCC", "buyer; NCC qua cổng", "Hồ sơ NCC loại 'Chứng nhận môi trường' / 'An toàn lao động – trách nhiệm xã hội' (ISO 14001, ISO 45001, SA8000, nhãn sinh thái…), xác minh và theo dõi hạn hiệu lực.", esgVendors),
                    new("Báo cáo mua sắm xanh", "director", "Tỷ trọng chi tiêu với NCC có chứng nhận theo nhóm hàng, chứng nhận sắp hết hạn, các bộ tiêu chí có tiêu chí xanh.", L(("Mua sắm xanh – ESG", "/Reports/Show/green"))),
                }),
            new("src", "11. Quản lý nguồn cung", "network",
                "Sơ tuyển để có danh sách NCC đủ năng lực, ngân hàng giá làm căn cứ, bảng giá tiêu chuẩn có giá trần, đánh giá & xếp loại NCC định kỳ.",
                new()
                {
                    new("Sơ tuyển: phát hành & nhận hồ sơ", "buyer; NCC qua cổng", "Chèn tiêu chí từ bộ tiêu chí; NCC đăng ký và đính kèm hồ sơ pháp lý.", Join(await Pq(PrequalificationStatus.Draft), await Pq(PrequalificationStatus.Open))),
                    new("Sơ tuyển: đánh giá & duyệt", "buyer2 → director", "Chấm từng tiêu chí, đạt / không đạt, trình duyệt danh sách.", Join(await Pq(PrequalificationStatus.Evaluating), await Pq(PrequalificationStatus.PendingApproval), await Pq(PrequalificationStatus.Completed))),
                    new("Ngân hàng giá", "buyer", "Giá khảo sát, niêm yết, công bố + giá trúng thầu / PO thực tế; xem biến động theo mặt hàng; lịch sử giá chi tiết (nguồn giá, NCC, tiền tệ, đơn giá vật tư / thi công kèm thuế) thêm, sửa, xóa được.", L(("Ngân hàng giá", "/PriceBank"), ("Lịch sử giá", "/PriceBank/Records"))),
                    new("Bảng giá tiêu chuẩn", "buyer → director", "Lập giá chuẩn + giá trần, trình duyệt; PO vượt trần phải nêu lý do.", Join(await Bg(StandardPriceStatus.Draft), await Bg(StandardPriceStatus.Submitted), await Bg(StandardPriceStatus.Approved))),
                    new("Đánh giá & xếp loại NCC", "buyer", "Đánh giá định kỳ theo tiêu chí có trọng số; xếp loại A/B/C; hồ sơ pháp lý chờ xác minh / sắp hết hạn.", L(("Đánh giá nhà cung cấp", "/SupplierEvaluations"), ("Danh sách nhà cung cấp", "/Vendors"))),
                }),
            new("portal", "12. Cổng nhà cung cấp", "building",
                "NCC tự đăng ký, cập nhật hồ sơ pháp lý, nhận lời mời chào giá / thầu, nộp báo giá và HSDT, xem đơn hàng, báo giao hàng, gửi hóa đơn.",
                new()
                {
                    new("Đăng nhập cổng NCC", $"ncc1{SampleFlows.VendorDomain} … ncc4{SampleFlows.VendorDomain} / {SampleFlows.Password}", "Mở trang cổng NCC ở cửa sổ ẩn danh (phiên đăng nhập riêng với trang nội bộ).", L(("Cổng nhà cung cấp", "/Portal"), ("Thông báo mời thầu công khai", "/PortalPublic/Notices"))),
                    new("Thử nghiệp vụ trên cổng", "NCC", "Nộp báo giá cho RFQ đang mở, gửi câu hỏi làm rõ HSMT, đăng ký sơ tuyển, báo giao hàng cho đơn đã gửi NCC.", L(("Quản lý chào giá", "/PortalQuotes"), ("Quản lý gói thầu", "/PortalTenders"), ("Đăng ký sơ tuyển", "/PortalPrequalifications"), ("Đơn đặt hàng", "/PortalOrders"))),
                }),
        };

        var accounts = await _db.Users.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Role).ThenBy(x => x.Username)
            .Select(x => new Account(x.Username, x.FullName, x.Role, x.Department)).ToListAsync();
        ViewBag.Accounts = accounts;
        ViewBag.SampleLoaded = await _db.PurchaseRequests.AnyAsync(x => x.Number.StartsWith(P));
        ViewData["Title"] = "Kịch bản nghiệp vụ";
        return View(flows);
    }
}
