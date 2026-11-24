using System.Security.Claims;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Thiết lập hệ thống: phân quyền theo vai trò, quyền theo gói thầu, tiêu chí đánh giá NCC, cảnh báo, email,
/// mẫu thông báo, mã tự sinh, thông tin in biểu mẫu. Phần lớn chỉ Quản trị; quyền theo gói cho cả nhân viên mua hàng.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing)]
public class SettingsController : Controller
{
    private readonly EProcDbContext _db;
    private readonly SettingsStore _store;
    private readonly NotificationService _notify;
    private readonly NotificationSettings _legacy;
    public SettingsController(EProcDbContext db, SettingsStore store, NotificationService notify, IOptions<NotificationSettings> legacy)
    { _db = db; _store = store; _notify = notify; _legacy = legacy.Value; }

    private IActionResult Saved(string msg, string action, object? route = null) { TempData["Success"] = msg; return RedirectToAction(action, route); }

    // ------------------------------------------------------------------ tổng quan
    public async Task<IActionResult> Index()
    {
        ViewBag.Counts = new Dictionary<string, int>
        {
            ["users"] = await _db.Users.CountAsync(u => u.IsActive),
            ["limits"] = await _db.ApprovalLimits.CountAsync(),
            ["criteria"] = await _db.CriteriaSets.CountAsync(c => c.IsActive),
            ["tender"] = await _db.CriteriaSets.CountAsync(c => c.IsActive && (c.Kind == CriteriaKind.TenderQualification || c.Kind == CriteriaKind.TenderTechnical)),
            ["vendors"] = await _db.Vendors.CountAsync(),
            ["blacklist"] = await _db.Vendors.CountAsync(v => v.Status == VendorStatus.Blacklisted),
            ["packages"] = await _db.PackageMembers.Select(m => m.PackageId).Distinct().CountAsync(),
            ["categories"] = await _db.Categories.CountAsync(),
            ["forms"] = await _db.FormTemplates.CountAsync(f => f.IsActive),
        };
        var perms = await _store.GetAsync(SettingsStore.Permissions, () => new PermissionSettings());
        ViewBag.Denied = perms.Denied.Sum(d => d.Value.Count);
        var alerts = await _notify.AlertsAsync();
        ViewBag.AlertsOn = AlertSettings.Defs.Count(d => alerts.On(d.Key));
        ViewBag.Email = await _notify.EmailConfigAsync();
        ViewBag.Numbering = await _store.ExistsAsync(SettingsStore.Numbering);
        ViewData["Title"] = "Thiết lập";
        return View();
    }

    // ------------------------------------------------------------------ thiết lập phân quyền (chức năng × vai trò)
    public record PermRow(string Group, string Controller, string Label);

