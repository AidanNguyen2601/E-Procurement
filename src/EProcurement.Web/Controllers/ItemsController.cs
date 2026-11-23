using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Danh mục hàng hóa & nhóm hàng (mọi người xem, bộ phận mua hàng cập nhật)</summary>
public class ItemsController : ProcControllerBase
{
    public ItemsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(string? keyword, int? categoryId)
    {
        var q = Db.Items.Include(i => i.Category).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(i => i.Name.Contains(keyword) || i.Code.Contains(keyword));
        if (categoryId.HasValue) q = q.Where(i => i.CategoryId == categoryId);
        ViewBag.Keyword = keyword;
        ViewBag.Categories = new SelectList(await Db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", categoryId);
        ViewBag.CategoryList = await Db.Categories.OrderBy(c => c.Code).ToListAsync();
        return View(await q.OrderBy(i => i.Code).ToListAsync());
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create()
    {
        await LoadCategories(null);
        return View("Form", new Item());
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var i = await Db.Items.FindAsync(id);
        if (i == null) return NotFound();
        await LoadCategories(i.CategoryId);
        return View("Form", i);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Item model)
    {
        if (await Db.Items.AnyAsync(i => i.Code == model.Code && i.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), "Mã hàng đã tồn tại");
        if (!ModelState.IsValid)
        {
            await LoadCategories(model.CategoryId);
            return View("Form", model);
        }
        if (model.Id == 0) Db.Items.Add(model);
        else
        {
            var i = await Db.Items.FindAsync(model.Id);
            if (i == null) return NotFound();
            i.Code = model.Code; i.Name = model.Name; i.CategoryId = model.CategoryId; i.Unit = model.Unit;
            i.ReferencePrice = model.ReferencePrice; i.Specification = model.Specification; i.IsActive = model.IsActive;
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã lưu hàng hóa.";
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategory(Category model)
    {
        if (string.IsNullOrWhiteSpace(model.Code) || string.IsNullOrWhiteSpace(model.Name))
            TempData["Error"] = "Nhập mã và tên nhóm hàng.";
        else if (await Db.Categories.AnyAsync(c => c.Code == model.Code && c.Id != model.Id))
            TempData["Error"] = "Mã nhóm hàng đã tồn tại.";
        else
        {
            if (model.Id == 0) Db.Categories.Add(model);
            else
            {
                var c = await Db.Categories.FindAsync(model.Id);
                if (c == null) return NotFound();
                c.Code = model.Code; c.Name = model.Name;
            }
            await Db.SaveChangesAsync();
            TempData["Success"] = "Đã lưu nhóm hàng.";
        }
        return RedirectToAction(nameof(Index));
    }

    private async Task LoadCategories(int? selected) =>
        ViewBag.Categories = new SelectList(await Db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name", selected);
}
