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
        ViewBag.Threshold = Settings.PoApprovalThreshold;
        return View(po);
    }

    /// <summary>Bản in đơn đặt hàng gửi NCC</summary>
    public async Task<IActionResult> Print(int id)
    {
        var po = await LoadPoAsync(id);
        return po == null ? NotFound() : View(po);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int? prId)
    {
        var vm = new PoFormViewModel { Number = "(tự sinh khi lưu)", DeliveryAddress = Settings.DefaultDeliveryAddress, PurchaseRequestId = prId };
        if (prId.HasValue)
        {
            var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == prId);
            if (pr == null) return NotFound();
            if (pr.Status is not (PrStatus.Approved or PrStatus.Processing))
            {
                TempData["Error"] = "Chỉ lập PO cho đề nghị đã được duyệt.";
                return RedirectToAction("Details", "PurchaseRequests", new { id = prId });
            }
            vm.DeliveryDate = pr.NeededDate;
            vm.Lines = pr.Lines.OrderBy(l => l.SortOrder)
                .Select(l => new LineInput { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = l.EstimatedPrice }).ToList();
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
            VatPercent = Math.Round(po.VatPercent, 0), Notes = po.Notes,
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
                BuyerId = Me, PurchaseRequestId = vm.PurchaseRequestId, CreatedAt = DateTime.Now
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
        var s = 1;
        foreach (var l in vm.Lines)
            po.Lines.Add(new PurchaseOrderLine { ItemId = l.ItemId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, UnitPrice = l.Price, SortOrder = s++ });
        po.Recalculate();
        await Db.SaveChangesAsync();

        if (vm.Id == 0)
        {
            Log(DocType.PO, po.Id, "Tạo đơn mua hàng");
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
        if (po.Total > Settings.PoApprovalThreshold && !User.IsAdmin())
        {
            po.Status = PoStatus.PendingApproval;
            Log(DocType.PO, id, "Trình Giám đốc duyệt", $"Giá trị {po.Total.ToMoney()} vượt ngưỡng {Settings.PoApprovalThreshold.ToMoney()}");
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

    private async Task<PurchaseOrder?> LoadPoAsync(int id)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Buyer).Include(p => p.PurchaseRequest).Include(p => p.Rfq)
            .Include(p => p.Lines).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (po != null) po.Lines = po.Lines.OrderBy(l => l.SortOrder).ToList();
        return po;
    }

    private async Task LoadFormAsync(PoFormViewModel vm)
    {
        ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status == VendorStatus.Approved || v.Id == vm.VendorId).OrderBy(v => v.Name)
            .Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name", vm.VendorId);
        ViewBag.PurchaseRequest = vm.PurchaseRequestId.HasValue ? await Db.PurchaseRequests.FindAsync(vm.PurchaseRequestId) : null;
        await LoadItemsJsonAsync();
    }
}