    /// <summary>Chức năng có thể phân quyền = các mục menu (theo controller); vai trò thấy mục nào theo mặc định</summary>
    private (List<PermRow> Rows, Dictionary<string, HashSet<string>> Base) Features()
    {
        static ClaimsPrincipal As(string role) => new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, "0") }, "perm"));
        var rows = new List<PermRow>();
        foreach (var g in NavMenu.Build(Url, As(AppRoles.Admin), ""))
            foreach (var l in g.Links.Where(l => l.Controller != null && !RolePermissionFilter.Exempt.Contains(l.Controller)))
                if (rows.All(r => r.Controller != l.Controller))
                    rows.Add(new PermRow(g.IsGroup ? g.Label : "Chung", l.Controller!, l.Label));
        var bas = AppRoles.All.ToDictionary(r => r, r => NavMenu.Build(Url, As(r), "").SelectMany(m => m.Links).Select(l => l.Controller ?? "").ToHashSet());
        return (rows, bas);
    }

    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Permissions()
    {
        var (rows, bas) = Features();
        ViewBag.Rows = rows; ViewBag.Base = bas;
        ViewData["Title"] = "Thiết lập phân quyền";
        return View(await _store.GetAsync(SettingsStore.Permissions, () => new PermissionSettings()));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Permissions(List<string>? allow)
    {
        var (rows, bas) = Features();
        var set = (allow ?? new()).ToHashSet();
        var p = new PermissionSettings();
        foreach (var r in rows)
            foreach (var role in AppRoles.All.Where(x => x != AppRoles.Admin && bas[x].Contains(r.Controller)))
                if (!set.Contains($"{r.Controller}|{role}"))
                    (p.Denied.TryGetValue(r.Controller, out var l) ? l : p.Denied[r.Controller] = new()).Add(role);
        await _store.SaveAsync(SettingsStore.Permissions, p);
        return Saved($"Đã lưu phân quyền: {p.Denied.Sum(d => d.Value.Count)} quyền bị thu hẹp so với mặc định.", nameof(Permissions));
    }

    // ------------------------------------------------------------------ quyền theo gói thầu
    public async Task<IActionResult> PackageAccess(int? id, string? q)
    {
        var packages = _db.ProcurementPackages.AsNoTracking().Include(p => p.Buyer).Where(p => p.Status != PackageStatus.Cancelled);
        if (!string.IsNullOrWhiteSpace(q)) packages = packages.Where(p => p.Number.Contains(q) || p.Title.Contains(q));
        ViewBag.Packages = await packages.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync();
        ViewBag.MemberCounts = await _db.PackageMembers.GroupBy(m => m.PackageId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        ViewBag.Q = q;
        if (id.HasValue)
        {
            var pkg = await _db.ProcurementPackages.AsNoTracking().Include(p => p.Buyer).FirstOrDefaultAsync(p => p.Id == id);
            if (pkg == null) return NotFound();
            ViewBag.Selected = pkg;
            ViewBag.Members = await _db.PackageMembers.AsNoTracking().Include(m => m.User).Where(m => m.PackageId == id).OrderBy(m => m.Role).ThenBy(m => m.User!.FullName).ToListAsync();
            ViewBag.CanEdit = await Infrastructure.PackageAccess.CanEditAsync(_db, User, id.Value);
            var taken = await _db.PackageMembers.Where(m => m.PackageId == id).Select(m => m.UserId).ToListAsync();
            ViewBag.Users = new SelectList(await _db.Users.AsNoTracking().Where(u => u.IsActive && !taken.Contains(u.Id) && u.Id != pkg.BuyerId).OrderBy(u => u.FullName)
                .Select(u => new { u.Id, Name = u.FullName + " – " + u.Department }).ToListAsync(), "Id", "Name");
        }
        ViewData["Title"] = "Quyền theo gói thầu";
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPackageMember(int id, int userId, PackageRole role)
    {
        if (!await Infrastructure.PackageAccess.CanEditAsync(_db, User, id)) { TempData["Error"] = Infrastructure.PackageAccess.Explain; return RedirectToAction(nameof(PackageAccess), new { id }); }
        if (!await _db.Users.AnyAsync(u => u.Id == userId && u.IsActive) || await _db.PackageMembers.AnyAsync(m => m.PackageId == id && m.UserId == userId))
            return RedirectToAction(nameof(PackageAccess), new { id });
        _db.PackageMembers.Add(new PackageMember { PackageId = id, UserId = userId, Role = Enum.IsDefined(role) ? role : PackageRole.Member });
        await _db.SaveChangesAsync();
        return Saved("Đã thêm thành viên gói.", nameof(PackageAccess), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePackageMember(int memberId, PackageRole? role, bool remove = false)
    {
        var m = await _db.PackageMembers.FindAsync(memberId);
        if (m == null) return NotFound();
        if (!await Infrastructure.PackageAccess.CanEditAsync(_db, User, m.PackageId)) { TempData["Error"] = Infrastructure.PackageAccess.Explain; return RedirectToAction(nameof(PackageAccess), new { id = m.PackageId }); }
        if (remove) _db.PackageMembers.Remove(m);
        else if (role.HasValue && Enum.IsDefined(role.Value)) m.Role = role.Value;
        await _db.SaveChangesAsync();
        return Saved(remove ? "Đã gỡ thành viên khỏi gói." : "Đã cập nhật quyền.", nameof(PackageAccess), new { id = m.PackageId });
    }

    // ------------------------------------------------------------------ tiêu chí đánh giá NCC
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> SupplierCriteria()
    {
        ViewBag.Levels = await SupplierScorecard.LevelsAsync(_db);
        ViewData["Title"] = "Tiêu chí đánh giá nhà cung cấp";
        return View(await _store.GetAsync(SettingsStore.SupplierCriteria, () => new SupplierCriteriaSettings()));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SupplierCriteria(SupplierCriteriaSettings model)
    {
        var defaults = SupplierCriteriaSettings.Defaults();
        var items = defaults.Select(d =>
        {
            var m = model.Items.FirstOrDefault(i => i.Key == d.Key);
            return new SupplierCriterion
            {
                Key = d.Key, Label = string.IsNullOrWhiteSpace(m?.Label) ? d.Label : m.Label.Trim()[..Math.Min(60, m.Label.Trim().Length)],
                Weight = Math.Clamp(m?.Weight ?? d.Weight, 0, 100), Guide = m?.Guide?.Trim() is { Length: > 0 } g ? g[..Math.Min(300, g.Length)] : null
            };
        }).ToList();
        if (items.Sum(i => i.Weight) == 0) { TempData["Error"] = "Tổng trọng số phải lớn hơn 0."; return RedirectToAction(nameof(SupplierCriteria)); }
        await _store.SaveAsync(SettingsStore.SupplierCriteria, new SupplierCriteriaSettings { Items = items });
        return Saved("Đã lưu tiêu chí đánh giá NCC. Điểm tổng hợp và xếp loại tính lại theo trọng số mới.", nameof(SupplierCriteria));
    }

    // ------------------------------------------------------------------ cảnh báo
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Alerts()
    {
        ViewData["Title"] = "Thiết lập cảnh báo";
        return View(await _notify.AlertsAsync());
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Alerts(List<AlertRule> rules, bool runNow = false)
    {
        var s = new AlertSettings
        {
            Rules = AlertSettings.Defs.Select(d =>
            {
                var r = rules.FirstOrDefault(x => x.Key == d.Key);
                return new AlertRule { Key = d.Key, Enabled = r?.Enabled ?? false, Value = d.Max == 0 ? 0 : Math.Clamp(r?.Value ?? d.Default, d.Min, d.Max) };
            }).ToList()
        };
        await _store.SaveAsync(SettingsStore.Alerts, s);
        var msg = "Đã lưu thiết lập cảnh báo.";
        if (runNow) msg += $" Đã quét ngay: {await _notify.RunRemindersAsync()} cảnh báo mới.";
        return Saved(msg, nameof(Alerts));
    }

    // ------------------------------------------------------------------ email
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Email()
    {
        var e = await _notify.EmailConfigAsync();
        ViewBag.HasPassword = !string.IsNullOrEmpty(e.Password);
        ViewBag.FromWeb = await _store.ExistsAsync(SettingsStore.Email);
        ViewBag.Stats = new
        {
            Pending = await _db.Notifications.CountAsync(n => n.EmailedAt == null && n.ReadAt == null && n.CreatedAt > DateTime.Now.AddDays(-3)),
            Sent7 = await _db.Notifications.CountAsync(n => n.EmailedAt != null && n.EmailedAt > DateTime.Now.AddDays(-7)),
            NoEmail = await _db.Users.CountAsync(u => u.IsActive && (u.Email == null || u.Email == "")),
            OptOut = await _db.Users.CountAsync(u => u.IsActive && !u.EmailNotifications)
        };
        ViewData["Title"] = "Email, thông báo";
        e.Password = null; // không đưa mật khẩu ra trang
        return View(e);
    }

    private async Task<EmailConfig> MergeEmailAsync(EmailConfig model, bool clearPassword)
    {
        var old = await _notify.EmailConfigAsync();
        return new EmailConfig
        {
            Enabled = model.Enabled, Host = model.Host?.Trim() ?? "", Port = Math.Clamp(model.Port, 1, 65535), EnableSsl = model.EnableSsl,
            UserName = string.IsNullOrWhiteSpace(model.UserName) ? null : model.UserName.Trim(),
            Password = clearPassword ? null : string.IsNullOrEmpty(model.Password) ? old.Password : model.Password,
            From = model.From?.Trim() ?? "", FromName = string.IsNullOrWhiteSpace(model.FromName) ? "E-Procurement" : model.FromName.Trim(),
            BaseUrl = string.IsNullOrWhiteSpace(model.BaseUrl) ? old.BaseUrl : model.BaseUrl.Trim().TrimEnd('/')
        };
    }

    private static string? EmailProblem(EmailConfig e)
    {
        if (e.Enabled && string.IsNullOrWhiteSpace(e.Host)) return "Nhập máy chủ SMTP.";
        if (!System.Net.Mail.MailAddress.TryCreate(e.From, out _)) return "Địa chỉ người gửi không hợp lệ.";
        if (!Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return "Địa chỉ trang web phải bắt đầu bằng http:// hoặc https://";
        return null;
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Email(EmailConfig model, bool clearPassword = false, string? testTo = null, bool test = false)
    {
        var e = await MergeEmailAsync(model, clearPassword);
        if (EmailProblem(e) is { } err) { TempData["Error"] = err; return RedirectToAction(nameof(Email)); }
        if (test)
        {
            var to = string.IsNullOrWhiteSpace(testTo) ? User.FindFirstValue(ClaimTypes.Email) ?? (await _db.Users.FindAsync(User.GetUserId()))?.Email : testTo.Trim();
            if (to == null || !System.Net.Mail.MailAddress.TryCreate(to, out _)) { TempData["Error"] = "Nhập email nhận thư thử."; return RedirectToAction(nameof(Email)); }
            if (string.IsNullOrWhiteSpace(e.Host)) { TempData["Error"] = "Nhập máy chủ SMTP để gửi thử."; return RedirectToAction(nameof(Email)); }
            try
            {
                await _notify.SendTestAsync(e, await _store.GetAsync(SettingsStore.Templates, () => new NotifyTemplates()), to, User.GetFullName());
                TempData["Success"] = $"Đã gửi email thử tới {to}. Kiểm tra hộp thư (cả thư rác). Cấu hình chưa được lưu – bấm Lưu nếu đúng.";
            }
            catch (Exception ex) { TempData["Error"] = "Gửi thử thất bại: " + ex.GetBaseException().Message; }
            return RedirectToAction(nameof(Email));
        }
        await _store.SaveAsync(SettingsStore.Email, e);
        return Saved(e.Enabled ? "Đã lưu cấu hình email – hệ thống gửi email thông báo theo chu kỳ." : "Đã lưu cấu hình email (đang tắt gửi email).", nameof(Email));
    }

    // ------------------------------------------------------------------ mẫu thông báo
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Templates()
    {
        ViewData["Title"] = "Thiết lập mẫu thông báo";
        ViewBag.Company = (await _store.GetAsync(SettingsStore.Print, () => new PrintSettings())).CompanyName;
        return View(await _store.GetAsync(SettingsStore.Templates, () => new NotifyTemplates()));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Templates(NotifyTemplates model, bool reset = false)
    {
        var t = reset ? new NotifyTemplates { Channels = (await _store.GetAsync(SettingsStore.Templates, () => new NotifyTemplates())).Channels } : new NotifyTemplates
        {
            Subject = string.IsNullOrWhiteSpace(model.Subject) ? NotifyTemplates.DefaultSubject : model.Subject.Trim()[..Math.Min(200, model.Subject.Trim().Length)],
            Body = string.IsNullOrWhiteSpace(model.Body) ? NotifyTemplates.DefaultBody : model.Body[..Math.Min(8000, model.Body.Length)],
            Channels = NotifyTemplates.Groups.Select(g =>
            {
                var c = model.Channels.FirstOrDefault(x => x.Key == g.Key);
                return new NotifyChannel { Key = g.Key, InApp = c?.InApp ?? false, Email = c?.Email ?? false };
            }).ToList()
        };
        await _store.SaveAsync(SettingsStore.Templates, t);
        return Saved(reset ? "Đã khôi phục mẫu email mặc định." : "Đã lưu mẫu thông báo.", nameof(Templates));
    }

    // ------------------------------------------------------------------ mã tự sinh
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Numbering()
    {
        ViewData["Title"] = "Thiết lập mã tự sinh";
        return View(await _store.GetAsync(SettingsStore.Numbering, () => new NumberingSettings()));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Numbering(List<NumberRule> rules, Dictionary<string, string>? codes)
    {
        var s = new NumberingSettings();
        var errors = new List<string>();
        foreach (var d in NumberingSettings.Defs)
        {
            var r = rules.FirstOrDefault(x => x.Key == d.Key);
            var pattern = string.IsNullOrWhiteSpace(r?.Pattern) ? NumberingSettings.DefaultPattern : r.Pattern.Trim();
            var code = codes != null && codes.TryGetValue(d.Key, out var c) && !string.IsNullOrWhiteSpace(c) ? c.Trim().ToUpperInvariant() : d.Key;
            var digits = r?.Digits ?? 4;
            if (NumberingSettings.Validate(pattern, code, digits) is { } err) { errors.Add($"{d.Label}: {err}"); continue; }
            s.Rules.Add(new NumberRule { Key = d.Key, Pattern = pattern, Digits = digits });
            if (code != d.Key) s.Codes[d.Key] = code;
        }
        if (errors.Count > 0) { TempData["Error"] = string.Join(" ", errors); return RedirectToAction(nameof(Numbering)); }
        await _store.SaveAsync(SettingsStore.Numbering, s);
        DocNumbers.Rules = s;
        return Saved("Đã lưu mẫu mã tự sinh – áp dụng cho chứng từ tạo mới từ bây giờ, số đã cấp giữ nguyên.", nameof(Numbering));
    }

    // ------------------------------------------------------------------ thông tin in biểu mẫu
    [Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Print()
    {
        ViewData["Title"] = "Thông tin in trên biểu mẫu";
        return View(await _store.GetAsync(SettingsStore.Print, () => new PrintSettings()));
    }

    [Authorize(Roles = AppRoles.Admin)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Print(PrintSettings model)
    {
        static string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
        await _store.SaveAsync(SettingsStore.Print, new PrintSettings
        {
            CompanyName = T(model.CompanyName, 200) ?? "", Address = T(model.Address, 300), TaxCode = T(model.TaxCode, 20), Phone = T(model.Phone, 50),
            Email = T(model.Email, 100), Footer = T(model.Footer, 200), ShowNationalHeader = model.ShowNationalHeader
        });
        return Saved("Đã lưu thông tin in trên biểu mẫu.", nameof(Print));
    }
}
