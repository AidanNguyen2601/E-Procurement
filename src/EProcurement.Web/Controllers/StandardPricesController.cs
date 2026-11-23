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

public class StandardPriceForm
{
    public int Id { get; set; }
    public string Number { get; set; } = "(tự sinh khi lưu)";
    [Required(ErrorMessage = "Nhập tên bảng giá"), StringLength(200)][Display(Name = "Tên bảng giá")] public string Title { get; set; } = string.Empty;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực từ")] public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    [DataType(DataType.Date)][Display(Name = "Hiệu lực đến")] public DateTime? EffectiveTo { get; set; }
    [StringLength(1000)][Display(Name = "Căn cứ xây dựng giá")] public string? Basis { get; set; }
    public int? CategoryId { get; set; }
    public List<StandardPriceLineInput> Lines { get; set; } = new();
}

public class StandardPriceLineInput
{
    public int ItemId { get; set; }
    public bool Include { get; set; } = true;
    public decimal StandardPrice { get; set; }
    public decimal CeilingPrice { get; set; }
    [StringLength(300)] public string? Basis { get; set; }
    // hiển thị
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Unit { get; set; }
    public decimal Reference { get; set; }
    public PriceStats? Stats { get; set; }
    public decimal? Current { get; set; }
}

/// <summary>
/// Bảng giá tiêu chuẩn hàng hóa, vật tư: giá chuẩn + giá trần theo thời kỳ, đề xuất từ ngân hàng giá, Giám đốc ký duyệt.
/// Khi duyệt: giá tiêu chuẩn thay giá tham khảo của mặt hàng (dùng cho dự toán PR); mua vượt giá trần phải nêu lý do + Giám đốc duyệt PO.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class StandardPricesController : ProcControllerBase
{
    public StandardPricesController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(int? categoryId, string? q)
    {
        await StandardPrices.SyncReferenceAsync(Db);
        ViewBag.Lists = await Db.StandardPriceLists.AsNoTracking().Include(x => x.CreatedBy).Include(x => x.Lines).OrderByDescending(x => x.CreatedAt).ToListAsync();
        var current = await StandardPrices.CurrentAsync(Db, null, DateTime.Today);
        var items = Db.Items.AsNoTracking().Include(i => i.Category).Where(i => current.Keys.Contains(i.Id));
        if (categoryId.HasValue) items = items.Where(i => i.CategoryId == categoryId);
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); items = items.Where(i => i.Code.Contains(k) || i.Name.Contains(k)); }
        ViewBag.Current = current;
        ViewBag.Categories = new SelectList(await Db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", categoryId);
        ViewBag.Q = q; ViewBag.CategoryId = categoryId;
        return View(await items.OrderBy(i => i.Code).ToListAsync());
    }

    private async Task FillAsync(StandardPriceForm vm, bool generate)
    {
        var ids = vm.Lines.Select(l => l.ItemId).ToList();
        var q = Db.Items.AsNoTracking().Where(i => i.IsActive);
        q = generate ? q.Where(i => vm.CategoryId == null || i.CategoryId == vm.CategoryId) : q.Where(i => ids.Contains(i.Id));
        var items = await q.OrderBy(i => i.Code).ToListAsync();
        var itemIds = items.Select(i => i.Id).ToList();
        var obs = await PriceBank.ObservationsAsync(Db, itemIds, DateTime.Today.AddMonths(-12), DateTime.Today);
        var current = await StandardPrices.CurrentAsync(Db, itemIds, DateTime.Today);
        var pct = Settings.StandardPriceCeilingPercent;
        var lines = new List<StandardPriceLineInput>();
        foreach (var i in items)
        {
            var st = PriceStats.From(i.Id, obs.Where(o => o.ItemId == i.Id).ToList());
            var existing = vm.Lines.FirstOrDefault(l => l.ItemId == i.Id);
            var l = existing ?? new StandardPriceLineInput { ItemId = i.Id };
            if (generate || existing == null)
            {
                l.StandardPrice = StandardPrices.Suggest(st, i.ReferencePrice);
                l.CeilingPrice = StandardPrices.DefaultCeiling(l.StandardPrice, pct);
                l.Basis = st != null ? $"Trung vị {st.Count} đơn giá 12 tháng (thấp nhất {st.Min.ToMoney()}, cao nhất {st.Max.ToMoney()})" : "Giá tham khảo hiện hành";
                l.Include = l.StandardPrice > 0;
            }
            l.Code = i.Code; l.Name = i.Name; l.Unit = i.Unit; l.Reference = i.ReferencePrice; l.Stats = st;
            l.Current = current.TryGetValue(i.Id, out var c) ? c.Price : null;
            lines.Add(l);
        }
        vm.Lines = lines;
        ViewBag.Categories = new SelectList(await Db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", vm.CategoryId);
        ViewBag.Percent = pct;
    }

    /// <summary>Lập bảng giá: chọn nhóm hàng → hệ thống đề xuất giá chuẩn (trung vị 12 tháng) và giá trần (+N%)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int? categoryId)
    {
        var cat = categoryId.HasValue ? await Db.Categories.FindAsync(categoryId) : null;
        var vm = new StandardPriceForm
        {
            CategoryId = categoryId, Title = $"Bảng giá tiêu chuẩn {(cat != null ? cat.Name.ToLowerInvariant() + " " : "")}từ {DateTime.Today:MM/yyyy}",
            Basis = "Ngân hàng giá (đơn hàng, báo giá, hồ sơ dự thầu, hợp đồng, khảo sát) 12 tháng gần nhất; giá tham khảo hiện hành."
        };
        await FillAsync(vm, generate: true);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var l = await Db.StandardPriceLists.Include(x => x.Lines).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        if (l.Status is not (StandardPriceStatus.Draft or StandardPriceStatus.Returned)) { TempData["Error"] = "Chỉ sửa bảng giá nháp hoặc bị trả lại."; return RedirectToAction(nameof(Details), new { id }); }
        var vm = new StandardPriceForm
        {
            Id = l.Id, Number = l.Number, Title = l.Title, EffectiveFrom = l.EffectiveFrom, EffectiveTo = l.EffectiveTo, Basis = l.Basis,
            Lines = l.Lines.Select(x => new StandardPriceLineInput { ItemId = x.ItemId, StandardPrice = x.StandardPrice, CeilingPrice = x.CeilingPrice, Basis = x.Basis }).ToList()
        };
        await FillAsync(vm, generate: false);
        return View("Form", vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(StandardPriceForm vm, bool submit = false)
    {
        StandardPriceList? list = null;
        if (vm.Id != 0)
        {
            list = await Db.StandardPriceLists.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (list == null) return NotFound();
            if (list.Status is not (StandardPriceStatus.Draft or StandardPriceStatus.Returned)) return BadRequest();
        }
        var chosen = vm.Lines.Where(l => l.Include).ToList();
        if (chosen.Count == 0) ModelState.AddModelError(string.Empty, "Chọn ít nhất một mặt hàng.");
        foreach (var l in chosen)
        {
            if (l.StandardPrice <= 0) ModelState.AddModelError(string.Empty, $"Mặt hàng #{l.ItemId}: giá tiêu chuẩn phải > 0.");
            if (l.CeilingPrice < l.StandardPrice) ModelState.AddModelError(string.Empty, $"Mặt hàng #{l.ItemId}: giá trần không được thấp hơn giá tiêu chuẩn.");
        }
        if (vm.EffectiveTo.HasValue && vm.EffectiveTo < vm.EffectiveFrom) ModelState.AddModelError(nameof(vm.EffectiveTo), "Ngày hết hiệu lực phải sau ngày bắt đầu.");
        var validIds = await Db.Items.Where(i => chosen.Select(c => c.ItemId).Contains(i.Id)).Select(i => i.Id).ToListAsync();
        if (!ModelState.IsValid) { await FillAsync(vm, generate: false); return View("Form", vm); }

        if (list == null)
        {
            list = new StandardPriceList { Number = await DocNumbers.NextAsync(Db.StandardPriceLists.Select(x => x.Number), "BG"), CreatedById = Me, CreatedAt = DateTime.Now };
            Db.StandardPriceLists.Add(list);
        }
        else { Db.StandardPriceLines.RemoveRange(list.Lines); list.Lines.Clear(); }
        list.Title = vm.Title.Trim(); list.EffectiveFrom = vm.EffectiveFrom.Date; list.EffectiveTo = vm.EffectiveTo?.Date; list.Basis = vm.Basis;
        foreach (var l in chosen.Where(c => validIds.Contains(c.ItemId)))
            list.Lines.Add(new StandardPriceLine { ItemId = l.ItemId, StandardPrice = Math.Round(l.StandardPrice, 0), CeilingPrice = Math.Round(l.CeilingPrice, 0), Basis = l.Basis });
        await Db.SaveChangesAsync();
        if (vm.Id == 0) { Log(DocType.StandardPriceList, list.Id, "Lập bảng giá tiêu chuẩn", $"{list.Lines.Count} mặt hàng"); await Db.SaveChangesAsync(); }
        if (submit) return await Submit(list.Id);
        TempData["Success"] = $"Đã lưu {list.Number}.";
        return RedirectToAction(nameof(Details), new { id = list.Id });
    }

    private Task<StandardPriceList?> LoadAsync(int id) => Db.StandardPriceLists.AsNoTracking().Include(x => x.CreatedBy).Include(x => x.DecidedBy)
        .Include(x => x.Lines).ThenInclude(l => l.Item).ThenInclude(i => i!.Category).AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);

    public async Task<IActionResult> Details(int id)
    {
        var l = await LoadAsync(id);
        if (l == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.StandardPriceList, id);
        ViewBag.CanDecide = (User.IsInRole(AppRoles.Director) || User.IsAdmin()) && l.Status == StandardPriceStatus.Submitted;
        return View(l);
    }

    public async Task<IActionResult> Print(int id)
    {
        var l = await LoadAsync(id);
        return l == null ? NotFound() : View(l);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var l = await Db.StandardPriceLists.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        if (l.Status is not (StandardPriceStatus.Draft or StandardPriceStatus.Returned) || l.Lines.Count == 0) { TempData["Error"] = "Không trình được bảng giá."; return RedirectToAction(nameof(Details), new { id }); }
        l.Status = StandardPriceStatus.Submitted; l.SubmittedAt = DateTime.Now;
        Log(DocType.StandardPriceList, id, "Trình Giám đốc phê duyệt bảng giá tiêu chuẩn", $"{l.Lines.Count} mặt hàng, hiệu lực từ {l.EffectiveFrom:dd/MM/yyyy}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã trình {l.Number}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.StandardPriceList)]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment)
    {
        var l = await Db.StandardPriceLists.Include(x => x.Lines).ThenInclude(x => x.Item).FirstOrDefaultAsync(x => x.Id == id);
        if (l == null) return NotFound();
        if (l.Status != StandardPriceStatus.Submitted) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(comment)) { TempData["Error"] = "Nhập lý do trả lại."; return RedirectToAction(nameof(Details), new { id }); }
        l.Status = approve ? StandardPriceStatus.Approved : StandardPriceStatus.Returned;
        l.DecidedById = Me; l.DecidedAt = DateTime.Now; l.DecisionNote = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        Log(DocType.StandardPriceList, id, approve ? "Giám đốc phê duyệt bảng giá tiêu chuẩn" : "Giám đốc trả lại bảng giá", l.DecisionNote);
        await Db.SaveChangesAsync();
        if (approve) await StandardPrices.SyncReferenceAsync(Db); // giá tiêu chuẩn đang hiệu lực thay giá tham khảo (dự toán PR, phân tích giá)
        TempData["Success"] = approve ? $"Đã phê duyệt {l.Number}." : $"Đã trả lại {l.Number}.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
