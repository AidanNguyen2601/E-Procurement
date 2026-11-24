using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace EProcurement.Web.Controllers;

/// <summary>Nạp / gỡ bộ dữ liệu mẫu cho các quy trình mua sắm – chỉ Admin</summary>
[Authorize(Roles = AppRoles.Admin)]
public class SampleDataController : Controller
{
    private readonly EProcDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;
    private readonly ILogger<SampleDataController> _log;

    public SampleDataController(EProcDbContext db, IPasswordHasher<AppUser> hasher, ILogger<SampleDataController> log)
    { _db = db; _hasher = hasher; _log = log; }

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Dữ liệu mẫu";
        ViewBag.Flows = await SampleFlows.StatusAsync(_db);
        return View(await SampleDataSeeder.StatusAsync(_db));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Load()
    {
        try
        {
            await SampleDataSeeder.LoadAsync(_db, _hasher);
            var s = await SampleDataSeeder.StatusAsync(_db);
            TempData["Success"] = I18n.T("Đã nạp dữ liệu mẫu: {0} đề nghị, {1} RFQ, {2} tờ trình chỉ định, {3} gói thầu, {4} hợp đồng, {5} PO, {6} phiếu nhận hàng.", s.Requests, s.Rfqs, s.DirectAwards, s.Tenders, s.Contracts, s.Orders, s.Receipts);
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Nạp dữ liệu mẫu thất bại");
            TempData["Error"] = I18n.T("Nạp dữ liệu mẫu thất bại, không có thay đổi nào được lưu: ") + ex.GetBaseException().Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove()
    {
        try
        {
            await SampleDataSeeder.RemoveAsync(_db);
            TempData["Success"] = I18n.T("Đã gỡ toàn bộ chứng từ mẫu. Danh mục người dùng, hàng hóa, NCC được giữ lại.");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gỡ dữ liệu mẫu thất bại");
            TempData["Error"] = I18n.T("Gỡ dữ liệu mẫu thất bại, không có thay đổi nào được lưu: ") + ex.GetBaseException().Message;
        }
        return RedirectToAction(nameof(Index));
    }
    /// <summary>Nạp bổ sung các luồng mở rộng trên bộ dữ liệu mẫu đã có (phần nào có rồi thì bỏ qua)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> LoadFlows()
    {
        try
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            var done = await SampleFlows.LoadAsync(_db);
            await tx.CommitAsync();
            TempData["Success"] = done.Count == 0 ? I18n.T("Các luồng bổ sung đã có đủ, không nạp thêm.") : I18n.T("Đã nạp bổ sung: ") + string.Join("; ", done) + ".";
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Nạp luồng bổ sung thất bại");
            TempData["Error"] = I18n.T("Nạp bổ sung thất bại, không có thay đổi nào được lưu: ") + ex.GetBaseException().Message;
        }
        return RedirectToAction(nameof(Index));
    }
}
