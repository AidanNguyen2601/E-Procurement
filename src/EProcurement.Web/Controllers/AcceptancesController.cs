using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class AcceptanceForm
{
    public BoqAcceptance Acceptance { get; set; } = new();
    public Contract Contract { get; set; } = null!;
    public List<BoqProgressRow> Rows { get; set; } = new();
    public Dictionary<int, decimal> Qty { get; set; } = new();
    public Dictionary<int, decimal> Cumulative { get; set; } = new();
}

public class AcceptanceDetails
{
    public BoqAcceptance Acceptance { get; set; } = null!;
    public List<BoqProgressRow> Rows { get; set; } = new();
    public Dictionary<int, decimal> Qty { get; set; } = new();
    public Payment? Payment { get; set; }
    public decimal Gross { get; set; }
    public decimal ContractGross { get; set; }
    public decimal AdvanceDeduction { get; set; }
    public decimal TotalAccepted { get; set; }
}

/// <summary>Nghiệm thu khối lượng theo BOQ hợp đồng → đề nghị thanh toán theo khối lượng hoàn thành</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class AcceptancesController : ProcControllerBase
{
    private readonly FinanceService _fin;
    public AcceptancesController(EProcDbContext db, IOptions<ProcurementSettings> s, FinanceService fin) : base(db, s) { _fin = fin; }

    public async Task<IActionResult> Index(int? contractId, AcceptanceStatus? status)
    {
        var q = Db.BoqAcceptances.AsNoTracking().Include(a => a.Contract).ThenInclude(c => c!.Vendor).Include(a => a.CreatedBy).AsQueryable();
        if (contractId.HasValue) q = q.Where(a => a.ContractId == contractId);
        if (status.HasValue) q = q.Where(a => a.Status == status);
        ViewBag.ContractId = contractId; ViewBag.Status = status;
        ViewBag.Paid = (await Db.Payments.AsNoTracking().Where(p => p.AcceptanceId != null && p.Status != PaymentStatus.Rejected).Select(p => p.AcceptanceId!.Value).ToListAsync()).ToHashSet();
        return View(await q.OrderByDescending(a => a.CreatedAt).Take(300).ToListAsync());
    }

    private async Task<Contract?> LoadContractAsync(int id) =>
        await Db.Contracts.Include(c => c.Lines).Include(c => c.Vendor).FirstOrDefaultAsync(c => c.Id == id);

    private async Task<Dictionary<int, decimal>> CumulativeAsync(int contractId, int? excludeId = null) =>
        BoqAcceptances.Cumulative(await Db.BoqAcceptances.AsNoTracking().Include(a => a.Lines).Where(a => a.ContractId == contractId && a.Status == AcceptanceStatus.Confirmed).ToListAsync(), excludeId);

    private bool CanCreate => User.IsPurchasing();

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int contractId)
    {
        var c = await LoadContractAsync(contractId);
        if (c == null) return NotFound();
        if (c.Status != ContractStatus.Active) { TempData["Error"] = I18n.T("Hợp đồng không còn hiệu lực."); return RedirectToAction("Details", "Contracts", new { id = contractId }); }
        if (c.IsFramework || !c.Lines.Any()) { TempData["Error"] = I18n.T("Hợp đồng khung / hợp đồng không có dòng hàng không nghiệm thu theo khối lượng."); return RedirectToAction("Details", "Contracts", new { id = contractId }); }
        var existing = await Db.BoqAcceptances.CountAsync(a => a.ContractId == contractId);
        return View("Form", await BuildFormAsync(new BoqAcceptance { ContractId = contractId, Period = existing + 1 }, c, new()));
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var a = await Db.BoqAcceptances.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        if (a.Status != AcceptanceStatus.Draft) { TempData["Error"] = I18n.T("Chỉ sửa biên bản ở trạng thái Nháp."); return RedirectToAction(nameof(Details), new { id }); }
        var c = await LoadContractAsync(a.ContractId);
        return View("Form", await BuildFormAsync(a, c!, a.Lines.ToDictionary(l => l.ContractLineId, l => l.Quantity)));
    }

    private async Task<AcceptanceForm> BuildFormAsync(BoqAcceptance a, Contract c, Dictionary<int, decimal> qty)
    {
        var cum = await CumulativeAsync(c.Id, a.Id == 0 ? null : a.Id);
        return new AcceptanceForm { Acceptance = a, Contract = c, Qty = qty, Cumulative = cum, Rows = BoqAcceptances.Progress(c.Lines, cum) };
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int id, int contractId, DateTime acceptanceDate, string? note, Dictionary<int, decimal>? q, bool confirmNow = false)
    {
        var c = await LoadContractAsync(contractId);
        if (c == null) return NotFound();
        q ??= new();
        BoqAcceptance a;
        if (id > 0)
        {
            a = (await Db.BoqAcceptances.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.ContractId == contractId))!;
            if (a == null) return NotFound();
            if (a.Status != AcceptanceStatus.Draft) { TempData["Error"] = I18n.T("Chỉ sửa biên bản ở trạng thái Nháp."); return RedirectToAction(nameof(Details), new { id }); }
        }
        else a = new BoqAcceptance { ContractId = contractId, CreatedById = Me, CreatedAt = DateTime.Now, Period = await Db.BoqAcceptances.CountAsync(x => x.ContractId == contractId) + 1 };
        var nonZero = q.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        var errors = new List<string>();
        if (c.Status != ContractStatus.Active) errors.Add(I18n.T("Hợp đồng không còn hiệu lực."));
        if (acceptanceDate.Date > DateTime.Today) errors.Add(I18n.T("Ngày nghiệm thu không được ở tương lai."));
        errors.AddRange(BoqAcceptances.Validate(c.Lines.ToList(), nonZero, await CumulativeAsync(contractId, id == 0 ? null : id)));
        if (errors.Count > 0)
        {
            a.AcceptanceDate = acceptanceDate; a.Note = note;
            var vm = await BuildFormAsync(a, c, q);
            ViewBag.Errors = errors;
            return View("Form", vm);
        }
        a.AcceptanceDate = acceptanceDate.Date; a.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        a.SubTotal = BoqAcceptances.SubTotal(c.Lines.ToList(), nonZero);
        if (id == 0) { a.Number = await DocNumbers.NextAsync(Db.BoqAcceptances.Select(x => x.Number), "NT"); Db.BoqAcceptances.Add(a); }
        else { Db.BoqAcceptanceLines.RemoveRange(a.Lines.ToList()); await Db.SaveChangesAsync(); } // xóa dòng cũ trước khi thêm (chỉ mục duy nhất)
        foreach (var kv in nonZero.Where(kv => kv.Value > 0)) Db.BoqAcceptanceLines.Add(new BoqAcceptanceLine { Acceptance = a, ContractLineId = kv.Key, Quantity = kv.Value });
        await Db.SaveChangesAsync();
        Log(DocType.Contract, contractId, I18n.T("Lập biên bản nghiệm thu khối lượng {0}", a.Number), I18n.T("Đợt {0}: {1} đ (trước VAT)", a.Period, a.SubTotal.ToMoney()));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu biên bản nghiệm thu {0}.", a.Number);
        return RedirectToAction(nameof(Details), new { id = a.Id });
    }

    public async Task<IActionResult> Details(int id)
    {
        var a = await Db.BoqAcceptances.AsNoTracking().Include(x => x.Lines).Include(x => x.CreatedBy).Include(x => x.ConfirmedBy).Include(x => x.Contract).ThenInclude(c => c!.Vendor).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        var c = await LoadContractAsync(a.ContractId);
        var cum = await CumulativeAsync(a.ContractId);
        var qty = a.Lines.ToDictionary(l => l.ContractLineId, l => l.Quantity);
        var gross = BoqAcceptances.Gross(a.SubTotal, c!.VatPercent);
        var pay = await Db.Payments.AsNoTracking().Where(p => p.AcceptanceId == id && p.Status != PaymentStatus.Rejected).FirstOrDefaultAsync();
        var (_, _, advPaid, advRecovered) = await _fin.ContractPaymentsAsync(a.ContractId);
        var contractGross = Math.Round(c.Value * (1 + c.VatPercent / 100m), 0);
        ViewBag.History = await HistoryAsync(DocType.Contract, a.ContractId);
        return View(new AcceptanceDetails
        {
            Acceptance = a, Rows = BoqAcceptances.Progress(c.Lines, cum), Qty = qty, Payment = pay, Gross = gross, ContractGross = contractGross,
            AdvanceDeduction = pay?.AdvanceDeduction ?? BoqAcceptances.AdvanceDeduction(gross, contractGross, advPaid, advRecovered),
            TotalAccepted = a.SubTotal
        });
    }

    /// <summary>Xác nhận biên bản: người xác nhận khác người lập (Giám đốc / Quản trị luôn được). Khối lượng lũy kế kiểm tra lại tại thời điểm xác nhận.</summary>
    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int id)
    {
        var a = await Db.BoqAcceptances.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (a.Status != AcceptanceStatus.Draft) return Back(I18n.T("Chỉ xác nhận biên bản ở trạng thái Nháp."));
        if (a.CreatedById == Me && !User.IsAdmin()) return Back(I18n.T("Người lập biên bản không được tự xác nhận – cần người khác (Giám đốc / nhân viên mua hàng khác) xác nhận."));
        var c = await LoadContractAsync(a.ContractId);
        if (c!.Status != ContractStatus.Active) return Back(I18n.T("Hợp đồng không còn hiệu lực."));
        var errors = BoqAcceptances.Validate(c.Lines.ToList(), a.Lines.ToDictionary(l => l.ContractLineId, l => l.Quantity), await CumulativeAsync(a.ContractId));
        if (errors.Count > 0) return Back(string.Join(" ", errors));
        a.Status = AcceptanceStatus.Confirmed; a.ConfirmedById = Me; a.ConfirmedAt = DateTime.Now;
        Log(DocType.Contract, a.ContractId, I18n.T("Xác nhận nghiệm thu khối lượng {0}", a.Number), $"{a.SubTotal.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xác nhận biên bản {0}. Kế toán có thể lập đề nghị thanh toán theo khối lượng.", a.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var a = await Db.BoqAcceptances.FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (a.Status == AcceptanceStatus.Cancelled) return Back(I18n.T("Biên bản đã hủy."));
        if (await Db.Payments.AnyAsync(p => p.AcceptanceId == id && p.Status != PaymentStatus.Rejected)) return Back(I18n.T("Biên bản đã có đề nghị thanh toán – không hủy được."));
        if (string.IsNullOrWhiteSpace(reason)) return Back(I18n.T("Nhập lý do hủy."));
        a.Status = AcceptanceStatus.Cancelled;
        Log(DocType.Contract, a.ContractId, I18n.T("Hủy biên bản nghiệm thu {0}", a.Number), reason.Trim());
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã hủy biên bản {0}.", a.Number);
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Kế toán lập đề nghị thanh toán theo khối lượng đã nghiệm thu (trừ tạm ứng theo tỷ lệ), chuyển Giám đốc duyệt</summary>
    [Authorize(Roles = AppRoles.Finance)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestPayment(int id)
    {
        var a = await Db.BoqAcceptances.FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }
        if (a.Status != AcceptanceStatus.Confirmed) return Back(I18n.T("Chỉ đề nghị thanh toán biên bản đã xác nhận."));
        if (await Db.Payments.AnyAsync(p => p.AcceptanceId == id && p.Status != PaymentStatus.Rejected)) return Back(I18n.T("Biên bản này đã có đề nghị thanh toán."));
        var c = await Db.Contracts.FindAsync(a.ContractId);
        if (c!.Status != ContractStatus.Active) return Back(I18n.T("Hợp đồng không còn hiệu lực."));
        var gross = BoqAcceptances.Gross(a.SubTotal, c.VatPercent);
        var contractGross = Math.Round(c.Value * (1 + c.VatPercent / 100m), 0);
        // Không thanh toán vượt giá trị hợp đồng (tính cả hóa đơn / đợt đã đề nghị): tránh trả trùng giữa luồng hóa đơn và nghiệm thu
        var ledger = await ContractClose.LedgerAsync(Db, c);
        var committed = ledger.WorkGross;
        if (committed + gross > contractGross)
            return Back(I18n.T("Thanh toán vượt giá trị hợp đồng: đã đề nghị {0} đ, đợt này {1} đ, giá trị HĐ {2} đ.", committed.ToMoney(), gross.ToMoney(), contractGross.ToMoney()));
        var ded = ContractClose.Deductions(c, gross, ledger);
        var advance = ded.Advance;
        var pay = new Payment
        {
            Number = await DocNumbers.NextAsync(Db.Payments.Select(x => x.Number), "TT"), Type = PaymentType.Acceptance, VendorId = c.VendorId, ContractId = c.Id, AcceptanceId = a.Id,
            Amount = gross, AdvanceDeduction = advance, RetentionAmount = ded.Retention, NetAmount = gross - advance - ded.Retention,
            Note = I18n.T("Thanh toán theo khối lượng nghiệm thu {0} (đợt {1}) – HĐ {2}", a.Number, a.Period, c.Number) + (string.IsNullOrEmpty(ded.Note) ? "" : " · " + ded.Note),
            RequestedById = Me, RequestedAt = DateTime.Now
        };
        Db.Payments.Add(pay);
        await Db.SaveChangesAsync();
        Log(DocType.Payment, pay.Id, I18n.T("Đề nghị thanh toán {0} đ", pay.NetAmount.ToMoney()), pay.Note);
        Log(DocType.Contract, c.Id, I18n.T("Lập đề nghị thanh toán {0} theo nghiệm thu {1}", pay.Number, a.Number));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lập {0}: thực chi {1} đ – chờ Giám đốc duyệt.", pay.Number, pay.NetAmount.ToMoney());
        return RedirectToAction("Details", "Payments", new { id = pay.Id });
    }
}
