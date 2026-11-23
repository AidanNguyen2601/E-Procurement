using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

// =====================================================================
//  CỔNG NHÀ CUNG CẤP – tài khoản, hồ sơ, báo giá / dự thầu online, đơn hàng
// =====================================================================

public class PortalLoginModel
{
    [Required(ErrorMessage = "Nhập email"), EmailAddress(ErrorMessage = "Email không hợp lệ")][Display(Name = "Email")] public string Email { get; set; } = "";
    [Required(ErrorMessage = "Nhập mật khẩu"), DataType(DataType.Password)][Display(Name = "Mật khẩu")] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}

public class PortalRegisterModel
{
    [Required(ErrorMessage = "Nhập mã số thuế"), StringLength(20), RegularExpression(@"^\d{10}(-\d{3})?$", ErrorMessage = "Mã số thuế gồm 10 số (hoặc 10 số-3 số chi nhánh)")]
    [Display(Name = "Mã số thuế")] public string TaxCode { get; set; } = "";
    [Required(ErrorMessage = "Nhập tên công ty"), StringLength(200)][Display(Name = "Tên công ty")] public string CompanyName { get; set; } = "";
    [StringLength(300)][Display(Name = "Địa chỉ")] public string? Address { get; set; }
    [StringLength(20)][Display(Name = "Điện thoại công ty")] public string? Phone { get; set; }
    [Required(ErrorMessage = "Nhập họ tên"), StringLength(150)][Display(Name = "Họ tên người đăng ký")] public string FullName { get; set; } = "";
    [StringLength(100)][Display(Name = "Chức vụ")] public string? Position { get; set; }
    [StringLength(20)][Display(Name = "Điện thoại di động")] public string? Mobile { get; set; }
    [Required(ErrorMessage = "Nhập email"), EmailAddress(ErrorMessage = "Email không hợp lệ"), StringLength(150)][Display(Name = "Email đăng nhập")] public string Email { get; set; } = "";
    [Required(ErrorMessage = "Nhập mật khẩu"), StringLength(100, MinimumLength = 8, ErrorMessage = "Mật khẩu tối thiểu 8 ký tự"), DataType(DataType.Password)][Display(Name = "Mật khẩu")] public string Password { get; set; } = "";
    [Compare(nameof(Password), ErrorMessage = "Mật khẩu nhập lại không khớp"), DataType(DataType.Password)][Display(Name = "Nhập lại mật khẩu")] public string ConfirmPassword { get; set; } = "";
    [Display(Name = "Nhóm hàng cung cấp")] public List<int> CategoryIds { get; set; } = new();
    [Range(typeof(bool), "true", "true", ErrorMessage = "Cần đồng ý điều khoản sử dụng cổng")] public bool Agree { get; set; }
}

[AllowAnonymous]
public class PortalAccountController : Controller
{
    private readonly EProcDbContext _db;
    private readonly IPasswordHasher<VendorUser> _hasher;
    public PortalAccountController(EProcDbContext db, IPasswordHasher<VendorUser> hasher) { _db = db; _hasher = hasher; }

    [HttpGet]
    public IActionResult Login(string? returnUrl) => View(new PortalLoginModel { ReturnUrl = returnUrl });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(PortalLoginModel vm)
    {
        if (!ModelState.IsValid) return View(vm);
        var u = await _db.VendorUsers.Include(x => x.Vendor).FirstOrDefaultAsync(x => x.Email == vm.Email.Trim());
        if (u != null && LoginGuard.IsLocked(u.LockoutEnd)) { ModelState.AddModelError("", LoginGuard.LockedMessage(u.LockoutEnd!.Value)); return View(vm); }
        if (u == null || !u.IsActive || u.Vendor == null || u.Vendor.Status == VendorStatus.Blacklisted
            || _hasher.VerifyHashedPassword(u, u.PasswordHash, vm.Password) == PasswordVerificationResult.Failed)
        {
            if (u != null)
            {
                int f = u.FailedLogins; DateTime? until = u.LockoutEnd;
                if (LoginGuard.Fail(ref f, ref until)) ModelState.AddModelError("", LoginGuard.LockedMessage(until!.Value));
                u.FailedLogins = f; u.LockoutEnd = until; await _db.SaveChangesAsync();
            }
            ModelState.AddModelError("", "Email hoặc mật khẩu không đúng, hoặc tài khoản đã bị khóa.");
            return View(vm);
        }
        u.FailedLogins = 0; u.LockoutEnd = null; u.LastLoginAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await SignInAsync(u);
        return !string.IsNullOrEmpty(vm.ReturnUrl) && Url.IsLocalUrl(vm.ReturnUrl) && vm.ReturnUrl.StartsWith("/Portal") ? LocalRedirect(vm.ReturnUrl) : RedirectToAction("Index", "Portal");
    }

