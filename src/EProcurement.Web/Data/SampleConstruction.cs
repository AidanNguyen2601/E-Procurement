using EProcurement.Web.Controllers;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Data;

/// <summary>
/// Dữ liệu mẫu ngành xây dựng (tiền tố MAU-…-XD-): một gói NHÁP đang lập BOQ (cơ cấu giá, chi phí chung / lợi nhuận, 2 phiên bản)
/// và một gói đã đấu thầu theo BOQ → 3 nhà thầu chào giá từng dòng → hợp đồng theo BOQ → tạm ứng → 3 đợt nghiệm thu khối lượng
/// (đã chi / chờ duyệt thanh toán / nháp) → phụ lục khối lượng phát sinh đã duyệt.
/// </summary>
public static class SampleConstruction
{
    private const string P = SampleDataSeeder.Prefix;
    public const string PackageA = P + "GM-XD-001", PackageB = P + "GM-XD-002";

    public static Task<bool> ExistsAsync(EProcDbContext db) => db.ProcurementPackages.AnyAsync(p => p.Number.StartsWith(P + "GM-XD"));

    private static decimal R(decimal v) => Math.Round(v / 1000m) * 1000m;

    /// <summary>Trả về mô tả những gì đã nạp (rỗng nếu đã có). Gọi trong giao dịch của người gọi.</summary>
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
        var vendors = await db.Vendors.Where(v => v.Status == VendorStatus.Approved).OrderBy(v => v.Code).Take(3).ToListAsync();
        if (vendors.Count < 3) throw new InvalidOperationException("Cần có ít nhất 3 nhà cung cấp đã duyệt (nạp dữ liệu mẫu chính trước).");
        var committeeUsers = users.Where(x => x.Id != director.Id && x.Id != buyer.Id && x.Role != AppRoles.Admin).Take(3).ToList();
        if (committeeUsers.Count < 3) committeeUsers = users.Where(x => x.Id != director.Id).Take(3).ToList();
        var today = DateTime.Today; var now = DateTime.Now;
        var rnd = new Random(77);
        var logs = new List<(DocType Type, Func<int> Id, AppUser User, string Action, DateTime At, string? Comment)>();
        void Log(DocType t, Func<int> id, AppUser u, string action, DateTime at, string? comment = null) => logs.Add((t, id, u, action, at, comment));

        var (rows, errors) = Boq.Parse(BoqController.SampleConstructionText);
        if (errors.Count > 0) throw new InvalidOperationException("BOQ mẫu không hợp lệ.");

        List<BoqItem> BuildBoq(int packageId)
        {
            var (built, _) = Boq.Build(packageId, rows);
            var byCode = new Dictionary<string, BoqItem>();
            foreach (var (item, parentCode) in built) { if (parentCode != null) item.Parent = byCode[parentCode]; byCode[item.Code] = item; db.BoqItems.Add(item); }
            return built.Select(b => b.Item).ToList();
        }

        PurchaseRequest NewPr(string suffix, string title, PrStatus st, int daysAgo, decimal estimate)
        {
            var pr = new PurchaseRequest
            {
                Number = $"{P}PR-XD-{suffix}", Title = title, RequesterId = requester.Id, Department = "Phòng Kỹ thuật", RequestDate = today.AddDays(-daysAgo), NeededDate = today.AddDays(120),
                Priority = PrPriority.Normal, Purpose = "Xây dựng cơ sở hạ tầng phục vụ mở rộng kho vận.", Status = st, EstimatedTotal = estimate, SubmittedAt = today.AddDays(-daysAgo).AddHours(9),
                Method = ProcurementMethod.OpenTender, MethodReason = "Giá trị lớn, công trình xây lắp: đấu thầu rộng rãi theo BOQ.", CreatedAt = today.AddDays(-daysAgo)
            };
            var s = 1;
            foreach (var g in rows.Where(r => !r.Code.Contains('.')))
            {
                var amount = rows.Where(r => r.Code.StartsWith(g.Code + ".")).Sum(r => r.Quantity * r.UnitPrice);
                pr.Lines.Add(new PurchaseRequestLine { Description = g.Name, Unit = "Hạng mục", Quantity = 1, EstimatedPrice = amount, Amount = amount, SortOrder = s++ });
            }
            db.PurchaseRequests.Add(pr);
            return pr;
        }
        var directTotal = rows.Sum(r => r.Quantity * r.UnitPrice);

