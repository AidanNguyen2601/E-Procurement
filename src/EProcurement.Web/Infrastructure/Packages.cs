using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>
/// Gói mua sắm: gom các PR đã duyệt; chỉ gói thuộc KHLCNT đã duyệt mới được lập hồ sơ lựa chọn NCC.
/// Hồ sơ (RFQ / gói thầu / tờ trình / PO) giữ PurchaseRequestId = PR đầu tiên của gói để các luồng hiện có không đổi,
/// dòng hàng lấy từ tất cả PR trong gói.
/// </summary>
public static class Packages
{
    public static Task<ProcurementPackage?> LoadAsync(EProcDbContext db, int id, bool track = false)
    {
        var q = db.ProcurementPackages.Include(p => p.PurchaseRequests).ThenInclude(r => r.Lines)
            .Include(p => p.PurchaseRequests).ThenInclude(r => r.Requester)
            .Include(p => p.SelectionPlan).Include(p => p.Buyer).AsSplitQuery();
        return (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(p => p.Id == id);
    }

    /// <summary>PR đại diện (PR đầu tiên) – gắn vào hồ sơ để các luồng theo PR vẫn chạy</summary>
    public static PurchaseRequest Lead(ProcurementPackage p) => p.PurchaseRequests.OrderBy(r => r.Id).First();

    /// <summary>Dòng hàng của gói: các dòng của từng PR theo thứ tự PR</summary>
    public static List<PurchaseRequestLine> Lines(ProcurementPackage p) =>
        p.PurchaseRequests.OrderBy(r => r.Id).SelectMany(r => r.Lines.OrderBy(l => l.SortOrder)).ToList();

    public static decimal Estimate(IEnumerable<PurchaseRequest> prs) => prs.Sum(r => r.EstimatedTotal);

    /// <summary>PR "ảo" gộp toàn bộ dòng + giá gói – dùng lại các quy tắc theo PR (gợi ý hình thức, PO tham chiếu)</summary>
    public static PurchaseRequest Synthetic(ProcurementPackage p) => new()
    {
        Title = p.Title, EstimatedTotal = p.Budget, Department = p.PurchaseRequests.FirstOrDefault()?.Department ?? "",
        Lines = Lines(p).Select(l => new PurchaseRequestLine { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.EstimatedPrice, Amount = l.Amount }).ToList()
    };

    /// <summary>Kiểm tra lựa chọn hình thức của gói. Trả về danh sách lỗi (rỗng = hợp lệ).</summary>
    public static List<string> MethodProblems(ProcurementMethod method, ProcurementMethod suggested, string? reason, bool hasCoveringReference, decimal budget, decimal estimate, ProcurementSettings s)
    {
        var p = new List<string>();
        if (!ProcurementRules.Supported.Contains(method)) p.Add($"Hình thức \"{method.GetDisplayName()}\" chưa được hỗ trợ.");
        if (method == ProcurementMethod.DirectPurchase && !hasCoveringReference)
            p.Add($"Không có PO tham chiếu hợp lệ (đã nhận đủ trong {s.DirectPurchaseMonths} tháng, cùng mặt hàng, còn đủ khối lượng) để mua sắm trực tiếp.");
        if (method == ProcurementMethod.CompetitiveQuotation && budget > s.CompetitiveQuotationMax)
            p.Add($"Giá gói vượt hạn mức chào hàng cạnh tranh ({s.CompetitiveQuotationMax.ToMoney()} đ).");
        if (ProcurementRules.NeedsReason(method, suggested) && string.IsNullOrWhiteSpace(reason))
            p.Add($"Hệ thống gợi ý \"{suggested.GetDisplayName()}\". Nêu lý do khi chọn \"{method.GetDisplayName()}\".");
        if (budget <= 0) p.Add("Giá gói phải lớn hơn 0.");
        else if (budget < estimate * 0.5m) p.Add($"Giá gói thấp bất thường so với tổng dự toán các PR ({estimate.ToMoney()} đ).");
        return p;
    }

    /// <summary>Gói được lập hồ sơ theo hình thức <paramref name="method"/>? Trả về lý do nếu không.</summary>
    public static string? ExecuteProblem(ProcurementPackage p, ProcurementMethod method)
    {
        if (p.Status is not (PackageStatus.Approved or PackageStatus.Executing))
            return $"Gói {p.Number} chưa nằm trong KHLCNT được phê duyệt (trạng thái: {p.Status.GetDisplayName()}).";
        if (p.Method != method) return $"Gói {p.Number} theo hình thức \"{p.Method.GetDisplayName()}\", không phải \"{method.GetDisplayName()}\".";
        if (p.PurchaseRequests.Count == 0) return $"Gói {p.Number} không có đề nghị mua hàng.";
        return null;
    }

    /// <summary>Có hồ sơ lựa chọn NCC / PO đang xử lý (không hủy) của gói</summary>
    public static async Task<bool> HasActiveDocumentAsync(EProcDbContext db, int packageId) =>
        await db.Rfqs.AnyAsync(r => r.PackageId == packageId && r.Status != RfqStatus.Cancelled)
        || await db.Tenders.AnyAsync(t => t.PackageId == packageId && t.Status != TenderStatus.Cancelled)
        || await db.DirectAwards.AnyAsync(d => d.PackageId == packageId && d.Status != DirectAwardStatus.Cancelled)
        || await db.PurchaseOrders.AnyAsync(o => o.PackageId == packageId && o.Status != PoStatus.Cancelled);

    /// <summary>Bắt đầu lựa chọn NCC: gói → đang thực hiện, các PR → đang mua hàng</summary>
    public static async Task StartAsync(EProcDbContext db, int packageId)
    {
        var p = await db.ProcurementPackages.Include(x => x.PurchaseRequests).FirstAsync(x => x.Id == packageId);
        if (p.Status == PackageStatus.Approved) p.Status = PackageStatus.Executing;
        foreach (var pr in p.PurchaseRequests.Where(r => r.Status == PrStatus.Approved)) pr.Status = PrStatus.Processing;
    }

    /// <summary>Mọi PO của gói đã nhận đủ → gói và các PR hoàn tất</summary>
    public static async Task CompleteIfDoneAsync(EProcDbContext db, int packageId)
    {
        var pos = await db.PurchaseOrders.Where(o => o.PackageId == packageId && o.Status != PoStatus.Cancelled).Select(o => o.Status).ToListAsync();
        if (pos.Count == 0 || pos.Any(s => s != PoStatus.Received)) return;
        var p = await db.ProcurementPackages.Include(x => x.PurchaseRequests).FirstAsync(x => x.Id == packageId);
        p.Status = PackageStatus.Completed;
        foreach (var pr in p.PurchaseRequests.Where(r => r.Status == PrStatus.Processing)) pr.Status = PrStatus.Completed;
    }

    /// <summary>
    /// Dữ liệu trước khi có gói mua sắm: mỗi PR đã chọn hình thức (không đặt theo hợp đồng khung) → một gói,
    /// gắn các hồ sơ đã lập với gói và đưa vào một KHLCNT "chuyển đổi" đã phê duyệt theo năm. Chạy lại không tạo trùng.
    /// </summary>
    public static async Task EnsureLegacyAsync(EProcDbContext db)
    {
        var prs = await db.PurchaseRequests.Where(p => p.PackageId == null && p.ContractId == null && p.Method != null
                && (p.Status == PrStatus.Approved || p.Status == PrStatus.Processing || p.Status == PrStatus.Completed))
            .OrderBy(p => p.Id).ToListAsync();
        if (prs.Count == 0) return;
        var fallbackUser = await db.Users.Where(u => u.Role == AppRoles.Buyer || u.Role == AppRoles.Admin).OrderBy(u => u.Role == AppRoles.Buyer ? 0 : 1).ThenBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync()
                           ?? await db.Users.OrderBy(u => u.Id).Select(u => (int?)u.Id).FirstOrDefaultAsync();
        if (fallbackUser == null) return;

        var plans = new Dictionary<int, SelectionPlan>();
        foreach (var pr in prs)
        {
            var year = pr.RequestDate.Year;
            if (!plans.TryGetValue(year, out var plan))
            {
                var no = $"KH-CD-{year}";
                plan = await db.SelectionPlans.FirstOrDefaultAsync(x => x.Number == no) ?? new SelectionPlan
                {
                    Number = no, Title = $"KHLCNT năm {year} (chuyển đổi từ dữ liệu trước khi có gói mua sắm)",
                    Basis = "Các đề nghị mua hàng đã được chọn hình thức lựa chọn NCC trước khi hệ thống áp dụng gói mua sắm.",
                    Status = SelectionPlanStatus.Approved, CreatedById = fallbackUser.Value, DecidedById = fallbackUser.Value,
                    CreatedAt = DateTime.Now, SubmittedAt = DateTime.Now, DecidedAt = DateTime.Now, DecisionNote = "Chuyển đổi tự động"
                };
                if (plan.Id == 0) db.SelectionPlans.Add(plan);
                plans[year] = plan;
            }
            var prefix = pr.Number.StartsWith(Data.SampleDataSeeder.Prefix) ? Data.SampleDataSeeder.Prefix + "GM" : "GM";
            var buyer = await db.Rfqs.Where(r => r.PurchaseRequestId == pr.Id).Select(r => (int?)r.BuyerId).FirstOrDefaultAsync()
                        ?? await db.Tenders.Where(r => r.PurchaseRequestId == pr.Id).Select(r => (int?)r.BuyerId).FirstOrDefaultAsync()
                        ?? await db.DirectAwards.Where(r => r.PurchaseRequestId == pr.Id).Select(r => (int?)r.BuyerId).FirstOrDefaultAsync()
                        ?? await db.PurchaseOrders.Where(r => r.PurchaseRequestId == pr.Id).Select(r => (int?)r.BuyerId).FirstOrDefaultAsync()
                        ?? fallbackUser.Value;
            var tender = await db.Tenders.Where(t => t.PurchaseRequestId == pr.Id && t.Status != TenderStatus.Cancelled).FirstOrDefaultAsync();
            var pkg = new ProcurementPackage
            {
                Number = await DocNumbers.NextAsync(db.ProcurementPackages.Select(x => x.Number), prefix),
                Title = pr.Title, Budget = tender?.PackageBudget ?? pr.EstimatedTotal, Method = pr.Method!.Value, MethodReason = pr.MethodReason,
                EnvelopeMethod = tender?.EnvelopeMethod ?? EnvelopeMethod.OneEnvelope, ContractType = tender?.ContractType ?? "Trọn gói",
                ContractDurationDays = tender?.ContractDurationDays ?? Math.Max(1, (pr.NeededDate - pr.RequestDate).Days),
                PlannedStart = pr.SubmittedAt?.Date ?? pr.RequestDate, BuyerId = buyer, CreatedAt = DateTime.Now, SelectionPlan = plan,
                Status = pr.Status switch { PrStatus.Completed => PackageStatus.Completed, PrStatus.Processing => PackageStatus.Executing, _ => PackageStatus.Approved },
                Note = "Tạo tự động từ đề nghị " + pr.Number
            };
            pkg.PurchaseRequests.Add(pr);
            db.ProcurementPackages.Add(pkg);
            await db.SaveChangesAsync(); // cần Id gói + số gói không trùng cho vòng sau
            await db.Rfqs.Where(r => r.PurchaseRequestId == pr.Id && r.PackageId == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.PackageId, pkg.Id));
            await db.Tenders.Where(r => r.PurchaseRequestId == pr.Id && r.PackageId == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.PackageId, pkg.Id));
            await db.DirectAwards.Where(r => r.PurchaseRequestId == pr.Id && r.PackageId == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.PackageId, pkg.Id));
            await db.PurchaseOrders.Where(r => r.PurchaseRequestId == pr.Id && r.PackageId == null).ExecuteUpdateAsync(x => x.SetProperty(r => r.PackageId, pkg.Id));
        }
    }

    /// <summary>Gợi ý lĩnh vực của gói từ tên gói, nội dung dòng hàng và nhóm hàng (tìm từ khóa, không phân biệt hoa thường)</summary>
    public static string SuggestField(string text)
    {
        var t = (text ?? "").ToLowerInvariant();
        bool Has(params string[] k) => k.Any(x => t.Contains(x));
        if (Has("xây dựng", "xây lắp", "thi công", "nhà xưởng", "nhà kho", "công trình")) return "Xây lắp";
        if (Has("tư vấn", "thiết kế", "giám sát")) return "Dịch vụ tư vấn";
        if (Has("bảo trì", "sửa chữa", "bảo dưỡng", "khắc phục")) return "Bảo trì, sửa chữa";
        if (Has("cntt", "phần mềm", "máy tính", "laptop", "máy chủ", "màn hình", "wifi", "mạng", "bản quyền", "ups", "camera", "máy in")) return "Thiết bị & phần mềm CNTT";
        if (Has("dịch vụ", "vệ sinh", "bảo vệ", "thuê")) return "Dịch vụ phi tư vấn";
        return "Hàng hóa";
    }

    /// <summary>Gói chưa có lĩnh vực (dữ liệu cũ): gán theo gợi ý từ tên gói và nội dung / nhóm hàng của các đề nghị trong gói. Gọi lại an toàn.</summary>
    public static async Task BackfillFieldsAsync(EProcDbContext db)
    {
        var pkgs = await db.ProcurementPackages.Where(p => p.Field == null).Include(p => p.PurchaseRequests).ThenInclude(r => r.Lines).ThenInclude(l => l.Item).ThenInclude(i => i!.Category).ToListAsync();
        if (pkgs.Count == 0) return;
        foreach (var p in pkgs)
        {
            var text = p.Title + " " + string.Join(" ", p.PurchaseRequests.SelectMany(r => r.Lines).Select(l => l.Description + " " + l.Item?.Category?.Name));
            p.Field = SuggestField(text);
        }
        await db.SaveChangesAsync();
    }
}