    private Task SignInAsync(VendorUser u)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "v" + u.Id), new(ClaimTypes.Name, u.Email), new(ClaimsExtensions.FullNameClaim, u.FullName),
            new(PortalAuth.VendorIdClaim, u.VendorId.ToString()), new(PortalAuth.VendorUserIdClaim, u.Id.ToString())
        };
        return HttpContext.SignInAsync(PortalAuth.Scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, PortalAuth.Scheme)));
    }

    [HttpGet]
    public async Task<IActionResult> Register()
    {
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync();
        return View(new PortalRegisterModel());
    }

    /// <summary>
    /// Đăng ký: MST chưa có → tạo NCC mới (chờ thẩm định); MST đã có → tạo tài khoản chờ bộ phận mua hàng xác nhận
    /// (tránh người ngoài chiếm quyền một NCC có sẵn).
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(PortalRegisterModel vm)
    {
        vm.Email = vm.Email.Trim().ToLowerInvariant(); vm.TaxCode = vm.TaxCode.Trim();
        if (await _db.VendorUsers.AnyAsync(x => x.Email == vm.Email)) ModelState.AddModelError(nameof(vm.Email), "Email đã được đăng ký – đăng nhập hoặc dùng email khác.");
        var vendor = await _db.Vendors.FirstOrDefaultAsync(v => v.TaxCode == vm.TaxCode);
        if (vendor is { Status: VendorStatus.Blacklisted }) ModelState.AddModelError(nameof(vm.TaxCode), "Nhà cung cấp này không được đăng ký trên hệ thống.");
        if (!ModelState.IsValid) { ViewBag.Categories = await _db.Categories.OrderBy(c => c.Name).ToListAsync(); return View(vm); }

        var isNew = vendor == null;
        if (isNew)
        {
            var codes = await _db.Vendors.Where(v => v.Code.StartsWith("NCC")).Select(v => v.Code).ToListAsync();
            var next = codes.Select(c => int.TryParse(c[3..], out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;
            vendor = new Vendor
            {
                Code = $"NCC{next:000}", Name = vm.CompanyName.Trim(), TaxCode = vm.TaxCode, Address = vm.Address, Phone = vm.Phone, Email = vm.Email,
                ContactPerson = vm.FullName, ContactPhone = vm.Mobile, Status = VendorStatus.Pending, Note = $"Tự đăng ký qua cổng NCC ngày {DateTime.Today:dd/MM/yyyy}."
            };
            var validCats = await _db.Categories.Where(c => vm.CategoryIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
            foreach (var c in validCats) vendor.Categories.Add(new VendorCategory { CategoryId = c });
            _db.Vendors.Add(vendor);
        }
        var u = new VendorUser { Vendor = vendor!, Email = vm.Email, FullName = vm.FullName.Trim(), Phone = vm.Mobile, Position = vm.Position, IsApproved = isNew };
        u.PasswordHash = _hasher.HashPassword(u, vm.Password);
        _db.VendorUsers.Add(u);
        await _db.SaveChangesAsync();

        foreach (var b in await _db.Users.Where(x => x.IsActive && x.Role == AppRoles.Buyer).Select(x => x.Id).ToListAsync())
            _db.Notifications.Add(new Notification
            {
                UserId = b, Kind = NotificationKind.Action, Url = $"/Vendors/Details/{vendor!.Id}",
                Title = isNew ? $"NCC mới đăng ký qua cổng: {vendor.Name}" : $"Tài khoản cổng NCC chờ xác nhận: {u.FullName} – {vendor.Name}",
                Message = isNew ? "Thẩm định hồ sơ và phê duyệt NCC." : $"{u.Email} đăng ký vào NCC đã có (MST {vendor.TaxCode}). Xác nhận nếu đúng người của NCC."
            });
        await _db.SaveChangesAsync();
        await SignInAsync(u);
        TempData["Success"] = isNew
            ? "Đăng ký thành công. Vui lòng tải lên hồ sơ pháp lý để bộ phận mua hàng thẩm định."
            : "Đã tạo tài khoản. Bộ phận mua hàng sẽ xác nhận bạn thuộc nhà cung cấp này trước khi bạn tham gia báo giá / dự thầu.";
        return RedirectToAction("Index", "Portal");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(PortalAuth.Scheme);
        return RedirectToAction(nameof(Login));
    }
}

/// <summary>Cơ sở cho trang cổng NCC: nạp tài khoản + NCC mỗi request, tự đăng xuất nếu bị khóa</summary>
[Authorize(AuthenticationSchemes = PortalAuth.Scheme)]
public abstract class PortalControllerBase : Controller
{
    protected readonly EProcDbContext Db;
    protected PortalControllerBase(EProcDbContext db) => Db = db;
    protected VendorUser Me { get; private set; } = null!;
    protected Vendor MyVendor => Me.Vendor!;
    /// <summary>Được tham gia báo giá / dự thầu / đơn hàng: tài khoản đã xác nhận và NCC không bị đình chỉ</summary>
    protected bool CanTrade => Me.IsApproved && MyVendor.Status is VendorStatus.Approved or VendorStatus.Pending;
    protected bool CanQuote => Me.IsApproved && MyVendor.Status == VendorStatus.Approved;

    public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var id = User.VendorUserId();
        var me = await Db.VendorUsers.Include(u => u.Vendor).FirstOrDefaultAsync(u => u.Id == id);
        if (me == null || !me.IsActive || me.Vendor == null || me.Vendor.Status == VendorStatus.Blacklisted)
        {
            await HttpContext.SignOutAsync(PortalAuth.Scheme);
            context.Result = RedirectToAction("Login", "PortalAccount");
            return;
        }
        Me = me;
        ViewBag.PortalUser = me;
        await next();
    }

    protected int MaxFileBytes => HttpContext.RequestServices.GetRequiredService<IOptions<ProcurementSettings>>().Value.MaxAttachmentMb * 1024 * 1024;

    protected async Task NotifyBuyersAsync(IEnumerable<int> userIds, string title, string? msg, string url)
    {
        foreach (var uid in userIds.Distinct())
            Db.Notifications.Add(new Notification { UserId = uid, Kind = NotificationKind.Info, Title = title.Length > 200 ? title[..199] + "…" : title, Message = msg, Url = url });
        await Db.SaveChangesAsync();
    }

    protected static readonly string[] AllowedExt = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".zip", ".rar", ".7z", ".jpg", ".jpeg", ".png" };
    protected string? CheckFile(IFormFile? f)
    {
        if (f == null || f.Length == 0) return null;
        if (f.Length > MaxFileBytes) return $"File vượt quá {MaxFileBytes / 1024 / 1024} MB.";
        if (!AllowedExt.Contains(Path.GetExtension(f.FileName).ToLowerInvariant())) return "Định dạng file không được hỗ trợ (pdf, doc(x), xls(x), zip, rar, 7z, jpg, png).";
        return null;
    }

    protected static async Task<byte[]> ReadAsync(IFormFile f)
    {
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms);
        return ms.ToArray();
    }
}

/// <summary>Tổng quan, hồ sơ công ty, hồ sơ pháp lý</summary>
public class PortalController : PortalControllerBase
{
    private readonly IPasswordHasher<VendorUser> _hasher;
    public PortalController(EProcDbContext db, IPasswordHasher<VendorUser> hasher) : base(db) => _hasher = hasher;

    public class Dashboard
    {
        public List<RfqVendor> Invitations { get; set; } = new();
        public List<DirectAward> DirectQuotes { get; set; } = new();
        public List<TenderBidder> MyTenders { get; set; } = new();
        public List<Tender> OpenTenders { get; set; } = new();
        public List<PurchaseOrder> Orders { get; set; } = new();
        public List<VendorDocument> Expiring { get; set; } = new();
        public int DocCount { get; set; }
    }

