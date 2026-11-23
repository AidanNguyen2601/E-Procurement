using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Ma trận hạn mức phê duyệt (Quản trị) và ủy quyền phê duyệt khi vắng mặt
/// (Giám đốc / Trưởng bộ phận tự ủy quyền; Quản trị lập thay được).
/// </summary>
[Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director + "," + AppRoles.Approver)]
public class ApprovalSettingsController : Controller
{
    private const int MaxDelegationDays = 90;
    private readonly EProcDbContext _db;
    private readonly ApprovalMatrix _matrix;
    private readonly IMemoryCache _cache;
    public ApprovalSettingsController(EProcDbContext db, ApprovalMatrix matrix, IMemoryCache cache) { _db = db; _matrix = matrix; _cache = cache; }

    private int Me => User.GetUserId();
    /// <summary>Vai trò gốc (không tính quyền được ủy quyền)</summary>
    private string BaseRole => User.FindFirst(DelegationClaimsTransformation.BaseRoleClaim)?.Value ?? "";

    public class Page
    {
        public List<ApprovalLimit> Limits { get; set; } = new();
        public Dictionary<ApprovalLimitType, decimal> Defaults { get; set; } = new();
        public List<ApprovalDelegation> Delegations { get; set; } = new();
        public bool IsAdmin { get; set; }
        public bool CanDelegate { get; set; }
    }

