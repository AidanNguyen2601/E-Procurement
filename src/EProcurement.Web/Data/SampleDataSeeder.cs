using System.Text;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Data;

public record SampleDataStatus(bool Loaded, int Requests, int Orders, int Rfqs, int DirectAwards, int Tenders, int Contracts, int Receipts);

/// <summary>
/// Bộ dữ liệu mẫu cho toàn bộ quy trình (≈ 15 tháng, 6 phòng ban): mua sắm trực tiếp, chào hàng cạnh tranh,
/// chỉ định thầu, đấu thầu rộng rãi, hợp đồng (kể cả hợp đồng khung), nhận hàng, đánh giá NCC.
/// Mọi chứng từ mẫu có số bắt đầu bằng "MAU-" nên không ảnh hưởng đến dãy số chứng từ thật và gỡ được hoàn toàn.
/// Danh mục (người dùng, hàng hóa, NCC) được bổ sung nếu chưa có và được giữ lại khi gỡ dữ liệu mẫu.
/// </summary>
public static class SampleDataSeeder
{
    public const string Prefix = "MAU-";

    public static async Task<SampleDataStatus> StatusAsync(EProcDbContext db) => new(
        await db.PurchaseRequests.AnyAsync(x => x.Number.StartsWith(Prefix)),
        await db.PurchaseRequests.CountAsync(x => x.Number.StartsWith(Prefix)),
        await db.PurchaseOrders.CountAsync(x => x.Number.StartsWith(Prefix)),
        await db.Rfqs.CountAsync(x => x.Number.StartsWith(Prefix)),
        await db.DirectAwards.CountAsync(x => x.Number.StartsWith(Prefix)),
        await db.Tenders.CountAsync(x => x.Number.StartsWith(Prefix)),
        await db.Contracts.CountAsync(x => x.Number.StartsWith(Prefix)),
        await db.GoodsReceipts.CountAsync(x => x.Number.StartsWith(Prefix)));

    public static async Task LoadAsync(EProcDbContext db, IPasswordHasher<AppUser> hasher)
    {
        if (await db.PurchaseRequests.AnyAsync(x => x.Number.StartsWith(Prefix)))
            throw new InvalidOperationException("Dữ liệu mẫu đã được nạp. Gỡ dữ liệu mẫu trước nếu muốn nạp lại.");
        await using var tx = await db.Database.BeginTransactionAsync();
        await new Builder(db, hasher).RunAsync();
        await tx.CommitAsync();
        await Catalogs.EnsureAsync(db); // bổ sung phòng ban mới vào danh mục
        await OrgUnits.EnsureAsync(db); // phòng ban mẫu → đơn vị trong cơ cấu tổ chức
        await Packages.EnsureLegacyAsync(db); // gói mua sắm + KHLCNT cho các đề nghị mẫu đã chọn hình thức
    }

    /// <summary>Gỡ toàn bộ chứng từ mẫu (số "MAU-…") cùng lịch sử, file đính kèm; giữ lại danh mục.</summary>
    public static async Task RemoveAsync(EProcDbContext db)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var prIds = await db.PurchaseRequests.Where(x => x.Number.StartsWith(Prefix)).Select(x => x.Id).ToListAsync();
        // Chứng từ (kể cả do người dùng tạo thêm) bám vào PR / gói thầu mẫu cũng bị gỡ theo
        var tenderIds = await db.Tenders.Where(x => x.Number.StartsWith(Prefix) || prIds.Contains(x.PurchaseRequestId)).Select(x => x.Id).ToListAsync();
        var daIds = await db.DirectAwards.Where(x => x.Number.StartsWith(Prefix) || prIds.Contains(x.PurchaseRequestId)).Select(x => x.Id).ToListAsync();
        var contractIds = await db.Contracts.Where(x => x.Number.StartsWith(Prefix) || (x.TenderId != null && tenderIds.Contains(x.TenderId.Value))).Select(x => x.Id).ToListAsync();
        var rfqIds = await db.Rfqs.Where(x => x.Number.StartsWith(Prefix)).Select(x => x.Id).ToListAsync();
        var poIds = await db.PurchaseOrders.Where(x => x.Number.StartsWith(Prefix)
            || (x.ContractId != null && contractIds.Contains(x.ContractId.Value)) || (x.DirectAwardId != null && daIds.Contains(x.DirectAwardId.Value))).Select(x => x.Id).ToListAsync();

        var pkgIds = await db.ProcurementPackages.Where(x => x.Number.StartsWith(Prefix) || x.PurchaseRequests.Any(p => prIds.Contains(p.Id))).Select(x => x.Id).ToListAsync();

        // Chứng từ thật tham chiếu tới chứng từ mẫu → bỏ liên kết
        await db.PurchaseRequests.Where(x => !prIds.Contains(x.Id) && x.PackageId != null && pkgIds.Contains(x.PackageId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageId, (int?)null));
        await db.Rfqs.Where(x => !rfqIds.Contains(x.Id) && x.PackageId != null && pkgIds.Contains(x.PackageId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageId, (int?)null));
        await db.PurchaseOrders.Where(x => !poIds.Contains(x.Id) && x.PackageId != null && pkgIds.Contains(x.PackageId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageId, (int?)null));
        await db.Tenders.Where(x => !tenderIds.Contains(x.Id) && x.PackageId != null && pkgIds.Contains(x.PackageId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageId, (int?)null));
        await db.DirectAwards.Where(x => !daIds.Contains(x.Id) && x.PackageId != null && pkgIds.Contains(x.PackageId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PackageId, (int?)null));
        await db.PurchaseOrders.Where(x => !poIds.Contains(x.Id) && x.ReferencePoId != null && poIds.Contains(x.ReferencePoId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReferencePoId, (int?)null));
        await db.PurchaseOrders.Where(x => !poIds.Contains(x.Id) && x.RfqId != null && rfqIds.Contains(x.RfqId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.RfqId, (int?)null));
        await db.PurchaseOrders.Where(x => !poIds.Contains(x.Id) && x.PurchaseRequestId != null && prIds.Contains(x.PurchaseRequestId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PurchaseRequestId, (int?)null));
        await db.Rfqs.Where(x => !rfqIds.Contains(x.Id) && x.PurchaseRequestId != null && prIds.Contains(x.PurchaseRequestId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PurchaseRequestId, (int?)null));
        await db.PurchaseRequests.Where(x => x.ContractId != null && contractIds.Contains(x.ContractId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContractId, (int?)null));
        await db.ProcurementPlanLines.Where(x => x.PurchaseRequestId != null && prIds.Contains(x.PurchaseRequestId.Value)).ExecuteUpdateAsync(s => s.SetProperty(x => x.PurchaseRequestId, (int?)null));

