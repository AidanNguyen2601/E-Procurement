using System.Security.Claims;
using EProcurement.Web.Controllers;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

public class SettingsTests
{
    [Theory]
    [InlineData("{MA}-{YYYY}-{STT}", "PR", "PR-2026-")]
    [InlineData("{MA}/{YYYY}/{MM}/{STT}", "PO", "PO/2026/11/")]
    [InlineData("{MA}{YY}{MM}-{STT}", "HD", "HD2611-")]
    [InlineData("{MA}-{STT}", "GM", "GM-")]
    public void Numbering_head_renders_tokens(string pattern, string code, string head) =>
        Assert.Equal(head, NumberingSettings.Head(pattern, code, new DateTime(2026, 11, 24)));

    [Theory]
    [InlineData("{MA}-{YYYY}-{STT}", "PR", 4, true)]
    [InlineData("{MA}-{YYYY}", "PR", 4, false)]          // thiếu STT
    [InlineData("{STT}-{MA}", "PR", 4, false)]           // STT không ở cuối
    [InlineData("{MA}-{XYZ}-{STT}", "PR", 4, false)]     // mã lạ
    [InlineData("{MA} {STT}", "PR", 4, false)]           // ký tự không cho phép
    [InlineData("{MA}-{YYYY}-{STT}", "P R", 4, false)]   // mã viết tắt sai
    [InlineData("{MA}-{YYYY}-{STT}", "PR", 9, false)]    // quá nhiều chữ số
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWX-{STT}", "PR", 8, false)] // vượt 30 ký tự
    public void Numbering_validation(string pattern, string code, int digits, bool ok) =>
        Assert.Equal(ok, NumberingSettings.Validate(pattern, code, digits) == null);

    [Fact]
    public async Task DocNumbers_default_keeps_legacy_format()
    {
        using var db = TestDb.Create();
        db.ProcurementPackages.Add(new ProcurementPackage { Number = $"GM-{DateTime.Today:yyyy}-0001", Title = "x", BuyerId = 1 });
        await db.SaveChangesAsync();
        Assert.Equal($"GM-{DateTime.Today:yyyy}-0002", await EProcurement.Web.Data.DocNumbers.NextAsync(db.ProcurementPackages.Select(p => p.Number), "GM"));
    }

    [Fact]
    public void Alerts_default_from_legacy_and_clamped()
    {
        var a = AlertSettings.FromLegacy(45, 3, 85);
        Assert.Equal(45, a.Val("contract-expiry"));
        Assert.Equal(3, a.Val("stale-approval"));
        Assert.Equal(85, a.Val("budget"));
        Assert.Equal(1, a.Val("rfq-deadline"));   // không cấu hình → mặc định
        Assert.True(a.On("po-late"));
        a.Rules.Add(new AlertRule { Key = "tender-closing", Enabled = false, Value = 999 });
        Assert.False(a.On("tender-closing"));
        Assert.Equal(30, a.Val("tender-closing")); // kẹp theo Max
    }

    [Fact]
    public void Supplier_score_uses_weights()
    {
        var s = new SupplierCriteriaSettings();
        Assert.Equal(3.5m, s.Score(5, 4, 3, 2));    // trọng số đều = trung bình
        s.Items.Single(i => i.Key == "Quality").Weight = 70;
        s.Items.Single(i => i.Key == "Delivery").Weight = 30;
        s.Items.Single(i => i.Key == "Price").Weight = 0;
        s.Items.Single(i => i.Key == "Service").Weight = 0;
        Assert.Equal(4.7m, s.Score(5, 4, 1, 1));
    }

    [Fact]
    public void Permission_denies_only_listed_roles_never_admin()
    {
        var p = new PermissionSettings { Denied = { ["Reports"] = new() { AppRoles.Accountant, AppRoles.Admin } } };
        Assert.True(p.IsDenied("Reports", AppRoles.Accountant));
        Assert.False(p.IsDenied("Reports", AppRoles.Admin));
        Assert.False(p.IsDenied("Reports", AppRoles.Buyer));
        Assert.False(p.IsDenied("Vendors", AppRoles.Accountant));
    }

