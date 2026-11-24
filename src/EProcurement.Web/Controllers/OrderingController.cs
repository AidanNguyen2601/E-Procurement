using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Một hạng mục đặt được trong catalog hợp đồng khung</summary>
public record CatalogLine(ContractLine Line, decimal Ordered, decimal Pending, decimal Available);

/// <summary>
/// Mua theo hợp đồng khung (catalog): người đề nghị chọn hàng từ hợp đồng khung đang hiệu lực với đơn giá đã trúng thầu;
/// tạo PR gắn hợp đồng → duyệt như thường → mua hàng lập PO đặt hàng đợt bằng một thao tác (không phải chào giá lại).
/// </summary>
[Authorize]
public class OrderingController : ProcControllerBase
{
    public OrderingController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    /// <summary>Khối lượng còn đặt được = HĐ − đã lập PO − PR theo HĐ đang chờ / đã duyệt chưa lập PO</summary>
    public static async Task<Dictionary<int, CatalogLine>> AvailabilityAsync(EProcDbContext db, Contract c, int? excludePrId = null)
    {
        var ordered = await db.PurchaseOrderLines.Where(l => l.PurchaseOrder!.ContractId == c.Id && l.PurchaseOrder.Status != PoStatus.Cancelled)
            .Select(l => new { l.ItemId, l.Description, l.Quantity }).ToListAsync();
        var pending = await db.PurchaseRequestLines.Where(l => l.PurchaseRequest!.ContractId == c.Id && l.PurchaseRequestId != excludePrId
                && (l.PurchaseRequest.Status == PrStatus.Submitted || l.PurchaseRequest.Status == PrStatus.PendingDirector || l.PurchaseRequest.Status == PrStatus.Approved))
            .Select(l => new { l.ItemId, l.Description, l.Quantity }).ToListAsync();
        return c.Lines.ToDictionary(l => l.Id, l =>
        {
            bool Same(int? item, string d) => item == l.ItemId && d == l.Description;
            var o = ordered.Where(x => Same(x.ItemId, x.Description)).Sum(x => x.Quantity);
            var p = pending.Where(x => Same(x.ItemId, x.Description)).Sum(x => x.Quantity);
            return new CatalogLine(l, o, p, Math.Max(0, l.Quantity - o - p));
        });
    }

    public async Task<IActionResult> Index()
    {
        var contracts = await Db.Contracts.AsNoTracking().Include(c => c.Vendor).Include(c => c.Lines).ThenInclude(l => l.Item)
            .Where(c => c.IsFramework && c.Status == ContractStatus.Active && c.EndDate >= DateTime.Today).OrderBy(c => c.EndDate).ToListAsync();
        var avail = new Dictionary<int, Dictionary<int, CatalogLine>>();
        foreach (var c in contracts) avail[c.Id] = await AvailabilityAsync(Db, c);
        ViewBag.Availability = avail;
        ViewData["Title"] = "Mua theo hợp đồng khung";
        return View(contracts);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Order(int contractId, List<int> lineIds, List<decimal> quantities, string? purpose, DateTime? neededDate)
    {
        var c = await Db.Contracts.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == contractId && x.IsFramework && x.Status == ContractStatus.Active);
        if (c == null || c.EndDate < DateTime.Today) { TempData["Error"] = I18n.T("Hợp đồng khung không còn hiệu lực."); return RedirectToAction(nameof(Index)); }
        var avail = await AvailabilityAsync(Db, c);
        var picked = lineIds.Zip(quantities).Where(x => x.Second > 0).ToList();
        if (picked.Count == 0) { TempData["Error"] = I18n.T("Nhập số lượng cho ít nhất một hạng mục."); return RedirectToAction(nameof(Index)); }
        foreach (var (lid, q) in picked)
            if (!avail.TryGetValue(lid, out var a) || q > a.Available) { TempData["Error"] = I18n.T("Số lượng vượt khối lượng còn đặt được ({0}).", a?.Available.ToQty() ?? "0"); return RedirectToAction(nameof(Index)); }
        var pr = new PurchaseRequest
        {
            Number = await DocNumbers.NextAsync(Db.PurchaseRequests.Select(p => p.Number), "PR"),
            Title = $"Đặt hàng theo hợp đồng khung {c.Number}", RequesterId = Me, Department = User.GetDepartment(), Status = PrStatus.Draft,
            RequestDate = DateTime.Today, NeededDate = neededDate?.Date ?? DateTime.Today.AddDays(10), ContractId = c.Id,
            Purpose = string.IsNullOrWhiteSpace(purpose) ? $"Đặt hàng theo hợp đồng khung {c.Number} – {c.Title}" : purpose.Trim()[..Math.Min(1000, purpose.Trim().Length)],
            CreatedAt = DateTime.Now
        };
        var s = 1;
        foreach (var (lid, q) in picked)
        {
            var cl = c.Lines.First(l => l.Id == lid);
            pr.Lines.Add(new PurchaseRequestLine { ItemId = cl.ItemId, Description = cl.Description, Unit = cl.Unit, Quantity = q, EstimatedPrice = cl.UnitPrice, SortOrder = s++ });
        }
        pr.Recalculate();
        Db.PurchaseRequests.Add(pr);
        await Db.SaveChangesAsync();
        Log(DocType.PR, pr.Id, $"Lập đề nghị từ catalog hợp đồng khung {c.Number}", $"{pr.Lines.Count} hạng mục · {pr.EstimatedTotal.ToMoney()} đ (đơn giá hợp đồng)");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã tạo {0} theo hợp đồng khung (nháp). Kiểm tra và gửi duyệt.", pr.Number);
        return RedirectToAction("Details", "PurchaseRequests", new { id = pr.Id });
    }
}