        // Hóa đơn, thanh toán, giao hàng, nộp online gắn với chứng từ mẫu
        var invIds = await db.Invoices.Where(x => poIds.Contains(x.PurchaseOrderId)).Select(x => x.Id).ToListAsync();
        var payIds = await db.Payments.Where(x => (x.InvoiceId != null && invIds.Contains(x.InvoiceId.Value)) || (x.ContractId != null && contractIds.Contains(x.ContractId.Value))).Select(x => x.Id).ToListAsync();
        await db.Payments.Where(x => payIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.InvoiceLines.Where(x => invIds.Contains(x.InvoiceId)).ExecuteDeleteAsync();
        await db.Invoices.Where(x => invIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PoShipments.Where(x => poIds.Contains(x.PurchaseOrderId)).ExecuteDeleteAsync();
        await db.VendorSubmissions.Where(x => (x.RfqVendorId != null && rfqIds.Contains(x.RfqVendor!.RfqId)) || (x.TenderBidderId != null && tenderIds.Contains(x.TenderBidder!.TenderId))
            || (x.DirectAwardId != null && daIds.Contains(x.DirectAwardId.Value))).ExecuteDeleteAsync();
        await db.TenderNegotiations.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.TenderAppraisals.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.TenderQuestions.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.ContractAmendments.Where(x => contractIds.Contains(x.ContractId)).ExecuteDeleteAsync();
        await db.ContractMilestones.Where(x => contractIds.Contains(x.ContractId)).ExecuteDeleteAsync();
        await db.ContractGuarantees.Where(x => contractIds.Contains(x.ContractId)).ExecuteDeleteAsync();

        await db.VendorEvaluations.Where(x => x.PurchaseOrderId != null && poIds.Contains(x.PurchaseOrderId.Value)).ExecuteDeleteAsync();
        await db.GoodsReceiptLines.Where(x => poIds.Contains(x.GoodsReceipt!.PurchaseOrderId)).ExecuteDeleteAsync();
        await db.GoodsReceipts.Where(x => poIds.Contains(x.PurchaseOrderId)).ExecuteDeleteAsync();
        await db.PurchaseOrderLines.Where(x => poIds.Contains(x.PurchaseOrderId)).ExecuteDeleteAsync();
        await db.PurchaseOrders.Where(x => poIds.Contains(x.Id)).ExecuteDeleteAsync();

        await db.ContractLines.Where(x => contractIds.Contains(x.ContractId)).ExecuteDeleteAsync();
        await db.Contracts.Where(x => contractIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.TenderScores.Where(x => tenderIds.Contains(x.TenderBidder!.TenderId)).ExecuteDeleteAsync();
        await db.TenderBidLines.Where(x => tenderIds.Contains(x.TenderBidder!.TenderId)).ExecuteDeleteAsync();
        await db.TenderBidders.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.TenderCommitteeMembers.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.TenderClarifications.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.TenderComplaints.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.TenderLines.Where(x => tenderIds.Contains(x.TenderId)).ExecuteDeleteAsync();
        await db.Tenders.Where(x => tenderIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.DirectAwardLines.Where(x => daIds.Contains(x.DirectAwardId)).ExecuteDeleteAsync();
        await db.DirectAwards.Where(x => daIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.VendorQuoteLines.Where(x => rfqIds.Contains(x.RfqVendor!.RfqId)).ExecuteDeleteAsync();
        await db.RfqVendors.Where(x => rfqIds.Contains(x.RfqId)).ExecuteDeleteAsync();
        await db.RfqLines.Where(x => rfqIds.Contains(x.RfqId)).ExecuteDeleteAsync();
        await db.Rfqs.Where(x => rfqIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.PurchaseRequestLines.Where(x => prIds.Contains(x.PurchaseRequestId)).ExecuteDeleteAsync();
        await db.PurchaseRequests.Where(x => prIds.Contains(x.Id)).ExecuteDeleteAsync();
        await db.ProcurementPackages.Where(x => pkgIds.Contains(x.Id)).ExecuteDeleteAsync();
        var emptyPlans = await db.SelectionPlans.Where(x => (x.Number.StartsWith(Prefix) || x.Number.StartsWith("KH-CD-")) && !x.Packages.Any()).Select(x => x.Id).ToListAsync();
        await db.SelectionPlans.Where(x => emptyPlans.Contains(x.Id)).ExecuteDeleteAsync();
        foreach (var (type, ids) in new[] { (DocType.Package, pkgIds), (DocType.SelectionPlan, emptyPlans) })
            await db.ApprovalHistories.Where(x => x.DocumentType == type && ids.Contains(x.DocumentId)).ExecuteDeleteAsync();

        foreach (var (type, ids) in new[] { (DocType.PR, prIds), (DocType.PO, poIds), (DocType.RFQ, rfqIds), (DocType.DirectAward, daIds), (DocType.Tender, tenderIds), (DocType.Contract, contractIds), (DocType.Invoice, invIds), (DocType.Payment, payIds) })
        {
            await db.ApprovalHistories.Where(x => x.DocumentType == type && ids.Contains(x.DocumentId)).ExecuteDeleteAsync();
            await db.Attachments.Where(x => x.DocumentType == type && ids.Contains(x.DocumentId)).ExecuteDeleteAsync();
        }
        await db.Budgets.Where(b => b.Note == Builder.BudgetNote).ExecuteDeleteAsync();
        await tx.CommitAsync();
        await RecalculateRatingsAsync(db);
    }

    /// <summary>Điểm NCC = trung bình các phiếu đánh giá (giống khi đánh giá trên hệ thống); NCC chưa có phiếu giữ nguyên điểm.</summary>
    private static async Task RecalculateRatingsAsync(EProcDbContext db)
    {
        var avg = await db.VendorEvaluations.GroupBy(e => e.VendorId)
            .Select(g => new { g.Key, Avg = g.Average(e => (e.Quality + e.Delivery + e.Price + e.Service) / 4m) }).ToListAsync();
        foreach (var a in avg)
        {
            var v = await db.Vendors.FindAsync(a.Key);
            if (v != null) v.Rating = Math.Round(a.Avg, 2);
        }
        await db.SaveChangesAsync();
    }

    // =====================================================================
    private sealed class Builder
    {
        private record Quote(Vendor V, int? Lead, decimal? Factor, bool? Tech = true, int? Score = null, string? Note = null);

        private readonly EProcDbContext db;
        private readonly IPasswordHasher<AppUser> hasher;
        private readonly Random rnd = new(2026);
        private readonly DateTime today = DateTime.Today;
        private readonly Dictionary<string, int> seq = new();
        private readonly List<(DocType Type, Func<int> Id, int UserId, string Action, DateTime At, string? Comment)> logs = new();
        private readonly List<(Attachment File, Func<int> Id)> files = new();
        private readonly List<PurchaseRequest> prs = new();
        private readonly List<PurchaseOrder> pos = new();
        private readonly Dictionary<PurchaseOrder, DateTime> lastReceipt = new();

        private AppUser admin = null!, director = null!, buyer = null!, buyer2 = null!, accountant = null!;
        private Contract contractA = null!, contractB = null!;
        private readonly Dictionary<string, (AppUser Req, AppUser Appr)> depts = new();
        private readonly Dictionary<string, Category> cat = new();
        private readonly Dictionary<string, Item> it = new();
        private readonly Dictionary<string, Vendor> vd = new();

        private const string CNTT = "Phòng CNTT", HC = "Phòng Hành chính", KT = "Phòng Kế toán", KD = "Phòng Kinh doanh", KTH = "Phòng Kỹ thuật", NS = "Phòng Nhân sự";

        public Builder(EProcDbContext db, IPasswordHasher<AppUser> hasher) { this.db = db; this.hasher = hasher; }

        public async Task RunAsync()
        {
            await MasterDataAsync();
            CompetitiveAndDirectPurchases();
            DirectAwards();
            Tenders();
            PipelineRequests();
            await BudgetsAsync();
            FinishRequests();
            Evaluations();
            Finance();
            await db.SaveChangesAsync();

            foreach (var l in logs)
                db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = l.Type, DocumentId = l.Id(), UserId = l.UserId, Action = l.Action, Comment = l.Comment, CreatedAt = l.At });
            foreach (var f in files) { f.File.DocumentId = f.Id(); db.Attachments.Add(f.File); }
            await db.SaveChangesAsync();
            await RecalculateRatingsAsync(db);
        }

        // ------------------------------------------------------------------ danh mục
        private async Task MasterDataAsync()
        {
            async Task<AppUser> U(string u, string name, string role, string dept)
            {
                var x = await db.Users.FirstOrDefaultAsync(a => a.Username == u);
                if (x != null) return x;
                x = new AppUser { Username = u, FullName = name, Role = role, Department = dept, Email = $"{u}@company.local" };
                x.PasswordHash = hasher.HashPassword(x, "Abc@123");
                db.Users.Add(x);
                return x;
            }
            admin = await U("admin", "Quản trị hệ thống", AppRoles.Admin, CNTT);
            director = await U("director", "Nguyễn Văn Đức", AppRoles.Director, "Ban Giám đốc");
            buyer = await U("buyer", "Vũ Thị Thu", AppRoles.Buyer, "Phòng Mua hàng");
            buyer2 = await U("buyer2", "Đỗ Quang Vinh", AppRoles.Buyer, "Phòng Mua hàng");
            accountant = await U("ketoan.tt", "Phạm Thị Kim Oanh", AppRoles.Accountant, KT);
            depts[CNTT] = (await U("requester", "Trần Văn Nam", AppRoles.Requester, CNTT), await U("approver", "Lê Thị Hồng", AppRoles.Approver, CNTT));
            depts[HC] = (await U("requester2", "Hoàng Thị Mai", AppRoles.Requester, HC), await U("approver2", "Phan Minh Quân", AppRoles.Approver, HC));
            depts[KT] = (await U("ketoan", "Nguyễn Thị Lan Anh", AppRoles.Requester, KT), await U("ketoan.tp", "Bùi Văn Hải", AppRoles.Approver, KT));
            depts[KD] = (await U("kinhdoanh", "Mai Thanh Hương", AppRoles.Requester, KD), await U("kinhdoanh.tp", "La Văn Khoa", AppRoles.Approver, KD));
            depts[KTH] = (await U("kythuat", "Trịnh Văn Tùng", AppRoles.Requester, KTH), await U("kythuat.tp", "Đinh Quốc Bảo", AppRoles.Approver, KTH));
            depts[NS] = (await U("nhansu", "Lương Thị Ngọc", AppRoles.Requester, NS), await U("nhansu.tp", "Tạ Minh Châu", AppRoles.Approver, NS));

            async Task C(string code, string name) =>
                cat[code] = await db.Categories.FirstOrDefaultAsync(c => c.Code == code) ?? db.Categories.Add(new Category { Code = code, Name = name }).Entity;
            await C("IT", "Thiết bị CNTT"); await C("VPP", "Văn phòng phẩm"); await C("NT", "Nội thất văn phòng");
            await C("BHLD", "Bảo hộ lao động"); await C("DV", "Dịch vụ"); await C("SW", "Phần mềm & bản quyền");
            await db.SaveChangesAsync();

            async Task I(string code, string name, string c, string unit, decimal price, string? spec = null) =>
                it[code] = await db.Items.FirstOrDefaultAsync(x => x.Code == code)
                           ?? db.Items.Add(new Item { Code = code, Name = name, CategoryId = cat[c].Id, Unit = unit, ReferencePrice = price, Specification = spec }).Entity;
            await I("IT-LAP-01", "Laptop văn phòng 14 inch", "IT", "Cái", 22_000_000, "Core i5 / 16GB RAM / SSD 512GB / Win 11 Pro");
            await I("IT-MON-24", "Màn hình 24 inch Full HD", "IT", "Cái", 4_500_000, "IPS, 75Hz, HDMI + DisplayPort");
            await I("IT-NET-SW", "Switch 24 port Gigabit", "IT", "Cái", 6_800_000, "Managed L2, 4 SFP");
            await I("IT-NET-AP", "Bộ phát WiFi 6 ốp trần", "IT", "Cái", 3_900_000);
            await I("IT-SRV-01", "Máy chủ rack 2U", "IT", "Cái", 185_000_000, "2 x Xeon Silver / 256GB RAM / 4 x SSD 1.92TB / nguồn kép");
            await I("IT-NAS", "Thiết bị lưu trữ NAS 4 khay", "IT", "Cái", 32_000_000, "4 x 8TB, RAID 5, 2 x 10GbE");
            await I("IT-UPS-3K", "Bộ lưu điện UPS 3kVA online", "IT", "Cái", 14_500_000, "Online double conversion, rack/tower");
            await I("IT-PRN-A4", "Máy in laser đen trắng A4", "IT", "Cái", 6_200_000, "In 2 mặt tự động, in mạng, 38 trang/phút");
            await I("IT-CAM-IP", "Camera IP 4MP ngoài trời", "IT", "Cái", 2_850_000, "Hồng ngoại 30m, IP67, PoE");
            await I("SW-OFF365", "Microsoft 365 Business Standard (1 năm)", "SW", "License", 3_200_000);
            await I("SW-ERP-MT", "Bảo trì phần mềm ERP (năm)", "SW", "Gói", 96_000_000);
            await I("VPP-A4", "Giấy A4 Double A 70gsm", "VPP", "Ram", 78_000);
            await I("VPP-PEN", "Bút bi Thiên Long (hộp 20)", "VPP", "Hộp", 65_000);
            await I("VPP-FILE", "Bìa còng 7cm", "VPP", "Cái", 42_000);
            await I("VPP-INK", "Mực in laser HP 107A", "VPP", "Hộp", 950_000);
            await I("VPP-NOTE", "Sổ tay bìa da A5", "VPP", "Quyển", 55_000);
            await I("VPP-STAPLE", "Kim bấm số 10 (hộp)", "VPP", "Hộp", 6_000);
            await I("NT-DESK", "Bàn họp gỗ 2,4m", "NT", "Bộ", 18_500_000);
            await I("NT-CHAIR", "Ghế lưới văn phòng", "NT", "Cái", 1_850_000);
            await I("NT-CAB", "Tủ hồ sơ sắt 4 ngăn", "NT", "Cái", 3_400_000);
            await I("NT-WS", "Bàn làm việc cụm 4 chỗ", "NT", "Bộ", 12_800_000);
            await I("BH-HELM", "Mũ bảo hộ", "BHLD", "Cái", 120_000);
            await I("BH-SHOE", "Giày bảo hộ mũi thép", "BHLD", "Đôi", 450_000);
            await I("BH-GLOVE", "Găng tay chống cắt", "BHLD", "Đôi", 85_000);
            await I("DV-CLEAN", "Dịch vụ vệ sinh văn phòng (tháng)", "DV", "Tháng", 15_000_000);
            await I("DV-SEC", "Dịch vụ bảo vệ tòa nhà (tháng)", "DV", "Tháng", 42_000_000);
            await I("DV-MAINT", "Bảo trì hệ thống điều hòa (lần)", "DV", "Lần", 8_500_000);

            async Task V(string code, string name, string tax, decimal rating, string contact, params string[] cats)
            {
                var v = await db.Vendors.Include(x => x.Categories).FirstOrDefaultAsync(x => x.Code == code || x.TaxCode == tax);
                if (v == null)
                {
                    v = new Vendor
                    {
                        Code = code, Name = name, TaxCode = tax, Status = VendorStatus.Approved, Rating = rating, ContactPerson = contact,
                        ContactPhone = "09" + tax[^8..], Phone = "028 3" + tax[^7..], Email = $"sales@{code.ToLower()}.vn", Address = "TP. Hồ Chí Minh",
                        PaymentTerms = "Thanh toán 30 ngày sau khi nhận hàng và hóa đơn", BankName = "Vietcombank", BankAccount = "0071" + tax[^6..]
                    };
                    db.Vendors.Add(v);
                }
                foreach (var c in cats.Where(c => v.Categories.All(x => x.CategoryId != cat[c].Id)))
                    v.Categories.Add(new VendorCategory { CategoryId = cat[c].Id });
                vd[code] = v;
            }
            await V("NCC001", "Công ty TNHH Thiết bị Tin học Sao Mai", "0312345671", 4.25m, "Ngô Minh Trí", "IT", "SW");
            await V("NCC002", "Công ty CP Công nghệ Phong Vũ Số", "0312345682", 4.0m, "Đặng Thu Trang", "IT");
            await V("NCC003", "Công ty TNHH Giải pháp Mạng Hoàng Long", "0312345693", 3.75m, "Lý Văn Long", "IT", "DV");
            await V("NCC004", "Công ty TNHH Văn phòng phẩm Hồng Hà Nam", "0312345604", 4.5m, "Trần Thị Hà", "VPP");
            await V("NCC005", "Công ty CP Nội thất Hòa Phát Miền Nam", "0312345615", 4.0m, "Phạm Quốc Huy", "NT");
            await V("NCC006", "Công ty TNHH Dịch vụ Vệ sinh Xanh", "0312345626", 3.5m, "Võ Thị Lan", "DV");
            await V("NCC009", "Công ty CP Hệ thống Số Thành Công", "0313456709", 4.0m, "Hồ Thành Công", "IT");
            await V("NCC010", "Công ty TNHH Công nghệ An Phát", "0313456710", 3.75m, "Kiều Anh Phát", "IT");
            await V("NCC011", "Công ty CP Nội thất Văn phòng Minh Khang", "0313456711", 4.25m, "Dương Minh Khang", "NT");
            await V("NCC012", "Công ty TNHH Dịch vụ Bảo vệ Long Hải", "0313456712", 4.0m, "Âu Văn Hải", "DV");
            await V("NCC013", "Công ty TNHH Phần mềm Bách Khoa", "0313456713", 4.5m, "Quách Thị Bích", "SW");
            await V("NCC014", "Công ty CP Văn phòng phẩm Thiên Phú", "0313456714", 3.75m, "Thân Văn Phú", "VPP");
            await V("NCC015", "Công ty TNHH Điện lạnh Sông Hàn", "0313456715", 3.5m, "Mạc Văn Hàn", "DV");
            await V("NCC016", "Công ty TNHH Thiết bị Bảo hộ Đại Việt", "0313456716", 4.0m, "Ninh Đại Việt", "BHLD");
            await V("NCC017", "Công ty TNHH Nội thất Gia Phúc", "0313456717", 3.5m, "Châu Gia Phúc", "NT");
            await db.SaveChangesAsync();
        }

        // ------------------------------------------------------------------ helpers
        private DateTime Day(int daysAgo) => today.AddDays(-daysAgo);
        private string No(string p, DateTime d) { var k = p + d.Year; seq[k] = seq.GetValueOrDefault(k) + 1; return $"{Prefix}{p}-{d:yyyy}-{seq[k]:000}"; }
        private void Log(DocType t, Func<int> id, AppUser u, string action, DateTime at, string? comment = null) => logs.Add((t, id, u.Id, action, at, comment));
        private static decimal R(decimal v) => Math.Round(v / 1000m) * 1000m;
        private decimal Jitter(double pct) => 1 + (decimal)((rnd.NextDouble() * 2 - 1) * pct);
        private double Hours(int min, int max) => rnd.Next(min, max + 1);
        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

        private PurchaseRequest Pr(string dept, string title, int daysAgo, PrStatus st, ProcurementMethod? method, (Item it, decimal q)[] lines,
            PrPriority pri = PrPriority.Normal, string? purpose = null, string? methodReason = null, string? rejectComment = null, bool rejectByDirector = false)
        {
            var (req, appr) = depts[dept];
            var d = Day(daysAgo);
            var active = st is not (PrStatus.Rejected or PrStatus.Cancelled);
            var pr = new PurchaseRequest
            {
                Number = No("PR", d), Title = title, RequesterId = req.Id, Department = dept, Status = st, Priority = pri, RequestDate = d,
                NeededDate = d.AddDays(pri == PrPriority.Normal ? 21 : 7), Purpose = purpose ?? "Phục vụ hoạt động của phòng ban.",
                CreatedAt = d.AddHours(8.5), SubmittedAt = st == PrStatus.Draft ? null : d.AddHours(9),
                Method = active && st >= PrStatus.Processing ? method : null, MethodReason = active && st >= PrStatus.Processing ? methodReason : null
            };
            var s = 1;
            foreach (var (item, q) in lines)
                pr.Lines.Add(new PurchaseRequestLine { ItemId = item.Id == 0 ? null : item.Id, Description = item.Name, Unit = item.Unit, Quantity = q, EstimatedPrice = item.ReferencePrice, SortOrder = s++ });
            pr.Recalculate();
            prs.Add(pr); db.PurchaseRequests.Add(pr);
            if (st == PrStatus.Draft) return pr;

            Func<int> id = () => pr.Id;
            var t = d.AddHours(9);
            Log(DocType.PR, id, req, "Gửi duyệt", t);
            if (st == PrStatus.Submitted) return pr;
            t = t.AddHours(Hours(2, 30));
            if (st == PrStatus.Cancelled) { Log(DocType.PR, id, req, "Hủy đề nghị", t, rejectComment); return pr; }
            if (st == PrStatus.Rejected && !rejectByDirector) { Log(DocType.PR, id, appr, "Trưởng bộ phận từ chối", t, rejectComment); return pr; }
            Log(DocType.PR, id, appr, "Trưởng bộ phận duyệt", t);
            if (st == PrStatus.PendingDirector) return pr;
            if (pr.EstimatedTotal > 50_000_000 || rejectByDirector)
            {
                t = t.AddHours(Hours(3, 40));
                if (st == PrStatus.Rejected) { Log(DocType.PR, id, director, "Giám đốc từ chối", t, rejectComment); return pr; }
                Log(DocType.PR, id, director, "Giám đốc duyệt", t);
            }
            if (st >= PrStatus.Processing && method.HasValue)
                Log(DocType.PR, id, buyer, $"Chọn hình thức: {method.Value.GetDisplayName()}", t.AddHours(Hours(2, 20)), methodReason);
            return pr;
        }

        private PurchaseOrder Po(Vendor v, int daysAgo, ProcurementMethod method, PurchaseRequest? pr, int lead, IEnumerable<(Item it, decimal q, decimal p)> lines,
            string created, PoStatus st = PoStatus.Issued, string? approval = null, string? submitNote = null, AppUser? by = null, string? notes = null)
        {
            by ??= buyer;
            var d = Day(daysAgo);
            var po = new PurchaseOrder
            {
                Number = No("PO", d), VendorId = v.Id, Vendor = v, PurchaseRequest = pr, BuyerId = by.Id, Method = method, OrderDate = d, DeliveryDate = d.AddDays(lead),
                DeliveryAddress = "Kho công ty – Tầng 1, tòa nhà văn phòng", PaymentTerms = v.PaymentTerms, Status = st, Notes = notes, CreatedAt = d.AddHours(10)
            };
            var s = 1;
            foreach (var (item, q, p) in lines)
                po.Lines.Add(new PurchaseOrderLine { ItemId = item.Id == 0 ? null : item.Id, Description = item.Name, Unit = item.Unit, Quantity = q, UnitPrice = p, SortOrder = s++ });
            po.Recalculate();
            pos.Add(po); db.PurchaseOrders.Add(po);

            Func<int> id = () => po.Id;
            var t = d.AddHours(10);
            Log(DocType.PO, id, by, created, t);
            if (pr != null) Log(DocType.PR, () => pr.Id, by, $"Mua hàng lập {po.Number}", t);
            if (st == PoStatus.Draft) return po;
            t = t.AddHours(1);
            if (approval != null) Log(DocType.PO, id, by, approval, t);
            else if (po.Total > 100_000_000 || submitNote != null)
            {
                Log(DocType.PO, id, by, "Trình Giám đốc duyệt", t, submitNote ?? $"Giá trị {po.Total.ToMoney()} vượt ngưỡng {100_000_000m.ToMoney()}");
                if (st == PoStatus.PendingApproval) return po;
                t = t.AddHours(Hours(2, 6));
                Log(DocType.PO, id, director, "Giám đốc duyệt", t);
            }
            else Log(DocType.PO, id, by, "Duyệt (dưới ngưỡng phân cấp)", t);
            if (st >= PoStatus.Issued && st != PoStatus.Cancelled)
            {
                po.IssuedAt = t.AddHours(1);
                Log(DocType.PO, id, by, "Gửi NCC", po.IssuedAt.Value);
            }
            return po;
        }

        private void Cancel(PurchaseOrder po, int daysAgo, string reason)
        {
            po.Status = PoStatus.Cancelled;
            Log(DocType.PO, () => po.Id, buyer, "Hủy đơn mua hàng", Day(daysAgo).AddHours(15), reason);
        }

        private void Receive(PurchaseOrder po, int daysAgo, Func<PurchaseOrderLine, decimal>? acc = null, Func<PurchaseOrderLine, decimal>? rej = null, string? note = null)
        {
            var d = Day(daysAgo);
            var g = new GoodsReceipt
            {
                Number = No("GRN", d), PurchaseOrder = po, ReceiptDate = d, ReceivedById = buyer.Id, DeliveryNoteNo = "PGH-" + rnd.Next(10000, 99999),
                Note = note, CreatedAt = d.AddHours(14)
            };
            foreach (var l in po.Lines)
            {
                var a = Math.Min(l.RemainingQuantity, acc?.Invoke(l) ?? l.RemainingQuantity);
                var r = rej?.Invoke(l) ?? 0;
                if (a <= 0 && r <= 0) continue;
                g.Lines.Add(new GoodsReceiptLine { PurchaseOrderLine = l, QuantityAccepted = a, QuantityRejected = r, Note = r > 0 ? note : null });
                l.ReceivedQuantity += a;
            }
            if (g.Lines.Count == 0) return;
            po.Status = po.Lines.All(l => l.ReceivedQuantity >= l.Quantity) ? PoStatus.Received : PoStatus.PartiallyReceived;
            db.GoodsReceipts.Add(g);
            lastReceipt[po] = d;
            Log(DocType.PO, () => po.Id, buyer, $"Nhận hàng {g.Number}" + (po.Status == PoStatus.Received ? " – đã nhận đủ" : " – nhận một phần"), d.AddHours(14));
        }

        /// <summary>Nhận hàng với độ trễ ngẫu nhiên so với ngày giao (đa số đúng hạn); không nhận nếu rơi vào tương lai</summary>
        private void ReceiveTypical(PurchaseOrder po, double rejectChance = 0.06)
        {
            var due = (today - po.DeliveryDate).Days; // số ngày trước hôm nay của hạn giao
            int[] delays = { -1, 0, 0, 0, 0, 1, 2, 4 };
            var at = due - delays[rnd.Next(delays.Length)];
            if (at < 1) return;
            if (rnd.NextDouble() < rejectChance && po.Lines.Any(l => l.Quantity >= 2) && at >= 4)
            {
                var bad = po.Lines.Where(l => l.Quantity >= 2).OrderBy(_ => rnd.Next()).First();
                var badQty = Math.Max(1, Math.Floor(bad.Quantity * 0.1m));
                Receive(po, at, l => l == bad ? l.Quantity - badQty : l.Quantity, l => l == bad ? badQty : 0, "Hàng lỗi / không đúng quy cách – trả lại NCC đổi hàng");
                Receive(po, at - 3, note: "Nhận hàng đổi bù");
            }
            else Receive(po, at);
        }

        private Rfq NewRfq(PurchaseRequest pr, string title, int daysAgo, int days, RfqStatus st, EvaluationMethod em, Quote[] quotes,
            Vendor? pick = null, string? proposeReason = null, string? cancelReason = null)
        {
            var d = Day(daysAgo);
            var r = new Rfq
            {
                Number = No("RFQ", d), Title = title, PurchaseRequest = pr, BuyerId = buyer.Id, IssueDate = d, Deadline = d.AddDays(days), Status = st,
                EvaluationMethod = em, Requirements = "Hàng mới 100%, đúng quy cách; giá đã gồm vận chuyển, lắp đặt tại kho công ty; bảo hành tối thiểu 12 tháng.",
                CreatedAt = d.AddHours(9)
            };
            if (em == EvaluationMethod.WeightedScore) { r.PriceWeight = 60; r.TechnicalWeight = 30; r.DeliveryWeight = 10; }
            var s = 1;
            foreach (var l in pr.Lines)
                r.Lines.Add(new RfqLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, SortOrder = s++ });
            var opened = st is RfqStatus.Opened or RfqStatus.PendingApproval or RfqStatus.Awarded || (st == RfqStatus.Cancelled && cancelReason != null);
            foreach (var q in quotes)
            {
                var rv = new RfqVendor
                {
                    VendorId = q.V.Id, Vendor = q.V, Responded = q.Factor.HasValue, LeadTimeDays = q.Factor.HasValue ? q.Lead : null,
                    RespondedAt = q.Factor.HasValue ? Min(d.AddDays(rnd.Next(1, Math.Max(2, days))).AddHours(15), Day(1).AddHours(15)) : null,
                    PaymentTerms = q.V.PaymentTerms, Warranty = "12 tháng",
                    TechnicalPass = opened && q.Factor.HasValue ? q.Tech : null,
                    TechnicalScore = opened && q.Factor.HasValue && em == EvaluationMethod.WeightedScore ? q.Score : null,
                    TechnicalNote = opened ? q.Note : null
                };
                if (q.Factor is decimal f)
                {
                    foreach (var (rl, pl) in r.Lines.Zip(pr.Lines))
                    {
                        var price = R(pl.EstimatedPrice * f * Jitter(0.02));
                        rv.QuoteLines.Add(new VendorQuoteLine { RfqLine = rl, UnitPrice = price, Amount = price * rl.Quantity });
                    }
                    rv.TotalAmount = rv.QuoteLines.Sum(x => x.Amount);
                }
                r.Vendors.Add(rv);
            }
            db.Rfqs.Add(r);

            Func<int> id = () => r.Id;
            Log(DocType.PR, () => pr.Id, buyer, $"Mua hàng lập {r.Number}", d.AddHours(9));
            Log(DocType.RFQ, id, buyer, "Phát hành RFQ", d.AddHours(9), $"Hạn nộp báo giá {r.Deadline.ToDate()}");
            if (!opened)
            {
                if (st == RfqStatus.Cancelled) Log(DocType.RFQ, id, buyer, "Hủy RFQ", d.AddDays(1).AddHours(10), cancelReason ?? "Đổi kế hoạch mua sắm");
                return r;
            }
            var t = r.Deadline.AddDays(1).AddHours(9);
            r.OpenedAt = t;
            var responded = r.Vendors.Count(v => v.Responded);
            var missing = r.Vendors.Count - responded;
            Log(DocType.RFQ, id, buyer, $"Mở báo giá – {responded} báo giá" + (missing > 0 ? $", {missing} NCC không nộp" : ""), t);
            if (st == RfqStatus.Cancelled) { Log(DocType.RFQ, id, buyer, "Hủy RFQ", t.AddHours(3), cancelReason); return r; }
            if (!RfqEvaluation.AllTechnicallyEvaluated(r)) return r;
            t = t.AddDays(1);
            var failed = r.Vendors.Where(v => v.Responded && v.TechnicalPass == false).Select(v => v.Vendor!.Name).ToList();
            Log(DocType.RFQ, id, buyer, "Đánh giá kỹ thuật", t, failed.Count == 0 ? "Tất cả báo giá đạt kỹ thuật" : "Không đạt: " + string.Join(", ", failed));
            if (st == RfqStatus.Opened) return r;

            var results = RfqEvaluation.Evaluate(r);
            var chosen = pick != null ? results.First(x => x.Quote.VendorId == pick.Id) : results.First(x => x.Rank == 1);
            var valid = results.Count(x => x.Valid);
            var needApproval = chosen.Rank != 1 || valid < 3;
            r.ProposedVendorId = chosen.Quote.VendorId;
            if (needApproval) r.ResultApprovalReason = proposeReason ?? (valid < 3 ? $"Chỉ có {valid} báo giá hợp lệ (yêu cầu tối thiểu 3)." : "Không chọn NCC xếp hạng 1.");
            t = t.AddHours(4);
            Log(DocType.RFQ, id, buyer, $"Đề xuất NCC trúng: {chosen.Quote.Vendor!.Name} (hạng {chosen.Rank}, {chosen.Quote.GrandTotal.ToMoney()} đ gồm VAT)", t, proposeReason);
            if (needApproval) Log(DocType.RFQ, id, buyer, "Trình Giám đốc duyệt kết quả", t.AddMinutes(5), r.ResultApprovalReason);
            if (st == RfqStatus.PendingApproval) return r;
            t = t.AddHours(Hours(3, 26));
            if (needApproval) Log(DocType.RFQ, id, director, "Giám đốc duyệt kết quả", t, "Đồng ý với đề xuất của bộ phận mua hàng.");
            else Log(DocType.RFQ, id, buyer, "Duyệt kết quả (NCC hạng 1, đủ báo giá hợp lệ)", t);
            r.AwardedVendor = chosen.Quote.Vendor; r.AwardedVendorId = chosen.Quote.VendorId;
            r.AwardReason = proposeReason ?? (em == EvaluationMethod.WeightedScore ? "Tổng điểm cao nhất (giá – kỹ thuật – tiến độ)." : "Giá thấp nhất trong các báo giá đạt kỹ thuật.");
            return r;
        }

        private PurchaseOrder PoFromRfq(Rfq r, int daysAgo, int? lead = null, PoStatus st = PoStatus.Issued)
        {
            var w = r.Vendors.First(v => v.VendorId == r.AwardedVendorId);
            var po = Po(w.Vendor!, daysAgo, ProcurementMethod.CompetitiveQuotation, r.PurchaseRequest, lead ?? w.LeadTimeDays ?? 7,
                w.QuoteLines.Select(q => (it.Values.First(i => i.Id == q.RfqLine!.ItemId), q.RfqLine!.Quantity, q.UnitPrice)), $"Tạo từ {r.Number}", st);
            po.Rfq = r;
            return po;
        }

        private DirectAward Da(PurchaseRequest pr, Vendor v, int daysAgo, DirectAwardReason reason, string justification, DirectAwardStatus st,
            decimal proposed, decimal negotiated, int lead, (string name, string content)? evidence = null, string? splitWarning = null)
        {
            var d = Day(daysAgo);
            var da = new DirectAward
            {
                Number = No("DA", d), Title = pr.Title, PurchaseRequest = pr, VendorId = v.Id, Vendor = v, BuyerId = buyer.Id, Reason = reason, Justification = justification,
                Status = st, NegotiationDate = d.AddDays(1), LeadTimeDays = lead, PaymentTerms = v.PaymentTerms, Warranty = "12 tháng", SplitWarning = splitWarning,
                NegotiationNote = st == DirectAwardStatus.Draft ? "Đang thương thảo đơn giá và tiến độ." :
                    $"NCC chào {(proposed - 1) * 100:+0;-0;0}% so với dự toán; sau thương thảo giảm còn {(negotiated - 1) * 100:+0;-0;0}%, giữ nguyên tiến độ và bảo hành.",
                CreatedAt = d.AddHours(9)
            };
            var s = 1;
            foreach (var l in pr.Lines)
                da.Lines.Add(new DirectAwardLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice,
                    ProposedPrice = R(l.EstimatedPrice * proposed), NegotiatedPrice = R(l.EstimatedPrice * negotiated), SortOrder = s++ });
            da.Recalculate();
            db.DirectAwards.Add(da);

            Func<int> id = () => da.Id;
            Log(DocType.PR, () => pr.Id, buyer, $"Mua hàng lập {da.Number}", d.AddHours(9));
            Log(DocType.DirectAward, id, buyer, "Lập tờ trình chỉ định thầu", d.AddHours(9), $"{v.Name} – {reason.GetDisplayName()}");
            if (evidence is { } e)
                files.Add((new Attachment
                {
                    DocumentType = DocType.DirectAward, FileName = e.name, ContentType = "text/plain", Data = Encoding.UTF8.GetBytes(e.content),
                    Size = Encoding.UTF8.GetByteCount(e.content), Note = "Tài liệu chứng minh căn cứ chỉ định", UploadedById = buyer.Id, UploadedAt = d.AddHours(10)
                }, id));
            if (st == DirectAwardStatus.Draft) return da;
            da.SubmittedAt = d.AddDays(1).AddHours(16);
            Log(DocType.DirectAward, id, buyer, "Trình Giám đốc duyệt", da.SubmittedAt.Value, splitWarning);
            if (st == DirectAwardStatus.PendingApproval) return da;
            da.ApprovedAt = da.SubmittedAt.Value.AddHours(Hours(2, 20));
            Log(DocType.DirectAward, id, director, "Giám đốc duyệt chỉ định thầu", da.ApprovedAt.Value);
            return da;
        }

        private PurchaseOrder PoFromDa(DirectAward da, int daysAgo, PoStatus st = PoStatus.Issued)
        {
            var po = Po(da.Vendor!, daysAgo, ProcurementMethod.DirectAward, da.PurchaseRequest, da.LeadTimeDays ?? 7,
                da.Lines.Select(l => (l.ItemId.HasValue ? it.Values.First(i => i.Id == l.ItemId) : new Item { Name = l.Description, Unit = l.Unit ?? "Gói" }, l.Quantity, l.NegotiatedPrice)),
                $"Tạo từ tờ trình {da.Number}", st, approval: $"Duyệt theo tờ trình chỉ định {da.Number} đã được Giám đốc phê duyệt");
            po.DirectAward = da;
            return po;
        }

        // ------------------------------------------------------------------ 1. Chào hàng cạnh tranh + mua sắm trực tiếp
        private void CompetitiveAndDirectPurchases()
        {
            var vpp = new[] { "VPP-A4", "VPP-PEN", "VPP-FILE", "VPP-INK", "VPP-NOTE", "VPP-STAPLE" };

            // VPP dùng chung cả năm (đợt 1) → PO gốc làm căn cứ mua sắm trực tiếp
            var prA = Pr(HC, "Văn phòng phẩm dùng chung toàn công ty (khung 12 tháng)", 462, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation,
                new[] { (it["VPP-A4"], 1500m), (it["VPP-PEN"], 200m), (it["VPP-FILE"], 700m), (it["VPP-INK"], 80m), (it["VPP-NOTE"], 200m), (it["VPP-STAPLE"], 500m) },
                purpose: "Chọn NCC văn phòng phẩm làm căn cứ để các phòng ban mua sắm trực tiếp trong 12 tháng.");
            var rA = NewRfq(prA, "Văn phòng phẩm dùng chung 12 tháng", 458, 7, RfqStatus.Awarded, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC004"], 3, 0.97m), new Quote(vd["NCC014"], 4, 0.99m), new Quote(vd["NCC010"], 5, 1.03m)
            });
            var baseA = PoFromRfq(rA, 445, 5);
            Receive(baseA, 440);

            // Đợt 2 (đã quá 12 tháng của PO đợt 1)
            var prB = Pr(HC, "Văn phòng phẩm dùng chung (đợt 2)", 92, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation,
                new[] { (it["VPP-A4"], 400m), (it["VPP-PEN"], 60m), (it["VPP-FILE"], 200m), (it["VPP-INK"], 24m), (it["VPP-NOTE"], 60m), (it["VPP-STAPLE"], 150m) },
                purpose: "PO văn phòng phẩm đợt 1 đã hết hiệu lực làm căn cứ mua sắm trực tiếp (quá 12 tháng).");
            var rB = NewRfq(prB, "Văn phòng phẩm dùng chung (đợt 2)", 88, 7, RfqStatus.Awarded, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC004"], 3, 0.98m), new Quote(vd["NCC014"], 3, 0.95m), new Quote(vd["NCC010"], 6, 1.02m)
            });
            var baseB = PoFromRfq(rB, 76, 4);
            Receive(baseB, 72);

            // Mua sắm trực tiếp hằng tháng theo PO gốc (≤ 130% khối lượng)
            var used = new Dictionary<(PurchaseOrder, int), decimal>();
            var ranges = new Dictionary<string, (int min, int max)>
            {
                ["VPP-A4"] = (20, 60), ["VPP-PEN"] = (3, 10), ["VPP-FILE"] = (10, 30), ["VPP-INK"] = (1, 4), ["VPP-NOTE"] = (5, 15), ["VPP-STAPLE"] = (10, 30)
            };
            var cancelledOne = false;
            for (var month = 14; month >= 0; month--)
                foreach (var dept in depts.Keys)
                {
                    if (rnd.NextDouble() > 0.6) continue;
                    var day = month * 30 + rnd.Next(1, 26);
                    var basePo = day >= 80 ? baseA : day <= 69 ? baseB : null;
                    if (basePo == null || day > 437 || day < 1) continue;
                    var lines = new List<(Item, decimal, decimal)>();
                    foreach (var code in vpp.OrderBy(_ => rnd.Next()).Take(rnd.Next(2, 5)))
                    {
                        var item = it[code];
                        var bl = basePo.Lines.First(l => l.ItemId == item.Id);
                        var cap = Math.Floor(bl.Quantity * 1.3m) - used.GetValueOrDefault((basePo, item.Id));
                        var q = Math.Min(cap, rnd.Next(ranges[code].min, ranges[code].max + 1));
                        if (q <= 0) continue;
                        used[(basePo, item.Id)] = used.GetValueOrDefault((basePo, item.Id)) + q;
                        lines.Add((item, q, bl.UnitPrice));
                    }
                    if (lines.Count == 0) continue;
                    var d = Day(day);
                    var pr = Pr(dept, $"Văn phòng phẩm tháng {d:MM/yyyy}", day + 3, PrStatus.Processing, ProcurementMethod.DirectPurchase,
                        lines.Select(l => (l.Item1, l.Item2)).ToArray(), purpose: "Văn phòng phẩm sử dụng hằng tháng.");
                    var st = day <= 2 ? PoStatus.Approved : PoStatus.Issued;
                    var po = Po(basePo.Vendor!, day, ProcurementMethod.DirectPurchase, pr, rnd.Next(3, 6), lines, "Tạo đơn mua sắm trực tiếp", st,
                        notes: $"Mua sắm trực tiếp theo {basePo.Number}");
                    po.ReferencePo = basePo;
                    if (!cancelledOne && day is > 150 and < 250)
                    {
                        cancelledOne = true;
                        Cancel(po, day - 1, "Phòng ban điều chuyển dùng tồn kho của Phòng Hành chính, không cần mua.");
                        pr.Status = PrStatus.Cancelled;
                        continue;
                    }
                    if (st == PoStatus.Issued) ReceiveTypical(po);
                }

            // Mua sắm trực tiếp có đơn giá cao hơn PO tham chiếu → chờ Giám đốc duyệt
            var inkLine = baseB.Lines.First(l => l.ItemId == it["VPP-INK"].Id);
            var prInk = Pr(KT, "Mực in bổ sung quyết toán cuối năm", 5, PrStatus.Processing, ProcurementMethod.DirectPurchase, new[] { (it["VPP-INK"], 4m) },
                PrPriority.Urgent, "Phục vụ in chứng từ quyết toán.");
            var poInk = Po(baseB.Vendor!, 3, ProcurementMethod.DirectPurchase, prInk, 3, new[] { (it["VPP-INK"], 4m, R(inkLine.UnitPrice * 1.04m)) },
                "Tạo đơn mua sắm trực tiếp", PoStatus.PendingApproval,
                submitNote: "Mua sắm trực tiếp có đơn giá cao hơn PO tham chiếu: NCC điều chỉnh giá mực theo thông báo tăng giá của hãng từ tháng 11.");
            poInk.ReferencePo = baseB;
            poInk.PriceDeviationReason = "NCC điều chỉnh giá mực theo thông báo tăng giá của hãng từ tháng 11 (đính kèm thông báo).";

            // Dịch vụ vệ sinh 12 tháng – nghiệm thu hằng tháng
            var prClean = Pr(HC, "Dịch vụ vệ sinh văn phòng 12 tháng", 420, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation, new[] { (it["DV-CLEAN"], 12m) });
            var rClean = NewRfq(prClean, "Dịch vụ vệ sinh văn phòng 12 tháng", 416, 10, RfqStatus.Awarded, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC006"], 5, 0.96m), new Quote(vd["NCC015"], 7, 1.00m), new Quote(vd["NCC012"], 7, 1.05m)
            });
            var poClean = PoFromRfq(rClean, 405, 365);
            for (var day = 375; day >= 45; day -= 30) Receive(poClean, day, _ => 1, note: "Nghiệm thu dịch vụ tháng");