    public async Task<IActionResult> Index()
    {
        var v = MyVendor.Id; var soon = DateTime.Today.AddDays(30);
        var vm = new Dashboard
        {
            Invitations = await Db.RfqVendors.Include(x => x.Rfq).Where(x => x.VendorId == v && x.Rfq!.Status != RfqStatus.Draft)
                .OrderByDescending(x => x.Rfq!.IssueDate).Take(20).ToListAsync(),
            DirectQuotes = await Db.DirectAwards.Where(d => d.VendorId == v && d.QuoteRequestedAt != null).OrderByDescending(d => d.QuoteRequestedAt).Take(10).ToListAsync(),
            MyTenders = await Db.TenderBidders.Include(x => x.Tender).Where(x => x.VendorId == v).OrderByDescending(x => x.RegisteredAt).Take(20).ToListAsync(),
            OpenTenders = await Db.Tenders.Where(t => t.Status == TenderStatus.Published && t.ClosingAt > DateTime.Now && !t.Bidders.Any(b => b.VendorId == v))
                .OrderBy(t => t.ClosingAt).Take(10).ToListAsync(),
            Orders = await Db.PurchaseOrders.Where(p => p.VendorId == v && (p.Status == PoStatus.Issued || p.Status == PoStatus.PartiallyReceived))
                .OrderBy(p => p.DeliveryDate).Take(20).ToListAsync(),
            Expiring = await Db.VendorDocuments.Where(d => d.VendorId == v && d.ExpiryDate != null && d.ExpiryDate <= soon).OrderBy(d => d.ExpiryDate).ToListAsync(),
            DocCount = await Db.VendorDocuments.CountAsync(d => d.VendorId == v)
        };
        ViewData["Title"] = "Tổng quan";
        return View(vm);
    }