        // =============================================================== Gói A: nháp, đang lập BOQ
        var prA = NewPr("001", "Xây dựng nhà kho lạnh khu B – lập dự toán", PrStatus.Approved, 20, directTotal);
        var pkgA = new ProcurementPackage
        {
            Number = PackageA, Title = "Xây dựng nhà kho lạnh khu B – hạng mục móng, thân, hoàn thiện", Status = PackageStatus.Draft, Method = ProcurementMethod.OpenTender,
            MethodReason = "Gói xây lắp giá trị lớn: đấu thầu rộng rãi.", EnvelopeMethod = EnvelopeMethod.OneEnvelope, ContractType = "Đơn giá cố định", ContractDurationDays = 120,
            PlannedStart = today.AddDays(30), BuyerId = buyer.Id, Team = "Tổ dự án xây dựng", CreatedAt = today.AddDays(-18), FundingSource = "Vốn đầu tư phát triển",
            BoqOverheadPercent = 6.5m, BoqProfitPercent = 5.5m, BoqContingencyPercent = 3, BoqVatPercent = 10, Note = "Mẫu: gói xây dựng đang lập BOQ"
        };
        pkgA.PurchaseRequests.Add(prA);
        db.ProcurementPackages.Add(pkgA);
        await db.SaveChangesAsync();
        var boqA = BuildBoq(pkgA.Id);
        await db.SaveChangesAsync();
        Boq.Renumber(boqA); await db.SaveChangesAsync();
        // v1: BOQ dự toán ban đầu; sau đó chỉnh theo khảo sát hiện trường → v2; BOQ hiện tại khác cả hai
        await BoqVersions.SnapshotAsync(db, pkgA.Id, "BOQ dự toán ban đầu", buyer.Id); await db.SaveChangesAsync();
        var concrete = boqA.First(b => b.Name.StartsWith("Bê tông móng")); concrete.Quantity += 12; concrete.Note = "Tăng theo kết quả khảo sát địa chất";
        var wall = boqA.First(b => b.Name.StartsWith("Xây tường gạch")); wall.Quantity -= 10;
        await db.SaveChangesAsync();
        await BoqVersions.SnapshotAsync(db, pkgA.Id, "Điều chỉnh sau khảo sát địa chất", buyer.Id); await db.SaveChangesAsync();
        var paint = boqA.First(b => b.Name.StartsWith("Sơn nước")); paint.MaterialPrice += 4000; paint.UnitPrice += 4000;
        var extra = new BoqItem { PackageId = pkgA.Id, Name = "Chống thấm sàn mái", Unit = "m2", Quantity = 320, UnitPrice = 185000, MaterialPrice = 120000, LaborPrice = 55000, MachinePrice = 10000, SortOrder = 99, Note = "Bổ sung theo yêu cầu thiết kế" };
        db.BoqItems.Add(extra);
        await db.SaveChangesAsync();
        var currentA = await db.BoqItems.Where(i => i.PackageId == pkgA.Id).ToListAsync();
        Boq.Renumber(currentA);
        pkgA.Budget = R(BoqCost.Summary(currentA, pkgA.BoqOverheadPercent, pkgA.BoqProfitPercent, pkgA.BoqContingencyPercent, pkgA.BoqVatPercent).TotalExVat); // giá gói khớp giá trị BOQ
        await db.SaveChangesAsync();
        Log(DocType.Package, () => pkgA.Id, buyer, "Nhập BOQ", today.AddDays(-17).AddHours(10), $"{rows.Count} dòng, tổng {directTotal.ToMoney()} đ");
        done.Add($"gói xây dựng nháp có BOQ {rows.Count + 1} dòng, cơ cấu chi phí và 2 phiên bản");

