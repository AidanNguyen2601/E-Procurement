using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class VendorsController : ProcControllerBase
{
    public VendorsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(string? keyword, VendorStatus? status, int? categoryId)
    {
        var q = Db.Vendors.Include(v => v.Categories).ThenInclude(c => c.Category).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
            q = q.Where(v => v.Name.Contains(keyword) || v.Code.Contains(keyword) || v.TaxCode.Contains(keyword));
        if (status.HasValue) q = q.Where(v => v.Status == status);
        if (categoryId.HasValue) q = q.Where(v => v.Categories.Any(c => c.CategoryId == categoryId));
        ViewBag.Keyword = keyword; ViewBag.Status = status; ViewBag.CategoryId = categoryId;
        ViewBag.Categories = await Db.Categories.OrderBy(c => c.Name).ToListAsync();
        var list = await q.OrderBy(v => v.Code).ToListAsync();
        var ids = list.Select(v => v.Id).ToList();
        ViewBag.Spend = (await Db.PurchaseOrders.Where(p => p.VendorId != null && ids.Contains(p.VendorId.Value)
                                                          && p.Status >= PoStatus.Approved && p.Status != PoStatus.Cancelled)
                .Select(p => new { p.VendorId, p.SubTotal }).ToListAsync())
            .GroupBy(x => x.VendorId!.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.SubTotal));
        return View(list);
    }

    public async Task<IActionResult> Details(int id)
    {
        var v = await Db.Vendors.Include(x => x.Categories).ThenInclude(c => c.Category).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();
        ViewBag.Orders = await Db.PurchaseOrders.Where(p => p.VendorId == id).OrderByDescending(p => p.OrderDate).ToListAsync();
        ViewBag.Evaluations = await Db.VendorEvaluations.Include(e => e.Evaluator).Include(e => e.PurchaseOrder)
            .Where(e => e.VendorId == id).OrderByDescending(e => e.CreatedAt).ToListAsync();
        ViewBag.Rfqs = await Db.RfqVendors.Include(r => r.Rfq).Where(r => r.VendorId == id).OrderByDescending(r => r.RfqId).ToListAsync();
        ViewBag.PortalUsers = await Db.VendorUsers.AsNoTracking().Where(u => u.VendorId == id).OrderBy(u => u.CreatedAt).ToListAsync();
        ViewBag.Documents = await Db.VendorDocuments.AsNoTracking().Include(d => d.VerifiedBy).Where(d => d.VendorId == id).OrderBy(d => d.Type).ThenByDescending(d => d.UploadedAt)
            .Select(d => new VendorDocument { Id = d.Id, Type = d.Type, Number = d.Number, IssueDate = d.IssueDate, ExpiryDate = d.ExpiryDate, FileName = d.FileName, Size = d.Size,
                Status = d.Status, Note = d.Note, UploadedAt = d.UploadedAt, VerifiedAt = d.VerifiedAt, VerifiedBy = d.VerifiedBy }).ToListAsync();
        return View(v);
    }

    // ------------------------------------------------------------------ Cổng NCC: tài khoản & hồ sơ
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> PortalUser(int id, int userId, string op)
    {
        var u = await Db.VendorUsers.FirstOrDefaultAsync(x => x.Id == userId && x.VendorId == id);
        if (u == null) return NotFound();
        switch (op)
        {
            case "approve": u.IsApproved = true; u.IsActive = true; break;
            case "lock": u.IsActive = false; break;
            case "unlock": u.IsActive = true; u.FailedLogins = 0; u.LockoutEnd = null; break;
            default: return BadRequest();
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = op switch { "approve" => $"Đã xác nhận tài khoản {u.Email}.", "lock" => $"Đã khóa tài khoản {u.Email}.", _ => $"Đã mở khóa {u.Email}." };
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
    public async Task<IActionResult> Document(int id, int docId)
    {
        var d = await Db.VendorDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == docId && x.VendorId == id);
        return d == null ? NotFound() : File(d.Data, d.ContentType, d.FileName);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyDocument(int id, int docId, bool accept, string? note)
    {
        var d = await Db.VendorDocuments.FirstOrDefaultAsync(x => x.Id == docId && x.VendorId == id);
        if (d == null) return NotFound();
        if (!accept && string.IsNullOrWhiteSpace(note)) { TempData["Error"] = "Nhập lý do không chấp nhận hồ sơ."; return RedirectToAction(nameof(Details), new { id }); }
        d.Status = accept ? VendorDocumentStatus.Verified : VendorDocumentStatus.Rejected;
        d.Note = string.IsNullOrWhiteSpace(note) ? d.Note : note.Trim(); d.VerifiedById = Me; d.VerifiedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        TempData["Success"] = accept ? "Đã xác minh hồ sơ." : "Đã đánh dấu hồ sơ không chấp nhận.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create()
    {
        var n = await Db.Vendors.CountAsync() + 1;
        var v = new Vendor { Code = $"NCC{n:000}", PaymentTerms = "Thanh toán 30 ngày sau khi nhận hàng và hóa đơn" };
        while (await Db.Vendors.AnyAsync(x => x.Code == v.Code)) v.Code = $"NCC{++n:000}";
        ViewBag.AllCategories = await Db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.Selected = new List<int>();
        return View("Form", v);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var v = await Db.Vendors.Include(x => x.Categories).FirstOrDefaultAsync(x => x.Id == id);
        if (v == null) return NotFound();
        ViewBag.AllCategories = await Db.Categories.OrderBy(c => c.Name).ToListAsync();
        ViewBag.Selected = v.Categories.Select(c => c.CategoryId).ToList();
        return View("Form", v);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Vendor model, List<int> categoryIds)
    {
        if (await Db.Vendors.AnyAsync(v => v.Code == model.Code && v.Id != model.Id))
            ModelState.AddModelError(nameof(model.Code), "Mã NCC đã tồn tại");
        if (await Db.Vendors.AnyAsync(v => v.TaxCode == model.TaxCode && v.Id != model.Id))
            ModelState.AddModelError(nameof(model.TaxCode), "Mã số thuế đã được đăng ký cho NCC khác");
        if (categoryIds.Count == 0) ModelState.AddModelError(string.Empty, "Chọn ít nhất một nhóm hàng NCC cung cấp.");
        if (!ModelState.IsValid)
        {
            ViewBag.AllCategories = await Db.Categories.OrderBy(c => c.Name).ToListAsync();
            ViewBag.Selected = categoryIds;
            return View("Form", model);
        }

        Vendor v;
        if (model.Id == 0)
        {
            v = model;
            v.CreatedAt = DateTime.Now;
            v.Rating = 0;
            v.Categories = new List<VendorCategory>();
            Db.Vendors.Add(v);
            await Db.SaveChangesAsync();
        }
        else
        {
            v = await Db.Vendors.Include(x => x.Categories).FirstOrDefaultAsync(x => x.Id == model.Id) ?? throw new InvalidOperationException();
            v.Code = model.Code; v.Name = model.Name; v.TaxCode = model.TaxCode; v.Address = model.Address; v.Phone = model.Phone;
            v.Email = model.Email; v.ContactPerson = model.ContactPerson; v.ContactPhone = model.ContactPhone; v.BankAccount = model.BankAccount;
            v.BankName = model.BankName; v.PaymentTerms = model.PaymentTerms; v.Status = model.Status; v.Note = model.Note;
        }

        // Đồng bộ nhóm hàng
        var current = await Db.VendorCategories.Where(c => c.VendorId == v.Id).ToListAsync();
        Db.VendorCategories.RemoveRange(current.Where(c => !categoryIds.Contains(c.CategoryId)));
        foreach (var cid in categoryIds.Where(cid => current.All(c => c.CategoryId != cid)))
            Db.VendorCategories.Add(new VendorCategory { VendorId = v.Id, CategoryId = cid });

        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu nhà cung cấp {v.Name}.";
        return RedirectToAction(nameof(Details), new { id = v.Id });
    }

    /// <summary>Thẩm định / tạm ngưng / đưa vào danh sách đen</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetStatus(int id, VendorStatus status, string? note)
    {
        var v = await Db.Vendors.FindAsync(id);
        if (v == null) return NotFound();
        v.Status = status;
        if (!string.IsNullOrWhiteSpace(note)) v.Note = $"{DateTime.Today:dd/MM/yyyy}: {note}" + (string.IsNullOrEmpty(v.Note) ? "" : "\n" + v.Note);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"{v.Name}: {status.GetDisplayName()}.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
