using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Xác thực cổng NCC: cookie riêng, không bao giờ mang vai trò nội bộ</summary>
public static class PortalAuth
{
    public const string Scheme = "VendorPortal";
    public const string VendorIdClaim = "eproc:vendor-id";
    public const string VendorUserIdClaim = "eproc:vendor-user-id";
    public const string ApprovedClaim = "eproc:vendor-approved";

    public static int VendorId(this ClaimsPrincipal u) => int.TryParse(u.FindFirstValue(VendorIdClaim), out var v) ? v : 0;
    public static int VendorUserId(this ClaimsPrincipal u) => int.TryParse(u.FindFirstValue(VendorUserIdClaim), out var v) ? v : 0;
    public static bool IsVendorPrincipal(this ClaimsPrincipal u) => u.HasClaim(c => c.Type == VendorIdClaim);
}

/// <summary>Áp yêu cầu đăng nhập nội bộ cho mọi controller trừ cổng NCC (Portal*) – thay cho filter toàn cục</summary>
public class InternalAuthConvention : IControllerModelConvention
{
    private readonly AuthorizeFilter _filter;
    public InternalAuthConvention(AuthorizeFilter filter) => _filter = filter;
    public void Apply(ControllerModel controller)
    {
        // Cổng NCC (cookie riêng) và REST API (API key) có cơ chế xác thực riêng
        var isApi = controller.Attributes.Any(a => a.GetType().Name == "ApiKeyAttribute");
        if (!controller.ControllerName.StartsWith("Portal", StringComparison.Ordinal) && !isApi) controller.Filters.Add(_filter);
    }
}

// ---------------------------------------------------------------- nội dung niêm phong
public record QuotePayload(int RfqVendorId, int? LeadTimeDays, string? PaymentTerms, string? Warranty, decimal VatPercent, string? Note,
    List<QuoteLinePayload> Lines, string? FileName, string? ContentType, string? FileBase64);
public record QuoteLinePayload(int RfqLineId, decimal UnitPrice, string? Note);

/// <summary>Báo giá của NCC được chỉ định (dòng theo thứ tự dòng của hồ sơ chào giá chỉ định)</summary>
public record DirectQuotePayload(int DirectAwardId, int? LeadTimeDays, string? PaymentTerms, string? Warranty, decimal VatPercent, string? Note,
    List<DirectQuoteLinePayload> Lines, string? FileName, string? ContentType, string? FileBase64);
public record DirectQuoteLinePayload(int SortOrder, decimal UnitPrice, string? Note);

/// <summary>
/// HSDT niêm phong. FileName/ContentType/FileBase64: bản cũ một file (vẫn đọc được). Files: các thành phần HSDT –
/// file tài chính của gói hai túi chỉ được mở cùng hồ sơ tài chính.
/// </summary>
public record BidPayload(int TenderBidderId, decimal DiscountPercent, decimal VatPercent, int? LeadTimeDays, int? ValidityDays,
    decimal BidSecurityAmount, string? BidSecurityRef, DateTime? BidSecurityExpiry, string? Note,
    List<BidLinePayload> Lines, string? FileName, string? ContentType, string? FileBase64, List<BidFilePayload>? Files = null);
public record BidLinePayload(int TenderLineId, decimal UnitPrice);
public record BidFilePayload(BidFileKind Kind, string FileName, string? ContentType, string Base64);