    public async Task<IActionResult> Profile()
    {
        ViewBag.Documents = await Db.VendorDocuments.AsNoTracking().Include(d => d.VerifiedBy).Where(d => d.VendorId == MyVendor.Id)
            .OrderBy(d => d.Type).ThenByDescending(d => d.UploadedAt).Select(d => new VendorDocument
            { Id = d.Id, Type = d.Type, Number = d.Number, IssueDate = d.IssueDate, ExpiryDate = d.ExpiryDate, FileName = d.FileName, Size = d.Size, Status = d.Status, Note = d.Note, UploadedAt = d.UploadedAt })
            .ToListAsync();
        ViewBag.Users = await Db.VendorUsers.AsNoTracking().Where(u => u.VendorId == MyVendor.Id).OrderBy(u => u.CreatedAt).ToListAsync();
        ViewData["Title"] = "Hồ sơ công ty";
        return View(MyVendor);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(string? address, string? phone, string? email, string? contactPerson, string? contactPhone, string? bankName, string? bankAccount, string? paymentTerms)
    {
        string? T(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
        var v = MyVendor;
        v.Address = T(address, 300); v.Phone = T(phone, 20); v.ContactPerson = T(contactPerson, 150); v.ContactPhone = T(contactPhone, 20);
        v.BankName = T(bankName, 100); v.BankAccount = T(bankAccount, 30); v.PaymentTerms = T(paymentTerms, 200);
        if (email != null && new EmailAddressAttribute().IsValid(email)) v.Email = T(email, 150);
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã cập nhật thông tin công ty.";
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(60 * 1024 * 1024)]
    public async Task<IActionResult> UploadDocument(VendorDocumentType type, string? number, DateTime? issueDate, DateTime? expiryDate, string? note, IFormFile? file)
    {
        if (file == null || file.Length == 0) { TempData["Error"] = "Chọn file hồ sơ."; return RedirectToAction(nameof(Profile)); }
        if (CheckFile(file) is string err) { TempData["Error"] = err; return RedirectToAction(nameof(Profile)); }
        if (expiryDate.HasValue && issueDate.HasValue && expiryDate < issueDate) { TempData["Error"] = "Ngày hết hiệu lực phải sau ngày cấp."; return RedirectToAction(nameof(Profile)); }
        Db.VendorDocuments.Add(new VendorDocument
        {
            VendorId = MyVendor.Id, Type = type, Number = number?.Trim(), IssueDate = issueDate, ExpiryDate = expiryDate, Note = note?.Trim(),
            FileName = Path.GetFileName(file.FileName), ContentType = file.ContentType ?? "application/octet-stream", Size = file.Length,
            Data = await ReadAsync(file), UploadedByVendorUserId = Me.Id
        });
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(await Db.Users.Where(u => u.IsActive && u.Role == AppRoles.Buyer).Select(u => u.Id).ToListAsync(),
            $"{MyVendor.Name} tải lên hồ sơ: {type.GetDisplayName()}", "Xác minh hồ sơ trong trang nhà cung cấp.", $"/Vendors/Details/{MyVendor.Id}");
        TempData["Success"] = "Đã tải lên hồ sơ – chờ bộ phận mua hàng xác minh.";
        return RedirectToAction(nameof(Profile));
    }

    public async Task<IActionResult> Document(int id)
    {
        var d = await Db.VendorDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.VendorId == MyVendor.Id);
        return d == null ? NotFound() : File(d.Data, d.ContentType, d.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDocument(int id)
    {
        var d = await Db.VendorDocuments.FirstOrDefaultAsync(x => x.Id == id && x.VendorId == MyVendor.Id && x.Status != VendorDocumentStatus.Verified);
        if (d == null) { TempData["Error"] = "Chỉ xóa được hồ sơ chưa xác minh."; return RedirectToAction(nameof(Profile)); }
        Db.VendorDocuments.Remove(d);
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã xóa hồ sơ.";
        return RedirectToAction(nameof(Profile));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword, string confirmPassword)
    {
        if (_hasher.VerifyHashedPassword(Me, Me.PasswordHash, currentPassword ?? "") == PasswordVerificationResult.Failed) TempData["Error"] = "Mật khẩu hiện tại không đúng.";
        else if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 8) TempData["Error"] = "Mật khẩu mới tối thiểu 8 ký tự.";
        else if (newPassword != confirmPassword) TempData["Error"] = "Mật khẩu nhập lại không khớp.";
        else { Me.PasswordHash = _hasher.HashPassword(Me, newPassword); await Db.SaveChangesAsync(); TempData["Success"] = "Đã đổi mật khẩu."; }
        return RedirectToAction(nameof(Profile));
    }
}

/// <summary>Báo giá online (chào hàng cạnh tranh)</summary>
public class PortalRfqsController : PortalControllerBase
{
    private readonly SealingService _seal;
    public PortalRfqsController(EProcDbContext db, SealingService seal) : base(db) => _seal = seal;

    private Task<RfqVendor?> LoadAsync(int rfqId) => Db.RfqVendors.Include(x => x.Rfq).ThenInclude(r => r!.Lines)
        .Include(x => x.Rfq).ThenInclude(r => r!.AwardedVendor).Include(x => x.QuoteLines)
        .FirstOrDefaultAsync(x => x.RfqId == rfqId && x.VendorId == MyVendor.Id && x.Rfq!.Status != RfqStatus.Draft);

    /// <summary>Hạn nộp tính hết ngày hạn</summary>
    public static DateTime CloseAt(Rfq r) => r.Deadline.Date.AddDays(1);

    public async Task<IActionResult> Details(int id)
    {
        var rv = await LoadAsync(id);
        if (rv == null) return NotFound();
        ViewBag.Submissions = await Db.VendorSubmissions.AsNoTracking().Where(s => s.RfqVendorId == rv.Id).OrderByDescending(s => s.Revision)
            .Select(s => new VendorSubmission { Id = s.Id, Revision = s.Revision, SubmittedAt = s.SubmittedAt, Sha256 = s.Sha256, IsLatest = s.IsLatest, IsWithdrawn = s.IsWithdrawn, FileName = s.FileName, UnsealedAt = s.UnsealedAt })
            .ToListAsync();
        ViewBag.CanSubmit = CanQuote && rv.Rfq!.Status == RfqStatus.Open && DateTime.Now < CloseAt(rv.Rfq);
        ViewBag.CanQuote = CanQuote;
        ViewData["Title"] = rv.Rfq!.Number;
        return View(rv);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(60 * 1024 * 1024)]
    public async Task<IActionResult> Submit(int id, List<decimal> prices, List<string?> notes, int? leadTimeDays, string? paymentTerms, string? warranty, decimal vatPercent, string? note, IFormFile? file)
    {
        var rv = await LoadAsync(id);
        if (rv == null) return NotFound();
        var r = rv.Rfq!;
        IActionResult Back(string err) { TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanQuote) return Back("Tài khoản / NCC chưa được phê duyệt để báo giá.");
        if (r.Status != RfqStatus.Open || DateTime.Now >= CloseAt(r)) return Back("Đã hết hạn nộp báo giá.");
        var lines = r.Lines.OrderBy(l => l.SortOrder).ToList();
        if (prices.Count != lines.Count || prices.Any(p => p < 0)) return Back("Đơn giá không hợp lệ.");
        if (prices.All(p => p == 0)) return Back("Nhập đơn giá cho ít nhất một dòng.");
        if (vatPercent is < 0 or > 20) return Back("VAT không hợp lệ.");
        if (leadTimeDays is < 0 or > 365) return Back("Thời gian giao không hợp lệ.");
        if (CheckFile(file) is string ferr) return Back(ferr);

        var payload = new QuotePayload(rv.Id, leadTimeDays, paymentTerms?.Trim(), warranty?.Trim(), vatPercent, note?.Trim(),
            lines.Select((l, i) => new QuoteLinePayload(l.Id, Math.Round(prices[i], 0), i < notes.Count ? notes[i]?.Trim() : null)).ToList(),
            file == null ? null : Path.GetFileName(file.FileName), file?.ContentType, file == null ? null : Convert.ToBase64String(await ReadAsync(file)));
        var prev = await Db.VendorSubmissions.Where(s => s.RfqVendorId == rv.Id).ToListAsync();
        foreach (var p in prev) p.IsLatest = false;
        var sub = _seal.Seal(payload, SubmissionKind.Quote, Me.Id, rv.Id, null, prev.Count + 1, payload.FileName, file?.Length ?? 0);
        Db.VendorSubmissions.Add(sub);
        // Chỉ đánh dấu đã nộp – giá chỉ được đưa vào hệ thống khi mở báo giá
        rv.Responded = true; rv.RespondedAt = sub.SubmittedAt;
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { r.BuyerId }, $"{MyVendor.Name} đã nộp báo giá online cho {r.Number}" + (sub.Revision > 1 ? $" (lần {sub.Revision})" : ""),
            "Báo giá được niêm phong đến khi mở báo giá.", $"/Rfqs/Details/{r.Id}");
        TempData["Success"] = $"Đã nộp báo giá lúc {sub.SubmittedAt:dd/MM/yyyy HH:mm:ss}. Mã biên nhận: {sub.Sha256}. Báo giá được niêm phong đến khi mở.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id)
    {
        var rv = await LoadAsync(id);
        if (rv == null) return NotFound();
        if (rv.Rfq!.Status != RfqStatus.Open || DateTime.Now >= CloseAt(rv.Rfq)) { TempData["Error"] = "Đã hết hạn – không rút được."; return RedirectToAction(nameof(Details), new { id }); }
        var subs = await Db.VendorSubmissions.Where(s => s.RfqVendorId == rv.Id && !s.IsWithdrawn).ToListAsync();
        foreach (var s in subs) s.IsWithdrawn = true;
        rv.Responded = false; rv.RespondedAt = null;
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { rv.Rfq.BuyerId }, $"{MyVendor.Name} đã rút báo giá {rv.Rfq.Number}", null, $"/Rfqs/Details/{rv.RfqId}");
        TempData["Success"] = "Đã rút báo giá.";
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>
/// Đấu thầu online: thông báo mời thầu, tải HSMT, đề nghị làm rõ HSMT, nộp HSDT niêm phong (nhiều thành phần),
/// biên nhận, biên bản mở thầu, kết quả đánh giá của mình, kiến nghị kết quả.
/// </summary>
public class PortalTendersController : PortalControllerBase
{
    private readonly SealingService _seal;
    public PortalTendersController(EProcDbContext db, SealingService seal) : base(db) => _seal = seal;

    private ProcurementSettings Settings => HttpContext.RequestServices.GetRequiredService<IOptions<ProcurementSettings>>().Value;

    public record PublicFile(int Id, string FileName, long Size, string? Note, DateTime UploadedAt);
    public record TenderRow(Tender Tender, TenderBidder? Bidder, DateTime? LastAmendment)
    {
        public bool AmendedAfterSubmit => Bidder?.SubmittedAt != null && LastAmendment > Bidder.SubmittedAt;
    }

    public class TenderPage
    {
        public Tender Tender { get; set; } = null!;
        public TenderBidder? Bidder { get; set; }
        public List<VendorSubmission> Submissions { get; set; } = new();
        public VendorSubmission? Latest => Submissions.FirstOrDefault(s => s.IsLatest && !s.IsWithdrawn);
        public bool Open { get; set; }
        public bool CanTrade { get; set; }
        public List<PublicFile> Files { get; set; } = new();
        public List<TenderQuestion> MyQuestions { get; set; } = new();
        public DateTime QuestionDeadline { get; set; }
        public bool CanAsk { get; set; }
        public List<TenderClarification> AmendmentsAfterSubmit { get; set; } = new();
        public bool PricesVisible { get; set; }
        public BidResult? MyResult { get; set; }
        public List<TenderComplaint> MyComplaints { get; set; } = new();
        public bool CanComplain { get; set; }
        public int MaxFileMb { get; set; }
    }

    // ------------------------------------------------------------------ Danh sách
    public async Task<IActionResult> Index(string? tab, string? q)
    {
        var v = MyVendor.Id;
        var query = Db.Tenders.AsNoTracking().Include(t => t.AwardedVendor)
            .Where(t => t.Status == TenderStatus.Published || t.Bidders.Any(b => b.VendorId == v));
        tab = tab is "open" or "mine" ? tab : "all";
        if (tab == "open") query = query.Where(t => t.Status == TenderStatus.Published && t.ClosingAt > DateTime.Now);
        if (tab == "mine") query = query.Where(t => t.Bidders.Any(b => b.VendorId == v));
        if (!string.IsNullOrWhiteSpace(q)) { var k = q.Trim(); query = query.Where(t => t.Number.Contains(k) || t.Title.Contains(k)); }
        var tenders = await query.OrderByDescending(t => t.Status == TenderStatus.Published).ThenBy(t => t.ClosingAt).ToListAsync();
        var ids = tenders.Select(t => t.Id).ToList();
        var mine = await Db.TenderBidders.AsNoTracking().Where(b => b.VendorId == v && ids.Contains(b.TenderId)).ToDictionaryAsync(b => b.TenderId);
        var amended = await Db.TenderClarifications.Where(c => c.IsAmendment && ids.Contains(c.TenderId))
            .GroupBy(c => c.TenderId).Select(g => new { g.Key, At = g.Max(c => c.CreatedAt) }).ToDictionaryAsync(x => x.Key, x => x.At);
        ViewBag.Tab = tab; ViewBag.Q = q;
        ViewData["Title"] = "Gói thầu";
        return View(tenders.Select(t => new TenderRow(t, mine.GetValueOrDefault(t.Id), amended.TryGetValue(t.Id, out var a) ? a : null)).ToList());
    }

    // ------------------------------------------------------------------ Chi tiết
    /// <summary>Gói thầu nhà thầu được xem: đã đăng tải (không hủy), hoặc đã tham gia</summary>
    private IQueryable<Tender> Visible() => Db.Tenders.Where(x =>
        x.Status >= TenderStatus.Published && (x.Status != TenderStatus.Cancelled || x.Bidders.Any(b => b.VendorId == MyVendor.Id)));

    public async Task<IActionResult> Details(int id)
    {
        var t = await Visible().AsNoTracking().AsSplitQuery().Include(x => x.Lines).Include(x => x.Clarifications).Include(x => x.AwardedVendor).Include(x => x.Buyer)
            .Include(x => x.Bidders).ThenInclude(b => b.Vendor)
            .Include(x => x.Bidders).ThenInclude(b => b.Scores)
            .Include(x => x.Committee)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (t == null) return NotFound();
        t.Lines = t.Lines.OrderBy(l => l.SortOrder).ToList();
        var me = t.Bidders.FirstOrDefault(b => b.VendorId == MyVendor.Id);
        var now = DateTime.Now;
        var s = Settings;
        var vm = new TenderPage
        {
            Tender = t, Bidder = me, CanTrade = CanTrade, MaxFileMb = s.MaxAttachmentMb,
            Open = t.Status == TenderStatus.Published && now < t.ClosingAt,
            Files = await Db.Attachments.AsNoTracking().Where(a => a.DocumentType == DocType.Tender && a.DocumentId == id && a.IsPublic).OrderBy(a => a.UploadedAt)
                .Select(a => new PublicFile(a.Id, a.FileName, a.Size, a.Note, a.UploadedAt)).ToListAsync(),
            QuestionDeadline = TenderEvaluation.QuestionDeadline(t, s.TenderQuestionMinDays),
            PricesVisible = TenderEvaluation.PricesVisible(t)
        };
        vm.CanAsk = vm.Open && me != null && CanTrade && now <= vm.QuestionDeadline;
        if (me != null)
        {
            vm.Submissions = await Db.VendorSubmissions.AsNoTracking().Where(x => x.TenderBidderId == me.Id).OrderByDescending(x => x.Revision)
                .Select(x => new VendorSubmission { Id = x.Id, Revision = x.Revision, SubmittedAt = x.SubmittedAt, Sha256 = x.Sha256, IsLatest = x.IsLatest, IsWithdrawn = x.IsWithdrawn, FileName = x.FileName, FileSize = x.FileSize, UnsealedAt = x.UnsealedAt })
                .ToListAsync();
            vm.MyQuestions = await Db.TenderQuestions.AsNoTracking().Include(x => x.Clarification).Where(x => x.TenderId == id && x.VendorId == MyVendor.Id)
                .OrderByDescending(x => x.AskedAt).ToListAsync();
            vm.MyComplaints = await Db.TenderComplaints.AsNoTracking().Where(c => c.TenderId == id && c.VendorId == MyVendor.Id).OrderBy(c => c.ReceivedAt).ToListAsync();
            vm.AmendmentsAfterSubmit = vm.Open ? TenderEvaluation.AmendmentsAfter(t, me.SubmittedAt) : new();
            vm.CanComplain = CanTrade && TenderEvaluation.CanComplain(t, me, now);
            if (t.Status is TenderStatus.Awarded or TenderStatus.Contracted)
                vm.MyResult = TenderEvaluation.Evaluate(t).FirstOrDefault(r => r.Bid.VendorId == MyVendor.Id);
        }
        ViewData["Title"] = t.Number;
        return View(vm);
    }

    /// <summary>Tải file HSMT (chỉ file bên mời thầu đánh dấu công khai)</summary>
    public async Task<IActionResult> Document(int id, int fileId)
    {
        if (!await Visible().AnyAsync(t => t.Id == id)) return NotFound();
        var a = await Db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == fileId && x.DocumentType == DocType.Tender && x.DocumentId == id && x.IsPublic);
        return a == null ? NotFound() : File(a.Data, a.ContentType, a.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(int id)
    {
        var t = await Db.Tenders.FirstOrDefaultAsync(x => x.Id == id && x.Status == TenderStatus.Published);
        if (t == null || DateTime.Now >= t.ClosingAt) { TempData["Error"] = "Gói thầu không còn nhận đăng ký."; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanTrade) { TempData["Error"] = "Tài khoản chưa được xác nhận thuộc nhà cung cấp."; return RedirectToAction(nameof(Details), new { id }); }
        if (!await Db.TenderBidders.AnyAsync(b => b.TenderId == id && b.VendorId == MyVendor.Id))
        {
            Db.TenderBidders.Add(new TenderBidder { TenderId = id, VendorId = MyVendor.Id, RegisteredAt = DateTime.Now, VatPercent = 10 });
            await Db.SaveChangesAsync();
            await NotifyBuyersAsync(new[] { t.BuyerId }, $"{MyVendor.Name} nhận HSMT gói thầu {t.Number} qua cổng NCC", null, $"/Tenders/Details/{t.Id}");
        }
        TempData["Success"] = "Đã đăng ký nhận HSMT. Bạn có thể tải HSMT, gửi đề nghị làm rõ và nộp HSDT online trước thời điểm đóng thầu.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Làm rõ HSMT
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask(int id, string? content)
    {
        var t = await Db.Tenders.FirstOrDefaultAsync(x => x.Id == id);
        var b = await Db.TenderBidders.AnyAsync(x => x.TenderId == id && x.VendorId == MyVendor.Id);
        IActionResult Back(string err) { TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (t == null || !b) return Back("Đăng ký nhận HSMT trước khi gửi đề nghị làm rõ.");
        if (!CanTrade) return Back("Tài khoản chưa được xác nhận thuộc nhà cung cấp.");
        var deadline = TenderEvaluation.QuestionDeadline(t, Settings.TenderQuestionMinDays);
        if (t.Status != TenderStatus.Published || DateTime.Now > deadline)
            return Back($"Đã hết hạn gửi đề nghị làm rõ HSMT ({deadline.ToDateTime()} – {Settings.TenderQuestionMinDays} ngày trước đóng thầu).");
        if (string.IsNullOrWhiteSpace(content)) return Back("Nhập nội dung đề nghị làm rõ.");
        var text = content.Trim();
        if (text.Length > 2000) return Back("Nội dung tối đa 2000 ký tự.");
        Db.TenderQuestions.Add(new TenderQuestion { TenderId = id, VendorId = MyVendor.Id, VendorUserId = Me.Id, Content = text, AskedAt = DateTime.Now });
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { t.BuyerId }, $"{MyVendor.Name} đề nghị làm rõ HSMT gói {t.Number}", text, $"/Tenders/Details/{t.Id}");
        TempData["Success"] = "Đã gửi đề nghị làm rõ. Nội dung trả lời được đăng công khai cho mọi nhà thầu (không nêu tên nhà thầu hỏi).";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ------------------------------------------------------------------ Nộp / rút HSDT
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(60 * 1024 * 1024)]
    public async Task<IActionResult> Submit(int id, List<decimal> prices, decimal discountPercent, decimal vatPercent, int? leadTimeDays, int? validityDays,
        decimal bidSecurityAmount, string? bidSecurityRef, DateTime? bidSecurityExpiry, string? note,
        IFormFile? techFile, IFormFile? finFile, IFormFile? securityFile, IFormFile? qualFile, List<IFormFile>? otherFiles)
    {
        var t = await Db.Tenders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id);
        var b = await Db.TenderBidders.FirstOrDefaultAsync(x => x.TenderId == id && x.VendorId == MyVendor.Id);
        IActionResult Back(string err) { TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (t == null || b == null) return Back("Đăng ký nhận HSMT trước khi nộp HSDT.");
        if (!CanTrade) return Back("Tài khoản chưa được xác nhận thuộc nhà cung cấp.");
        if (t.Status != TenderStatus.Published || DateTime.Now >= t.ClosingAt) return Back($"Đã quá thời điểm đóng thầu {t.ClosingAt:dd/MM/yyyy HH:mm} – không nhận HSDT.");
        var lines = t.Lines.OrderBy(l => l.SortOrder).ToList();
        if (prices.Count != lines.Count || prices.Any(p => p <= 0)) return Back("Nhập đơn giá dự thầu cho tất cả các dòng.");
        if (discountPercent is < 0 or >= 100 || vatPercent is < 0 or > 20) return Back("Giảm giá / VAT không hợp lệ.");
        if (leadTimeDays is null or < 0 or > 3650) return Back("Nhập tiến độ thực hiện (ngày).");
        var problems = TenderEvaluation.BidProblems(t, bidSecurityAmount, bidSecurityRef, bidSecurityExpiry, validityDays);
        if (problems.Count > 0) return Back("HSDT chưa đáp ứng HSMT: " + string.Join("; ", problems) + ".");

        if (techFile == null || techFile.Length == 0) return Back("Đính kèm file đề xuất kỹ thuật.");
        var parts = new List<(BidFileKind Kind, IFormFile File)> { (BidFileKind.Technical, techFile) };
        if (finFile is { Length: > 0 }) parts.Add((BidFileKind.Financial, finFile));
        if (securityFile is { Length: > 0 }) parts.Add((BidFileKind.BidSecurity, securityFile));
        if (qualFile is { Length: > 0 }) parts.Add((BidFileKind.Qualification, qualFile));
        parts.AddRange((otherFiles ?? new()).Where(f => f.Length > 0).Take(5).Select(f => (BidFileKind.Other, f)));
        foreach (var (kind, f) in parts) if (CheckFile(f) is string ferr) return Back($"{kind.GetDisplayName()}: {ferr}");

        var files = new List<BidFilePayload>();
        foreach (var (kind, f) in parts)
            files.Add(new BidFilePayload(kind, Path.GetFileName(f.FileName), f.ContentType, Convert.ToBase64String(await ReadAsync(f))));
        var payload = new BidPayload(b.Id, discountPercent, vatPercent, leadTimeDays, validityDays, bidSecurityAmount, bidSecurityRef?.Trim(), bidSecurityExpiry, note?.Trim(),
            lines.Select((l, i) => new BidLinePayload(l.Id, Math.Round(prices[i], 0))).ToList(), null, null, null, files);
        var names = string.Join("; ", files.Select(f => f.FileName));
        var prev = await Db.VendorSubmissions.Where(s => s.TenderBidderId == b.Id).ToListAsync();
        foreach (var p in prev) p.IsLatest = false;
        var sub = _seal.Seal(payload, SubmissionKind.Bid, Me.Id, null, b.Id, prev.Count + 1, names.Length > 260 ? names[..259] + "…" : names, parts.Sum(p => p.File.Length));
        Db.VendorSubmissions.Add(sub);
        b.Submitted = true; b.SubmittedAt = sub.SubmittedAt; // nội dung (kể cả giá) chỉ đưa vào khi mở thầu
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { t.BuyerId }, $"{MyVendor.Name} đã nộp HSDT online gói {t.Number}" + (sub.Revision > 1 ? $" (lần {sub.Revision})" : ""),
            "HSDT được niêm phong đến thời điểm mở thầu.", $"/Tenders/Details/{t.Id}");
        TempData["Success"] = $"Đã nộp HSDT lúc {sub.SubmittedAt:dd/MM/yyyy HH:mm:ss} ({files.Count} file). Mã biên nhận: {sub.Sha256}. HSDT được niêm phong đến khi mở thầu.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(int id)
    {
        var t = await Db.Tenders.FindAsync(id);
        var b = await Db.TenderBidders.FirstOrDefaultAsync(x => x.TenderId == id && x.VendorId == MyVendor.Id);
        if (t == null || b == null || t.Status != TenderStatus.Published || DateTime.Now >= t.ClosingAt) { TempData["Error"] = "Không rút được HSDT."; return RedirectToAction(nameof(Details), new { id }); }
        foreach (var s in await Db.VendorSubmissions.Where(s => s.TenderBidderId == b.Id && !s.IsWithdrawn).ToListAsync()) s.IsWithdrawn = true;
        b.Submitted = false; b.SubmittedAt = null;
        await Db.SaveChangesAsync();
        await NotifyBuyersAsync(new[] { t.BuyerId }, $"{MyVendor.Name} đã rút HSDT gói {t.Number}", null, $"/Tenders/Details/{t.Id}");
        TempData["Success"] = "Đã rút HSDT.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Biên nhận nộp HSDT (in / lưu PDF)</summary>
    public async Task<IActionResult> Receipt(int id, int submissionId)
    {
        var s = await Db.VendorSubmissions.AsNoTracking().Include(x => x.VendorUser).Include(x => x.TenderBidder).ThenInclude(b => b!.Tender)
            .FirstOrDefaultAsync(x => x.Id == submissionId && x.TenderBidder!.TenderId == id && x.TenderBidder.VendorId == MyVendor.Id);
        if (s == null) return NotFound();
        s.SealedPayload = string.Empty; // không đưa nội dung niêm phong ra view
        ViewBag.Vendor = MyVendor;
        ViewData["Title"] = $"Biên nhận HSDT {s.TenderBidder!.Tender!.Number} – lần {s.Revision}";
        return View(s);
    }

    // ------------------------------------------------------------------ Sau đóng thầu
    /// <summary>Thông báo kết quả lựa chọn nhà thầu gửi quý công ty</summary>
    public async Task<IActionResult> ResultNotice(int id)
    {
        var t = await Visible().AsNoTracking().AsSplitQuery().Include(x => x.Buyer).Include(x => x.AwardedVendor)
            .Include(x => x.Bidders).ThenInclude(b => b.Vendor).Include(x => x.Bidders).ThenInclude(b => b.Scores).Include(x => x.Committee)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (t == null || t.Status is not (TenderStatus.Awarded or TenderStatus.Contracted)) return NotFound();
        var r = TenderEvaluation.Evaluate(t).FirstOrDefault(x => x.Bid.VendorId == MyVendor.Id);
        if (r == null) return NotFound();
        ViewBag.Result = r;
        ViewBag.PortalLayout = true;
        return View("~/Views/Tenders/ResultNotice.cshtml", t);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Complain(int id, string? content)
    {
        var t = await Db.Tenders.FirstOrDefaultAsync(x => x.Id == id);
        var b = await Db.TenderBidders.AsNoTracking().FirstOrDefaultAsync(x => x.TenderId == id && x.VendorId == MyVendor.Id);
        IActionResult Back(string err) { TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (t == null || b == null) return NotFound();
        if (!CanTrade) return Back("Tài khoản chưa được xác nhận thuộc nhà cung cấp.");
        if (!TenderEvaluation.CanComplain(t, b, DateTime.Now))
            return Back(t.ComplaintDeadline.HasValue && DateTime.Now > t.ComplaintDeadline ? $"Đã hết thời hạn kiến nghị ({t.ComplaintDeadline.ToDateTime()})." : "Chỉ nhà thầu đã nộp HSDT được kiến nghị sau khi công bố kết quả.");
        if (string.IsNullOrWhiteSpace(content)) return Back("Nhập nội dung kiến nghị.");
        var text = content.Trim();
        if (text.Length > 2000) return Back("Nội dung tối đa 2000 ký tự.");
        Db.TenderComplaints.Add(new TenderComplaint { TenderId = id, VendorId = MyVendor.Id, Content = text, ReceivedAt = DateTime.Now });
        await Db.SaveChangesAsync();
        var directors = await Db.Users.Where(u => u.IsActive && u.Role == AppRoles.Director).Select(u => u.Id).ToListAsync();
        await NotifyBuyersAsync(directors.Append(t.BuyerId), $"{MyVendor.Name} kiến nghị kết quả gói thầu {t.Number}", text, $"/Tenders/Details/{t.Id}");
        TempData["Success"] = "Đã gửi kiến nghị. Hợp đồng chưa được ký cho tới khi kiến nghị được giải quyết.";
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>Đơn hàng: xác nhận / từ chối PO, thông báo giao hàng</summary>
public class PortalOrdersController : PortalControllerBase
{
    public PortalOrdersController(EProcDbContext db) : base(db) { }

    private static readonly PoStatus[] Visible = { PoStatus.Issued, PoStatus.PartiallyReceived, PoStatus.Received, PoStatus.Cancelled };

    public async Task<IActionResult> Index()
    {
        ViewData["Title"] = "Đơn hàng";
        return View(await Db.PurchaseOrders.AsNoTracking().Where(p => p.VendorId == MyVendor.Id && Visible.Contains(p.Status) && p.IssuedAt != null)
            .OrderByDescending(p => p.OrderDate).Take(200).ToListAsync());
    }

    private Task<PurchaseOrder?> LoadAsync(int id, bool track = false)
    {
        var q = Db.PurchaseOrders.Include(p => p.Lines).Include(p => p.Buyer).Where(p => p.Id == id && p.VendorId == MyVendor.Id && Visible.Contains(p.Status) && p.IssuedAt != null);
        return track ? q.FirstOrDefaultAsync() : q.AsNoTracking().FirstOrDefaultAsync();
    }

    public async Task<IActionResult> Details(int id)
    {
        var po = await LoadAsync(id);
        if (po == null) return NotFound();
        po.Lines = po.Lines.OrderBy(l => l.SortOrder).ToList();
        ViewBag.Shipments = await Db.PoShipments.AsNoTracking().Include(s => s.VendorUser).Where(s => s.PurchaseOrderId == id).OrderBy(s => s.CreatedAt).ToListAsync();
        ViewBag.Receipts = await Db.GoodsReceipts.AsNoTracking().Include(g => g.Lines).Where(g => g.PurchaseOrderId == id).OrderBy(g => g.ReceiptDate).ToListAsync();
        ViewBag.Invoices = await Db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.PurchaseOrderId == id).OrderBy(i => i.CreatedAt).ToListAsync();
        ViewBag.Payments = await Db.Payments.AsNoTracking().Where(p => p.Invoice!.PurchaseOrderId == id).ToListAsync();
        ViewData["Title"] = po.Number;
        return View(po);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Respond(int id, bool accept, DateTime? promisedDate, string? response)
    {
        var po = await LoadAsync(id, track: true);
        if (po == null) return NotFound();
        IActionResult Back(string? err = null) { if (err != null) TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanTrade) return Back("Tài khoản chưa được xác nhận thuộc nhà cung cấp.");
        if (po.Status != PoStatus.Issued || po.VendorConfirmedAt != null || po.VendorRejectedAt != null) return Back("Đơn hàng đã được phản hồi.");
        if (!accept && string.IsNullOrWhiteSpace(response)) return Back("Nhập lý do từ chối.");
        if (accept && promisedDate.HasValue && promisedDate < DateTime.Today) return Back("Ngày cam kết giao không hợp lệ.");
        var text = response?.Trim();
        if (text?.Length > 1000) text = text[..1000];
        if (accept) { po.VendorConfirmedAt = DateTime.Now; po.VendorPromisedDate = promisedDate ?? po.DeliveryDate; }
        else po.VendorRejectedAt = DateTime.Now;
        po.VendorResponse = text;
        Db.ApprovalHistories.Add(new ApprovalHistory
        {
            DocumentType = DocType.PO, DocumentId = po.Id, UserId = po.BuyerId, CreatedAt = DateTime.Now,
            Action = accept ? $"NCC xác nhận đơn hàng (cổng NCC), cam kết giao {po.VendorPromisedDate:dd/MM/yyyy}" : "NCC từ chối đơn hàng (cổng NCC)",
            Comment = $"{Me.FullName} – {MyVendor.Name}" + (string.IsNullOrEmpty(text) ? "" : ": " + text)
        });
        await Db.SaveChangesAsync();
        var promiseLate = accept && po.VendorPromisedDate > po.DeliveryDate;
        Db.Notifications.Add(new Notification
        {
            UserId = po.BuyerId, Url = $"/PurchaseOrders/Details/{po.Id}",
            Kind = accept && !promiseLate ? NotificationKind.Info : NotificationKind.Warning,
            Title = accept ? $"{MyVendor.Name} xác nhận {po.Number}" + (promiseLate ? $" – cam kết giao {po.VendorPromisedDate:dd/MM} (trễ hơn yêu cầu)" : "") : $"{MyVendor.Name} TỪ CHỐI {po.Number}",
            Message = text
        });
        await Db.SaveChangesAsync();
        TempData["Success"] = accept ? "Đã xác nhận đơn hàng." : "Đã gửi từ chối đơn hàng.";
        return Back();
    }

    /// <summary>NCC gửi hóa đơn điện tử cho PO đã nhận hàng – hệ thống đối chiếu 3 bên ngay</summary>
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> SubmitInvoice(int id, InvoiceForm f, IFormFile? file)
    {
        var po = await Db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id && p.VendorId == MyVendor.Id && p.IssuedAt != null);
        if (po == null) return NotFound();
        IActionResult Back(string? err = null) { if (err != null) TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanTrade) return Back("Tài khoản chưa được xác nhận thuộc nhà cung cấp.");
        if (po.Status is not (PoStatus.PartiallyReceived or PoStatus.Received)) return Back("Chỉ gửi hóa đơn sau khi bên mua đã nhận hàng.");
        if (file == null || file.Length == 0) return Back("Đính kèm file hóa đơn điện tử (pdf / xml).");
        f.PurchaseOrderId = id;
        var fin = HttpContext.RequestServices.GetRequiredService<FinanceService>();
        var (inv, match, err) = await InvoiceFactory.CreateAsync(Db, fin, po, f, file, MaxFileBytes, null, Me.Id);
        if (err != null) return Back(err);
        await Db.SaveChangesAsync();
        Db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = DocType.Invoice, DocumentId = inv!.Id, UserId = po.BuyerId, CreatedAt = DateTime.Now,
            Action = $"NCC gửi hóa đơn {inv.Series} {inv.InvoiceNo} qua cổng – {inv.Total.ToMoney()} đ", Comment = $"{Me.FullName} – {MyVendor.Name}" });
        Db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = DocType.Invoice, DocumentId = inv.Id, UserId = po.BuyerId, CreatedAt = DateTime.Now,
            Action = match!.Ok ? "Đối chiếu 3 bên: khớp" : "Đối chiếu 3 bên: lệch", Comment = inv.MatchNote });
        await NotifyBuyersAsync(await Db.Users.Where(u => u.IsActive && u.Role == AppRoles.Accountant).Select(u => u.Id).ToListAsync(),
            $"Hóa đơn mới từ {MyVendor.Name}: {inv.InvoiceNo} – {(match.Ok ? "khớp 3 bên" : "LỆCH")}", inv.MatchNote, $"/Invoices/Details/{inv.Id}");
        TempData[match.Ok ? "Success" : "Error"] = match.Ok ? $"Đã gửi hóa đơn {inv.InvoiceNo} – khớp với đơn hàng và số lượng đã giao. Hạn thanh toán dự kiến {inv.DueDate:dd/MM/yyyy}."
            : $"Đã gửi hóa đơn {inv.InvoiceNo} nhưng chưa khớp: {inv.MatchNote}. Bộ phận kế toán sẽ liên hệ.";
        return Back();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Ship(int id, DateTime shipDate, DateTime expectedDate, string? reference, string? note)
    {
        var po = await LoadAsync(id);
        if (po == null) return NotFound();
        IActionResult Back(string? err = null) { if (err != null) TempData["Error"] = err; return RedirectToAction(nameof(Details), new { id }); }
        if (!CanTrade || po.Status is not (PoStatus.Issued or PoStatus.PartiallyReceived) || po.VendorRejectedAt != null) return Back("Đơn hàng không ở trạng thái giao hàng.");
        if (expectedDate < shipDate) return Back("Ngày dự kiến giao phải từ ngày xuất hàng.");
        Db.PoShipments.Add(new PoShipment { PurchaseOrderId = id, ShipDate = shipDate.Date, ExpectedDate = expectedDate.Date, Reference = reference?.Trim(), Note = note?.Trim(), VendorUserId = Me.Id });
        Db.Notifications.Add(new Notification
        {
            UserId = po.BuyerId, Kind = NotificationKind.Info, Url = $"/PurchaseOrders/Details/{po.Id}",
            Title = $"{MyVendor.Name} báo giao hàng {po.Number}: dự kiến {expectedDate:dd/MM/yyyy}", Message = string.Join(" · ", new[] { reference, note }.Where(s => !string.IsNullOrWhiteSpace(s)))
        });
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã gửi thông báo giao hàng.";
        return Back();
    }
}
