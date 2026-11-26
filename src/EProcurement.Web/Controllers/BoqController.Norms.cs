using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Phân tích đơn giá chi tiết của dòng BOQ theo định mức hao phí vật liệu / nhân công / máy</summary>
public partial class BoqController
{
    public async Task<IActionResult> Norms(int id)
    {
        var it = await Db.BoqItems.Include(x => x.Package).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (it == null) return NotFound();
        if (await Db.BoqItems.AnyAsync(x => x.ParentId == id)) { TempData["Error"] = I18n.T("Dòng nhóm không phân tích đơn giá – phân tích ở các dòng công việc."); return RedirectToAction(nameof(Index), new { packageId = it.PackageId }); }
        ViewBag.CanEdit = !IsLocked(it.Package!) && User.IsPurchasing() && await PackageAccess.CanEditAsync(Db, User, it.PackageId);
        ViewData["Title"] = I18n.T("Phân tích đơn giá {0}", it.Code);
        return View(it);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveNorms(int id, List<int>? kind, List<string?>? code, List<string?>? name, List<string?>? unit, List<decimal?>? norm, List<decimal?>? price, bool apply = false, bool fillPrices = false)
    {
        var it = await Db.BoqItems.FirstOrDefaultAsync(x => x.Id == id);
        if (it == null) return NotFound();
        var (_, fail) = await EditableAsync(it.PackageId);
        if (fail != null) return fail;
        IActionResult Back(string? e = null, string? ok = null) { if (e != null) TempData["Error"] = e; if (ok != null) TempData["Success"] = ok; return RedirectToAction(nameof(Norms), new { id }); }
        if (await Db.BoqItems.AnyAsync(x => x.ParentId == id)) return Back(I18n.T("Dòng nhóm không phân tích đơn giá – phân tích ở các dòng công việc."));
        var lines = new List<BoqNormLine>();
        for (var i = 0; i < (name?.Count ?? 0); i++)
        {
            if (string.IsNullOrWhiteSpace(name![i]) && string.IsNullOrWhiteSpace(code?.ElementAtOrDefault(i))) continue;
            lines.Add(new BoqNormLine
            {
                Kind = kind?.ElementAtOrDefault(i) ?? 1, Code = code?.ElementAtOrDefault(i)?.Trim(), Name = (name[i] ?? "").Trim(), Unit = unit?.ElementAtOrDefault(i)?.Trim(),
                Norm = norm?.ElementAtOrDefault(i) ?? 0, Price = Math.Round(price?.ElementAtOrDefault(i) ?? 0, 0)
            });
        }
        // Lấy đơn giá tài nguyên từ ngân hàng giá (trung vị 12 tháng) cho dòng có mã trùng mã hàng hóa và chưa nhập giá
        var filled = 0;
        if (fillPrices)
        {
            var codes = lines.Where(l => l.Price == 0 && !string.IsNullOrEmpty(l.Code)).Select(l => l.Code!).Distinct().ToList();
            var items = await Db.Items.Where(x => codes.Contains(x.Code)).Select(x => new { x.Id, x.Code, x.Name, x.Unit, x.ReferencePrice }).ToListAsync();
            if (items.Count > 0)
            {
                var obs = await PriceBank.ObservationsAsync(Db, items.Select(x => x.Id).ToList(), DateTime.Today.AddMonths(-12), DateTime.Today);
                foreach (var l in lines.Where(l => l.Price == 0 && !string.IsNullOrEmpty(l.Code)))
                {
                    var m = items.FirstOrDefault(x => string.Equals(x.Code, l.Code, StringComparison.OrdinalIgnoreCase));
                    if (m == null) continue;
                    var p = StandardPrices.Suggest(PriceStats.From(m.Id, obs.Where(o => o.ItemId == m.Id).ToList()), m.ReferencePrice);
                    if (p > 0) { l.Price = p; filled++; }
                    if (string.IsNullOrEmpty(l.Name)) l.Name = m.Name;
                    l.Unit ??= m.Unit;
                }
            }
        }
        var errors = BoqNorms.Validate(lines);
        if (errors.Count > 0) return Back(string.Join(" ", errors));
        it.Norms = BoqNorms.Serialize(lines);
        if (apply && lines.Count > 0)
        {
            var (mat, lab, mac) = BoqNorms.Components(lines);
            it.MaterialPrice = mat; it.LaborPrice = lab; it.MachinePrice = mac;
            it.UnitPrice = BoqCost.PriceFromComponents(it.UnitPrice, mat, lab, mac);
        }
        await Db.SaveChangesAsync();
        return Back(ok: (apply ? I18n.T("Đã lưu phân tích và cập nhật đơn giá dòng {0}: {1} đ.", it.Code, it.UnitPrice.ToMoney()) : I18n.T("Đã lưu phân tích đơn giá."))
            + (fillPrices ? " " + I18n.T("Lấy được {0} đơn giá từ ngân hàng giá.", filled) : ""));
    }
}
