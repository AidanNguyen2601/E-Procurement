using System.Security.Cryptography;
using System.Text;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

// =====================================================================
//  REST API tích hợp ERP / kế toán – xác thực bằng header X-Api-Key
// =====================================================================

public static class ApiScopes
{
    public const string Read = "read";
    public const string PaymentsWrite = "payments:write";
    public static readonly string[] All = { Read, PaymentsWrite };
    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}

/// <summary>Kiểm tra API key + phạm vi; ghi thời điểm dùng gần nhất</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public string Scope { get; }
    public ApiKeyAttribute(string scope = ApiScopes.Read) => Scope = scope;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext ctx)
    {
        var key = ctx.HttpContext.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key)) { ctx.Result = Problem(401, "Thiếu header X-Api-Key"); return; }
        var db = ctx.HttpContext.RequestServices.GetRequiredService<EProcDbContext>();
        var hash = ApiScopes.Hash(key.Trim());
        var client = await db.ApiClients.FirstOrDefaultAsync(c => c.KeyHash == hash);
        if (client == null || !client.IsActive) { ctx.Result = Problem(401, "API key không hợp lệ hoặc đã bị thu hồi"); return; }
        var scopes = client.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(Scope)) { ctx.Result = Problem(403, $"API key không có quyền '{Scope}'"); return; }
        if (client.LastUsedAt == null || client.LastUsedAt < DateTime.Now.AddMinutes(-1)) { client.LastUsedAt = DateTime.Now; await db.SaveChangesAsync(); }
        ctx.HttpContext.Items["ApiClient"] = client;
    }

    private static ObjectResult Problem(int status, string detail) => new(new { status, error = detail }) { StatusCode = status };
}

[ApiController]
[Route("api/v1")]
[ApiKey]
[EnableRateLimiting("api")]
[Produces("application/json")]
public class ApiV1Controller : ControllerBase
{
    private readonly EProcDbContext _db;
    public ApiV1Controller(EProcDbContext db) => _db = db;
    private ApiClient Client => (ApiClient)HttpContext.Items["ApiClient"]!;
    private const int MaxPage = 500;

    private static (int Skip, int Take) Paging(int page, int pageSize) { pageSize = Math.Clamp(pageSize, 1, MaxPage); return ((Math.Max(1, page) - 1) * pageSize, pageSize); }

    /// <summary>Nhà cung cấp</summary>
    [HttpGet("vendors")]
    public async Task<IActionResult> Vendors(string? status, int page = 1, int pageSize = 200)
    {
        var q = _db.Vendors.AsNoTracking().AsQueryable();
        if (Enum.TryParse<VendorStatus>(status, true, out var st)) q = q.Where(v => v.Status == st);
        var (skip, take) = Paging(page, pageSize);
        var total = await q.CountAsync();
        var data = await q.OrderBy(v => v.Code).Skip(skip).Take(take).Select(v => new
        {
            v.Id, v.Code, v.Name, v.TaxCode, status = v.Status.ToString(), v.Address, v.Phone, v.Email, v.ContactPerson, v.BankName, v.BankAccount, v.PaymentTerms, v.Rating
        }).ToListAsync();
        return Ok(new { page, pageSize = take, total, data });
    }

    /// <summary>Danh mục hàng hóa</summary>
    [HttpGet("items")]
    public async Task<IActionResult> Items(int page = 1, int pageSize = 500)
    {
        var (skip, take) = Paging(page, pageSize);
        var q = _db.Items.AsNoTracking();
        return Ok(new
        {
            page, pageSize = take, total = await q.CountAsync(),
            data = await q.OrderBy(i => i.Code).Skip(skip).Take(take).Select(i => new { i.Id, i.Code, i.Name, category = i.Category != null ? i.Category.Code : null, i.Unit, i.ReferencePrice, i.IsActive }).ToListAsync()
        });
    }

