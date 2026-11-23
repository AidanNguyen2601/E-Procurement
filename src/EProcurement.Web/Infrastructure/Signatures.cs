using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Infrastructure;

public record SignatureCheck(DocumentSignature Signature, bool Authentic, bool ContentUnchanged);

/// <summary>Ký / xác minh chữ ký phê duyệt</summary>
public class SignatureService
{
    private readonly EProcDbContext _db;
    private readonly IDataProtector _p;
    public SignatureService(EProcDbContext db, IDataProtectionProvider dp) { _db = db; _p = dp.CreateProtector("EProc.Signature.v1"); }

    private static string Payload(DocumentSignature s) =>
        $"{(int)s.DocumentType}|{s.DocumentId}|{s.UserId}|{s.SignedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture)}|{s.ContentHash}|{s.Action}";

    public async Task<DocumentSignature> SignAsync(DocType type, int id, int userId, string action, string? ip, string? ua)
    {
        var s = new DocumentSignature
        {
            DocumentType = type, DocumentId = id, UserId = userId, Action = action.Length > 200 ? action[..200] : action, SignedAt = DateTime.Now,
            ContentHash = await HashAsync(type, id), IpAddress = ip, UserAgent = ua?.Length > 300 ? ua[..300] : ua
        };
        s.SignatureValue = _p.Protect(Payload(s));
        _db.DocumentSignatures.Add(s);
        await _db.SaveChangesAsync();
        return s;
    }

    public async Task<List<SignatureCheck>> VerifyAsync(DocType type, int id)
    {
        var list = await _db.DocumentSignatures.AsNoTracking().Include(s => s.User).Where(s => s.DocumentType == type && s.DocumentId == id).OrderBy(s => s.SignedAt).ToListAsync();
        if (list.Count == 0) return new();
        var current = await HashAsync(type, id);
        return list.Select(s =>
        {
            bool ok;
            try { ok = _p.Unprotect(s.SignatureValue) == Payload(s); } catch (CryptographicException) { ok = false; }
            return new SignatureCheck(s, ok, s.ContentHash == current);
        }).ToList();
    }

    /// <summary>Mã băm các nội dung chính của chứng từ (không gồm trạng thái / thời điểm xử lý)</summary>
    public async Task<string> HashAsync(DocType type, int id)
    {
        object? snap = type switch
        {
            DocType.PR => await _db.PurchaseRequests.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.Title, x.Department, x.RequesterId, x.EstimatedTotal,
                Lines = x.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.ItemId, l.Description, l.Quantity, l.EstimatedPrice }) }).FirstOrDefaultAsync(),
            DocType.PO => await _db.PurchaseOrders.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.VendorId, x.VatPercent, x.Total, x.DeliveryDate, x.PaymentTerms,
                Lines = x.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.ItemId, l.Description, l.Quantity, l.UnitPrice }) }).FirstOrDefaultAsync(),
            DocType.RFQ => await _db.Rfqs.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.ProposedVendorId, x.EvaluationMethod,
                Quotes = x.Vendors.OrderBy(v => v.VendorId).Select(v => new { v.VendorId, v.TotalAmount, v.VatPercent, v.TechnicalPass, v.TechnicalScore }) }).FirstOrDefaultAsync(),
            DocType.DirectAward => await _db.DirectAwards.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.VendorId, x.Reason, x.NegotiatedTotal, x.VatPercent,
                Lines = x.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.ItemId, l.Quantity, l.NegotiatedPrice }) }).FirstOrDefaultAsync(),
            DocType.Tender => await _db.Tenders.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.PackageBudget, x.EnvelopeMethod, x.EvaluationMethod, x.ClosingAt, x.AwardedVendorId, x.AwardPrice,
                Lines = x.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.ItemId, l.Quantity, l.EstimatedPrice }) }).FirstOrDefaultAsync(),
            DocType.Contract => await _db.Contracts.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.VendorId, x.Value, x.VatPercent, x.EndDate,
                Lines = x.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.ItemId, l.Quantity, l.UnitPrice }) }).FirstOrDefaultAsync(),
            DocType.Invoice => await _db.Invoices.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.VendorId, x.InvoiceNo, x.Series, x.Total,
                Lines = x.Lines.Select(l => new { l.PurchaseOrderLineId, l.Quantity, l.UnitPrice }) }).FirstOrDefaultAsync(),
            DocType.Payment => await _db.Payments.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.VendorId, x.InvoiceId, x.ContractId, x.Amount, x.PenaltyAmount, x.AdvanceDeduction, x.NetAmount }).FirstOrDefaultAsync(),
            DocType.Plan => await _db.ProcurementPlans.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Year, x.Department,
                Lines = x.Lines.OrderBy(l => l.Id).Select(l => new { l.ItemId, l.Description, l.Quantity, l.EstimatedPrice, l.Quarter }) }).FirstOrDefaultAsync(),
            DocType.SelectionPlan => await _db.SelectionPlans.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.Number, x.Title,
                Packages = x.Packages.OrderBy(p => p.Id).Select(p => new { p.Number, p.Budget, p.Method, p.EnvelopeMethod, p.ContractType, p.ContractDurationDays, p.PlannedStart }) }).FirstOrDefaultAsync(),
            _ => null
        };
        var json = JsonSerializer.Serialize(snap);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}

