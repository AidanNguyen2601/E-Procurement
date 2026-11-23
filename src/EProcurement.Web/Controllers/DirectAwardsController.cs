using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Chỉ định thầu: PR đã duyệt (hình thức Chỉ định thầu) → lập tờ trình (căn cứ + thuyết minh + tài liệu chứng minh)
/// → thương thảo giá với NCC → trình Giám đốc (luôn bắt buộc) → duyệt → sinh PO nháp theo giá thương thảo.
/// Kiểm soát: hạn mức chỉ định (vượt chỉ khi khẩn cấp), cảnh báo chia nhỏ gói, báo cáo tỷ lệ chỉ định.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class DirectAwardsController : ProcControllerBase
{
    public DirectAwardsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    /// <summary>Danh sách + báo cáo tỷ lệ chỉ định thầu trong năm</summary>
    public async Task<IActionResult> Index(DirectAwardStatus? status, int? year)
    {
        year ??= DateTime.Today.Year;
        var q = Db.DirectAwards.Include(d => d.Vendor).Include(d => d.Buyer).Include(d => d.PurchaseRequest).AsNoTracking();
        if (status.HasValue) q = q.Where(d => d.Status == status);
        ViewBag.Status = status; ViewBag.Year = year;

        // Báo cáo: tờ trình đã duyệt trong năm so với tổng giá trị PO (đã duyệt trở lên) trong năm
        var start = new DateTime(year.Value, 1, 1); var end = start.AddYears(1);
        var approved = await Db.DirectAwards.Include(d => d.Vendor).Include(d => d.PurchaseRequest).AsNoTracking()
            .Where(d => d.Status == DirectAwardStatus.Approved && d.ApprovedAt >= start && d.ApprovedAt < end).ToListAsync();
        ViewBag.ApprovedCount = approved.Count;
        ViewBag.ApprovedValue = approved.Sum(d => d.NegotiatedTotal);
        ViewBag.TotalSpend = await Db.PurchaseOrders.Where(p => p.Status >= PoStatus.Approved && p.Status != PoStatus.Cancelled && p.OrderDate >= start && p.OrderDate < end)
            .SumAsync(p => (decimal?)p.SubTotal) ?? 0m;
        ViewBag.Saving = approved.Sum(d => d.ProposedTotal - d.NegotiatedTotal);
        ViewBag.ByDepartment = approved.GroupBy(d => d.PurchaseRequest?.Department ?? "—")
            .Select(g => (Name: g.Key, Count: g.Count(), Value: g.Sum(d => d.NegotiatedTotal))).OrderByDescending(x => x.Value).ToList();
        ViewBag.ByVendor = approved.GroupBy(d => d.Vendor?.Name ?? "—")
            .Select(g => (Name: g.Key, Count: g.Count(), Value: g.Sum(d => d.NegotiatedTotal))).OrderByDescending(x => x.Value).ToList();
        ViewBag.ByReason = approved.GroupBy(d => d.Reason)
            .Select(g => (Name: g.Key.GetDisplayName(), Count: g.Count(), Value: g.Sum(d => d.NegotiatedTotal))).OrderByDescending(x => x.Value).ToList();
        return View(await q.OrderByDescending(d => d.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var d = await LoadAsync(id);
        if (d == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.DirectAward, id);
        ViewBag.Attachments = await AttachmentPanelAsync(DocType.DirectAward, id, User.IsPurchasing() && d.Status == DirectAwardStatus.Draft);
        ViewBag.Po = await Db.PurchaseOrders.FirstOrDefaultAsync(p => p.DirectAwardId == id && p.Status != PoStatus.Cancelled);
        ViewBag.Max = Settings.DirectAwardMax;
        ViewBag.Problems = d.Status == DirectAwardStatus.Draft ? await SubmitProblemsAsync(d) : new List<string>();
        ViewBag.SplitWarning = d.Status == DirectAwardStatus.Draft ? await ProcurementRules.SplitWarningAsync(Db, d, Settings) : d.SplitWarning;
        return View(d);
    }

    /// <summary>Bản in: tờ trình chỉ định thầu + biên bản thương thảo</summary>
    public async Task<IActionResult> Print(int id)
    {
        var d = await LoadAsync(id);
        if (d == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.DirectAward, id);
        ViewBag.Files = (await AttachmentPanelAsync(DocType.DirectAward, id, false)).Files;
        return View(d);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int prId)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == prId);
        if (pr == null) return NotFound();
        IActionResult BackToPr(string msg) { TempData["Error"] = msg; return RedirectToAction("Details", "PurchaseRequests", new { id = prId }); }
        if (pr.Status is not (PrStatus.Approved or PrStatus.Processing)) return BackToPr("Chỉ lập tờ trình cho đề nghị đã được duyệt.");
        if (pr.Method != ProcurementMethod.DirectAward) return BackToPr("Chọn hình thức \"Chỉ định thầu\" cho đề nghị trước khi lập tờ trình.");
        if (await Db.DirectAwards.AnyAsync(d => d.PurchaseRequestId == prId && d.Status != DirectAwardStatus.Cancelled))
            return BackToPr("Đề nghị đã có tờ trình chỉ định đang xử lý.");
        var vm = new DirectAwardFormViewModel
        {
            Number = "(tự sinh khi lưu)", PurchaseRequestId = prId, PurchaseRequest = pr, Title = pr.Title,
            Justification = pr.MethodReason ?? string.Empty, NegotiationDate = DateTime.Today,
            Lines = pr.Lines.OrderBy(l => l.SortOrder).Select(l => new DirectAwardLineInput
                { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice }).ToList()
        };
        await LoadFormAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var d = await Db.DirectAwards.Include(x => x.Lines).Include(x => x.PurchaseRequest).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        if (d.Status != DirectAwardStatus.Draft) { TempData["Error"] = "Chỉ sửa tờ trình ở trạng thái Nháp."; return RedirectToAction(nameof(Details), new { id }); }
        var vm = new DirectAwardFormViewModel
        {
            Id = d.Id, Number = d.Number, PurchaseRequestId = d.PurchaseRequestId, PurchaseRequest = d.PurchaseRequest, Title = d.Title,
            VendorId = d.VendorId, Reason = d.Reason, Justification = d.Justification, NegotiationDate = d.NegotiationDate,
            NegotiationNote = d.NegotiationNote, LeadTimeDays = d.LeadTimeDays, PaymentTerms = d.PaymentTerms, Warranty = d.Warranty,
            VatPercent = Math.Round(d.VatPercent, 0),
            Lines = d.Lines.OrderBy(l => l.SortOrder).Select(l => new DirectAwardLineInput
            {
                ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                EstimatedPrice = l.EstimatedPrice, ProposedPrice = l.ProposedPrice, NegotiatedPrice = l.NegotiatedPrice
            }).ToList()
        };
        await LoadFormAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(DirectAwardFormViewModel vm, bool submit = false)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == vm.PurchaseRequestId);
        if (pr == null) return NotFound();
        DirectAward? d = null;
        if (vm.Id != 0)
        {
            d = await Db.DirectAwards.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (d == null) return NotFound();
            if (d.Status != DirectAwardStatus.Draft || d.PurchaseRequestId != pr.Id) return BadRequest();
        }
        else
        {
            if (pr.Method != ProcurementMethod.DirectAward || pr.Status is not (PrStatus.Approved or PrStatus.Processing))
                ModelState.AddModelError(string.Empty, "Đề nghị chưa được duyệt hoặc không theo hình thức Chỉ định thầu.");
            if (await Db.DirectAwards.AnyAsync(x => x.PurchaseRequestId == pr.Id && x.Status != DirectAwardStatus.Cancelled))
                ModelState.AddModelError(string.Empty, "Đề nghị đã có tờ trình chỉ định đang xử lý.");
        }
        var vendor = vm.VendorId.HasValue ? await Db.Vendors.FindAsync(vm.VendorId) : null;
        if (vendor != null && vendor.Status != VendorStatus.Approved)
            ModelState.AddModelError(nameof(vm.VendorId), $"NCC đang ở trạng thái \"{vendor.Status.GetDisplayName()}\", không được chỉ định.");
        // Dòng hàng lấy theo PR (không thêm / bớt); chỉ nhận đơn giá chào & thương thảo từ form
        var prLines = pr.Lines.OrderBy(l => l.SortOrder).ToList();
        if (vm.Lines.Count != prLines.Count) ModelState.AddModelError(string.Empty, "Dòng hàng không khớp với đề nghị.");
        for (var i = 0; i < Math.Min(vm.Lines.Count, prLines.Count); i++)
        {
            var l = vm.Lines[i];
            if (l.NegotiatedPrice > 0 && l.ProposedPrice > 0 && l.NegotiatedPrice > l.ProposedPrice)
                ModelState.AddModelError(string.Empty, $"\"{prLines[i].Description}\": giá thương thảo không được cao hơn giá NCC chào.");
        }
        if (!ModelState.IsValid)
        {
            vm.PurchaseRequest = pr;
            for (var i = 0; i < Math.Min(vm.Lines.Count, prLines.Count); i++)
            { vm.Lines[i].Description = prLines[i].Description; vm.Lines[i].Unit = prLines[i].Unit; vm.Lines[i].Quantity = prLines[i].Quantity; vm.Lines[i].EstimatedPrice = prLines[i].EstimatedPrice; }
            await LoadFormAsync(vm);
            return View("Form", vm);
        }

        if (d == null)
        {
            d = new DirectAward
            {
                Number = await DocNumbers.NextAsync(Db.DirectAwards.Select(x => x.Number), "DA"),
                PurchaseRequestId = pr.Id, BuyerId = Me, CreatedAt = DateTime.Now
            };
            Db.DirectAwards.Add(d);
        }
        else
        {
            Db.DirectAwardLines.RemoveRange(d.Lines);
            d.Lines.Clear();
        }
        d.Title = vm.Title; d.VendorId = vm.VendorId!.Value; d.Reason = vm.Reason; d.Justification = vm.Justification.Trim();
        d.NegotiationDate = vm.NegotiationDate; d.NegotiationNote = vm.NegotiationNote; d.LeadTimeDays = vm.LeadTimeDays;
        d.PaymentTerms = vm.PaymentTerms ?? vendor?.PaymentTerms; d.Warranty = vm.Warranty; d.VatPercent = vm.VatPercent;
        for (var i = 0; i < prLines.Count; i++)
        {
            var p = prLines[i];
            d.Lines.Add(new DirectAwardLine
            {
                ItemId = p.ItemId, Description = p.Description, Unit = p.Unit, Quantity = p.Quantity, EstimatedPrice = p.EstimatedPrice,
                ProposedPrice = vm.Lines[i].ProposedPrice, NegotiatedPrice = vm.Lines[i].NegotiatedPrice, SortOrder = i + 1
            });
        }
        d.Recalculate();
        await Db.SaveChangesAsync();

        if (vm.Id == 0)
        {
            Log(DocType.DirectAward, d.Id, "Lập tờ trình chỉ định thầu", $"{vendor?.Name} – {d.Reason.GetDisplayName()}");
            if (pr.Status == PrStatus.Approved) { pr.Status = PrStatus.Processing; Log(DocType.PR, pr.Id, $"Mua hàng lập {d.Number}"); }
            await Db.SaveChangesAsync();
        }
        if (submit) return await Submit(d.Id);
        TempData["Success"] = $"Đã lưu {d.Number}. Đính kèm tài liệu chứng minh rồi trình Giám đốc.";
        return RedirectToAction(nameof(Details), new { id = d.Id });
    }

    /// <summary>Trình Giám đốc duyệt (bắt buộc với mọi tờ trình chỉ định)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var d = await Db.DirectAwards.Include(x => x.Lines).Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        if (d.Status != DirectAwardStatus.Draft) return BadRequest();
        var problems = await SubmitProblemsAsync(d);
        if (problems.Count > 0)
        {
            TempData["Error"] = "Chưa trình được: " + string.Join(" ", problems);
            return RedirectToAction(nameof(Details), new { id });
        }
        d.SplitWarning = await ProcurementRules.SplitWarningAsync(Db, d, Settings);
        d.Status = DirectAwardStatus.PendingApproval;
        d.SubmittedAt = DateTime.Now;
        Log(DocType.DirectAward, id, "Trình Giám đốc duyệt",
            $"Giá chào {d.ProposedTotal.ToMoney()} → thương thảo {d.NegotiatedTotal.ToMoney()} đ (giảm {d.SavingPercent:0.#}%)" + (d.SplitWarning != null ? ". CẢNH BÁO: " + d.SplitWarning : ""));
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã trình {d.Number} lên Giám đốc." + (d.SplitWarning != null ? " Lưu ý: có cảnh báo nghi chia nhỏ gói." : "");
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Giám đốc duyệt → sinh PO nháp theo giá thương thảo; trả lại → về Nháp để bổ sung</summary>
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.DirectAward)]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment, string? returnUrl)
    {
        var d = await Db.DirectAwards.Include(x => x.Lines).Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();
        if (d.Status != DirectAwardStatus.PendingApproval) return BadRequest();
        IActionResult Back() => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
        if (!approve)
        {
            if (string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = "Nhập lý do trả lại."; return RedirectToAction(nameof(Details), new { id }); }
            d.Status = DirectAwardStatus.Draft;
            Log(DocType.DirectAward, id, "Giám đốc trả lại tờ trình", comment);
            await Db.SaveChangesAsync();
            TempData["Success"] = $"Đã trả lại {d.Number} để bổ sung.";
            return Back();
        }
        d.Status = DirectAwardStatus.Approved;
        d.ApprovedAt = DateTime.Now;
        Log(DocType.DirectAward, id, "Giám đốc duyệt chỉ định thầu", comment);
        var po = new PurchaseOrder
        {
            Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(p => p.Number), "PO"),
            VendorId = d.VendorId, PurchaseRequestId = d.PurchaseRequestId, DirectAwardId = d.Id, BuyerId = d.BuyerId,
            Method = ProcurementMethod.DirectAward, DeliveryDate = DateTime.Today.AddDays(d.LeadTimeDays ?? 7),
            PaymentTerms = d.PaymentTerms ?? d.Vendor?.PaymentTerms, DeliveryAddress = Settings.DefaultDeliveryAddress, VatPercent = d.VatPercent,
            Notes = string.IsNullOrEmpty(d.Warranty) ? null : "Bảo hành: " + d.Warranty, CreatedAt = DateTime.Now
        };
        foreach (var l in d.Lines.OrderBy(l => l.SortOrder))
            po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.NegotiatedPrice, SortOrder = l.SortOrder });
        po.Recalculate();
        Db.PurchaseOrders.Add(po);
        await Db.SaveChangesAsync();
        Log(DocType.PO, po.Id, $"Tạo từ tờ trình {d.Number}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã duyệt {d.Number}, tạo đơn mua hàng nháp {po.Number}.";
        return Back();
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var d = await Db.DirectAwards.FindAsync(id);
        if (d == null) return NotFound();
        if (d.Status is not (DirectAwardStatus.Draft or DirectAwardStatus.PendingApproval)) return BadRequest();
        if (string.IsNullOrWhiteSpace(reason)) { TempData["Error"] = "Nhập lý do hủy."; return RedirectToAction(nameof(Details), new { id }); }
        d.Status = DirectAwardStatus.Cancelled;
        Log(DocType.DirectAward, id, "Hủy tờ trình", reason);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã hủy {d.Number}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Điều kiện trình duyệt: đã thương thảo đủ giá, có tài liệu chứng minh (nếu căn cứ yêu cầu), trong hạn mức (trừ khẩn cấp)</summary>
    private async Task<List<string>> SubmitProblemsAsync(DirectAward d)
    {
        var p = new List<string>();
        if (d.Lines.Any(l => l.ProposedPrice <= 0 || l.NegotiatedPrice <= 0)) p.Add("Nhập đủ đơn giá NCC chào và đơn giá sau thương thảo cho mọi dòng.");
        if (d.NegotiationDate == null) p.Add("Nhập ngày thương thảo.");
        if (ProcurementRules.RequiresEvidence(d.Reason) && !await Db.Attachments.AnyAsync(a => a.DocumentType == DocType.DirectAward && a.DocumentId == d.Id))
            p.Add($"Căn cứ \"{d.Reason.GetDisplayName()}\" cần đính kèm tài liệu chứng minh.");
        if (d.NegotiatedTotal > Settings.DirectAwardMax && d.Reason != DirectAwardReason.Emergency)
            p.Add($"Giá trị {d.NegotiatedTotal.ToMoney()} đ vượt hạn mức chỉ định {Settings.DirectAwardMax.ToMoney()} đ (chỉ được vượt khi khẩn cấp) – chuyển sang chào hàng cạnh tranh.");
        return p;
    }

    private async Task<DirectAward?> LoadAsync(int id)
    {
        var d = await Db.DirectAwards.Include(x => x.Vendor).Include(x => x.Buyer).Include(x => x.PurchaseRequest).ThenInclude(p => p!.Requester)
            .Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (d != null) d.Lines = d.Lines.OrderBy(l => l.SortOrder).ToList();
        return d;
    }

    private async Task LoadFormAsync(DirectAwardFormViewModel vm)
    {
        ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status == VendorStatus.Approved || v.Id == vm.VendorId).OrderBy(v => v.Name)
            .Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name", vm.VendorId);
        ViewBag.Max = Settings.DirectAwardMax;
    }
}
