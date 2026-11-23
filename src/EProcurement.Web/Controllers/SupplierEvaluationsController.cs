using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Đánh giá nhà cung cấp: bảng xếp loại (phiếu đánh giá sau giao hàng + số liệu giao đúng hạn, hàng không đạt)
/// và đánh giá định kỳ theo kỳ (gợi ý điểm từ số liệu thực tế).
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class SupplierEvaluationsController : ProcControllerBase
{
    public SupplierEvaluationsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public static string CurrentPeriod(DateTime d) => $"{d.Year}-Q{(d.Month - 1) / 3 + 1}";

    public async Task<IActionResult> Index(DateTime? from, DateTime? to, string? grade, int? categoryId)
    {
        var f = from ?? DateTime.Today.AddMonths(-12); var t = to ?? DateTime.Today;
        var rows = await SupplierScorecard.BuildAsync(Db, f, t);
        if (categoryId.HasValue)
        {
            var ids = await Db.VendorCategories.Where(c => c.CategoryId == categoryId).Select(c => c.VendorId).ToListAsync();
            rows = rows.Where(r => ids.Contains(r.Vendor.Id)).ToList();
        }
        ViewBag.Counts = rows.GroupBy(r => r.Grade).ToDictionary(g => g.Key, g => g.Count());
        if (!string.IsNullOrEmpty(grade)) rows = rows.Where(r => r.Grade == grade).ToList();
        ViewBag.From = f; ViewBag.To = t; ViewBag.Grade = grade; ViewBag.CategoryId = categoryId;
        ViewBag.Levels = await SupplierScorecard.LevelsAsync(Db);
        ViewBag.Categories = await Db.Categories.OrderBy(c => c.Name).ToListAsync();
        var period = CurrentPeriod(DateTime.Today);
        ViewBag.Period = period;
        ViewBag.Periodic = await Db.VendorEvaluations.Where(e => e.Period == period).Select(e => e.VendorId).Distinct().ToListAsync();
        return View(rows);
    }

    /// <summary>Đánh giá định kỳ: điểm tiến độ / chất lượng gợi ý từ số liệu 12 tháng</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Evaluate(int vendorId)
    {
        var v = await Db.Vendors.FindAsync(vendorId);
        if (v == null) return NotFound();
        var period = CurrentPeriod(DateTime.Today);
        var score = (await SupplierScorecard.BuildAsync(Db, DateTime.Today.AddMonths(-12), DateTime.Today, vendorId)).FirstOrDefault();
        ViewBag.Score = score;
        ViewBag.Existing = await Db.VendorEvaluations.Include(e => e.Evaluator).FirstOrDefaultAsync(e => e.VendorId == vendorId && e.Period == period);
        ViewBag.History = await Db.VendorEvaluations.AsNoTracking().Include(e => e.Evaluator).Include(e => e.PurchaseOrder)
            .Where(e => e.VendorId == vendorId).OrderByDescending(e => e.CreatedAt).Take(20).ToListAsync();
        ViewData["Title"] = $"Đánh giá định kỳ – {v.Name}";
        return View(new VendorEvaluation
        {
            VendorId = vendorId, Vendor = v, Period = period,
            Delivery = score?.OnTimePercent is decimal ot ? SupplierScorecard.DeliveryScore(ot) : 4,
            Quality = score?.RejectPercent is decimal rj ? SupplierScorecard.QualityScore(rj) : score?.OnTimePercent != null ? 5 : 4,
            Price = (int)Math.Round(score?.Price ?? 4), Service = (int)Math.Round(score?.Service ?? 4)
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(VendorEvaluation model)
    {
        var v = await Db.Vendors.FindAsync(model.VendorId);
        if (v == null) return NotFound();
        var period = CurrentPeriod(DateTime.Today);
        int Clamp(int x) => Math.Clamp(x, 1, 5);
        var e = await Db.VendorEvaluations.FirstOrDefaultAsync(x => x.VendorId == v.Id && x.Period == period);
        if (e == null) { e = new VendorEvaluation { VendorId = v.Id, Period = period, CreatedAt = DateTime.Now }; Db.VendorEvaluations.Add(e); }
        e.EvaluatorId = Me; e.Quality = Clamp(model.Quality); e.Delivery = Clamp(model.Delivery); e.Price = Clamp(model.Price); e.Service = Clamp(model.Service);
        e.Comment = string.IsNullOrWhiteSpace(model.Comment) ? null : model.Comment.Trim();
        await Db.SaveChangesAsync();
        var evals = await Db.VendorEvaluations.Where(x => x.VendorId == v.Id).ToListAsync();
        v.Rating = Math.Round(evals.Average(x => x.Average), 2);
        await Db.SaveChangesAsync();
        var levels = await SupplierScorecard.LevelsAsync(Db);
        var grade = SupplierScorecard.Grade(e.Average, levels);
        TempData["Success"] = $"Đã đánh giá định kỳ {period} cho {v.Name}: {e.Average:0.00}/5 – loại {grade} ({SupplierScorecard.GradeText(grade, levels)}). Điểm chung mới: {v.Rating:0.00}.";
        return RedirectToAction(nameof(Index));
    }
}
