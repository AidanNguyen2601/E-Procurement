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
                    new("Gom gói mua sắm", "buyer", "Chọn các đề nghị đã duyệt, hệ thống gợi ý hình thức theo giá trị gói.", Join(await Pkg(PackageStatus.Draft), await Pkg(PackageStatus.InPlan))),
                    new("KHLCNT trình duyệt", "buyer → director", "Đưa gói vào kế hoạch lựa chọn NCC, trình Giám đốc phê duyệt; gói được duyệt mới được tổ chức lựa chọn.", Join(await Sp(SelectionPlanStatus.Submitted), await Sp(SelectionPlanStatus.Approved), await Pkg(PackageStatus.Executing))),
                    new("Quyền theo gói", "buyer", "Phân thành viên / người chỉ xem cho từng gói (Thiết lập → Quyền theo gói thầu).", L(("Quyền theo gói thầu", "/Settings/PackageAccess"))),
                }),
            new("rfq", "3. Chào hàng cạnh tranh", "send",
                "Gói giá trị vừa: mời ≥ 3 NCC báo giá (qua cổng NCC) → mở báo giá khi đến hạn → đánh giá → Giám đốc duyệt kết quả → lập đơn hàng.",
                new()
                {
                    new("Đang nhận báo giá", "buyer; NCC qua cổng", "NCC đăng nhập cổng, nộp báo giá trước hạn; báo giá niêm phong đến giờ mở.", await Rfq(RfqStatus.Open)),
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
                    new("Lập KH & HSMT, trình duyệt", "buyer → director", "Chèn tiêu chí từ bộ tiêu chí HSDT; chọn tổ chuyên gia.", await Td(TenderStatus.Draft, TenderStatus.PendingPlanApproval)),
                    new("Đang mời thầu", "NCC qua cổng; buyer", "NCC tải HSMT, gửi câu hỏi làm rõ, nộp HSDT (niêm phong đến giờ mở thầu).", await Td(TenderStatus.Published)),
                    new("Mở thầu & đánh giá", "tổ chuyên gia", "Đánh giá hợp lệ → năng lực → chấm kỹ thuật từng thành viên → mở giá (hai túi hồ sơ).", Join(await Td(TenderStatus.Opened), L(("Đánh giá HSDT/HSCG", "/Tenders/Evaluations")))),
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
                    new("Đề nghị thanh toán & chi", "ketoan.tt → director", "Đề nghị thanh toán theo hóa đơn / đợt hợp đồng → duyệt → ghi nhận đã chi.", Join(await Pay(PaymentStatus.Requested), await Pay(PaymentStatus.Approved), await Pay(PaymentStatus.Paid))),
                }),
            new("src", "7. Quản lý nguồn cung", "network",
                "Sơ tuyển để có danh sách NCC đủ năng lực, ngân hàng giá làm căn cứ, bảng giá tiêu chuẩn có giá trần, đánh giá & xếp loại NCC định kỳ.",
                new()
                {
                    new("Sơ tuyển: phát hành & nhận hồ sơ", "buyer; NCC qua cổng", "Chèn tiêu chí từ bộ tiêu chí; NCC đăng ký và đính kèm hồ sơ pháp lý.", Join(await Pq(PrequalificationStatus.Draft), await Pq(PrequalificationStatus.Open))),
                    new("Sơ tuyển: đánh giá & duyệt", "buyer2 → director", "Chấm từng tiêu chí, đạt / không đạt, trình duyệt danh sách.", Join(await Pq(PrequalificationStatus.Evaluating), await Pq(PrequalificationStatus.PendingApproval), await Pq(PrequalificationStatus.Completed))),
                    new("Ngân hàng giá", "buyer", "Giá khảo sát, niêm yết, công bố + giá trúng thầu / PO thực tế; xem biến động theo mặt hàng.", L(("Ngân hàng giá", "/PriceBank"))),
                    new("Bảng giá tiêu chuẩn", "buyer → director", "Lập giá chuẩn + giá trần, trình duyệt; PO vượt trần phải nêu lý do.", Join(await Bg(StandardPriceStatus.Draft), await Bg(StandardPriceStatus.Submitted), await Bg(StandardPriceStatus.Approved))),
                    new("Đánh giá & xếp loại NCC", "buyer", "Đánh giá định kỳ theo tiêu chí có trọng số; xếp loại A/B/C; hồ sơ pháp lý chờ xác minh / sắp hết hạn.", L(("Đánh giá nhà cung cấp", "/SupplierEvaluations"), ("Danh sách nhà cung cấp", "/Vendors"))),
                }),
            new("portal", "8. Cổng nhà cung cấp", "building",
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
