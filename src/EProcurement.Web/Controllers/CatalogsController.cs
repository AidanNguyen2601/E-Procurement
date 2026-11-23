using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Danh mục dùng chung: phòng ban, đơn vị tính, điều khoản thanh toán, loại hợp đồng, địa điểm giao hàng, ngân hàng, bảo hành.
/// Mua hàng quản lý; danh mục phòng ban chỉ Quản trị được sửa.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing)]
public class CatalogsController : Controller
{
    private readonly EProcDbContext _db;
    public CatalogsController(EProcDbContext db) => _db = db;

    public class CatalogPage
    {
        public CatalogInfo Info { get; set; } = null!;
        public List<CatalogEntry> Entries { get; set; } = new();
        public Dictionary<CatalogType, int> Counts { get; set; } = new();
        /// <summary>Số bản ghi đang dùng từng giá trị (chỉ với danh mục bắt buộc chọn)</summary>
        public Dictionary<string, int> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool CanEdit { get; set; }
        public CatalogEntry? Editing { get; set; }
        public List<Category> Categories { get; set; } = new();
    }

    public async Task<IActionResult> Index(CatalogType type = CatalogType.Department, int? edit = null)
    {
        var info = Catalogs.Info(type);
        var vm = new CatalogPage
        {
            Info = info, CanEdit = CanEdit(info),
            Entries = await _db.CatalogEntries.AsNoTracking().Where(e => e.Type == type).OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ToListAsync(),
            Counts = await _db.CatalogEntries.GroupBy(e => e.Type).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N),
            Categories = await _db.Categories.AsNoTracking().OrderBy(c => c.Code).ToListAsync()
        };
        vm.Editing = edit.HasValue ? vm.Entries.FirstOrDefault(e => e.Id == edit) : null;
        vm.Usage = await UsageAsync(type);
        ViewBag.ItemCount = await _db.Items.CountAsync();
        ViewBag.VendorCount = await _db.Vendors.CountAsync();
        ViewBag.UserCount = await _db.Users.CountAsync();
        ViewData["Title"] = "Danh mục dùng chung";
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CatalogEntry model)
    {
        var info = Catalogs.Info(model.Type);
        if (!CanEdit(info)) return Forbid();
        var name = model.Name?.Trim() ?? "";
        string? error = null;
        if (name.Length == 0) error = "Nhập tên / giá trị.";
        else if (name.Length > info.MaxLength) error = $"{info.Label}: tối đa {info.MaxLength} ký tự.";
        else if (await _db.CatalogEntries.AnyAsync(e => e.Type == model.Type && e.Name == name && e.Id != model.Id)) error = $"\"{name}\" đã có trong danh mục.";
        if (error != null) { TempData["Error"] = error; return RedirectToAction(nameof(Index), new { type = model.Type, edit = model.Id == 0 ? (int?)null : model.Id }); }

        CatalogEntry entry;
        string? renamedFrom = null;
        if (model.Id == 0)
        {
            var max = await _db.CatalogEntries.Where(e => e.Type == model.Type).MaxAsync(e => (int?)e.SortOrder) ?? 0;
            entry = new CatalogEntry { Type = model.Type, SortOrder = model.SortOrder > 0 ? model.SortOrder : max + 10 };
            _db.CatalogEntries.Add(entry);
        }
        else
        {
            entry = await _db.CatalogEntries.FirstOrDefaultAsync(e => e.Id == model.Id && e.Type == model.Type) ?? throw new BadHttpRequestException("Không tìm thấy");
            if (!string.Equals(entry.Name, name, StringComparison.Ordinal)) renamedFrom = entry.Name;
            entry.SortOrder = model.SortOrder;
            entry.IsActive = model.IsActive;
        }
        entry.Name = name;
        entry.Code = string.IsNullOrWhiteSpace(model.Code) ? null : model.Code.Trim();
        entry.Note = string.IsNullOrWhiteSpace(model.Note) ? null : model.Note.Trim();

        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();
        var cascaded = renamedFrom == null ? 0 : await CascadeRenameAsync(model.Type, renamedFrom, name);
        await tx.CommitAsync();
        TempData["Success"] = $"Đã lưu \"{name}\"." + (cascaded > 0 ? $" Cập nhật tên mới cho {cascaded} bản ghi đang dùng." : "");
        return RedirectToAction(nameof(Index), new { type = model.Type });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(int id)
    {
        var e = await _db.CatalogEntries.FindAsync(id);
        if (e == null) return NotFound();
        if (!CanEdit(Catalogs.Info(e.Type))) return Forbid();
        e.IsActive = !e.IsActive;
        await _db.SaveChangesAsync();
        TempData["Success"] = e.IsActive ? $"Đã dùng lại \"{e.Name}\"." : $"Đã ngưng sử dụng \"{e.Name}\" (dữ liệu cũ giữ nguyên, không chọn mới được).";
        return RedirectToAction(nameof(Index), new { type = e.Type });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var e = await _db.CatalogEntries.FindAsync(id);
        if (e == null) return NotFound();
        if (!CanEdit(Catalogs.Info(e.Type))) return Forbid();
        var used = (await UsageAsync(e.Type)).GetValueOrDefault(e.Name);
        if (used > 0)
        {
            TempData["Error"] = $"\"{e.Name}\" đang được dùng ở {used} bản ghi – chọn \"Ngưng dùng\" thay vì xóa.";
            return RedirectToAction(nameof(Index), new { type = e.Type });
        }
        _db.CatalogEntries.Remove(e);
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã xóa \"{e.Name}\".";
        return RedirectToAction(nameof(Index), new { type = e.Type });
    }

    private bool CanEdit(CatalogInfo info) => !info.AdminOnly || User.IsAdmin();

    /// <summary>Số bản ghi danh mục / dữ liệu gốc đang dùng mỗi giá trị (chứng từ đã lập giữ nguyên văn bản, không tính)</summary>
    private async Task<Dictionary<string, int>> UsageAsync(CatalogType t)
    {
        var rows = t switch
        {
            CatalogType.Department => (await _db.Users.GroupBy(u => u.Department).Select(g => new { g.Key, N = g.Count() }).ToListAsync())
                .Concat(await _db.PurchaseRequests.GroupBy(p => p.Department).Select(g => new { g.Key, N = g.Count() }).ToListAsync()).ToList(),
            CatalogType.Unit => await _db.Items.GroupBy(i => i.Unit).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.PaymentTerm => await _db.Vendors.Where(v => v.PaymentTerms != null).GroupBy(v => v.PaymentTerms!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.Bank => await _db.Vendors.Where(v => v.BankName != null).GroupBy(v => v.BankName!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.FundingSource => await _db.ProcurementPackages.Where(p => p.FundingSource != null).GroupBy(p => p.FundingSource!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.ImplementationTeam => await _db.ProcurementPackages.Where(p => p.Team != null).GroupBy(p => p.Team!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.OrderType => await _db.PurchaseOrders.Where(p => p.OrderType != null).GroupBy(p => p.OrderType!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.BusinessSector => await _db.Vendors.Where(v => v.BusinessSector != null).GroupBy(v => v.BusinessSector!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.Origin => await _db.Items.Where(i => i.Origin != null).GroupBy(i => i.Origin!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            CatalogType.Brand => await _db.Items.Where(i => i.Brand != null).GroupBy(i => i.Brand!).Select(g => new { g.Key, N = g.Count() }).ToListAsync(),
            _ => new()
        };
        return rows.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => x.N), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Đổi tên → cập nhật dữ liệu gốc đang dùng tên cũ (người dùng, PR, hàng hóa, NCC). Chứng từ đã lập không đổi.</summary>
    private async Task<int> CascadeRenameAsync(CatalogType t, string from, string to) => t switch
    {
        CatalogType.Department => await _db.Users.Where(u => u.Department == from).ExecuteUpdateAsync(s => s.SetProperty(u => u.Department, to))
                                  + await _db.PurchaseRequests.Where(p => p.Department == from).ExecuteUpdateAsync(s => s.SetProperty(p => p.Department, to)),
        CatalogType.Unit => await _db.Items.Where(i => i.Unit == from).ExecuteUpdateAsync(s => s.SetProperty(i => i.Unit, to)),
        CatalogType.PaymentTerm => await _db.Vendors.Where(v => v.PaymentTerms == from).ExecuteUpdateAsync(s => s.SetProperty(v => v.PaymentTerms, to)),
        CatalogType.Bank => await _db.Vendors.Where(v => v.BankName == from).ExecuteUpdateAsync(s => s.SetProperty(v => v.BankName, to)),
        CatalogType.BusinessSector => await _db.Vendors.Where(v => v.BusinessSector == from).ExecuteUpdateAsync(s => s.SetProperty(v => v.BusinessSector, to)),
        CatalogType.Origin => await _db.Items.Where(i => i.Origin == from).ExecuteUpdateAsync(s => s.SetProperty(i => i.Origin, to)),
        CatalogType.Brand => await _db.Items.Where(i => i.Brand == from).ExecuteUpdateAsync(s => s.SetProperty(i => i.Brand, to)),
        _ => 0
    };
}
