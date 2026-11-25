using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace EProcurement.Tests;

public class PackageProgressTests
{
    private static PackageTier Tier() => new() { Name = "T", MaxValue = 0, PlanDays = 5, ApprovalDays = 3, SelectionDays = 20, ContractDays = 7, ExecutionDays = 60, PaymentDays = 30 };
    private static readonly DateTime D0 = new(2026, 1, 1);

    [Fact]
    public void TierFor_uses_first_tier_whose_max_value_covers_the_package()
    {
        var s = new PackageTierSettings();
        Assert.Equal("Gói nhỏ", s.TierFor(50_000_000).Name);
        Assert.Equal("Gói nhỏ", s.TierFor(100_000_000).Name);   // biên: bằng hạn mức vẫn thuộc hạn mức đó
        Assert.Equal("Gói vừa", s.TierFor(100_000_001).Name);
        Assert.Equal("Gói lớn", s.TierFor(4_999_999_999).Name);
        Assert.Equal("Gói rất lớn", s.TierFor(900_000_000_000).Name);
        Assert.Equal("Gói nhỏ", s.TierFor(0).Name);
    }

    [Fact]
    public void Validate_requires_ascending_values_unlimited_last_names_and_days()
    {
        Assert.Empty(PackageTierSettings.Validate(PackageTierSettings.Defaults()));
        var t = PackageTierSettings.Defaults();
        t[1].MaxValue = 50_000_000;                                   // không tăng dần
        Assert.NotEmpty(PackageTierSettings.Validate(t));
        t = PackageTierSettings.Defaults(); t[3].MaxValue = 9_000_000_000;   // hạn mức cuối có giới hạn
        Assert.NotEmpty(PackageTierSettings.Validate(t));
        t = PackageTierSettings.Defaults(); t[1].MaxValue = 0;        // không giới hạn ở giữa
        Assert.NotEmpty(PackageTierSettings.Validate(t));
        t = PackageTierSettings.Defaults(); t[0].Name = " ";
        Assert.NotEmpty(PackageTierSettings.Validate(t));
        t = PackageTierSettings.Defaults(); t[0].ExecutionDays = 5000;
        Assert.NotEmpty(PackageTierSettings.Validate(t));
        Assert.NotEmpty(PackageTierSettings.Validate(new List<PackageTier>()));
    }

    [Fact]
    public void New_package_is_at_stage_one_and_due_date_follows_tier()
    {
        var f = new PackageFacts(D0, false, null, null, null, null, null, null);
        var r = PackageProgress.Compute(f, Tier(), D0.AddDays(2));
        Assert.Equal(0, r.CurrentIndex); Assert.Equal(ProgressState.OnTrack, r.State);
        Assert.Equal(D0.AddDays(5), r.Current!.Due); Assert.Equal(2, r.Current.DaysUsed);
        // các giai đoạn sau có hạn dự kiến nối tiếp
        Assert.Equal(D0.AddDays(5 + 3), r.Stages[1].Due); Assert.Equal(D0.AddDays(5 + 3 + 20), r.Stages[2].Due);
        Assert.Equal(D0.AddDays(125), r.FinalDue);
        Assert.All(r.Stages.Skip(1), s => Assert.Equal(StageStatus.Pending, s.Status));
    }

    [Fact]
    public void Due_soon_and_overdue_are_detected_on_the_current_stage()
    {
        var f = new PackageFacts(D0, false, D0.AddDays(4), D0.AddDays(6), null, null, null, null);   // đang ở giai đoạn 3 (lựa chọn NCC, hạn = D0+6+20)
        var soon = PackageProgress.Compute(f, Tier(), D0.AddDays(6 + 20 - 3));
        Assert.Equal(2, soon.CurrentIndex); Assert.Equal(ProgressState.DueSoon, soon.State);
        var late = PackageProgress.Compute(f, Tier(), D0.AddDays(6 + 20 + 9).AddHours(1));
        Assert.Equal(ProgressState.Overdue, late.State); Assert.Equal(10, late.Current!.DaysLate);      // làm tròn lên
        var fine = PackageProgress.Compute(f, Tier(), D0.AddDays(10));
        Assert.Equal(ProgressState.OnTrack, fine.State);
    }

