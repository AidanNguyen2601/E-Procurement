using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class InvoiceLineInput { public int PurchaseOrderLineId { get; set; } public decimal Quantity { get; set; } public decimal UnitPrice { get; set; } }

public class InvoiceForm
{
    public int PurchaseOrderId { get; set; }
    public string InvoiceNo { get; set; } = "";
    public string? Series { get; set; }
    public DateTime InvoiceDate { get; set; } = DateTime.Today;
    /// <summary>Thuế GTGT ghi trên hóa đơn (để trống = tính theo % VAT của PO)</summary>
    public decimal? VatAmount { get; set; }
    public List<InvoiceLineInput> Lines { get; set; } = new();
}

/// <summary>Tạo hóa đơn + đối chiếu 3 bên – dùng chung cho kế toán nhập và NCC gửi qua cổng</summary>
public static class InvoiceFactory
{
    public static async Task<(Invoice? Invoice, MatchResult? Match, string? Error)> CreateAsync(EProcDbContext db, FinanceService fin, PurchaseOrder po, InvoiceForm f,
        IFormFile? file, int maxBytes, int? userId, int? vendorUserId)
    {
        f.InvoiceNo = (f.InvoiceNo ?? "").Trim(); f.Series = string.IsNullOrWhiteSpace(f.Series) ? null : f.Series.Trim().ToUpperInvariant();
        if (f.InvoiceNo.Length == 0 || f.InvoiceNo.Length > 30) return (null, null, "Nhập số hóa đơn (tối đa 30 ký tự).");
        if (f.InvoiceDate.Date > DateTime.Today) return (null, null, "Ngày hóa đơn không được ở tương lai.");
        if (await db.Invoices.AnyAsync(i => i.VendorId == po.VendorId && i.InvoiceNo == f.InvoiceNo && i.Series == f.Series && i.Status != InvoiceStatus.Rejected))
            return (null, null, $"Hóa đơn {f.Series} {f.InvoiceNo} của NCC đã được ghi nhận – không nhận trùng.");
        var lines = f.Lines.Where(l => l.Quantity > 0).ToList();
        if (lines.Count == 0) return (null, null, "Nhập số lượng cho ít nhất một dòng.");
        if (lines.Any(l => l.UnitPrice < 0 || po.Lines.All(p => p.Id != l.PurchaseOrderLineId))) return (null, null, "Dòng hóa đơn không hợp lệ.");
        if (file != null && file.Length > 0)
        {
            if (file.Length > maxBytes) return (null, null, "File hóa đơn quá lớn.");
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not (".pdf" or ".xml" or ".jpg" or ".jpeg" or ".png" or ".zip")) return (null, null, "File hóa đơn: pdf, xml, jpg, png hoặc zip.");
        }
        var inv = new Invoice
        {
            Number = await DocNumbers.NextAsync(db.Invoices.Select(i => i.Number), "HDV"), VendorId = po.VendorId!.Value, PurchaseOrderId = po.Id,
            InvoiceNo = f.InvoiceNo, Series = f.Series, InvoiceDate = f.InvoiceDate.Date, DueDate = FinanceService.DueDate(f.InvoiceDate, po.PaymentTerms),
            FromPortal = vendorUserId.HasValue, CreatedById = userId, VendorUserId = vendorUserId, CreatedAt = DateTime.Now
        };
        foreach (var l in lines)
            inv.Lines.Add(new InvoiceLine { PurchaseOrderLineId = l.PurchaseOrderLineId, Quantity = l.Quantity, UnitPrice = Math.Round(l.UnitPrice, 0), Amount = Math.Round(l.Quantity * l.UnitPrice, 0) });
        inv.SubTotal = inv.Lines.Sum(l => l.Amount);
        inv.VatAmount = f.VatAmount.HasValue ? Math.Round(f.VatAmount.Value, 0) : Math.Round(inv.SubTotal * po.VatPercent / 100m, 0);
        inv.Total = inv.SubTotal + inv.VatAmount;
        if (file != null && file.Length > 0)
        {
            using var ms = new MemoryStream(); await file.CopyToAsync(ms);
            inv.FileData = ms.ToArray(); inv.FileName = Path.GetFileName(file.FileName); inv.ContentType = file.ContentType ?? "application/octet-stream";
        }
        var match = await fin.MatchAsync(inv, po);
        inv.Status = match.Ok ? InvoiceStatus.Matched : InvoiceStatus.Exception;
        inv.MatchNote = match.Summary.Length > 2000 ? match.Summary[..2000] : match.Summary;
        db.Invoices.Add(inv);
        return (inv, match, null);
    }
}

