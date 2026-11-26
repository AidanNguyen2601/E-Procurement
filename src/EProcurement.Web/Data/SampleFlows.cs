using System.IO.Compression;
using System.Text;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Data;

/// <summary>
/// Dữ liệu mẫu bổ sung cho các luồng mở rộng: cơ cấu tổ chức theo khối, phân cấp nhóm hàng, tài khoản cổng NCC,
/// hồ sơ pháp lý NCC, sơ tuyển, ngân hàng giá, bảng giá tiêu chuẩn, kế hoạch năm, đàm phán / thẩm định LCNT,
/// hỏi đáp HSMT, thông báo giao hàng, quyền theo gói, biểu mẫu. Mỗi phần chỉ nạp khi chưa có (gọi lại an toàn);
/// gỡ cùng "Gỡ dữ liệu mẫu".
/// </summary>
public static class SampleFlows
{
    public const string Tag = "[Mẫu]";
    public const string VendorDomain = "@ncc-mau.vn";
    public const string Password = "Abc@123";
    private const string P = SampleDataSeeder.Prefix;

    public record Status(int Prequalifications, int PriceRecords, int PriceLists, int VendorAccounts, int Negotiations, int Appraisals, int Forms, int Plans);

    public static async Task<Status> StatusAsync(EProcDbContext db) => new(
        await db.Prequalifications.CountAsync(x => x.Number.StartsWith(P)),
        await db.PriceRecords.CountAsync(x => x.SourceNote != null && x.SourceNote.StartsWith(Tag)),
        await db.StandardPriceLists.CountAsync(x => x.Number.StartsWith(P)),
        await db.VendorUsers.CountAsync(x => x.Email.EndsWith(VendorDomain)),
        await db.TenderNegotiations.CountAsync(x => x.Tender!.Number.StartsWith(P)),
        await db.TenderAppraisals.CountAsync(x => x.Tender!.Number.StartsWith(P)),
        await db.FormTemplates.CountAsync(x => x.Code != null && x.Code.StartsWith(P)),
        await db.ProcurementPlans.CountAsync(x => x.Note != null && x.Note.StartsWith(Tag)));