    [Fact]
    public void Completed_stages_record_dates_lateness_and_next_stage_starts_when_previous_done()
    {
        // giai đoạn 1 hạn 5 ngày nhưng nộp ở ngày 8 → trễ 3 ngày; duyệt xong ngày 10 (hạn = 8 + 3 = 11)
        var f = new PackageFacts(D0, false, D0.AddDays(8), D0.AddDays(10), null, null, null, null);
        var r = PackageProgress.Compute(f, Tier(), D0.AddDays(12));
        Assert.Equal(3, r.Stages[0].DaysLate); Assert.True(r.Stages[0].Late);
        Assert.Equal(0, r.Stages[1].DaysLate); Assert.Equal(D0.AddDays(11), r.Stages[1].Due); Assert.Equal(2, r.Stages[1].DaysUsed);
        Assert.Equal(D0.AddDays(10), r.Stages[2].Start);
        Assert.Equal(D0.AddDays(30), r.Stages[2].Due);
        Assert.Equal(2, r.DoneCount);
    }

    [Fact]
    public void Later_milestone_implies_earlier_stages_done_for_legacy_data()
    {
        var f = new PackageFacts(D0, false, null, null, null, D0.AddDays(40), null, null);   // chỉ biết ngày ký hợp đồng
        var r = PackageProgress.Compute(f, Tier(), D0.AddDays(45));
        Assert.Equal(4, r.CurrentIndex);                          // đang thực hiện
        Assert.All(r.Stages.Take(4), s => Assert.Equal(StageStatus.Done, s.Status));
        Assert.Equal(D0.AddDays(40), r.Stages[3].CompletedAt);
    }

    [Fact]
    public void Fully_done_is_completed_cancelled_has_no_current_stage()
    {
        var f = new PackageFacts(D0, false, D0.AddDays(2), D0.AddDays(4), D0.AddDays(15), D0.AddDays(20), D0.AddDays(70), D0.AddDays(90));
        var r = PackageProgress.Compute(f, Tier(), D0.AddDays(200));
        Assert.Equal(ProgressState.Completed, r.State); Assert.Equal(-1, r.CurrentIndex); Assert.Equal(6, r.DoneCount); Assert.Equal(90, r.ElapsedDays);
        var c = PackageProgress.Compute(new PackageFacts(D0, true, D0.AddDays(2), null, null, null, null, null), Tier(), D0.AddDays(400));
        Assert.Equal(ProgressState.Cancelled, c.State); Assert.Equal(-1, c.CurrentIndex);
    }

    [Fact]
    public void Completion_before_start_is_clamped_not_negative()
    {
        var f = new PackageFacts(D0, false, D0.AddDays(-3), null, null, null, null, null);   // mốc lệch trước ngày tạo
        var r = PackageProgress.Compute(f, Tier(), D0.AddDays(1));
        Assert.Equal(0, r.Stages[0].DaysUsed); Assert.Equal(D0, r.Stages[0].CompletedAt);
    }

    // ----- lĩnh vực & hạn mức theo loại mua sắm -----
    [Fact]
    public void TierFor_prefers_the_field_specific_set_and_falls_back_to_default()
    {
        var s = new PackageTierSettings();                                   // mặc định có thêm bộ "Xây lắp"
        Assert.Equal("Xây lắp nhỏ", s.TierFor(500_000_000, "Xây lắp").Name);
        Assert.Equal("Xây lắp vừa", s.TierFor(5_000_000_000, "xây lắp").Name);   // không phân biệt hoa thường
        Assert.Equal("Xây lắp lớn", s.TierFor(50_000_000_000, "Xây lắp").Name);
        Assert.Equal("Gói vừa", s.TierFor(500_000_000, "Hàng hóa").Name);        // lĩnh vực chưa có bộ riêng → mặc định
        Assert.Equal("Gói vừa", s.TierFor(500_000_000, null).Name);
        Assert.Equal("Gói vừa", s.TierFor(500_000_000).Name);
        Assert.Equal(new[] { "Xây lắp" }, s.Fields);
        Assert.True(s.TierFor(500_000_000, "Xây lắp").ExecutionDays > s.TierFor(500_000_000, "Hàng hóa").ExecutionDays);
    }

