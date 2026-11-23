using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Danh sách theo dõi báo giá (đọc từ hồ sơ có sẵn):
/// báo giá nhà cung cấp (chào giá chỉ định), báo giá chào hàng cạnh tranh, đánh giá hồ sơ chào giá.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class QuotesController : ProcControllerBase
{
    public QuotesController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public record DirectQuoteRow(DirectAward Award, VendorSubmission? Latest, string State);

    /// <summary>Mua sắm trực tiếp / chỉ định: báo giá của NCC được chỉ định (qua cổng hoặc nhập tay)</summary>
    public async Task<IActionResult> Direct(string? state)
    {
        var das = await Db.DirectAwards.AsNoTracking().Include(d => d.Vendor).Include(d => d.Package)
            .Where(d => d.Status != DirectAwardStatus.Cancelled).OrderByDescending(d => d.QuoteRequestedAt ?? d.CreatedAt).Take(300).ToListAsync();
        var ids = das.Select(d => d.Id).ToList();
        var subs = await Db.VendorSubmissions.AsNoTracking().Where(s => s.DirectAwardId != null && ids.Contains(s.DirectAwardId.Value) && s.IsLatest)
            .Select(s => new VendorSubmission { Id = s.Id, DirectAwardId = s.DirectAwardId, Revision = s.Revision, SubmittedAt = s.SubmittedAt, Sha256 = s.Sha256, IsWithdrawn = s.IsWithdrawn, UnsealedAt = s.UnsealedAt })
            .ToListAsync();
        var rows = das.Select(d =>
        {
            var s = subs.FirstOrDefault(x => x.DirectAwardId == d.Id && !x.IsWithdrawn);
            var st = d.QuoteOpenedAt.HasValue ? "opened"
                : s != null ? "sealed"
                : d.QuoteRequestedAt.HasValue ? (d.QuoteDeadline < DateTime.Now ? "late" : "waiting")
                : d.ProposedTotal > 0 ? "manual" : "none";
            return new DirectQuoteRow(d, s, st);
        }).ToList();
        ViewBag.Counts = rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        if (!string.IsNullOrEmpty(state)) rows = rows.Where(r => r.State == state).ToList();
        ViewBag.State = state;
        return View(rows);
    }

    /// <summary>Chào giá cạnh tranh: mọi báo giá của NCC được mời</summary>
    public async Task<IActionResult> Competitive(string? q, bool pending = false)
    {
        var query = Db.RfqVendors.AsNoTracking().Include(v => v.Vendor).Include(v => v.Rfq).ThenInclude(r => r!.AwardedVendor)
            .Where(v => v.Rfq!.Status != RfqStatus.Draft);
        if (pending) query = query.Where(v => v.Rfq!.Status == RfqStatus.Open);
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(v => v.Rfq!.Number.Contains(k) || v.Rfq.Title.Contains(k) || v.Vendor!.Name.Contains(k)); }
        var list = await query.OrderByDescending(v => v.Rfq!.IssueDate).ThenBy(v => v.Rfq!.Number).Take(500).ToListAsync();
        var rvIds = list.Select(v => v.Id).ToList();
        ViewBag.Online = await Db.VendorSubmissions.Where(s => s.RfqVendorId != null && rvIds.Contains(s.RfqVendorId.Value) && s.IsLatest && !s.IsWithdrawn)
            .Select(s => s.RfqVendorId!.Value).Distinct().ToListAsync();
        ViewBag.Q = q; ViewBag.Pending = pending;
        return View(list);
    }

    /// <summary>Đánh giá hồ sơ chào giá: RFQ đã hết hạn / đã mở / chờ duyệt / đã chọn</summary>
    public async Task<IActionResult> Evaluations()
    {
        var list = await Db.Rfqs.AsNoTracking().Include(r => r.Vendors).Include(r => r.ProposedVendor).Include(r => r.AwardedVendor).Include(r => r.Package)
            .Where(r => r.Status == RfqStatus.Opened || r.Status == RfqStatus.PendingApproval || r.Status == RfqStatus.Awarded
                        || (r.Status == RfqStatus.Open && r.Deadline < DateTime.Today))
            .OrderBy(r => r.Status == RfqStatus.Awarded).ThenByDescending(r => r.OpenedAt ?? r.Deadline).Take(200).ToListAsync();
        return View(list);
    }
}
