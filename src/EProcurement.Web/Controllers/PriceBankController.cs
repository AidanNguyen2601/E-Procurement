using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Ngân hàng giá: tra cứu đơn giá thực tế theo mặt hàng (đơn hàng, báo giá, HSDT, chào giá chỉ định, hợp đồng)
/// cùng giá khảo sát nhập tay; so với giá tiêu chuẩn / giá tham khảo hiện hành.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class PriceBankController : ProcControllerBase
{
    public PriceBankController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public record ItemRow(Item Item, PriceStats? Stats, StandardPrice? Standard);

    public async Task<IActionResult> Index(string? q, int? categoryId, int months = 12, bool onlyWithData = true)
    {
        months = Math.Clamp(months, 1, 60);
        var items = Db.Items.AsNoTracking().Include(i => i.Category).Where(i => i.IsActive);
        if (categoryId.HasValue) items = items.Where(i => i.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); items = items.Where(i => i.Code.Contains(k) || i.Name.Contains(k)); }
        var list = await items.OrderBy(i => i.Code).ToListAsync();
        var ids = list.Select(i => i.Id).ToList();
        var obs = await PriceBank.ObservationsAsync(Db, ids, DateTime.Today.AddMonths(-months), DateTime.Today);
        var std = await StandardPrices.CurrentAsync(Db, ids, DateTime.Today);
        var rows = list.Select(i => new ItemRow(i, PriceStats.From(i.Id, obs.Where(o => o.ItemId == i.Id).ToList()), std.GetValueOrDefault(i.Id)))
            .Where(r => !onlyWithData || r.Stats != null).ToList();
        ViewBag.Q = q; ViewBag.CategoryId = categoryId; ViewBag.Months = months; ViewBag.OnlyWithData = onlyWithData;
        ViewBag.Categories = new SelectList(await Db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", categoryId);
        ViewBag.Observations = obs.Count;
        return View(rows);
    }

    public async Task<IActionResult> Item(int id, int months = 24)
    {
        var item = await Db.Items.AsNoTracking().Include(i => i.Category).FirstOrDefaultAsync(i => i.Id == id);
        if (item == null) return NotFound();
        months = Math.Clamp(months, 1, 120);
        var obs = await PriceBank.ObservationsAsync(Db, new[] { id }, DateTime.Today.AddMonths(-months), DateTime.Today);
        ViewBag.Stats = PriceStats.From(id, obs);
        ViewBag.Standard = (await StandardPrices.CurrentAsync(Db, new[] { id }, DateTime.Today)).GetValueOrDefault(id);
        ViewBag.Months = months;
        ViewBag.Manual = await Db.PriceRecords.AsNoTracking().Include(r => r.CreatedBy).Include(r => r.Vendor).Where(r => r.ItemId == id).OrderByDescending(r => r.PriceDate).ToListAsync();
        ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status != VendorStatus.Blacklisted).OrderBy(v => v.Name).Select(v => new { v.Id, v.Name }).ToListAsync(), "Id", "Name");
        ViewBag.Observations = obs;
        ViewData["Title"] = $"Ngân hàng giá – {item.Code} {item.Name}";
        return View(item);
    }

    /// <summary>Ghi nhận giá khảo sát / công bố / website vào ngân hàng giá</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRecord(int itemId, int? vendorId, string? supplierName, PriceSource source, string? sourceNote, decimal unitPrice, DateTime? priceDate)
    {
        if (!await Db.Items.AnyAsync(i => i.Id == itemId)) return NotFound();
        IActionResult Back(string? err = null) { if (err != null) TempData["Error"] = err; return RedirectToAction(nameof(Item), new { id = itemId }); }
        if (unitPrice <= 0) return Back(I18n.T("Nhập đơn giá > 0."));
        if (vendorId == null && string.IsNullOrWhiteSpace(supplierName)) return Back(I18n.T("Chọn nhà cung cấp hoặc nhập tên đơn vị cung cấp."));
        if (priceDate == null || priceDate > DateTime.Today) return Back(I18n.T("Ngày giá không hợp lệ."));
        Db.PriceRecords.Add(new PriceRecord
        {
            ItemId = itemId, VendorId = vendorId, SupplierName = vendorId == null ? supplierName?.Trim() : null, Source = source, SourceNote = sourceNote?.Trim(),
            UnitPrice = Math.Round(unitPrice, 0), PriceDate = priceDate.Value, CreatedById = Me, CreatedAt = DateTime.Now
        });
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã ghi nhận giá vào ngân hàng giá.");
        return Back();
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRecord(int id)
    {
        var r = await Db.PriceRecords.FindAsync(id);
        if (r == null) return NotFound();
        Db.PriceRecords.Remove(r);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa giá khảo sát.");
        return RedirectToAction(nameof(Item), new { id = r.ItemId });
    }
}
