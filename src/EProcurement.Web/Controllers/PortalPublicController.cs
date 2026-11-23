using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Trang công khai của cổng NCC (không cần đăng nhập): hướng dẫn nộp thầu, thông báo mời thầu / mời sơ tuyển / kết quả.
/// Nếu NCC đã đăng nhập cổng thì hiển thị menu đầy đủ.
/// </summary>
[AllowAnonymous]
public class PortalPublicController : Controller
{
    private readonly EProcDbContext _db;
    public PortalPublicController(EProcDbContext db) => _db = db;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var auth = await HttpContext.AuthenticateAsync(PortalAuth.Scheme);
        if (auth.Succeeded && auth.Principal.VendorUserId() is int id and > 0)
            ViewBag.PortalUser = await _db.VendorUsers.AsNoTracking().Include(u => u.Vendor).FirstOrDefaultAsync(u => u.Id == id && u.IsActive);
        await next();
    }

    public IActionResult Guide()
    {
        ViewData["Title"] = "Hướng dẫn nộp thầu";
        ViewData["HeroSub"] = "Đăng ký nhà cung cấp · sơ tuyển · chào giá · dự thầu online · đơn hàng & hóa đơn";
        return View();
    }

    public record NoticeRow(string Kind, string Number, string Title, string? Category, DateTime? Published, DateTime? Deadline, string Status, string Css, string Url, string? Result);

    public async Task<IActionResult> Notices(string? tab, string? q)
    {
        tab = tab is "prequal" or "results" ? tab : "tenders";
        var loggedIn = ViewBag.PortalUser is VendorUser;
        var now = DateTime.Now;
        var k = q?.Trim();
        var rows = new List<NoticeRow>();
        if (tab == "tenders")
        {
            var list = await _db.Tenders.AsNoTracking().Where(t => t.Status == TenderStatus.Published || t.Status == TenderStatus.Opened || t.Status == TenderStatus.PendingResultApproval)
                .Where(t => k == null || t.Number.Contains(k) || t.Title.Contains(k)).OrderByDescending(t => t.Status == TenderStatus.Published && t.ClosingAt > now).ThenBy(t => t.ClosingAt).Take(100).ToListAsync();
            rows = list.Select(t => new NoticeRow("Đấu thầu rộng rãi", t.Number, t.Title, t.EnvelopeMethod.GetDisplayName(), t.PublishedAt, t.ClosingAt,
                t.Status == TenderStatus.Published && t.ClosingAt > now ? "Đang nhận HSDT" : "Đã đóng thầu – đang đánh giá",
                t.Status == TenderStatus.Published && t.ClosingAt > now ? "vstatus-ok" : "vstatus-info",
                loggedIn ? Url.Action("Details", "PortalTenders", new { id = t.Id })! : Url.Action("Notice", "Tenders", new { id = t.Id })!, null)).ToList();
        }
        else if (tab == "prequal")
        {
            var list = await _db.Prequalifications.AsNoTracking().Include(p => p.Category)
                .Where(p => p.Status == PrequalificationStatus.Open || p.Status == PrequalificationStatus.Evaluating || p.Status == PrequalificationStatus.PendingApproval)
                .Where(p => k == null || p.Number.Contains(k) || p.Title.Contains(k)).OrderBy(p => p.Deadline).Take(100).ToListAsync();
            rows = list.Select(p => new NoticeRow("Mời sơ tuyển", p.Number, p.Title, p.Category?.Name, p.PublishedAt, p.Deadline,
                p.Status == PrequalificationStatus.Open && p.Deadline > now ? "Đang nhận hồ sơ" : "Đang đánh giá",
                p.Status == PrequalificationStatus.Open && p.Deadline > now ? "vstatus-ok" : "vstatus-info",
                Url.Action("Details", "PortalPrequalifications", new { id = p.Id })!, null)).ToList();
        }
        else
        {
            var tenders = await _db.Tenders.AsNoTracking().Include(t => t.AwardedVendor)
                .Where(t => t.Status == TenderStatus.Awarded || t.Status == TenderStatus.Contracted)
                .Where(t => k == null || t.Number.Contains(k) || t.Title.Contains(k)).OrderByDescending(t => t.OpenedAt).Take(50).ToListAsync();
            rows.AddRange(tenders.Select(t => new NoticeRow("Kết quả đấu thầu", t.Number, t.Title, null, t.OpenedAt, t.ComplaintDeadline, t.Status.GetDisplayName(), "vstatus-muted",
                Url.Action("Notice", "Tenders", new { id = t.Id })!, $"{t.AwardedVendor?.Name} – {t.AwardPrice?.ToMoney()} đ")));
            var pqs = await _db.Prequalifications.AsNoTracking().Include(p => p.Category)
                .Where(p => p.Status == PrequalificationStatus.Completed).Where(p => k == null || p.Number.Contains(k) || p.Title.Contains(k))
                .OrderByDescending(p => p.DecidedAt).Take(50)
                .Select(p => new { p.Id, p.Number, p.Title, Cat = p.Category!.Name, p.DecidedAt, Passed = p.Applications.Count(a => a.Status == PrequalificationResult.Passed) }).ToListAsync();
            rows.AddRange(pqs.Select(p => new NoticeRow("Kết quả sơ tuyển", p.Number, p.Title, p.Cat, p.DecidedAt, null, "Đã có kết quả", "vstatus-muted",
                Url.Action("Details", "PortalPrequalifications", new { id = p.Id })!, $"{p.Passed} nhà cung cấp đạt")));
        }
        ViewBag.Tab = tab; ViewBag.Q = q;
        ViewData["Title"] = "Thông báo mời thầu";
        return View(rows);
    }
}
