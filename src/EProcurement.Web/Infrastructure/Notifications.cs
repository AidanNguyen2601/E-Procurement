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
    public NotificationService(EProcDbContext db, IOptions<NotificationSettings> s, IOptions<ProcurementSettings> p) { _db = db; _s = s.Value; _p = p.Value; }

    public static string UrlOf(DocType t, int id) => t switch
    {
        DocType.PR => $"/PurchaseRequests/Details/{id}",
        DocType.PO => $"/PurchaseOrders/Details/{id}",
        DocType.RFQ => $"/Rfqs/Details/{id}",
        DocType.DirectAward => $"/DirectAwards/Details/{id}",
        DocType.Tender => $"/Tenders/Details/{id}",
        DocType.Contract => $"/Contracts/Details/{id}",
        _ => "/"
    };

    // ------------------------------------------------------------------ sự kiện nghiệp vụ
    public async Task OnEventsAsync(IEnumerable<DocEvent> events)
    {
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
                var t = await _db.Tenders.AsNoTracking().Include(x => x.Committee).FirstOrDefaultAsync(x => x.Id == e.Id);
                if (t == null) return;
                var members = t.Committee.Select(m => m.UserId).ToList();
                if (t.Status is TenderStatus.PendingPlanApproval or TenderStatus.PendingResultApproval)
                    Add(await DirectorsAsync(), NotificationKind.Action,
                        (t.Status == TenderStatus.PendingPlanApproval ? "Kế hoạch & HSMT chờ duyệt: " : "Kết quả lựa chọn nhà thầu chờ duyệt: ") + t.Number, t.Title, e, $"act:TD:{t.Id}:{t.Status}");
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
                if (c != null) Owner(c.CreatedById, e, $"{c.Number}: {e.Action}");
                break;
            }
        }
    }

    // ------------------------------------------------------------------ nhắc hạn định kỳ
    public async Task<int> RunRemindersAsync()
    {
        var today = DateTime.Today; var now = DateTime.Now;
        var created = 0;
        void R(IEnumerable<int> users, NotificationKind k, string title, string? msg, DocType t, int id, string key)
            => created += Add(users, k, title, msg, null, key, UrlOf(t, id), t, id);

        foreach (var r in await _db.Rfqs.AsNoTracking().Include(x => x.Vendors).Where(x => x.Status == RfqStatus.Open).ToListAsync())
        {
            if (r.Deadline.Date == today.AddDays(1) || r.Deadline.Date == today)
                R(new[] { r.BuyerId }, NotificationKind.Reminder, $"{r.Number} hết hạn nộp báo giá {r.Deadline.ToDate()}",
                    $"{r.Vendors.Count(v => v.Responded)}/{r.Vendors.Count} NCC đã gửi báo giá.", DocType.RFQ, r.Id, $"rem:RFQ:{r.Id}:deadline");
            if (RfqEvaluation.CanOpen(r))
                R(new[] { r.BuyerId }, NotificationKind.Action, $"{r.Number} đã đủ điều kiện mở báo giá", r.Title, DocType.RFQ, r.Id, $"rem:RFQ:{r.Id}:open");
        }
        foreach (var t in await _db.Tenders.AsNoTracking().Include(x => x.Committee).Where(x => x.Status == TenderStatus.Published).ToListAsync())
        {
            if (t.ClosingAt > now && t.ClosingAt <= now.AddDays(2))
                R(t.Committee.Select(m => m.UserId).Append(t.BuyerId), NotificationKind.Reminder, $"Gói thầu {t.Number} đóng thầu lúc {t.ClosingAt.ToDateTime()}", t.Title,
                    DocType.Tender, t.Id, $"rem:TD:{t.Id}:closing");
            if (t.ClosingAt <= now)
                R(new[] { t.BuyerId }, NotificationKind.Action, $"Gói thầu {t.Number} đã đến giờ đóng thầu – tiến hành mở thầu", t.Title, DocType.Tender, t.Id, $"rem:TD:{t.Id}:open");
        }
        foreach (var po in await _db.PurchaseOrders.AsNoTracking().Include(x => x.Vendor)
                     .Where(x => (x.Status == PoStatus.Issued || x.Status == PoStatus.PartiallyReceived) && x.DeliveryDate < today).ToListAsync())
            R(new[] { po.BuyerId }, NotificationKind.Warning, $"{po.Number} quá hạn giao hàng ({po.DeliveryDate.ToDate()})", po.Vendor?.Name, DocType.PO, po.Id, $"rem:PO:{po.Id}:late");

        var soon = today.AddDays(_s.ExpiryWarningDays);
        var directors = await DirectorsAsync();
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

        // Ngân sách sắp hết / đã vượt
        foreach (var b in await new BudgetService(_db).StatusAsync(today.Year))
        {
            var level = b.UsedPercent > 100 ? 100 : b.UsedPercent >= _p.BudgetWarnPercent ? _p.BudgetWarnPercent : 0;
            if (level == 0) continue;
            var heads = await UsersAsync(u => u.Role == AppRoles.Approver && u.Department == b.Budget.Department);
            created += Add(directors.Concat(heads), NotificationKind.Warning,
                level == 100 ? $"Vượt ngân sách: {b.Pool}" : $"Ngân sách đã dùng {b.UsedPercent}%: {b.Pool}",
                $"Ngân sách {b.Budget.Amount.ToMoney()} đ, đã dùng {b.Used.ToMoney()} đ, còn {b.Available.ToMoney()} đ.", null,
                $"rem:BG:{b.Budget.Id}:{b.Budget.Amount}:{level}", $"/Budgets?year={b.Budget.Year}");
        }

        // Chờ duyệt quá lâu – nhắc lại mỗi tuần
        var stale = now.AddDays(-_s.StaleApprovalDays);
        var week = System.Globalization.ISOWeek.GetWeekOfYear(today);
        foreach (var pr in await _db.PurchaseRequests.AsNoTracking().Where(p => (p.Status == PrStatus.Submitted || p.Status == PrStatus.PendingDirector) && p.SubmittedAt < stale).ToListAsync())
        {
            var to = pr.Status == PrStatus.Submitted
                ? await UsersAsync(u => u.Role == AppRoles.Approver && u.Department == pr.Department && u.Id != pr.RequesterId) : directors;
            R(to, NotificationKind.Reminder, $"Nhắc: {pr.Number} chờ duyệt từ {pr.SubmittedAt.ToDate()}", pr.Title, DocType.PR, pr.Id, $"rem:PR:{pr.Id}:stale:{week}");
        }
        foreach (var po in await _db.PurchaseOrders.AsNoTracking().Where(p => p.Status == PoStatus.PendingApproval && p.CreatedAt < stale).ToListAsync())
            R(directors, NotificationKind.Reminder, $"Nhắc: {po.Number} chờ duyệt", null, DocType.PO, po.Id, $"rem:PO:{po.Id}:stale:{week}");

        await _db.SaveChangesAsync();
        return created;
    }

    // ------------------------------------------------------------------ email
    public async Task<int> SendEmailsAsync(CancellationToken ct = default)
    {
        var e = _s.Email;
        if (!e.Enabled || string.IsNullOrWhiteSpace(e.Host)) return 0;
        var pending = await _db.Notifications.Include(n => n.User)
            .Where(n => n.EmailedAt == null && n.ReadAt == null && n.CreatedAt > DateTime.Now.AddDays(-3)
                        && n.User!.IsActive && n.User.EmailNotifications && n.User.Email != null && n.User.Email != "")
            .OrderBy(n => n.CreatedAt).Take(100).ToListAsync(ct);
        if (pending.Count == 0) return 0;
        using var smtp = new SmtpClient(e.Host, e.Port) { EnableSsl = e.EnableSsl };
        if (!string.IsNullOrEmpty(e.UserName)) smtp.Credentials = new NetworkCredential(e.UserName, e.Password);
        var sent = 0;
        foreach (var n in pending)
        {
            var link = string.IsNullOrEmpty(n.Url) ? _s.BaseUrl : _s.BaseUrl.TrimEnd('/') + "/Notifications/Open/" + n.Id;
            var body = $"<p>Chào {WebUtility.HtmlEncode(n.User!.FullName)},</p><p><b>{WebUtility.HtmlEncode(n.Title)}</b></p>" +
                       (string.IsNullOrEmpty(n.Message) ? "" : $"<p>{WebUtility.HtmlEncode(n.Message)}</p>") +
                       $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">Mở trên E-Procurement</a></p><p style=\"color:#888;font-size:12px\">Email tự động – tắt nhận email trong trang Thông báo.</p>";
            using var msg = new MailMessage(new MailAddress(e.From, e.FromName), new MailAddress(n.User.Email!))
                { Subject = "[E-Procurement] " + n.Title, Body = body, IsBodyHtml = true };
            await smtp.SendMailAsync(msg, ct);
            n.EmailedAt = DateTime.Now; sent++;
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
