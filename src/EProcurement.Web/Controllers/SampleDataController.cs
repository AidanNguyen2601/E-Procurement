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
        return View(await SampleDataSeeder.StatusAsync(_db));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Load()
    {
        try
        {
            await SampleDataSeeder.LoadAsync(_db, _hasher);
            var s = await SampleDataSeeder.StatusAsync(_db);
            TempData["Success"] = $"Đã nạp dữ liệu mẫu: {s.Requests} đề nghị, {s.Rfqs} RFQ, {s.DirectAwards} tờ trình chỉ định, {s.Tenders} gói thầu, {s.Contracts} hợp đồng, {s.Orders} PO, {s.Receipts} phiếu nhận hàng.";
        }
        catch (InvalidOperationException ex) { TempData["Error"] = ex.Message; }
        catch (Exception ex)
        {
            _log.LogError(ex, "Nạp dữ liệu mẫu thất bại");
            TempData["Error"] = "Nạp dữ liệu mẫu thất bại, không có thay đổi nào được lưu: " + ex.GetBaseException().Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Remove()
    {
        try
        {
            await SampleDataSeeder.RemoveAsync(_db);
            TempData["Success"] = "Đã gỡ toàn bộ chứng từ mẫu. Danh mục người dùng, hàng hóa, NCC được giữ lại.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Gỡ dữ liệu mẫu thất bại");
            TempData["Error"] = "Gỡ dữ liệu mẫu thất bại, không có thay đổi nào được lưu: " + ex.GetBaseException().Message;
        }
        return RedirectToAction(nameof(Index));
    }
}
