using System.ComponentModel.DataAnnotations;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Form lịch sử giá (giống màn "Lịch sử giá"): nguồn, NCC, hàng hóa, ĐVT, mã phụ, tiền tệ, đơn giá vật tư / thi công – dịch vụ kèm thuế suất, mốc thời gian, gói thầu / hợp đồng liên quan, vùng, ghi chú</summary>
public class PriceRecordForm
{
    public int Id { get; set; }
    [Display(Name = "Nguồn giá")] public PriceSource Source { get; set; } = PriceSource.Survey;
    [Display(Name = "Nhà cung cấp")] public int? VendorId { get; set; }
    [StringLength(200)][Display(Name = "Đơn vị cung cấp (ngoài danh sách NCC)")] public string? SupplierName { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "Chọn mã hàng hóa dịch vụ.")][Display(Name = "Mã hàng hóa dịch vụ")] public int ItemId { get; set; }
    [StringLength(30)][Display(Name = "Đơn vị tính")] public string? Unit { get; set; }
    [StringLength(50)][Display(Name = "Mã phụ")] public string? SubCode { get; set; }
    [Required(ErrorMessage = "Chọn loại tiền tệ"), StringLength(10)][Display(Name = "Loại tiền tệ")] public string Currency { get; set; } = "VND";
    [Range(0, 1_000_000_000_000)][Display(Name = "Đơn giá vật tư")] public decimal MaterialPrice { get; set; }
    [Range(0, 100)][Display(Name = "Thuế suất vật tư %")] public decimal MaterialVatPercent { get; set; }
    [Range(0, 1_000_000_000_000)][Display(Name = "Đơn giá thi công / dịch vụ")] public decimal ServicePrice { get; set; }
    [Range(0, 100)][Display(Name = "Thuế suất thi công / dịch vụ %")] public decimal ServiceVatPercent { get; set; }
    [DataType(DataType.Date)][Display(Name = "Mốc thời gian")] public DateTime PriceDate { get; set; } = DateTime.Today;
    [Display(Name = "Gói thầu liên quan")] public int? TenderId { get; set; }
    [Display(Name = "Hợp đồng liên quan")] public int? ContractId { get; set; }
    [StringLength(100)][Display(Name = "Vùng")] public string? Region { get; set; }
    [StringLength(300)][Display(Name = "Diễn giải nguồn (số báo giá / đường dẫn)")] public string? SourceNote { get; set; }
    [StringLength(1000)][Display(Name = "Ghi chú")] public string? Note { get; set; }

    public decimal UnitPrice => MaterialPrice + ServicePrice;
    public decimal PriceWithTax => Math.Round(MaterialPrice * (1 + MaterialVatPercent / 100m) + ServicePrice * (1 + ServiceVatPercent / 100m), 2);

    public static PriceRecordForm From(PriceRecord r) => new()
    {
        Id = r.Id, Source = r.Source, VendorId = r.VendorId, SupplierName = r.SupplierName, ItemId = r.ItemId, Unit = r.Unit, SubCode = r.SubCode, Currency = string.IsNullOrEmpty(r.Currency) ? "VND" : r.Currency,
        MaterialPrice = r.MaterialPrice == 0 && r.ServicePrice == 0 ? r.UnitPrice : r.MaterialPrice, MaterialVatPercent = r.MaterialVatPercent, ServicePrice = r.ServicePrice, ServiceVatPercent = r.ServiceVatPercent,
        PriceDate = r.PriceDate, TenderId = r.TenderId, ContractId = r.ContractId, Region = r.Region, SourceNote = r.SourceNote, Note = r.Note
    };

    public void ApplyTo(PriceRecord r)
    {
        static string? T(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        r.Source = Source; r.VendorId = VendorId; r.SupplierName = VendorId == null ? T(SupplierName) : null; r.ItemId = ItemId; r.Unit = T(Unit); r.SubCode = T(SubCode);
        r.Currency = T(Currency)?.ToUpperInvariant() ?? "VND"; r.MaterialPrice = Math.Round(MaterialPrice, 2); r.MaterialVatPercent = MaterialVatPercent;
        r.ServicePrice = Math.Round(ServicePrice, 2); r.ServiceVatPercent = ServiceVatPercent; r.UnitPrice = r.MaterialPrice + r.ServicePrice;
        r.PriceDate = PriceDate.Date; r.TenderId = TenderId; r.ContractId = ContractId; r.Region = T(Region); r.SourceNote = T(SourceNote); r.Note = T(Note);
    }
}

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

    // ------------------------------------------------------------------ Lịch sử giá (giá ghi nhận tay: khảo sát, công bố, chọn thầu, hợp đồng…)
    public async Task<IActionResult> Records(string? q, int? vendorId, PriceSource? source, string? region, string? currency, DateTime? from, DateTime? to)
    {
        var query = Db.PriceRecords.AsNoTracking().Include(r => r.Item).Include(r => r.Vendor).Include(r => r.Tender).Include(r => r.Contract).AsQueryable();
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(r => r.Item!.Code.Contains(k) || r.Item.Name.Contains(k) || (r.SubCode != null && r.SubCode.Contains(k))); }
        if (vendorId.HasValue) query = query.Where(r => r.VendorId == vendorId);
        if (source.HasValue) query = query.Where(r => r.Source == source);
        if (!string.IsNullOrWhiteSpace(region)) query = query.Where(r => r.Region == region);
        if (!string.IsNullOrWhiteSpace(currency)) query = query.Where(r => (r.Currency ?? "VND") == currency);
        if (from.HasValue) query = query.Where(r => r.PriceDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(r => r.PriceDate < to.Value.Date.AddDays(1));
        ViewBag.Q = q; ViewBag.VendorId = vendorId; ViewBag.Source = source; ViewBag.Region = region; ViewBag.Currency = currency; ViewBag.From = from; ViewBag.To = to;
        ViewBag.Vendors = new SelectList(await Db.Vendors.OrderBy(v => v.Name).Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name", vendorId);
        ViewBag.Regions = await Db.PriceRecords.Where(r => r.Region != null).Select(r => r.Region!).Distinct().OrderBy(x => x).ToListAsync();
        ViewBag.Currencies = await new CatalogService(Db).EntriesAsync(CatalogType.Currency);
        return View(await query.OrderByDescending(r => r.PriceDate).ThenByDescending(r => r.Id).Take(500).ToListAsync());
    }

    private async Task FillRecordFormAsync(PriceRecordForm f)
    {
        ViewBag.Items = await Db.Items.AsNoTracking().Where(i => i.IsActive || i.Id == f.ItemId).OrderBy(i => i.Code).Select(i => new { i.Id, i.Code, i.Name, i.Unit }).ToListAsync();
        ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status != VendorStatus.Blacklisted || v.Id == f.VendorId).OrderBy(v => v.Code).Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name", f.VendorId);
        ViewBag.Currencies = await new CatalogService(Db).EntriesAsync(CatalogType.Currency);
        ViewBag.Units = await new CatalogService(Db).NamesAsync(CatalogType.Unit);
        ViewBag.Tenders = new SelectList(await Db.Tenders.Where(t => t.Status != TenderStatus.Cancelled || t.Id == f.TenderId).OrderByDescending(t => t.Id).Take(300).Select(t => new { t.Id, Name = t.Number + " – " + t.Title }).ToListAsync(), "Id", "Name", f.TenderId);
        ViewBag.Contracts = new SelectList(await Db.Contracts.OrderByDescending(c => c.Id).Take(300).Select(c => new { c.Id, Name = c.Number + " – " + c.Title }).ToListAsync(), "Id", "Name", f.ContractId);
        ViewBag.SubCodes = await Db.PriceRecords.Where(r => r.SubCode != null && (f.ItemId == 0 || r.ItemId == f.ItemId)).Select(r => r.SubCode!).Distinct().Take(100).ToListAsync();
        ViewBag.Regions = await Db.PriceRecords.Where(r => r.Region != null).Select(r => r.Region!).Distinct().OrderBy(x => x).Take(100).ToListAsync();
    }

    /// <summary>Xem / thêm / sửa một bản ghi lịch sử giá. edit=false: chỉ xem (mọi trường khóa).</summary>
    public async Task<IActionResult> Record(int? id, int? itemId, bool edit = false)
    {
        PriceRecordForm f;
        if (id.HasValue)
        {
            var r = await Db.PriceRecords.AsNoTracking().Include(x => x.CreatedBy).FirstOrDefaultAsync(x => x.Id == id);
            if (r == null) return NotFound();
            f = PriceRecordForm.From(r);
            ViewBag.Record = r;
        }
        else
        {
            var item = itemId.HasValue ? await Db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId) : null;
            f = new PriceRecordForm { ItemId = item?.Id ?? 0, Unit = item?.Unit, Currency = "VND", PriceDate = DateTime.Today, MaterialVatPercent = 10, ServiceVatPercent = 10 };
            edit = true;
        }
        ViewBag.Edit = edit && User.IsPurchasing();
        await FillRecordFormAsync(f);
        return View(f);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRecord(PriceRecordForm f)
    {
        var item = await Db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == f.ItemId);
        if (item == null) ModelState.AddModelError(nameof(f.ItemId), I18n.T("Chọn mã hàng hóa dịch vụ."));
        if (f.VendorId == null && string.IsNullOrWhiteSpace(f.SupplierName)) ModelState.AddModelError(nameof(f.VendorId), I18n.T("Chọn nhà cung cấp hoặc nhập tên đơn vị cung cấp."));
        if (f.VendorId != null && !await Db.Vendors.AnyAsync(v => v.Id == f.VendorId)) ModelState.AddModelError(nameof(f.VendorId), I18n.T("Nhà cung cấp không hợp lệ."));
        if (f.MaterialPrice + f.ServicePrice <= 0) ModelState.AddModelError(nameof(f.MaterialPrice), I18n.T("Nhập đơn giá vật tư và / hoặc đơn giá thi công / dịch vụ lớn hơn 0."));
        if (f.PriceDate > DateTime.Today) ModelState.AddModelError(nameof(f.PriceDate), I18n.T("Mốc thời gian không được ở tương lai."));
        if (f.TenderId != null && !await Db.Tenders.AnyAsync(t => t.Id == f.TenderId)) ModelState.AddModelError(nameof(f.TenderId), I18n.T("Gói thầu không hợp lệ."));
        if (f.ContractId != null && !await Db.Contracts.AnyAsync(c => c.Id == f.ContractId)) ModelState.AddModelError(nameof(f.ContractId), I18n.T("Hợp đồng không hợp lệ."));
        var currencies = await new CatalogService(Db).EntriesAsync(CatalogType.Currency);
        if (!string.Equals(f.Currency, "VND", StringComparison.OrdinalIgnoreCase) && !currencies.Any(c => string.Equals(c.Code ?? c.Name, f.Currency, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError(nameof(f.Currency), I18n.T("Chọn loại tiền tệ trong danh mục."));
        if (!ModelState.IsValid) { ViewBag.Edit = true; await FillRecordFormAsync(f); return View("Record", f); }

        PriceRecord r;
        if (f.Id > 0)
        {
            r = (await Db.PriceRecords.FindAsync(f.Id))!;
            if (r == null) return NotFound();
            r.UpdatedAt = DateTime.Now;
        }
        else { r = new PriceRecord { CreatedById = Me, CreatedAt = DateTime.Now }; Db.PriceRecords.Add(r); }
        f.ApplyTo(r);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu lịch sử giá.");
        return RedirectToAction(nameof(Record), new { id = r.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRecord(int id, string? back)
    {
        var r = await Db.PriceRecords.FindAsync(id);
        if (r == null) return NotFound();
        Db.PriceRecords.Remove(r);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa giá khảo sát.");
        return back == "records" ? RedirectToAction(nameof(Records)) : RedirectToAction(nameof(Item), new { id = r.ItemId });
    }
}