    /// <summary>Nạp các phần còn thiếu. Trả về mô tả những gì đã nạp.</summary>
    public static async Task<List<string>> LoadAsync(EProcDbContext db)
    {
        var done = new List<string>();
        var vendorHasher = new PasswordHasher<VendorUser>();
        var u = await db.Users.ToDictionaryAsync(x => x.Username, StringComparer.OrdinalIgnoreCase);
        AppUser? U(string name) => u.GetValueOrDefault(name);
        var admin = U("admin") ?? await db.Users.FirstAsync(x => x.Role == AppRoles.Admin);
        var buyer = U("buyer") ?? admin; var buyer2 = U("buyer2") ?? buyer; var director = U("director") ?? admin;
        var today = DateTime.Today; var now = DateTime.Now;
        var rnd = new Random(2026);
        var logs = new List<ApprovalHistory>();
        void Log(DocType t, int id, AppUser by, string action, DateTime at, string? comment = null) =>
            logs.Add(new ApprovalHistory { DocumentType = t, DocumentId = id, UserId = by.Id, Action = action, Comment = comment, CreatedAt = at });

        var vendors = await db.Vendors.Include(v => v.Categories).Where(v => v.Status == VendorStatus.Approved).OrderBy(v => v.Code).ToListAsync();
        var items = await db.Items.Include(i => i.Category).Where(i => i.IsActive).OrderBy(i => i.Code).ToListAsync();
        var cats = await db.Categories.ToListAsync();
        if (vendors.Count < 3 || items.Count < 5) throw new InvalidOperationException("Cần có NCC và hàng hóa (nạp dữ liệu mẫu chính trước).");

        // ------------------------------------------------------------------ 1. Cơ cấu tổ chức theo khối
        if (!await db.OrgUnits.AnyAsync(o => o.Code.StartsWith(P)))
        {
            var units = await db.OrgUnits.ToListAsync();
            var root = units.FirstOrDefault(x => x.ParentId == null);
            if (root != null)
            {
                var plan = new (string Code, string Name, string[] Depts)[]
                {
                    ($"{P}KKD", "Khối Kinh doanh", new[] { "Phòng Kinh doanh", "Phòng Kinh doanh miền Bắc" }),
                    ($"{P}KVH", "Khối Vận hành", new[] { "Phòng Kỹ thuật", "Phòng CNTT", "Phòng Mua hàng" }),
                    ($"{P}KHT", "Khối Hỗ trợ", new[] { "Phòng Hành chính", "Phòng Nhân sự", "Phòng Kế toán" }),
                };
                var order = 100;
                foreach (var (code, name, depts) in plan)
                {
                    if (units.Any(x => x.Name == name)) continue;
                    var div = new OrgUnit { Code = code, Name = name, Type = OrgUnitType.Division, ParentId = root.Id, SortOrder = order += 10, Note = $"{Tag} khối gom các phòng ban" };
                    db.OrgUnits.Add(div);
                    await db.SaveChangesAsync();
                    foreach (var d in units.Where(x => depts.Contains(x.Name) && x.ParentId == root.Id)) d.ParentId = div.Id;
                }
                var it = units.FirstOrDefault(x => x.Name == "Phòng CNTT");
                if (it != null && !units.Any(x => x.Name == "Tổ Hạ tầng – Mạng"))
                    db.OrgUnits.Add(new OrgUnit { Code = $"{P}HTM", Name = "Tổ Hạ tầng – Mạng", Type = OrgUnitType.Team, ParentId = it.Id, Note = $"{Tag} tổ trực thuộc phòng CNTT" });
                await db.SaveChangesAsync();
                await OrgUnits.SyncCatalogAsync(db);
                done.Add("cơ cấu tổ chức 3 khối + 1 tổ");
            }
        }

        // ------------------------------------------------------------------ 2. Phân cấp nhóm hàng
        if (!cats.Any(c => c.Code.StartsWith(P)))
        {
            var groups = new (string Code, string Name, Func<Category, bool> Match)[]
            {
                ($"{P}HH", "Hàng hóa", c => !c.Name.Contains("Dịch vụ", StringComparison.OrdinalIgnoreCase) && !c.Name.Contains("Phần mềm", StringComparison.OrdinalIgnoreCase)),
                ($"{P}DV", "Dịch vụ & phần mềm", c => c.Name.Contains("Dịch vụ", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("Phần mềm", StringComparison.OrdinalIgnoreCase)),
            };
            var roots = cats.Where(c => c.ParentId == null).ToList();
            var i = 0;
            foreach (var (code, name, match) in groups)
            {
                var g = new Category { Code = code, Name = name, SortOrder = ++i, Description = $"{Tag} nhóm cấp 1" };
                db.Categories.Add(g);
                await db.SaveChangesAsync();
                foreach (var c in roots.Where(match)) c.ParentId = g.Id;
            }
            await db.SaveChangesAsync();
            done.Add("phân cấp nhóm hàng 2 cấp");
        }

        // ------------------------------------------------------------------ 3. Tài khoản cổng NCC + hồ sơ pháp lý
        var vUsers = await db.VendorUsers.Where(x => x.Email.EndsWith(VendorDomain)).ToListAsync();
        if (vUsers.Count == 0)
        {
            var people = new[] { ("Nguyễn Minh Tuấn", "Giám đốc kinh doanh"), ("Lê Hoài Phương", "Trưởng phòng dự án"), ("Trần Quốc Bảo", "Chuyên viên đấu thầu"), ("Phạm Thu Trang", "Kế toán trưởng") };
            for (var k = 0; k < Math.Min(4, vendors.Count); k++)
            {
                var vu = new VendorUser { VendorId = vendors[k].Id, Email = $"ncc{k + 1}{VendorDomain}", FullName = people[k].Item1, Position = people[k].Item2, Phone = $"09{rnd.Next(10000000, 99999999)}", IsApproved = true, IsActive = true };
                vu.PasswordHash = vendorHasher.HashPassword(vu, Password);
                vUsers.Add(vu); db.VendorUsers.Add(vu);
            }
            await db.SaveChangesAsync();
            done.Add($"{vUsers.Count} tài khoản cổng NCC");
        }
        if (!await db.VendorDocuments.AnyAsync(d => d.Note != null && d.Note.StartsWith(Tag)))
        {
            var k = 0;
            foreach (var v in vendors.Take(8))
            {
                var vu = vUsers.FirstOrDefault(x => x.VendorId == v.Id);
                VendorDocument Doc(VendorDocumentType t, string no, int issuedMonthsAgo, int? validMonths, VendorDocumentStatus st, string note) => new()
                {
                    VendorId = v.Id, Type = t, Number = no, IssueDate = today.AddMonths(-issuedMonthsAgo), ExpiryDate = validMonths == null ? null : today.AddMonths(-issuedMonthsAgo + validMonths.Value),
                    FileName = $"{t}-{v.Code}.pdf", ContentType = "application/pdf", Data = Pdf($"{t.GetDisplayName()} – {v.Name} – số {no}"), Status = st, Note = $"{Tag} {note}",
                    UploadedByVendorUserId = vu?.Id, UploadedAt = now.AddDays(-rnd.Next(5, 200)),
                    VerifiedById = st == VendorDocumentStatus.Pending ? null : buyer.Id, VerifiedAt = st == VendorDocumentStatus.Pending ? null : now.AddDays(-rnd.Next(1, 4))
                };
                db.VendorDocuments.Add(Doc(VendorDocumentType.BusinessLicense, $"01{rnd.Next(10000000, 99999999)}", 40, null, VendorDocumentStatus.Verified, "Đăng ký doanh nghiệp"));
                db.VendorDocuments.Add(Doc(VendorDocumentType.Financial, $"BCTC-{today.Year - 1}", 6, 12, VendorDocumentStatus.Verified, "Báo cáo tài chính năm trước"));
                // Đa dạng tình trạng: sắp hết hạn, chờ xác minh, bị từ chối
                if (k % 3 == 0) db.VendorDocuments.Add(Doc(VendorDocumentType.Quality, $"ISO-{rnd.Next(1000, 9999)}", 35, 36, VendorDocumentStatus.Verified, "Chứng chỉ ISO 9001 – sắp hết hiệu lực"));
                if (k % 3 == 1) db.VendorDocuments.Add(Doc(VendorDocumentType.Authorization, $"UQ-{rnd.Next(100, 999)}", 1, 12, VendorDocumentStatus.Pending, "Ủy quyền của hãng – NCC vừa nộp qua cổng"));
                if (k % 3 == 2) db.VendorDocuments.Add(Doc(VendorDocumentType.Experience, $"HS-{rnd.Next(100, 999)}", 2, null, VendorDocumentStatus.Rejected, "Hồ sơ năng lực – thiếu hợp đồng chứng minh, yêu cầu bổ sung"));
                k++;
            }
            await db.SaveChangesAsync();
            done.Add("hồ sơ pháp lý NCC (đã xác minh / chờ / từ chối / sắp hết hạn)");
        }

        // ------------------------------------------------------------------ 4. Sơ tuyển NCC
        if (!await db.Prequalifications.AnyAsync(x => x.Number.StartsWith(P)))
        {
            var critSet = await db.CriteriaSets.Include(s => s.Items).FirstOrDefaultAsync(s => s.Kind == CriteriaKind.Prequalification);
            var critTemplate = critSet?.Items.OrderBy(i => i.SortOrder).Select(i => (i.Name, i.Requirement, i.IsMandatory, i.MaxScore, i.DocumentType)).ToList()
                ?? new() { ("Tư cách pháp lý", "Giấy ĐKDN còn hiệu lực", true, 0, VendorDocumentType.BusinessLicense), ("Năng lực tài chính", "Doanh thu ≥ 1,5 lần", false, 50, VendorDocumentType.Financial), ("Kinh nghiệm", "≥ 2 hợp đồng tương tự", false, 50, VendorDocumentType.Experience) };
            var catOf = cats.Where(c => !c.Code.StartsWith(P)).ToList();
            var rounds = new (string Title, PrequalificationStatus St, int CatIdx, int OpenedDaysAgo, int Apps)[]
            {
                ("Sơ tuyển nhà cung cấp thiết bị CNTT năm " + today.Year, PrequalificationStatus.Completed, 0, 75, 4),
                ("Sơ tuyển nhà cung cấp văn phòng phẩm " + (today.Year + 1), PrequalificationStatus.PendingApproval, 1, 30, 3),
                ("Sơ tuyển đơn vị cung cấp nội thất văn phòng", PrequalificationStatus.Evaluating, 2, 20, 3),
                ("Sơ tuyển nhà thầu dịch vụ bảo trì hệ thống", PrequalificationStatus.Open, 3, 5, 1),
                ("Sơ tuyển nhà cung cấp bảo hộ lao động", PrequalificationStatus.Draft, 4, 1, 0),
            };
            var n = 0;
            foreach (var r in rounds)
            {
                var cat = catOf[r.CatIdx % catOf.Count];
                var opened = now.AddDays(-r.OpenedDaysAgo);
                var pq = new Prequalification
                {
                    Number = $"{P}ST-{today.Year}-{++n:000}", Title = r.Title, CategoryId = cat.Id, Status = r.St, CreatedById = buyer.Id, CreatedAt = opened.AddDays(-2),
                    Description = $"Lựa chọn danh sách nhà cung cấp {cat.Name.ToLowerInvariant()} đủ năng lực để mời chào giá trong {12} tháng tới.",
                    Deadline = r.St is PrequalificationStatus.Open or PrequalificationStatus.Draft ? today.AddDays(10).AddHours(17) : opened.AddDays(14).Date.AddHours(17),
                    ValidityMonths = 12, MinScore = 60, PublishedAt = r.St == PrequalificationStatus.Draft ? null : opened
                };
                var o = 0;
                foreach (var c in critTemplate) pq.Criteria.Add(new PrequalificationCriterion { Name = c.Item1, Requirement = c.Item2, IsMandatory = c.Item3, MaxScore = c.Item3 ? 0 : c.Item4, DocumentType = c.Item5, SortOrder = ++o });
                db.Prequalifications.Add(pq);
                await db.SaveChangesAsync();
                if (r.St != PrequalificationStatus.Draft) Log(DocType.Prequalification, pq.Id, buyer, "Phát hành đợt sơ tuyển", opened);

                var applicants = vendors.Where(v => v.Categories.Any(vc => vc.CategoryId == cat.Id)).Concat(vendors).Distinct().Take(r.Apps).ToList();
                var a = 0;
                foreach (var v in applicants)
                {
                    a++;
                    var evaluated = r.St is PrequalificationStatus.Completed or PrequalificationStatus.PendingApproval || (r.St == PrequalificationStatus.Evaluating && a == 1);
                    var fail = evaluated && a == applicants.Count && applicants.Count > 2; // NCC cuối không đạt
                    var app = new PrequalificationApplication
                    {
                        PrequalificationId = pq.Id, VendorId = v.Id, VendorUserId = vUsers.FirstOrDefault(x => x.VendorId == v.Id)?.Id, SubmittedAt = opened.AddDays(rnd.Next(2, 12)),
                        Note = $"{v.Name} có {rnd.Next(5, 20)} năm kinh nghiệm cung cấp {cat.Name.ToLowerInvariant()}, đã thực hiện {rnd.Next(3, 15)} hợp đồng tương tự trong 3 năm.",
                        Status = r.St == PrequalificationStatus.Completed ? (fail ? PrequalificationResult.Failed : PrequalificationResult.Passed) : PrequalificationResult.Submitted
                    };
                    if (evaluated)
                    {
                        app.EvaluatedById = buyer2.Id; app.EvaluatedAt = opened.AddDays(16);
                        decimal total = 0;
                        foreach (var c in pq.Criteria)
                        {
                            var sc = c.IsMandatory ? (decimal?)null : fail ? Math.Round(c.MaxScore * (decimal)(0.3 + rnd.NextDouble() * 0.2)) : Math.Round(c.MaxScore * (decimal)(0.65 + rnd.NextDouble() * 0.35));
                            total += sc ?? 0;
                            app.Scores.Add(new PrequalificationScore { CriterionId = c.Id, Pass = c.IsMandatory ? !fail || c.SortOrder > 1 : null, Score = sc,
                                Comment = c.IsMandatory ? (fail && c.SortOrder == 1 ? "Giấy phép không bao gồm ngành nghề phù hợp" : "Đạt") : null });
                        }
                        var max = pq.Criteria.Sum(c => c.MaxScore);
                        app.TotalScore = max == 0 ? 0 : Math.Round(total * 100 / max, 2);
                        app.ResultNote = fail ? "Không đạt: chưa đáp ứng yêu cầu tư cách pháp lý / điểm dưới mức tối thiểu." : "Đạt yêu cầu sơ tuyển.";
                        if (r.St == PrequalificationStatus.Completed && !fail) app.ValidUntil = opened.AddDays(20).AddMonths(pq.ValidityMonths).Date;
                    }
                    db.PrequalificationApplications.Add(app);
                }
                await db.SaveChangesAsync();
                if (r.St is PrequalificationStatus.Evaluating or PrequalificationStatus.PendingApproval or PrequalificationStatus.Completed)
                    Log(DocType.Prequalification, pq.Id, buyer, "Đóng nhận hồ sơ, bắt đầu đánh giá", opened.AddDays(14));
                if (r.St is PrequalificationStatus.PendingApproval or PrequalificationStatus.Completed)
                    Log(DocType.Prequalification, pq.Id, buyer, "Trình duyệt kết quả sơ tuyển", opened.AddDays(17));
                if (r.St == PrequalificationStatus.Completed)
                {
                    pq.DecidedById = director.Id; pq.DecidedAt = opened.AddDays(20); pq.DecisionNote = "Phê duyệt danh sách nhà cung cấp đạt sơ tuyển.";
                    Log(DocType.Prequalification, pq.Id, director, "Phê duyệt kết quả sơ tuyển", opened.AddDays(20), pq.DecisionNote);
                }
            }
            await db.SaveChangesAsync();
            done.Add($"{rounds.Length} đợt sơ tuyển (nháp → đang nhận hồ sơ → đang đánh giá → chờ duyệt → hoàn thành)");
        }

        // ------------------------------------------------------------------ 5. Ngân hàng giá
        if (!await db.PriceRecords.AnyAsync(x => x.SourceNote != null && x.SourceNote.StartsWith(Tag)))
        {
            var sources = new[] { (PriceSource.Survey, "Khảo sát giá thị trường"), (PriceSource.Website, "Giá niêm yết trên website"), (PriceSource.Published, "Thông báo giá của Sở Tài chính"), (PriceSource.Other, "Báo giá tham khảo") };
            var shops = new[] { "Siêu thị điện máy Nguyễn Kim", "Phong Vũ", "Thế giới di động", "Văn phòng phẩm Hồng Hà", "Nội thất Hòa Phát" };
            var cnt = 0;
            foreach (var it in items.Where(i => i.ReferencePrice > 0).Take(18))
                for (var m = 11; m >= 0; m -= rnd.Next(2, 4))
                {
                    var (src, srcName) = sources[rnd.Next(sources.Length)];
                    var useVendor = rnd.Next(3) > 0;
                    db.PriceRecords.Add(new PriceRecord
                    {
                        ItemId = it.Id, VendorId = useVendor ? vendors[rnd.Next(vendors.Count)].Id : null, SupplierName = useVendor ? null : shops[rnd.Next(shops.Length)],
                        Source = src, SourceNote = $"{Tag} {srcName} tháng {today.AddMonths(-m):MM/yyyy}",
                        UnitPrice = Math.Round(it.ReferencePrice * (decimal)(0.88 + rnd.NextDouble() * 0.27) * (1 + m * 0.004m) / 1000m) * 1000m,
                        PriceDate = today.AddMonths(-m).AddDays(-rnd.Next(0, 25)), CreatedById = rnd.Next(2) == 0 ? buyer.Id : buyer2.Id, CreatedAt = now.AddMonths(-m),
                        Unit = it.Unit, Currency = "VND", MaterialVatPercent = 10, Region = new[] { "Hà Nội", "TP. Hồ Chí Minh", "Đà Nẵng" }[cnt % 3]
                    });
                    cnt++;
                }
            foreach (var r in db.PriceRecords.Local.Where(r => r.MaterialPrice == 0 && r.ServicePrice == 0)) r.MaterialPrice = r.UnitPrice; // giá mẫu là giá hàng hóa (vật tư)
            await db.SaveChangesAsync();
            done.Add($"{cnt} giá tham khảo trong ngân hàng giá");
        }

        // ------------------------------------------------------------------ 6. Bảng giá tiêu chuẩn
        if (!await db.StandardPriceLists.AnyAsync(x => x.Number.StartsWith(P)))
        {
            var lists = new (string Title, StandardPriceStatus St, DateTime From, DateTime? To, int Skip)[]
            {
                ($"Bảng giá tiêu chuẩn hàng hóa văn phòng năm {today.Year}", StandardPriceStatus.Approved, new DateTime(today.Year, 1, 1), new DateTime(today.Year, 12, 31), 0),
                ($"Bảng giá tiêu chuẩn thiết bị CNTT năm {today.Year + 1}", StandardPriceStatus.Submitted, new DateTime(today.Year + 1, 1, 1), new DateTime(today.Year + 1, 12, 31), 6),
                ($"Điều chỉnh giá vật tư quý {((today.Month - 1) / 3 + 2 > 4 ? 1 : (today.Month - 1) / 3 + 2)}", StandardPriceStatus.Draft, today.AddMonths(1), null, 12),
            };
            var n = 0;
            foreach (var l in lists)
            {
                var created = l.St == StandardPriceStatus.Approved ? new DateTime(today.Year - 1, 12, 15) : now.AddDays(-rnd.Next(2, 10));
                var sp = new StandardPriceList
                {
                    Number = $"{P}BG-{l.From.Year}-{++n:000}", Title = l.Title, EffectiveFrom = l.From, EffectiveTo = l.To, Status = l.St, CreatedById = buyer.Id, CreatedAt = created,
                    Basis = "Ngân hàng giá 12 tháng, báo giá của ≥ 3 NCC và giá trúng thầu gần nhất.",
                    SubmittedAt = l.St == StandardPriceStatus.Draft ? null : created.AddDays(2),
                    DecidedById = l.St == StandardPriceStatus.Approved ? director.Id : null, DecidedAt = l.St == StandardPriceStatus.Approved ? created.AddDays(4) : null,
                    DecisionNote = l.St == StandardPriceStatus.Approved ? "Ban hành áp dụng cho các đơn hàng trong năm; vượt giá trần phải giải trình." : null
                };
                foreach (var it in items.Where(i => i.ReferencePrice > 0).Skip(l.Skip).Take(10))
                {
                    var std = Math.Round(it.ReferencePrice * (decimal)(0.95 + rnd.NextDouble() * 0.1) / 1000m) * 1000m;
                    sp.Lines.Add(new StandardPriceLine { ItemId = it.Id, StandardPrice = std, CeilingPrice = Math.Round(std * 1.1m / 1000m) * 1000m, Basis = "Trung vị ngân hàng giá + 10% trần" });
                }
                db.StandardPriceLists.Add(sp);
                await db.SaveChangesAsync();
                if (sp.SubmittedAt != null) Log(DocType.StandardPriceList, sp.Id, buyer, "Trình duyệt bảng giá", sp.SubmittedAt.Value);
                if (sp.DecidedAt != null) Log(DocType.StandardPriceList, sp.Id, director, "Phê duyệt bảng giá", sp.DecidedAt.Value, sp.DecisionNote);
            }
            done.Add("3 bảng giá tiêu chuẩn (đã ban hành / chờ duyệt / nháp)");
        }

        // ------------------------------------------------------------------ 7. Kế hoạch mua sắm năm sau
        if (!await db.ProcurementPlans.AnyAsync(x => x.Note != null && x.Note.StartsWith(Tag)))
        {
            // Các phòng chưa có kế hoạch năm sau, lần lượt nhận 4 trạng thái để thấy đủ luồng
            var existing = await db.ProcurementPlans.Where(x => x.Year == today.Year + 1).Select(x => x.Department).ToListAsync();
            var heads = await db.Users.Where(x => x.IsActive && x.Role == AppRoles.Approver).ToListAsync();
            var statuses = new[] { PlanStatus.Approved, PlanStatus.Submitted, PlanStatus.Returned, PlanStatus.Draft };
            var depts = heads.Select(h => h.Department).Distinct().Where(d => !existing.Contains(d)).OrderBy(d => d).Take(4)
                .Select((d, i) => (Dept: d, St: statuses[i], Approver: heads.First(h => h.Department == d).Username)).ToList();
            var added = 0;
            foreach (var (dept, st, appr) in depts)
            {
                var owner = U(appr) ?? admin;
                var plan = new ProcurementPlan
                {
                    Year = today.Year + 1, Department = dept, Status = st, CreatedById = owner.Id, CreatedAt = now.AddDays(-rnd.Next(10, 30)),
                    Note = $"{Tag} Kế hoạch mua sắm năm {today.Year + 1} của {dept}",
                    SubmittedAt = st == PlanStatus.Draft ? null : now.AddDays(-8), ApprovedById = st == PlanStatus.Approved ? director.Id : null,
                    ApprovedAt = st == PlanStatus.Approved ? now.AddDays(-3) : null,
                    DecisionNote = st switch { PlanStatus.Approved => "Đồng ý kế hoạch, triển khai theo quý.", PlanStatus.Returned => "Đề nghị rà soát lại số lượng máy đo, tách theo quý.", _ => null }
                };
                var q = 0;
                foreach (var it in items.OrderBy(_ => rnd.Next()).Take(4))
                    plan.Lines.Add(new ProcurementPlanLine { ItemId = it.Id, Description = it.Name, Unit = it.Unit, Quantity = rnd.Next(2, 20), EstimatedPrice = it.ReferencePrice, Quarter = ++q, Note = "Thay thế / bổ sung theo định mức" });
                db.ProcurementPlans.Add(plan);
                await db.SaveChangesAsync();
                if (plan.SubmittedAt != null) Log(DocType.Plan, plan.Id, owner, "Gửi duyệt kế hoạch", plan.SubmittedAt.Value);
                if (st == PlanStatus.Approved) Log(DocType.Plan, plan.Id, director, "Phê duyệt kế hoạch", plan.ApprovedAt!.Value, plan.DecisionNote);
                if (st == PlanStatus.Returned) Log(DocType.Plan, plan.Id, director, "Trả lại kế hoạch", now.AddDays(-4), plan.DecisionNote);
                added++;
            }
            if (added > 0) done.Add($"kế hoạch mua sắm năm {today.Year + 1} của {added} phòng (nháp / chờ duyệt / trả lại / đã duyệt)");
        }

        // ------------------------------------------------------------------ 8. Đấu thầu: đàm phán, thẩm định, hỏi đáp HSMT
        var tenders = await db.Tenders.Include(t => t.Bidders).Include(t => t.Negotiations).Include(t => t.Appraisals).Include(t => t.Questions)
            .Where(t => t.Number.StartsWith(P)).ToListAsync();
        var appraiser = U("ketoan.tp") ?? U("approver") ?? director;
        var negAdded = 0; var apprAdded = 0; var qAdded = 0;
        foreach (var t in tenders.Where(t => t.Status is TenderStatus.PendingResultApproval or TenderStatus.Awarded or TenderStatus.Contracted && t.Negotiations.Count == 0))
        {
            var bids = t.Bidders.Where(b => b.Submitted && b.Valid != false && b.Qualified != false).OrderBy(b => b.BidPrice * (1 - b.DiscountPercent / 100)).ToList();
            var win = bids.FirstOrDefault(b => b.VendorId == t.AwardedVendorId) ?? bids.FirstOrDefault();
            if (win == null) continue;
            var day = (t.OpenedAt ?? t.ClosingAt).Date.AddDays(5);
            var price = Math.Round(win.BidPrice * (1 - win.DiscountPercent / 100));
            var negotiated = t.AwardPrice ?? Math.Round(price * 0.985m / 1000m) * 1000m;
            db.TenderNegotiations.Add(new TenderNegotiation
            {
                TenderId = t.Id, TenderBidderId = win.Id, Rank = 1, NegotiatedOn = day, CreatedById = t.BuyerId, CreatedAt = day.AddHours(15),
                Participants = $"Bên mời thầu: {buyer.FullName}, {buyer2.FullName}; Nhà thầu: đại diện theo pháp luật", BidPrice = price, NegotiatedPrice = negotiated, Success = true,
                Content = "Thống nhất khối lượng, tiến độ giao hàng 30 ngày, bảo hành 24 tháng, thanh toán 2 đợt. Nhà thầu đồng ý giảm giá và bổ sung đào tạo vận hành.",
                Note = "Đàm phán thành công – căn cứ đề xuất trúng thầu"
            });
            Log(DocType.Tender, t.Id, buyer, "Đàm phán hợp đồng thành công", day.AddHours(15), $"Giá sau đàm phán {negotiated.ToMoney()} đ");
            negAdded++;
            if (t.Appraisals.Count == 0)
            {
                var completed = t.Status != TenderStatus.PendingResultApproval || negAdded % 2 == 0;
                db.TenderAppraisals.Add(new TenderAppraisal
                {
                    TenderId = t.Id, AppraiserId = appraiser.Id, AssignedById = director.Id, AssignedAt = day.AddDays(1), CompletedAt = completed ? day.AddDays(3) : null,
                    Agree = completed ? true : null,
                    Content = completed ? "Quy trình lựa chọn nhà thầu đúng kế hoạch được duyệt; HSDT được đánh giá đúng tiêu chí HSMT; giá đề nghị trúng thầu không vượt giá gói thầu." : null,
                    Recommendation = completed ? "Đề nghị Giám đốc phê duyệt kết quả lựa chọn nhà thầu." : null
                });
                Log(DocType.Tender, t.Id, director, "Giao thẩm định kết quả LCNT", day.AddDays(1), appraiser.FullName);
                if (completed) Log(DocType.Tender, t.Id, appraiser, "Hoàn thành báo cáo thẩm định – thống nhất", day.AddDays(3));
                apprAdded++;
            }
        }
        foreach (var t in tenders.Where(t => t.Status is TenderStatus.Published or TenderStatus.Opened && t.Questions.Count == 0))
        {
            var askers = vUsers.Take(2).ToList();
            foreach (var vu in askers)
            {
                db.TenderQuestions.Add(new TenderQuestion
                {
                    TenderId = t.Id, VendorId = vu.VendorId, VendorUserId = vu.Id, AskedAt = (t.PublishedAt ?? now.AddDays(-5)).AddDays(2 + qAdded),
                    Content = qAdded % 2 == 0 ? "Đề nghị làm rõ: thiết bị có chấp nhận hàng tương đương (cùng thông số) của hãng khác không?"
                                              : "Đề nghị cho biết địa điểm giao hàng và yêu cầu lắp đặt tại từng chi nhánh."
                });
                qAdded++;
            }
        }
        await db.SaveChangesAsync();
        if (negAdded + apprAdded + qAdded > 0) done.Add($"đấu thầu: {negAdded} biên bản đàm phán, {apprAdded} thẩm định, {qAdded} câu hỏi làm rõ HSMT");

        // ------------------------------------------------------------------ 9. Thông báo giao hàng từ cổng NCC
        if (!await db.PoShipments.AnyAsync(s => s.VendorUser!.Email.EndsWith(VendorDomain)))
        {
            var vendorIds = vUsers.Select(x => x.VendorId).ToList();
            var open = await db.PurchaseOrders.Where(p => p.Number.StartsWith(P) && (p.Status == PoStatus.Issued || p.Status == PoStatus.PartiallyReceived) && p.VendorId != null && vendorIds.Contains(p.VendorId.Value))
                .OrderByDescending(p => p.OrderDate).Take(4).ToListAsync();
            foreach (var po in open)
            {
                var vu = vUsers.First(x => x.VendorId == po.VendorId);
                db.PoShipments.Add(new PoShipment { PurchaseOrderId = po.Id, VendorUserId = vu.Id, ShipDate = today.AddDays(-1), ExpectedDate = today.AddDays(1),
                    Reference = $"PXK-{rnd.Next(1000, 9999)}", Note = "Đã xuất kho, giao bằng xe của NCC; liên hệ thủ kho trước 30 phút.", CreatedAt = now.AddDays(-1) });
            }
            await db.SaveChangesAsync();
            if (open.Count > 0) done.Add($"{open.Count} thông báo giao hàng từ cổng NCC");
        }

        // ------------------------------------------------------------------ 10. Quyền theo gói
        if (!await db.PackageMembers.AnyAsync(m => m.Package!.Number.StartsWith(P) || m.Package.PurchaseRequests.Any(r => r.Number.StartsWith(P))))
        {
            var pkgs = await db.ProcurementPackages.Where(p => p.PurchaseRequests.Any(r => r.Number.StartsWith(P)) && p.Status != PackageStatus.Cancelled)
                .OrderByDescending(p => p.Budget).Take(3).ToListAsync();
            var viewer = U("kythuat.tp") ?? U("approver");
            foreach (var p in pkgs)
            {
                var other = p.BuyerId == buyer.Id ? buyer2 : buyer;
                db.PackageMembers.Add(new PackageMember { PackageId = p.Id, UserId = other.Id, Role = PackageRole.Member });
                if (viewer != null) db.PackageMembers.Add(new PackageMember { PackageId = p.Id, UserId = viewer.Id, Role = PackageRole.Viewer });
            }
            await db.SaveChangesAsync();
            if (pkgs.Count > 0) done.Add($"phân quyền {pkgs.Count} gói (thành viên / chỉ xem)");
        }

        // ------------------------------------------------------------------ 11. Biểu mẫu
        if (!await db.FormTemplates.AnyAsync(f => f.Code != null && f.Code.StartsWith(P)))
        {
            var forms = new (string Code, string Name, string Group, string Desc, string[] Body)[]
            {
                ("BM-01", "Phiếu đề nghị mua sắm", "Kế hoạch & đề nghị", "Dùng khi lập đề nghị ngoài hệ thống (trường hợp khẩn).",
                    new[] { "Phòng đề nghị: ……………", "Nội dung mua sắm: ……………", "Bảng kê hàng hóa: STT – Tên hàng – ĐVT – Số lượng – Đơn giá dự kiến – Thành tiền", "Mục đích sử dụng / căn cứ: ……………", "Người đề nghị – Trưởng bộ phận – Ban Giám đốc" }),
                ("BM-02", "Hồ sơ mời thầu mua sắm hàng hóa (mẫu)", "Hồ sơ mời thầu / yêu cầu", "Khung HSMT: chỉ dẫn nhà thầu, bảng dữ liệu, tiêu chuẩn đánh giá, yêu cầu kỹ thuật, dự thảo hợp đồng.",
                    new[] { "Chương I. Chỉ dẫn nhà thầu", "Chương II. Bảng dữ liệu đấu thầu", "Chương III. Tiêu chuẩn đánh giá HSDT", "Chương IV. Biểu mẫu dự thầu", "Chương V. Yêu cầu kỹ thuật", "Chương VI. Điều kiện hợp đồng và mẫu hợp đồng" }),
                ("BM-03", "Thư mời chào giá", "Chào giá", "Gửi kèm danh mục hàng hóa khi mời NCC báo giá ngoài cổng.",
                    new[] { "Kính gửi: ……………", "Công ty kính mời Quý đơn vị gửi báo giá cho danh mục hàng hóa đính kèm.", "Hạn nộp báo giá: ……………", "Báo giá ghi rõ đơn giá trước VAT, thời gian giao hàng, bảo hành, hiệu lực báo giá." }),
                ("BM-04", "Biên bản đánh giá hồ sơ chào giá", "Đánh giá & thẩm định", "Tổ chuyên gia lập sau khi mở và so sánh báo giá.",
                    new[] { "Thành phần tổ chuyên gia: ……………", "Danh sách báo giá nhận được: ……………", "Kết quả đánh giá tính hợp lệ, kỹ thuật, giá", "Kiến nghị nhà cung cấp được lựa chọn: ……………" }),
                ("BM-05", "Hợp đồng mua bán hàng hóa (mẫu)", "Hợp đồng", "Dự thảo hợp đồng chuẩn đã được pháp chế rà soát.",
                    new[] { "Điều 1. Hàng hóa, số lượng, giá", "Điều 2. Chất lượng, bảo hành", "Điều 3. Giao hàng, nghiệm thu", "Điều 4. Thanh toán", "Điều 5. Bảo đảm thực hiện hợp đồng", "Điều 6. Phạt vi phạm, bồi thường", "Điều 7. Điều khoản chung" }),
                ("BM-06", "Biên bản giao nhận – nghiệm thu hàng hóa", "Giao nhận & nghiệm thu", "Lập khi nhận hàng, ghi số lượng đạt / không đạt.",
                    new[] { "Căn cứ đơn hàng / hợp đồng số: ……………", "Bảng kê: Tên hàng – ĐVT – SL giao – SL đạt – SL không đạt – Ghi chú", "Kết luận nghiệm thu: ……………", "Đại diện bên giao – Đại diện bên nhận – Thủ kho" }),
                ("BM-07", "Giấy đề nghị thanh toán", "Thanh toán", "Kèm hóa đơn, biên bản nghiệm thu khi đề nghị thanh toán cho NCC.",
                    new[] { "Người đề nghị: ……………", "Nội dung thanh toán: ……………", "Số tiền: ……………  Bằng chữ: ……………", "Chứng từ kèm theo: hóa đơn, biên bản nghiệm thu, hợp đồng", "Người đề nghị – Kế toán trưởng – Giám đốc" }),
                ("BM-08", "Phiếu đánh giá nhà cung cấp định kỳ", "Nhà cung cấp", "Đánh giá 6 tháng / năm theo 4 tiêu chí: chất lượng, tiến độ, giá, dịch vụ.",
                    new[] { "Tên nhà cung cấp: ……………  Kỳ đánh giá: ……………", "Chất lượng (1–5): …  Tiến độ (1–5): …  Giá (1–5): …  Dịch vụ (1–5): …", "Nhận xét, đề xuất: ……………" }),
            };
            foreach (var f in forms)
            {
                var data = Docx(f.Name, f.Body);
                db.FormTemplates.Add(new FormTemplate
                {
                    Code = $"{P}{f.Code}", Name = f.Name, Group = f.Group, Description = f.Desc, Version = "v1.0", FileName = $"{f.Code}-{Slug(f.Name)}.docx",
                    ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Data = data, Size = data.Length, IsActive = true,
                    Downloads = rnd.Next(0, 40), UploadedById = admin.Id, UploadedAt = now.AddDays(-rnd.Next(10, 90))
                });
            }
            await db.SaveChangesAsync();
            done.Add($"{forms.Length} biểu mẫu Word");
        }

        done.AddRange(await SampleConstruction.LoadAsync(db)); // gói / hợp đồng xây dựng theo BOQ
        done.AddRange(await SampleAdvanced.LoadAsync(db)); // tính năng nâng cao: quyết toán, liên danh, năng lực, rủi ro, lưu trữ, ESG

        db.ApprovalHistories.AddRange(logs);
        await db.SaveChangesAsync();
        return done;
    }

    /// <summary>Gỡ phần bổ sung (gọi trong giao dịch của SampleDataSeeder.RemoveAsync, trước khi xóa chứng từ mẫu)</summary>
    public static async Task RemoveAsync(EProcDbContext db)
    {
        await SampleAdvanced.RemoveAsync(db); // dữ liệu tính năng nâng cao gắn với chứng từ mẫu
        await SampleConstruction.RemoveAsync(db); // dữ liệu xây dựng mẫu phải gỡ trước (khóa ngoại tới gói / hợp đồng)
        var vuIds = await db.VendorUsers.Where(x => x.Email.EndsWith(VendorDomain)).Select(x => x.Id).ToListAsync();
        var pqIds = await db.Prequalifications.Where(x => x.Number.StartsWith(P)).Select(x => x.Id).ToListAsync();
        var appIds = await db.PrequalificationApplications.Where(a => pqIds.Contains(a.PrequalificationId) || (a.VendorUserId != null && vuIds.Contains(a.VendorUserId.Value))).Select(a => a.Id).ToListAsync();
        await db.PrequalificationScores.Where(s => appIds.Contains(s.ApplicationId)).ExecuteDeleteAsync();
        await db.PrequalificationDocuments.Where(s => appIds.Contains(s.ApplicationId)).ExecuteDeleteAsync();
        await db.PrequalificationApplications.Where(a => appIds.Contains(a.Id)).ExecuteDeleteAsync();
        await db.PrequalificationCriteria.Where(c => pqIds.Contains(c.PrequalificationId)).ExecuteDeleteAsync();
        await db.Prequalifications.Where(x => pqIds.Contains(x.Id)).ExecuteDeleteAsync();

        var spIds = await db.StandardPriceLists.Where(x => x.Number.StartsWith(P)).Select(x => x.Id).ToListAsync();
        await db.StandardPriceLines.Where(l => spIds.Contains(l.ListId)).ExecuteDeleteAsync();
        await db.StandardPriceLists.Where(x => spIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PriceRecords.Where(x => x.SourceNote != null && x.SourceNote.StartsWith(Tag)).ExecuteDeleteAsync();

        var planIds = await db.ProcurementPlans.Where(x => x.Note != null && x.Note.StartsWith(Tag)).Select(x => x.Id).ToListAsync();
        await db.ProcurementPlanLines.Where(l => planIds.Contains(l.PlanId)).ExecuteDeleteAsync();
        await db.ProcurementPlans.Where(x => planIds.Contains(x.Id)).ExecuteDeleteAsync();

        foreach (var (type, ids) in new[] { (DocType.Prequalification, pqIds), (DocType.StandardPriceList, spIds), (DocType.Plan, planIds) })
            await db.ApprovalHistories.Where(x => x.DocumentType == type && ids.Contains(x.DocumentId)).ExecuteDeleteAsync();

        // Hoạt động của tài khoản cổng NCC mẫu
        var docIds = await db.VendorDocuments.Where(d => (d.Note != null && d.Note.StartsWith(Tag)) || (d.UploadedByVendorUserId != null && vuIds.Contains(d.UploadedByVendorUserId.Value))).Select(d => d.Id).ToListAsync();
        await db.PrequalificationDocuments.Where(d => docIds.Contains(d.VendorDocumentId)).ExecuteDeleteAsync();
        await db.VendorDocuments.Where(d => docIds.Contains(d.Id)).ExecuteDeleteAsync();
        await db.TenderQuestions.Where(q => vuIds.Contains(q.VendorUserId)).ExecuteDeleteAsync();
        await db.PoShipments.Where(s => vuIds.Contains(s.VendorUserId)).ExecuteDeleteAsync();
        await db.VendorSubmissions.Where(s => vuIds.Contains(s.VendorUserId)).ExecuteDeleteAsync();
        await db.Invoices.Where(i => i.VendorUserId != null && vuIds.Contains(i.VendorUserId.Value)).ExecuteUpdateAsync(s => s.SetProperty(i => i.VendorUserId, (int?)null));
        await db.VendorUsers.Where(x => vuIds.Contains(x.Id)).ExecuteDeleteAsync();

        await db.FormTemplates.Where(f => f.Code != null && f.Code.StartsWith(P)).ExecuteDeleteAsync();

        // Cơ cấu tổ chức: phòng ban về lại trực thuộc đơn vị cấp trên của khối mẫu; xóa khối / tổ mẫu không còn dùng
        var sampleUnits = await db.OrgUnits.Where(o => o.Code.StartsWith(P)).ToListAsync();
        foreach (var unit in sampleUnits)
            await db.OrgUnits.Where(o => o.ParentId == unit.Id && !o.Code.StartsWith(P)).ExecuteUpdateAsync(s => s.SetProperty(o => o.ParentId, unit.ParentId));
        foreach (var unit in sampleUnits.OrderByDescending(x => x.Type))
            if (await OrgUnits.UsageAsync(db, unit.Name) == 0 && !await db.OrgUnits.AnyAsync(o => o.ParentId == unit.Id && o.Id != unit.Id && !o.Code.StartsWith(P)))
            {
                await db.OrgUnits.Where(o => o.ParentId == unit.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.ParentId, unit.ParentId));
                await db.OrgUnits.Where(o => o.Id == unit.Id).ExecuteDeleteAsync();
                await db.CatalogEntries.Where(e => e.Type == CatalogType.Department && e.Name == unit.Name).ExecuteDeleteAsync();
            }

        // Nhóm hàng cấp 1 mẫu: nhóm con về lại cấp gốc
        var sampleCats = await db.Categories.Where(c => c.Code.StartsWith(P)).Select(c => c.Id).ToListAsync();
        await db.Categories.Where(c => c.ParentId != null && sampleCats.Contains(c.ParentId.Value)).ExecuteUpdateAsync(s => s.SetProperty(c => c.ParentId, (int?)null));
        foreach (var id in sampleCats)
            if (!await db.Items.AnyAsync(i => i.CategoryId == id) && !await db.VendorCategories.AnyAsync(v => v.CategoryId == id)
                && !await db.CriteriaSets.AnyAsync(s => s.CategoryId == id) && !await db.Prequalifications.AnyAsync(p => p.CategoryId == id))
                await db.Categories.Where(c => c.Id == id).ExecuteDeleteAsync();
    }

    // ------------------------------------------------------------------ tệp mẫu
    private static string Slug(string s) =>
        string.Concat(s.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark))
            .Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant().Replace(' ', '-').Replace("–", "").Replace("(", "").Replace(")", "").Replace("--", "-");

