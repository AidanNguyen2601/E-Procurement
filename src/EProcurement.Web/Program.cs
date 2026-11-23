using Microsoft.AspNetCore.DataProtection;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ---------- Database (SQL Server) ----------
builder.Services.AddDbContext<EProcDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("EProcConnection")));

// ---------- Authentication: cookie + role ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
    })
    .AddCookie(EProcurement.Web.Infrastructure.PortalAuth.Scheme, o =>
    {
        o.Cookie.Name = ".EProc.Vendor";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.LoginPath = "/PortalAccount/Login";
        o.LogoutPath = "/PortalAccount/Logout";
        o.AccessDeniedPath = "/PortalAccount/Login";
        o.ExpireTimeSpan = TimeSpan.FromHours(4);
        o.SlidingExpiration = true;
    });

// Mặc định mọi trang đều yêu cầu đăng nhập (trừ [AllowAnonymous])
// Cổng NCC (controller Portal*) dùng cookie riêng – không áp yêu cầu đăng nhập nội bộ
builder.Services.AddControllersWithViews(o =>
{
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.Conventions.Add(new EProcurement.Web.Infrastructure.InternalAuthConvention(new AuthorizeFilter(policy)));
});

// Lưu khóa mã hóa cookie ra thư mục (volume Docker) để restart container không bị đăng xuất
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
        .SetApplicationName("EProcurement");
}

// ---------- Services ----------
builder.Services.Configure<EProcurement.Web.Infrastructure.ProcurementSettings>(builder.Configuration.GetSection("Procurement"));
builder.Services.AddScoped<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddScoped<IPasswordHasher<VendorUser>, PasswordHasher<VendorUser>>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.Reporting.StandardReports>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.CatalogService>();
builder.Services.Configure<EProcurement.Web.Infrastructure.NotificationSettings>(builder.Configuration.GetSection("Notifications"));
builder.Services.AddScoped<EProcurement.Web.Infrastructure.NotificationService>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.BudgetService>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.ApprovalMatrix>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.SealingService>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.FinanceService>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.SignatureService>();
builder.Services.AddScoped<EProcurement.Web.Infrastructure.UiThemeService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, EProcurement.Web.Infrastructure.DelegationClaimsTransformation>();
if (builder.Configuration.GetValue("Notifications:BackgroundEnabled", true)) builder.Services.AddHostedService<EProcurement.Web.Infrastructure.NotificationWorker>();

// REST API: tối đa 120 request / phút / API key
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("api", ctx => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
        ctx.Request.Headers["X-Api-Key"].ToString(),
        _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Tạo database + dữ liệu mẫu ở lần chạy đầu.
// Thử lại nhiều lần vì khi chạy bằng Docker, SQL Server có thể khởi động chậm hơn web app.
if (app.Configuration.GetValue("Database:SeedOnStartup", true))
{
    var retries = app.Configuration.GetValue("Database:StartupRetries", 12);
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await DbSeeder.SeedAsync(app.Services);
            break;
        }
        catch (Exception ex) when (attempt < retries)
        {
            app.Logger.LogWarning("Chưa kết nối được database (lần {Attempt}/{Max}): {Message}. Thử lại sau 5 giây...",
                attempt, retries, ex.Message);
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Không khởi tạo được database. Kiểm tra ConnectionStrings:EProcConnection");
            throw;
        }
    }
}

app.Run();