        // =============================================================== Gói B: đã đấu thầu theo BOQ → hợp đồng
        var prB = NewPr("002", "Xây dựng nhà xưởng đóng gói khu A", PrStatus.Processing, 140, directTotal * 1.02m);
        var plan = new SelectionPlan
        {
            Number = $"{P}KH-XD-001", Title = "Kế hoạch lựa chọn nhà thầu – công trình xây dựng khu A", Basis = "Kế hoạch đầu tư xây dựng năm; thiết kế bản vẽ thi công được duyệt.",
            Status = SelectionPlanStatus.Approved, CreatedById = buyer.Id, CreatedAt = today.AddDays(-135), SubmittedAt = today.AddDays(-134), DecidedById = director.Id, DecidedAt = today.AddDays(-132), DecisionNote = "Đồng ý."
        };
        var pkgB = new ProcurementPackage
        {
            Number = PackageB, Title = "Xây dựng nhà xưởng đóng gói khu A", Status = PackageStatus.Executing, Method = ProcurementMethod.OpenTender, MethodReason = "Gói xây lắp giá trị lớn.",
            EnvelopeMethod = EnvelopeMethod.OneEnvelope, ContractType = "Đơn giá cố định", ContractDurationDays = 180, PlannedStart = today.AddDays(-125), BuyerId = buyer.Id,
            Budget = directTotal * 1.02m, CreatedAt = today.AddDays(-138), SelectionPlan = plan, FundingSource = "Vốn đầu tư phát triển", Team = "Tổ dự án xây dựng",
            BoqOverheadPercent = 6.5m, BoqProfitPercent = 5.5m, BoqContingencyPercent = 0, BoqVatPercent = 10, Note = "Mẫu: gói xây dựng đã đấu thầu theo BOQ"
        };
        pkgB.PurchaseRequests.Add(prB);
        plan.Packages.Add(pkgB);
        db.ProcurementPackages.Add(pkgB);
        await db.SaveChangesAsync();
        var boqB = BuildBoq(pkgB.Id);
        await db.SaveChangesAsync();
        Boq.Renumber(boqB); await db.SaveChangesAsync();
        await BoqVersions.SnapshotAsync(db, pkgB.Id, $"Lập gói thầu {P}TD-XD-001", buyer.Id); await db.SaveChangesAsync();