            // Dịch vụ bảo vệ 12 tháng – đang thực hiện
            var prSec = Pr(HC, "Dịch vụ bảo vệ tòa nhà 12 tháng", 340, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation, new[] { (it["DV-SEC"], 12m) });
            var rSec = NewRfq(prSec, "Dịch vụ bảo vệ tòa nhà 12 tháng", 336, 10, RfqStatus.Awarded, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC012"], 3, 0.95m), new Quote(vd["NCC006"], 5, 0.99m), new Quote(vd["NCC015"], 7, 1.04m)
            });
            var poSec = PoFromRfq(rSec, 322, 365);
            for (var day = 292; day >= 2; day -= 30) Receive(poSec, day, _ => 1, note: "Nghiệm thu dịch vụ tháng");

            // Máy in: NCC rẻ nhất không đạt kỹ thuật
            var prPrn = Pr(CNTT, "Máy in laser cho các phòng ban", 300, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation,
                new[] { (it["IT-PRN-A4"], 12m), (it["VPP-INK"], 24m) });
            var rPrn = NewRfq(prPrn, "Máy in laser cho các phòng ban", 296, 7, RfqStatus.Awarded, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC001"], 7, 0.96m), new Quote(vd["NCC002"], 5, 0.98m),
                new Quote(vd["NCC010"], 10, 0.92m, false, Note: "Máy in không hỗ trợ in 2 mặt tự động theo yêu cầu"),
                new Quote(vd["NCC003"], null, null)
            });
            var poPrn = PoFromRfq(rPrn, 285, 10);
            Receive(poPrn, 277);

            // Camera văn phòng: chấm điểm có trọng số → NCC giá không thấp nhất thắng
            var prCam = Pr(HC, "Camera an ninh khu văn phòng", 215, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation,
                new[] { (it["IT-CAM-IP"], 40m), (it["IT-NAS"], 1m) }, purpose: "Lắp camera hành lang, sảnh và bãi xe.");
            var rCam = NewRfq(prCam, "Camera an ninh khu văn phòng", 211, 10, RfqStatus.Awarded, EvaluationMethod.WeightedScore, new[]
            {
                new Quote(vd["NCC003"], 7, 0.99m, true, 90), new Quote(vd["NCC001"], 20, 0.95m, true, 72), new Quote(vd["NCC009"], 12, 0.97m, true, 80),
                new Quote(vd["NCC010"], null, null)
            });
            var poCam = PoFromRfq(rCam, 195, 14);
            Receive(poCam, 176);

            // Bàn ghế phòng họp: chỉ 2 báo giá hợp lệ → Giám đốc duyệt kết quả; giao trễ, có hàng lỗi
            var prMeet = Pr(KD, "Bàn ghế phòng họp & tủ hồ sơ", 160, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation,
                new[] { (it["NT-DESK"], 2m), (it["NT-CHAIR"], 24m), (it["NT-CAB"], 6m) });
            var rMeet = NewRfq(prMeet, "Bàn ghế phòng họp & tủ hồ sơ", 156, 7, RfqStatus.Awarded, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC011"], 15, 0.95m), new Quote(vd["NCC005"], 12, 0.92m, false, Note: "Ghế không đạt tải trọng 120kg theo yêu cầu"),
                new Quote(vd["NCC017"], 20, 1.01m)
            }, proposeReason: "Chỉ có 2 báo giá hợp lệ; Minh Khang giá thấp nhất trong số đạt kỹ thuật, đề nghị chọn để kịp khai trương phòng họp.");
            var poMeet = PoFromRfq(rMeet, 140, 15);
            var chair = poMeet.Lines.First(l => l.ItemId == it["NT-CHAIR"].Id);
            Receive(poMeet, 118, l => l == chair ? l.Quantity - 2 : l.Quantity, l => l == chair ? 2 : 0, "2 ghế bị rách lưới tựa lưng – trả lại NCC");
            Receive(poMeet, 112, note: "NCC giao bù 2 ghế");

            // Bảo trì điều hòa: chỉ 1 báo giá → hủy RFQ, chuyển chỉ định thầu (xem DirectAwards)
            prAcMaint = Pr(HC, "Bảo trì hệ thống điều hòa 6 tháng", 130, PrStatus.Processing, ProcurementMethod.DirectAward,
                new[] { (it["DV-MAINT"], 6m) }, methodReason: "Đã chào hàng cạnh tranh nhưng chỉ 1 NCC nộp báo giá; giá trị dưới hạn mức chỉ định thầu.");
            NewRfq(prAcMaint, "Bảo trì hệ thống điều hòa 6 tháng", 128, 7, RfqStatus.Cancelled, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC015"], 5, 1.02m), new Quote(vd["NCC006"], null, null), new Quote(vd["NCC003"], null, null)
            }, cancelReason: "Chỉ 1 NCC nộp báo giá, không đủ 3 báo giá hợp lệ – chuyển sang chỉ định thầu (dưới hạn mức).");

            // UPS phòng máy: đề xuất NCC hạng 2 → chờ Giám đốc duyệt kết quả
            var prUps = Pr(CNTT, "UPS cho phòng máy chủ mới", 45, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation, new[] { (it["IT-UPS-3K"], 4m) });
            NewRfq(prUps, "UPS cho phòng máy chủ mới", 42, 7, RfqStatus.PendingApproval, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC010"], 45, 0.93m), new Quote(vd["NCC001"], 7, 0.97m), new Quote(vd["NCC003"], 10, 1.00m)
            }, pick: vd["NCC001"], proposeReason: "An Phát giao hàng sau 45 ngày, không kịp tiến độ chuyển phòng máy chủ; Sao Mai giao 7 ngày, chênh lệch giá nhỏ.");

            // Máy tính Phòng Kế toán: đã mở báo giá, đang đánh giá kỹ thuật
            var prAcc = Pr(KT, "Máy tính cho Phòng Kế toán", 22, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation,
                new[] { (it["IT-LAP-01"], 6m), (it["IT-MON-24"], 6m) }, purpose: "Thay máy tính đã sử dụng trên 6 năm.");
            NewRfq(prAcc, "Máy tính cho Phòng Kế toán", 19, 7, RfqStatus.Opened, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC001"], 5, 0.96m, true), new Quote(vd["NCC002"], 7, 0.97m, null), new Quote(vd["NCC009"], 10, 0.95m, null),
                new Quote(vd["NCC010"], null, null)
            });

            // Microsoft 365: đang nhận báo giá (niêm phong)
            var prM365 = Pr(CNTT, "Bản quyền Microsoft 365 năm 2027", 9, PrStatus.Processing, ProcurementMethod.CompetitiveQuotation, new[] { (it["SW-OFF365"], 60m) });
            NewRfq(prM365, "Bản quyền Microsoft 365 năm 2027", 6, 10, RfqStatus.Open, EvaluationMethod.LowestPrice, new[]
            {
                new Quote(vd["NCC013"], 2, 0.95m), new Quote(vd["NCC001"], null, null), new Quote(vd["NCC002"], null, null)
            });
        }

        private PurchaseRequest prAcMaint = null!;

        // ------------------------------------------------------------------ 2. Chỉ định thầu
        private void DirectAwards()
        {
            var prEmg = Pr(KTH, "Khắc phục sự cố điều hòa phòng máy chủ", 205, PrStatus.Processing, ProcurementMethod.DirectAward,
                new[] { (it["DV-MAINT"], 3m) }, PrPriority.Critical, "Điều hòa phòng máy chủ hỏng, nhiệt độ vượt 35°C.",
                "Sự cố khẩn cấp ảnh hưởng an toàn hệ thống máy chủ, không thể chờ chào hàng.");
            var da1 = Da(prEmg, vd["NCC015"], 205, DirectAwardReason.Emergency, "Điều hòa chính và dự phòng phòng máy chủ cùng hỏng; cần khắc phục trong 24 giờ để tránh hư hỏng thiết bị.",
                DirectAwardStatus.Approved, 1.08m, 1.00m, 2, ("bien-ban-su-co-dieu-hoa.txt", "BIÊN BẢN SỰ CỐ\nĐiều hòa phòng máy chủ ngừng hoạt động lúc 02:15, nhiệt độ phòng 36°C.\nĐề nghị khắc phục khẩn cấp."));
            Receive(PoFromDa(da1, 203), 201);

            var prErp = Pr(KT, "Gia hạn bảo trì phần mềm ERP", 112, PrStatus.Processing, ProcurementMethod.DirectAward,
                new[] { (it["SW-ERP-MT"], 1m) }, methodReason: "Chỉ đơn vị phát triển phần mềm có mã nguồn và quyền bảo trì.");
            var da2 = Da(prErp, vd["NCC013"], 110, DirectAwardReason.SoleSource, "Phần mềm ERP do Bách Khoa phát triển, giữ bản quyền mã nguồn; không đơn vị nào khác được phép bảo trì.",
                DirectAwardStatus.Approved, 1.00m, 0.95m, 1, ("xac-nhan-ban-quyen-ERP.txt", "Công ty TNHH Phần mềm Bách Khoa xác nhận là đơn vị duy nhất sở hữu mã nguồn và được quyền bảo trì phần mềm ERP."));
            Receive(PoFromDa(da2, 100), 99, note: "Nghiệm thu gói bảo trì năm");

            var da3 = Da(prAcMaint, vd["NCC015"], 122, DirectAwardReason.UnderThreshold, "Giá trị dưới hạn mức chỉ định thầu; chào hàng cạnh tranh trước đó chỉ có 1 báo giá.",
                DirectAwardStatus.Approved, 1.02m, 0.97m, 120);
            var poAc = PoFromDa(da3, 116);
            foreach (var day in new[] { 90, 60, 30 }) Receive(poAc, day, _ => 1, note: "Nghiệm thu bảo trì định kỳ");

            var prWifi = Pr(CNTT, "Mở rộng WiFi đồng bộ hệ thống hiện có", 55, PrStatus.Processing, ProcurementMethod.DirectAward,
                new[] { (it["IT-NET-AP"], 10m), (it["IT-NET-SW"], 1m) }, methodReason: "Thiết bị phải đồng bộ với hệ thống quản lý WiFi tập trung đang dùng.");
            var da4 = Da(prWifi, vd["NCC003"], 52, DirectAwardReason.Compatibility, "Hệ thống WiFi hiện hữu dùng bộ điều khiển tập trung do Hoàng Long triển khai; thiết bị mới phải cùng hãng để quản lý chung.",
                DirectAwardStatus.Approved, 1.03m, 0.98m, 10, ("danh-muc-thiet-bi-hien-huu.txt", "Danh mục thiết bị WiFi hiện hữu: 24 AP + 1 controller, triển khai bởi Hoàng Long năm 2024."));
            PoFromDa(da4, 48); // quá hạn giao, chưa nhận

            var prBh1 = Pr(KTH, "Đồ bảo hộ đội kỹ thuật (đợt 1)", 40, PrStatus.Processing, ProcurementMethod.DirectAward,
                new[] { (it["BH-HELM"], 40m), (it["BH-SHOE"], 40m), (it["BH-GLOVE"], 100m) }, methodReason: "Giá trị nhỏ, dưới hạn mức chỉ định thầu.");
            var da5 = Da(prBh1, vd["NCC016"], 38, DirectAwardReason.UnderThreshold, "Giá trị nhỏ, cần trang bị ngay cho đội kỹ thuật mới tuyển.",
                DirectAwardStatus.Approved, 1.00m, 0.96m, 5);
            Receive(PoFromDa(da5, 33), 27);

            var prBh2 = Pr(KTH, "Đồ bảo hộ đội kỹ thuật (đợt 2)", 12, PrStatus.Processing, ProcurementMethod.DirectAward,
                new[] { (it["BH-SHOE"], 100m), (it["BH-GLOVE"], 300m), (it["BH-HELM"], 80m) }, methodReason: "Giá trị dưới hạn mức chỉ định thầu.");
            var est2 = prBh2.Lines.Sum(l => R(l.EstimatedPrice * 0.97m) * l.Quantity);
            var est1 = prBh1.Lines.Sum(l => R(l.EstimatedPrice * 0.96m) * l.Quantity);
            Da(prBh2, vd["NCC016"], 10, DirectAwardReason.UnderThreshold, "Bổ sung đồ bảo hộ cho công trình mới.", DirectAwardStatus.PendingApproval, 1.00m, 0.97m, 5,
                splitWarning: $"1 tờ trình cùng NCC / nhóm hàng trong 90 ngày; cộng dồn {(est1 + est2).ToMoney()} đ vượt hạn mức chỉ định {100_000_000m.ToMoney()} đ – nghi chia nhỏ gói.");

            var gen = new Item { Name = "Đại tu máy phát điện dự phòng 250kVA", Unit = "Gói", ReferencePrice = 78_000_000 };
            var prGen = Pr(KTH, "Sửa chữa máy phát điện dự phòng", 4, PrStatus.Processing, ProcurementMethod.DirectAward, new[] { (gen, 1m) },
                PrPriority.Urgent, methodReason: "Chỉ đơn vị bảo hành của hãng có phụ tùng chính hãng.");
            Da(prGen, vd["NCC015"], 2, DirectAwardReason.SoleSource, "Đơn vị ủy quyền bảo hành duy nhất của hãng máy phát tại khu vực.", DirectAwardStatus.Draft, 1.05m, 1.05m, 15);
        }

        // ------------------------------------------------------------------ 3. Đấu thầu rộng rãi + hợp đồng
        private Tender NewTender(PurchaseRequest pr, string title, int createdAgo, int publishedAgo, int biddingDays, TenderStatus st, EnvelopeMethod env,
            TenderEvaluationMethod em, bool framework, int durationDays, AppUser[] committee)
        {
            var c = Day(createdAgo);
            var pub = Day(publishedAgo).AddHours(9);
            var t = new Tender
            {
                Number = No("TD", c), Title = title, PurchaseRequest = pr, BuyerId = buyer.Id, Status = st, PackageBudget = pr.EstimatedTotal, EnvelopeMethod = env,
                ContractType = framework ? "Theo đơn giá cố định" : "Trọn gói", ContractDurationDays = durationDays, IsFramework = framework,
                TechnicalRequirements = "Hàng mới 100%, đúng chủng loại và thông số tại phụ lục; có CO/CQ; bảo hành tối thiểu 24 tháng tại nơi sử dụng; hỗ trợ kỹ thuật trong 4 giờ.",
                QualificationCriteria = "Tối thiểu 02 hợp đồng tương tự trong 03 năm gần nhất; doanh thu bình quân 03 năm ≥ 1,5 lần giá gói thầu; không nợ thuế; có ủy quyền của hãng.",
                ContractTerms = "Tạm ứng 30% sau khi ký hợp đồng và nộp bảo lãnh; thanh toán 70% sau nghiệm thu; bảo đảm thực hiện 5% giá trị hợp đồng.",
                EvaluationMethod = em, MinTechnicalScore = 70, TechnicalWeight = 30, PriceWeight = 70,
                BidSecurityAmount = Math.Round(pr.EstimatedTotal * 0.01m / 1_000_000m) * 1_000_000m, BidValidityDays = 90, PerformanceSecurityPercent = 5,
                PublishedAt = st >= TenderStatus.Published && st != TenderStatus.Cancelled ? pub : null,
                ClosingAt = pub.Date.AddDays(biddingDays).AddHours(9), CreatedAt = c.AddHours(9)
            };
            var s = 1;
            foreach (var l in pr.Lines)
                t.Lines.Add(new TenderLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice, SortOrder = s++ });
            db.Tenders.Add(t);

            Func<int> id = () => t.Id;
            Log(DocType.PR, () => pr.Id, buyer, $"Mua hàng lập gói thầu {t.Number}", c.AddHours(9));
            for (var i = 0; i < committee.Length; i++)
            {
                t.Committee.Add(new TenderCommitteeMember { UserId = committee[i].Id, User = committee[i], IsLeader = i == 0 });
                Log(DocType.Tender, id, buyer, "Bổ sung tổ chuyên gia", c.AddHours(10 + i), committee[i].FullName + (i == 0 ? " (tổ trưởng)" : ""));
            }
            if (st == TenderStatus.Draft) return t;
            Log(DocType.Tender, id, buyer, "Trình duyệt kế hoạch & HSMT", c.AddDays(2).AddHours(9), $"Đóng thầu {t.ClosingAt.ToDateTime()}, tổ chuyên gia {committee.Length} người");
            if (st == TenderStatus.PendingPlanApproval) return t;
            Log(DocType.Tender, id, director, "Giám đốc duyệt KH & HSMT – phát hành, đăng tải thông báo mời thầu", pub);
            return t;
        }

        private TenderBidder Bid(Tender t, Vendor v, int regDays, decimal? factor, decimal discount = 0, int lead = 45, bool? valid = null, bool? qualified = null,
            string? validNote = null, string? qualNote = null, int[]? scores = null, bool shortSecurity = false)
        {
            var b = new TenderBidder { VendorId = v.Id, Vendor = v, RegisteredAt = t.PublishedAt!.Value.AddDays(regDays).AddHours(Hours(1, 6)) };
            t.Bidders.Add(b);
            Log(DocType.Tender, () => t.Id, buyer, "Nhà thầu nhận HSMT", b.RegisteredAt, v.Name);
            if (factor is not decimal f) return b;
            b.Submitted = true;
            b.SubmittedAt = t.ClosingAt.AddHours(-Hours(2, 60));
            foreach (var l in t.Lines) b.Lines.Add(new TenderBidLine { TenderLine = l, UnitPrice = R(l.EstimatedPrice * f * Jitter(0.015)) });
            b.BidPrice = t.Lines.Zip(b.Lines).Sum(x => x.First.Quantity * x.Second.UnitPrice);
            b.DiscountPercent = discount; b.LeadTimeDays = lead; b.ValidityDays = t.BidValidityDays;
            b.BidSecurityAmount = shortSecurity ? t.BidSecurityAmount / 2 : t.BidSecurityAmount;
            b.BidSecurityRef = $"BL{rnd.Next(1000, 9999)}/{t.ClosingAt:yy}/NH"; b.BidSecurityExpiry = t.ClosingAt.Date.AddDays(t.BidValidityDays + 35);
            b.Valid = valid; b.ValidNote = validNote; b.Qualified = qualified; b.QualifiedNote = qualNote;
            if (scores != null)
                foreach (var (m, sc) in t.Committee.Zip(scores))
                    b.Scores.Add(new TenderScore { MemberUserId = m.UserId, MemberUser = m.User, TechnicalScore = sc, ScoredAt = t.ClosingAt.AddDays(3 + rnd.Next(0, 4)),
                        Comment = sc >= 85 ? "Đáp ứng tốt, có giải pháp vượt yêu cầu." : sc >= 70 ? "Đáp ứng yêu cầu." : "Một số thông số chưa đáp ứng." });
            return b;
        }

        /// <summary>Mở thầu, đánh giá, phê duyệt kết quả theo đúng trình tự (dừng ở trạng thái của gói thầu)</summary>
        private BidResult? Evaluate(Tender t, int awardAgo)
        {
            Func<int> id = () => t.Id;
            var open = t.ClosingAt.AddHours(1);
            t.OpenedAt = open;
            foreach (var m in t.Committee) m.ConflictDeclaredAt = open.AddMinutes(-30);
            foreach (var m in t.Committee) Log(DocType.Tender, id, m.User!, "Cam kết không xung đột lợi ích", open.AddMinutes(-30), m.User!.FullName);
            Log(DocType.Tender, id, buyer, $"Mở thầu – {t.Bidders.Count(b => b.Submitted)} HSDT", open,
                t.Bidders.Count(b => !b.Submitted) is var n and > 0 ? $"{n} nhà thầu nhận HSMT nhưng không nộp" : null);
            var scored = t.Committee.Where(m => t.Bidders.Where(b => b.Valid == true && b.Qualified == true).All(b => b.Scores.Any(s => s.MemberUserId == m.UserId))).ToList();
            foreach (var m in scored) m.ScoringCompletedAt = open.AddDays(7);
            if (t.Status == TenderStatus.Opened) return null;

            if (t.EnvelopeMethod == EnvelopeMethod.TwoEnvelope)
            {
                t.FinancialOpenedAt = open.AddDays(8);
                Log(DocType.Tender, id, buyer, $"Mở hồ sơ tài chính – {TenderEvaluation.Evaluate(t).Count(r => r.TechnicalPass)} HSDT đạt kỹ thuật", t.FinancialOpenedAt.Value);
            }
            var win = TenderEvaluation.Evaluate(t).First(r => r.Rank == 1);
            t.AwardedVendor = win.Bid.Vendor; t.AwardedVendorId = win.Bid.VendorId; t.AwardPrice = win.Bid.NetPrice;
            t.AwardReason = t.EvaluationMethod == TenderEvaluationMethod.Combined
                ? $"Tổng điểm kỹ thuật – giá cao nhất ({win.Total:0.##} điểm)." : "Giá đánh giá thấp nhất trong các HSDT đạt kỹ thuật.";
            Log(DocType.Tender, id, buyer, "Trình duyệt kết quả lựa chọn nhà thầu", Day(awardAgo + 2).AddHours(10), $"{win.Bid.Vendor!.Name} – {win.Bid.NetPrice.ToMoney()} đ (trước VAT)");
            Log(DocType.Tender, id, director, "Giám đốc phê duyệt kết quả lựa chọn nhà thầu – công khai kết quả", Day(awardAgo).AddHours(15));
            t.ComplaintDeadline = Day(awardAgo).AddDays(5).AddHours(17);
            return win;
        }

        private Contract NewContract(Tender t, BidResult win, int signedAgo, DateTime end, string title)
        {
            var d = Day(signedAgo);
            var b = win.Bid;
            var c = new Contract
            {
                Number = No("HD", d), Title = title, Tender = t, VendorId = b.VendorId, Vendor = b.Vendor, IsFramework = t.IsFramework, VatPercent = 10,
                SignedDate = d, StartDate = d, EndDate = end, PaymentTerms = "Tạm ứng 30%, thanh toán 70% trong 30 ngày sau nghiệm thu",
                PerformanceSecurityRef = $"BLTH{rnd.Next(1000, 9999)}/{d:yy}", PerformanceSecurityExpiry = end.AddDays(30), Terms = t.ContractTerms,
                CreatedById = buyer.Id, CreatedAt = d.AddHours(10)
            };
            var s = 1;
            foreach (var (tl, bl) in t.Lines.Zip(b.Lines))
                c.Lines.Add(new ContractLine { ItemId = tl.ItemId, Description = tl.Description, Unit = tl.Unit, Quantity = tl.Quantity,
                    UnitPrice = Math.Round(bl.UnitPrice * (1 - b.DiscountPercent / 100m), 0), SortOrder = s++ });
            c.Value = c.Lines.Sum(l => l.Quantity * l.UnitPrice);
            c.PerformanceSecurityAmount = R(c.Value * t.PerformanceSecurityPercent / 100m);
            db.Contracts.Add(c);
            t.Status = TenderStatus.Contracted;
            Log(DocType.Contract, () => c.Id, buyer, "Ký hợp đồng", d.AddHours(10), $"Từ gói thầu {t.Number} – {c.Value.ToMoney()} đ (trước VAT)");
            return c;
        }

        private decimal contractUsed;
        private PurchaseOrder CallOff(Contract c, PurchaseRequest? pr, int daysAgo, int lead, params (string code, decimal q)[] lines)
        {
            var items = lines.Select(x => (it[x.code], x.q, c.Lines.First(l => l.ItemId == it[x.code].Id).UnitPrice)).ToList();
            var sub = items.Sum(x => x.q * x.UnitPrice);
            contractUsed += sub;
            var po = Po(c.Vendor!, daysAgo, ProcurementMethod.OpenTender, pr, lead, items,
                c.IsFramework ? $"Đặt hàng theo hợp đồng khung {c.Number}" : $"Tạo theo hợp đồng {c.Number}",
                approval: $"Duyệt theo hợp đồng {c.Number} (đã thực hiện {contractUsed.ToMoney()}/{c.Value.ToMoney()} đ)",
                notes: $"Theo hợp đồng {c.Number} ký ngày {c.SignedDate.ToDate()}");
            po.Contract = c;
            Log(DocType.Contract, () => c.Id, buyer, $"Đặt hàng đợt {po.Number}", Day(daysAgo).AddHours(11), $"{po.SubTotal.ToMoney()} đ");
            return po;
        }

        private void Tenders()
        {
            var (_, apCntt) = depts[CNTT]; var (_, apHc) = depts[HC]; var (_, apKt) = depts[KTH]; var (_, apKd) = depts[KD]; var (_, apNs) = depts[NS];

            // A. Hạ tầng máy chủ: hợp đồng trọn gói, đã hoàn thành
            var prA = Pr(CNTT, "Nâng cấp hạ tầng máy chủ và lưu trữ", 300, PrStatus.Processing, ProcurementMethod.OpenTender,
                new[] { (it["IT-SRV-01"], 24m), (it["IT-NAS"], 10m), (it["IT-UPS-3K"], 20m), (it["IT-NET-SW"], 20m) },
                purpose: "Thay thế hệ thống máy chủ hết khấu hao, chuẩn bị triển khai ERP mới.");
            var tA = NewTender(prA, "Nâng cấp hạ tầng máy chủ và lưu trữ", 292, 285, 20, TenderStatus.Awarded, EnvelopeMethod.OneEnvelope,
                TenderEvaluationMethod.LowestEvaluatedPrice, false, 90, new[] { apCntt, apKt, buyer2 });
            Bid(tA, vd["NCC009"], 1, 0.93m, 1, 45, true, true, scores: new[] { 85, 82, 88 });
            Bid(tA, vd["NCC001"], 2, 0.95m, 0, 30, true, true, scores: new[] { 80, 78, 84 });
            Bid(tA, vd["NCC002"], 3, 0.97m, 0, 40, true, true, scores: new[] { 74, 70, 72 });
            Bid(tA, vd["NCC010"], 4, 0.90m, 0, 60, false, null, "Bảo lãnh dự thầu không đủ giá trị yêu cầu", shortSecurity: true);
            Bid(tA, vd["NCC003"], 5, null);
            var winA = Evaluate(tA, 255)!;
            tA.Complaints.Add(new TenderComplaint
            {
                VendorId = vd["NCC002"].Id, Content = "Đề nghị xem xét lại điểm kỹ thuật, giải pháp lưu trữ của chúng tôi vượt yêu cầu.",
                ReceivedAt = Day(253).AddHours(10), Response = "Tổ chuyên gia đã rà soát; điểm kỹ thuật chấm đúng tiêu chí HSMT, giữ nguyên kết quả.",
                Upheld = false, ResolvedAt = Day(250).AddHours(16), ResolvedById = director.Id
            });
            Log(DocType.Tender, () => tA.Id, buyer, "Tiếp nhận kiến nghị của nhà thầu", Day(253).AddHours(10), "Công ty CP Công nghệ Phong Vũ Số: đề nghị xem xét lại điểm kỹ thuật");
            var cA = NewContract(tA, winA, 245, Day(155), "Hợp đồng cung cấp, lắp đặt hạ tầng máy chủ và lưu trữ");
            contractA = cA;
            contractUsed = 0;
            var poA = CallOff(cA, prA, 244, 60, ("IT-SRV-01", 24), ("IT-NAS", 10), ("IT-UPS-3K", 20), ("IT-NET-SW", 20));
            var srv = poA.Lines.First(l => l.ItemId == it["IT-SRV-01"].Id);
            Receive(poA, 190, l => l == srv ? 14 : l.Quantity, note: "Đợt 1: 14 máy chủ + toàn bộ lưu trữ, UPS, switch");
            Receive(poA, 178, note: "Đợt 2: 10 máy chủ còn lại");
            cA.Status = ContractStatus.Completed;
            Log(DocType.Contract, () => cA.Id, buyer, "Thanh lý – hoàn thành hợp đồng", Day(150).AddHours(15), "Đã nghiệm thu toàn bộ, giao trễ 6 ngày đợt 2 (đã phạt theo hợp đồng).");

            // B. Laptop & màn hình: hợp đồng khung, các phòng ban đặt hàng nhiều đợt, sắp hết hạn
            var prB = Pr(CNTT, "Cung cấp laptop và màn hình năm 2026 (hợp đồng khung)", 345, PrStatus.Processing, ProcurementMethod.OpenTender,
                new[] { (it["IT-LAP-01"], 220m), (it["IT-MON-24"], 100m) }, purpose: "Tổng hợp nhu cầu cả năm của các phòng ban để đấu thầu một lần.");
            var tB = NewTender(prB, "Cung cấp laptop và màn hình năm 2026 (hợp đồng khung)", 338, 332, 20, TenderStatus.Awarded, EnvelopeMethod.OneEnvelope,
                TenderEvaluationMethod.Combined, true, 365, new[] { apCntt, apHc, buyer2 });
            Bid(tB, vd["NCC001"], 1, 0.92m, 0, 7, true, true, scores: new[] { 88, 85, 90 });
            Bid(tB, vd["NCC002"], 2, 0.90m, 0, 10, true, true, scores: new[] { 72, 70, 69 });
            Bid(tB, vd["NCC009"], 2, 0.94m, 1, 10, true, true, scores: new[] { 80, 83, 79 });
            tB.Clarifications.Add(new TenderClarification
            {
                Question = "Có chấp nhận laptop Core i5 thế hệ 12 không?", Answer = "Chấp nhận từ thế hệ 12 trở lên, RAM tối thiểu 16GB.",
                CreatedById = buyer.Id, CreatedAt = Day(325).AddHours(10)
            });
            var winB = Evaluate(tB, 300)!;
            var cB = NewContract(tB, winB, 292, today.AddDays(25), "Hợp đồng khung cung cấp laptop và màn hình năm 2026");
            contractB = cB;
            contractUsed = 0;
            var callOffs = new (string dept, int day, int lead, int lap, int mon, int recv)[]
            {
                (CNTT, 285, 7, 40, 20, 278), (KT, 240, 7, 15, 15, 233), (KD, 200, 7, 30, 10, 192), (NS, 150, 5, 20, 0, 145),
                (KTH, 95, 7, 25, 20, 88), (HC, 50, 5, 20, 10, 44), (KD, 14, 7, 30, 10, -1)
            };
            foreach (var (dept, day, lead, lap, mon, recv) in callOffs)
            {
                var lines = new List<(Item, decimal)> { (it["IT-LAP-01"], lap) };
                if (mon > 0) lines.Add((it["IT-MON-24"], mon));
                var pr = Pr(dept, $"Laptop{(mon > 0 ? " và màn hình" : "")} cho {dept.Replace("Phòng ", "phòng ")}", day + 4, PrStatus.Processing, ProcurementMethod.OpenTender,
                    lines.ToArray(), methodReason: $"Đặt hàng theo hợp đồng khung {cB.Number}.");
                var po = CallOff(cB, pr, day, lead, lines.Select(l => (l.Item1.Code, l.Item2)).ToArray());
                if (recv > 0) Receive(po, recv);
                else Receive(po, 8, l => l.ItemId == it["IT-LAP-01"].Id ? 15 : 0, note: "Đợt 1: 15 laptop");
            }

            // C. Nội thất chi nhánh: đang mời thầu
            var prC = Pr(HC, "Trang bị nội thất văn phòng chi nhánh Đà Nẵng", 30, PrStatus.Processing, ProcurementMethod.OpenTender,
                new[] { (it["NT-WS"], 200m), (it["NT-CHAIR"], 900m), (it["NT-CAB"], 300m) }, purpose: "Chi nhánh Đà Nẵng khai trương quý I năm sau.");
            var tC = NewTender(prC, "Trang bị nội thất văn phòng chi nhánh Đà Nẵng", 25, 10, 16, TenderStatus.Published, EnvelopeMethod.OneEnvelope,
                TenderEvaluationMethod.LowestEvaluatedPrice, false, 60, new[] { apHc, apKd, buyer });
            Bid(tC, vd["NCC011"], 1, 0.95m, 0, 45).SubmittedAt = Day(1).AddHours(15); // đã nộp, giá niêm phong đến khi mở thầu
            Bid(tC, vd["NCC005"], 2, null); Bid(tC, vd["NCC017"], 6, null);
            tC.Clarifications.Add(new TenderClarification { Question = "Bàn làm việc cụm có yêu cầu vách ngăn nỉ không?", Answer = "Có, vách ngăn nỉ cao 1.100mm, màu ghi.", CreatedById = buyer.Id, CreatedAt = Day(6).AddHours(10) });
            tC.Clarifications.Add(new TenderClarification { IsAmendment = true, Answer = "Sửa đổi HSMT số 01: ghế làm việc yêu cầu có tựa đầu và tay ghế điều chỉnh 3D.", CreatedById = buyer.Id, CreatedAt = Day(5).AddHours(9) });

            // D. Camera nhà máy: hai túi hồ sơ, đã mở thầu, đang chấm kỹ thuật
            var prD = Pr(KTH, "Hệ thống camera giám sát nhà máy", 75, PrStatus.Processing, ProcurementMethod.OpenTender,
                new[] { (it["IT-CAM-IP"], 1200m), (it["IT-NAS"], 30m), (it["IT-NET-SW"], 120m) }, purpose: "Giám sát an ninh toàn bộ khu nhà xưởng và kho bãi.");
            var tD = NewTender(prD, "Hệ thống camera giám sát nhà máy", 70, 55, 25, TenderStatus.Opened, EnvelopeMethod.TwoEnvelope,
                TenderEvaluationMethod.Combined, false, 120, new[] { apKt, apCntt, buyer2 });
            Bid(tD, vd["NCC003"], 1, 0.94m, 0, 60, true, true, scores: new[] { 86, 84 });
            Bid(tD, vd["NCC009"], 2, 0.91m, 0, 75, true, true, scores: new[] { 78, 75 });
            Bid(tD, vd["NCC001"], 3, 0.96m, 0, 50, true, false, qualNote: "Chưa đủ 02 hợp đồng tương tự quy mô trên 1.000 camera");
            Bid(tD, vd["NCC010"], 4, null);
            Evaluate(tD, 0);

            // E. Dịch vụ bảo vệ & vệ sinh 2027–2028: chờ duyệt kế hoạch (chọn đấu thầu dù dưới hạn mức → cạnh tranh hơn)
            var prE = Pr(HC, "Dịch vụ bảo vệ và vệ sinh 2027–2028", 15, PrStatus.Processing, ProcurementMethod.OpenTender,
                new[] { (it["DV-SEC"], 24m), (it["DV-CLEAN"], 24m) }, purpose: "Hợp đồng dịch vụ hiện tại hết hạn cuối năm.");
            NewTender(prE, "Dịch vụ bảo vệ và vệ sinh 2027–2028", 8, 0, 20, TenderStatus.PendingPlanApproval, EnvelopeMethod.OneEnvelope,
                TenderEvaluationMethod.LowestEvaluatedPrice, false, 730, new[] { apHc, apNs, buyer });
        }

        // ------------------------------------------------------------------ Ngân sách mẫu (năm nay + năm trước)
        public const string BudgetNote = "Dữ liệu mẫu";
        private async Task BudgetsAsync()
        {
            var plan = new (string dept, string? cat, decimal amount)[]
            {
                (CNTT, null, 7_000_000_000), (CNTT, "SW", 400_000_000), (HC, null, 8_500_000_000), (HC, "VPP", 150_000_000), (KT, null, 750_000_000),
                (KD, null, 1_600_000_000), (KTH, null, 7_000_000_000), (KTH, "BHLD", 80_000_000), (NS, null, 600_000_000)
            };
            foreach (var year in new[] { today.Year - 1, today.Year })
                foreach (var (dept, code, amount) in plan)
                {
                    int? catId = code == null ? null : cat[code].Id;
                    if (await db.Budgets.AnyAsync(b => b.Year == year && b.Department == dept && b.CategoryId == catId)) continue;
                    db.Budgets.Add(new Budget { Year = year, Department = dept, CategoryId = catId, Amount = year == today.Year ? amount : Math.Round(amount * 0.9m / 1_000_000m) * 1_000_000m, Note = BudgetNote });
                }
        }

        // ------------------------------------------------------------------ 4. Đề nghị ở các bước duyệt
        private void PipelineRequests()
        {
            Pr(CNTT, "Màn hình thay thế cho máy hỏng", 2, PrStatus.Submitted, null, new[] { (it["IT-MON-24"], 2m) });
            Pr(KD, "Sổ tay quà tặng khách hàng cuối năm", 1, PrStatus.Submitted, null, new[] { (it["VPP-NOTE"], 300m) }, PrPriority.Urgent);
            Pr(NS, "Trang thiết bị phòng đào tạo", 4, PrStatus.PendingDirector, null, new[] { (it["IT-LAP-01"], 2m), (it["IT-MON-24"], 2m), (it["NT-DESK"], 2m) });
            Pr(KT, "Tủ hồ sơ lưu trữ chứng từ", 6, PrStatus.Approved, null, new[] { (it["NT-CAB"], 10m) });
            Pr(KD, "Laptop cho cộng tác viên", 60, PrStatus.Rejected, null, new[] { (it["IT-LAP-01"], 5m) },
                rejectComment: "Cộng tác viên sử dụng máy cá nhân theo quy định hiện hành.");
            Pr(KTH, "Máy chủ dự phòng cho xưởng", 100, PrStatus.Rejected, null, new[] { (it["IT-SRV-01"], 2m) },
                rejectComment: "Đã nằm trong gói nâng cấp hạ tầng máy chủ của Phòng CNTT.", rejectByDirector: true);
            Pr(HC, "In ấn tài liệu hội nghị khách hàng", 35, PrStatus.Cancelled, null, new[] { (it["VPP-A4"], 100m) },
                rejectComment: "Hội nghị chuyển sang hình thức trực tuyến.");
            Pr(NS, "Văn phòng phẩm cho nhân viên mới", 0, PrStatus.Draft, null, new[] { (it["VPP-PEN"], 5m), (it["VPP-NOTE"], 20m) });
        }

        /// <summary>PR đã nhận đủ hàng ở mọi PO → hoàn tất (giống khi nhận hàng trên hệ thống)</summary>
        private void FinishRequests()
        {
            foreach (var pr in prs.Where(p => p.Status == PrStatus.Processing))
            {
                var list = pos.Where(p => p.PurchaseRequest == pr && p.Status != PoStatus.Cancelled).ToList();
                if (list.Count == 0 || list.Any(p => p.Status != PoStatus.Received)) continue;
                pr.Status = PrStatus.Completed;
                Log(DocType.PR, () => pr.Id, buyer, "Hoàn tất – đã nhận đủ hàng", list.Max(p => lastReceipt[p]).AddHours(15));
            }
        }

        // ------------------------------------------------------------------ Hóa đơn, thanh toán, hợp đồng nâng cao
        private Payment Pay(PaymentType type, Vendor v, DateTime at, decimal amount, decimal penalty, decimal advance, string note, PaymentStatus st,
            Invoice? inv = null, Contract? c = null, ContractMilestone? m = null)
        {
            var p = new Payment
            {
                Number = No("TT", at), Type = type, VendorId = v.Id, Invoice = inv, Contract = c, Milestone = m, Amount = amount, PenaltyAmount = penalty,
                AdvanceDeduction = advance, NetAmount = amount - penalty - advance, Note = note, Status = st, RequestedById = accountant.Id, RequestedAt = at.AddHours(10)
            };
            Func<int> id = () => p.Id;
            Log(DocType.Payment, id, accountant, $"Đề nghị thanh toán {p.NetAmount.ToMoney()} đ", note, p.RequestedAt);
            if (st is PaymentStatus.Approved or PaymentStatus.Paid)
            {
                p.ApprovedById = director.Id; p.ApprovedAt = at.AddDays(1).AddHours(9);
                Log(DocType.Payment, id, director, "Giám đốc duyệt thanh toán", null, p.ApprovedAt.Value);
            }
            if (st == PaymentStatus.Paid)
            {
                p.PaidById = accountant.Id; p.PaidAt = at.AddDays(rnd.Next(2, 5)).Date; p.PaymentRef = "UNC" + rnd.Next(10000, 99999);
                Log(DocType.Payment, id, accountant, $"Đã chi {p.NetAmount.ToMoney()} đ", $"UNC {p.PaymentRef} ngày {p.PaidAt:dd/MM/yyyy}", p.PaidAt.Value.AddHours(15));
            }
            db.Payments.Add(p);
            return p;
        }

        private void Log(DocType t, Func<int> id, AppUser u, string action, string? comment, DateTime at) => Log(t, id, u, action, at, comment);

        private void Finance()
        {
            // Hợp đồng A: tạm ứng 30% có bảo lãnh tạm ứng, thanh toán theo hóa đơn có phạt chậm giao và khấu trừ tạm ứng
            var cA = contractA; var gA = Math.Round(cA.Value * 1.1m, 0);
            var adv = new ContractMilestone { Name = "Tạm ứng 30%", Type = PaymentType.Advance, Percent = 30, Amount = Math.Round(gA * 0.3m, 0), DueDate = cA.SignedDate.AddDays(7), Condition = "Sau khi nộp bảo lãnh tạm ứng", SortOrder = 1 };
            cA.Milestones.Add(adv);
            cA.Milestones.Add(new ContractMilestone { Name = "Thanh toán 70% theo hóa đơn từng đợt giao", Type = PaymentType.Milestone, Percent = 70, Amount = gA - adv.Amount, Condition = "Nghiệm thu, hóa đơn hợp lệ", SortOrder = 2 });
            cA.Guarantees.Add(new ContractGuarantee { Type = GuaranteeType.Advance, Amount = adv.Amount, Reference = "BLTU-2026-118", Bank = "BIDV", ExpiryDate = cA.EndDate.AddDays(30), ReleasedAt = cA.EndDate.AddDays(5) });
            cA.Guarantees.Add(new ContractGuarantee { Type = GuaranteeType.Warranty, Amount = Math.Round(cA.Value * 0.05m, 0), Reference = "BLBH-2026-042", Bank = "BIDV", ExpiryDate = cA.EndDate.AddMonths(24) });
            var advPay = Pay(PaymentType.Advance, cA.Vendor!, cA.SignedDate.AddDays(3), adv.Amount, 0, 0, $"Tạm ứng 30% HĐ {cA.Number}", PaymentStatus.Paid, c: cA, m: adv);
            var advanceRate = adv.Amount / gA;
            var recovered = 0m;

            var invSeq = 0; var exceptionDone = false;
            foreach (var po in pos.Where(p => (p.Status == PoStatus.Received || (p.Status == PoStatus.PartiallyReceived && p.Lines.Any(l => l.ReceivedQuantity > 0)))
                                              && lastReceipt.ContainsKey(p) && (today - lastReceipt[p]).Days >= 4))
            {
                var recv = lastReceipt[po];
                var invDate = recv.AddDays(rnd.Next(1, 4));
                if (invDate > today) continue;
                invSeq++;
                var inv = new Invoice
                {
                    Number = No("HDV", invDate), VendorId = po.VendorId!.Value, PurchaseOrder = po, InvoiceNo = rnd.Next(1000, 99999).ToString("0000000"), Series = "1C" + invDate.ToString("yy") + "T" + (char)('A' + rnd.Next(0, 6)) + "A",
                    InvoiceDate = invDate.Date, DueDate = Infrastructure.FinanceService.DueDate(invDate, po.PaymentTerms), FromPortal = rnd.NextDouble() < 0.3, CreatedById = accountant.Id, CreatedAt = invDate.AddHours(11)
                };
                // Một hóa đơn văn phòng phẩm ghi đơn giá mực cao hơn PO → lệch, chờ Giám đốc xử lý
                var priceUp = !exceptionDone && po.Method == ProcurementMethod.DirectPurchase && (today - recv).Days < 40 && po.Lines.Any(l => l.ItemId == it["VPP-INK"].Id && l.ReceivedQuantity > 0);
                if (priceUp) exceptionDone = true;
                foreach (var l in po.Lines.Where(l => l.ReceivedQuantity > 0))
                {
                    var price = priceUp && l.ItemId == it["VPP-INK"].Id ? R(l.UnitPrice * 1.05m) : l.UnitPrice;
                    inv.Lines.Add(new InvoiceLine { PurchaseOrderLine = l, Quantity = l.ReceivedQuantity, UnitPrice = price, Amount = Math.Round(l.ReceivedQuantity * price, 0) });
                }
                inv.SubTotal = inv.Lines.Sum(l => l.Amount); inv.VatAmount = Math.Round(inv.SubTotal * po.VatPercent / 100m, 0); inv.Total = inv.SubTotal + inv.VatAmount;
                Func<int> iid = () => inv.Id;
                Log(DocType.Invoice, iid, accountant, $"Nhận hóa đơn {inv.Series} {inv.InvoiceNo} – {inv.Total.ToMoney()} đ", null, inv.CreatedAt);
                if (priceUp)
                {
                    inv.Status = InvoiceStatus.Exception;
                    inv.MatchNote = "Mực in laser HP 107A: đơn giá hóa đơn cao hơn giá PO 5%";
                    Log(DocType.Invoice, iid, accountant, "Đối chiếu 3 bên: lệch", inv.MatchNote, inv.CreatedAt.AddMinutes(1));
                    db.Invoices.Add(inv);
                    continue;
                }
                inv.Status = InvoiceStatus.Matched; inv.MatchNote = "Khớp 3 bên: số lượng ≤ đã nhận, đơn giá ≤ PO, thuế GTGT đúng.";
                Log(DocType.Invoice, iid, accountant, "Đối chiếu 3 bên: khớp", null, inv.CreatedAt.AddMinutes(1));
                db.Invoices.Add(inv);

                var age = (today - invDate).Days;
                if (age < 6 || po.Status == PoStatus.PartiallyReceived) continue; // mới nhận / chưa giao đủ – chờ đề nghị thanh toán
                decimal penalty = 0, deduct = 0; var notes = new List<string> { $"Thanh toán hóa đơn {inv.Series} {inv.InvoiceNo} – {po.Number}" };
                var contract = po.Contract;
                if (contract != null)
                {
                    var late = (recv.Date - po.DeliveryDate.Date).Days;
                    if (late > 0)
                    {
                        var pct = Math.Min(contract.LatePenaltyCapPercent, contract.LatePenaltyPercentPerDay * late);
                        penalty = Math.Round(inv.SubTotal * pct / 100m, 0);
                        notes.Add($"Phạt chậm giao {late} ngày × {contract.LatePenaltyPercentPerDay:0.###}%/ngày = {pct:0.###}% tiền hàng");
                    }
                    if (contract == cA)
                    {
                        deduct = Math.Min(adv.Amount - recovered, Math.Round(inv.Total * advanceRate, 0));
                        recovered += deduct;
                        notes.Add($"Khấu trừ tạm ứng {advanceRate * 100:0.#}%");
                    }
                }
                var st = age > 30 ? PaymentStatus.Paid : age > 15 ? PaymentStatus.Approved : PaymentStatus.Requested;
                inv.Status = st == PaymentStatus.Paid ? InvoiceStatus.Paid : InvoiceStatus.PaymentRequested;
                var pay = Pay(PaymentType.Invoice, po.Vendor!, invDate.AddDays(rnd.Next(2, 5)), inv.Total, penalty, deduct, string.Join("; ", notes), st, inv, contract);
                Log(DocType.Invoice, iid, accountant, $"Lập đề nghị thanh toán {pay.Number}", null, pay.RequestedAt);
            }

            // Hợp đồng khung B: bảo lãnh bảo hành; phụ lục gia hạn 3 tháng đang chờ duyệt (HĐ sắp hết hạn, còn khối lượng)
            var cB = contractB;
            cB.Guarantees.Add(new ContractGuarantee { Type = GuaranteeType.Warranty, Amount = Math.Round(cB.Value * 0.03m, 0), Reference = "BLBH-2026-077", Bank = "Vietcombank", ExpiryDate = today.AddDays(20) });
            cB.Amendments.Add(new ContractAmendment
            {
                Number = "PL01", Description = "Gia hạn thời gian thực hiện hợp đồng khung 3 tháng để các phòng ban đặt hàng phần khối lượng còn lại; giữ nguyên đơn giá.",
                NewEndDate = cB.EndDate.AddMonths(3), Status = AmendmentStatus.Pending, CreatedById = buyer.Id, CreatedAt = today.AddDays(-2).AddHours(10)
            });
            Log(DocType.Contract, () => cB.Id, buyer, "Trình duyệt phụ lục PL01", $"Gia hạn đến {cB.EndDate.AddMonths(3):dd/MM/yyyy}", today.AddDays(-2).AddHours(10));
        }

        private void Evaluations()
        {
            foreach (var po in pos.Where(p => p.Status == PoStatus.Received && rnd.NextDouble() < 0.5))
            {
                var v = po.Vendor!;
                int S(decimal bias = 0) => Math.Clamp((int)Math.Round((v.Rating == 0 ? 4 : v.Rating) + bias + (decimal)(rnd.NextDouble() - 0.5) * 1.6m), 1, 5);
                var late = lastReceipt[po] > po.DeliveryDate;
                db.VendorEvaluations.Add(new VendorEvaluation
                {
                    VendorId = v.Id, PurchaseOrder = po, EvaluatorId = buyer.Id, Quality = S(), Delivery = S(late ? -1.2m : 0.2m), Price = S(), Service = S(),
                    Comment = late ? "Giao hàng trễ so với cam kết." : "Giao đủ, đúng hẹn.", CreatedAt = lastReceipt[po].AddDays(1).AddHours(10)
                });
            }
        }
    }
}
