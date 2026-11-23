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
/// Đơn mua hàng (PO). Nháp → (vượt ngưỡng: chờ Giám đốc duyệt) → Đã duyệt → Gửi NCC
/// → Nhận một phần / Nhận đủ (qua phiếu nhận hàng) → Đánh giá NCC.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class PurchaseOrdersController : ProcControllerBase
{
    public PurchaseOrdersController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(PoStatus? status, string? keyword, bool late = false)
    {
        var q = Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Buyer).AsNoTracking();
        if (status.HasValue) q = q.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(p => p.Number.Contains(keyword) || (p.Vendor != null && p.Vendor.Name.Contains(keyword)));
        if (late) q = q.Where(p => (p.Status == PoStatus.Issued || p.Status == PoStatus.PartiallyReceived) && p.DeliveryDate < DateTime.Today);
        ViewBag.Status = status; ViewBag.Keyword = keyword; ViewBag.Late = late;
        return View(await q.OrderByDescending(p => p.OrderDate).ThenByDescending(p => p.Id).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var po = await LoadPoAsync(id);
        if (po == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.PO, id);
        ViewBag.Receipts = await Db.GoodsReceipts.Include(g => g.ReceivedBy).Include(g => g.Lines).Where(g => g.PurchaseOrderId == id).OrderBy(g => g.ReceiptDate).ToListAsync();
        ViewBag.Evaluation = await Db.VendorEvaluations.Include(e => e.Evaluator).FirstOrDefaultAsync(e => e.PurchaseOrderId == id);
        ViewBag.Threshold = await Matrix.PoDirectorAsync(po.PurchaseRequest?.Department);
        return View(po);
    }

    /// <summary>Bản in đơn đặt hàng gửi NCC</summary>
    public async Task<IActionResult> Print(int id)
    {
        var po = await LoadPoAsync(id);
        return po == null ? NotFound() : View(po);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    /// <param name="prId">Lập từ đề nghị (bắt buộc PR đã chọn hình thức Mua sắm trực tiếp)</param>
    /// <param name="refPoId">PO tham chiếu cho mua sắm trực tiếp; để trống → hiện danh sách để chọn</param>
    public async Task<IActionResult> Create(int? prId, int? refPoId)
    {
        var vm = new PoFormViewModel { Number = "(tự sinh khi lưu)", DeliveryAddress = Settings.DefaultDeliveryAddress, PurchaseRequestId = prId };
        if (prId.HasValue)
        {
            var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == prId);
            if (pr == null) return NotFound();
            IActionResult BackToPr(string msg) { TempData["Error"] = msg; return RedirectToAction("Details", "PurchaseRequests", new { id = prId }); }
            if (pr.Status is not (PrStatus.Approved or PrStatus.Processing)) return BackToPr("Chỉ lập PO cho đề nghị đã được duyệt.");
            if (pr.Method == ProcurementMethod.CompetitiveQuotation) return BackToPr("Đề nghị theo hình thức Chào hàng cạnh tranh: PO được sinh từ RFQ sau khi chọn NCC.");
            if (pr.Method != ProcurementMethod.DirectPurchase) return BackToPr("Chọn hình thức lựa chọn NCC cho đề nghị trước khi lập PO.");

            var refs = await ProcurementRules.FindReferencesAsync(Db, pr, Settings);
            var chosen = refs.FirstOrDefault(r => r.Po.Id == refPoId);
            if (chosen == null || !chosen.CoversRequest)
            {
                ViewBag.PurchaseRequest = pr;
                ViewBag.Months = Settings.DirectPurchaseMonths;
                ViewBag.MaxPercent = Settings.DirectPurchaseMaxQuantityPercent;
                return View("SelectReference", refs);
            }

            vm.ReferencePoId = chosen.Po.Id;
            vm.VendorId = chosen.Po.VendorId;
            vm.PaymentTerms = chosen.Po.PaymentTerms;
            vm.VatPercent = Math.Round(chosen.Po.VatPercent, 0);
            vm.DeliveryDate = pr.NeededDate;
            // Đơn giá mặc định = đơn giá PO tham chiếu (không vượt giá đã mua)
            vm.Lines = pr.Lines.OrderBy(l => l.SortOrder)
                .Select(l => new LineInput { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = chosen.PriceByItem[l.ItemId!.Value] }).ToList();
        }
        if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
        await LoadFormAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
        if (po == null) return NotFound();
        if (po.Status != PoStatus.Draft)
        {
            TempData["Error"] = "Chỉ sửa được PO ở trạng thái Nháp.";
            return RedirectToAction(nameof(Details), new { id });
        }
        var vm = new PoFormViewModel
        {
            Id = po.Id, Number = po.Number, VendorId = po.VendorId, PurchaseRequestId = po.PurchaseRequestId, RfqId = po.RfqId,
            OrderDate = po.OrderDate, DeliveryDate = po.DeliveryDate, DeliveryAddress = po.DeliveryAddress, PaymentTerms = po.PaymentTerms,
            VatPercent = Math.Round(po.VatPercent, 0), Notes = po.Notes, ReferencePoId = po.ReferencePoId, PriceDeviationReason = po.PriceDeviationReason,
            Lines = po.Lines.OrderBy(l => l.SortOrder).Select(l => new LineInput { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = l.UnitPrice }).ToList()
        };
        await LoadFormAsync(vm);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PoFormViewModel vm, bool submit = false)
    {
        vm.Lines = CleanLines(vm.Lines);
        if (vm.DeliveryDate < vm.OrderDate) ModelState.AddModelError(nameof(vm.DeliveryDate), "Ngày giao hàng phải sau ngày đặt hàng");
        var vendor = vm.VendorId.HasValue ? await Db.Vendors.FindAsync(vm.VendorId) : null;
        if (vendor != null && vendor.Status != VendorStatus.Approved)
            ModelState.AddModelError(nameof(vm.VendorId), $"NCC đang ở trạng thái \"{vendor.Status.GetDisplayName()}\", không được đặt hàng.");

        // Hình thức + PO tham chiếu: khi sửa lấy theo PO đã lưu, khi tạo mới lấy theo PR (không tin dữ liệu form)
        var existing = vm.Id == 0 ? null : await Db.PurchaseOrders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == vm.Id);
        ProcurementMethod? method;
        if (existing != null) { method = existing.Method; vm.ReferencePoId = existing.ReferencePoId; }
        else if (vm.PurchaseRequestId.HasValue)
        {
            method = await Db.PurchaseRequests.Where(p => p.Id == vm.PurchaseRequestId).Select(p => p.Method).FirstOrDefaultAsync();
            if (method != ProcurementMethod.DirectPurchase)
                ModelState.AddModelError(string.Empty, "Đề nghị không theo hình thức Mua sắm trực tiếp, không lập PO trực tiếp từ đề nghị được.");
        }
        else { method = null; vm.ReferencePoId = null; }

        var priceDeviation = false;
        if (method == ProcurementMethod.DirectPurchase)
            priceDeviation = await ValidateDirectPurchaseAsync(vm);

        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
            await LoadFormAsync(vm);
            return View("Form", vm);
        }

        PurchaseOrder po;
        if (vm.Id == 0)
        {
            po = new PurchaseOrder
            {
                Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(p => p.Number), "PO"),
                BuyerId = Me, PurchaseRequestId = vm.PurchaseRequestId, CreatedAt = DateTime.Now,
                Method = method, ReferencePoId = method == ProcurementMethod.DirectPurchase ? vm.ReferencePoId : null
            };
            Db.PurchaseOrders.Add(po);
        }
        else
        {
            po = await Db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == vm.Id) ?? throw new InvalidOperationException();
            if (po.Status != PoStatus.Draft) return BadRequest();
            Db.PurchaseOrderLines.RemoveRange(po.Lines);
            po.Lines.Clear();
        }
        po.VendorId = vm.VendorId; po.OrderDate = vm.OrderDate; po.DeliveryDate = vm.DeliveryDate; po.DeliveryAddress = vm.DeliveryAddress;
        po.PaymentTerms = vm.PaymentTerms ?? vendor?.PaymentTerms; po.VatPercent = vm.VatPercent; po.Notes = vm.Notes;
        po.PriceDeviationReason = priceDeviation ? vm.PriceDeviationReason!.Trim() : null;
        var s = 1;
        foreach (var l in vm.Lines)
            po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.Price, SortOrder = s++ });
        po.Recalculate();
        await Db.SaveChangesAsync();

        if (vm.Id == 0)
        {
            Log(DocType.PO, po.Id, po.Method == ProcurementMethod.DirectPurchase ? "Tạo đơn mua sắm trực tiếp" : "Tạo đơn mua hàng",
                po.ReferencePoId.HasValue ? "Căn cứ: " + await Db.PurchaseOrders.Where(p => p.Id == po.ReferencePoId).Select(p => p.Number).FirstAsync() : null);
            if (po.PurchaseRequestId.HasValue)
            {
                var pr = await Db.PurchaseRequests.FindAsync(po.PurchaseRequestId);
                if (pr is { Status: PrStatus.Approved }) { pr.Status = PrStatus.Processing; Log(DocType.PR, pr.Id, $"Mua hàng lập {po.Number}"); }
            }
            await Db.SaveChangesAsync();
        }
        if (submit) return await Submit(po.Id);
        TempData["Success"] = $"Đã lưu {po.Number}.";
        return RedirectToAction(nameof(Details), new { id = po.Id });
    }

    /// <summary>Trình duyệt: dưới ngưỡng → tự duyệt; vượt ngưỡng → chờ Giám đốc</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var po = await Db.PurchaseOrders.FindAsync(id);
        if (po == null) return NotFound();
        if (po.Status != PoStatus.Draft) return BadRequest();
        var award = po.DirectAwardId.HasValue ? await Db.DirectAwards.FindAsync(po.DirectAwardId) : null;
        var poLimit = await Matrix.PoDirectorAsync(po.PurchaseRequestId == null ? null
            : await Db.PurchaseRequests.Where(p => p.Id == po.PurchaseRequestId).Select(p => p.Department).FirstOrDefaultAsync());
        var contract = po.ContractId.HasValue ? await Db.Contracts.FindAsync(po.ContractId) : null;
        var contractUsed = contract == null ? 0 : await Db.PurchaseOrders
            .Where(p => p.ContractId == contract.Id && p.Id != po.Id && p.Status != PoStatus.Cancelled).SumAsync(p => (decimal?)p.SubTotal) ?? 0;
        if (contract is { Status: ContractStatus.Active } && contractUsed + po.SubTotal <= contract.Value)
        {
            // Kết quả đấu thầu và hợp đồng đã được Giám đốc phê duyệt → đặt hàng trong giá trị hợp đồng không duyệt lại
            po.Status = PoStatus.Approved;
            Log(DocType.PO, id, $"Duyệt theo hợp đồng {contract.Number} (đã thực hiện {(contractUsed + po.SubTotal).ToMoney()}/{contract.Value.ToMoney()} đ)");
        }
        else if (award is { Status: DirectAwardStatus.Approved } && po.Total <= award.NegotiatedGrandTotal)
        {
            // Giám đốc đã duyệt đúng NCC và giá trên tờ trình chỉ định → không duyệt lại
            po.Status = PoStatus.Approved;
            Log(DocType.PO, id, $"Duyệt theo tờ trình chỉ định {award.Number} đã được Giám đốc phê duyệt");
        }
        else if (po.Total > poLimit && !User.IsAdmin())
        {
            po.Status = PoStatus.PendingApproval;
            Log(DocType.PO, id, "Trình Giám đốc duyệt", $"Giá trị {po.Total.ToMoney()} vượt ngưỡng {poLimit.ToMoney()}");
        }
        else if (po.PriceDeviationReason != null && !User.IsAdmin())
        {
            po.Status = PoStatus.PendingApproval;
            Log(DocType.PO, id, "Trình Giám đốc duyệt", "Mua sắm trực tiếp có đơn giá cao hơn PO tham chiếu: " + po.PriceDeviationReason);
        }
        else
        {
            po.Status = PoStatus.Approved;
            Log(DocType.PO, id, "Duyệt (dưới ngưỡng phân cấp)");
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = $"{po.Number}: {po.Status.GetDisplayName()}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment, string? returnUrl)
    {
        var po = await Db.PurchaseOrders.FindAsync(id);
        if (po == null) return NotFound();
        if (po.Status != PoStatus.PendingApproval) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            TempData["Error"] = "Vui lòng nhập lý do trả lại.";
            return RedirectToAction(nameof(Details), new { id });
        }
        po.Status = approve ? PoStatus.Approved : PoStatus.Draft;
        Log(DocType.PO, id, approve ? "Giám đốc duyệt" : "Giám đốc trả lại", comment);
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? $"Đã duyệt {po.Number}." : $"Đã trả lại {po.Number} để chỉnh sửa.";
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Gửi đơn hàng cho NCC</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Issue(int id)
    {
        var po = await Db.PurchaseOrders.FindAsync(id);
        if (po == null) return NotFound();
        if (po.Status != PoStatus.Approved) return BadRequest();
        po.Status = PoStatus.Issued;
        po.IssuedAt = DateTime.Now;
        Log(DocType.PO, id, "Gửi NCC");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã gửi {po.Number} cho nhà cung cấp. Dùng nút In để xuất đơn đặt hàng.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
        if (po == null) return NotFound();
        if (po.Status is PoStatus.Received or PoStatus.Cancelled || po.Lines.Any(l => l.ReceivedQuantity > 0))
        {
            TempData["Error"] = "Không thể hủy PO đã nhận hàng.";
            return RedirectToAction(nameof(Details), new { id });
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Vui lòng nhập lý do hủy.";
            return RedirectToAction(nameof(Details), new { id });
        }
        po.Status = PoStatus.Cancelled;
        Log(DocType.PO, id, "Hủy đơn mua hàng", reason);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã hủy {po.Number}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đánh giá NCC sau khi nhận đủ hàng – cập nhật điểm trung bình của NCC</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(int id, VendorEvaluation model)
    {
        var po = await Db.PurchaseOrders.FindAsync(id);
        if (po == null || po.VendorId == null) return NotFound();
        if (po.Status != PoStatus.Received) return BadRequest();
        if (await Db.VendorEvaluations.AnyAsync(e => e.PurchaseOrderId == id))
        {
            TempData["Error"] = "Đơn hàng này đã được đánh giá.";
            return RedirectToAction(nameof(Details), new { id });
        }
        int Clamp(int v) => Math.Clamp(v, 1, 5);
        Db.VendorEvaluations.Add(new VendorEvaluation
        {
            VendorId = po.VendorId.Value, PurchaseOrderId = id, EvaluatorId = Me, Quality = Clamp(model.Quality), Delivery = Clamp(model.Delivery),
            Price = Clamp(model.Price), Service = Clamp(model.Service), Comment = model.Comment, CreatedAt = DateTime.Now
        });
        await Db.SaveChangesAsync();

        var evals = await Db.VendorEvaluations.Where(e => e.VendorId == po.VendorId).ToListAsync();
        var vendor = await Db.Vendors.FindAsync(po.VendorId);
        if (vendor != null) vendor.Rating = Math.Round(evals.Average(e => e.Average), 2);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã đánh giá nhà cung cấp. Điểm trung bình mới: {vendor?.Rating:0.00}/5.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Kiểm tra điều kiện mua sắm trực tiếp: PO tham chiếu còn hiệu lực, cùng NCC, chỉ gồm mặt hàng đã mua,
    /// khối lượng cộng dồn không vượt hạn mức, đơn giá không cao hơn (nếu cao hơn phải có lý do).
    /// </summary>
    /// <returns>true nếu có dòng vượt đơn giá tham chiếu (kèm lý do hợp lệ) → bắt buộc Giám đốc duyệt</returns>
    private async Task<bool> ValidateDirectPurchaseAsync(PoFormViewModel vm)
    {
        var refPo = vm.ReferencePoId.HasValue
            ? await Db.PurchaseOrders.Include(p => p.Lines).AsNoTracking().FirstOrDefaultAsync(p => p.Id == vm.ReferencePoId)
            : null;
        if (refPo == null) { ModelState.AddModelError(string.Empty, "Mua sắm trực tiếp cần có PO tham chiếu."); return false; }
        if (refPo.Status != PoStatus.Received)
            ModelState.AddModelError(string.Empty, $"PO tham chiếu {refPo.Number} chưa nhận đủ hàng.");
        if (vm.Id == 0 && refPo.OrderDate < ProcurementRules.ReferenceCutoff(Settings))
            ModelState.AddModelError(string.Empty, $"PO tham chiếu {refPo.Number} đã quá {Settings.DirectPurchaseMonths} tháng.");
        if (vm.VendorId != refPo.VendorId)
            ModelState.AddModelError(nameof(vm.VendorId), $"Mua sắm trực tiếp phải đặt cùng NCC với PO tham chiếu {refPo.Number}.");

        var c = await ProcurementRules.BuildCandidateAsync(Db, refPo, Settings, excludePoId: vm.Id == 0 ? null : vm.Id);
        var deviation = false;
        foreach (var g in vm.Lines.GroupBy(l => l.ItemId))
        {
            var name = g.First().Description;
            if (!g.Key.HasValue || !c.RemainingByItem.TryGetValue(g.Key.Value, out var remaining))
            {
                ModelState.AddModelError(string.Empty, $"\"{name}\" không có trong PO tham chiếu {refPo.Number}, không được mua trực tiếp.");
                continue;
            }
            var qty = g.Sum(l => l.Quantity);
            if (qty > remaining)
                ModelState.AddModelError(string.Empty, $"\"{name}\": đặt {qty.ToQty()}, vượt khối lượng còn được mua trực tiếp ({remaining.ToQty()} = {Settings.DirectPurchaseMaxQuantityPercent:0}% PO tham chiếu trừ phần đã mua).");
            if (g.Any(l => l.Price > c.PriceByItem[g.Key.Value])) deviation = true;
        }
        if (deviation && string.IsNullOrWhiteSpace(vm.PriceDeviationReason))
            ModelState.AddModelError(nameof(vm.PriceDeviationReason), "Có dòng đơn giá cao hơn PO tham chiếu: nhập lý do (đơn sẽ phải trình Giám đốc duyệt).");
        return deviation;
    }

    private async Task<PurchaseOrder?> LoadPoAsync(int id)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Buyer).Include(p => p.PurchaseRequest).Include(p => p.Rfq)
            .Include(p => p.ReferencePo).Include(p => p.DirectAward).Include(p => p.Contract).Include(p => p.Lines).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (po != null) po.Lines = po.Lines.OrderBy(l => l.SortOrder).ToList();
        return po;
    }

    private async Task LoadFormAsync(PoFormViewModel vm)
    {
        ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status == VendorStatus.Approved || v.Id == vm.VendorId).OrderBy(v => v.Name)
            .Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name", vm.VendorId);
        ViewBag.PurchaseRequest = vm.PurchaseRequestId.HasValue ? await Db.PurchaseRequests.FindAsync(vm.PurchaseRequestId) : null;
        if (vm.ReferencePoId.HasValue)
        {
            var refPo = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).AsNoTracking().FirstAsync(p => p.Id == vm.ReferencePoId);
            ViewBag.Reference = await ProcurementRules.BuildCandidateAsync(Db, refPo, Settings, excludePoId: vm.Id == 0 ? null : vm.Id);
            ViewBag.MaxPercent = Settings.DirectPurchaseMaxQuantityPercent;
        }
        await LoadItemsJsonAsync();
    }
}