        var tender = new Tender
        {
            Number = $"{P}TD-XD-001", Title = "Gói thầu xây lắp nhà xưởng đóng gói khu A", PurchaseRequest = prB, Package = pkgB, BuyerId = buyer.Id, Status = TenderStatus.Contracted,
            PackageBudget = pkgB.Budget, EnvelopeMethod = EnvelopeMethod.OneEnvelope, ContractType = "Đơn giá cố định", ContractDurationDays = 180,
            TechnicalRequirements = "Thi công đúng bản vẽ thiết kế, vật liệu có chứng chỉ chất lượng; biện pháp thi công, tiến độ và an toàn lao động được duyệt.",
            QualificationCriteria = "Có ≥ 2 công trình tương tự trong 5 năm gần nhất; năng lực tài chính ≥ 1,5 lần giá gói thầu; có chứng chỉ năng lực hoạt động xây dựng phù hợp.",
            ContractTerms = "Tạm ứng 30%; thanh toán theo khối lượng nghiệm thu từng đợt; giữ lại bảo hành 5%; bảo đảm thực hiện 5%.",
            EvaluationMethod = TenderEvaluationMethod.LowestEvaluatedPrice, MinTechnicalScore = 70, TechnicalWeight = 30, PriceWeight = 70, BidSecurityAmount = 15_000_000, BidValidityDays = 90,
            PerformanceSecurityPercent = 5, PublishedAt = today.AddDays(-125).AddHours(9), ClosingAt = today.AddDays(-105).AddHours(9), CreatedAt = today.AddDays(-130)
        };
        var s = 1;
        foreach (var l in Boq.Leaves(await db.BoqItems.AsNoTracking().Where(i => i.PackageId == pkgB.Id).ToListAsync()))
            tender.Lines.Add(new TenderLine { Description = l.Node.Item.Name, Unit = l.Node.Item.Unit, Quantity = l.Node.Item.Quantity, EstimatedPrice = l.Node.Item.UnitPrice, BoqCode = l.Node.Code, BoqPath = l.Path, SortOrder = s++ });
        db.Tenders.Add(tender);
        for (var i = 0; i < 3; i++)
        {
            tender.Committee.Add(new TenderCommitteeMember { UserId = committeeUsers[i].Id, User = committeeUsers[i], IsLeader = i == 0, ConflictDeclaredAt = tender.ClosingAt.AddHours(-1), ScoringCompletedAt = tender.ClosingAt.AddDays(6) });
            Log(DocType.Tender, () => tender.Id, buyer, "Bổ sung tổ chuyên gia", tender.CreatedAt.AddHours(2 + i), committeeUsers[i].FullName + (i == 0 ? " (tổ trưởng)" : ""));
        }
        // 3 nhà thầu chào giá từng dòng: A giá hợp lý, B thấp nhưng có dòng thép chào thấp bất thường, C cao hơn kèm một dòng cao bất thường
        var factors = new[] { 0.985m, 0.955m, 1.04m };
        var scores = new[] { new[] { 84, 80, 82 }, new[] { 78, 76, 80 }, new[] { 88, 90, 86 } };
        for (var i = 0; i < 3; i++)
        {
            var b = new TenderBidder
            {
                Vendor = vendors[i], RegisteredAt = tender.PublishedAt!.Value.AddDays(2 + i), Submitted = true, SubmittedAt = tender.ClosingAt.AddHours(-(4 + i * 7)),
                DiscountPercent = i == 1 ? 1 : 0, VatPercent = 10, LeadTimeDays = 170 + i * 5, ValidityDays = 90, BidSecurityAmount = 15_000_000,
                BidSecurityRef = $"BL{rnd.Next(1000, 9999)}/{tender.ClosingAt:yy}/NH", BidSecurityExpiry = tender.ClosingAt.Date.AddDays(125), Valid = true, Qualified = true
            };
            tender.Bidders.Add(b);
            foreach (var l in tender.Lines)
            {
                var f = factors[i] * (1 + (decimal)(rnd.NextDouble() * 0.04 - 0.02));
                if (i == 1 && l.BoqCode == "1.2.3") f = 0.62m;   // thép móng: thấp bất thường
                if (i == 2 && l.BoqCode == "3.1") f = 1.55m;   // sơn: cao bất thường
                b.Lines.Add(new TenderBidLine { TenderLine = l, UnitPrice = R(l.EstimatedPrice * f) });
            }
            b.BidPrice = tender.Lines.Zip(b.Lines).Sum(x => x.First.Quantity * x.Second.UnitPrice);
            for (var m = 0; m < 3; m++)
                b.Scores.Add(new TenderScore { MemberUserId = committeeUsers[m].Id, MemberUser = committeeUsers[m], TechnicalScore = scores[i][m], ScoredAt = tender.ClosingAt.AddDays(3 + m), Comment = "Đáp ứng yêu cầu kỹ thuật và biện pháp thi công." });
            Log(DocType.Tender, () => tender.Id, buyer, "Nhà thầu nhận HSMT", b.RegisteredAt, vendors[i].Name);
        }
        tender.OpenedAt = tender.ClosingAt.AddHours(1);
        var win = TenderEvaluation.Evaluate(tender).First(r => r.Rank == 1);
        tender.AwardedVendor = win.Bid.Vendor; tender.AwardedVendorId = win.Bid.VendorId; tender.AwardPrice = win.Bid.NetPrice;
        tender.AwardReason = "Giá đánh giá thấp nhất trong các HSDT đạt kỹ thuật."; tender.ComplaintDeadline = today.AddDays(-95).AddHours(17);
        Log(DocType.Tender, () => tender.Id, buyer, "Trình duyệt kế hoạch & HSMT", tender.CreatedAt.AddDays(2).AddHours(9), $"Đóng thầu {tender.ClosingAt.ToDateTime()}, tổ chuyên gia 3 người");
        Log(DocType.Tender, () => tender.Id, director, "Giám đốc duyệt KH & HSMT – phát hành, đăng tải thông báo mời thầu", tender.PublishedAt!.Value);
        Log(DocType.Tender, () => tender.Id, buyer, "Mở thầu – 3 HSDT", tender.OpenedAt.Value);
        Log(DocType.Tender, () => tender.Id, director, "Giám đốc phê duyệt kết quả lựa chọn nhà thầu – công khai kết quả", today.AddDays(-100).AddHours(15));
        await db.SaveChangesAsync();