    /// <summary>Đơn mua hàng (đã duyệt trở đi), kèm dòng hàng</summary>
    [HttpGet("purchase-orders")]
    public async Task<IActionResult> PurchaseOrders(string? status, DateTime? from, DateTime? to, int page = 1, int pageSize = 100)
    {
        var q = _db.PurchaseOrders.AsNoTracking().Where(p => p.Status >= PoStatus.Approved);
        if (Enum.TryParse<PoStatus>(status, true, out var st)) q = q.Where(p => p.Status == st);
        if (from.HasValue) q = q.Where(p => p.OrderDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(p => p.OrderDate < to.Value.Date.AddDays(1));
        var (skip, take) = Paging(page, pageSize);
        return Ok(new
        {
            page, pageSize = take, total = await q.CountAsync(),
            data = await q.OrderBy(p => p.OrderDate).ThenBy(p => p.Id).Skip(skip).Take(take).Select(p => new
            {
                p.Id, p.Number, status = p.Status.ToString(), method = p.Method.HasValue ? p.Method.ToString() : null, p.OrderDate, p.DeliveryDate,
                vendor = new { p.Vendor!.Code, p.Vendor.Name, p.Vendor.TaxCode }, department = p.PurchaseRequest != null ? p.PurchaseRequest.Department : null,
                purchaseRequest = p.PurchaseRequest != null ? p.PurchaseRequest.Number : null, contract = p.Contract != null ? p.Contract.Number : null,
                p.SubTotal, p.VatPercent, p.VatAmount, p.Total, p.PaymentTerms,
                lines = p.Lines.OrderBy(l => l.SortOrder).Select(l => new { l.Id, item = l.Item != null ? l.Item.Code : null, l.Description, l.Unit, l.Quantity, l.UnitPrice, l.Amount, l.ReceivedQuantity })
            }).ToListAsync()
        });
    }

    /// <summary>Phiếu nhận hàng</summary>
    [HttpGet("goods-receipts")]
    public async Task<IActionResult> GoodsReceipts(DateTime? from, DateTime? to, int page = 1, int pageSize = 200)
    {
        var q = _db.GoodsReceipts.AsNoTracking().AsQueryable();
        if (from.HasValue) q = q.Where(g => g.ReceiptDate >= from.Value.Date);
        if (to.HasValue) q = q.Where(g => g.ReceiptDate < to.Value.Date.AddDays(1));
        var (skip, take) = Paging(page, pageSize);
        return Ok(new
        {
            page, pageSize = take, total = await q.CountAsync(),
            data = await q.OrderBy(g => g.ReceiptDate).ThenBy(g => g.Id).Skip(skip).Take(take).Select(g => new
            {
                g.Id, g.Number, purchaseOrder = g.PurchaseOrder!.Number, g.ReceiptDate, g.DeliveryNoteNo,
                lines = g.Lines.Select(l => new { purchaseOrderLineId = l.PurchaseOrderLineId, item = l.PurchaseOrderLine!.Item != null ? l.PurchaseOrderLine.Item.Code : null, l.QuantityAccepted, l.QuantityRejected })
            }).ToListAsync()
        });
    }

    /// <summary>Hóa đơn NCC (đã đối chiếu 3 bên)</summary>
    [HttpGet("invoices")]
    public async Task<IActionResult> Invoices(string? status, DateTime? from, int page = 1, int pageSize = 200)
    {
        var q = _db.Invoices.AsNoTracking().AsQueryable();
        if (Enum.TryParse<InvoiceStatus>(status, true, out var st)) q = q.Where(i => i.Status == st);
        if (from.HasValue) q = q.Where(i => i.InvoiceDate >= from.Value.Date);
        var (skip, take) = Paging(page, pageSize);
        return Ok(new
        {
            page, pageSize = take, total = await q.CountAsync(),
            data = await q.OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id).Skip(skip).Take(take).Select(i => new
            {
                i.Id, i.Number, i.Series, i.InvoiceNo, i.InvoiceDate, i.DueDate, status = i.Status.ToString(),
                vendor = new { i.Vendor!.Code, i.Vendor.TaxCode }, purchaseOrder = i.PurchaseOrder!.Number, i.SubTotal, i.VatAmount, i.Total, i.MatchNote,
                lines = i.Lines.Select(l => new { l.PurchaseOrderLineId, item = l.PurchaseOrderLine!.Item != null ? l.PurchaseOrderLine.Item.Code : null, l.Quantity, l.UnitPrice, l.Amount })
            }).ToListAsync()
        });
    }

