using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Cổng NCC: thông báo mời sơ tuyển, nộp / rút hồ sơ dự sơ tuyển, xem kết quả</summary>
public class PortalPrequalificationsController : PortalControllerBase
{
    public PortalPrequalificationsController(EProcDbContext db) : base(db) { }

    public async Task<IActionResult> Index()
    {
        var v = MyVendor.Id;
        ViewBag.Mine = await Db.PrequalificationApplications.AsNoTracking().Where(a => a.VendorId == v).ToDictionaryAsync(a => a.PrequalificationId);
        ViewBag.MyCategories = await Db.VendorCategories.Where(c => c.VendorId == v).Select(c => c.CategoryId).ToListAsync();
        ViewData["Title"] = "Sơ tuyển nhà cung cấp";
        return View(await Db.Prequalifications.AsNoTracking().Include(p => p.Category)
            .Where(p => p.Status == PrequalificationStatus.Open || p.Applications.Any(a => a.VendorId == v))
            .OrderByDescending(p => p.Status == PrequalificationStatus.Open).ThenBy(p => p.Deadline).ToListAsync());
    }

    private Task<Prequalification?> LoadAsync(int id) => Db.Prequalifications.AsNoTracking().Include(p => p.Category).Include(p => p.Criteria)
        .FirstOrDefaultAsync(p => p.Id == id && p.Status != PrequalificationStatus.Draft && (p.Status != PrequalificationStatus.Cancelled || p.Applications.Any(a => a.VendorId == MyVendor.Id)));

    public static bool IsOpen(Prequalification p) => p.Status == PrequalificationStatus.Open && DateTime.Now < p.Deadline;

    public async Task<IActionResult> Details(int id)
    {
        var p = await LoadAsync(id);
        if (p == null) return NotFound();
        p.Criteria = p.Criteria.OrderBy(c => c.SortOrder).ToList();
        ViewBag.Application = await Db.PrequalificationApplications.AsNoTracking().Include(a => a.Documents).Include(a => a.Scores)
            .FirstOrDefaultAsync(a => a.PrequalificationId == id && a.VendorId == MyVendor.Id);
        ViewBag.Documents = await Db.VendorDocuments.AsNoTracking().Where(d => d.VendorId == MyVendor.Id && d.Status != VendorDocumentStatus.Rejected).OrderBy(d => d.Type).ToListAsync();
        ViewBag.CanApply = CanTrade && IsOpen(p);
        ViewData["Title"] = p.Number;
        return View(p);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> Apply(int id, string? note, List<int> documentIds, VendorDocumentType? newDocType, string? newDocNumber, DateTime? newDocExpiry, IFormFile? newDocFile)
    {
        var p = await LoadAsync(id);
        if (p == null) return NotFound();
        IActionResult Back(string err) { TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanTrade) return Back("Tài khoản chưa được xác nhận thuộc nhà cung cấp.");
        if (!IsOpen(p)) return Back("Đợt sơ tuyển không còn nhận hồ sơ.");
        if (MyVendor.Status == VendorStatus.Suspended) return Back("Nhà cung cấp đang bị tạm ngưng giao dịch.");
        var text = note?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return Back("Nhập thuyết minh năng lực, kinh nghiệm theo các tiêu chí.");
        if (text.Length > 2000) return Back("Thuyết minh tối đa 2000 ký tự.");

        var a = await Db.PrequalificationApplications.Include(x => x.Documents).FirstOrDefaultAsync(x => x.PrequalificationId == id && x.VendorId == MyVendor.Id);
        if (newDocFile is { Length: > 0 })
        {
            if (CheckFile(newDocFile) is string ferr) return Back(ferr);
            var doc = new VendorDocument
            {
                VendorId = MyVendor.Id, Type = newDocType ?? VendorDocumentType.Other, Number = newDocNumber?.Trim(), ExpiryDate = newDocExpiry,
                FileName = Path.GetFileName(newDocFile.FileName), ContentType = string.IsNullOrEmpty(newDocFile.ContentType) ? "application/octet-stream" : newDocFile.ContentType,
                Size = newDocFile.Length, Data = await ReadAsync(newDocFile), UploadedByVendorUserId = Me.Id, UploadedAt = DateTime.Now
            };
            Db.VendorDocuments.Add(doc);
            await Db.SaveChangesAsync();
            documentIds.Add(doc.Id);
        }
        var valid = await Db.VendorDocuments.Where(d => d.VendorId == MyVendor.Id && documentIds.Contains(d.Id)).Select(d => d.Id).ToListAsync();
        if (valid.Count == 0) return Back("Chọn hoặc tải lên ít nhất một hồ sơ chứng minh.");
        var resubmit = a != null;
        if (a == null) { a = new PrequalificationApplication { PrequalificationId = id, VendorId = MyVendor.Id }; Db.PrequalificationApplications.Add(a); }
        else { Db.PrequalificationDocuments.RemoveRange(a.Documents); a.Documents.Clear(); }
        a.VendorUserId = Me.Id; a.SubmittedAt = DateTime.Now; a.Note = text; a.Status = PrequalificationResult.Submitted;
        foreach (var d in valid) a.Documents.Add(new PrequalificationDocument { VendorDocumentId = d });
        await Db.SaveChangesAsync();
        var buyer = await Db.Prequalifications.Where(x => x.Id == id).Select(x => x.CreatedById).FirstAsync();
        await NotifyBuyersAsync(new[] { buyer }, $"{MyVendor.Name} {(resubmit ? "nộp lại" : "nộp")} hồ sơ dự sơ tuyển {p.Number}", $"{valid.Count} tài liệu", $"/Prequalifications/Details/{id}");
        TempData["Success"] = $"Đã nộp hồ sơ dự sơ tuyển lúc {a.SubmittedAt:dd/MM/yyyy HH:mm}. Có thể cập nhật trước hạn {p.Deadline:dd/MM/yyyy HH:mm}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id)
    {
        var p = await LoadAsync(id);
        var a = await Db.PrequalificationApplications.FirstOrDefaultAsync(x => x.PrequalificationId == id && x.VendorId == MyVendor.Id);
        if (p == null || a == null || !IsOpen(p)) { TempData["Error"] = "Không rút được hồ sơ."; return RedirectToAction(nameof(Details), new { id }); }
        a.Status = PrequalificationResult.Withdrawn;
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã rút hồ sơ dự sơ tuyển.";
        return RedirectToAction(nameof(Details), new { id });
    }
}