        // Hợp đồng: đơn giá hợp đồng = đơn giá dự thầu quy đổi theo giá trúng thầu
        var bid = win.Bid; var factor = bid.BidPrice > 0 ? tender.AwardPrice!.Value / bid.BidPrice : 1;
        var signed = today.AddDays(-90);
        var contract = new Contract
        {
            Number = $"{P}HD-XD-001", Title = "Hợp đồng thi công xây dựng nhà xưởng đóng gói khu A", Tender = tender, VendorId = bid.VendorId, Vendor = bid.Vendor, VatPercent = 10,
            SignedDate = signed, StartDate = signed, EndDate = today.AddDays(95), PaymentTerms = "Tạm ứng 30%; thanh toán theo khối lượng nghiệm thu, 30 ngày kể từ khi có hồ sơ thanh toán hợp lệ",
            PerformanceSecurityRef = $"BLTH{rnd.Next(1000, 9999)}/{signed:yy}", Terms = tender.ContractTerms, CreatedById = buyer.Id, CreatedAt = signed.AddHours(10), Status = ContractStatus.Active
        };
        s = 1;
        foreach (var (tl, bl) in tender.Lines.Zip(bid.Lines))
            contract.Lines.Add(new ContractLine { Description = tl.Description, Unit = tl.Unit, Quantity = tl.Quantity, UnitPrice = Math.Round(bl.UnitPrice * factor, 0), SortOrder = s++, BoqCode = tl.BoqCode, BoqPath = tl.BoqPath });
        contract.Value = tender.AwardPrice!.Value;
        contract.PerformanceSecurityAmount = R(contract.Value * 0.05m); contract.PerformanceSecurityExpiry = contract.EndDate.AddDays(30);
        db.Contracts.Add(contract);
        await db.SaveChangesAsync();
        Log(DocType.Contract, () => contract.Id, buyer, "Ký hợp đồng", signed.AddHours(10), $"Từ gói thầu {tender.Number} – {contract.Value.ToMoney()} đ (trước VAT)");
        var gross0 = Math.Round(contract.Value * 1.1m, 0);

        // Tạm ứng 30% đã chi + bảo lãnh tạm ứng
        var advance = Math.Round(gross0 * 0.30m, 0);
        var ms = new ContractMilestone { Contract = contract, Name = "Tạm ứng 30%", Type = PaymentType.Advance, Percent = 30, Amount = advance, DueDate = signed.AddDays(10), Condition = "Sau khi ký hợp đồng và nộp bảo lãnh tạm ứng", SortOrder = 1 };
        db.ContractMilestones.Add(ms);
        db.ContractGuarantees.Add(new ContractGuarantee { Contract = contract, Type = GuaranteeType.Advance, Amount = advance, Reference = $"BLTU{rnd.Next(1000, 9999)}", Bank = "Vietcombank", ExpiryDate = contract.EndDate.AddDays(15) });
        db.ContractGuarantees.Add(new ContractGuarantee { Contract = contract, Type = GuaranteeType.Warranty, Amount = R(contract.Value * 0.05m), Reference = $"BLBH{rnd.Next(1000, 9999)}", Bank = "BIDV", ExpiryDate = contract.EndDate.AddDays(400) });
        var payAdv = new Payment
        {
            Number = $"{P}TT-XD-001", Type = PaymentType.Advance, VendorId = contract.VendorId, Contract = contract, Milestone = ms, Amount = advance, NetAmount = advance,
            Note = $"Tạm ứng 30% (30% HĐ {contract.Number})", Status = PaymentStatus.Paid, RequestedById = accountant.Id, RequestedAt = signed.AddDays(9), ApprovedById = director.Id, ApprovedAt = signed.AddDays(10),
            PaidById = accountant.Id, PaidAt = signed.AddDays(12), PaymentRef = "UNC" + rnd.Next(10000, 99999)
        };
        db.Payments.Add(payAdv);
        await db.SaveChangesAsync();
        Log(DocType.Payment, () => payAdv.Id, accountant, $"Đề nghị tạm ứng hợp đồng {advance.ToMoney()} đ", payAdv.RequestedAt);
        Log(DocType.Payment, () => payAdv.Id, director, "Giám đốc duyệt thanh toán", payAdv.ApprovedAt!.Value);
        Log(DocType.Payment, () => payAdv.Id, accountant, $"Đã chi {advance.ToMoney()} đ", payAdv.PaidAt!.Value, $"UNC {payAdv.PaymentRef} ngày {payAdv.PaidAt:dd/MM/yyyy}");

