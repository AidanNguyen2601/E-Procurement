using System.Globalization;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Khấu trừ của một đợt thanh toán khối lượng</summary>
public record PaymentDeduction(decimal Advance, decimal Retention, string Note);

/// <summary>Sổ theo dõi tiền của một hợp đồng (gồm VAT)</summary>
public class ContractLedger
{
    public decimal ContractGross { get; set; }
    /// <summary>Giá trị khối lượng đã đề nghị / thanh toán (hóa đơn, đợt, nghiệm thu; chưa trừ khấu trừ)</summary>
    public decimal WorkGross { get; set; }
    public decimal PaidCash { get; set; }
    public decimal PendingCash { get; set; }
    public decimal AdvancePaid { get; set; }
    public decimal AdvanceRecovered { get; set; }
    public decimal Retained { get; set; }
    public decimal RetentionReleased { get; set; }
    public decimal Penalties { get; set; }
    public decimal AdjustmentPaid { get; set; }
    public decimal AdvanceOutstanding => Math.Max(0, AdvancePaid - AdvanceRecovered);
    public decimal RetentionOutstanding => Math.Max(0, Retained - RetentionReleased);
    public bool HasPending { get; set; }
}

public record LiquidationCheck(string Text, bool Ok);

public static class ContractClose
{
    /// <summary>Loại thanh toán tính vào giá trị khối lượng (lũy kế để thu hồi tạm ứng, giữ lại, chặn vượt giá trị HĐ)</summary>
    public static readonly PaymentType[] WorkTypes = { PaymentType.Invoice, PaymentType.Milestone, PaymentType.Acceptance };

    public static decimal Gross(decimal value, decimal vatPercent) => Math.Round(value * (1 + vatPercent / 100m), 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Khấu trừ tạm ứng và giữ lại của một đợt thanh toán khối lượng <paramref name="gross"/> (gồm VAT).
    /// Tạm ứng thu hồi dần khi giá trị lũy kế đi từ X% đến Y% giá trị hợp đồng (X=0, Y=100: theo tỷ lệ tạm ứng / giá trị HĐ mỗi đợt);
    /// khi lũy kế đạt Y% thì thu hồi hết phần còn lại. Giữ lại = % số tiền đợt, tổng không vượt mức tối đa (% giá trị HĐ).
    /// </summary>
    public static PaymentDeduction Deductions(Contract c, decimal gross, ContractLedger l)
    {
        var notes = new List<string>();
        var cg = l.ContractGross > 0 ? l.ContractGross : Gross(c.Value, c.VatPercent);
        decimal adv = 0;
        var outstanding = l.AdvanceOutstanding;
        if (outstanding > 0 && cg > 0 && gross > 0)
        {
            var start = cg * Math.Clamp(c.AdvanceRecoveryStartPercent, 0, 100) / 100m;
            var end = cg * Math.Clamp(c.AdvanceRecoveryEndPercent, 0, 100) / 100m;
            var before = l.WorkGross; var after = l.WorkGross + gross;
            if (end <= start) adv = after >= start ? outstanding : 0;
            else if (after >= end) adv = outstanding;
            else
            {
                decimal Clamp(decimal v) => Math.Min(end, Math.Max(start, v));
                adv = Math.Round(l.AdvancePaid * (Clamp(after) - Clamp(before)) / (end - start), 0, MidpointRounding.AwayFromZero);
            }
            adv = Math.Min(adv, outstanding);
            if (adv > 0) notes.Add(I18n.T("Thu hồi tạm ứng {0} đ (lũy kế {1}% → {2}% giá trị HĐ; thu hồi từ {3}% đến {4}%)", adv.ToMoney(),
                (before * 100 / cg).ToString("0.#"), (after * 100 / cg).ToString("0.#"), c.AdvanceRecoveryStartPercent.ToString("0.#"), c.AdvanceRecoveryEndPercent.ToString("0.#")));
        }
        decimal ret = 0;
        if (c.RetentionPercent > 0 && gross > 0)
        {
            var cap = Math.Round(cg * c.RetentionCapPercent / 100m, 0);
            ret = Math.Min(Math.Round(gross * c.RetentionPercent / 100m, 0, MidpointRounding.AwayFromZero), Math.Max(0, cap - l.Retained));
            if (ret > 0) notes.Add(I18n.T("Giữ lại bảo hành {0}% = {1} đ (lũy kế tối đa {2}% giá trị HĐ)", c.RetentionPercent.ToString("0.#"), ret.ToMoney(), c.RetentionCapPercent.ToString("0.#")));
        }
        if (adv + ret > gross) { adv = Math.Min(adv, gross); ret = Math.Max(0, gross - adv); }
        return new PaymentDeduction(adv, ret, string.Join("; ", notes));
    }

    public static async Task<ContractLedger> LedgerAsync(EProcDbContext db, Contract c, int? excludePaymentId = null)
    {
        var ps = await db.Payments.AsNoTracking().Where(p => p.ContractId == c.Id && p.Status != PaymentStatus.Rejected && p.Id != (excludePaymentId ?? 0)).ToListAsync();
        return Ledger(c, ps);
    }

    public static ContractLedger Ledger(Contract c, IEnumerable<Payment> payments)
    {
        var ps = payments.Where(p => p.Status != PaymentStatus.Rejected).ToList();
        return new ContractLedger
        {
            ContractGross = Gross(c.Value, c.VatPercent),
            WorkGross = ps.Where(p => WorkTypes.Contains(p.Type)).Sum(p => p.Amount),
            PaidCash = ps.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.NetAmount),
            PendingCash = ps.Where(p => p.Status != PaymentStatus.Paid).Sum(p => p.NetAmount),
            HasPending = ps.Any(p => p.Status != PaymentStatus.Paid),
            AdvancePaid = ps.Where(p => p.Type == PaymentType.Advance && p.Status == PaymentStatus.Paid).Sum(p => p.NetAmount),
            AdvanceRecovered = ps.Sum(p => p.AdvanceDeduction),
            Retained = ps.Sum(p => p.RetentionAmount),
            RetentionReleased = ps.Where(p => p.Type == PaymentType.RetentionRelease).Sum(p => p.Amount),
            Penalties = ps.Sum(p => p.PenaltyAmount),
            AdjustmentPaid = ps.Where(p => p.Type == PaymentType.PriceAdjustment).Sum(p => p.Amount)
        };
    }

