using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Cổng NCC – Quản lý chào giá: lời mời chào giá cạnh tranh + hồ sơ chào giá chỉ định gửi quý công ty</summary>
public class PortalQuotesController : PortalControllerBase
{
    public PortalQuotesController(EProcDbContext db) : base(db) { }

    public record QuoteRow(string Kind, string Number, string Title, DateTime? Deadline, string StatusKey, string Status, string Css, string Url,
        int Lines, DateTime? SubmittedAt, string? Requirements, string? Buyer);

    public static readonly (string Key, string Text)[] StatusOptions =
    {
        ("todo", "Chưa nộp"), ("submitted", "Đã nộp"), ("expired", "Hết hạn"), ("evaluating", "Đang đánh giá / thương thảo"),
        ("won", "Được chọn"), ("lost", "Không được chọn"), ("cancelled", "Đã hủy")
    };

    public async Task<IActionResult> Index(string? q, string? status, string? kind)
    {
        var v = MyVendor.Id; var now = DateTime.Now;
        var rows = new List<QuoteRow>();

        if (kind != "direct")
        {
            var rfqs = await Db.RfqVendors.AsNoTracking().Include(x => x.Rfq).ThenInclude(r => r!.Lines).Include(x => x.Rfq).ThenInclude(r => r!.Buyer)
                .Where(x => x.VendorId == v && x.Rfq!.Status != RfqStatus.Draft).OrderByDescending(x => x.Rfq!.IssueDate).Take(300).ToListAsync();
            foreach (var x in rfqs)
            {
                var r = x.Rfq!; var close = PortalRfqsController.CloseAt(r);
                var (key, text, css) = r.Status switch
                {
                    RfqStatus.Cancelled => ("cancelled", "Đã hủy", "vstatus-muted"),
                    RfqStatus.Awarded => r.AwardedVendorId == v ? ("won", "Được chọn", "vstatus-ok") : ("lost", "Không được chọn", "vstatus-muted"),
                    RfqStatus.Opened or RfqStatus.PendingApproval => ("evaluating", "Đang đánh giá", "vstatus-info"),
                    RfqStatus.Open when now < close => x.Responded ? ("submitted", "Đã nộp", "vstatus-info") : ("todo", "Chưa nộp", "vstatus-wait"),
                    _ => x.Responded ? ("evaluating", "Hết hạn – chờ mở báo giá", "vstatus-info") : ("expired", "Hết hạn – không nộp", "vstatus-bad")
                };
                rows.Add(new QuoteRow("Chào giá cạnh tranh", r.Number, r.Title, close, key, text, css, Url.Action("Details", "PortalRfqs", new { id = r.Id })!,
                    r.Lines.Count, x.RespondedAt, r.Requirements, r.Buyer?.FullName));
            }
        }
        if (kind != "competitive")
        {
            var das = await Db.DirectAwards.AsNoTracking().Include(d => d.Lines).Include(d => d.Buyer)
                .Where(d => d.VendorId == v && d.QuoteRequestedAt != null).OrderByDescending(d => d.QuoteRequestedAt).Take(300).ToListAsync();
            var ids = das.Select(d => d.Id).ToList();
            var subs = await Db.VendorSubmissions.AsNoTracking().Where(s => s.DirectAwardId != null && ids.Contains(s.DirectAwardId.Value) && s.IsLatest && !s.IsWithdrawn)
                .Select(s => new { Id = s.DirectAwardId!.Value, s.SubmittedAt }).ToListAsync();
            foreach (var d in das)
            {
                var sub = subs.Where(s => s.Id == d.Id && s.SubmittedAt >= d.QuoteRequestedAt).Select(s => (DateTime?)s.SubmittedAt).FirstOrDefault();
                var (key, text, css) = d.Status switch
                {
                    DirectAwardStatus.Cancelled => ("cancelled", "Đã hủy", "vstatus-muted"),
                    DirectAwardStatus.Approved => ("won", "Được chọn – chờ đơn hàng", "vstatus-ok"),
                    DirectAwardStatus.PendingApproval => ("evaluating", "Đang trình duyệt", "vstatus-info"),
                    _ when d.QuoteOpenedAt != null => ("evaluating", "Đang thương thảo", "vstatus-info"),
                    _ when d.QuoteDeadline > now => sub != null ? ("submitted", "Đã nộp", "vstatus-info") : ("todo", "Chưa nộp", "vstatus-wait"),
                    _ => sub != null ? ("evaluating", "Hết hạn – chờ mở báo giá", "vstatus-info") : ("expired", "Hết hạn – không nộp", "vstatus-bad")
                };
                rows.Add(new QuoteRow("Chào giá chỉ định", d.Number, d.Title, d.QuoteDeadline, key, text, css, Url.Action("Details", "PortalDirectQuotes", new { id = d.Id })!,
                    d.Lines.Count, sub, d.QuoteRequirements, d.Buyer?.FullName));
            }
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim();
            rows = rows.Where(r => r.Number.Contains(k, StringComparison.OrdinalIgnoreCase) || r.Title.Contains(k, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        ViewBag.Counts = rows.GroupBy(r => r.StatusKey).ToDictionary(g => g.Key, g => g.Count());
        if (!string.IsNullOrEmpty(status)) rows = rows.Where(r => r.StatusKey == status).ToList();
        // Việc cần làm lên đầu: chưa nộp (hạn gần nhất trước), rồi theo hạn mới nhất
        rows = rows.OrderBy(r => r.StatusKey == "todo" ? 0 : 1).ThenBy(r => r.StatusKey == "todo" ? r.Deadline : null).ThenByDescending(r => r.Deadline).ToList();
        ViewBag.Q = q; ViewBag.Status = status; ViewBag.Kind = kind;
        ViewData["Title"] = "Quản lý chào giá";
        return View(rows);
    }
}