        // Phụ lục PL01 (đã duyệt): tăng khối lượng thép móng và thêm cọc tre gia cố nền yếu
        var steel = contract.Lines.First(l => l.BoqCode == "1.2.3");
        var pl = new ContractAmendment
        {
            Contract = contract, Number = "PL01", Description = "Gia cố nền yếu theo kết quả khoan địa chất bổ sung: thêm cọc tre và tăng cốt thép móng.", Status = AmendmentStatus.Approved,
            CreatedById = buyer.Id, CreatedAt = today.AddDays(-35), DecidedById = director.Id, DecidedAt = today.AddDays(-34), DecisionNote = "Đồng ý theo đề xuất của tổ kỹ thuật."
        };
        var addPlan = BoqVariations.Plan(contract.Lines.ToList(), new Dictionary<int, decimal>(), new Dictionary<int, decimal> { [steel.Id] = steel.Quantity + 1.5m },
            new[] { new VariationAddition("Gia cố nền bằng cọc tre L=2,5m", "m", 120, 85_000, contract.Lines.First(l => l.BoqCode == "1.1.2").BoqPath) });
        if (addPlan.Errors.Count > 0) throw new InvalidOperationException(string.Join(" ", addPlan.Errors));
        pl.ValueChange = addPlan.ValueChange; foreach (var l in addPlan.Lines) pl.Lines.Add(l);
        BoqVariations.Apply(contract, pl, new Dictionary<int, decimal>());
        contract.Value += pl.ValueChange;
        db.ContractAmendments.Add(pl);
        await db.SaveChangesAsync();
        Log(DocType.Contract, () => contract.Id, buyer, "Trình duyệt phụ lục PL01", pl.CreatedAt, $"{pl.Description} · {pl.Lines.Count} dòng điều chỉnh · +{pl.ValueChange.ToMoney()} đ");
        Log(DocType.Contract, () => contract.Id, director, $"Giám đốc duyệt phụ lục PL01 – giá trị HĐ {contract.Value.ToMoney()} đ, hiệu lực đến {contract.EndDate:dd/MM/yyyy}", pl.DecidedAt!.Value);

        // Nghiệm thu khối lượng: đợt 1 (đã xác nhận, đã chi), đợt 2 (đã xác nhận, chờ duyệt thanh toán), đợt 3 (nháp)
        var byCode = contract.Lines.ToDictionary(l => l.BoqCode ?? "");
        decimal Gross(decimal sub) => BoqAcceptances.Gross(sub, contract.VatPercent);
        BoqAcceptance NewAcc(int period, int daysAgo, string note, AcceptanceStatus st, params (string code, decimal q)[] qs)
        {
            var a = new BoqAcceptance { Number = $"{P}NT-XD-{period:000}", Contract = contract, Period = period, AcceptanceDate = today.AddDays(-daysAgo), Note = note, Status = st, CreatedById = buyer.Id, CreatedAt = today.AddDays(-daysAgo).AddHours(9) };
            foreach (var (code, q) in qs) a.Lines.Add(new BoqAcceptanceLine { ContractLine = byCode[code], Quantity = q });
            a.SubTotal = a.Lines.Sum(l => Math.Round(l.Quantity * l.ContractLine!.UnitPrice, 0, MidpointRounding.AwayFromZero));
            if (st == AcceptanceStatus.Confirmed) { a.ConfirmedById = director.Id; a.ConfirmedAt = today.AddDays(-daysAgo).AddHours(15); }
            db.BoqAcceptances.Add(a);
            return a;
        }
        var acc1 = NewAcc(1, 55, "Hoàn thành phần đào đất và bê tông lót.", AcceptanceStatus.Confirmed, ("1.1.1", 320), ("1.1.2", 90), ("1.2.1", 24));
        var acc2 = NewAcc(2, 20, "Hoàn thành bê tông móng, một phần cốt thép móng.", AcceptanceStatus.Confirmed, ("1.1.3", 120), ("1.2.2", 96), ("1.2.3", 4));
        var acc3 = NewAcc(3, 1, "Đang xây tường tầng 1 – nghiệm thu một phần.", AcceptanceStatus.Draft, ("2.1.1", 70), ("1.2.3", 2));
        await db.SaveChangesAsync();
        foreach (var a in new[] { acc1, acc2, acc3 })
            Log(DocType.Contract, () => contract.Id, buyer, $"Lập biên bản nghiệm thu khối lượng {a.Number}", a.CreatedAt, $"Đợt {a.Period}: {a.SubTotal.ToMoney()} đ (trước VAT)");
        Log(DocType.Contract, () => contract.Id, director, $"Xác nhận nghiệm thu khối lượng {acc1.Number}", acc1.ConfirmedAt!.Value, $"{acc1.SubTotal.ToMoney()} đ");
        Log(DocType.Contract, () => contract.Id, director, $"Xác nhận nghiệm thu khối lượng {acc2.Number}", acc2.ConfirmedAt!.Value, $"{acc2.SubTotal.ToMoney()} đ");

