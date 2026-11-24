using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Cổng NCC: hồ sơ chào giá chỉ định gửi tới quý công ty → nộp báo giá niêm phong / rút báo giá</summary>
public class PortalDirectQuotesController : PortalControllerBase
{
    private readonly SealingService _seal;
    public PortalDirectQuotesController(EProcDbContext db, SealingService seal) : base(db) => _seal = seal;

    private Task<DirectAward?> LoadAsync(int id) => Db.DirectAwards.Include(d => d.Lines).Include(d => d.Buyer)
        .FirstOrDefaultAsync(d => d.Id == id && d.VendorId == MyVendor.Id && d.QuoteRequestedAt != null);

    /// <summary>Còn nhận báo giá: hồ sơ đang soạn, chưa mở báo giá, chưa quá hạn</summary>
    public static bool IsOpen(DirectAward d) => d.Status == DirectAwardStatus.Draft && d.QuoteOpenedAt == null && d.QuoteDeadline > DateTime.Now;

    public async Task<IActionResult> Details(int id)
    {
        var d = await LoadAsync(id);
        if (d == null) return NotFound();
        d.Lines = d.Lines.OrderBy(l => l.SortOrder).ToList();
        // Vòng báo giá hiện tại: các lần nộp sau thời điểm gửi yêu cầu gần nhất
        ViewBag.Submissions = await Db.VendorSubmissions.AsNoTracking().Where(s => s.DirectAwardId == id && s.SubmittedAt >= d.QuoteRequestedAt).OrderByDescending(s => s.Revision)
            .Select(s => new VendorSubmission { Id = s.Id, Revision = s.Revision, SubmittedAt = s.SubmittedAt, Sha256 = s.Sha256, IsLatest = s.IsLatest, IsWithdrawn = s.IsWithdrawn, FileName = s.FileName, UnsealedAt = s.UnsealedAt })
            .ToListAsync();
        ViewBag.CanSubmit = CanQuote && IsOpen(d);
        ViewBag.CanQuote = CanQuote;
        ViewData["Title"] = $"Chào giá chỉ định {d.Number}";
        return View(d);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> Submit(int id, List<decimal> prices, List<string?> notes, int? leadTimeDays, string? paymentTerms, string? warranty, decimal vatPercent, string? note, IFormFile? file)
    {
        var d = await LoadAsync(id);
        if (d == null) return NotFound();
        IActionResult Back(string err) { TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanQuote) return Back(I18n.T("Tài khoản / NCC chưa được phê duyệt để báo giá."));
        if (!IsOpen(d)) return Back(I18n.T("Hồ sơ không còn nhận báo giá (đã hết hạn hoặc đã mở)."));
        var lines = d.Lines.OrderBy(l => l.SortOrder).ToList();
        if (prices.Count != lines.Count || prices.Any(p => p <= 0)) return Back(I18n.T("Nhập đơn giá cho tất cả các dòng."));
        if (vatPercent is < 0 or > 20) return Back(I18n.T("VAT không hợp lệ."));
        if (leadTimeDays is < 0 or > 365) return Back(I18n.T("Thời gian giao không hợp lệ."));
        if (CheckFile(file) is string ferr) return Back(ferr);

        var payload = new DirectQuotePayload(d.Id, leadTimeDays, paymentTerms?.Trim(), warranty?.Trim(), vatPercent, note?.Trim(),
            lines.Select((l, i) => new DirectQuoteLinePayload(l.SortOrder, Math.Round(prices[i], 0), i < notes.Count ? notes[i]?.Trim() : null)).ToList(),
            file == null ? null : Path.GetFileName(file.FileName), file?.ContentType, file == null ? null : Convert.ToBase64String(await ReadAsync(file)));
        var prev = await Db.VendorSubmissions.Where(s => s.DirectAwardId == d.Id).ToListAsync();
        foreach (var p in prev) p.IsLatest = false;
        var sub = _seal.Seal(payload, SubmissionKind.DirectQuote, Me.Id, null, null, prev.Count + 1, payload.FileName, file?.Length ?? 0);
        sub.DirectAwardId = d.Id;
        Db.VendorSubmissions.Add(sub);
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { d.BuyerId }, $"{MyVendor.Name} đã nộp báo giá cho hồ sơ chào giá chỉ định {d.Number}" + (sub.Revision > 1 ? $" (lần {sub.Revision})" : ""),
            "Báo giá được niêm phong – mở báo giá trên hồ sơ để thương thảo.", $"/DirectAwards/Details/{d.Id}");
        TempData["Success"] = I18n.T("Đã nộp báo giá lúc {0:dd/MM/yyyy HH:mm:ss}. Mã biên nhận: {1}.", sub.SubmittedAt, sub.Sha256);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id)
    {
        var d = await LoadAsync(id);
        if (d == null) return NotFound();
        if (!IsOpen(d)) { TempData["Error"] = I18n.T("Không rút được báo giá."); return RedirectToAction(nameof(Details), new { id }); }
        foreach (var s in await Db.VendorSubmissions.Where(s => s.DirectAwardId == id && !s.IsWithdrawn && s.UnsealedAt == null).ToListAsync()) s.IsWithdrawn = true;
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { d.BuyerId }, $"{MyVendor.Name} đã rút báo giá hồ sơ {d.Number}", null, $"/DirectAwards/Details/{d.Id}");
        TempData["Success"] = I18n.T("Đã rút báo giá.");
        return RedirectToAction(nameof(Details), new { id });
    }
}
