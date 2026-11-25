using System.Net;
using System.Net.Mail;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Infrastructure;

public class NotificationSettings
{
    /// <summary>Chu kỳ quét nhắc hạn + gửi email (phút)</summary>
    public int IntervalMinutes { get; set; } = 30;
    /// <summary>Nhắc chứng từ chờ duyệt quá N ngày</summary>
    public int StaleApprovalDays { get; set; } = 2;
    /// <summary>Nhắc hợp đồng / bảo lãnh sắp hết hạn trong N ngày</summary>
    public int ExpiryWarningDays { get; set; } = 30;
    /// <summary>Địa chỉ gốc của site để tạo link trong email, ví dụ https://eproc.congty.vn</summary>
    public string BaseUrl { get; set; } = "http://localhost:8110";
    public EmailSettings Email { get; set; } = new();
}

public class EmailSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool EnableSsl { get; set; } = true;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "eprocurement@company.local";
    public string FromName { get; set; } = "E-Procurement";
}

/// <summary>Một bước nghiệp vụ vừa được ghi nhật ký (đầu vào để sinh thông báo)</summary>
public record DocEvent(DocType Type, int Id, int ActorId, string Action, string? Comment);

/// <summary>
/// Sinh thông báo:
/// - "Cần xử lý": gửi người phải thao tác ở trạng thái hiện tại của chứng từ (trưởng bộ phận, Giám đốc, tổ chuyên gia…), mỗi trạng thái một lần.
/// - "Thông tin": người lập / người đề nghị được báo khi người khác thao tác trên chứng từ của mình.
/// - "Nhắc hạn": quét định kỳ (hạn báo giá, đóng thầu, PO trễ, hợp đồng / bảo lãnh sắp hết hạn, chứng từ chờ duyệt lâu).
/// </summary>
public class NotificationService
{
    private readonly EProcDbContext _db;
    private readonly NotificationSettings _s;
    private readonly ProcurementSettings _p;
    private readonly SettingsStore? _store;
    private NotifyTemplates? _tpl;
    public NotificationService(EProcDbContext db, IOptions<NotificationSettings> s, IOptions<ProcurementSettings> p, SettingsStore? store = null)
    { _db = db; _s = s.Value; _p = p.Value; _store = store; }

    // ------------------------------------------------------------------ cấu hình (Thiết lập → Cảnh báo / Email / Mẫu thông báo)
    private async Task<NotifyTemplates> TemplatesAsync() =>
        _tpl ??= _store == null ? new NotifyTemplates() : await _store.GetAsync(SettingsStore.Templates, () => new NotifyTemplates());

    public async Task<AlertSettings> AlertsAsync() => _store == null
        ? AlertSettings.FromLegacy(_s.ExpiryWarningDays, _s.StaleApprovalDays, _p.BudgetWarnPercent)
        : await _store.GetAsync(SettingsStore.Alerts, () => AlertSettings.FromLegacy(_s.ExpiryWarningDays, _s.StaleApprovalDays, _p.BudgetWarnPercent));

    public async Task<EmailConfig> EmailConfigAsync() => _store == null
        ? EmailConfig.FromLegacy(_s) : await _store.GetAsync(SettingsStore.Email, () => EmailConfig.FromLegacy(_s));

    private static SmtpClient Smtp(EmailConfig e)
    {
        var smtp = new SmtpClient(e.Host, e.Port) { EnableSsl = e.EnableSsl, Timeout = 20000 };
        if (!string.IsNullOrEmpty(e.UserName)) smtp.Credentials = new NetworkCredential(e.UserName, e.Password);
        return smtp;
    }

    private static Dictionary<string, string> Values(string title, string? message, string recipient, string link, DateTime at, string company) => new()
    {
        ["{TieuDe}"] = title, ["{NoiDung}"] = message ?? "", ["{NguoiNhan}"] = recipient, ["{LienKet}"] = link,
        ["{ThoiGian}"] = at.ToString("dd/MM/yyyy HH:mm"), ["{CongTy}"] = company
    };

    private async Task<string> CompanyAsync() =>
        _store == null ? "" : (await _store.GetAsync(SettingsStore.Print, () => new PrintSettings())).CompanyName;