        // Thanh toán theo khối lượng (khấu trừ tạm ứng theo tỷ lệ). Đợt 1 đã chi; đợt 2 chờ duyệt.
        var contractGross = Math.Round(contract.Value * 1.1m, 0);
        decimal advRecovered = 0;
        Payment PayFor(BoqAcceptance a, string number, PaymentStatus st, int daysAgo)
        {
            var g = Gross(a.SubTotal);
            var ded = BoqAcceptances.AdvanceDeduction(g, contractGross, advance, advRecovered); advRecovered += ded;
            var p = new Payment
            {
                Number = number, Type = PaymentType.Acceptance, VendorId = contract.VendorId, Contract = contract, Acceptance = a, Amount = g, AdvanceDeduction = ded, NetAmount = g - ded,
                Note = $"Thanh toán theo khối lượng nghiệm thu {a.Number} (đợt {a.Period}) – HĐ {contract.Number}" + (ded > 0 ? $" · khấu trừ tạm ứng {ded.ToMoney()} đ" : ""),
                Status = st, RequestedById = accountant.Id, RequestedAt = today.AddDays(-daysAgo)
            };
            if (st >= PaymentStatus.Approved) { p.ApprovedById = director.Id; p.ApprovedAt = p.RequestedAt.AddDays(1); }
            if (st == PaymentStatus.Paid) { p.PaidById = accountant.Id; p.PaidAt = p.RequestedAt.AddDays(3); p.PaymentRef = "UNC" + rnd.Next(10000, 99999); }
            db.Payments.Add(p);
            return p;
        }
        var pay1 = PayFor(acc1, $"{P}TT-XD-002", PaymentStatus.Paid, 52);
        var pay2 = PayFor(acc2, $"{P}TT-XD-003", PaymentStatus.Requested, 17);
        await db.SaveChangesAsync();
        Log(DocType.Payment, () => pay1.Id, accountant, $"Đề nghị thanh toán {pay1.NetAmount.ToMoney()} đ", pay1.RequestedAt, pay1.Note);
        Log(DocType.Payment, () => pay1.Id, director, "Giám đốc duyệt thanh toán", pay1.ApprovedAt!.Value);
        Log(DocType.Payment, () => pay1.Id, accountant, $"Đã chi {pay1.NetAmount.ToMoney()} đ", pay1.PaidAt!.Value, $"UNC {pay1.PaymentRef} ngày {pay1.PaidAt:dd/MM/yyyy}");
        Log(DocType.Payment, () => pay2.Id, accountant, $"Đề nghị thanh toán {pay2.NetAmount.ToMoney()} đ", pay2.RequestedAt, pay2.Note);
        done.Add($"gói xây dựng đấu thầu theo BOQ: 3 nhà thầu chào giá từng dòng, hợp đồng {contract.Number}, tạm ứng, 3 đợt nghiệm thu khối lượng, phụ lục khối lượng phát sinh");

