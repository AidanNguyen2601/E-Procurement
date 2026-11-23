using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Phiếu nhận hàng (GRN): ghi nhận số lượng đạt / không đạt theo từng dòng PO</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class GoodsReceiptsController : ProcControllerBase
{
    public GoodsReceiptsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index()
        => View(await Db.GoodsReceipts.Include(g => g.PurchaseOrder).ThenInclude(p => p!.Vendor).Include(g => g.ReceivedBy).Include(g => g.Lines)
            .OrderByDescending(g => g.ReceiptDate).ThenByDescending(g => g.Id).ToListAsync());

    public async Task<IActionResult> Details(int id)
    {
        var g = await Db.GoodsReceipts.Include(x => x.PurchaseOrder).ThenInclude(p => p!.Vendor).Include(x => x.ReceivedBy)
            .Include(x => x.Lines).ThenInclude(l => l.PurchaseOrderLine).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return g == null ? NotFound() : View(g);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int poId)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == poId);
        if (po == null) return NotFound();
        if (po.Status is not (PoStatus.Issued or PoStatus.PartiallyReceived))
        {
            TempData["Error"] = "Chỉ nhận hàng cho PO đã gửi NCC.";
            return RedirectToAction("Details", "PurchaseOrders", new { id = poId });
        }
        return View(new ReceiptFormViewModel
        {
            PurchaseOrderId = poId, PurchaseOrder = po,
            Lines = po.Lines.OrderBy(l => l.SortOrder).Select(l => new ReceiptLineInput
            {
                PurchaseOrderLineId = l.Id, Description = l.Description, Unit = l.Unit, Ordered = l.Quantity, Received = l.ReceivedQuantity,
                QuantityAccepted = l.RemainingQuantity
            }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ReceiptFormViewModel vm)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == vm.PurchaseOrderId);
        if (po == null) return NotFound();
        if (po.Status is not (PoStatus.Issued or PoStatus.PartiallyReceived)) return BadRequest();

        foreach (var l in vm.Lines)
        {
            var pl = po.Lines.FirstOrDefault(x => x.Id == l.PurchaseOrderLineId);
            if (pl == null) continue;
            if (l.QuantityAccepted > pl.RemainingQuantity)
                ModelState.AddModelError(string.Empty, $"\"{pl.Description}\": số nhận đạt ({l.QuantityAccepted.ToQty()}) vượt số còn lại ({pl.RemainingQuantity.ToQty()}).");
            l.Description = pl.Description; l.Unit = pl.Unit; l.Ordered = pl.Quantity; l.Received = pl.ReceivedQuantity;
        }
        if (vm.Lines.All(l => l.QuantityAccepted <= 0 && l.QuantityRejected <= 0))
            ModelState.AddModelError(string.Empty, "Nhập số lượng nhận cho ít nhất một dòng.");
        if (vm.ReceiptDate > DateTime.Today) ModelState.AddModelError(nameof(vm.ReceiptDate), "Ngày nhận không được ở tương lai");
        if (!ModelState.IsValid)
        {
            vm.PurchaseOrder = po;
            return View(vm);
        }

        var g = new GoodsReceipt
        {
            Number = await DocNumbers.NextAsync(Db.GoodsReceipts.Select(x => x.Number), "GRN"), PurchaseOrderId = po.Id,
            ReceiptDate = vm.ReceiptDate, DeliveryNoteNo = vm.DeliveryNoteNo, Note = vm.Note, ReceivedById = Me, CreatedAt = DateTime.Now
        };
        foreach (var l in vm.Lines.Where(l => l.QuantityAccepted > 0 || l.QuantityRejected > 0))
        {
            g.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = l.PurchaseOrderLineId, QuantityAccepted = l.QuantityAccepted, QuantityRejected = l.QuantityRejected, Note = l.Note });
            po.Lines.First(x => x.Id == l.PurchaseOrderLineId).ReceivedQuantity += l.QuantityAccepted;
        }
        Db.GoodsReceipts.Add(g);

        po.Status = po.Lines.All(l => l.ReceivedQuantity >= l.Quantity) ? PoStatus.Received : PoStatus.PartiallyReceived;
        await Db.SaveChangesAsync();
        Log(DocType.PO, po.Id, $"Nhận hàng {g.Number}" + (po.Status == PoStatus.Received ? " – đã nhận đủ" : " – nhận một phần"),
            vm.Lines.Any(l => l.QuantityRejected > 0) ? "Có hàng không đạt, trả lại NCC." : null);

        // Gói mua sắm (gồm nhiều PR) hoàn tất khi mọi PO của gói đã nhận đủ
        if (po.Status == PoStatus.Received && po.PackageId.HasValue)
        {
            await Packages.CompleteIfDoneAsync(Db, po.PackageId.Value);
            Log(DocType.Package, po.PackageId.Value, $"Nhận đủ hàng {po.Number}");
        }
        // PR hoàn tất khi mọi PO của PR đã nhận đủ / hủy
        else if (po.Status == PoStatus.Received && po.PurchaseRequestId.HasValue)
        {
            var allDone = await Db.PurchaseOrders.Where(p => p.PurchaseRequestId == po.PurchaseRequestId)
                .AllAsync(p => p.Status == PoStatus.Received || p.Status == PoStatus.Cancelled);
            var pr = await Db.PurchaseRequests.FindAsync(po.PurchaseRequestId);
            if (allDone && pr is { Status: PrStatus.Processing })
            {
                pr.Status = PrStatus.Completed;
                Log(DocType.PR, pr.Id, "Hoàn tất – đã nhận đủ hàng");
            }
        }
        await Db.SaveChangesAsync();

        TempData["Success"] = $"Đã lập {g.Number}. Đơn hàng: {po.Status.GetDisplayName()}." + (po.Status == PoStatus.Received ? " Hãy đánh giá nhà cung cấp." : "");
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }
}