    /// <summary>
    /// Lập dự thảo quyết toán: giá trị quyết toán mặc định = khối lượng đã nghiệm thu × đơn giá (hợp đồng có BOQ nghiệm thu),
    /// ngược lại = giá trị hợp đồng hiện hành. Còn phải trả = quyết toán + điều chỉnh giá − phạt − giảm trừ khác − đã chi (tiền mặt).
    /// </summary>
    public static async Task<ContractSettlement> DraftAsync(EProcDbContext db, Contract c, decimal? finalValue, decimal otherDeduction)
    {
        var l = await LedgerAsync(db, c);
        var accepted = await db.BoqAcceptances.AsNoTracking().Where(a => a.ContractId == c.Id && a.Status == AcceptanceStatus.Confirmed).SumAsync(a => (decimal?)a.SubTotal) ?? 0;
        var adj = await db.ContractPriceAdjustments.AsNoTracking().Where(a => a.ContractId == c.Id && a.Status == SettlementStatus.Approved).SumAsync(a => (decimal?)a.Amount) ?? 0;
        var s = new ContractSettlement
        {
            ContractId = c.Id, ContractValue = c.Value, VatPercent = c.VatPercent,
            FinalValue = Math.Round(finalValue ?? (accepted > 0 ? accepted : c.Value), 0),
            PriceAdjustment = Gross(adj, c.VatPercent), PenaltyTotal = l.Penalties, OtherDeduction = Math.Max(0, Math.Round(otherDeduction, 0)),
            PaidTotal = l.PaidCash, RetentionHeld = l.RetentionOutstanding, AdvanceOutstanding = l.AdvanceOutstanding, WarrantyEndDate = c.WarrantyEndDate
        };
        s.FinalGross = Gross(s.FinalValue, s.VatPercent);
        s.Balance = s.FinalGross + s.PriceAdjustment - s.PenaltyTotal - s.OtherDeduction - s.PaidTotal;
        return s;
    }

