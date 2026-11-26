using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Yêu cầu năng lực có cấu trúc, liên danh / thầu phụ, kê khai năng lực và ưu đãi của nhà thầu</summary>
public partial class TendersController
{
    private async Task<HashSet<int>> BannedVendorIdsAsync() =>
        (await Db.Vendors.Where(v => v.Status == VendorStatus.Blacklisted || v.Status == VendorStatus.Suspended).Select(v => v.Id).ToListAsync()).ToHashSet();

    // ------------------------------------------------------------------ yêu cầu năng lực của HSMT
    public async Task<IActionResult> Requirements(int id)
    {
        var t = await Db.Tenders.Include(x => x.Requirements).Include(x => x.Committee).Include(x => x.Appraisals).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        if (!CanView(t)) return Forbid();
        ViewData["Title"] = I18n.T("Yêu cầu năng lực, kinh nghiệm – {0}", t.Number);
        return View(t);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddRequirement(int id, QualificationKind kind, string? name, int minCount, decimal minValue, int minYears)
    {
        var t = await Db.Tenders.Include(x => x.Requirements).FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        IActionResult Back(string? e = null) { if (e != null) TempData["Error"] = e; return RedirectToAction(nameof(Requirements), new { id }); }
        if (t.Status != TenderStatus.Draft) return Back(I18n.T("Chỉ thiết lập yêu cầu khi HSMT còn ở trạng thái Nháp."));
        if (string.IsNullOrWhiteSpace(name)) return Back(I18n.T("Nhập nội dung yêu cầu."));
        if (minCount < 0 || minValue < 0 || minYears < 0) return Back(I18n.T("Ngưỡng không được âm."));
        if (kind is QualificationKind.AverageRevenue or QualificationKind.FinancialResources && minValue <= 0) return Back(I18n.T("Nhập giá trị tối thiểu."));
        t.Requirements.Add(new TenderRequirement { TenderId = id, Kind = kind, Name = name.Trim(), MinCount = minCount, MinValue = Math.Round(minValue, 0), MinYears = minYears, SortOrder = t.Requirements.Count + 1 });
        Log(DocType.Tender, id, I18n.T("Thêm yêu cầu năng lực: {0} – {1}", kind.GetDisplayName(), name.Trim()));
        await Db.SaveChangesAsync();
        return Back();
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRequirement(int id, int requirementId)
    {
        var t = await Db.Tenders.FindAsync(id);
        if (t == null) return NotFound();
        if (t.Status != TenderStatus.Draft) { TempData["Error"] = I18n.T("Chỉ thiết lập yêu cầu khi HSMT còn ở trạng thái Nháp."); return RedirectToAction(nameof(Requirements), new { id }); }
        await Db.TenderBidderCapabilities.Where(c => c.RequirementId == requirementId).ExecuteDeleteAsync();
        await Db.TenderRequirements.Where(r => r.Id == requirementId && r.TenderId == id).ExecuteDeleteAsync();
        return RedirectToAction(nameof(Requirements), new { id });
    }

    // ------------------------------------------------------------------ hồ sơ năng lực / liên danh của một nhà thầu
    private async Task<(Tender? T, TenderBidder? B)> BidderAsync(int bidderId)
    {
        var tid = await Db.TenderBidders.Where(b => b.Id == bidderId).Select(b => (int?)b.TenderId).FirstOrDefaultAsync();
        if (tid == null) return (null, null);
        var t = await LoadAsync(tid.Value);
        return (t, t?.Bidders.FirstOrDefault(b => b.Id == bidderId));
    }

    /// <summary>Mua hàng nhập theo HSDT: trong thời gian mời thầu và khi đang đánh giá (chưa trình kết quả)</summary>
    private bool CanEditProfile(Tender t) => User.IsPurchasing() && t.Status is TenderStatus.Published or TenderStatus.Opened;

    public async Task<IActionResult> Bidder(int id)
    {
        var (t, b) = await BidderAsync(id);
        if (t == null || b == null) return NotFound();
        if (!CanView(t)) return Forbid();
        if (!User.IsPurchasing() && !t.OpenedAt.HasValue) return Forbid(); // HSDT niêm phong đến khi mở thầu
        ViewBag.Tender = t;
        ViewBag.Banned = await BannedVendorIdsAsync();
        ViewBag.CanEdit = CanEditProfile(t);
        ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Id != b.VendorId).OrderBy(v => v.Name).Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name");
        ViewData["Title"] = I18n.T("Năng lực & liên danh – {0}", b.Vendor?.Name ?? "");
        return View(b);
    }

    private IActionResult ToBidder(int bidderId, string? error = null) { if (error != null) TempData["Error"] = error; return RedirectToAction(nameof(Bidder), new { id = bidderId }); }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPartner(int id, int? vendorId, string? name, string? taxCode, BidPartnerRole role, decimal sharePercent, string? workScope)
    {
        var b = await Db.TenderBidders.Include(x => x.Tender).Include(x => x.Partners).Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        if (!CanEditProfile(b.Tender!)) return ToBidder(id, I18n.T("Không còn được cập nhật hồ sơ nhà thầu ở bước này."));
        if (vendorId.HasValue)
        {
            var v = await Db.Vendors.FindAsync(vendorId.Value);
            if (v == null) return ToBidder(id, I18n.T("Nhà cung cấp không tồn tại."));
            name ??= v.Name; taxCode ??= v.TaxCode;
        }
        if (role == BidPartnerRole.Lead) { vendorId = b.VendorId; name = b.Vendor?.Name; taxCode = b.Vendor?.TaxCode; }
        if (string.IsNullOrWhiteSpace(name)) return ToBidder(id, I18n.T("Nhập tên đối tác."));
        if (sharePercent is < 0 or > 100) return ToBidder(id, I18n.T("Tỷ lệ công việc phải từ 0 đến 100%."));
        if (role == BidPartnerRole.Lead && b.Partners.Any(p => p.Role == BidPartnerRole.Lead)) return ToBidder(id, I18n.T("Đã có thành viên đứng đầu."));
        if (role == BidPartnerRole.Member && !b.Tender!.AllowJointVenture) return ToBidder(id, I18n.T("HSMT không cho phép liên danh."));
        b.Partners.Add(new TenderBidderPartner { TenderBidderId = id, VendorId = vendorId, Name = name.Trim(), TaxCode = string.IsNullOrWhiteSpace(taxCode) ? null : taxCode.Trim(), Role = role, SharePercent = sharePercent, WorkScope = string.IsNullOrWhiteSpace(workScope) ? null : workScope.Trim(), SortOrder = b.Partners.Count + 1 });
        Log(DocType.Tender, b.TenderId, I18n.T("{0}: kê khai {1} {2} ({3}%)", b.Vendor?.Name ?? "", role.GetDisplayName().ToLower(), name.Trim(), sharePercent.ToString("0.##")));
        await Db.SaveChangesAsync();
        return ToBidder(id);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePartner(int id, int partnerId)
    {
        var b = await Db.TenderBidders.Include(x => x.Tender).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        if (!CanEditProfile(b.Tender!)) return ToBidder(id, I18n.T("Không còn được cập nhật hồ sơ nhà thầu ở bước này."));
        await Db.TenderBidderCapabilities.Where(c => c.PartnerId == partnerId && c.TenderBidderId == id).ExecuteUpdateAsync(x => x.SetProperty(c => c.PartnerId, (int?)null));
        await Db.TenderBidderPartners.Where(p => p.Id == partnerId && p.TenderBidderId == id).ExecuteDeleteAsync();
        return ToBidder(id);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCapability(int id, int requirementId, int? partnerId, int count, decimal value, int years, string? detail)
    {
        var b = await Db.TenderBidders.Include(x => x.Tender).ThenInclude(t => t!.Requirements).Include(x => x.Partners).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        if (!CanEditProfile(b.Tender!)) return ToBidder(id, I18n.T("Không còn được cập nhật hồ sơ nhà thầu ở bước này."));
        if (b.Tender!.Requirements.All(r => r.Id != requirementId)) return ToBidder(id, I18n.T("Yêu cầu không thuộc gói thầu."));
        if (partnerId.HasValue && b.Partners.All(p => p.Id != partnerId)) return ToBidder(id, I18n.T("Thành viên không thuộc HSDT."));
        if (count < 0 || value < 0 || years < 0) return ToBidder(id, I18n.T("Số liệu kê khai không được âm."));
        Db.TenderBidderCapabilities.Add(new TenderBidderCapability { TenderBidderId = id, RequirementId = requirementId, PartnerId = partnerId, Count = count, Value = Math.Round(value, 0), Years = years, Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim() });
        await Db.SaveChangesAsync();
        return ToBidder(id);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteCapability(int id, int capabilityId)
    {
        var b = await Db.TenderBidders.Include(x => x.Tender).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        if (!CanEditProfile(b.Tender!)) return ToBidder(id, I18n.T("Không còn được cập nhật hồ sơ nhà thầu ở bước này."));
        await Db.TenderBidderCapabilities.Where(c => c.Id == capabilityId && c.TenderBidderId == id).ExecuteDeleteAsync();
        return ToBidder(id);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPreference(int id, bool preferred, string? note)
    {
        var b = await Db.TenderBidders.Include(x => x.Tender).Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Id == id);
        if (b == null) return NotFound();
        if (!CanEditProfile(b.Tender!)) return ToBidder(id, I18n.T("Không còn được cập nhật hồ sơ nhà thầu ở bước này."));
        if (preferred && string.IsNullOrWhiteSpace(note)) return ToBidder(id, I18n.T("Nêu căn cứ được hưởng ưu đãi (tài liệu chứng minh trong HSDT)."));
        b.DomesticPreferred = preferred; b.PreferenceNote = preferred ? note!.Trim() : null;
        Log(DocType.Tender, b.TenderId, preferred ? I18n.T("{0}: thuộc đối tượng ưu đãi", b.Vendor?.Name ?? "") : I18n.T("{0}: không hưởng ưu đãi", b.Vendor?.Name ?? ""), b.PreferenceNote);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã cập nhật ưu đãi.");
        return ToBidder(id);
    }
}