/// <summary>Hóa đơn NCC: tiếp nhận, đối chiếu 3 bên, xử lý sai lệch, đề nghị thanh toán</summary>
[Authorize(Roles = AppRoles.Finance + "," + AppRoles.Director + "," + AppRoles.Buyer)]
public class InvoicesController : ProcControllerBase
{
    private readonly FinanceService _fin;
    public InvoicesController(EProcDbContext db, IOptions<ProcurementSettings> s, FinanceService fin) : base(db, s) => _fin = fin;

    public async Task<IActionResult> Index(InvoiceStatus? status, bool overdue = false)
    {
        var q = Db.Invoices.AsNoTracking().Include(i => i.Vendor).Include(i => i.PurchaseOrder).AsQueryable();
        if (status.HasValue) q = q.Where(i => i.Status == status);
        if (overdue) q = q.Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Rejected && i.DueDate < DateTime.Today);
        ViewBag.Status = status; ViewBag.Overdue = overdue;
        ViewBag.Counts = await Db.Invoices.GroupBy(i => i.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        // PO đã nhận hàng còn phần chưa có hóa đơn
        var received = await Db.PurchaseOrders.AsNoTracking().Include(p => p.Vendor)
            .Where(p => p.Status == PoStatus.Received || p.Status == PoStatus.PartiallyReceived)
            .Select(p => new { Po = p, Received = p.Lines.Sum(l => l.ReceivedQuantity * l.UnitPrice) }).ToListAsync();
        var invoicedByPo = await Db.InvoiceLines.Where(x => x.Invoice!.Status != InvoiceStatus.Rejected)
            .GroupBy(x => x.Invoice!.PurchaseOrderId).Select(g => new { g.Key, S = g.Sum(x => x.Quantity * x.UnitPrice) }).ToDictionaryAsync(x => x.Key, x => x.S);
        ViewBag.Uninvoiced = received.Where(x => x.Received - invoicedByPo.GetValueOrDefault(x.Po.Id) > 1000)
            .Select(x => (x.Po, Remaining: x.Received - invoicedByPo.GetValueOrDefault(x.Po.Id))).OrderBy(x => x.Po.OrderDate).ToList();
        ViewData["Title"] = "Hóa đơn nhà cung cấp";
        return View(await q.OrderByDescending(i => i.CreatedAt).Take(300).ToListAsync());
    }

