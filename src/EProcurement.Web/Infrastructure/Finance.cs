using System.Text.RegularExpressions;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Một dòng kết quả đối chiếu 3 bên (PO – phiếu nhận – hóa đơn)</summary>
public record MatchLine(PurchaseOrderLine PoLine, decimal Ordered, decimal Received, decimal InvoicedBefore, decimal Invoiced, decimal PoPrice, decimal InvoicePrice, List<string> Issues);

public class MatchResult
{
    public List<MatchLine> Lines { get; } = new();
    public List<string> Issues { get; } = new();
    public bool Ok => Issues.Count == 0 && Lines.All(l => l.Issues.Count == 0);
    public string Summary => Ok ? "Khớp 3 bên: số lượng ≤ đã nhận, đơn giá ≤ PO, thuế GTGT đúng."
        : string.Join("; ", Issues.Concat(Lines.SelectMany(l => l.Issues.Select(i => $"{l.PoLine.Description}: {i}"))));
}

/// <summary>Đề xuất số tiền cho đề nghị thanh toán theo hóa đơn</summary>
public record PaymentProposal(decimal Amount, decimal Penalty, int LateDays, decimal AdvanceDeduction, string Note)
{
    public decimal Net => Amount - Penalty - AdvanceDeduction;
}

/// <summary>Nghiệp vụ hóa đơn / thanh toán: đối chiếu 3 bên, hạn thanh toán, phạt chậm giao, khấu trừ tạm ứng</summary>
public class FinanceService
{
    /// <summary>Sai lệch làm tròn cho phép khi so tiền thuế (đ)</summary>
    public const decimal VatTolerance = 1000;
    private readonly EProcDbContext _db;
    public FinanceService(EProcDbContext db) => _db = db;

    /// <summary>Hạn thanh toán: lấy số ngày trong điều khoản thanh toán của PO ("… 30 ngày …"), mặc định 30 ngày</summary>
    public static DateTime DueDate(DateTime invoiceDate, string? paymentTerms)
    {
        var m = paymentTerms == null ? null : Regex.Match(paymentTerms, @"(\d{1,3})\s*ngày");
        var days = m is { Success: true } && int.TryParse(m.Groups[1].Value, out var d) && d is > 0 and <= 365 ? d : 30;
        return invoiceDate.Date.AddDays(days);
    }

