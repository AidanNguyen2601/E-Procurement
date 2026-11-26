using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Đánh giá rủi ro gói trước khi trình KHLCNT / mời thầu</summary>
public partial class PackagesController
{
    public async Task<IActionResult> Risk(int id)
    {
        var p = await Packages.LoadAsync(Db, id);
        if (p == null) return NotFound();
        ViewBag.Risk = await PackageRisk.AssessAsync(Db, p, Settings);
        ViewBag.CanEdit = User.IsPurchasing() && p.Status is PackageStatus.Draft or PackageStatus.InPlan or PackageStatus.Approved && await PackageAccess.CanEditAsync(Db, User, id);
        ViewBag.Assessor = p.RiskAssessedById.HasValue ? await Db.Users.Where(u => u.Id == p.RiskAssessedById).Select(u => u.FullName).FirstOrDefaultAsync() : null;
        ViewData["Title"] = I18n.T("Đánh giá rủi ro gói {0}", p.Number);
        return View(p);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveRisk(int id, List<string>? factors, string? mitigation)
    {
        var p = await Packages.LoadAsync(Db, id, track: true);
        if (p == null) return NotFound();
        if (p.Status is not (PackageStatus.Draft or PackageStatus.InPlan or PackageStatus.Approved) || !await PackageAccess.CanEditAsync(Db, User, id))
        { TempData["Error"] = I18n.T("Không cập nhật được đánh giá rủi ro của gói ở bước này."); return RedirectToAction(nameof(Risk), new { id }); }
        var keys = (factors ?? new()).Where(k => PackageRisk.ManualFactors.Any(m => m.Key == k)).Distinct().ToList();
        p.RiskChecklist = keys.Count == 0 ? null : string.Join(",", keys);
        p.RiskMitigation = string.IsNullOrWhiteSpace(mitigation) ? null : mitigation.Trim();
        p.RiskAssessedAt = DateTime.Now; p.RiskAssessedById = Me;
        var r = await PackageRisk.AssessAsync(Db, p, Settings);
        if (r.IsHigh && p.RiskMitigation == null)
        { TempData["Error"] = I18n.T("Gói rủi ro cao ({0} điểm) – nhập biện pháp kiểm soát rủi ro.", r.Score); await Db.SaveChangesAsync(); return RedirectToAction(nameof(Risk), new { id }); }
        Log(DocType.Package, id, I18n.T("Đánh giá rủi ro: {0} điểm – mức {1}", r.Score, I18n.T(r.Level)), p.RiskMitigation);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu đánh giá rủi ro: {0} điểm – mức {1}.", r.Score, I18n.T(r.Level));
        return RedirectToAction(nameof(Risk), new { id });
    }
}