    [Fact]
    public void Templates_render_encodes_values_in_html()
    {
        var v = new Dictionary<string, string> { ["{TieuDe}"] = "<script>x</script>", ["{NguoiNhan}"] = "An" };
        Assert.Equal("Chào An: &lt;script&gt;x&lt;/script&gt;", NotifyTemplates.Render("Chào {NguoiNhan}: {TieuDe}", v, true));
        Assert.Equal("[E] <script>x</script>", NotifyTemplates.Render("[E] {TieuDe}", v, false));
        var t = new NotifyTemplates { Channels = { new NotifyChannel { Key = "PR", InApp = true, Email = false } } };
        Assert.False(t.Channel(DocType.PR).Email);
        Assert.True(t.Channel(DocType.PO).Email);          // chưa cấu hình → bật
        Assert.Equal("other", t.Channel(null).Key);
    }

    [Fact]
    public void Category_tree_flatten_and_subtree()
    {
        var all = new List<Category>
        {
            new() { Id = 1, Code = "IT", Name = "CNTT" }, new() { Id = 2, Code = "IT-HW", Name = "Phần cứng", ParentId = 1 },
            new() { Id = 3, Code = "IT-PC", Name = "Máy tính", ParentId = 2 }, new() { Id = 4, Code = "VPP", Name = "Văn phòng phẩm" }
        };
        var flat = CategoriesController.Flatten(all);
        Assert.Equal(new[] { 1, 2, 3, 4 }, flat.Select(n => n.Cat.Id));
        Assert.Equal(2, flat.Single(n => n.Cat.Id == 3).Depth);
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, CategoriesController.SubtreeIds(all, 1));
        Assert.DoesNotContain(CategoriesController.Options(all, null, CategoriesController.SubtreeIds(all, 2)), o => o.Value == "3");
    }

    private static ClaimsPrincipal As(int id, string role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role) }, "t"));

    [Fact]
    public async Task Package_access_open_until_members_assigned()
    {
        using var db = TestDb.Create();
        var (buyer, _, _, _, _, _) = TestDb.Seed(db);
        var other = new AppUser { Username = "b2", FullName = "B2", Role = AppRoles.Buyer, Department = "Phòng Mua hàng", PasswordHash = "x" };
        var viewer = new AppUser { Username = "b3", FullName = "B3", Role = AppRoles.Buyer, Department = "Phòng Mua hàng", PasswordHash = "x" };
        db.Users.AddRange(other, viewer);
        var pkg = new ProcurementPackage { Number = "GM-1", Title = "Gói", BuyerId = buyer.Id };
        db.ProcurementPackages.Add(pkg);
        await db.SaveChangesAsync();

        Assert.True(await PackageAccess.CanEditAsync(db, As(other.Id, AppRoles.Buyer), pkg.Id));    // chưa phân quyền
        db.PackageMembers.Add(new PackageMember { PackageId = pkg.Id, UserId = viewer.Id, Role = PackageRole.Viewer });
        await db.SaveChangesAsync();
        Assert.False(await PackageAccess.CanEditAsync(db, As(other.Id, AppRoles.Buyer), pkg.Id));   // không phải thành viên
        Assert.False(await PackageAccess.CanEditAsync(db, As(viewer.Id, AppRoles.Buyer), pkg.Id));  // chỉ xem
        Assert.True(await PackageAccess.CanEditAsync(db, As(buyer.Id, AppRoles.Buyer), pkg.Id));    // người lập gói
        Assert.True(await PackageAccess.CanEditAsync(db, As(999, AppRoles.Director), pkg.Id));
        Assert.False(await PackageAccess.CanEditAsync(db, As(999, AppRoles.Requester), pkg.Id));
    }

    [Fact]
    public async Task Criteria_seed_is_idempotent()
    {
        using var db = TestDb.Create();
        TestDb.Seed(db);
        await CriteriaSeed.EnsureAsync(db);
        var n = await db.CriteriaSets.CountAsync();
        Assert.True(n >= 3);
        Assert.Contains(await db.CriteriaSets.ToListAsync(), s => s.Kind == CriteriaKind.TenderQualification);
        await CriteriaSeed.EnsureAsync(db);
        Assert.Equal(n, await db.CriteriaSets.CountAsync());
        Assert.All(await db.CriteriaItems.Where(i => i.IsMandatory).ToListAsync(), i => Assert.Equal(0, i.MaxScore));
    }
}
