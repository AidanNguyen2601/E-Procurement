using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Controllers;

/// <summary>Quản trị người dùng & phân quyền – chỉ Admin</summary>
[Authorize(Roles = AppRoles.Admin)]
public class UsersController : Controller
{
    private readonly EProcDbContext _db;
    private readonly IPasswordHasher<AppUser> _hasher;

    public UsersController(EProcDbContext db, IPasswordHasher<AppUser> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public async Task<IActionResult> Index()
    {
        return View(await _db.Users.OrderBy(u => u.Department).ThenBy(u => u.Username).ToListAsync());
    }

    public IActionResult Create()
    {
        LoadRoles();
        return View("Form", new UserFormViewModel());
    }

    public async Task<IActionResult> Edit(int id)
    {
        var u = await _db.Users.FindAsync(id);
        if (u == null) return NotFound();
        LoadRoles();
        return View("Form", new UserFormViewModel
        {
            Id = u.Id, Username = u.Username, FullName = u.FullName, Email = u.Email, Department = u.Department, Role = u.Role, IsActive = u.IsActive
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(UserFormViewModel vm)
    {
        if (!AppRoles.All.Contains(vm.Role)) ModelState.AddModelError(nameof(vm.Role), I18n.T("Vai trò không hợp lệ"));
        if (await _db.Users.AnyAsync(u => u.Username == vm.Username && u.Id != vm.Id))
            ModelState.AddModelError(nameof(vm.Username), I18n.T("Tên đăng nhập đã tồn tại"));
        if (vm.Id == 0 && string.IsNullOrWhiteSpace(vm.Password))
            ModelState.AddModelError(nameof(vm.Password), I18n.T("Vui lòng nhập mật khẩu"));
        if (vm.Id == User.GetUserId() && (!vm.IsActive || vm.Role != AppRoles.Admin))
            ModelState.AddModelError(string.Empty, I18n.T("Không thể tự khóa hoặc hạ quyền tài khoản đang đăng nhập."));
        var oldDept = vm.Id == 0 ? null : await _db.Users.Where(u => u.Id == vm.Id).Select(u => u.Department).FirstOrDefaultAsync();
        if (!await HttpContext.RequestServices.GetRequiredService<CatalogService>().IsValidAsync(CatalogType.Department, vm.Department, oldDept))
            ModelState.AddModelError(nameof(vm.Department), I18n.T("Chọn phòng ban trong danh mục"));
        if (!ModelState.IsValid)
        {
            LoadRoles();
            return View("Form", vm);
        }

        AppUser? user;
        if (vm.Id == 0) { user = new AppUser(); _db.Users.Add(user); }
        else
        {
            user = await _db.Users.FindAsync(vm.Id);
            if (user == null) return NotFound();
        }
        user.Username = vm.Username;
        user.FullName = vm.FullName;
        user.Email = vm.Email;
        user.Department = vm.Department.Trim();
        user.Role = vm.Role;
        user.IsActive = vm.IsActive;
        if (!string.IsNullOrWhiteSpace(vm.Password)) { user.PasswordHash = _hasher.HashPassword(user, vm.Password); user.FailedLogins = 0; user.LockoutEnd = null; }
        await _db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu tài khoản {0}.", user.Username);
        return RedirectToAction(nameof(Index));
    }

    private void LoadRoles()
    {
        ViewBag.Roles = new SelectList(AppRoles.All.Select(r => new { Value = r, Text = AppRoles.DisplayName(r) }), "Value", "Text");
        ViewBag.Departments = _db.Users.Select(u => u.Department).Distinct().OrderBy(d => d).ToList();
    }
}
