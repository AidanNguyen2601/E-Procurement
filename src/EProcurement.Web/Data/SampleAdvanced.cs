using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Data;

/// <summary>
/// Dữ liệu mẫu cho các tính năng nâng cao: mốc thời gian theo luật, liên danh / thầu phụ, yêu cầu năng lực có cấu trúc, ưu đãi,
/// phân tích đơn giá theo định mức, giữ lại bảo hành / thu hồi tạm ứng, điều chỉnh giá, quyết toán – thanh lý, rủi ro gói, lưu trữ hồ sơ, ESG.
/// Chứng từ đánh số MAU-…-XD-… nên được gỡ cùng dữ liệu xây dựng mẫu (SampleConstruction.RemoveAsync).
/// </summary>
public static class SampleAdvanced
{
    private const string P = SampleDataSeeder.Prefix;
    public const string Marker = P + "HD-XD-002";

    private static decimal R(decimal v) => Math.Round(v / 1000m) * 1000m;

    public static Task<bool> ExistsAsync(EProcDbContext db) => db.Contracts.AnyAsync(c => c.Number == Marker);

    public static async Task<List<string>> LoadAsync(EProcDbContext db)
    {
        var done = new List<string>();
        if (await ExistsAsync(db)) return done;
        var users = await db.Users.Where(u => u.IsActive).OrderBy(u => u.Id).ToListAsync();
        AppUser? U(string name) => users.FirstOrDefault(x => x.Username.Equals(name, StringComparison.OrdinalIgnoreCase));
        var admin = U("admin") ?? users.First(x => x.Role == AppRoles.Admin);
        var buyer = U("buyer") ?? admin; var director = U("director") ?? admin;
        var accountant = U("ketoan.tt") ?? users.FirstOrDefault(x => x.Role == AppRoles.Accountant) ?? admin;
        var requester = U("requester") ?? admin;
        var committee = users.Where(x => x.Id != director.Id && x.Id != buyer.Id && x.Role != AppRoles.Admin).Take(3).ToList();
        if (committee.Count < 3) committee = users.Where(x => x.Id != director.Id).Take(3).ToList();
        var vendors = await db.Vendors.Where(v => v.Status == VendorStatus.Approved).OrderBy(v => v.Code).ToListAsync();
        if (vendors.Count < 3) throw new InvalidOperationException("Cần có ít nhất 3 nhà cung cấp đã duyệt (nạp dữ liệu mẫu chính trước).");
        Vendor V(int i) => vendors[i % vendors.Count];
        var today = DateTime.Today;
        var logs = new List<(DocType Type, Func<int> Id, AppUser User, string Action, DateTime At, string? Comment)>();
        void Log(DocType t, Func<int> id, AppUser u, string action, DateTime at, string? comment = null) => logs.Add((t, id, u, action, at, comment));
        var files = new List<(DocType Type, Func<int> Id, string Name, byte[] Data, string Ct, DateTime At)>();
        void File(DocType t, Func<int> id, string name, byte[] data, DateTime at) =>
            files.Add((t, id, name, data, name.EndsWith(".pdf") ? "application/pdf" : "application/vnd.openxmlformats-officedocument.wordprocessingml.document", at));

        PurchaseRequest NewPr(string suffix, string title, PrStatus st, DateTime date, decimal amount, string dept = "Phòng Kỹ thuật", ProcurementMethod method = ProcurementMethod.OpenTender)
        {
            var pr = new PurchaseRequest
            {
                Number = $"{P}PR-XD-{suffix}", Title = title, RequesterId = requester.Id, Department = dept, RequestDate = date, NeededDate = date.AddDays(90), Priority = PrPriority.Normal,
                Purpose = title, Status = st, EstimatedTotal = amount, SubmittedAt = date.AddHours(9), Method = method, CreatedAt = date
            };
            pr.Lines.Add(new PurchaseRequestLine { Description = title, Unit = "Gói", Quantity = 1, EstimatedPrice = amount, Amount = amount, SortOrder = 1 });
            db.PurchaseRequests.Add(pr);
            return pr;
        }
        ProcurementPackage NewPkg(string suffix, string title, string field, ProcurementMethod method, decimal budget, PackageStatus st, DateTime created, PurchaseRequest pr, string? note = null)
        {
            var p = new ProcurementPackage
            {
                Number = $"{P}GM-XD-{suffix}", Title = title, Field = field, Method = method, Budget = budget, Status = st, BuyerId = buyer.Id, CreatedAt = created,
                PlannedStart = created.AddDays(10), ContractDurationDays = 90, ContractType = "Trọn gói", FundingSource = "Ngân sách hoạt động", Note = note,
                MethodReason = method is ProcurementMethod.DirectAward ? "Bổ sung thiết bị cùng chủng loại, cần gấp cho dây chuyền mới." : null
            };
            p.PurchaseRequests.Add(pr);
            db.ProcurementPackages.Add(p);
            return p;
        }

        // =============================================================== 1. Hợp đồng xây dựng đang thực hiện: giữ lại, thu hồi tạm ứng, điều chỉnh giá
        var hd1 = await db.Contracts.Include(c => c.PriceIndices).FirstOrDefaultAsync(c => c.Number == P + "HD-XD-001");
        if (hd1 != null && hd1.PriceIndices.Count == 0)
        {
            hd1.RetentionPercent = 5; hd1.RetentionCapPercent = 5; hd1.AdvanceRecoveryStartPercent = 0; hd1.AdvanceRecoveryEndPercent = 80;
            hd1.WarrantyEndDate = hd1.EndDate.AddMonths(12);
            var idx = new[] { ("Thép xây dựng", 0.20m, 100m), ("Xi măng PCB40", 0.10m, 100m), ("Nhân công xây dựng", 0.25m, 100m) }
                .Select((x, i) => new ContractPriceIndex { ContractId = hd1.Id, Name = x.Item1, Weight = x.Item2, BaseIndex = x.Item3, SortOrder = i + 1 }).ToList();
            db.ContractPriceIndices.AddRange(idx);
            await db.SaveChangesAsync();
            var accs = await db.BoqAcceptances.Where(a => a.ContractId == hd1.Id && a.Status == AcceptanceStatus.Confirmed).OrderBy(a => a.Period).ToListAsync();
            ContractPriceAdjustment Adj(int n, BoqAcceptance a, decimal[] cur, SettlementStatus st, int daysAgo)
            {
                var map = idx.Select((x, i) => (x.Id, cur[i])).ToDictionary(x => x.Id, x => x.Item2);
                var f = ContractClose.Factor(idx, map);
                var adj = new ContractPriceAdjustment
                {
                    Number = $"{P}DCG-XD-{n:000}", ContractId = hd1.Id, Period = $"Đợt nghiệm thu {a.Period}", AcceptanceId = a.Id, BaseAmount = a.SubTotal, Factor = f,
                    Amount = Math.Round(a.SubTotal * (f - 1), 0, MidpointRounding.AwayFromZero), Indices = ContractClose.IndicesText(idx, map),
                    Note = "Chỉ số giá xây dựng do Sở Xây dựng công bố tháng nghiệm thu (số liệu minh họa)", Status = st, CreatedById = buyer.Id, CreatedAt = today.AddDays(-daysAgo)
                };
                if (st == SettlementStatus.Approved) { adj.DecidedById = director.Id; adj.DecidedAt = adj.CreatedAt.AddDays(1); adj.DecisionNote = "Đồng ý theo chỉ số công bố."; }
                db.ContractPriceAdjustments.Add(adj);
                return adj;
            }
            if (accs.Count >= 2)
            {
                var a1 = Adj(1, accs[0], new[] { 112m, 104m, 108m }, SettlementStatus.Approved, 40);
                var a2 = Adj(2, accs[1], new[] { 115m, 106m, 110m }, SettlementStatus.Pending, 5);
                await db.SaveChangesAsync();
                var g = ContractClose.Gross(a1.Amount, hd1.VatPercent);
                var pay = new Payment
                {
                    Number = $"{P}TT-XD-010", Type = PaymentType.PriceAdjustment, VendorId = hd1.VendorId, ContractId = hd1.Id, PriceAdjustmentId = a1.Id, Amount = g, NetAmount = g,
                    Note = $"Thanh toán điều chỉnh giá {a1.Number} – HĐ {hd1.Number} (hệ số {a1.Factor:0.######})", Status = PaymentStatus.Paid, RequestedById = accountant.Id,
                    RequestedAt = today.AddDays(-37), ApprovedById = director.Id, ApprovedAt = today.AddDays(-36), PaidById = accountant.Id, PaidAt = today.AddDays(-33), PaymentRef = "UNC57120"
                };
                db.Payments.Add(pay);
                await db.SaveChangesAsync();
                Log(DocType.Contract, () => hd1.Id, buyer, "Thiết lập công thức điều chỉnh giá: phần cố định 0.45", today.AddDays(-60), "Thép b=0,20; Xi măng b=0,10; Nhân công b=0,25");
                Log(DocType.Contract, () => hd1.Id, buyer, $"Trình điều chỉnh giá {a1.Number} kỳ {a1.Period}: hệ số {a1.Factor:0.######}, {a1.Amount.ToMoney()} đ (trước VAT)", a1.CreatedAt, a1.Indices);
                Log(DocType.Contract, () => hd1.Id, director, $"Giám đốc duyệt điều chỉnh giá {a1.Number}: {a1.Amount.ToMoney()} đ", a1.DecidedAt!.Value);
                Log(DocType.Contract, () => hd1.Id, buyer, $"Trình điều chỉnh giá {a2.Number} kỳ {a2.Period}: hệ số {a2.Factor:0.######}, {a2.Amount.ToMoney()} đ (trước VAT)", a2.CreatedAt, a2.Indices);
                Log(DocType.Payment, () => pay.Id, accountant, $"Đã chi {g.ToMoney()} đ", pay.PaidAt!.Value);
            }
            Log(DocType.Contract, () => hd1.Id, buyer, "Cập nhật điều khoản thanh toán: giữ lại 5% mỗi đợt (tối đa 5%), thu hồi tạm ứng từ 0% đến 80% giá trị lũy kế", today.AddDays(-61));
            done.Add($"hợp đồng {hd1.Number}: giữ lại bảo hành 5%, thu hồi tạm ứng đến 80%, công thức điều chỉnh giá + 2 kỳ điều chỉnh (1 đã duyệt & chi, 1 chờ duyệt)");
        }

        // =============================================================== 2. Hợp đồng đã quyết toán và thanh lý (gói hoàn tất → lưu trữ)
        {
            var signed = today.AddDays(-420);
            var pr = NewPr("003", "Bảo trì, thay thế hệ thống PCCC nhà xưởng khu A", PrStatus.Completed, signed.AddDays(-60), 950_000_000);
            var pkg = NewPkg("003", "Bảo trì, thay thế hệ thống phòng cháy chữa cháy nhà xưởng khu A", "Dịch vụ phi tư vấn", ProcurementMethod.OpenTender, 950_000_000, PackageStatus.Completed, signed.AddDays(-55), pr, "Mẫu: hợp đồng đã quyết toán, thanh lý");
            var t = new Tender
            {
                Number = $"{P}TD-XD-004", Title = "Gói thầu bảo trì, thay thế hệ thống PCCC nhà xưởng khu A", PurchaseRequest = pr, Package = pkg, BuyerId = buyer.Id, Status = TenderStatus.Contracted,
                PackageBudget = 950_000_000, ContractType = "Trọn gói", ContractDurationDays = 60, TechnicalRequirements = "Thay thế đầu báo khói, bình chữa cháy, bơm PCCC; nghiệm thu với cơ quan PCCC.",
                QualificationCriteria = "Có giấy phép kinh doanh dịch vụ PCCC; ≥ 2 hợp đồng tương tự.", BidSecurityAmount = 9_000_000, BidValidityDays = 60,
                PublishedAt = signed.AddDays(-45).AddHours(9), ClosingAt = signed.AddDays(-25).AddHours(9), OpenedAt = signed.AddDays(-25).AddHours(10), CreatedAt = signed.AddDays(-50),
                AwardedVendorId = V(1).Id, AwardPrice = 900_000_000, AwardReason = "Giá đánh giá thấp nhất trong các HSDT đạt kỹ thuật.", ComplaintDeadline = signed.AddDays(-10)
            };
            t.Bidders.Add(new TenderBidder { VendorId = V(1).Id, RegisteredAt = t.PublishedAt.Value.AddDays(2), Submitted = true, SubmittedAt = t.ClosingAt.AddHours(-5), BidPrice = 900_000_000, VatPercent = 10, ValidityDays = 60, BidSecurityAmount = 9_000_000, Valid = true, Qualified = true });
            t.Bidders.Add(new TenderBidder { VendorId = V(2).Id, RegisteredAt = t.PublishedAt.Value.AddDays(3), Submitted = true, SubmittedAt = t.ClosingAt.AddHours(-3), BidPrice = 935_000_000, VatPercent = 10, ValidityDays = 60, BidSecurityAmount = 9_000_000, Valid = true, Qualified = true });
            db.Tenders.Add(t);
            var c = new Contract
            {
                Number = Marker, Title = "Hợp đồng bảo trì, thay thế hệ thống PCCC nhà xưởng khu A", Tender = t, VendorId = V(1).Id, Value = 900_000_000, VatPercent = 10,
                SignedDate = signed, StartDate = signed, EndDate = signed.AddDays(60), PaymentTerms = "Tạm ứng 20%; thanh toán 2 đợt theo nghiệm thu; giữ lại 5% đến hết bảo hành 12 tháng",
                PerformanceSecurityAmount = 45_000_000, PerformanceSecurityRef = "BLTH-PCCC-01", PerformanceSecurityExpiry = signed.AddDays(90), Status = ContractStatus.Liquidated,
                RetentionPercent = 5, RetentionCapPercent = 5, AdvanceRecoveryStartPercent = 0, AdvanceRecoveryEndPercent = 80, WarrantyEndDate = today.AddDays(-12),
                CreatedById = buyer.Id, CreatedAt = signed.AddHours(10), Terms = "Bảo hành 12 tháng kể từ nghiệm thu bàn giao."
            };
            c.Lines.Add(new ContractLine { Description = "Thay thế đầu báo khói, báo nhiệt", Unit = "Bộ", Quantity = 120, UnitPrice = 2_500_000, SortOrder = 1 });
            c.Lines.Add(new ContractLine { Description = "Bơm chữa cháy diesel 75 kW + tủ điều khiển", Unit = "Bộ", Quantity = 1, UnitPrice = 450_000_000, SortOrder = 2 });
            c.Lines.Add(new ContractLine { Description = "Nhân công lắp đặt, kiểm định, nghiệm thu PCCC", Unit = "Gói", Quantity = 1, UnitPrice = 150_000_000, SortOrder = 3 });
            db.Contracts.Add(c);
            var guarantees = new[]
            {
                new ContractGuarantee { Contract = c, Type = GuaranteeType.Advance, Amount = 198_000_000, Reference = "BLTU-PCCC-01", Bank = "Vietcombank", ExpiryDate = signed.AddDays(75), ReleasedAt = signed.AddDays(62) },
                new ContractGuarantee { Contract = c, Type = GuaranteeType.Warranty, Amount = 49_500_000, Reference = "BLBH-PCCC-01", Bank = "BIDV", ExpiryDate = today.AddDays(-10), ReleasedAt = today.AddDays(-8) },
            };
            db.ContractGuarantees.AddRange(guarantees);
            await db.SaveChangesAsync();

            var gross = ContractClose.Gross(c.Value, c.VatPercent);
            var ledger = new ContractLedger { ContractGross = gross };
            var payments = new List<Payment>();
            Payment Pay(string no, PaymentType type, decimal amount, decimal adv, decimal ret, DateTime at, string note, int? milestone = null)
            {
                var p = new Payment
                {
                    Number = $"{P}TT-XD-{no}", Type = type, VendorId = c.VendorId, ContractId = c.Id, Amount = amount, AdvanceDeduction = adv, RetentionAmount = ret, NetAmount = amount - adv - ret,
                    Note = note, Status = PaymentStatus.Paid, RequestedById = accountant.Id, RequestedAt = at, ApprovedById = director.Id, ApprovedAt = at.AddDays(1),
                    PaidById = accountant.Id, PaidAt = at.AddDays(3), PaymentRef = "UNC" + (60000 + payments.Count * 137), MilestoneId = milestone
                };
                db.Payments.Add(p); payments.Add(p);
                return p;
            }
            var mAdv = new ContractMilestone { ContractId = c.Id, Name = "Tạm ứng 20%", Type = PaymentType.Advance, Percent = 20, Amount = 198_000_000, DueDate = signed.AddDays(7), SortOrder = 1, Condition = "Nộp bảo lãnh tạm ứng" };
            var m1 = new ContractMilestone { ContractId = c.Id, Name = "Đợt 1 – hoàn thành thay thế thiết bị", Type = PaymentType.Milestone, Percent = 50, Amount = 495_000_000, DueDate = signed.AddDays(35), SortOrder = 2 };
            var m2 = new ContractMilestone { ContractId = c.Id, Name = "Đợt 2 – nghiệm thu PCCC, bàn giao", Type = PaymentType.Milestone, Percent = 50, Amount = 495_000_000, DueDate = signed.AddDays(60), SortOrder = 3 };
            db.ContractMilestones.AddRange(mAdv, m1, m2);
            await db.SaveChangesAsync();
            Pay("011", PaymentType.Advance, 198_000_000, 0, 0, signed.AddDays(5), $"Tạm ứng 20% (HĐ {c.Number})", mAdv.Id);
            ledger.AdvancePaid = 198_000_000;
            foreach (var (m, no, at) in new[] { (m1, "012", signed.AddDays(36)), (m2, "013", signed.AddDays(62)) })
            {
                var d = ContractClose.Deductions(c, m.Amount, ledger);
                Pay(no, PaymentType.Milestone, m.Amount, d.Advance, d.Retention, at, $"{m.Name} (50% HĐ {c.Number}) · {d.Note}", m.Id);
                ledger.WorkGross += m.Amount; ledger.AdvanceRecovered += d.Advance; ledger.Retained += d.Retention;
            }
            await db.SaveChangesAsync();
            var s = await ContractClose.DraftAsync(db, c, null, 0);
            s.Number = $"{P}QT-XD-001"; s.SettlementDate = signed.AddDays(75); s.Status = SettlementStatus.Approved; s.CreatedById = buyer.Id; s.CreatedAt = signed.AddDays(75);
            s.DecidedById = director.Id; s.DecidedAt = signed.AddDays(77); s.DecisionNote = "Đồng ý quyết toán."; s.Note = "Căn cứ biên bản nghiệm thu hoàn thành, nghiệm thu PCCC và hồ sơ hoàn công.";
            s.WarrantyEndDate = c.WarrantyEndDate;
            db.ContractSettlements.Add(s);
            await db.SaveChangesAsync();
            var release = Pay("014", PaymentType.RetentionRelease, s.RetentionHeld, 0, 0, today.AddDays(-9), $"Hoàn trả tiền giữ lại bảo hành – HĐ {c.Number} · hết bảo hành {c.WarrantyEndDate:dd/MM/yyyy}");
            s.LiquidatedAt = today.AddDays(-3).AddHours(14); s.LiquidationNote = "Hai bên hoàn thành nghĩa vụ; hệ thống PCCC vận hành ổn định trong thời gian bảo hành.";
            await db.SaveChangesAsync();

            Log(DocType.Package, () => pkg.Id, buyer, "Lập gói mua sắm", pkg.CreatedAt);
            Log(DocType.Tender, () => t.Id, director, "Giám đốc duyệt KH & HSMT – phát hành, đăng tải thông báo mời thầu", t.PublishedAt.Value);
            Log(DocType.Tender, () => t.Id, director, "Giám đốc phê duyệt kết quả lựa chọn nhà thầu – công khai kết quả", signed.AddDays(-15));
            Log(DocType.Contract, () => c.Id, buyer, "Ký hợp đồng", signed.AddHours(10), $"{c.Value.ToMoney()} đ (trước VAT)");
            Log(DocType.Contract, () => c.Id, buyer, "Hoàn thành hợp đồng – nghiệm thu bàn giao (chờ quyết toán, thanh lý)", signed.AddDays(61));
            Log(DocType.Contract, () => c.Id, buyer, $"Trình quyết toán {s.Number}: giá trị {s.FinalGross.ToMoney()} đ (gồm VAT), còn phải thanh toán {s.Balance.ToMoney()} đ", s.CreatedAt, s.Note);
            Log(DocType.Contract, () => c.Id, director, $"Giám đốc duyệt quyết toán {s.Number}", s.DecidedAt!.Value);
            Log(DocType.Contract, () => c.Id, accountant, $"Lập đề nghị hoàn trả tiền giữ lại {release.Number}: {release.Amount.ToMoney()} đ", release.RequestedAt);
            Log(DocType.Contract, () => c.Id, buyer, "Giải tỏa bảo lãnh bảo hành BLBH-PCCC-01", today.AddDays(-8));
            Log(DocType.Contract, () => c.Id, buyer, "Thanh lý hợp đồng – hai bên hoàn thành mọi nghĩa vụ", s.LiquidatedAt.Value, s.LiquidationNote);
            Log(DocType.Package, () => pkg.Id, buyer, "Gói hoàn tất – đã thanh lý hợp đồng", s.LiquidatedAt.Value.AddMinutes(5));
            foreach (var p in payments) Log(DocType.Payment, () => p.Id, accountant, $"Đã chi {p.NetAmount.ToMoney()} đ", p.PaidAt!.Value, p.Note);
            File(DocType.Package, () => pkg.Id, "To-trinh-phe-duyet-goi-PCCC.pdf", SampleFlows.Pdf("To trinh phe duyet goi mua sam bao tri he thong PCCC nha xuong khu A"), pkg.CreatedAt);
            File(DocType.Tender, () => t.Id, "HSMT-PCCC.pdf", SampleFlows.Pdf("Ho so moi thau goi bao tri he thong PCCC – yeu cau ky thuat, tieu chi danh gia"), t.PublishedAt.Value);
            File(DocType.Contract, () => c.Id, "Hop-dong-PCCC-da-ky.docx", SampleFlows.Docx("HỢP ĐỒNG BẢO TRÌ HỆ THỐNG PCCC", new[] { $"Số: {c.Number}", "Giá trị: 900.000.000 đ (trước VAT)", "Tạm ứng 20%, giữ lại 5% đến hết bảo hành." }), signed);
            File(DocType.Contract, () => c.Id, "Bien-ban-quyet-toan-thanh-ly.docx", SampleFlows.Docx("BIÊN BẢN QUYẾT TOÁN VÀ THANH LÝ HỢP ĐỒNG", new[] { $"Số: {s.Number}", $"Giá trị quyết toán: {s.FinalGross.ToMoney()} đ", "Hai bên thống nhất thanh lý hợp đồng." }), s.LiquidatedAt.Value);
            done.Add($"hợp đồng {c.Number}: tạm ứng, 2 đợt thanh toán có giữ lại + thu hồi tạm ứng, quyết toán {s.Number}, hoàn trả giữ lại, giải tỏa bảo lãnh, thanh lý; gói hoàn tất có file đính kèm (lưu trữ)");
        }

        // =============================================================== 3. Gói thầu đang đánh giá: yêu cầu năng lực, liên danh, thầu phụ, ưu đãi
        {
            var pr = NewPr("004", "Thi công hệ thống thoát nước, sân đường nội bộ khu B", PrStatus.Processing, today.AddDays(-45), 15_000_000_000);
            var pkg = NewPkg("004", "Thi công hệ thống thoát nước, sân đường nội bộ khu B", "Xây lắp", ProcurementMethod.OpenTender, 15_000_000_000, PackageStatus.Executing, today.AddDays(-40), pr, "Mẫu: đánh giá năng lực có cấu trúc, liên danh, ưu đãi");
            pkg.RiskChecklist = "scope"; pkg.RiskMitigation = "Khảo sát bổ sung hiện trạng cống; dự phòng phát sinh 5% trong dự toán."; pkg.RiskAssessedAt = today.AddDays(-38); pkg.RiskAssessedById = buyer.Id;
            var t = new Tender
            {
                Number = $"{P}TD-XD-002", Title = "Gói thầu thi công thoát nước, sân đường nội bộ khu B", PurchaseRequest = pr, Package = pkg, BuyerId = buyer.Id, Status = TenderStatus.Opened,
                PackageBudget = 15_000_000_000, ContractType = "Đơn giá cố định", ContractDurationDays = 150, AllowJointVenture = true, MaxSubcontractPercent = 30, DomesticPreferencePercent = 7.5m,
                TechnicalRequirements = "Thi công theo bản vẽ thiết kế; biện pháp đảm bảo giao thông nội bộ; vật liệu có chứng chỉ chất lượng.",
                QualificationCriteria = "Theo bảng yêu cầu năng lực có cấu trúc (hợp đồng tương tự, doanh thu, tài chính, nhân sự, thiết bị).",
                ContractTerms = "Tạm ứng 20%; thanh toán theo khối lượng; giữ lại 5%.", EvaluationMethod = TenderEvaluationMethod.LowestEvaluatedPrice, MinTechnicalScore = 70,
                BidSecurityAmount = 150_000_000, BidValidityDays = 90, PerformanceSecurityPercent = 5, CreatedAt = today.AddDays(-30),
                PublishedAt = today.AddDays(-21).AddHours(9), ClosingAt = today.AddDays(-6).AddHours(9), OpenedAt = today.AddDays(-6).AddHours(10)
            };
            var reqs = new[]
            {
                (QualificationKind.SimilarContracts, "Công trình hạ tầng kỹ thuật tương tự trong 5 năm gần đây", 2, 7_000_000_000m, 0),
                (QualificationKind.AverageRevenue, "Doanh thu xây dựng bình quân 3 năm gần nhất", 0, 22_000_000_000m, 0),
                (QualificationKind.FinancialResources, "Nguồn lực tài chính cho gói thầu (tiền mặt, hạn mức tín dụng)", 0, 3_000_000_000m, 0),
                (QualificationKind.KeyPersonnel, "Chỉ huy trưởng công trường hạng II trở lên", 1, 0m, 5),
                (QualificationKind.KeyPersonnel, "Kỹ sư giám sát kỹ thuật hạ tầng", 2, 0m, 3),
                (QualificationKind.Equipment, "Máy đào dung tích gầu ≥ 0,8 m3", 2, 0m, 0),
                (QualificationKind.Other, "Biện pháp thi công đảm bảo giao thông nội bộ, an toàn lao động", 0, 0m, 0)
            }.Select((x, i) => new TenderRequirement { Kind = x.Item1, Name = x.Item2, MinCount = x.Item3, MinValue = x.Item4, MinYears = x.Item5, SortOrder = i + 1 }).ToList();
            foreach (var r in reqs) t.Requirements.Add(r);
            for (var i = 0; i < 3; i++) t.Committee.Add(new TenderCommitteeMember { UserId = committee[i].Id, IsLeader = i == 0, ConflictDeclaredAt = t.ClosingAt.AddHours(-2) });
            t.Clarifications.Add(new TenderClarification { IsAmendment = false, Question = "Có cho phép sử dụng ống HDPE thay ống BTCT D600 không?", Answer = "Không; thực hiện đúng chủng loại vật liệu trong hồ sơ thiết kế.", CreatedById = buyer.Id, CreatedAt = today.AddDays(-17) });
            t.Clarifications.Add(new TenderClarification { IsAmendment = true, Answer = "Sửa đổi HSMT: bổ sung hạng mục hố ga thu nước mặt đường (12 cái) vào bảng khối lượng.", CreatedById = buyer.Id, CreatedAt = today.AddDays(-14) });
            TenderBidder Bid(Vendor v, decimal price, int regDay, bool pref = false, string? prefNote = null) =>
                new() { VendorId = v.Id, RegisteredAt = t.PublishedAt.Value.AddDays(regDay), Submitted = true, SubmittedAt = t.ClosingAt.AddHours(-(10 + regDay)), BidPrice = price, VatPercent = 10, LeadTimeDays = 150,
                        ValidityDays = 90, BidSecurityAmount = 150_000_000, BidSecurityRef = $"BLDT-{v.Code}-{today:yy}", BidSecurityExpiry = t.ClosingAt.Date.AddDays(120), DomesticPreferred = pref, PreferenceNote = prefNote };
            var bA = Bid(V(0), 14_200_000_000, 1); var bB = Bid(V(1), 13_900_000_000, 2); var bC = Bid(V(2), 14_600_000_000, 3, true, "Nhà thầu trong nước; vật tư chính sản xuất trong nước theo kê khai HSDT (minh họa).");
            foreach (var b in new[] { bA, bB, bC }) t.Bidders.Add(b);
            db.Tenders.Add(t);
            await db.SaveChangesAsync();
            // Liên danh A (60/40) và thầu phụ của B (35% > mức tối đa 30%)
            var leadA = new TenderBidderPartner { TenderBidderId = bA.Id, VendorId = V(0).Id, Name = V(0).Name, TaxCode = V(0).TaxCode, Role = BidPartnerRole.Lead, SharePercent = 60, WorkScope = "Thoát nước, hố ga", SortOrder = 1 };
            var jv = vendors.Count > 3 ? V(3) : null;
            var memA = new TenderBidderPartner { TenderBidderId = bA.Id, VendorId = jv?.Id, Name = jv?.Name ?? "Công ty CP Xây dựng Hạ tầng Phương Nam", TaxCode = jv?.TaxCode ?? "0312345987", Role = BidPartnerRole.Member, SharePercent = 40, WorkScope = "Sân đường bê tông nhựa", SortOrder = 2 };
            var subB = new TenderBidderPartner { TenderBidderId = bB.Id, Name = "Công ty TNHH Cơ giới Đông Á", TaxCode = "0316789123", Role = BidPartnerRole.Subcontractor, SharePercent = 35, WorkScope = "Đào đắp, vận chuyển đất", SortOrder = 1 };
            db.TenderBidderPartners.AddRange(leadA, memA, subB);
            await db.SaveChangesAsync();
            TenderBidderCapability Cap(TenderBidder b, int req, decimal value = 0, int years = 0, int count = 1, string? detail = null, TenderBidderPartner? by = null) =>
                new() { TenderBidderId = b.Id, RequirementId = reqs[req].Id, PartnerId = by?.Id, Value = value, Years = years, Count = count, Detail = detail };
            db.TenderBidderCapabilities.AddRange(
                // A – liên danh: đạt khi cộng gộp
                Cap(bA, 0, 9_000_000_000, detail: "HĐ 12/2023 – Hạ tầng KCN Tân Phú", by: leadA), Cap(bA, 0, 7_500_000_000, detail: "HĐ 05/2022 – Đường nội bộ khu dân cư", by: memA),
                Cap(bA, 1, 15_000_000_000, detail: "BCTC kiểm toán 2023–2025", by: leadA), Cap(bA, 1, 10_000_000_000, detail: "BCTC kiểm toán 2023–2025", by: memA),
                Cap(bA, 2, 2_000_000_000, detail: "Cam kết tín dụng ngân hàng", by: leadA), Cap(bA, 2, 1_500_000_000, detail: "Tiền gửi", by: memA),
                Cap(bA, 3, years: 8, detail: "Nguyễn Văn Hùng – CHT hạng II"), Cap(bA, 4, years: 6, detail: "Trần Minh Đức – KS cầu đường"), Cap(bA, 4, years: 4, detail: "Lê Thu Trang – KS cấp thoát nước"),
                Cap(bA, 5, count: 1, detail: "Komatsu PC200 – sở hữu", by: leadA), Cap(bA, 5, count: 2, detail: "Hitachi ZX200 – sở hữu", by: memA),
                Cap(bA, 6, detail: "Phân luồng 2 giai đoạn, biển báo, người điều tiết"),
                // B – không đạt: thiếu 1 HĐ tương tự đủ giá trị, thiếu kỹ sư đủ kinh nghiệm
                Cap(bB, 0, 8_000_000_000, detail: "HĐ 02/2024 – Hạ tầng cụm công nghiệp"), Cap(bB, 0, 5_000_000_000, detail: "HĐ 09/2021 – Đường nội bộ nhà máy"),
                Cap(bB, 1, 24_000_000_000, detail: "BCTC 2023–2025"), Cap(bB, 2, 3_200_000_000, detail: "Hạn mức tín dụng"),
                Cap(bB, 3, years: 6, detail: "Phạm Quốc Bảo – CHT hạng II"), Cap(bB, 4, years: 2, detail: "Võ Hoài Nam"), Cap(bB, 4, years: 4, detail: "Đặng Thị Lan"),
                Cap(bB, 5, count: 2, detail: "Doosan DX225 – thuê"), Cap(bB, 6, detail: "Biện pháp sơ sài, chưa có phân luồng"),
                // C – đạt, được ưu đãi
                Cap(bC, 0, 8_200_000_000, detail: "HĐ 07/2024 – Thoát nước KCN"), Cap(bC, 0, 7_100_000_000, detail: "HĐ 11/2022 – Sân bãi kho vận"),
                Cap(bC, 1, 23_000_000_000, detail: "BCTC kiểm toán"), Cap(bC, 2, 4_000_000_000, detail: "Cam kết tín dụng"),
                Cap(bC, 3, years: 7, detail: "Hoàng Văn Sơn – CHT hạng I"), Cap(bC, 4, years: 3, detail: "Ngô Đức Anh"), Cap(bC, 4, years: 5, detail: "Bùi Thị Hạnh"),
                Cap(bC, 5, count: 3, detail: "Komatsu PC210 – sở hữu"), Cap(bC, 6, detail: "Thi công cuốn chiếu, phân luồng theo ca"));
            await db.SaveChangesAsync();
            Log(DocType.Package, () => pkg.Id, buyer, "Đánh giá rủi ro: 10 điểm – mức Thấp", pkg.RiskAssessedAt!.Value, pkg.RiskMitigation);
            Log(DocType.Tender, () => t.Id, director, "Giám đốc duyệt KH & HSMT – phát hành, đăng tải thông báo mời thầu", t.PublishedAt.Value);
            Log(DocType.Tender, () => t.Id, buyer, "Sửa đổi HSMT", today.AddDays(-14), "Bổ sung hạng mục hố ga thu nước mặt đường");
            Log(DocType.Tender, () => t.Id, buyer, "Mở thầu – 3 HSDT", t.OpenedAt.Value);
            Log(DocType.Tender, () => t.Id, buyer, $"{V(0).Name}: kê khai liên danh với {memA.Name} (60% / 40%)", t.OpenedAt.Value.AddHours(2));
            done.Add($"gói thầu {t.Number} đang đánh giá: 7 yêu cầu năng lực có cấu trúc, liên danh, thầu phụ vượt mức, nhà thầu được ưu đãi 7,5%, sửa đổi HSMT đúng mốc");
        }

        // =============================================================== 4. HSMT nháp đặt thời điểm đóng thầu quá sớm (vi phạm mốc thời gian)
        {
            var pr = NewPr("005", "Mua sắm, lắp đặt hệ thống điều hòa trung tâm VRV khu văn phòng", PrStatus.Processing, today.AddDays(-20), 12_000_000_000);
            var pkg = NewPkg("005", "Mua sắm, lắp đặt hệ thống điều hòa trung tâm VRV khu văn phòng", "Hàng hóa", ProcurementMethod.OpenTender, 12_000_000_000, PackageStatus.Executing, today.AddDays(-18), pr, "Mẫu: HSMT vi phạm mốc thời gian theo luật");
            pkg.RiskChecklist = "brand"; pkg.RiskMitigation = "Thay tên hãng bằng thông số kỹ thuật tương đương; chấp nhận thiết bị của ít nhất 3 hãng."; pkg.RiskAssessedAt = today.AddDays(-17); pkg.RiskAssessedById = buyer.Id;
            var t = new Tender
            {
                Number = $"{P}TD-XD-003", Title = "Gói thầu mua sắm, lắp đặt điều hòa trung tâm VRV", PurchaseRequest = pr, Package = pkg, BuyerId = buyer.Id, Status = TenderStatus.Draft,
                PackageBudget = 12_000_000_000, ContractType = "Trọn gói", ContractDurationDays = 90, TechnicalRequirements = "Hệ thống VRV công suất 180 HP, hiệu suất năng lượng cao, bảo hành 36 tháng.",
                QualificationCriteria = "Theo bảng yêu cầu năng lực có cấu trúc.", BidSecurityAmount = 120_000_000, BidValidityDays = 90, CreatedAt = today.AddDays(-15),
                ClosingAt = today.AddDays(8).AddHours(9), DomesticPreferencePercent = 0
            };
            t.Requirements.Add(new TenderRequirement { Kind = QualificationKind.SimilarContracts, Name = "Hợp đồng cung cấp, lắp đặt hệ thống điều hòa trung tâm", MinCount = 2, MinValue = 6_000_000_000, SortOrder = 1 });
            t.Requirements.Add(new TenderRequirement { Kind = QualificationKind.Other, Name = "Ủy quyền của nhà sản xuất và cam kết bảo hành chính hãng", SortOrder = 2 });
            for (var i = 0; i < 3; i++) t.Committee.Add(new TenderCommitteeMember { UserId = committee[i].Id, IsLeader = i == 0 });
            db.Tenders.Add(t);
            await db.SaveChangesAsync();
            Log(DocType.Tender, () => t.Id, buyer, "Lập HSMT theo gói " + pkg.Number, t.CreatedAt, "Đóng thầu dự kiến sau 8 ngày – chưa đủ thời gian chuẩn bị HSDT tối thiểu theo luật");
            done.Add($"HSMT nháp {t.Number}: thời điểm đóng thầu chưa đủ 18 ngày (hàng hóa > 10 tỷ) – bị chặn khi trình");
        }

        // =============================================================== 5. Gói rủi ro cao: sát hạn mức chỉ định + chia nhỏ, trong KHLCNT nháp
        {
            var pr6 = NewPr("006", "Mua bổ sung máy in, máy quét cho văn phòng khu B", PrStatus.Approved, today.AddDays(-6), 95_000_000, method: ProcurementMethod.DirectAward);
            var pr7 = NewPr("007", "Mua máy in khu B (đợt 2)", PrStatus.Approved, today.AddDays(-4), 60_000_000, method: ProcurementMethod.DirectAward);
            var p6 = NewPkg("006", "Mua bổ sung máy in, máy quét văn phòng khu B", "Hàng hóa", ProcurementMethod.DirectAward, 95_000_000, PackageStatus.Draft, today.AddDays(-5), pr6, "Mẫu: rủi ro cao – sát hạn mức chỉ định thầu");
            var p7 = NewPkg("007", "Mua máy in văn phòng khu B (đợt 2)", "Hàng hóa", ProcurementMethod.DirectAward, 60_000_000, PackageStatus.Draft, today.AddDays(-3), pr7, "Mẫu: rủi ro cao – dấu hiệu chia nhỏ gói");
            p6.RiskChecklist = "urgent"; p6.RiskAssessedAt = today.AddDays(-2); p6.RiskAssessedById = buyer.Id;
            var plan = new SelectionPlan { Number = $"{P}KH-XD-002", Title = "KHLCNT bổ sung thiết bị văn phòng khu B", Basis = "Nhu cầu phát sinh của khu B.", Status = SelectionPlanStatus.Draft, CreatedById = buyer.Id, CreatedAt = today.AddDays(-2) };
            plan.Packages.Add(p6); plan.Packages.Add(p7);
            db.SelectionPlans.Add(plan);
            await db.SaveChangesAsync();
            done.Add($"KHLCNT nháp {plan.Number} với 2 gói chỉ định thầu rủi ro cao (sát hạn mức, chia nhỏ gói) – bị chặn khi trình nếu chưa có biện pháp kiểm soát");
        }

        // =============================================================== 6. Hồ sơ cũ trong sổ lưu trữ: sắp hết / đã hết thời hạn lưu trữ
        foreach (var (suffix, title, created, closed) in new[]
                 {
                     ("015", "Mua sắm máy tính văn phòng năm 2015", new DateTime(2015, 3, 10), new DateTime(2015, 6, 18)),
                     ("016", "Mua sắm bàn ghế phòng họp năm 2016", new DateTime(2016, 10, 5), new DateTime(2017, 3, 15))
                 })
        {
            var pr = NewPr(suffix, title, PrStatus.Completed, created, 420_000_000, "Phòng Hành chính", ProcurementMethod.CompetitiveQuotation);
            var pkg = NewPkg(suffix, title, "Hàng hóa", ProcurementMethod.CompetitiveQuotation, 420_000_000, PackageStatus.Completed, created.AddDays(2), pr, "Mẫu: hồ sơ cũ trong sổ lưu trữ");
            await db.SaveChangesAsync();
            Log(DocType.Package, () => pkg.Id, buyer, "Lập gói mua sắm", pkg.CreatedAt);
            Log(DocType.Package, () => pkg.Id, buyer, "Gói hoàn tất – đã nghiệm thu, thanh toán", closed);
            File(DocType.Package, () => pkg.Id, $"Ho-so-goi-{suffix}.pdf", SampleFlows.Pdf($"Ho so goi mua sam {title}"), closed);
        }
        done.Add("2 hồ sơ gói cũ (2015, 2017) trong sổ lưu trữ: đã hết / sắp hết thời hạn lưu trữ");

        // =============================================================== 7. Phân tích đơn giá theo định mức trên BOQ gói xây dựng nháp
        var pkgA = await db.ProcurementPackages.FirstOrDefaultAsync(p => p.Number == SampleConstruction.PackageA);
        if (pkgA != null)
        {
            var items = await db.BoqItems.Where(i => i.PackageId == pkgA.Id).ToListAsync();
            BoqNormLine N(int k, string code, string name, string unit, decimal norm, decimal price) => new() { Kind = k, Code = code, Name = name, Unit = unit, Norm = norm, Price = price };
            var analyses = new Dictionary<string, List<BoqNormLine>>
            {
                ["Đào móng bằng máy đào"] = new() { N(2, "NC3.0", "Nhân công bậc 3,0/7", "công", 0.06m, 300_000), N(3, "M.DAO08", "Máy đào ≤ 0,8 m3", "ca", 0.0125m, 3_360_000) },
                ["Bê tông lót M100 đá 4x6"] = new() { N(1, "VL.XM30", "Xi măng PCB30", "kg", 200, 1_600), N(1, "VL.CAT", "Cát vàng", "m3", 0.53m, 450_000), N(1, "VL.DA46", "Đá 4x6", "m3", 0.94m, 420_000),
                    N(1, "VL.NUOC", "Nước", "lít", 170, 15), N(2, "NC3.0", "Nhân công bậc 3,0/7", "công", 0.7m, 300_000), N(3, "M.TRON250", "Máy trộn bê tông 250 lít", "ca", 0.095m, 350_000), N(3, "M.DAMBAN", "Máy đầm bàn 1 kW", "ca", 0.089m, 245_000) },
                ["Xây tường gạch 220 vữa XM M75"] = new() { N(1, "VL.GACH", "Gạch đặc 6,5x10,5x22", "viên", 550, 1_500), N(1, "VL.XM30", "Xi măng PCB30", "kg", 76, 1_600), N(1, "VL.CAT", "Cát vàng", "m3", 0.32m, 450_000),
                    N(1, "VL.NUOC", "Nước", "lít", 75, 15), N(2, "NC3.5", "Nhân công bậc 3,5/7", "công", 1.67m, 310_000), N(3, "M.TRON80", "Máy trộn vữa 80 lít", "ca", 0.036m, 300_000), N(3, "M.VTHANG", "Vận thăng 0,8 tấn", "ca", 0.04m, 600_000) }
            };
            var applied = 0;
            foreach (var it in items.Where(i => i.Norms == null && analyses.ContainsKey(i.Name)))
            {
                var lines = analyses[it.Name];
                it.Norms = BoqNorms.Serialize(lines);
                var (m, l, mc) = BoqNorms.Components(lines);
                it.MaterialPrice = m; it.LaborPrice = l; it.MachinePrice = mc; it.UnitPrice = m + l + mc;
                applied++;
            }
            if (applied > 0)
            {
                pkgA.Budget = R(BoqCost.Summary(items, pkgA.BoqOverheadPercent, pkgA.BoqProfitPercent, pkgA.BoqContingencyPercent, pkgA.BoqVatPercent).TotalExVat);
                Log(DocType.Package, () => pkgA.Id, buyer, $"Phân tích đơn giá theo định mức cho {applied} dòng BOQ", today.AddDays(-10));
                done.Add($"phân tích đơn giá theo định mức cho {applied} dòng BOQ gói {pkgA.Number}");
            }
            await db.SaveChangesAsync();
        }

        // =============================================================== 8. Chứng nhận môi trường / xã hội của NCC (ESG)
        {
            var esg = new[]
            {
                (V(0), VendorDocumentType.Environmental, "ISO 14001:2015 – QMS-EN-2318", today.AddYears(-1), today.AddYears(2)),
                (V(0), VendorDocumentType.SocialResponsibility, "ISO 45001:2018 – OHS-7781", today.AddMonths(-8), today.AddYears(2)),
                (V(1), VendorDocumentType.Environmental, "Nhãn sinh thái Việt Nam – NST-0452", today.AddYears(-2), today.AddDays(45)),
                (V(2), VendorDocumentType.Environmental, "ISO 14001:2015 – EN-1120", today.AddYears(-4), today.AddDays(-30)),
                (V(3), VendorDocumentType.SocialResponsibility, "SA8000 – SA-3390", today.AddMonths(-3), today.AddYears(3)),
            };
            foreach (var (v, type, no, issued, exp) in esg)
            {
                var data = SampleFlows.Pdf($"Chung nhan {no}");
                db.VendorDocuments.Add(new VendorDocument
                {
                    VendorId = v.Id, Type = type, Number = no, IssueDate = issued, ExpiryDate = exp, FileName = $"chung-nhan-{no.Split(' ')[0].ToLower()}.pdf", ContentType = "application/pdf",
                    Data = data, Size = data.Length, Status = VendorDocumentStatus.Verified, Note = SampleFlows.Tag + " Chứng nhận ESG mẫu", UploadedAt = issued, VerifiedById = admin.Id, VerifiedAt = issued.AddDays(3)
                });
            }
            await db.SaveChangesAsync();
            done.Add($"{esg.Length} chứng nhận môi trường / xã hội của NCC (1 sắp hết hạn, 1 đã hết hạn)");
        }

        foreach (var l in logs)
        {
            // Action tối đa 100 ký tự: phần thừa chuyển sang Comment (như ProcControllerBase.Log)
            var (action, comment) = (l.Action, l.Comment);
            if (action.Length > 100) { comment = "…" + action[99..] + (string.IsNullOrWhiteSpace(comment) ? "" : " | " + comment); action = action[..99] + "…"; }
            if (comment?.Length > 1000) comment = comment[..999] + "…";
            db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = l.Type, DocumentId = l.Id(), UserId = l.User.Id, Action = action, Comment = comment, CreatedAt = l.At });
        }
        foreach (var f in files)
            db.Attachments.Add(new Attachment { DocumentType = f.Type, DocumentId = f.Id(), FileName = f.Name, ContentType = f.Ct, Data = f.Data, Size = f.Data.Length, UploadedById = buyer.Id, UploadedAt = f.At, Note = SampleFlows.Tag });
        await db.SaveChangesAsync();
        return done;
    }

    /// <summary>
    /// Gỡ dữ liệu của các tính năng nâng cao gắn với chứng từ mẫu (MAU-…): quyết toán, điều chỉnh giá, công thức chỉ số, liên danh, kê khai năng lực, yêu cầu năng lực,
    /// làm rõ HSMT của gói thầu xây dựng mẫu, file đính kèm mẫu. Gọi trước SampleConstruction.RemoveAsync.
    /// </summary>
    public static async Task RemoveAsync(EProcDbContext db)
    {
        var contractIds = await db.Contracts.Where(c => c.Number.StartsWith(P)).Select(c => c.Id).ToListAsync();
        var tenderIds = await db.Tenders.Where(t => t.Number.StartsWith(P)).Select(t => t.Id).ToListAsync();
        await db.Payments.Where(p => p.ContractId != null && contractIds.Contains(p.ContractId.Value) && (p.SettlementId != null || p.PriceAdjustmentId != null))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.SettlementId, (int?)null).SetProperty(p => p.PriceAdjustmentId, (int?)null));
        await db.ContractSettlements.Where(s => contractIds.Contains(s.ContractId)).ExecuteDeleteAsync();
        await db.ContractPriceAdjustments.Where(a => contractIds.Contains(a.ContractId)).ExecuteDeleteAsync();
        await db.ContractPriceIndices.Where(i => contractIds.Contains(i.ContractId)).ExecuteDeleteAsync();
        var bidderIds = await db.TenderBidders.Where(b => tenderIds.Contains(b.TenderId)).Select(b => b.Id).ToListAsync();
        await db.TenderBidderCapabilities.Where(c => bidderIds.Contains(c.TenderBidderId)).ExecuteDeleteAsync();
        await db.TenderBidderPartners.Where(p => bidderIds.Contains(p.TenderBidderId)).ExecuteDeleteAsync();
        await db.TenderRequirements.Where(r => tenderIds.Contains(r.TenderId)).ExecuteDeleteAsync();
        var xdTenders = await db.Tenders.Where(t => t.Number.StartsWith(P + "TD-XD")).Select(t => t.Id).ToListAsync();
        await db.TenderClarifications.Where(c => xdTenders.Contains(c.TenderId)).ExecuteDeleteAsync();
        await db.Attachments.Where(a => a.Note == SampleFlows.Tag).ExecuteDeleteAsync();
        // Gói nâng cao (khác gói A, B của dữ liệu xây dựng) được SampleConstruction.RemoveAsync gỡ theo tiền tố GM-XD / TD-XD / HD-XD / PR-XD / KH-XD
    }
}