/// <summary>
/// Thao tác quyết định (duyệt / từ chối) phải ký: nhập lại mật khẩu (form field signPassword). Sau khi thao tác thành công,
/// lưu chữ ký gắn mã băm nội dung chứng từ. Tắt bằng Procurement:RequireSignature = false.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class RequireSignatureAttribute : Attribute, IAsyncActionFilter
{
    public DocType Type { get; }
    public RequireSignatureAttribute(DocType type) => Type = type;

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var sp = ctx.HttpContext.RequestServices;
        if (!sp.GetRequiredService<IOptions<ProcurementSettings>>().Value.RequireSignature) { await next(); return; }
        var http = ctx.HttpContext;
        var db = sp.GetRequiredService<EProcDbContext>();
        var me = http.User.GetUserId();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == me);
        var pwd = http.Request.HasFormContentType ? http.Request.Form["signPassword"].ToString() : "";
        string? error = null;
        if (user == null) error = "Không xác định được người ký.";
        else if (LoginGuard.IsLocked(user.LockoutEnd)) error = LoginGuard.LockedMessage(user.LockoutEnd!.Value);
        else if (string.IsNullOrEmpty(pwd)) error = "Nhập mật khẩu để ký xác nhận quyết định.";
        else if (sp.GetRequiredService<IPasswordHasher<AppUser>>().VerifyHashedPassword(user, user.PasswordHash, pwd) == PasswordVerificationResult.Failed)
        {
            int f = user.FailedLogins; DateTime? until = user.LockoutEnd;
            var locked = LoginGuard.Fail(ref f, ref until);
            user.FailedLogins = f; user.LockoutEnd = until; await db.SaveChangesAsync();
            error = locked ? LoginGuard.LockedMessage(until!.Value) : "Mật khẩu ký không đúng – quyết định chưa được ghi nhận.";
        }
        if (error != null)
        {
            var tempData = sp.GetRequiredService<ITempDataDictionaryFactory>().GetTempData(http);
            tempData["Error"] = error;
            var back = http.Request.Headers.Referer.ToString();
            ctx.Result = Uri.TryCreate(back, UriKind.Absolute, out var u) && u.Host == http.Request.Host.Host ? new RedirectResult(u.PathAndQuery) : new RedirectResult("/");
            return;
        }
        user!.FailedLogins = 0;
        var executed = await next();
        if (executed.Exception != null && !executed.ExceptionHandled) return;
        if (executed.Result is not (RedirectResult or RedirectToActionResult or LocalRedirectResult)) return; // lỗi nghiệp vụ hiển thị lại form
        if (ctx.Controller is EProcurement.Web.Controllers.ProcControllerBase pc && !pc.HasSavedLog) return; // bị từ chối do thiếu điều kiện – không có gì để ký
        if (!ctx.ActionArguments.TryGetValue("id", out var idObj) || idObj is not int id) return;
        var form = http.Request.Form;
        var approve = form["approve"].ToString() is "true" || form["accept"].ToString() is "true" || form["agree"].ToString() is "true";
        var label = (approve ? "Ký duyệt" : "Ký quyết định không duyệt / từ chối") + " – " + Type.GetDisplayName().ToLowerInvariant()
                    + (ctx.ActionDescriptor.RouteValues["action"] is string a && a.Contains("Plan") ? " (kế hoạch & HSMT)" : "")
                    + (ctx.ActionDescriptor.RouteValues["action"] is string b && b.Contains("Amendment") ? " (phụ lục)" : "")
                    + (ctx.ActionDescriptor.RouteValues["action"] is string c && c == "DecideResult" ? " (kết quả)" : "")
                    + (ctx.ActionDescriptor.RouteValues["action"] is string e && e == "Appraise" ? " (thẩm định kết quả LCNT)" : "");
        await sp.GetRequiredService<SignatureService>().SignAsync(Type, id, me, label, http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
    }
}

/// <summary>Khối chữ ký trên trang chứng từ</summary>
public class SignaturesViewComponent : ViewComponent
{
    private readonly SignatureService _s;
    public SignaturesViewComponent(SignatureService s) => _s = s;
    public async Task<IViewComponentResult> InvokeAsync(DocType type, int id) => View(await _s.VerifyAsync(type, id));
}
