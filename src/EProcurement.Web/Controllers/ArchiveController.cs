using System.IO.Compression;
using System.Net;
using System.Text;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Một dòng sổ lưu trữ: hồ sơ của một gói mua sắm (gói, PR, hồ sơ lựa chọn NCC, hợp đồng, đơn hàng, hóa đơn, thanh toán)</summary>
public class ArchiveRow
{
    public ProcurementPackage Package { get; set; } = null!;
    public int Documents { get; set; }
    public int Attachments { get; set; }
    public long Bytes { get; set; }
    /// <summary>Ngày đóng hồ sơ (gói hoàn tất / hủy và mọi hợp đồng đã thanh lý / chấm dứt); null = còn đang thực hiện</summary>
    public DateTime? ClosedAt { get; set; }
    public DateTime? KeepUntil { get; set; }
    public string? OpenReason { get; set; }
    public string State => ClosedAt == null ? "Đang thực hiện" : KeepUntil < DateTime.Today ? "Hết thời hạn lưu trữ" : KeepUntil <= DateTime.Today.AddYears(1) ? "Sắp hết thời hạn" : "Đang lưu trữ";
}

/// <summary>Lưu trữ hồ sơ: sổ theo dõi thời hạn lưu trữ theo gói và xuất trọn bộ hồ sơ (file đính kèm + nhật ký) thành tệp ZIP</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class ArchiveController : ProcControllerBase
{
    public ArchiveController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    /// <summary>Tập chứng từ liên quan của một gói (loại, id, số, tiêu đề, trạng thái)</summary>
    private record Doc(DocType Type, int Id, string Number, string Title, string Status);

    private async Task<List<Doc>> DocsAsync(int packageId)
    {
        var docs = new List<Doc>();
        var p = await Db.ProcurementPackages.AsNoTracking().FirstAsync(x => x.Id == packageId);
        docs.Add(new(DocType.Package, p.Id, p.Number, p.Title, p.Status.GetDisplayName()));
        docs.AddRange((await Db.PurchaseRequests.AsNoTracking().Where(x => x.PackageId == packageId).ToListAsync()).Select(x => new Doc(DocType.PR, x.Id, x.Number, x.Purpose ?? "", x.Status.GetDisplayName())));
        docs.AddRange((await Db.Rfqs.AsNoTracking().Where(x => x.PackageId == packageId).ToListAsync()).Select(x => new Doc(DocType.RFQ, x.Id, x.Number, x.Title, x.Status.GetDisplayName())));
        docs.AddRange((await Db.DirectAwards.AsNoTracking().Where(x => x.PackageId == packageId).ToListAsync()).Select(x => new Doc(DocType.DirectAward, x.Id, x.Number, x.Title, x.Status.GetDisplayName())));
        var tenders = await Db.Tenders.AsNoTracking().Where(x => x.PackageId == packageId).ToListAsync();
        docs.AddRange(tenders.Select(x => new Doc(DocType.Tender, x.Id, x.Number, x.Title, x.Status.GetDisplayName())));
        var pos = await Db.PurchaseOrders.AsNoTracking().Include(x => x.Vendor).Where(x => x.PackageId == packageId).ToListAsync();
        var tids = tenders.Select(t => (int?)t.Id).ToList(); var poContracts = pos.Where(o => o.ContractId != null).Select(o => o.ContractId).ToList();
        var contracts = await Db.Contracts.AsNoTracking().Where(c => tids.Contains(c.TenderId) || poContracts.Contains(c.Id)).ToListAsync();
        docs.AddRange(contracts.Select(x => new Doc(DocType.Contract, x.Id, x.Number, x.Title, x.Status.GetDisplayName())));
        docs.AddRange(pos.Select(x => new Doc(DocType.PO, x.Id, x.Number, x.Vendor?.Name ?? "", x.Status.GetDisplayName())));
        var poIds = pos.Select(o => o.Id).ToList(); var cIds = contracts.Select(c => (int?)c.Id).ToList();
        var invoices = await Db.Invoices.AsNoTracking().Where(i => poIds.Contains(i.PurchaseOrderId)).ToListAsync();
        docs.AddRange(invoices.Select(x => new Doc(DocType.Invoice, x.Id, x.Number, $"{x.Series} {x.InvoiceNo}", x.Status.GetDisplayName())));
        var invIds = invoices.Select(i => (int?)i.Id).ToList();
        docs.AddRange((await Db.Payments.AsNoTracking().Where(x => cIds.Contains(x.ContractId) || invIds.Contains(x.InvoiceId)).ToListAsync()).Select(x => new Doc(DocType.Payment, x.Id, x.Number, x.Type.GetDisplayName(), x.Status.GetDisplayName())));
        return docs.DistinctBy(d => (d.Type, d.Id)).ToList();
    }

    private async Task<ArchiveRow> RowAsync(ProcurementPackage p, int years)
    {
        var docs = await DocsAsync(p.Id);
        var row = new ArchiveRow { Package = p, Documents = docs.Count };
        foreach (var g in docs.GroupBy(d => d.Type))
        {
            var ids = g.Select(d => d.Id).ToList();
            var a = await Db.Attachments.AsNoTracking().Where(x => x.DocumentType == g.Key && ids.Contains(x.DocumentId)).Select(x => x.Size).ToListAsync();
            row.Attachments += a.Count; row.Bytes += a.Sum();
        }
        if (p.Status is not (PackageStatus.Completed or PackageStatus.Cancelled)) { row.OpenReason = I18n.T("Gói đang ở trạng thái {0}", p.Status.GetDisplayName()); return row; }
        var contractIds = docs.Where(d => d.Type == DocType.Contract).Select(d => d.Id).ToList();
        var contracts = await Db.Contracts.AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToListAsync();
        var open = contracts.FirstOrDefault(c => c.Status is ContractStatus.Active or ContractStatus.Completed);
        if (open != null) { row.OpenReason = I18n.T("Hợp đồng {0} chưa thanh lý", open.Number); return row; }
        var last = new List<DateTime?>
        {
            await Db.ApprovalHistories.Where(h => h.DocumentType == DocType.Package && h.DocumentId == p.Id).MaxAsync(h => (DateTime?)h.CreatedAt),
            await Db.ContractSettlements.Where(s => contractIds.Contains(s.ContractId)).MaxAsync(s => s.LiquidatedAt),
            await Db.ApprovalHistories.Where(h => h.DocumentType == DocType.Contract && contractIds.Contains(h.DocumentId)).MaxAsync(h => (DateTime?)h.CreatedAt)
        };
        row.ClosedAt = last.Where(x => x.HasValue).Max() ?? p.CreatedAt;
        row.KeepUntil = row.ClosedAt.Value.Date.AddYears(years);
        return row;
    }

    public async Task<IActionResult> Index(string? state, int? year)
    {
        var legal = await HttpContext.RequestServices.GetRequiredService<SettingsStore>().LegalAsync();
        var q = Db.ProcurementPackages.AsNoTracking().Where(p => p.Status != PackageStatus.Draft);
        if (year.HasValue) q = q.Where(p => p.CreatedAt.Year == year);
        var rows = new List<ArchiveRow>();
        foreach (var p in await q.OrderByDescending(p => p.CreatedAt).Take(500).ToListAsync()) rows.Add(await RowAsync(p, legal.ArchiveYears));
        ViewBag.Counts = rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        if (!string.IsNullOrEmpty(state)) rows = rows.Where(r => r.State == state).ToList();
        ViewBag.State = state; ViewBag.Year = year; ViewBag.Years = legal.ArchiveYears;
        ViewData["Title"] = "Lưu trữ hồ sơ";
        return View(rows);
    }

    /// <summary>Xuất trọn bộ hồ sơ của gói: mục lục (HTML), nhật ký từng chứng từ (CSV) và toàn bộ file đính kèm, chia thư mục theo chứng từ</summary>
    public async Task<IActionResult> Dossier(int id)
    {
        var p = await Db.ProcurementPackages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        var docs = await DocsAsync(id);
        var legal = await HttpContext.RequestServices.GetRequiredService<SettingsStore>().LegalAsync();
        var row = await RowAsync(p, legal.ArchiveYears);
        static string Safe(string s) { foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' })) s = s.Replace(c, '_'); return s.Trim(); }
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var index = new StringBuilder();
            index.Append($"<!doctype html><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(p.Number)}</title><style>body{{font-family:Arial;font-size:13px}}td,th{{border:1px solid #ccc;padding:4px 6px}}table{{border-collapse:collapse}}</style>");
            index.Append($"<h2>{WebUtility.HtmlEncode(I18n.T("Hồ sơ lưu trữ gói {0}", p.Number))} – {WebUtility.HtmlEncode(p.Title)}</h2>");
            index.Append($"<p>{WebUtility.HtmlEncode(I18n.T("Xuất lúc {0} · giá gói {1} đ · hình thức {2} · đóng hồ sơ {3} · lưu trữ đến {4}", DateTime.Now.ToDateTime(), p.Budget.ToMoney(), p.Method.GetDisplayName(), row.ClosedAt.ToDate(), row.KeepUntil.ToDate()))}</p>");
            index.Append($"<table><tr><th>#</th><th>{I18n.T("Loại")}</th><th>{I18n.T("Số")}</th><th>{I18n.T("Nội dung")}</th><th>{I18n.T("Trạng thái")}</th><th>{I18n.T("Thư mục")}</th><th>{I18n.T("File")}</th></tr>");
            var n = 0;
            foreach (var d in docs)
            {
                var folder = $"{++n:00}-{Safe(d.Type.GetDisplayName())}-{Safe(d.Number)}";
                var files = await Db.Attachments.AsNoTracking().Where(a => a.DocumentType == d.Type && a.DocumentId == d.Id).OrderBy(a => a.UploadedAt).ToListAsync();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in files)
                {
                    var name = Safe(a.FileName); var k = 1;
                    while (!used.Add(name)) name = $"{Path.GetFileNameWithoutExtension(a.FileName)}-{++k}{Path.GetExtension(a.FileName)}";
                    var e = zip.CreateEntry($"{folder}/{name}", CompressionLevel.Optimal);
                    await using var es = e.Open(); await es.WriteAsync(a.Data);
                }
                var hist = await Db.ApprovalHistories.AsNoTracking().Include(h => h.User).Where(h => h.DocumentType == d.Type && h.DocumentId == d.Id).OrderBy(h => h.CreatedAt).ToListAsync();
                var csv = new StringBuilder("﻿" + I18n.T("Thời điểm") + "," + I18n.T("Người thực hiện") + "," + I18n.T("Hành động") + "," + I18n.T("Ý kiến") + "\r\n");
                static string C(string? v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                foreach (var h in hist) csv.Append($"{C(h.CreatedAt.ToDateTime())},{C(h.User?.FullName)},{C(h.Action)},{C(h.Comment)}\r\n");
                var ce = zip.CreateEntry($"{folder}/{Safe(I18n.T("nhat-ky"))}.csv");
                await using (var cs = ce.Open()) await cs.WriteAsync(Encoding.UTF8.GetBytes(csv.ToString()));
                index.Append($"<tr><td>{n}</td><td>{WebUtility.HtmlEncode(d.Type.GetDisplayName())}</td><td>{WebUtility.HtmlEncode(d.Number)}</td><td>{WebUtility.HtmlEncode(d.Title)}</td><td>{WebUtility.HtmlEncode(d.Status)}</td><td>{WebUtility.HtmlEncode(folder)}</td><td>{files.Count}</td></tr>");
            }
            index.Append("</table>");
            var ie = zip.CreateEntry("00-muc-luc.html");
            await using (var s = ie.Open()) await s.WriteAsync(Encoding.UTF8.GetBytes(index.ToString()));
        }
        Log(DocType.Package, id, I18n.T("Xuất hồ sơ lưu trữ (ZIP)"), I18n.T("{0} chứng từ, {1} file", docs.Count, row.Attachments));
        await Db.SaveChangesAsync();
        return File(ms.ToArray(), "application/zip", $"HoSo-{Safe(p.Number)}-{DateTime.Now:yyyyMMdd}.zip");
    }
}
