using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Cuối vòng đời hợp đồng: giữ lại bảo hành, lộ trình thu hồi tạm ứng, điều chỉnh giá, quyết toán, hoàn trả giữ lại, thanh lý</summary>
public partial class ContractsController
{
    private IActionResult ToSettlement(int id, string? error = null, string? ok = null)
    {
        if (error != null) TempData["Error"] = error;
        if (ok != null) TempData["Success"] = ok;
        return RedirectToAction(nameof(Settlement), new { id });
    }

    // ------------------------------------------------------------------ điều khoản giữ lại & thu hồi tạm ứng
    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Accountant)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoneyTerms(int id, decimal retentionPercent, decimal retentionCap, decimal advanceStart, decimal advanceEnd, DateTime? warrantyEnd, string? back)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        IActionResult Back(string? e = null) { if (e != null) TempData["Error"] = e; return back == "settlement" ? RedirectToAction(nameof(Settlement), new { id }) : RedirectToAction(nameof(Details), new { id }); }
        if (c.Status is ContractStatus.Liquidated or ContractStatus.Terminated) return Back(I18n.T("Hợp đồng đã đóng."));
        if (retentionPercent is < 0 or > 100 || retentionCap is < 0 or > 100) return Back(I18n.T("Tỷ lệ giữ lại phải từ 0 đến 100%."));
        if (advanceStart is < 0 or > 100 || advanceEnd is < 0 or > 100 || advanceEnd < advanceStart) return Back(I18n.T("Lộ trình thu hồi tạm ứng: 0 ≤ bắt đầu ≤ thu hồi hết ≤ 100%."));
        c.RetentionPercent = retentionPercent; c.RetentionCapPercent = retentionCap; c.AdvanceRecoveryStartPercent = advanceStart; c.AdvanceRecoveryEndPercent = advanceEnd;
        c.WarrantyEndDate = warrantyEnd?.Date;
        Log(DocType.Contract, id, I18n.T("Cập nhật điều khoản thanh toán: giữ lại {0}% mỗi đợt (tối đa {1}%), thu hồi tạm ứng từ {2}% đến {3}% giá trị lũy kế", retentionPercent.ToString("0.##"), retentionCap.ToString("0.##"), advanceStart.ToString("0.##"), advanceEnd.ToString("0.##"))
            + (c.WarrantyEndDate.HasValue ? I18n.T(", bảo hành đến {0}", c.WarrantyEndDate.ToDate()) : ""));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu điều khoản giữ lại và thu hồi tạm ứng – áp dụng cho các đề nghị thanh toán tiếp theo.");
        return Back();
    }

    // ------------------------------------------------------------------ trang quyết toán & thanh lý
    public async Task<IActionResult> Settlement(int id)
    {
        var c = await Db.Contracts.Include(x => x.Vendor).Include(x => x.Guarantees).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        var payments = await Db.Payments.AsNoTracking().Where(p => p.ContractId == id).OrderBy(p => p.RequestedAt).ToListAsync();
        var ledger = ContractClose.Ledger(c, payments);
        var settlements = await Db.ContractSettlements.AsNoTracking().Include(s => s.CreatedBy).Include(s => s.DecidedBy).Where(s => s.ContractId == id).OrderByDescending(s => s.CreatedAt).ToListAsync();
        var current = settlements.FirstOrDefault(s => s.Status != SettlementStatus.Rejected);
        ViewBag.Payments = payments; ViewBag.Ledger = ledger; ViewBag.Settlements = settlements; ViewBag.Current = current;
        ViewBag.Draft = current == null ? await ContractClose.DraftAsync(Db, c, null, 0) : null;
        ViewBag.Checks = ContractClose.LiquidationChecks(c, current, ledger, c.Guarantees);
        ViewBag.RetentionProblem = ContractClose.RetentionReleaseProblem(c, ledger, c.Guarantees, DateTime.Today);
        ViewBag.Adjustments = await Db.ContractPriceAdjustments.AsNoTracking().Where(a => a.ContractId == id).ToListAsync();
        ViewData["Title"] = I18n.T("Quyết toán & thanh lý {0}", c.Number);
        return View(c);
    }

    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Accountant)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSettlement(int id, decimal? finalValue, decimal otherDeduction, string? otherNote, string? note, DateTime? warrantyEnd)
    {
        var c = await Db.Contracts.FindAsync(id);
        if (c == null) return NotFound();
        if (c.Status is not (ContractStatus.Active or ContractStatus.Completed)) return ToSettlement(id, I18n.T("Hợp đồng đã chấm dứt / thanh lý."));
        if (await Db.ContractSettlements.AnyAsync(s => s.ContractId == id && s.Status != SettlementStatus.Rejected)) return ToSettlement(id, I18n.T("Hợp đồng đã có quyết toán đang chờ duyệt / đã duyệt."));
        if (await Db.Payments.AnyAsync(p => p.ContractId == id && (p.Status == PaymentStatus.Requested || p.Status == PaymentStatus.Approved)))
            return ToSettlement(id, I18n.T("Còn đề nghị thanh toán chưa chi – hoàn tất (chi / từ chối) trước khi quyết toán."));
        if (await Db.ContractPriceAdjustments.AnyAsync(a => a.ContractId == id && a.Status == SettlementStatus.Pending))
            return ToSettlement(id, I18n.T("Còn điều chỉnh giá chờ duyệt."));
        if (finalValue is < 0) return ToSettlement(id, I18n.T("Giá trị quyết toán không hợp lệ."));
        if (otherDeduction > 0 && string.IsNullOrWhiteSpace(otherNote)) return ToSettlement(id, I18n.T("Nêu nội dung khoản giảm trừ."));
        if (warrantyEnd.HasValue) c.WarrantyEndDate = warrantyEnd.Value.Date;
        var s = await ContractClose.DraftAsync(Db, c, finalValue, otherDeduction);
        s.Number = await DocNumbers.NextAsync(Db.ContractSettlements.Select(x => x.Number), "QT");
        s.OtherDeductionNote = string.IsNullOrWhiteSpace(otherNote) ? null : otherNote.Trim();
        s.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        s.CreatedById = Me; s.CreatedAt = DateTime.Now; s.SettlementDate = DateTime.Today;
        Db.ContractSettlements.Add(s);
        Log(DocType.Contract, id, I18n.T("Trình quyết toán {0}: giá trị {1} đ (gồm VAT), còn phải thanh toán {2} đ", s.Number, s.FinalGross.ToMoney(), s.Balance.ToMoney()), s.Note);
        await Db.SaveChangesAsync();
        return ToSettlement(id, ok: I18n.T("Đã lập quyết toán {0} – chờ Giám đốc duyệt.", s.Number));
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Contract)]
    public async Task<IActionResult> DecideSettlement(int id, int settlementId, bool approve, string? comment)
    {
        var c = await Db.Contracts.FindAsync(id);
        var s = await Db.ContractSettlements.FirstOrDefaultAsync(x => x.Id == settlementId && x.ContractId == id && x.Status == SettlementStatus.Pending);
        if (c == null || s == null) return NotFound();
        if (!approve && string.IsNullOrWhiteSpace(comment)) return ToSettlement(id, I18n.T("Nhập lý do trả lại."));
        if (approve)
        {
            // số liệu tiền có thể đã thay đổi sau khi lập (chi thêm, phạt…) → buộc lập lại
            var fresh = await ContractClose.DraftAsync(Db, c, s.FinalValue, s.OtherDeduction);
            if (fresh.PaidTotal != s.PaidTotal || fresh.PenaltyTotal != s.PenaltyTotal || fresh.PriceAdjustment != s.PriceAdjustment || fresh.RetentionHeld != s.RetentionHeld)
                return ToSettlement(id, I18n.T("Số liệu thanh toán đã thay đổi sau khi lập quyết toán – trả lại để lập lại."));
            s.Status = SettlementStatus.Approved;
            if (c.Status == ContractStatus.Active) c.Status = ContractStatus.Completed;
            if (s.WarrantyEndDate.HasValue) c.WarrantyEndDate = s.WarrantyEndDate;
            if (s.PayNow > 0)
            {
                var pay = new Payment
                {
                    Number = await DocNumbers.NextAsync(Db.Payments.Select(x => x.Number), "TT"), Type = PaymentType.FinalSettlement, VendorId = c.VendorId, ContractId = id,
                    SettlementId = s.Id, Amount = s.PayNow, NetAmount = s.PayNow, RequestedById = Me, RequestedAt = DateTime.Now,
                    Note = I18n.T("Thanh toán theo quyết toán {0} – HĐ {1}", s.Number, c.Number) + (s.RetentionHeld > 0 ? " · " + I18n.T("giữ lại {0} đ đến hết bảo hành", s.RetentionHeld.ToMoney()) : "")
                };
                Db.Payments.Add(pay);
                await Db.SaveChangesAsync();
                Log(DocType.Payment, pay.Id, I18n.T("Đề nghị thanh toán {0} đ", pay.NetAmount.ToMoney()), pay.Note);
            }
            else if (s.PayNow < 0) Log(DocType.Contract, id, I18n.T("Nhà thầu phải hoàn trả {0} đ theo quyết toán {1}", (-s.PayNow).ToMoney(), s.Number));
        }
        else s.Status = SettlementStatus.Rejected;
        s.DecidedById = Me; s.DecidedAt = DateTime.Now; s.DecisionNote = comment?.Trim();
        Log(DocType.Contract, id, approve ? I18n.T("Giám đốc duyệt quyết toán {0}", s.Number) : I18n.T("Giám đốc trả lại quyết toán {0}", s.Number), comment);
        await Db.SaveChangesAsync();
        return ToSettlement(id, ok: approve ? I18n.T("Đã duyệt quyết toán {0}.", s.Number) + (s.PayNow > 0 ? " " + I18n.T("Đã lập đề nghị thanh toán quyết toán {0} đ.", s.PayNow.ToMoney()) : "") : I18n.T("Đã trả lại quyết toán {0}.", s.Number));
    }

    /// <summary>Đề nghị hoàn trả tiền giữ lại (hết bảo hành hoặc có bảo lãnh bảo hành thay thế)</summary>
    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestRetentionRelease(int id)
    {
        var c = await Db.Contracts.Include(x => x.Guarantees).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        var ledger = await ContractClose.LedgerAsync(Db, c);
        if (await Db.Payments.AnyAsync(p => p.ContractId == id && p.Type == PaymentType.RetentionRelease && p.Status != PaymentStatus.Paid && p.Status != PaymentStatus.Rejected))
            return ToSettlement(id, I18n.T("Đã có đề nghị hoàn trả tiền giữ lại đang chờ xử lý."));
        var problem = ContractClose.RetentionReleaseProblem(c, ledger, c.Guarantees, DateTime.Today);
        if (problem != null) return ToSettlement(id, problem);
        var amount = ledger.RetentionOutstanding;
        var pay = new Payment
        {
            Number = await DocNumbers.NextAsync(Db.Payments.Select(x => x.Number), "TT"), Type = PaymentType.RetentionRelease, VendorId = c.VendorId, ContractId = id,
            Amount = amount, NetAmount = amount, RequestedById = Me, RequestedAt = DateTime.Now,
            Note = I18n.T("Hoàn trả tiền giữ lại bảo hành – HĐ {0}", c.Number) + (c.WarrantyEndDate.HasValue && DateTime.Today > c.WarrantyEndDate ? " · " + I18n.T("hết bảo hành {0}", c.WarrantyEndDate.ToDate()) : " · " + I18n.T("thay bằng bảo lãnh bảo hành"))
        };
        Db.Payments.Add(pay);
        await Db.SaveChangesAsync();
        Log(DocType.Payment, pay.Id, I18n.T("Đề nghị thanh toán {0} đ", pay.NetAmount.ToMoney()), pay.Note);
        Log(DocType.Contract, id, I18n.T("Lập đề nghị hoàn trả tiền giữ lại {0}: {1} đ", pay.Number, amount.ToMoney()));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lập {0} – chờ Giám đốc duyệt.", pay.Number);
        return RedirectToAction("Details", "Payments", new { id = pay.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Liquidate(int id, string? note)
    {
        var c = await Db.Contracts.Include(x => x.Guarantees).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        if (c.Status is ContractStatus.Liquidated or ContractStatus.Terminated) return ToSettlement(id, I18n.T("Hợp đồng đã đóng."));
        var s = await Db.ContractSettlements.FirstOrDefaultAsync(x => x.ContractId == id && x.Status == SettlementStatus.Approved);
        var ledger = await ContractClose.LedgerAsync(Db, c);
        var fails = ContractClose.LiquidationChecks(c, s, ledger, c.Guarantees).Where(x => !x.Ok).Select(x => x.Text).ToList();
        if (fails.Count > 0) return ToSettlement(id, I18n.T("Chưa thanh lý được: ") + string.Join("; ", fails) + ".");
        c.Status = ContractStatus.Liquidated;
        s!.LiquidatedAt = DateTime.Now; s.LiquidationNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Log(DocType.Contract, id, I18n.T("Thanh lý hợp đồng – hai bên hoàn thành mọi nghĩa vụ"), s.LiquidationNote);
        await Db.SaveChangesAsync();
        return ToSettlement(id, ok: I18n.T("Đã thanh lý hợp đồng {0}.", c.Number));
    }

    /// <summary>Biên bản quyết toán / thanh lý (in)</summary>
    public async Task<IActionResult> SettlementMinutes(int id, int settlementId)
    {
        var c = await Db.Contracts.Include(x => x.Vendor).Include(x => x.Tender).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        var s = await Db.ContractSettlements.Include(x => x.CreatedBy).Include(x => x.DecidedBy).AsNoTracking().FirstOrDefaultAsync(x => x.Id == settlementId && x.ContractId == id);
        if (c == null || s == null) return NotFound();
        ViewBag.Settlement = s;
        ViewBag.Amendments = await Db.ContractAmendments.AsNoTracking().Where(a => a.ContractId == id && a.Status == AmendmentStatus.Approved).OrderBy(a => a.CreatedAt).ToListAsync();
        ViewData["Title"] = I18n.T("Biên bản quyết toán {0}", s.Number);
        return View(c);
    }

    // ------------------------------------------------------------------ điều chỉnh giá theo chỉ số
    public async Task<IActionResult> PriceAdjustment(int id)
    {
        var c = await Db.Contracts.Include(x => x.Vendor).Include(x => x.Tender).Include(x => x.PriceIndices).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        c.PriceIndices = c.PriceIndices.OrderBy(i => i.SortOrder).ToList();
        ViewBag.Adjustments = await Db.ContractPriceAdjustments.AsNoTracking().Include(a => a.Acceptance).Include(a => a.CreatedBy).Where(a => a.ContractId == id).OrderBy(a => a.CreatedAt).ToListAsync();
        ViewBag.Acceptances = await Db.BoqAcceptances.AsNoTracking().Where(a => a.ContractId == id && a.Status == AcceptanceStatus.Confirmed).OrderBy(a => a.AcceptanceDate).ToListAsync();
        ViewBag.AdjPaid = (await Db.Payments.AsNoTracking().Where(p => p.ContractId == id && p.PriceAdjustmentId != null && p.Status != PaymentStatus.Rejected)
            .Select(p => new { p.PriceAdjustmentId, p.Id, p.Number }).ToListAsync()).ToDictionary(x => x.PriceAdjustmentId!.Value, x => (x.Id, x.Number));
        ViewData["Title"] = I18n.T("Điều chỉnh giá {0}", c.Number);
        return View(c);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveIndices(int id, List<string?>? name, List<decimal?>? weight, List<decimal?>? baseIndex)
    {
        var c = await Db.Contracts.Include(x => x.PriceIndices).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        IActionResult Back(string? e = null, string? ok = null) { if (e != null) TempData["Error"] = e; if (ok != null) TempData["Success"] = ok; return RedirectToAction(nameof(PriceAdjustment), new { id }); }
        if (await Db.ContractPriceAdjustments.AnyAsync(a => a.ContractId == id && a.Status != SettlementStatus.Rejected))
            return Back(I18n.T("Đã có điều chỉnh giá – không đổi công thức (lập phụ lục nếu cần thay đổi)."));
        var rows = new List<ContractPriceIndex>();
        for (var i = 0; i < (name?.Count ?? 0); i++)
        {
            if (string.IsNullOrWhiteSpace(name![i])) continue;
            rows.Add(new ContractPriceIndex { ContractId = id, Name = name[i]!.Trim(), Weight = weight?.ElementAtOrDefault(i) ?? 0, BaseIndex = baseIndex?.ElementAtOrDefault(i) ?? 0, SortOrder = rows.Count + 1 });
        }
        var errors = ContractClose.ValidateIndices(rows);
        if (errors.Count > 0) return Back(string.Join(" ", errors));
        Db.ContractPriceIndices.RemoveRange(c.PriceIndices);
        Db.ContractPriceIndices.AddRange(rows);
        Log(DocType.Contract, id, I18n.T("Thiết lập công thức điều chỉnh giá: phần cố định {0}", ContractClose.FixedShare(rows).ToString("0.####")),
            string.Join("; ", rows.Select(r => $"{r.Name} b={r.Weight:0.####}, I0={r.BaseIndex:0.####}")));
        await Db.SaveChangesAsync();
        return Back(ok: I18n.T("Đã lưu công thức điều chỉnh giá."));
    }

    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Accountant)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPriceAdjustment(int id, string? period, int? acceptanceId, decimal? baseAmount, Dictionary<int, decimal>? idx, string? note)
    {
        var c = await Db.Contracts.Include(x => x.PriceIndices).FirstOrDefaultAsync(x => x.Id == id);
        if (c == null) return NotFound();
        IActionResult Back(string? e = null, string? ok = null) { if (e != null) TempData["Error"] = e; if (ok != null) TempData["Success"] = ok; return RedirectToAction(nameof(PriceAdjustment), new { id }); }
        if (c.Status is not (ContractStatus.Active or ContractStatus.Completed)) return Back(I18n.T("Hợp đồng đã đóng."));
        if (await Db.ContractSettlements.AnyAsync(s => s.ContractId == id && s.Status != SettlementStatus.Rejected)) return Back(I18n.T("Hợp đồng đã quyết toán."));
        if (c.PriceIndices.Count == 0) return Back(I18n.T("Thiết lập công thức điều chỉnh giá trước."));
        if (string.IsNullOrWhiteSpace(period)) return Back(I18n.T("Nhập kỳ điều chỉnh."));
        decimal amountBase;
        if (acceptanceId.HasValue)
        {
            var a = await Db.BoqAcceptances.FirstOrDefaultAsync(x => x.Id == acceptanceId && x.ContractId == id && x.Status == AcceptanceStatus.Confirmed);
            if (a == null) return Back(I18n.T("Biên bản nghiệm thu không hợp lệ."));
            if (await Db.ContractPriceAdjustments.AnyAsync(x => x.AcceptanceId == a.Id && x.Status != SettlementStatus.Rejected)) return Back(I18n.T("Biên bản nghiệm thu này đã được điều chỉnh giá."));
            amountBase = a.SubTotal;
        }
        else amountBase = Math.Round(baseAmount ?? 0, 0);
        if (amountBase <= 0) return Back(I18n.T("Giá trị kỳ điều chỉnh phải > 0."));
        idx ??= new();
        if (c.PriceIndices.Any(i => !idx.TryGetValue(i.Id, out var v) || v <= 0)) return Back(I18n.T("Nhập chỉ số kỳ điều chỉnh (> 0) cho mọi thành phần."));
        var factor = ContractClose.Factor(c.PriceIndices, idx);
        var adj = new ContractPriceAdjustment
        {
            Number = await DocNumbers.NextAsync(Db.ContractPriceAdjustments.Select(x => x.Number), "DCG"), ContractId = id, Period = period.Trim(), AcceptanceId = acceptanceId,
            BaseAmount = amountBase, Factor = factor, Amount = Math.Round(amountBase * (factor - 1), 0, MidpointRounding.AwayFromZero),
            Indices = ContractClose.IndicesText(c.PriceIndices.OrderBy(i => i.SortOrder), idx), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedById = Me, CreatedAt = DateTime.Now
        };
        Db.ContractPriceAdjustments.Add(adj);
        Log(DocType.Contract, id, I18n.T("Trình điều chỉnh giá {0} kỳ {1}: hệ số {2}, {3} đ (trước VAT)", adj.Number, adj.Period, adj.Factor.ToString("0.######"), adj.Amount.ToMoney()), adj.Indices);
        await Db.SaveChangesAsync();
        return Back(ok: I18n.T("Đã lập điều chỉnh giá {0} – chờ Giám đốc duyệt.", adj.Number));
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Contract)]
    public async Task<IActionResult> DecidePriceAdjustment(int id, int adjustmentId, bool approve, string? comment)
    {
        var a = await Db.ContractPriceAdjustments.FirstOrDefaultAsync(x => x.Id == adjustmentId && x.ContractId == id && x.Status == SettlementStatus.Pending);
        if (a == null) return NotFound();
        if (!approve && string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = I18n.T("Nhập lý do trả lại."); return RedirectToAction(nameof(PriceAdjustment), new { id }); }
        a.Status = approve ? SettlementStatus.Approved : SettlementStatus.Rejected; a.DecidedById = Me; a.DecidedAt = DateTime.Now; a.DecisionNote = comment?.Trim();
        Log(DocType.Contract, id, approve ? I18n.T("Giám đốc duyệt điều chỉnh giá {0}: {1} đ", a.Number, a.Amount.ToMoney()) : I18n.T("Giám đốc trả lại điều chỉnh giá {0}", a.Number), comment);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? I18n.T("Đã duyệt {0}.", a.Number) : I18n.T("Đã trả lại {0}.", a.Number);
        return RedirectToAction(nameof(PriceAdjustment), new { id });
    }

    /// <summary>Đề nghị thanh toán phần điều chỉnh tăng (gồm VAT); phần giảm được trừ khi quyết toán</summary>
    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestAdjustmentPayment(int id, int adjustmentId)
    {
        var c = await Db.Contracts.FindAsync(id);
        var a = await Db.ContractPriceAdjustments.FirstOrDefaultAsync(x => x.Id == adjustmentId && x.ContractId == id);
        if (c == null || a == null) return NotFound();
        IActionResult Back(string e) { TempData["Error"] = e; return RedirectToAction(nameof(PriceAdjustment), new { id }); }
        if (a.Status != SettlementStatus.Approved || a.Amount <= 0) return Back(I18n.T("Chỉ thanh toán điều chỉnh tăng đã được duyệt."));
        if (c.Status is not (ContractStatus.Active or ContractStatus.Completed)) return Back(I18n.T("Hợp đồng đã đóng."));
        if (await Db.Payments.AnyAsync(p => p.PriceAdjustmentId == a.Id && p.Status != PaymentStatus.Rejected)) return Back(I18n.T("Điều chỉnh này đã có đề nghị thanh toán."));
        var gross = ContractClose.Gross(a.Amount, c.VatPercent);
        var pay = new Payment
        {
            Number = await DocNumbers.NextAsync(Db.Payments.Select(x => x.Number), "TT"), Type = PaymentType.PriceAdjustment, VendorId = c.VendorId, ContractId = id,
            PriceAdjustmentId = a.Id, Amount = gross, NetAmount = gross, RequestedById = Me, RequestedAt = DateTime.Now,
            Note = I18n.T("Thanh toán điều chỉnh giá {0} kỳ {1} – HĐ {2} (hệ số {3})", a.Number, a.Period, c.Number, a.Factor.ToString("0.######"))
        };
        Db.Payments.Add(pay);
        await Db.SaveChangesAsync();
        Log(DocType.Payment, pay.Id, I18n.T("Đề nghị thanh toán {0} đ", pay.NetAmount.ToMoney()), pay.Note);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lập {0} – chờ Giám đốc duyệt.", pay.Number);
        return RedirectToAction("Details", "Payments", new { id = pay.Id });
    }
}