        foreach (var l in logs)
            db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = l.Type, DocumentId = l.Id(), UserId = l.User.Id, Action = l.Action, Comment = l.Comment, CreatedAt = l.At });
        await db.SaveChangesAsync();
        return done;
    }

    /// <summary>Gỡ dữ liệu mẫu xây dựng (gọi trước khi gỡ chứng từ mẫu chính)</summary>
    public static async Task RemoveAsync(EProcDbContext db)
    {
        var pkgIds = await db.ProcurementPackages.Where(p => p.Number.StartsWith(P + "GM-XD")).Select(p => p.Id).ToListAsync();
        var tenderIds = await db.Tenders.Where(t => t.Number.StartsWith(P + "TD-XD")).Select(t => t.Id).ToListAsync();
        var contractIds = await db.Contracts.Where(c => c.Number.StartsWith(P + "HD-XD")).Select(c => c.Id).ToListAsync();
        var prIds = await db.PurchaseRequests.Where(p => p.Number.StartsWith(P + "PR-XD")).Select(p => p.Id).ToListAsync();
        var planIds = await db.SelectionPlans.Where(p => p.Number.StartsWith(P + "KH-XD")).Select(p => p.Id).ToListAsync();
        if (pkgIds.Count + tenderIds.Count + contractIds.Count + prIds.Count == 0) return;
        var payIds = await db.Payments.Where(p => p.ContractId != null && contractIds.Contains(p.ContractId.Value)).Select(p => p.Id).ToListAsync();
        var accIds = await db.BoqAcceptances.Where(a => contractIds.Contains(a.ContractId)).Select(a => a.Id).ToListAsync();
        var amIds = await db.ContractAmendments.Where(a => contractIds.Contains(a.ContractId)).Select(a => a.Id).ToListAsync();
        var bidderIds = await db.TenderBidders.Where(b => tenderIds.Contains(b.TenderId)).Select(b => b.Id).ToListAsync();

        await db.Payments.Where(p => payIds.Contains(p.Id)).ExecuteDeleteAsync();
        await db.BoqAcceptanceLines.Where(l => accIds.Contains(l.AcceptanceId)).ExecuteDeleteAsync();
        await db.BoqAcceptances.Where(a => accIds.Contains(a.Id)).ExecuteDeleteAsync();
        await db.ContractAmendmentLines.Where(l => amIds.Contains(l.AmendmentId)).ExecuteDeleteAsync();
        await db.ContractAmendments.Where(a => amIds.Contains(a.Id)).ExecuteDeleteAsync();
        await db.ContractGuarantees.Where(g => contractIds.Contains(g.ContractId)).ExecuteDeleteAsync();
        await db.ContractMilestones.Where(m => contractIds.Contains(m.ContractId)).ExecuteDeleteAsync();
        await db.ContractLines.Where(l => contractIds.Contains(l.ContractId)).ExecuteDeleteAsync();
        await db.Contracts.Where(c => contractIds.Contains(c.Id)).ExecuteDeleteAsync();

        await db.TenderScores.Where(s => bidderIds.Contains(s.TenderBidderId)).ExecuteDeleteAsync();
        await db.TenderBidLines.Where(l => bidderIds.Contains(l.TenderBidderId)).ExecuteDeleteAsync();
        await db.TenderBidders.Where(b => bidderIds.Contains(b.Id)).ExecuteDeleteAsync();
        await db.TenderCommitteeMembers.Where(m => tenderIds.Contains(m.TenderId)).ExecuteDeleteAsync();
        await db.TenderLines.Where(l => tenderIds.Contains(l.TenderId)).ExecuteDeleteAsync();
        await db.Tenders.Where(t => tenderIds.Contains(t.Id)).ExecuteDeleteAsync();

        await db.BoqVersions.Where(v => pkgIds.Contains(v.PackageId)).ExecuteDeleteAsync();
        await db.BoqItems.Where(i => pkgIds.Contains(i.PackageId)).ExecuteDeleteAsync();
        await db.PackageMembers.Where(m => pkgIds.Contains(m.PackageId)).ExecuteDeleteAsync();
        await db.PurchaseRequests.Where(p => prIds.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.PackageId, (int?)null));
        await db.ProcurementPackages.Where(p => pkgIds.Contains(p.Id)).ExecuteDeleteAsync();
        await db.SelectionPlans.Where(p => planIds.Contains(p.Id)).ExecuteDeleteAsync();
        await db.PurchaseRequestLines.Where(l => prIds.Contains(l.PurchaseRequestId)).ExecuteDeleteAsync();
        await db.PurchaseRequests.Where(p => prIds.Contains(p.Id)).ExecuteDeleteAsync();

        foreach (var (type, ids) in new[] { (DocType.Package, pkgIds), (DocType.Tender, tenderIds), (DocType.Contract, contractIds), (DocType.Payment, payIds), (DocType.SelectionPlan, planIds), (DocType.PR, prIds) })
            await db.ApprovalHistories.Where(h => h.DocumentType == type && ids.Contains(h.DocumentId)).ExecuteDeleteAsync();
    }
}