    [Fact]
    public void Validate_checks_each_field_set_separately_and_requires_a_default_set()
    {
        Assert.Empty(PackageTierSettings.Validate(PackageTierSettings.Defaults()));
        var t = PackageTierSettings.Defaults();
        t.RemoveAll(x => x.Field == null);                                    // thiếu bộ mặc định
        Assert.Contains(PackageTierSettings.Validate(t), e => e.Contains("mặc định") || e.Contains("default"));
        t = PackageTierSettings.Defaults();
        t.First(x => x.Field == "Xây lắp" && x.MaxValue == 0).MaxValue = 99_000_000_000;   // bộ Xây lắp không có hạn mức không giới hạn
        var errs = PackageTierSettings.Validate(t);
        Assert.NotEmpty(errs); Assert.All(errs, e => Assert.StartsWith("[Xây lắp]", e));
        t = PackageTierSettings.Defaults();
        t.First(x => x.Field == "Xây lắp" && x.MaxValue == 10_000_000_000).MaxValue = 500_000_000;   // giảm dần trong bộ Xây lắp
        Assert.NotEmpty(PackageTierSettings.Validate(t));
    }

    [Theory]
    [InlineData("Xây dựng nhà kho lạnh khu B", "Xây lắp")]
    [InlineData("Thi công nhà xưởng", "Xây lắp")]
    [InlineData("Tư vấn thiết kế hệ thống", "Dịch vụ tư vấn")]
    [InlineData("Gia hạn bảo trì phần mềm ERP", "Bảo trì, sửa chữa")]
    [InlineData("Mua laptop và màn hình", "Thiết bị & phần mềm CNTT")]
    [InlineData("Dịch vụ vệ sinh văn phòng", "Dịch vụ phi tư vấn")]
    [InlineData("Giấy A4, bút bi", "Hàng hóa")]
    [InlineData("", "Hàng hóa")]
    public void SuggestField_reads_keywords(string text, string expected) => Assert.Equal(expected, Packages.SuggestField(text));

    [Fact]
    public async Task Backfill_assigns_a_field_only_to_packages_without_one()
    {
        var db = TestDb.Create();
        var (buyer, _, _, _, _, _) = TestDb.Seed(db);
        db.ProcurementPackages.AddRange(
            new ProcurementPackage { Number = "G1", Title = "Xây dựng kho", BuyerId = buyer.Id },
            new ProcurementPackage { Number = "G2", Title = "Bút bi", BuyerId = buyer.Id },
            new ProcurementPackage { Number = "G3", Title = "Xây dựng kho", BuyerId = buyer.Id, Field = "Hàng hóa" });
        db.SaveChanges();
        await Packages.BackfillFieldsAsync(db);
        Assert.Equal("Xây lắp", db.ProcurementPackages.Single(p => p.Number == "G1").Field);
        Assert.Equal("Hàng hóa", db.ProcurementPackages.Single(p => p.Number == "G2").Field);
        Assert.Equal("Hàng hóa", db.ProcurementPackages.Single(p => p.Number == "G3").Field);     // đã có thì không đổi
        await Packages.BackfillFieldsAsync(db);                                                  // an toàn khi gọi lại
    }

    // ----- hạn mức theo hình thức mua sắm -----
    [Fact]
    public void TierFor_picks_the_most_specific_set_field_method_then_field_then_method_then_default()
    {
        var s = new PackageTierSettings();
        // chỉ có hình thức
        Assert.Equal("Chào hàng nhỏ", s.TierFor(300_000_000, "Hàng hóa", ProcurementMethod.CompetitiveQuotation).Name);
        Assert.Equal("Chào hàng lớn", s.TierFor(900_000_000, null, ProcurementMethod.CompetitiveQuotation).Name);
        Assert.Equal("Mua trực tiếp", s.TierFor(50_000_000, null, ProcurementMethod.DirectPurchase).Name);
        Assert.Equal("Chỉ định thầu nhỏ", s.TierFor(100_000_000, null, ProcurementMethod.DirectAward).Name);
        Assert.Equal("Đấu thầu lớn", s.TierFor(20_000_000_000, "Hàng hóa", ProcurementMethod.OpenTender).Name);
        // lĩnh vực có bộ riêng thắng bộ theo hình thức
        Assert.Equal("Xây lắp nhỏ", s.TierFor(500_000_000, "Xây lắp", ProcurementMethod.OpenTender).Name);
        // lĩnh vực + hình thức cùng khớp thắng tất cả
        s.Tiers.Add(new PackageTier { Field = "Xây lắp", Method = ProcurementMethod.OpenTender, Name = "Xây lắp đấu thầu", MaxValue = 0, PlanDays = 20, ApprovalDays = 10, SelectionDays = 90, ContractDays = 20, ExecutionDays = 300, PaymentDays = 60 });
        Assert.Equal("Xây lắp đấu thầu", s.TierFor(500_000_000, "Xây lắp", ProcurementMethod.OpenTender).Name);
        Assert.Equal("Xây lắp nhỏ", s.TierFor(500_000_000, "Xây lắp", ProcurementMethod.DirectAward).Name);
        // không biết hình thức → bộ mặc định
        Assert.Equal("Gói vừa", s.TierFor(500_000_000).Name);
        // hình thức không có bộ riêng → mặc định
        s.Tiers.RemoveAll(t => t.Method == ProcurementMethod.DirectPurchase);
        Assert.Equal("Gói nhỏ", s.TierFor(50_000_000, null, ProcurementMethod.DirectPurchase).Name);
    }

