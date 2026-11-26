using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Data;

/// <summary>Tạo database (nếu chưa có) và nạp dữ liệu mẫu cho toàn bộ quy trình mua sắm.</summary>
public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<EProcDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();

        await db.Database.EnsureCreatedAsync();
        await UpgradeSchemaAsync(db);
        if (await db.Users.AnyAsync()) return;

        var today = DateTime.Today;
        var year = today.Year;

        // ---------- Người dùng (mật khẩu chung: Abc@123) ----------
        AppUser U(string u, string name, string role, string dept)
        {
            var x = new AppUser { Username = u, FullName = name, Role = role, Department = dept, Email = $"{u}@company.local" };
            x.PasswordHash = hasher.HashPassword(x, "Abc@123");
            return x;
        }
        var admin = U("admin", "Quản trị hệ thống", AppRoles.Admin, "Phòng CNTT");
        var director = U("director", "Nguyễn Văn Đức", AppRoles.Director, "Ban Giám đốc");
        var approver = U("approver", "Lê Thị Hồng", AppRoles.Approver, "Phòng CNTT");
        var approver2 = U("approver2", "Phan Minh Quân", AppRoles.Approver, "Phòng Hành chính");
        var requester = U("requester", "Trần Văn Nam", AppRoles.Requester, "Phòng CNTT");
        var requester2 = U("requester2", "Hoàng Thị Mai", AppRoles.Requester, "Phòng Hành chính");
        var buyer = U("buyer", "Vũ Thị Thu", AppRoles.Buyer, "Phòng Mua hàng");
        db.Users.AddRange(admin, director, approver, approver2, requester, requester2, buyer);

        // ---------- Nhóm hàng & hàng hóa ----------
        var cIT = new Category { Code = "IT", Name = "Thiết bị CNTT" };
        var cVPP = new Category { Code = "VPP", Name = "Văn phòng phẩm" };
        var cNT = new Category { Code = "NT", Name = "Nội thất văn phòng" };
        var cBH = new Category { Code = "BHLD", Name = "Bảo hộ lao động" };
        var cDV = new Category { Code = "DV", Name = "Dịch vụ" };
        var cSW = new Category { Code = "SW", Name = "Phần mềm & bản quyền" };
        db.Categories.AddRange(cIT, cVPP, cNT, cBH, cDV, cSW);
        await db.SaveChangesAsync();

        Item I(string code, string name, Category c, string unit, decimal price, string? spec = null)
            => new() { Code = code, Name = name, CategoryId = c.Id, Unit = unit, ReferencePrice = price, Specification = spec };
        var laptop = I("IT-LAP-01", "Laptop văn phòng 14 inch", cIT, "Cái", 22_000_000, "Core i5 / 16GB RAM / SSD 512GB / Win 11 Pro");
        var monitor = I("IT-MON-24", "Màn hình 24 inch Full HD", cIT, "Cái", 4_500_000, "IPS, 75Hz, HDMI + DisplayPort");
        var sw = I("IT-NET-SW", "Switch 24 port Gigabit", cIT, "Cái", 6_800_000, "Managed L2, 4 SFP");
        var ap = I("IT-NET-AP", "Bộ phát WiFi 6 ốp trần", cIT, "Cái", 3_900_000);
        var cable = I("IT-CAB-6", "Cáp mạng Cat6 (thùng 305m)", cIT, "Thùng", 2_600_000);
        var av = I("SW-AV-1Y", "Bản quyền diệt virus doanh nghiệp (1 năm)", cSW, "License", 480_000);
        var paper = I("VPP-A4", "Giấy A4 Double A 70gsm", cVPP, "Ram", 78_000);
        var pen = I("VPP-PEN", "Bút bi Thiên Long (hộp 20)", cVPP, "Hộp", 65_000);
        var file = I("VPP-FILE", "Bìa còng 7cm", cVPP, "Cái", 42_000);
        var ink = I("VPP-INK", "Mực in laser HP 107A", cVPP, "Hộp", 950_000);
        var desk = I("NT-DESK", "Bàn họp gỗ 2,4m", cNT, "Bộ", 18_500_000);
        var chair = I("NT-CHAIR", "Ghế lưới văn phòng", cNT, "Cái", 1_850_000);
        var helmet = I("BH-HELM", "Mũ bảo hộ", cBH, "Cái", 120_000);
        var clean = I("DV-CLEAN", "Dịch vụ vệ sinh văn phòng (tháng)", cDV, "Tháng", 15_000_000);
        db.Items.AddRange(laptop, monitor, sw, ap, cable, av, paper, pen, file, ink, desk, chair, helmet, clean);

        // ---------- Nhà cung cấp ----------
        Vendor V(string code, string name, string tax, VendorStatus st, decimal rating, string contact, params Category[] cats)
        {
            var v = new Vendor
            {
                Code = code, Name = name, TaxCode = tax, Status = st, Rating = rating, ContactPerson = contact,
                ContactPhone = "09" + tax[^8..], Phone = "028 3" + tax[^7..], Email = $"sales@{code.ToLower()}.vn",
                Address = "TP. Hồ Chí Minh", PaymentTerms = "Thanh toán 30 ngày sau khi nhận hàng và hóa đơn",
                BankName = "Vietcombank", BankAccount = "0071" + tax[^6..]
            };
            foreach (var c in cats) v.Categories.Add(new VendorCategory { CategoryId = c.Id });
            return v;
        }
        var vSaoMai = V("NCC001", "Công ty TNHH Thiết bị Tin học Sao Mai", "0312345671", VendorStatus.Approved, 4.25m, "Ngô Minh Trí", cIT, cSW);
        var vPhongVu = V("NCC002", "Công ty CP Công nghệ Phong Vũ Số", "0312345682", VendorStatus.Approved, 4.0m, "Đặng Thu Trang", cIT);
        var vHoangLong = V("NCC003", "Công ty TNHH Giải pháp Mạng Hoàng Long", "0312345693", VendorStatus.Approved, 3.75m, "Lý Văn Long", cIT, cDV);
        var vVPP = V("NCC004", "Công ty TNHH Văn phòng phẩm Hồng Hà Nam", "0312345604", VendorStatus.Approved, 4.5m, "Trần Thị Hà", cVPP);
        var vNoiThat = V("NCC005", "Công ty CP Nội thất Hòa Phát Miền Nam", "0312345615", VendorStatus.Approved, 4.0m, "Phạm Quốc Huy", cNT);
        var vXanh = V("NCC006", "Công ty TNHH Dịch vụ Vệ sinh Xanh", "0312345626", VendorStatus.Approved, 3.5m, "Võ Thị Lan", cDV);
        var vBH = V("NCC007", "Công ty TNHH Bảo hộ An Toàn Việt", "0312345637", VendorStatus.Pending, 0, "Huỳnh Văn Sang", cBH);
        var vBad = V("NCC008", "Công ty TNHH Thương mại Tân Tiến", "0312345648", VendorStatus.Blacklisted, 1.75m, "Lâm Tấn Tài", cIT, cVPP);
        vBad.Note = "Giao hàng trễ nhiều lần, hàng không đúng quy cách (2025).";
        db.Vendors.AddRange(vSaoMai, vPhongVu, vHoangLong, vVPP, vNoiThat, vXanh, vBH, vBad);
        await db.SaveChangesAsync();

        // ---------- Helper chứng từ ----------
        var prSeq = 1; var poSeq = 1; var rfqSeq = 1; var grnSeq = 1;
        PurchaseRequest PR(string title, AppUser req, PrStatus st, int daysAgo, PrPriority pri, params (Item it, decimal qty)[] lines)
        {
            var pr = new PurchaseRequest
            {
                Number = $"PR-{year}-{prSeq++:0000}", Title = title, RequesterId = req.Id, Department = req.Department, Status = st,
                Priority = pri, RequestDate = today.AddDays(-daysAgo), NeededDate = today.AddDays(14 - daysAgo / 2),
                Purpose = "Phục vụ hoạt động của phòng ban.", CreatedAt = today.AddDays(-daysAgo),
                SubmittedAt = st == PrStatus.Draft ? null : today.AddDays(-daysAgo).AddHours(9)
            };
            var s = 1;
            foreach (var (it, q) in lines)
                pr.Lines.Add(new PurchaseRequestLine { ItemId = it.Id, Description = it.Name, Unit = it.Unit, Quantity = q, EstimatedPrice = it.ReferencePrice, SortOrder = s++ });
            pr.Recalculate();
            return pr;
        }
        void Log(DocType t, int id, AppUser u, string action, DateTime at, string? cmt = null)
            => db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = t, DocumentId = id, UserId = u.Id, Action = action, Comment = cmt, CreatedAt = at });
        PurchaseOrder PO(Vendor v, int daysAgo, PoStatus st, PurchaseRequest? pr, params (Item it, decimal qty, decimal price)[] lines)
        {
            var po = new PurchaseOrder
            {
                Number = $"PO-{year}-{poSeq++:0000}", VendorId = v.Id, PurchaseRequestId = pr?.Id, BuyerId = buyer.Id, Status = st,
                OrderDate = today.AddDays(-daysAgo), DeliveryDate = today.AddDays(-daysAgo + 7), PaymentTerms = v.PaymentTerms,
                DeliveryAddress = "Kho công ty – Tầng 1, tòa nhà văn phòng", CreatedAt = today.AddDays(-daysAgo),
                IssuedAt = st >= PoStatus.Issued && st != PoStatus.Cancelled ? today.AddDays(-daysAgo) : null
            };
            var s = 1;
            foreach (var (it, q, p) in lines)
                po.Lines.Add(new PurchaseOrderLine { ItemId = it.Id, Description = it.Name, Unit = it.Unit, Quantity = q, UnitPrice = p, SortOrder = s++,
                    ReceivedQuantity = st == PoStatus.Received ? q : 0 });
            po.Recalculate();
            return po;
        }
        GoodsReceipt GRN(PurchaseOrder po, int daysAgo, Func<PurchaseOrderLine, decimal> qty)
        {
            var g = new GoodsReceipt { Number = $"GRN-{year}-{grnSeq++:0000}", PurchaseOrderId = po.Id, ReceiptDate = today.AddDays(-daysAgo),
                ReceivedById = buyer.Id, DeliveryNoteNo = $"PGH-{po.Number[^4..]}", CreatedAt = today.AddDays(-daysAgo) };
            foreach (var l in po.Lines)
            {
                var q = qty(l);
                if (q > 0) g.Lines.Add(new GoodsReceiptLine { PurchaseOrderLineId = l.Id, QuantityAccepted = q });
            }
            return g;
        }

        // ---------- Đề nghị mua hàng ----------
        var pr1 = PR("Mua laptop và màn hình cho nhân viên mới", requester, PrStatus.Processing, 20, PrPriority.Urgent, (laptop, 5), (monitor, 5));
        var pr2 = PR("Văn phòng phẩm quý IV", requester2, PrStatus.Completed, 25, PrPriority.Normal, (paper, 60), (pen, 10), (file, 40), (ink, 4));
        var pr3 = PR("Thiết bị mạng cho phòng họp tầng 3", requester, PrStatus.Approved, 8, PrPriority.Normal, (sw, 2), (ap, 4), (cable, 1));
        var pr4 = PR("Bàn ghế phòng họp lớn", requester2, PrStatus.PendingDirector, 4, PrPriority.Normal, (desk, 1), (chair, 16));
        var pr5 = PR("Gia hạn bản quyền diệt virus 2027", requester, PrStatus.Submitted, 1, PrPriority.Normal, (av, 50));
        var pr6 = PR("Đồ bảo hộ cho đội kỹ thuật", requester2, PrStatus.Draft, 0, PrPriority.Normal, (helmet, 20));
        var pr7 = PR("Mua thêm 3 laptop dự phòng", requester, PrStatus.Rejected, 12, PrPriority.Normal, (laptop, 3));
        db.PurchaseRequests.AddRange(pr1, pr2, pr3, pr4, pr5, pr6, pr7);
        await db.SaveChangesAsync();

        Log(DocType.PR, pr1.Id, requester, "Gửi duyệt", today.AddDays(-20).AddHours(9));
        Log(DocType.PR, pr1.Id, approver, "Trưởng bộ phận duyệt", today.AddDays(-19).AddHours(10), "Đồng ý, nhân sự mới vào ngày 01.");
        Log(DocType.PR, pr1.Id, director, "Giám đốc duyệt", today.AddDays(-19).AddHours(15));
        Log(DocType.PR, pr2.Id, requester2, "Gửi duyệt", today.AddDays(-25).AddHours(9));
        Log(DocType.PR, pr2.Id, approver2, "Trưởng bộ phận duyệt", today.AddDays(-25).AddHours(14));
        Log(DocType.PR, pr3.Id, requester, "Gửi duyệt", today.AddDays(-8).AddHours(9));
        Log(DocType.PR, pr3.Id, approver, "Trưởng bộ phận duyệt", today.AddDays(-7).AddHours(11));
        Log(DocType.PR, pr4.Id, requester2, "Gửi duyệt", today.AddDays(-4).AddHours(9));
        Log(DocType.PR, pr4.Id, approver2, "Trưởng bộ phận duyệt", today.AddDays(-3).AddHours(10), "Phòng họp lớn xuống cấp, cần thay.");
        Log(DocType.PR, pr5.Id, requester, "Gửi duyệt", today.AddDays(-1).AddHours(9));
        Log(DocType.PR, pr7.Id, requester, "Gửi duyệt", today.AddDays(-12).AddHours(9));
        Log(DocType.PR, pr7.Id, approver, "Từ chối", today.AddDays(-11).AddHours(9), "Kho còn 2 máy dự phòng, chưa cần mua.");

        // ---------- RFQ 1: đã chọn NCC (từ PR1) ----------
        var rfq1 = new Rfq
        {
            Number = $"RFQ-{year}-{rfqSeq++:0000}", Title = "Báo giá laptop + màn hình (PR1)", PurchaseRequestId = pr1.Id, BuyerId = buyer.Id,
            IssueDate = today.AddDays(-18), Deadline = today.AddDays(-14), Status = RfqStatus.Awarded, AwardedVendorId = vSaoMai.Id,
            AwardReason = "Giá tốt nhất, giao hàng nhanh, bảo hành 24 tháng.", Requirements = "Hàng mới 100%, bảo hành tối thiểu 12 tháng.",
            CreatedAt = today.AddDays(-18)
        };
        var l1 = new RfqLine { ItemId = laptop.Id, Description = laptop.Name, Unit = laptop.Unit, Quantity = 5, SortOrder = 1 };
        var l2 = new RfqLine { ItemId = monitor.Id, Description = monitor.Name, Unit = monitor.Unit, Quantity = 5, SortOrder = 2 };
        rfq1.Lines.Add(l1); rfq1.Lines.Add(l2);
        // ---------- RFQ 2: đang nhận báo giá (từ PR3) ----------
        var rfq2 = new Rfq
        {
            Number = $"RFQ-{year}-{rfqSeq++:0000}", Title = "Thiết bị mạng phòng họp tầng 3", PurchaseRequestId = pr3.Id, BuyerId = buyer.Id,
            IssueDate = today.AddDays(-5), Deadline = today.AddDays(2), Status = RfqStatus.Open, Requirements = "Bao gồm lắp đặt, cấu hình.",
            CreatedAt = today.AddDays(-5)
        };
        var m1 = new RfqLine { ItemId = sw.Id, Description = sw.Name, Unit = sw.Unit, Quantity = 2, SortOrder = 1 };
        var m2 = new RfqLine { ItemId = ap.Id, Description = ap.Name, Unit = ap.Unit, Quantity = 4, SortOrder = 2 };
        var m3 = new RfqLine { ItemId = cable.Id, Description = cable.Name, Unit = cable.Unit, Quantity = 1, SortOrder = 3 };
        rfq2.Lines.Add(m1); rfq2.Lines.Add(m2); rfq2.Lines.Add(m3);
        db.Rfqs.AddRange(rfq1, rfq2);
        await db.SaveChangesAsync();

        RfqVendor Inv(Rfq r, Vendor v, int? lead, params (RfqLine line, decimal price)[] prices)
        {
            var rv = new RfqVendor { RfqId = r.Id, VendorId = v.Id, Responded = prices.Length > 0, LeadTimeDays = lead,
                RespondedAt = prices.Length > 0 ? r.IssueDate.AddDays(2) : null, PaymentTerms = v.PaymentTerms, Warranty = "12 tháng" };
            foreach (var (line, p) in prices)
                rv.QuoteLines.Add(new VendorQuoteLine { RfqLineId = line.Id, UnitPrice = p, Amount = p * line.Quantity });
            rv.TotalAmount = rv.QuoteLines.Sum(x => x.Amount);
            return rv;
        }
        var w1 = Inv(rfq1, vSaoMai, 5, (l1, 21_200_000), (l2, 4_300_000)); w1.Warranty = "24 tháng";
        db.RfqVendors.AddRange(w1, Inv(rfq1, vPhongVu, 7, (l1, 21_900_000), (l2, 4_150_000)), Inv(rfq1, vHoangLong, 10, (l1, 22_500_000), (l2, 4_600_000)),
            Inv(rfq2, vSaoMai, 7, (m1, 6_500_000), (m2, 3_750_000), (m3, 2_550_000)),
            Inv(rfq2, vHoangLong, 5, (m1, 6_900_000), (m2, 3_600_000), (m3, 2_400_000)),
            Inv(rfq2, vPhongVu, null));
        Log(DocType.RFQ, rfq1.Id, buyer, "Phát hành RFQ", today.AddDays(-18).AddHours(10));
        Log(DocType.RFQ, rfq1.Id, buyer, "Chọn NCC trúng: " + vSaoMai.Name, today.AddDays(-13).AddHours(16));
        Log(DocType.RFQ, rfq2.Id, buyer, "Phát hành RFQ", today.AddDays(-5).AddHours(10));

        // ---------- Đơn mua hàng ----------
        var po1 = PO(vSaoMai, 12, PoStatus.PartiallyReceived, pr1, (laptop, 5, 21_200_000), (monitor, 5, 4_300_000));
        po1.RfqId = rfq1.Id;
        var po2 = PO(vVPP, 22, PoStatus.Received, pr2, (paper, 60, 76_000), (pen, 10, 62_000), (file, 40, 40_000), (ink, 4, 920_000));
        var po3 = PO(vPhongVu, 95, PoStatus.Received, null, (laptop, 3, 21_500_000));
        var po4 = PO(vXanh, 62, PoStatus.Received, null, (clean, 3, 15_000_000));
        var po5 = PO(vHoangLong, 40, PoStatus.Received, null, (sw, 1, 6_700_000), (cable, 2, 2_500_000));
        var po6 = PO(vVPP, 10, PoStatus.Issued, null, (paper, 30, 76_000), (ink, 2, 920_000));
        db.PurchaseOrders.AddRange(po1, po2, po3, po4, po5, po6);
        await db.SaveChangesAsync();
        Log(DocType.PO, po1.Id, buyer, "Trình duyệt", today.AddDays(-12).AddHours(9));
        Log(DocType.PO, po1.Id, director, "Giám đốc duyệt", today.AddDays(-12).AddHours(14));
        Log(DocType.PO, po1.Id, buyer, "Gửi NCC", today.AddDays(-12).AddHours(15));

        // ---------- Nhận hàng ----------
        var g1 = GRN(po1, 6, l => l.ItemId == laptop.Id ? 3 : 5);
        po1.Lines.First(l => l.ItemId == laptop.Id).ReceivedQuantity = 3;
        po1.Lines.First(l => l.ItemId == monitor.Id).ReceivedQuantity = 5;
        db.GoodsReceipts.AddRange(g1, GRN(po2, 18, l => l.Quantity), GRN(po3, 88, l => l.Quantity), GRN(po4, 60, l => l.Quantity), GRN(po5, 35, l => l.Quantity));

        // ---------- Đánh giá NCC ----------
        db.VendorEvaluations.AddRange(
            new VendorEvaluation { VendorId = vVPP.Id, PurchaseOrderId = po2.Id, EvaluatorId = buyer.Id, Quality = 5, Delivery = 4, Price = 5, Service = 4, Comment = "Giao đủ, đúng hẹn." },
            new VendorEvaluation { VendorId = vPhongVu.Id, PurchaseOrderId = po3.Id, EvaluatorId = buyer.Id, Quality = 4, Delivery = 4, Price = 4, Service = 4 },
            new VendorEvaluation { VendorId = vHoangLong.Id, PurchaseOrderId = po5.Id, EvaluatorId = buyer.Id, Quality = 4, Delivery = 3, Price = 4, Service = 4, Comment = "Giao trễ 2 ngày." });

        await db.SaveChangesAsync();
        await UpgradeSchemaAsync(db); // gắn hình thức cho chứng từ mẫu
    }

    /// <summary>
    /// Bổ sung cột mới cho database đã tạo từ phiên bản trước (dự án dùng EnsureCreated, không dùng migration).
    /// Mỗi lệnh chỉ chạy khi cột chưa tồn tại nên gọi lại nhiều lần vẫn an toàn.
    /// </summary>
    private static async Task UpgradeSchemaAsync(EProcDbContext db)
    {
        await CreateMissingTablesAsync(db);
        string[] statements =
        {
            // Chỉ định thầu: PO sinh từ tờ trình đã duyệt
            "IF COL_LENGTH('PurchaseOrders','DirectAwardId') IS NULL ALTER TABLE PurchaseOrders ADD DirectAwardId int NULL " +
                "CONSTRAINT FK_PurchaseOrders_DirectAwards_DirectAwardId REFERENCES DirectAwards(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrders_DirectAwardId') " +
                "CREATE INDEX IX_PurchaseOrders_DirectAwardId ON PurchaseOrders(DirectAwardId)",
            // Đấu thầu: PO đặt theo hợp đồng
            "IF COL_LENGTH('PurchaseOrders','ContractId') IS NULL ALTER TABLE PurchaseOrders ADD ContractId int NULL " +
                "CONSTRAINT FK_PurchaseOrders_Contracts_ContractId REFERENCES Contracts(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrders_ContractId') " +
                "CREATE INDEX IX_PurchaseOrders_ContractId ON PurchaseOrders(ContractId)",
            // Hình thức lựa chọn NCC trên PR
            "IF COL_LENGTH('Users','EmailNotifications') IS NULL ALTER TABLE Users ADD EmailNotifications bit NOT NULL CONSTRAINT DF_Users_EmailNotifications DEFAULT 1",
            "IF COL_LENGTH('Users','FailedLogins') IS NULL ALTER TABLE Users ADD FailedLogins int NOT NULL CONSTRAINT DF_Users_FailedLogins DEFAULT 0",
            "IF COL_LENGTH('Users','LockoutEnd') IS NULL ALTER TABLE Users ADD LockoutEnd datetime2 NULL",
            // Hợp đồng: mức phạt chậm giao
            "IF COL_LENGTH('Contracts','LatePenaltyPercentPerDay') IS NULL ALTER TABLE Contracts ADD LatePenaltyPercentPerDay decimal(5,3) NOT NULL CONSTRAINT DF_Contracts_LatePenalty DEFAULT 0.1",
            "IF COL_LENGTH('Contracts','LatePenaltyCapPercent') IS NULL ALTER TABLE Contracts ADD LatePenaltyCapPercent decimal(5,2) NOT NULL CONSTRAINT DF_Contracts_LatePenaltyCap DEFAULT 8",
            // Cổng NCC: phản hồi PO
            "IF COL_LENGTH('PurchaseOrders','VendorConfirmedAt') IS NULL ALTER TABLE PurchaseOrders ADD VendorConfirmedAt datetime2 NULL",
            "IF COL_LENGTH('PurchaseOrders','VendorPromisedDate') IS NULL ALTER TABLE PurchaseOrders ADD VendorPromisedDate datetime2 NULL",
            "IF COL_LENGTH('PurchaseOrders','VendorRejectedAt') IS NULL ALTER TABLE PurchaseOrders ADD VendorRejectedAt datetime2 NULL",
            "IF COL_LENGTH('PurchaseOrders','VendorResponse') IS NULL ALTER TABLE PurchaseOrders ADD VendorResponse nvarchar(1000) NULL",
            "IF COL_LENGTH('PurchaseRequests','Method') IS NULL ALTER TABLE PurchaseRequests ADD Method int NULL",
            "IF COL_LENGTH('PurchaseRequests','MethodReason') IS NULL ALTER TABLE PurchaseRequests ADD MethodReason nvarchar(1000) NULL",
            "IF COL_LENGTH('PurchaseRequests','ContractId') IS NULL ALTER TABLE PurchaseRequests ADD ContractId int NULL CONSTRAINT FK_PurchaseRequests_Contracts_ContractId REFERENCES Contracts(Id)",
            // Hình thức + căn cứ mua sắm trực tiếp trên PO
            "IF COL_LENGTH('PurchaseOrders','Method') IS NULL ALTER TABLE PurchaseOrders ADD Method int NULL",
            "IF COL_LENGTH('PurchaseOrders','PriceDeviationReason') IS NULL ALTER TABLE PurchaseOrders ADD PriceDeviationReason nvarchar(1000) NULL",
            "IF COL_LENGTH('PurchaseOrders','ReferencePoId') IS NULL ALTER TABLE PurchaseOrders ADD ReferencePoId int NULL " +
                "CONSTRAINT FK_PurchaseOrders_PurchaseOrders_ReferencePoId REFERENCES PurchaseOrders(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrders_ReferencePoId') " +
                "CREATE INDEX IX_PurchaseOrders_ReferencePoId ON PurchaseOrders(ReferencePoId)",
            // Chào hàng cạnh tranh: phương pháp đánh giá, niêm phong, đề xuất & duyệt kết quả
            "IF COL_LENGTH('Rfqs','EvaluationMethod') IS NULL ALTER TABLE Rfqs ADD EvaluationMethod int NOT NULL CONSTRAINT DF_Rfqs_EvaluationMethod DEFAULT 1",
            "IF COL_LENGTH('Rfqs','PriceWeight') IS NULL ALTER TABLE Rfqs ADD PriceWeight int NOT NULL CONSTRAINT DF_Rfqs_PriceWeight DEFAULT 70",
            "IF COL_LENGTH('Rfqs','TechnicalWeight') IS NULL ALTER TABLE Rfqs ADD TechnicalWeight int NOT NULL CONSTRAINT DF_Rfqs_TechnicalWeight DEFAULT 20",
            "IF COL_LENGTH('Rfqs','DeliveryWeight') IS NULL ALTER TABLE Rfqs ADD DeliveryWeight int NOT NULL CONSTRAINT DF_Rfqs_DeliveryWeight DEFAULT 10",
            "IF COL_LENGTH('Rfqs','OpenedAt') IS NULL ALTER TABLE Rfqs ADD OpenedAt datetime2 NULL",
            "IF COL_LENGTH('Rfqs','ResultApprovalReason') IS NULL ALTER TABLE Rfqs ADD ResultApprovalReason nvarchar(1000) NULL",
            "IF COL_LENGTH('Rfqs','ProposedVendorId') IS NULL ALTER TABLE Rfqs ADD ProposedVendorId int NULL " +
                "CONSTRAINT FK_Rfqs_Vendors_ProposedVendorId REFERENCES Vendors(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Rfqs_ProposedVendorId') CREATE INDEX IX_Rfqs_ProposedVendorId ON Rfqs(ProposedVendorId)",
            "IF COL_LENGTH('RfqVendors','TechnicalPass') IS NULL ALTER TABLE RfqVendors ADD TechnicalPass bit NULL",
            "IF COL_LENGTH('RfqVendors','TechnicalScore') IS NULL ALTER TABLE RfqVendors ADD TechnicalScore int NULL",
            "IF COL_LENGTH('RfqVendors','TechnicalNote') IS NULL ALTER TABLE RfqVendors ADD TechnicalNote nvarchar(500) NULL",
            // RFQ đã chọn NCC từ trước: coi như đã mở và các báo giá đã đạt kỹ thuật
            "UPDATE Rfqs SET OpenedAt = ISNULL(OpenedAt, Deadline) WHERE Status = 3 AND OpenedAt IS NULL",
            "UPDATE rv SET TechnicalPass = 1 FROM RfqVendors rv JOIN Rfqs r ON r.Id = rv.RfqId WHERE r.Status = 3 AND rv.Responded = 1 AND rv.TechnicalPass IS NULL",
            // Cổng NCC: file HSMT công khai cho nhà thầu. Dữ liệu cũ: file gói thầu tải lên trước đóng thầu (không phải HSDT mở niêm phong) coi là HSMT.
            "IF COL_LENGTH('Attachments','IsPublic') IS NULL BEGIN " +
                "ALTER TABLE Attachments ADD IsPublic bit NOT NULL CONSTRAINT DF_Attachments_IsPublic DEFAULT 0; " +
                "EXEC('UPDATE a SET IsPublic = 1 FROM Attachments a JOIN Tenders t ON a.DocumentType = 5 AND a.DocumentId = t.Id " +
                "WHERE a.UploadedAt < t.ClosingAt AND (a.Note IS NULL OR a.Note NOT LIKE N''HSDT online%'')') END",
            // Gói mua sắm: PR, RFQ, gói thầu, tờ trình chỉ định, PO gắn với gói
            "IF COL_LENGTH('PurchaseRequests','PackageId') IS NULL ALTER TABLE PurchaseRequests ADD PackageId int NULL CONSTRAINT FK_PurchaseRequests_ProcurementPackages_PackageId REFERENCES ProcurementPackages(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseRequests_PackageId') CREATE INDEX IX_PurchaseRequests_PackageId ON PurchaseRequests(PackageId)",
            "IF COL_LENGTH('Rfqs','PackageId') IS NULL ALTER TABLE Rfqs ADD PackageId int NULL CONSTRAINT FK_Rfqs_ProcurementPackages_PackageId REFERENCES ProcurementPackages(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Rfqs_PackageId') CREATE INDEX IX_Rfqs_PackageId ON Rfqs(PackageId)",
            "IF COL_LENGTH('Tenders','PackageId') IS NULL ALTER TABLE Tenders ADD PackageId int NULL CONSTRAINT FK_Tenders_ProcurementPackages_PackageId REFERENCES ProcurementPackages(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tenders_PackageId') CREATE INDEX IX_Tenders_PackageId ON Tenders(PackageId)",
            "IF COL_LENGTH('DirectAwards','PackageId') IS NULL ALTER TABLE DirectAwards ADD PackageId int NULL CONSTRAINT FK_DirectAwards_ProcurementPackages_PackageId REFERENCES ProcurementPackages(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DirectAwards_PackageId') CREATE INDEX IX_DirectAwards_PackageId ON DirectAwards(PackageId)",
            "IF COL_LENGTH('PurchaseOrders','PackageId') IS NULL ALTER TABLE PurchaseOrders ADD PackageId int NULL CONSTRAINT FK_PurchaseOrders_ProcurementPackages_PackageId REFERENCES ProcurementPackages(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PurchaseOrders_PackageId') CREATE INDEX IX_PurchaseOrders_PackageId ON PurchaseOrders(PackageId)",
            // Chỉ định thầu: yêu cầu báo giá gửi NCC qua cổng, báo giá niêm phong
            "IF COL_LENGTH('DirectAwards','QuoteRequestedAt') IS NULL ALTER TABLE DirectAwards ADD QuoteRequestedAt datetime2 NULL",
            "IF COL_LENGTH('DirectAwards','QuoteDeadline') IS NULL ALTER TABLE DirectAwards ADD QuoteDeadline datetime2 NULL",
            "IF COL_LENGTH('DirectAwards','QuoteRequirements') IS NULL ALTER TABLE DirectAwards ADD QuoteRequirements nvarchar(2000) NULL",
            "IF COL_LENGTH('DirectAwards','QuoteOpenedAt') IS NULL ALTER TABLE DirectAwards ADD QuoteOpenedAt datetime2 NULL",
            "IF COL_LENGTH('VendorSubmissions','DirectAwardId') IS NULL ALTER TABLE VendorSubmissions ADD DirectAwardId int NULL CONSTRAINT FK_VendorSubmissions_DirectAwards_DirectAwardId REFERENCES DirectAwards(Id)",
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_VendorSubmissions_DirectAwardId_IsLatest') CREATE INDEX IX_VendorSubmissions_DirectAwardId_IsLatest ON VendorSubmissions(DirectAwardId, IsLatest)",
            // Quản lý nguồn cung: đánh giá NCC định kỳ, lý do vượt giá trần bảng giá tiêu chuẩn
            "IF COL_LENGTH('VendorEvaluations','Period') IS NULL ALTER TABLE VendorEvaluations ADD Period nvarchar(20) NULL",
            "IF COL_LENGTH('PurchaseOrders','StandardPriceReason') IS NULL ALTER TABLE PurchaseOrders ADD StandardPriceReason nvarchar(1000) NULL",
            // Danh mục: tổ triển khai gói mua sắm, loại đơn hàng
            "IF COL_LENGTH('ProcurementPackages','Team') IS NULL ALTER TABLE ProcurementPackages ADD Team nvarchar(200) NULL",
            "IF COL_LENGTH('PurchaseOrders','OrderType') IS NULL ALTER TABLE PurchaseOrders ADD OrderType nvarchar(100) NULL",
            // Danh mục: lĩnh vực kinh doanh của NCC, xuất xứ / thương hiệu hàng hóa
            "IF COL_LENGTH('Vendors','BusinessSector') IS NULL ALTER TABLE Vendors ADD BusinessSector nvarchar(200) NULL",
            "IF COL_LENGTH('Items','Origin') IS NULL ALTER TABLE Items ADD Origin nvarchar(100) NULL",
            "IF COL_LENGTH('Items','Brand') IS NULL ALTER TABLE Items ADD Brand nvarchar(100) NULL",
            // BOQ phân cấp: dòng gói thầu sinh từ BOQ (mã, đường dẫn hạng mục), khối lượng 3 số lẻ
            "IF COL_LENGTH('TenderLines','BoqCode') IS NULL ALTER TABLE TenderLines ADD BoqCode nvarchar(30) NULL",
            "IF COL_LENGTH('TenderLines','BoqPath') IS NULL ALTER TABLE TenderLines ADD BoqPath nvarchar(500) NULL",
            "IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TenderLines') AND name = 'Quantity' AND scale <> 3) ALTER TABLE TenderLines ALTER COLUMN Quantity decimal(18,3) NOT NULL",
            "IF COL_LENGTH('ContractLines','BoqCode') IS NULL ALTER TABLE ContractLines ADD BoqCode nvarchar(30) NULL",
            "IF COL_LENGTH('ContractLines','BoqPath') IS NULL ALTER TABLE ContractLines ADD BoqPath nvarchar(500) NULL",
            "IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('ContractLines') AND name = 'Quantity' AND scale <> 3) ALTER TABLE ContractLines ALTER COLUMN Quantity decimal(18,3) NOT NULL",
            "IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PurchaseOrderLines') AND name = 'Quantity' AND scale <> 3) ALTER TABLE PurchaseOrderLines ALTER COLUMN Quantity decimal(18,3) NOT NULL",
            "IF COL_LENGTH('ContractLines','OriginalQuantity') IS NULL ALTER TABLE ContractLines ADD OriginalQuantity decimal(18,3) NULL",
            "IF COL_LENGTH('ContractLines','AddedByAmendment') IS NULL ALTER TABLE ContractLines ADD AddedByAmendment nvarchar(30) NULL",
            "IF COL_LENGTH('ProcurementPackages','Field') IS NULL ALTER TABLE ProcurementPackages ADD Field nvarchar(100) NULL",
            // Ngân hàng giá: lịch sử giá chi tiết (vật tư / thi công – dịch vụ, thuế suất, tiền tệ, gói thầu / hợp đồng liên quan, vùng)
            "IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('PriceRecords') AND name = 'UnitPrice' AND scale <> 2) ALTER TABLE PriceRecords ALTER COLUMN UnitPrice decimal(18,2) NOT NULL",
            "IF COL_LENGTH('PriceRecords','Unit') IS NULL ALTER TABLE PriceRecords ADD Unit nvarchar(30) NULL",
            "IF COL_LENGTH('PriceRecords','SubCode') IS NULL ALTER TABLE PriceRecords ADD SubCode nvarchar(50) NULL",
            "IF COL_LENGTH('PriceRecords','Currency') IS NULL ALTER TABLE PriceRecords ADD Currency nvarchar(10) NULL",
            "IF COL_LENGTH('PriceRecords','MaterialPrice') IS NULL ALTER TABLE PriceRecords ADD MaterialPrice decimal(18,2) NOT NULL CONSTRAINT DF_PriceRecords_Mat DEFAULT 0",
            "IF COL_LENGTH('PriceRecords','MaterialVatPercent') IS NULL ALTER TABLE PriceRecords ADD MaterialVatPercent decimal(5,2) NOT NULL CONSTRAINT DF_PriceRecords_MatVat DEFAULT 0",
            "IF COL_LENGTH('PriceRecords','ServicePrice') IS NULL ALTER TABLE PriceRecords ADD ServicePrice decimal(18,2) NOT NULL CONSTRAINT DF_PriceRecords_Svc DEFAULT 0",
            "IF COL_LENGTH('PriceRecords','ServiceVatPercent') IS NULL ALTER TABLE PriceRecords ADD ServiceVatPercent decimal(5,2) NOT NULL CONSTRAINT DF_PriceRecords_SvcVat DEFAULT 0",
            "IF COL_LENGTH('PriceRecords','TenderId') IS NULL ALTER TABLE PriceRecords ADD TenderId int NULL CONSTRAINT FK_PriceRecords_Tenders_TenderId REFERENCES Tenders(Id)",
            "IF COL_LENGTH('PriceRecords','ContractId') IS NULL ALTER TABLE PriceRecords ADD ContractId int NULL CONSTRAINT FK_PriceRecords_Contracts_ContractId REFERENCES Contracts(Id)",
            "IF COL_LENGTH('PriceRecords','Region') IS NULL ALTER TABLE PriceRecords ADD Region nvarchar(100) NULL",
            "IF COL_LENGTH('PriceRecords','Note') IS NULL ALTER TABLE PriceRecords ADD Note nvarchar(1000) NULL",
            "IF COL_LENGTH('PriceRecords','UpdatedAt') IS NULL ALTER TABLE PriceRecords ADD UpdatedAt datetime2 NULL",
            // giá cũ chỉ có một đơn giá: coi là đơn giá vật tư, tiền Việt
            "UPDATE PriceRecords SET MaterialPrice = UnitPrice WHERE MaterialPrice = 0 AND ServicePrice = 0 AND UnitPrice > 0",
            "UPDATE PriceRecords SET Currency = 'VND' WHERE Currency IS NULL",
            // BOQ: cơ cấu đơn giá (vật liệu / nhân công / máy) và cơ cấu chi phí của gói
            "IF COL_LENGTH('BoqItems','MaterialPrice') IS NULL ALTER TABLE BoqItems ADD MaterialPrice decimal(18,0) NOT NULL CONSTRAINT DF_BoqItems_Mat DEFAULT 0",
            "IF COL_LENGTH('BoqItems','LaborPrice') IS NULL ALTER TABLE BoqItems ADD LaborPrice decimal(18,0) NOT NULL CONSTRAINT DF_BoqItems_Lab DEFAULT 0",
            "IF COL_LENGTH('BoqItems','MachinePrice') IS NULL ALTER TABLE BoqItems ADD MachinePrice decimal(18,0) NOT NULL CONSTRAINT DF_BoqItems_Mac DEFAULT 0",
            "IF COL_LENGTH('ProcurementPackages','BoqOverheadPercent') IS NULL ALTER TABLE ProcurementPackages ADD BoqOverheadPercent decimal(5,2) NOT NULL CONSTRAINT DF_Pkg_Ovh DEFAULT 0",
            "IF COL_LENGTH('ProcurementPackages','BoqProfitPercent') IS NULL ALTER TABLE ProcurementPackages ADD BoqProfitPercent decimal(5,2) NOT NULL CONSTRAINT DF_Pkg_Prf DEFAULT 0",
            "IF COL_LENGTH('ProcurementPackages','BoqContingencyPercent') IS NULL ALTER TABLE ProcurementPackages ADD BoqContingencyPercent decimal(5,2) NOT NULL CONSTRAINT DF_Pkg_Cnt DEFAULT 0",
            "IF COL_LENGTH('ProcurementPackages','BoqVatPercent') IS NULL ALTER TABLE ProcurementPackages ADD BoqVatPercent decimal(5,2) NOT NULL CONSTRAINT DF_Pkg_Vat DEFAULT 10",
            // Thanh toán theo khối lượng nghiệm thu BOQ (khóa ngoại tới BoqAcceptances do script bảng mới tạo)
            "IF COL_LENGTH('Payments','AcceptanceId') IS NULL ALTER TABLE Payments ADD AcceptanceId int NULL",
            // Giao diện riêng của người dùng
            "IF COL_LENGTH('Users','UiPrefs') IS NULL ALTER TABLE Users ADD UiPrefs nvarchar(1000) NULL",
            // Thiết lập: cấu hình dài (mẫu email), phân cấp nhóm hàng
            "IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('AppSettings') AND name = 'Value' AND max_length <> -1) ALTER TABLE AppSettings ALTER COLUMN Value nvarchar(max) NULL",
            "IF COL_LENGTH('Categories','ParentId') IS NULL ALTER TABLE Categories ADD ParentId int NULL CONSTRAINT FK_Categories_Categories_ParentId REFERENCES Categories(Id)",
            "IF COL_LENGTH('Categories','SortOrder') IS NULL ALTER TABLE Categories ADD SortOrder int NOT NULL CONSTRAINT DF_Categories_SortOrder DEFAULT 0",
            "IF COL_LENGTH('Categories','Description') IS NULL ALTER TABLE Categories ADD Description nvarchar(500) NULL",
            "IF COL_LENGTH('Categories','IsActive') IS NULL ALTER TABLE Categories ADD IsActive bit NOT NULL CONSTRAINT DF_Categories_IsActive DEFAULT 1",
            // Dữ liệu cũ: chứng từ đã đi qua RFQ coi là chào hàng cạnh tranh
            "UPDATE PurchaseRequests SET Method = 3 WHERE Method IS NULL AND Id IN (SELECT PurchaseRequestId FROM Rfqs WHERE PurchaseRequestId IS NOT NULL)",
            "UPDATE PurchaseOrders SET Method = 3 WHERE Method IS NULL AND RfqId IS NOT NULL"
        };
        foreach (var sql in statements) await db.Database.ExecuteSqlRawAsync(sql);
        await EProcurement.Web.Infrastructure.Packages.EnsureLegacyAsync(db);
        await EProcurement.Web.Infrastructure.StandardPrices.SyncReferenceAsync(db); // bảng giá tiêu chuẩn tới ngày hiệu lực // PR đã chọn hình thức trước khi có gói → sinh gói + KHLCNT tương ứng
        await EProcurement.Web.Infrastructure.Catalogs.EnsureAsync(db); // danh mục dùng chung: nạp mặc định + giá trị đang dùng
        await EProcurement.Web.Infrastructure.Packages.BackfillFieldsAsync(db); // gói cũ chưa có lĩnh vực: suy ra từ nhóm hàng / tên gói
        await EProcurement.Web.Infrastructure.OrgUnits.EnsureAsync(db); // cơ cấu tổ chức: khởi tạo từ phòng ban đang dùng, đồng bộ danh mục Phòng ban
        await EProcurement.Web.Infrastructure.CriteriaSeed.EnsureAsync(db); // thư viện bộ tiêu chí mẫu
    }

    /// <summary>
    /// Tạo các bảng mới (có trong model nhưng chưa có trong database cũ) bằng chính script EF sinh ra,
    /// kèm các index của những bảng đó. Bảng đã tồn tại không bị đụng tới.
    /// </summary>
    private static async Task CreateMissingTablesAsync(EProcDbContext db)
    {
        var existing = (await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables").ToListAsync())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batches = System.Text.RegularExpressions.Regex.Split(db.Database.GenerateCreateScript(), @"^\s*GO\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (var raw in batches)
        {
            var sql = raw.Trim();
            if (sql.Length == 0) continue;
            var table = System.Text.RegularExpressions.Regex.Match(sql, @"^CREATE TABLE \[(\w+)\]");
            if (table.Success)
            {
                if (existing.Contains(table.Groups[1].Value)) continue;
                await db.Database.ExecuteSqlRawAsync(sql);
                created.Add(table.Groups[1].Value);
                continue;
            }
            var index = System.Text.RegularExpressions.Regex.Match(sql, @"^CREATE (UNIQUE )?INDEX \[\w+\] ON \[(\w+)\]");
            if (index.Success && created.Contains(index.Groups[2].Value)) await db.Database.ExecuteSqlRawAsync(sql);
        }
    }
}