    public async Task<IActionResult> Index()
    {
        var isAdmin = User.IsAdmin();
        var q = _db.ApprovalDelegations.AsNoTracking().Include(d => d.FromUser).Include(d => d.ToUser).AsQueryable();
        if (!isAdmin) q = q.Where(d => d.FromUserId == Me || d.ToUserId == Me);
        var vm = new Page
        {
            IsAdmin = isAdmin, CanDelegate = isAdmin || BaseRole is AppRoles.Director or AppRoles.Approver,
            Limits = await _db.ApprovalLimits.AsNoTracking().OrderBy(l => l.Type).ThenBy(l => l.Department).ToListAsync(),
            Defaults = Enum.GetValues<ApprovalLimitType>().ToDictionary(t => t, t => _matrix.Default(t)),
            Delegations = await q.OrderByDescending(d => d.EndDate).Take(200).ToListAsync()
        };
        ViewBag.Departments = await HttpContext.RequestServices.GetRequiredService<CatalogService>().SelectAsync(CatalogType.Department, null);
        // Người ủy quyền: Quản trị chọn mọi Giám đốc / Trưởng bộ phận; người khác chỉ chính mình
        var fromQ = _db.Users.Where(u => u.IsActive && (u.Role == AppRoles.Director || u.Role == AppRoles.Approver));
        if (!isAdmin) fromQ = fromQ.Where(u => u.Id == Me);
        ViewBag.FromUsers = new SelectList(await fromQ.OrderBy(u => u.FullName).Select(u => new { u.Id, Text = u.FullName + " – " + (u.Role == AppRoles.Director ? "Giám đốc" : "Trưởng bộ phận " + u.Department) }).ToListAsync(), "Id", "Text");
        ViewBag.ToUsers = new SelectList(await _db.Users.Where(u => u.IsActive && u.Role != AppRoles.Buyer).OrderBy(u => u.Department).ThenBy(u => u.FullName)
            .Select(u => new { u.Id, Text = u.FullName + " (" + u.Department + ")" }).ToListAsync(), "Id", "Text");
        ViewData["Title"] = "Hạn mức & ủy quyền duyệt";
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SaveLimit(ApprovalLimitType type, string? department, decimal amount, string? note)
    {
        department = string.IsNullOrWhiteSpace(department) ? null : department.Trim();
        if (amount < 0) { TempData["Error"] = "Hạn mức không được âm."; return RedirectToAction(nameof(Index)); }
        var l = await _db.ApprovalLimits.FirstOrDefaultAsync(x => x.Type == type && x.Department == department)
                ?? _db.ApprovalLimits.Add(new ApprovalLimit { Type = type, Department = department }).Entity;
        l.Amount = amount; l.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(); l.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu hạn mức {(department ?? "mặc định toàn công ty")}: {amount.ToMoney()} đ.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> DeleteLimit(int id)
    {
        await _db.ApprovalLimits.Where(l => l.Id == id).ExecuteDeleteAsync();
        TempData["Success"] = "Đã xóa hạn mức (áp dụng lại mức mặc định).";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delegate(int fromUserId, int toUserId, DateTime startDate, DateTime endDate, string? reason)
    {
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index)); }
        if (!User.IsAdmin() && fromUserId != Me) return Forbid();
        var from = await _db.Users.FindAsync(fromUserId);
        var to = await _db.Users.FindAsync(toUserId);
        if (from == null || to == null || !to.IsActive) return Fail("Chọn người ủy quyền và người nhận hợp lệ.");
        if (from.Role is not (AppRoles.Director or AppRoles.Approver)) return Fail("Chỉ Giám đốc hoặc Trưởng bộ phận được ủy quyền phê duyệt.");
        if (from.Id == to.Id) return Fail("Không tự ủy quyền cho chính mình.");
        // Phân tách nhiệm vụ: người lập chứng từ mua hàng không được duyệt chứng từ của chính bộ phận mua hàng
        if (to.Role == AppRoles.Buyer) return Fail("Không ủy quyền phê duyệt cho nhân viên mua hàng (tránh tự lập – tự duyệt).");
        if (from.Role == AppRoles.Approver && to.Department != from.Department) return Fail("Trưởng bộ phận chỉ ủy quyền cho người cùng phòng ban.");
        if (string.IsNullOrWhiteSpace(reason)) return Fail("Nhập lý do ủy quyền.");
        if (endDate.Date < startDate.Date || startDate.Date < DateTime.Today.AddDays(-1)) return Fail("Thời gian ủy quyền không hợp lệ.");
        if ((endDate.Date - startDate.Date).TotalDays > MaxDelegationDays) return Fail($"Ủy quyền tối đa {MaxDelegationDays} ngày mỗi lần.");
        if (await _db.ApprovalDelegations.AnyAsync(d => d.FromUserId == from.Id && d.RevokedAt == null && d.StartDate <= endDate.Date && d.EndDate >= startDate.Date))
            return Fail("Đã có ủy quyền trùng thời gian – thu hồi ủy quyền cũ trước.");
        _db.ApprovalDelegations.Add(new ApprovalDelegation
        {
            FromUserId = from.Id, ToUserId = to.Id, StartDate = startDate.Date, EndDate = endDate.Date, Reason = reason.Trim()[..Math.Min(500, reason.Trim().Length)],
            CreatedById = Me, CreatedAt = DateTime.Now
        });
        _db.Notifications.Add(new Notification
        {
            UserId = to.Id, Kind = NotificationKind.Info, Title = $"Bạn được {from.FullName} ủy quyền phê duyệt {startDate:dd/MM} – {endDate:dd/MM/yyyy}",
            Message = reason.Trim(), Url = "/Approvals"
        });
        await _db.SaveChangesAsync();
        DelegationClaimsTransformation.Invalidate(_cache, to.Id);
        TempData["Success"] = $"Đã ủy quyền cho {to.FullName} từ {startDate:dd/MM/yyyy} đến {endDate:dd/MM/yyyy}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(int id)
    {
        var d = await _db.ApprovalDelegations.FindAsync(id);
        if (d == null) return NotFound();
        if (!User.IsAdmin() && d.FromUserId != Me) return Forbid();
        d.RevokedAt ??= DateTime.Now;
        await _db.SaveChangesAsync();
        DelegationClaimsTransformation.Invalidate(_cache, d.ToUserId);
        TempData["Success"] = "Đã thu hồi ủy quyền.";
        return RedirectToAction(nameof(Index));
    }
}