    /// <summary>Đối chiếu hóa đơn với PO (đơn giá, VAT) và phiếu nhận (số lượng lũy kế ≤ đã nhận đạt)</summary>
    public async Task<MatchResult> MatchAsync(Invoice inv, PurchaseOrder po)
    {
        var r = new MatchResult();
        if (inv.VendorId != po.VendorId) r.Issues.Add("Hóa đơn không cùng nhà cung cấp với PO");
        if (po.Status is PoStatus.Cancelled or PoStatus.Draft or PoStatus.PendingApproval) r.Issues.Add($"PO đang ở trạng thái {po.Status.GetDisplayName()}");
        var others = await _db.InvoiceLines.AsNoTracking()
            .Where(l => l.Invoice!.PurchaseOrderId == po.Id && l.InvoiceId != inv.Id && l.Invoice.Status != InvoiceStatus.Rejected)
            .GroupBy(l => l.PurchaseOrderLineId).Select(g => new { g.Key, Q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        foreach (var l in inv.Lines)
        {
            var pl = po.Lines.FirstOrDefault(x => x.Id == l.PurchaseOrderLineId);
            if (pl == null) { r.Issues.Add("Có dòng không thuộc PO"); continue; }
            var before = others.GetValueOrDefault(pl.Id);
            var issues = new List<string>();
            if (before + l.Quantity > pl.ReceivedQuantity) issues.Add($"SL hóa đơn lũy kế {(before + l.Quantity).ToQty()} > SL đã nhận đạt {pl.ReceivedQuantity.ToQty()}");
            if (l.UnitPrice > pl.UnitPrice) issues.Add($"đơn giá {l.UnitPrice.ToMoney()} > giá PO {pl.UnitPrice.ToMoney()}");
            r.Lines.Add(new MatchLine(pl, pl.Quantity, pl.ReceivedQuantity, before, l.Quantity, pl.UnitPrice, l.UnitPrice, issues));
        }
        if (inv.Lines.Count == 0) r.Issues.Add("Hóa đơn chưa có dòng hàng");
        var expectedVat = Math.Round(inv.SubTotal * po.VatPercent / 100m, 0);
        if (Math.Abs(inv.VatAmount - expectedVat) > VatTolerance) r.Issues.Add($"Thuế GTGT {inv.VatAmount.ToMoney()} ≠ {po.VatPercent:0.#}% × tiền hàng ({expectedVat.ToMoney()})");
        if (inv.Total != inv.SubTotal + inv.VatAmount) r.Issues.Add("Tổng thanh toán ≠ tiền hàng + thuế");
        return r;
    }

    /// <summary>
    /// Đề xuất thanh toán hóa đơn: phạt chậm giao theo hợp đồng (số ngày trễ của lần nhận hàng cuối so với hạn giao PO,
    /// % / ngày × tiền hàng, tối đa mức trần) và khấu trừ tạm ứng theo tỷ lệ tạm ứng / giá trị hợp đồng cho đến khi thu hồi hết.
    /// </summary>
    public async Task<PaymentProposal> ProposeAsync(Invoice inv, PurchaseOrder po)
    {
        decimal penalty = 0, advance = 0; var late = 0; var notes = new List<string>();
        var contract = po.ContractId.HasValue ? await _db.Contracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == po.ContractId) : null;
        if (contract != null)
        {
            var lastReceipt = await _db.GoodsReceipts.Where(g => g.PurchaseOrderId == po.Id).MaxAsync(g => (DateTime?)g.ReceiptDate);
            var promised = po.DeliveryDate.Date;
            if (lastReceipt.HasValue && lastReceipt.Value.Date > promised)
            {
                late = (lastReceipt.Value.Date - promised).Days;
                var pct = Math.Min(contract.LatePenaltyCapPercent, contract.LatePenaltyPercentPerDay * late);
                penalty = Math.Round(inv.SubTotal * pct / 100m, 0);
                if (penalty > 0) notes.Add($"Phạt chậm giao {late} ngày × {contract.LatePenaltyPercentPerDay:0.###}%/ngày (tối đa {contract.LatePenaltyCapPercent:0.#}%) = {pct:0.###}% tiền hàng");
            }
            var paidAdvance = await _db.Payments.Where(p => p.ContractId == contract.Id && p.Type == PaymentType.Advance && p.Status == PaymentStatus.Paid).SumAsync(p => (decimal?)p.NetAmount) ?? 0;
            var recovered = await _db.Payments.Where(p => p.ContractId == contract.Id && p.Status != PaymentStatus.Rejected && p.InvoiceId != inv.Id).SumAsync(p => (decimal?)p.AdvanceDeduction) ?? 0;
            var outstanding = paidAdvance - recovered;
            if (outstanding > 0)
            {
                var gross = Math.Round(contract.Value * (1 + contract.VatPercent / 100m), 0);
                var rate = gross == 0 ? 0 : paidAdvance / gross;
                advance = Math.Min(outstanding, Math.Round(inv.Total * rate, 0));
                if (advance > 0) notes.Add($"Khấu trừ tạm ứng {rate * 100:0.#}% (còn phải thu hồi {outstanding.ToMoney()} đ)");
            }
        }
        return new PaymentProposal(inv.Total, penalty, late, advance, string.Join("; ", notes));
    }

    /// <summary>Đã thanh toán / đã cam kết của một hợp đồng (gồm VAT)</summary>
    public async Task<(decimal Paid, decimal Requested, decimal AdvancePaid, decimal AdvanceRecovered)> ContractPaymentsAsync(int contractId)
    {
        var ps = await _db.Payments.AsNoTracking().Where(p => p.ContractId == contractId && p.Status != PaymentStatus.Rejected).ToListAsync();
        return (ps.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.NetAmount), ps.Where(p => p.Status != PaymentStatus.Paid).Sum(p => p.NetAmount),
            ps.Where(p => p.Type == PaymentType.Advance && p.Status == PaymentStatus.Paid).Sum(p => p.NetAmount), ps.Sum(p => p.AdvanceDeduction));
    }
}