    /// <summary>Gửi email thử bằng cấu hình và mẫu đang nhập (trước khi lưu)</summary>
    public async Task SendTestAsync(EmailConfig e, NotifyTemplates tpl, string to, string recipientName)
    {
        var v = Values("Email thử từ E-Procurement", "Cấu hình máy chủ gửi email hoạt động bình thường.", recipientName, e.BaseUrl, DateTime.Now, await CompanyAsync());
        using var smtp = Smtp(e);
        using var msg = new MailMessage(new MailAddress(e.From, e.FromName), new MailAddress(to))
            { Subject = NotifyTemplates.Render(tpl.Subject, v, false), Body = NotifyTemplates.Render(tpl.Body, v, true), IsBodyHtml = true };
        await smtp.SendMailAsync(msg);
    }

    public static string UrlOf(DocType t, int id) => t switch
    {
        DocType.PR => $"/PurchaseRequests/Details/{id}",
        DocType.PO => $"/PurchaseOrders/Details/{id}",
        DocType.RFQ => $"/Rfqs/Details/{id}",
        DocType.DirectAward => $"/DirectAwards/Details/{id}",
        DocType.Tender => $"/Tenders/Details/{id}",
        DocType.Contract => $"/Contracts/Details/{id}",
        DocType.Invoice => $"/Invoices/Details/{id}",
        DocType.Payment => $"/Payments/Details/{id}",
        DocType.Plan => $"/Plans/Details/{id}",
        DocType.Package => $"/Packages/Details/{id}",
        DocType.SelectionPlan => $"/SelectionPlans/Details/{id}",
        DocType.Prequalification => $"/Prequalifications/Details/{id}",
        DocType.StandardPriceList => $"/StandardPrices/Details/{id}",
        _ => "/"
    };

    // ------------------------------------------------------------------ sự kiện nghiệp vụ
    public async Task OnEventsAsync(IEnumerable<DocEvent> events)
    {
        await TemplatesAsync();
        foreach (var e in events.GroupBy(e => (e.Type, e.Id)).Select(g => g.Last()))
            await OnEventAsync(e);
        await _db.SaveChangesAsync();
    }