    /// <summary>Điều kiện thanh lý hợp đồng</summary>
    public static List<LiquidationCheck> LiquidationChecks(Contract c, ContractSettlement? s, ContractLedger l, IEnumerable<ContractGuarantee> guarantees)
    {
        var r = new List<LiquidationCheck>
        {
            new(I18n.T("Quyết toán hợp đồng đã được duyệt"), s?.Status == SettlementStatus.Approved),
            new(I18n.T("Không còn đề nghị thanh toán chờ duyệt / chờ chi"), !l.HasPending),
            new(s == null ? I18n.T("Đã thanh toán đủ theo quyết toán") : I18n.T("Đã thanh toán đủ theo quyết toán (còn {0} đ ngoài tiền giữ lại)", Math.Max(0, s.FinalGross + s.PriceAdjustment - s.PenaltyTotal - s.OtherDeduction - l.PaidCash - l.RetentionOutstanding).ToMoney()),
                s != null && s.FinalGross + s.PriceAdjustment - s.PenaltyTotal - s.OtherDeduction - l.PaidCash - l.RetentionOutstanding <= 0),
            new(I18n.T("Đã thu hồi hết tạm ứng (còn {0} đ)", l.AdvanceOutstanding.ToMoney()), l.AdvanceOutstanding <= 0),
            new(I18n.T("Đã hoàn trả tiền giữ lại (còn {0} đ)", l.RetentionOutstanding.ToMoney()), l.RetentionOutstanding <= 0),
        };
        var open = guarantees.Where(g => g.ReleasedAt == null).ToList();
        r.Add(new(I18n.T("Đã giải tỏa các bảo lãnh (còn {0})", open.Count), open.Count == 0));
        return r;
    }

    /// <summary>Hoàn trả tiền giữ lại: khi hết hạn bảo hành, hoặc nhà thầu nộp bảo lãnh bảo hành còn hiệu lực đủ giá trị thay thế</summary>
    public static string? RetentionReleaseProblem(Contract c, ContractLedger l, IEnumerable<ContractGuarantee> guarantees, DateTime today)
    {
        if (l.RetentionOutstanding <= 0) return I18n.T("Không còn tiền giữ lại.");
        if (c.Status == ContractStatus.Active) return I18n.T("Hợp đồng chưa hoàn thành – hoàn trả tiền giữ lại sau khi hoàn thành / quyết toán.");
        var cover = guarantees.Where(g => g.Type == GuaranteeType.Warranty && g.ReleasedAt == null && g.ExpiryDate >= today).Sum(g => g.Amount);
        if (c.WarrantyEndDate.HasValue && today > c.WarrantyEndDate.Value.Date) return null;
        if (cover >= l.RetentionOutstanding) return null;
        return c.WarrantyEndDate.HasValue
            ? I18n.T("Chưa hết hạn bảo hành ({0}) và bảo lãnh bảo hành còn hiệu lực ({1} đ) chưa đủ thay thế tiền giữ lại {2} đ.", c.WarrantyEndDate.ToDate(), cover.ToMoney(), l.RetentionOutstanding.ToMoney())
            : I18n.T("Nhập ngày hết hạn bảo hành, hoặc tiếp nhận bảo lãnh bảo hành đủ giá trị ({0} đ) để hoàn trả tiền giữ lại.", l.RetentionOutstanding.ToMoney());
    }

    // ------------------------------------------------------------------ điều chỉnh giá theo chỉ số
    /// <summary>Phần cố định a = 1 − Σ tỷ trọng</summary>
    public static decimal FixedShare(IEnumerable<ContractPriceIndex> idx) => 1 - idx.Sum(i => i.Weight);

    public static List<string> ValidateIndices(IList<ContractPriceIndex> idx)
    {
        var e = new List<string>();
        if (idx.Any(i => i.Weight <= 0 || i.Weight > 1)) e.Add(I18n.T("Tỷ trọng mỗi thành phần phải trong khoảng (0; 1]."));
        if (idx.Any(i => i.BaseIndex <= 0)) e.Add(I18n.T("Chỉ số gốc phải > 0."));
        if (FixedShare(idx) < 0) e.Add(I18n.T("Tổng tỷ trọng các thành phần vượt 1."));
        if (idx.Select(i => i.Name.Trim().ToLowerInvariant()).Distinct().Count() != idx.Count) e.Add(I18n.T("Tên thành phần bị trùng."));
        return e;
    }

    /// <summary>Hệ số điều chỉnh = a + Σ bi × (chỉ số kỳ / chỉ số gốc)</summary>
    public static decimal Factor(IEnumerable<ContractPriceIndex> idx, IReadOnlyDictionary<int, decimal> current)
    {
        var list = idx.ToList();
        var f = FixedShare(list);
        foreach (var i in list) f += i.Weight * (current.TryGetValue(i.Id, out var v) && v > 0 ? v / i.BaseIndex : 1);
        return Math.Round(f, 6);
    }

    public static string IndicesText(IEnumerable<ContractPriceIndex> idx, IReadOnlyDictionary<int, decimal> current) =>
        string.Join("; ", idx.Select(i => $"{i.Name}={(current.TryGetValue(i.Id, out var v) ? v : i.BaseIndex).ToString("0.####", CultureInfo.InvariantCulture)}/{i.BaseIndex.ToString("0.####", CultureInfo.InvariantCulture)}"));
}