    [Fact]
    public void Validate_handles_method_sets_and_scope_text()
    {
        Assert.Empty(PackageTierSettings.Validate(PackageTierSettings.Defaults()));
        var t = PackageTierSettings.Defaults();
        t.First(x => x.Method == ProcurementMethod.OpenTender && x.MaxValue == 0).MaxValue = 99_000_000_000;
        var errs = PackageTierSettings.Validate(t);
        Assert.NotEmpty(errs); Assert.All(errs, e => Assert.StartsWith("[" + ProcurementMethod.OpenTender.GetDisplayName() + "]", e));
        t = PackageTierSettings.Defaults(); t.RemoveAll(x => x.Method == null && x.Field == null);   // thiếu bộ mặc định thật sự
        Assert.NotEmpty(PackageTierSettings.Validate(t));
        Assert.Equal("", PackageTierSettings.Defaults()[0].Scope);
        Assert.Equal("Xây lắp · " + ProcurementMethod.OpenTender.GetDisplayName(), new PackageTier { Field = "Xây lắp", Method = ProcurementMethod.OpenTender }.Scope);
    }

    [Fact]
    public void Tier_settings_round_trip_through_json_with_method()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new PackageTierSettings());
        var back = System.Text.Json.JsonSerializer.Deserialize<PackageTierSettings>(json)!;
        Assert.Equal(PackageTierSettings.Defaults().Count, back.Tiers.Count);
        Assert.Contains(back.Tiers, x => x.Method == ProcurementMethod.DirectAward);
        // cấu hình cũ (chưa có trường Method) vẫn đọc được: Method = null
        var old = System.Text.Json.JsonSerializer.Deserialize<PackageTierSettings>("{\"Tiers\":[{\"Name\":\"A\",\"MaxValue\":0,\"PlanDays\":1,\"ApprovalDays\":1,\"SelectionDays\":1,\"ContractDays\":1,\"ExecutionDays\":1,\"PaymentDays\":1}]}")!;
        Assert.Null(old.Tiers[0].Method); Assert.Equal("A", old.TierFor(5, null, ProcurementMethod.OpenTender).Name);
    }

    // ----- dịch vụ nạp dữ liệu trên bộ dữ liệu mẫu xây dựng -----
    [Fact]
    public async Task Service_places_sample_packages_in_the_expected_stage()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        db.Users.AddRange(
            new AppUser { Username = "admin", FullName = "QT", Role = AppRoles.Admin, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "director", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" },
            new AppUser { Username = "requester", FullName = "NV", Role = AppRoles.Requester, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "ketoan.tt", FullName = "KT", Role = AppRoles.Accountant, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "approver", FullName = "A", Role = AppRoles.Approver, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "approver2", FullName = "B", Role = AppRoles.Approver, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "buyer2", FullName = "C", Role = AppRoles.Buyer, Department = "MH", PasswordHash = "x" });
        db.Vendors.AddRange(new Vendor { Code = "N2", Name = "N2", TaxCode = "2", Status = VendorStatus.Approved }, new Vendor { Code = "N3", Name = "N3", TaxCode = "3", Status = VendorStatus.Approved });
        db.SaveChanges();
        await Web.Data.SampleConstruction.LoadAsync(db);
        var svc = new PackageProgressService(db, new SettingsStore(db, new MemoryCache(new MemoryCacheOptions())));
        var res = await svc.ComputeAsync();
        var a = res.Single(x => x.Package.Number == Web.Data.SampleConstruction.PackageA).Progress;
        var b = res.Single(x => x.Package.Number == Web.Data.SampleConstruction.PackageB).Progress;
        Assert.Equal(0, a.CurrentIndex);                                   // gói nháp chưa vào KHLCNT: đang lập gói
        Assert.Equal(4, b.CurrentIndex);                                   // đã ký hợp đồng, hợp đồng còn hiệu lực: đang thực hiện
        Assert.All(b.Stages.Take(4), s => Assert.Equal(StageStatus.Done, s.Status));
        Assert.NotNull(b.Stages[2].CompletedAt);                           // ngày chọn được nhà thầu lấy từ nhật ký phê duyệt kết quả
        Assert.Equal("Xây lắp nhỏ", b.Tier.Name);                         // ~824 triệu, lĩnh vực Xây lắp → bộ hạn mức riêng ≤ 1 tỷ
    }
    // ----- cảnh báo gói quá hạn -----
    private static async Task<(Web.Data.EProcDbContext Db, SettingsStore Store)> LoadedWithStoreAsync()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        db.Users.AddRange(
            new AppUser { Username = "admin", FullName = "QT", Role = AppRoles.Admin, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "director", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" },
            new AppUser { Username = "requester", FullName = "NV", Role = AppRoles.Requester, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "ketoan.tt", FullName = "KT", Role = AppRoles.Accountant, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "approver", FullName = "A", Role = AppRoles.Approver, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "approver2", FullName = "B", Role = AppRoles.Approver, Department = "KT", PasswordHash = "x" },
            new AppUser { Username = "buyer2", FullName = "C", Role = AppRoles.Buyer, Department = "MH", PasswordHash = "x" });
        db.Vendors.AddRange(new Vendor { Code = "N2", Name = "N2", TaxCode = "2", Status = VendorStatus.Approved }, new Vendor { Code = "N3", Name = "N3", TaxCode = "3", Status = VendorStatus.Approved });
        db.SaveChanges();
        await Web.Data.SampleConstruction.LoadAsync(db);
        return (db, new SettingsStore(db, new MemoryCache(new MemoryCacheOptions())));
    }

    [Fact]
    public async Task Overdue_packages_notify_owner_and_directors_once_per_week()
    {
        var (db, store) = await LoadedWithStoreAsync();
        var svc = new NotificationService(db, Microsoft.Extensions.Options.Options.Create(new NotificationSettings()), TestDb.Settings(), store);
        var created = await svc.RunRemindersAsync();
        Assert.True(created > 0);
        var pkgB = db.ProcurementPackages.Single(p => p.Number == Web.Data.SampleConstruction.PackageA);   // gói nháp lập từ 18 ngày: quá hạn giai đoạn lập gói
        var notes = db.Notifications.Where(n => n.DocumentType == DocType.Package && n.DocumentId == pkgB.Id).ToList();
        Assert.NotEmpty(notes);
        Assert.Contains(notes, n => n.Title.Contains("quá hạn"));
        var recipients = notes.Select(n => n.UserId).Distinct().ToList();
        Assert.Contains(pkgB.BuyerId, recipients);
        Assert.Contains(db.Users.Single(u => u.Username == "director").Id, recipients);
        // chạy lại ngay: không tạo thêm thông báo trùng
        var before = db.Notifications.Count();
        await svc.RunRemindersAsync();
        Assert.Equal(before, db.Notifications.Count());
    }

    [Fact]
    public async Task Alerts_can_be_turned_off()
    {
        var (db, store) = await LoadedWithStoreAsync();
        var s = new AlertSettings { Rules = AlertSettings.Defs.Select(d => new AlertRule { Key = d.Key, Enabled = !d.Key.StartsWith("package-"), Value = d.Default }).ToList() };
        await store.SaveAsync(SettingsStore.Alerts, s);
        var svc = new NotificationService(db, Microsoft.Extensions.Options.Options.Create(new NotificationSettings()), TestDb.Settings(), store);
        await svc.RunRemindersAsync();
        Assert.Empty(db.Notifications.Where(n => n.DocumentType == DocType.Package));
    }
}