/// <summary>
/// Niêm phong báo giá / HSDT nộp online bằng ASP.NET Data Protection (khóa lưu ở volume, không nằm trong database);
/// mã băm SHA-256 của nội dung + thời điểm nộp là biên nhận cho NCC và để kiểm tra toàn vẹn khi mở.
/// Hệ thống chỉ giải mã khi mở báo giá / mở thầu (/ mở hồ sơ tài chính).
/// </summary>
public class SealingService
{
    private readonly IDataProtector _p;
    private readonly EProcDbContext _db;
    public SealingService(IDataProtectionProvider dp, EProcDbContext db) { _p = dp.CreateProtector("EProc.Sealed.v1"); _db = db; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Định dạng thời điểm không phụ thuộc DateTimeKind (giá trị đọc lại từ datetime2 không còn múi giờ)</summary>
    public static string Hash(string json, DateTime at) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json + "|" + at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)))).ToLowerInvariant();

    public VendorSubmission Seal<T>(T payload, SubmissionKind kind, int vendorUserId, int? rfqVendorId, int? bidderId, int revision, string? fileName, long fileSize)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        var at = DateTime.Now;
        return new VendorSubmission
        {
            Kind = kind, RfqVendorId = rfqVendorId, TenderBidderId = bidderId, VendorUserId = vendorUserId, Revision = revision, SubmittedAt = at,
            SealedPayload = _p.Protect(json), Sha256 = Hash(json, at), FileName = fileName, FileSize = fileSize, IsLatest = true
        };
    }

    private (T Payload, bool Intact) Open<T>(VendorSubmission s)
    {
        var json = _p.Unprotect(s.SealedPayload);
        return (JsonSerializer.Deserialize<T>(json, Json)!, Hash(json, s.SubmittedAt) == s.Sha256);
    }

    /// <summary>Mở báo giá: đưa nội dung báo giá online (bản cuối) vào RfqVendor. Trả về ghi chú kiểm tra toàn vẹn.</summary>
    public async Task<List<string>> UnsealQuotesAsync(Rfq r, int actorId)
    {
        var notes = new List<string>();
        var ids = r.Vendors.Select(v => v.Id).ToList();
        var subs = await _db.VendorSubmissions.Where(s => s.RfqVendorId != null && ids.Contains(s.RfqVendorId.Value) && s.IsLatest && !s.IsWithdrawn && s.UnsealedAt == null).ToListAsync();
        foreach (var s in subs)
        {
            var rv = r.Vendors.First(v => v.Id == s.RfqVendorId);
            var (p, ok) = Open<QuotePayload>(s);
            rv.QuoteLines.Clear();
            foreach (var l in p.Lines.Where(l => r.Lines.Any(x => x.Id == l.RfqLineId)))
            {
                var line = r.Lines.First(x => x.Id == l.RfqLineId);
                rv.QuoteLines.Add(new VendorQuoteLine { RfqLineId = l.RfqLineId, UnitPrice = l.UnitPrice, Amount = Math.Round(l.UnitPrice * line.Quantity, 0), Note = l.Note });
            }
            rv.TotalAmount = rv.QuoteLines.Sum(q => q.Amount);
            rv.LeadTimeDays = p.LeadTimeDays; rv.PaymentTerms = p.PaymentTerms; rv.Warranty = p.Warranty; rv.VatPercent = p.VatPercent; rv.Note = p.Note;
            rv.Responded = true; rv.RespondedAt = s.SubmittedAt;
            s.UnsealedAt = DateTime.Now;
            if (p.FileBase64 != null) AddFile(DocType.RFQ, r.Id, p.FileName, p.ContentType, p.FileBase64, actorId, $"Báo giá online của {rv.Vendor?.Name}");
            notes.Add($"{rv.Vendor?.Name}: báo giá online lần {s.Revision} nộp {s.SubmittedAt:dd/MM/yyyy HH:mm:ss}, mã băm {s.Sha256[..12]}… {(ok ? "khớp" : "KHÔNG KHỚP")}");
        }
        return notes;
    }

    /// <summary>
    /// Mở báo giá NCC được chỉ định (bản cuối, chưa rút): đưa đơn giá chào vào từng dòng (theo thứ tự dòng), giá thương thảo
    /// mặc định = giá chào nếu chưa nhập. Trả về ghi chú kiểm tra toàn vẹn, null nếu không có báo giá.
    /// </summary>
    public async Task<string?> UnsealDirectQuoteAsync(DirectAward d, int actorId)
    {
        var s = await _db.VendorSubmissions.Where(x => x.DirectAwardId == d.Id && x.IsLatest && !x.IsWithdrawn && x.UnsealedAt == null).FirstOrDefaultAsync();
        if (s == null) return null;
        var (p, ok) = Open<DirectQuotePayload>(s);
        foreach (var l in d.Lines)
        {
            var q = p.Lines.FirstOrDefault(x => x.SortOrder == l.SortOrder);
            if (q == null) continue;
            l.ProposedPrice = q.UnitPrice;
            if (l.NegotiatedPrice <= 0 || l.NegotiatedPrice > q.UnitPrice) l.NegotiatedPrice = q.UnitPrice;
        }
        d.LeadTimeDays = p.LeadTimeDays ?? d.LeadTimeDays; d.PaymentTerms = p.PaymentTerms ?? d.PaymentTerms; d.Warranty = p.Warranty ?? d.Warranty;
        d.VatPercent = p.VatPercent;
        if (!string.IsNullOrWhiteSpace(p.Note)) d.NegotiationNote = string.IsNullOrWhiteSpace(d.NegotiationNote) ? $"Ghi chú báo giá NCC: {p.Note}" : d.NegotiationNote + $"\nGhi chú báo giá NCC: {p.Note}";
        d.Recalculate();
        d.QuoteOpenedAt = DateTime.Now;
        s.UnsealedAt = DateTime.Now;
        if (p.FileBase64 != null) AddFile(DocType.DirectAward, d.Id, p.FileName, p.ContentType, p.FileBase64, actorId, $"Báo giá online của {d.Vendor?.Name}");
        return $"Báo giá online lần {s.Revision} nộp {s.SubmittedAt:dd/MM/yyyy HH:mm:ss}, mã băm {s.Sha256[..12]}… {(ok ? "khớp" : "KHÔNG KHỚP")}";
    }

    /// <summary>Mở thầu (includePrices = true với một túi hồ sơ) / mở hồ sơ tài chính (chỉ giá)</summary>
    public async Task<List<string>> UnsealBidsAsync(Tender t, int actorId, bool includePrices, bool pricesOnly = false)
    {
        var notes = new List<string>();
        var ids = t.Bidders.Select(b => b.Id).ToList();
        var subs = await _db.VendorSubmissions.Where(s => s.TenderBidderId != null && ids.Contains(s.TenderBidderId.Value) && s.IsLatest && !s.IsWithdrawn).ToListAsync();
        foreach (var s in subs)
        {
            var b = t.Bidders.First(x => x.Id == s.TenderBidderId);
            var (p, ok) = Open<BidPayload>(s);
            if (!pricesOnly)
            {
                b.Submitted = true; b.SubmittedAt = s.SubmittedAt;
                b.LeadTimeDays = p.LeadTimeDays; b.ValidityDays = p.ValidityDays; b.BidSecurityAmount = p.BidSecurityAmount;
                b.BidSecurityRef = p.BidSecurityRef; b.BidSecurityExpiry = p.BidSecurityExpiry; b.Note = p.Note; b.VatPercent = p.VatPercent;
                if (p.FileBase64 != null) AddFile(DocType.Tender, t.Id, p.FileName, p.ContentType, p.FileBase64, actorId, $"HSDT online của {b.Vendor?.Name}");
                foreach (var f in (p.Files ?? new()).Where(f => f.Kind != BidFileKind.Financial))
                    AddFile(DocType.Tender, t.Id, f.FileName, f.ContentType, f.Base64, actorId, $"HSDT online của {b.Vendor?.Name} – {f.Kind.GetDisplayName()}");
                notes.Add($"{b.Vendor?.Name}: HSDT online lần {s.Revision} nộp {s.SubmittedAt:dd/MM/yyyy HH:mm:ss}, mã băm {s.Sha256[..12]}… {(ok ? "khớp" : "KHÔNG KHỚP")}");
            }
            if (includePrices)
            {
                foreach (var f in (p.Files ?? new()).Where(f => f.Kind == BidFileKind.Financial))
                    AddFile(DocType.Tender, t.Id, f.FileName, f.ContentType, f.Base64, actorId, $"HSDT online của {b.Vendor?.Name} – {f.Kind.GetDisplayName()}");
                b.DiscountPercent = p.DiscountPercent;
                b.Lines.Clear();
                foreach (var l in p.Lines.Where(l => t.Lines.Any(x => x.Id == l.TenderLineId)))
                    b.Lines.Add(new TenderBidLine { TenderLineId = l.TenderLineId, UnitPrice = l.UnitPrice });
                b.BidPrice = b.Lines.Sum(l => Math.Round(l.UnitPrice * t.Lines.First(x => x.Id == l.TenderLineId).Quantity, 0));
                s.UnsealedAt = DateTime.Now;
            }
        }
        return notes;
    }

    private void AddFile(DocType type, int id, string? fileName, string? contentType, string base64, int actorId, string note)
    {
        var data = Convert.FromBase64String(base64);
        _db.Attachments.Add(new Attachment
        {
            DocumentType = type, DocumentId = id, FileName = fileName ?? "ho-so.pdf", ContentType = contentType ?? "application/octet-stream",
            Size = data.Length, Data = data, Note = note, UploadedById = actorId, UploadedAt = DateTime.Now
        });
    }
}