    /// <summary>Tệp Word (.docx) tối giản: tiêu đề + các đoạn văn</summary>
    public static byte[] Docx(string title, IEnumerable<string> paragraphs)
    {
        static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";
        var body = new StringBuilder();
        body.Append($"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:b/><w:sz w:val=\"32\"/></w:rPr><w:t>{X(title.ToUpperInvariant())}</w:t></w:r></w:p>");
        body.Append("<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:rPr><w:i/></w:rPr><w:t>(Biểu mẫu tham khảo – dữ liệu mẫu E-Procurement)</w:t></w:r></w:p>");
        foreach (var p in paragraphs) body.Append($"<w:p><w:r><w:t xml:space=\"preserve\">{X(p)}</w:t></w:r></w:p>");
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Put(string path, string xml) { using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); w.Write(xml); }
            Put("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
            Put("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
            Put("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1701\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>");
        }
        return ms.ToArray();
    }

    /// <summary>PDF một trang chứa dòng chữ (không dấu) – đủ để xem thử hồ sơ mẫu</summary>
    public static byte[] Pdf(string text)
    {
        var plain = string.Concat(text.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark))
            .Replace('đ', 'd').Replace('Đ', 'D').Replace("(", "").Replace(")", "").Replace("\\", "").Replace("–", "-");
        plain = new string(plain.Where(c => c < 128).ToArray());
        var stream = $"BT /F1 14 Tf 60 780 Td (HO SO MAU - E-PROCUREMENT) Tj 0 -28 Td /F1 11 Tf ({plain}) Tj ET";
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        var sb = new StringBuilder("%PDF-1.4\n"); var offs = new List<int>();
        for (var i = 0; i < objs.Length; i++) { offs.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offs) sb.Append($"{o:0000000000} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