    /// <summary>Đề nghị thanh toán (mặc định: đã duyệt – chờ chi)</summary>
    [HttpGet("payments")]
    public async Task<IActionResult> Payments(string? status = "Approved", int page = 1, int pageSize = 200)
    {
        var q = _db.Payments.AsNoTracking().AsQueryable();
        if (Enum.TryParse<PaymentStatus>(status, true, out var st)) q = q.Where(p => p.Status == st);
        var (skip, take) = Paging(page, pageSize);
        return Ok(new
        {
            page, pageSize = take, total = await q.CountAsync(),
            data = await q.OrderBy(p => p.RequestedAt).Skip(skip).Take(take).Select(p => new
            {
                p.Id, p.Number, type = p.Type.ToString(), status = p.Status.ToString(), vendor = new { p.Vendor!.Code, p.Vendor.TaxCode, p.Vendor.BankName, p.Vendor.BankAccount },
                invoice = p.Invoice != null ? p.Invoice.Number : null, invoiceNo = p.Invoice != null ? p.Invoice.InvoiceNo : null, contract = p.Contract != null ? p.Contract.Number : null,
                p.Amount, p.PenaltyAmount, p.AdvanceDeduction, p.NetAmount, p.Note, p.ApprovedAt, p.PaidAt, p.PaymentRef
            }).ToListAsync()
        });
    }

    public record PaidRequest(DateTime PaidAt, string PaymentRef);

    /// <summary>ERP ghi nhận đã chi cho đề nghị thanh toán đã duyệt (phạm vi payments:write)</summary>
    [HttpPost("payments/{number}/paid")]
    [ApiKey(ApiScopes.PaymentsWrite)]
    public async Task<IActionResult> MarkPaid(string number, [FromBody] PaidRequest req)
    {
        var p = await _db.Payments.Include(x => x.Invoice).FirstOrDefaultAsync(x => x.Number == number);
        if (p == null) return NotFound(new { status = 404, error = $"Không có đề nghị thanh toán {number}" });
        if (p.Status == PaymentStatus.Paid) return Conflict(new { status = 409, error = "Đã ghi nhận chi trước đó", p.PaidAt, p.PaymentRef });
        if (p.Status != PaymentStatus.Approved) return UnprocessableEntity(new { status = 422, error = $"Đề nghị đang ở trạng thái {p.Status} – chỉ ghi nhận chi khi đã duyệt" });
        if (string.IsNullOrWhiteSpace(req.PaymentRef) || req.PaymentRef.Length > 100) return BadRequest(new { status = 400, error = "paymentRef bắt buộc, tối đa 100 ký tự" });
        if (req.PaidAt.Date > DateTime.Today) return BadRequest(new { status = 400, error = "paidAt không được ở tương lai" });
        p.Status = PaymentStatus.Paid; p.PaidAt = req.PaidAt.Date; p.PaymentRef = req.PaymentRef.Trim(); p.PaidById = Client.CreatedById;
        if (p.Invoice != null) p.Invoice.Status = InvoiceStatus.Paid;
        _db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = DocType.Payment, DocumentId = p.Id, UserId = Client.CreatedById, CreatedAt = DateTime.Now,
            Action = $"Đã chi {p.NetAmount.ToMoney()} đ (ghi nhận qua API)", Comment = $"{Client.Name}: UNC {p.PaymentRef} ngày {p.PaidAt:dd/MM/yyyy}" });
        await _db.SaveChangesAsync();
        return Ok(new { p.Number, status = p.Status.ToString(), p.PaidAt, p.PaymentRef });
    }

    /// <summary>Ngân sách năm và tình hình sử dụng</summary>
    [HttpGet("budgets")]
    public async Task<IActionResult> Budgets(int? year)
    {
        var rows = await new BudgetService(_db).StatusAsync(year ?? DateTime.Today.Year);
        return Ok(new
        {
            year = year ?? DateTime.Today.Year,
            data = rows.Select(r => new { r.Budget.Department, category = r.Budget.Category?.Code, amount = r.Budget.Amount, ordered = r.Ordered, reserved = r.Reserved, available = r.Available, usedPercent = r.UsedPercent })
        });
    }
}