    private async Task OnEventAsync(DocEvent e)
    {
        var url = UrlOf(e.Type, e.Id);
        switch (e.Type)
        {
            case DocType.PR:
            {
                var pr = await _db.PurchaseRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (pr == null) return;
                var label = $"{pr.Number} – {pr.Title}";
                if (pr.Status == PrStatus.Submitted)
                    Add(await UsersAsync(u => u.Role == AppRoles.Approver && u.Department == pr.Department && u.Id != pr.RequesterId), NotificationKind.Action,
                        $"Đề nghị chờ bạn duyệt: {pr.Number}", label, e, $"act:PR:{pr.Id}:{pr.Status}");
                if (pr.Status == PrStatus.PendingDirector)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Đề nghị chờ Giám đốc duyệt: {pr.Number}", $"{label} · {pr.EstimatedTotal.ToMoney()} đ", e, $"act:PR:{pr.Id}:{pr.Status}");
                if (pr.Status == PrStatus.Approved)
                    Add(await UsersAsync(u => u.Role == AppRoles.Buyer), NotificationKind.Action, $"Đề nghị đã duyệt, chờ mua hàng: {pr.Number}", label, e, $"act:PR:{pr.Id}:{pr.Status}");
                Owner(pr.RequesterId, e, $"{pr.Number}: {e.Action}", pr.Status == PrStatus.Rejected ? NotificationKind.Warning : NotificationKind.Info);
                break;
            }
            case DocType.PO:
            {
                var po = await _db.PurchaseOrders.AsNoTracking().Include(x => x.PurchaseRequest).Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == e.Id);
                if (po == null) return;
                if (po.Status == PoStatus.PendingApproval)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Đơn mua hàng chờ duyệt: {po.Number}", $"{po.Vendor?.Name} · {po.Total.ToMoney()} đ", e, $"act:PO:{po.Id}:{po.Status}");
                Owner(po.BuyerId, e, $"{po.Number}: {e.Action}");
                if (po.Status == PoStatus.Received && po.PurchaseRequest != null)
                    Add(new[] { po.PurchaseRequest.RequesterId }, NotificationKind.Info, $"Hàng đã về đủ: {po.Number}",
                        $"Theo đề nghị {po.PurchaseRequest.Number} – {po.PurchaseRequest.Title}", e, $"info:PO:{po.Id}:received", UrlOf(DocType.PR, po.PurchaseRequest.Id));
                break;
            }
            case DocType.RFQ:
            {
                var r = await _db.Rfqs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (r == null) return;
                if (r.Status == RfqStatus.PendingApproval)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Kết quả chào hàng chờ duyệt: {r.Number}", r.Title, e, $"act:RFQ:{r.Id}:{r.Status}");
                Owner(r.BuyerId, e, $"{r.Number}: {e.Action}");
                break;
            }
            case DocType.DirectAward:
            {
                var d = await _db.DirectAwards.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (d == null) return;
                if (d.Status == DirectAwardStatus.PendingApproval)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Tờ trình chỉ định thầu chờ duyệt: {d.Number}", $"{d.Title} · {d.NegotiatedTotal.ToMoney()} đ", e,
                        $"act:DA:{d.Id}:{d.Status}:{d.SubmittedAt:yyyyMMddHHmm}");
                Owner(d.BuyerId, e, $"{d.Number}: {e.Action}");
                break;
            }
            case DocType.Tender:
            {
                var t = await _db.Tenders.AsNoTracking().Include(x => x.Committee).Include(x => x.Appraisals).ThenInclude(a => a.Appraiser).FirstOrDefaultAsync(x => x.Id == e.Id);
                if (t == null) return;
                var members = t.Committee.Select(m => m.UserId).ToList();
                var appraisal = TenderEvaluation.CurrentAppraisal(t.Appraisals);
                if (t.Status == TenderStatus.PendingResultApproval && appraisal is { CompletedAt: null })
                    Add(new[] { appraisal.AppraiserId }, NotificationKind.Action, $"Bạn được giao thẩm định kết quả LCNT gói {t.Number}", t.Title, e, $"act:TD:{t.Id}:appraise:{appraisal.Id}");
                var waitingAppraisal = t.Status == TenderStatus.PendingResultApproval && TenderEvaluation.ApprovalBlocker(t, _p.TenderAppraisalRequired) != null;
                if (t.Status is TenderStatus.PendingPlanApproval or TenderStatus.PendingResultApproval && !waitingAppraisal)
                    Add(await DirectorsAsync(), NotificationKind.Action,
                        (t.Status == TenderStatus.PendingPlanApproval ? "HSMT chờ duyệt: " : "Kết quả lựa chọn nhà thầu chờ duyệt: ") + t.Number, t.Title, e,
                        $"act:TD:{t.Id}:{t.Status}" + (appraisal?.CompletedAt != null ? $":{appraisal.Id}" : ""));
                if (t.Status == TenderStatus.Published)
                    Add(members, NotificationKind.Info, $"Bạn trong tổ chuyên gia gói thầu {t.Number}", $"{t.Title} · đóng thầu {t.ClosingAt.ToDateTime()}", e, $"info:TD:{t.Id}:member");
                if (t.Status == TenderStatus.Opened)
                    Add(t.Committee.Where(m => m.ScoringCompletedAt == null).Select(m => m.UserId), NotificationKind.Action,
                        $"Gói thầu {t.Number} đã mở thầu – cần đánh giá, chấm điểm", t.Title, e, $"act:TD:{t.Id}:{t.Status}");
                Owner(t.BuyerId, e, $"{t.Number}: {e.Action}");
                break;
            }
            case DocType.Contract:
            {
                var c = await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (c == null) return;
                if (await _db.ContractAmendments.AnyAsync(a => a.ContractId == c.Id && a.Status == AmendmentStatus.Pending))
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Phụ lục hợp đồng {c.Number} chờ duyệt", c.Title, e,
                        $"act:HD:{c.Id}:amend:{await _db.ContractAmendments.Where(a => a.ContractId == c.Id).CountAsync()}");
                Owner(c.CreatedById, e, $"{c.Number}: {e.Action}");
                break;
            }
            case DocType.Prequalification:
            {
                var pq = await _db.Prequalifications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (pq == null) return;
                if (pq.Status == PrequalificationStatus.PendingApproval)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Kết quả sơ tuyển NCC chờ duyệt: {pq.Number}", pq.Title, e, $"act:ST:{pq.Id}:{pq.Status}");
                Owner(pq.CreatedById, e, $"{pq.Number}: {e.Action}");
                break;
            }
            case DocType.StandardPriceList:
            {
                var sl = await _db.StandardPriceLists.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (sl == null) return;
                if (sl.Status == StandardPriceStatus.Submitted)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Bảng giá tiêu chuẩn chờ duyệt: {sl.Number}", sl.Title, e, $"act:BG:{sl.Id}:{sl.SubmittedAt:yyyyMMddHHmm}");
                if (sl.Status == StandardPriceStatus.Approved)
                    Add(await UsersAsync(u => u.Role == AppRoles.Buyer), NotificationKind.Info, $"Bảng giá tiêu chuẩn {sl.Number} đã duyệt – áp dụng từ {sl.EffectiveFrom:dd/MM/yyyy}", sl.Title, e, $"info:BG:{sl.Id}:approved");
                Owner(sl.CreatedById, e, $"{sl.Number}: {e.Action}", sl.Status == StandardPriceStatus.Returned ? NotificationKind.Warning : NotificationKind.Info);
                break;
            }
            case DocType.SelectionPlan:
            {
                var sp = await _db.SelectionPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (sp == null) return;
                if (sp.Status == SelectionPlanStatus.Submitted)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Kế hoạch lựa chọn NCC chờ duyệt: {sp.Number}", sp.Title, e, $"act:KH:{sp.Id}:{sp.SubmittedAt:yyyyMMddHHmm}");
                if (sp.Status == SelectionPlanStatus.Approved)
                    Add(await UsersAsync(u => u.Role == AppRoles.Buyer), NotificationKind.Action, $"KHLCNT {sp.Number} đã duyệt – lập hồ sơ lựa chọn NCC cho các gói", sp.Title, e, $"act:KH:{sp.Id}:approved");
                Owner(sp.CreatedById, e, $"{sp.Number}: {e.Action}", sp.Status == SelectionPlanStatus.Returned ? NotificationKind.Warning : NotificationKind.Info);
                break;
            }
            case DocType.Plan:
            {
                var pl = await _db.ProcurementPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == e.Id);
                if (pl == null) return;
                if (pl.Status == PlanStatus.Submitted)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Kế hoạch mua sắm {pl.Year} chờ duyệt: {pl.Department}", null, e, $"act:PL:{pl.Id}:{pl.SubmittedAt:yyyyMMddHHmm}");
                if (pl.Status == PlanStatus.Approved)
                    Add(await UsersAsync(u => u.Role == AppRoles.Buyer), NotificationKind.Info, $"Kế hoạch mua sắm {pl.Year} của {pl.Department} đã duyệt – có thể gom nhu cầu", null, e, $"info:PL:{pl.Id}:approved");
                Owner(pl.CreatedById, e, $"Kế hoạch {pl.Year} – {pl.Department}: {e.Action}");
                break;
            }
            case DocType.Invoice:
            {
                var inv = await _db.Invoices.AsNoTracking().Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == e.Id);
                if (inv == null) return;
                if (inv.Status == InvoiceStatus.Exception)
                    Add(await DirectorsAsync(), NotificationKind.Warning, $"Hóa đơn lệch đối chiếu 3 bên: {inv.Vendor?.Name} {inv.InvoiceNo}", inv.MatchNote, e, $"act:INV:{inv.Id}:exception");
                if (inv.Status == InvoiceStatus.Matched)
                    Add(await UsersAsync(u => u.Role == AppRoles.Accountant), NotificationKind.Action, $"Hóa đơn {inv.Number} đã khớp – lập đề nghị thanh toán", $"{inv.Vendor?.Name} · {inv.Total.ToMoney()} đ", e, $"act:INV:{inv.Id}:matched");
                if (inv.CreatedById.HasValue) Owner(inv.CreatedById.Value, e, $"{inv.Number}: {e.Action}");
                break;
            }
            case DocType.Payment:
            {
                var p = await _db.Payments.AsNoTracking().Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == e.Id);
                if (p == null) return;
                if (p.Status == PaymentStatus.Requested)
                    Add(await DirectorsAsync(), NotificationKind.Action, $"Đề nghị thanh toán chờ duyệt: {p.Number}", $"{p.Vendor?.Name} · thực chi {p.NetAmount.ToMoney()} đ", e, $"act:TT:{p.Id}:requested");
                if (p.Status == PaymentStatus.Approved)
                    Add(await UsersAsync(u => u.Role == AppRoles.Accountant), NotificationKind.Action, $"Đã duyệt {p.Number} – tiến hành chi", $"{p.Vendor?.Name} · {p.NetAmount.ToMoney()} đ", e, $"act:TT:{p.Id}:approved");
                Owner(p.RequestedById, e, $"{p.Number}: {e.Action}");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ nhắc hạn định kỳ
    public async Task<int> RunRemindersAsync()
    {
        await TemplatesAsync();
        var a = await AlertsAsync();
        var today = DateTime.Today; var now = DateTime.Now;
        var created = 0;
        void R(IEnumerable<int> users, NotificationKind k, string title, string? msg, DocType t, int id, string key)
            => created += Add(users, k, title, msg, null, key, UrlOf(t, id), t, id);

        foreach (var r in await _db.Rfqs.AsNoTracking().Include(x => x.Vendors).Where(x => x.Status == RfqStatus.Open).ToListAsync())
        {
            if (a.On("rfq-deadline") && r.Deadline.Date >= today && r.Deadline.Date <= today.AddDays(a.Val("rfq-deadline")))
                R(new[] { r.BuyerId }, NotificationKind.Reminder, $"{r.Number} hết hạn nộp báo giá {r.Deadline.ToDate()}",
                    $"{r.Vendors.Count(v => v.Responded)}/{r.Vendors.Count} NCC đã gửi báo giá.", DocType.RFQ, r.Id, $"rem:RFQ:{r.Id}:deadline");
            if (a.On("rfq-open") && RfqEvaluation.CanOpen(r))
                R(new[] { r.BuyerId }, NotificationKind.Action, $"{r.Number} đã đủ điều kiện mở báo giá", r.Title, DocType.RFQ, r.Id, $"rem:RFQ:{r.Id}:open");
        }
        foreach (var t in await _db.Tenders.AsNoTracking().Include(x => x.Committee).Where(x => x.Status == TenderStatus.Published).ToListAsync())
        {
            if (a.On("tender-closing") && t.ClosingAt > now && t.ClosingAt <= now.AddDays(Math.Max(1, a.Val("tender-closing"))))
                R(t.Committee.Select(m => m.UserId).Append(t.BuyerId), NotificationKind.Reminder, $"Gói thầu {t.Number} đóng thầu lúc {t.ClosingAt.ToDateTime()}", t.Title,
                    DocType.Tender, t.Id, $"rem:TD:{t.Id}:closing");
            if (a.On("tender-open") && t.ClosingAt <= now)
                R(new[] { t.BuyerId }, NotificationKind.Action, $"Gói thầu {t.Number} đã đến giờ đóng thầu – tiến hành mở thầu", t.Title, DocType.Tender, t.Id, $"rem:TD:{t.Id}:open");
        }
        if (a.On("po-late"))
        {
            var lateBefore = today.AddDays(-a.Val("po-late"));
            foreach (var po in await _db.PurchaseOrders.AsNoTracking().Include(x => x.Vendor)
                         .Where(x => (x.Status == PoStatus.Issued || x.Status == PoStatus.PartiallyReceived) && x.DeliveryDate < lateBefore).ToListAsync())
                R(new[] { po.BuyerId }, NotificationKind.Warning, $"{po.Number} quá hạn giao hàng ({po.DeliveryDate.ToDate()})", po.Vendor?.Name, DocType.PO, po.Id, $"rem:PO:{po.Id}:late");
        }

        var directors = await DirectorsAsync();

        // Gói mua sắm sắp đến hạn / quá hạn giai đoạn hiện tại (theo hạn mức & thời gian gói)
        if (_store != null && (a.On("package-due-soon") || a.On("package-overdue")))
        {
            var progress = await new PackageProgressService(_db, _store).ComputeAsync(take: 1000);
            var memberMap = (await _db.PackageMembers.AsNoTracking().Where(m => m.Role != PackageRole.Viewer).Select(m => new { m.PackageId, m.UserId }).ToListAsync())
                .GroupBy(m => m.PackageId).ToDictionary(g => g.Key, g => g.Select(x => x.UserId).ToList());
            var week = System.Globalization.ISOWeek.GetYear(today) * 100 + System.Globalization.ISOWeek.GetWeekOfYear(today);
            foreach (var (p, pr) in progress)
            {
                var cur = pr.Current;
                if (cur == null || cur.Due == null) continue;
                var owners = new List<int> { p.BuyerId }; if (memberMap.TryGetValue(p.Id, out var ms)) owners.AddRange(ms);
                var title = $"Gói {p.Number} – giai đoạn \"{cur.Name}\"";
                if (a.On("package-overdue") && cur.DaysLate > a.Val("package-overdue"))
                    R(owners.Concat(directors).Distinct(), NotificationKind.Warning, $"{title} quá hạn {cur.DaysLate} ngày (hạn {cur.Due.ToDate()})",
                        $"{p.Title} · hạn mức {pr.Tier.Name}: {cur.PlannedDays} ngày cho giai đoạn này", DocType.Package, p.Id, $"rem:PK:{p.Id}:s{cur.Index}:overdue:{week}");
                else if (a.On("package-due-soon") && !cur.Late && cur.Due.Value.Date >= today && cur.Due.Value.Date <= today.AddDays(a.Val("package-due-soon")))
                    R(owners.Distinct(), NotificationKind.Reminder, $"{title} đến hạn {cur.Due.ToDate()}",
                        $"{p.Title} · hạn mức {pr.Tier.Name}: {cur.PlannedDays} ngày cho giai đoạn này", DocType.Package, p.Id, $"rem:PK:{p.Id}:s{cur.Index}:soon");
            }
        }
        if (a.On("contract-expiry"))
        {
            var soon = today.AddDays(a.Val("contract-expiry"));
            foreach (var c in await _db.Contracts.AsNoTracking().Include(x => x.Vendor).Where(x => x.Status == ContractStatus.Active).ToListAsync())
            {
                if (c.EndDate <= soon)
                    R(directors.Append(c.CreatedById), NotificationKind.Warning,
                        c.EndDate < today ? $"Hợp đồng {c.Number} đã hết hạn {c.EndDate.ToDate()}" : $"Hợp đồng {c.Number} hết hạn {c.EndDate.ToDate()}",
                        $"{c.Title} – {c.Vendor?.Name}", DocType.Contract, c.Id, $"rem:HD:{c.Id}:end:{(c.EndDate < today ? "past" : "soon")}");
                if (c.PerformanceSecurityExpiry is DateTime ps && ps <= soon)
                    R(new[] { c.CreatedById }, NotificationKind.Warning, $"Bảo lãnh thực hiện HĐ {c.Number} hết hiệu lực {ps.ToDate()}",
                        c.PerformanceSecurityRef, DocType.Contract, c.Id, $"rem:HD:{c.Id}:security:{(ps < today ? "past" : "soon")}");
            }
        }

        // Hóa đơn đến hạn / quá hạn thanh toán
        if (a.On("invoice-due"))
        {
            var accountants = await UsersAsync(u => u.Role == AppRoles.Accountant);
            var due = today.AddDays(a.Val("invoice-due"));
            foreach (var inv in await _db.Invoices.AsNoTracking().Include(x => x.Vendor)
                         .Where(x => x.Status != InvoiceStatus.Paid && x.Status != InvoiceStatus.Rejected && x.DueDate <= due).ToListAsync())
                created += Add(accountants, inv.DueDate < today ? NotificationKind.Warning : NotificationKind.Reminder,
                    (inv.DueDate < today ? "Quá hạn thanh toán: " : "Sắp đến hạn thanh toán: ") + $"{inv.Vendor?.Name} – HĐ {inv.InvoiceNo} ({inv.DueDate.ToDate()})",
                    $"{inv.Total.ToMoney()} đ · {inv.Status.GetDisplayName()}", null, $"rem:INV:{inv.Id}:{(inv.DueDate < today ? "overdue" : "due")}", UrlOf(DocType.Invoice, inv.Id), DocType.Invoice, inv.Id);
        }
        // Bảo lãnh tạm ứng / bảo hành sắp hết hiệu lực
        if (a.On("guarantee-expiry"))
        {
            var soon = today.AddDays(a.Val("guarantee-expiry"));
            foreach (var g in await _db.ContractGuarantees.AsNoTracking().Include(x => x.Contract)
                         .Where(x => x.ReleasedAt == null && x.ExpiryDate <= soon).ToListAsync())
                created += Add(new[] { g.Contract!.CreatedById }, NotificationKind.Warning,
                    $"{g.Type.GetDisplayName()} HĐ {g.Contract.Number} {(g.ExpiryDate < today ? "đã hết" : "sắp hết")} hiệu lực {g.ExpiryDate.ToDate()}",
                    $"{g.Reference} – {g.Amount.ToMoney()} đ", null, $"rem:BL:{g.Id}:{(g.ExpiryDate < today ? "past" : "soon")}", UrlOf(DocType.Contract, g.ContractId), DocType.Contract, g.ContractId);
        }

        // Hồ sơ pháp lý NCC sắp / đã hết hiệu lực (đang hợp tác, hồ sơ đã xác minh)
        if (a.On("vendor-doc"))
        {
            var soon = today.AddDays(a.Val("vendor-doc"));
            var buyers = await UsersAsync(u => u.Role == AppRoles.Buyer);
            foreach (var d in await _db.VendorDocuments.AsNoTracking().Include(x => x.Vendor)
                         .Where(x => x.ExpiryDate != null && x.ExpiryDate <= soon && x.Status == VendorDocumentStatus.Verified && x.Vendor!.Status == VendorStatus.Approved).ToListAsync())
                created += Add(buyers, NotificationKind.Warning,
                    (d.ExpiryDate < today ? "Hồ sơ NCC đã hết hiệu lực: " : "Hồ sơ NCC sắp hết hiệu lực: ") + $"{d.Vendor!.Name} – {d.Type.GetDisplayName()} ({d.ExpiryDate.ToDate()})",
                    "Đề nghị NCC cập nhật hồ sơ trên cổng nhà cung cấp.", null, $"rem:VD:{d.Id}:{(d.ExpiryDate < today ? "past" : "soon")}", $"/Vendors/Details/{d.VendorId}");
        }

        // Ngân sách sắp hết / đã vượt
        if (a.On("budget"))
        {
            var warn = a.Val("budget");
            foreach (var b in await new BudgetService(_db).StatusAsync(today.Year))
            {
                var level = b.UsedPercent > 100 ? 100 : b.UsedPercent >= warn ? warn : 0;
                if (level == 0) continue;
                var heads = await UsersAsync(u => u.Role == AppRoles.Approver && u.Department == b.Budget.Department);
                created += Add(directors.Concat(heads), NotificationKind.Warning,
                    level == 100 ? $"Vượt ngân sách: {b.Pool}" : $"Ngân sách đã dùng {b.UsedPercent}%: {b.Pool}",
                    $"Ngân sách {b.Budget.Amount.ToMoney()} đ, đã dùng {b.Used.ToMoney()} đ, còn {b.Available.ToMoney()} đ.", null,
                    $"rem:BG:{b.Budget.Id}:{b.Budget.Amount}:{level}", $"/Budgets?year={b.Budget.Year}");
            }
        }

        // Chờ duyệt quá lâu – nhắc lại mỗi tuần
        if (a.On("stale-approval"))
        {
            var stale = now.AddDays(-a.Val("stale-approval"));
            var week = System.Globalization.ISOWeek.GetWeekOfYear(today);
            foreach (var pr in await _db.PurchaseRequests.AsNoTracking().Where(p => (p.Status == PrStatus.Submitted || p.Status == PrStatus.PendingDirector) && p.SubmittedAt < stale).ToListAsync())
            {
                var to = pr.Status == PrStatus.Submitted
                    ? await UsersAsync(u => u.Role == AppRoles.Approver && u.Department == pr.Department && u.Id != pr.RequesterId) : directors;
                R(to, NotificationKind.Reminder, $"Nhắc: {pr.Number} chờ duyệt từ {pr.SubmittedAt.ToDate()}", pr.Title, DocType.PR, pr.Id, $"rem:PR:{pr.Id}:stale:{week}");
            }
            foreach (var po in await _db.PurchaseOrders.AsNoTracking().Where(p => p.Status == PoStatus.PendingApproval && p.CreatedAt < stale).ToListAsync())
                R(directors, NotificationKind.Reminder, $"Nhắc: {po.Number} chờ duyệt", null, DocType.PO, po.Id, $"rem:PO:{po.Id}:stale:{week}");
        }

        await _db.SaveChangesAsync();
        return created;
    }

    // ------------------------------------------------------------------ email
    public async Task<int> SendEmailsAsync(CancellationToken ct = default)
    {
        var e = await EmailConfigAsync();
        if (!e.Enabled || string.IsNullOrWhiteSpace(e.Host)) return 0;
        var tpl = await TemplatesAsync();
        var pending = await _db.Notifications.Include(n => n.User)
            .Where(n => n.EmailedAt == null && n.ReadAt == null && n.CreatedAt > DateTime.Now.AddDays(-3)
                        && n.User!.IsActive && n.User.EmailNotifications && n.User.Email != null && n.User.Email != "")
            .OrderBy(n => n.CreatedAt).Take(100).ToListAsync(ct);
        if (pending.Count == 0) return 0;
        using var smtp = Smtp(e);
        var company = await CompanyAsync();
        var sent = 0;
        foreach (var n in pending)
        {
            n.EmailedAt = DateTime.Now; // loại đã tắt email: coi như đã xử lý, không gửi
            if (!tpl.Channel(n.DocumentType).Email) continue;
            var link = string.IsNullOrEmpty(n.Url) ? e.BaseUrl : e.BaseUrl.TrimEnd('/') + "/Notifications/Open/" + n.Id;
            var v = Values(n.Title, n.Message, n.User!.FullName, link, n.CreatedAt, company);
            using var msg = new MailMessage(new MailAddress(e.From, e.FromName), new MailAddress(n.User.Email!))
                { Subject = NotifyTemplates.Render(tpl.Subject, v, false), Body = NotifyTemplates.Render(tpl.Body, v, true), IsBodyHtml = true };
            await smtp.SendMailAsync(msg, ct);
            sent++;
        }
        await _db.SaveChangesAsync(ct);
        return sent;
    }

    // ------------------------------------------------------------------ helpers
    private Task<List<int>> DirectorsAsync() => UsersAsync(u => u.Role == AppRoles.Director);

    /// <summary>Người dùng thỏa điều kiện + người đang nhận ủy quyền duyệt của họ</summary>
    private async Task<List<int>> UsersAsync(System.Linq.Expressions.Expression<Func<AppUser, bool>> where)
    {
        var ids = await _db.Users.AsNoTracking().Where(u => u.IsActive).Where(where).Select(u => u.Id).ToListAsync();
        if (ids.Count == 0) return ids;
        var today = DateTime.Today;
        var delegates = await _db.ApprovalDelegations.AsNoTracking()
            .Where(d => ids.Contains(d.FromUserId) && d.RevokedAt == null && d.StartDate <= today && d.EndDate >= today && d.ToUser!.IsActive)
            .Select(d => d.ToUserId).ToListAsync();
        return ids.Concat(delegates).Distinct().ToList();
    }

    /// <summary>Báo người lập / người đề nghị khi người khác thao tác (mỗi dòng nhật ký một lần)</summary>
    private void Owner(int ownerId, DocEvent e, string title, NotificationKind kind = NotificationKind.Info)
    {
        if (ownerId == e.ActorId) return;
        var k = e.Action.Contains("từ chối", StringComparison.OrdinalIgnoreCase) || e.Action.Contains("trả lại", StringComparison.OrdinalIgnoreCase)
                || e.Action.Contains("Hủy", StringComparison.Ordinal) ? NotificationKind.Warning : kind;
        Add(new[] { ownerId }, k, Cut(title, 200), e.Comment, e, null);
    }

    private readonly HashSet<(int, string)> _pendingKeys = new();

    /// <summary>Thêm thông báo cho danh sách người dùng (bỏ người thao tác, bỏ trùng khóa). Trả về số thông báo tạo mới.</summary>
    private int Add(IEnumerable<int> users, NotificationKind kind, string title, string? msg, DocEvent? e, string? key,
        string? url = null, DocType? type = null, int? id = null)
    {
        var n = 0;
        if (_tpl != null && !_tpl.Channel(type ?? e?.Type).InApp) return 0; // loại thông báo đã tắt
        foreach (var uid in users.Distinct())
        {
            if (e != null && uid == e.ActorId) continue;
            if (key != null && (!_pendingKeys.Add((uid, key)) || _db.Notifications.Any(x => x.UserId == uid && x.Key == key))) continue;
            _db.Notifications.Add(new Notification
            {
                UserId = uid, Kind = kind, Title = Cut(title, 200), Message = msg == null ? null : Cut(msg, 1000), Key = key,
                Url = url ?? (e != null ? UrlOf(e.Type, e.Id) : null), DocumentType = type ?? e?.Type, DocumentId = id ?? e?.Id, CreatedAt = DateTime.Now
            });
            n++;
        }
        return n;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>Chạy nền: nhắc hạn + gửi email theo chu kỳ</summary>
public class NotificationWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<NotificationWorker> _log;
    private readonly NotificationSettings _s;
    public NotificationWorker(IServiceProvider sp, ILogger<NotificationWorker> log, IOptions<NotificationSettings> s) { _sp = sp; _log = log; _s = s.Value; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stop); // chờ khởi tạo database
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<NotificationService>();
                var n = await svc.RunRemindersAsync();
                var m = await svc.SendEmailsAsync(stop);
                if (n + m > 0) _log.LogInformation("Thông báo: {Reminders} nhắc hạn mới, {Emails} email đã gửi", n, m);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Quét thông báo lỗi"); }
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _s.IntervalMinutes)), stop);
        }
    }
}