    private Task<PurchaseOrder?> LoadPoAsync(int id) =>
        Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);

    [Authorize(Roles = AppRoles.Finance)]
    public async Task<IActionResult> Create(int poId)
    {
        var po = await LoadPoAsync(poId);
        if (po == null) return NotFound();
        var invoiced = await Db.InvoiceLines.Where(l => l.Invoice!.PurchaseOrderId == poId && l.Invoice.Status != InvoiceStatus.Rejected)
            .GroupBy(l => l.PurchaseOrderLineId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        ViewBag.Po = po; ViewBag.Invoiced = invoiced;
        ViewData["Title"] = "Nhận hóa đơn – " + po.Number;
        return View(new InvoiceForm
        {
            PurchaseOrderId = poId,
            Lines = po.Lines.OrderBy(l => l.SortOrder).Select(l => new InvoiceLineInput
                { PurchaseOrderLineId = l.Id, Quantity = Math.Max(0, l.ReceivedQuantity - invoiced.GetValueOrDefault(l.Id)), UnitPrice = l.UnitPrice }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> Create(InvoiceForm f, IFormFile? file)
    {
        var po = await LoadPoAsync(f.PurchaseOrderId);
        if (po == null) return NotFound();
        var (inv, match, err) = await InvoiceFactory.CreateAsync(Db, _fin, po, f, file, Settings.MaxAttachmentMb * 1024 * 1024, Me, null);
        if (err != null) { TempData["Error"] = err; return RedirectToAction(nameof(Create), new { poId = po.Id }); }
        await Db.SaveChangesAsync();
        Log(DocType.Invoice, inv!.Id, $"Nhận hóa đơn {inv.Series} {inv.InvoiceNo} – {inv.Total.ToMoney()} đ");
        Log(DocType.Invoice, inv.Id, match!.Ok ? "Đối chiếu 3 bên: khớp" : "Đối chiếu 3 bên: lệch", inv.MatchNote);
        Log(DocType.PO, po.Id, $"Nhận hóa đơn {inv.Number} ({inv.InvoiceNo})");
        await Db.SaveChangesAsync();
        TempData[match.Ok ? "Success" : "Error"] = match.Ok ? I18n.T("Đã nhận {0} – khớp 3 bên.", inv.Number) : I18n.T("Đã nhận {0} – LỆCH: {1}", inv.Number, inv.MatchNote);
        return RedirectToAction(nameof(Details), new { id = inv.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var inv = await Db.Invoices.AsNoTracking().Include(i => i.Vendor).Include(i => i.CreatedBy).Include(i => i.Lines).ThenInclude(l => l.PurchaseOrderLine)
            .FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return NotFound();
        var po = await LoadPoAsync(inv.PurchaseOrderId);
        ViewBag.Po = po;
        ViewBag.Match = await _fin.MatchAsync(inv, po!);
        ViewBag.Payments = await Db.Payments.AsNoTracking().Include(p => p.ApprovedBy).Where(p => p.InvoiceId == id).OrderBy(p => p.RequestedAt).ToListAsync();
        ViewBag.Proposal = inv.Status == InvoiceStatus.Matched ? await _fin.ProposeAsync(inv, po!) : null;
        ViewBag.History = await HistoryAsync(DocType.Invoice, id);
        ViewData["Title"] = $"Hóa đơn {inv.Number}";
        return View(inv);
    }

    public async Task<IActionResult> File(int id)
    {
        var inv = await Db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        return inv?.FileData == null ? NotFound() : File(inv.FileData, inv.ContentType ?? "application/octet-stream", inv.FileName ?? "hoa-don");
    }

    /// <summary>Đối chiếu lại (sau khi nhận thêm hàng)</summary>
    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Rematch(int id)
    {
        var inv = await Db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return NotFound();
        if (inv.Status is not (InvoiceStatus.Received or InvoiceStatus.Exception or InvoiceStatus.Matched)) return BadRequest();
        var m = await _fin.MatchAsync(inv, (await LoadPoAsync(inv.PurchaseOrderId))!);
        inv.Status = m.Ok ? InvoiceStatus.Matched : InvoiceStatus.Exception; inv.MatchNote = m.Summary.Length > 2000 ? m.Summary[..2000] : m.Summary;
        Log(DocType.Invoice, id, m.Ok ? "Đối chiếu lại: khớp" : "Đối chiếu lại: vẫn lệch", inv.MatchNote);
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Giám đốc chấp nhận hóa đơn lệch (có lý do) hoặc kế toán / Giám đốc từ chối</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Invoice)]
    public async Task<IActionResult> Decide(int id, bool accept, string? reason)
    {
        var inv = await Db.Invoices.FindAsync(id);
        if (inv == null) return NotFound();
        if (accept && !(User.IsAdmin() || User.IsInRole(AppRoles.Director))) return Forbid();
        if (!accept && !(User.IsInRole(AppRoles.Accountant) || User.IsAdmin() || User.IsInRole(AppRoles.Director))) return Forbid();
        if (inv.Status is not (InvoiceStatus.Exception or InvoiceStatus.Matched or InvoiceStatus.Received)) return BadRequest();
        if (string.IsNullOrWhiteSpace(reason)) { TempData["Error"] = I18n.T("Nhập lý do."); return RedirectToAction(nameof(Details), new { id }); }
        if (accept && inv.Status != InvoiceStatus.Exception) return BadRequest();
        inv.Status = accept ? InvoiceStatus.Matched : InvoiceStatus.Rejected;
        inv.DecisionNote = reason.Trim();
        Log(DocType.Invoice, id, accept ? "Giám đốc chấp nhận hóa đơn lệch" : "Từ chối hóa đơn", reason.Trim());
        await Db.SaveChangesAsync();
        TempData["Success"] = accept ? I18n.T("Đã chấp nhận hóa đơn – có thể đề nghị thanh toán.") : I18n.T("Đã từ chối hóa đơn.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestPayment(int id, string? note)
    {
        var inv = await Db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id);
        if (inv == null) return NotFound();
        if (inv.Status != InvoiceStatus.Matched) { TempData["Error"] = I18n.T("Chỉ đề nghị thanh toán hóa đơn đã khớp (hoặc đã được chấp nhận)."); return RedirectToAction(nameof(Details), new { id }); }
        var po = (await LoadPoAsync(inv.PurchaseOrderId))!;
        var p = await _fin.ProposeAsync(inv, po);
        var pay = new Payment
        {
            Number = await DocNumbers.NextAsync(Db.Payments.Select(x => x.Number), "TT"), Type = PaymentType.Invoice, VendorId = inv.VendorId, InvoiceId = inv.Id,
            ContractId = po.ContractId, Amount = p.Amount, PenaltyAmount = p.Penalty, AdvanceDeduction = p.AdvanceDeduction, NetAmount = p.Net,
            Note = string.Join(" · ", new[] { $"Thanh toán hóa đơn {inv.Series} {inv.InvoiceNo} – {po.Number}", p.Note, note?.Trim() }.Where(s => !string.IsNullOrEmpty(s))),
            RequestedById = Me, RequestedAt = DateTime.Now
        };
        if (pay.Note!.Length > 1000) pay.Note = pay.Note[..1000];
        Db.Payments.Add(pay);
        inv.Status = InvoiceStatus.PaymentRequested;
        await Db.SaveChangesAsync();
        Log(DocType.Payment, pay.Id, $"Đề nghị thanh toán {pay.NetAmount.ToMoney()} đ", pay.Note);
        Log(DocType.Invoice, inv.Id, $"Lập đề nghị thanh toán {pay.Number}");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lập {0}: thực chi {1} đ – chờ Giám đốc duyệt.", pay.Number, pay.NetAmount.ToMoney());
        return RedirectToAction("Details", "Payments", new { id = pay.Id });
    }
}

/// <summary>Đề nghị thanh toán: Giám đốc duyệt, kế toán ghi nhận đã chi</summary>
[Authorize(Roles = AppRoles.Finance + "," + AppRoles.Director + "," + AppRoles.Buyer)]
public class PaymentsController : ProcControllerBase
{
    public PaymentsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(PaymentStatus? status)
    {
        var q = Db.Payments.AsNoTracking().Include(p => p.Vendor).Include(p => p.Invoice).Include(p => p.Contract).AsQueryable();
        if (status.HasValue) q = q.Where(p => p.Status == status);
        ViewBag.Status = status;
        ViewBag.Counts = await Db.Payments.GroupBy(p => p.Status).Select(g => new { g.Key, N = g.Count(), S = g.Sum(x => x.NetAmount) }).ToDictionaryAsync(x => x.Key, x => (x.N, x.S));
        ViewData["Title"] = "Thanh toán";
        return View(await q.OrderByDescending(p => p.RequestedAt).Take(300).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await Db.Payments.AsNoTracking().Include(x => x.Vendor).Include(x => x.Invoice).Include(x => x.Contract).Include(x => x.Milestone)
            .Include(x => x.RequestedBy).Include(x => x.ApprovedBy).Include(x => x.PaidBy).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.Payment, id);
        ViewData["Title"] = $"Đề nghị thanh toán {p.Number}";
        return View(p);
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Payment)]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment)
    {
        var p = await Db.Payments.Include(x => x.Invoice).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PaymentStatus.Requested) return BadRequest();
        if (p.RequestedById == Me && !User.IsAdmin()) return Forbid(); // không tự đề nghị – tự duyệt
        if (!approve && string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = I18n.T("Nhập lý do từ chối."); return RedirectToAction(nameof(Details), new { id }); }
        if (approve) { p.Status = PaymentStatus.Approved; p.ApprovedById = Me; p.ApprovedAt = DateTime.Now; }
        else { p.Status = PaymentStatus.Rejected; p.RejectReason = comment!.Trim(); if (p.Invoice != null) p.Invoice.Status = InvoiceStatus.Matched; }
        Log(DocType.Payment, id, approve ? "Giám đốc duyệt thanh toán" : "Giám đốc từ chối thanh toán", comment);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? I18n.T("Đã duyệt {0} – chuyển kế toán chi.", p.Number) : I18n.T("Đã từ chối {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, DateTime paidAt, string? paymentRef)
    {
        var p = await Db.Payments.Include(x => x.Invoice).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PaymentStatus.Approved) { TempData["Error"] = I18n.T("Chỉ ghi nhận chi cho đề nghị đã được duyệt."); return RedirectToAction(nameof(Details), new { id }); }
        if (string.IsNullOrWhiteSpace(paymentRef)) { TempData["Error"] = I18n.T("Nhập số chứng từ chi / ủy nhiệm chi."); return RedirectToAction(nameof(Details), new { id }); }
        if (paidAt.Date > DateTime.Today) { TempData["Error"] = I18n.T("Ngày chi không được ở tương lai."); return RedirectToAction(nameof(Details), new { id }); }
        p.Status = PaymentStatus.Paid; p.PaidAt = paidAt.Date; p.PaidById = Me; p.PaymentRef = paymentRef.Trim();
        if (p.Invoice != null) p.Invoice.Status = InvoiceStatus.Paid;
        Log(DocType.Payment, id, $"Đã chi {p.NetAmount.ToMoney()} đ", $"UNC {p.PaymentRef} ngày {p.PaidAt:dd/MM/yyyy}");
        if (p.InvoiceId.HasValue) Log(DocType.Invoice, p.InvoiceId.Value, $"Đã thanh toán theo {p.Number}");
        if (p.ContractId.HasValue) Log(DocType.Contract, p.ContractId.Value, $"Thanh toán {p.Type.GetDisplayName().ToLower()} {p.Number}: {p.NetAmount.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã ghi nhận chi {0}.", p.Number);
        return RedirectToAction(nameof(Details), new { id });
    }
}
