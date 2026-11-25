using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Hạn mức giá trị gói và thời gian thực hiện (ngày) cho từng giai đoạn của gói thuộc hạn mức đó</summary>
public class PackageTier
{
    public string Name { get; set; } = "";
    /// <summary>Lĩnh vực (loại mua sắm) áp dụng; rỗng = bộ hạn mức mặc định cho mọi lĩnh vực chưa có bộ riêng</summary>
    public string? Field { get; set; }
    /// <summary>Hình thức mua sắm áp dụng (mua trực tiếp / chào hàng cạnh tranh / chỉ định thầu / đấu thầu rộng rãi); null = mọi hình thức</summary>
    public ProcurementMethod? Method { get; set; }
    /// <summary>Phạm vi áp dụng để hiển thị: "Xây lắp · Đấu thầu rộng rãi"; rỗng = mặc định</summary>
    public string Scope => string.Join(" · ", new[] { string.IsNullOrWhiteSpace(Field) ? null : Field, Method?.GetDisplayName() }.Where(x => x != null));
    /// <summary>Giá gói tối đa (trước VAT) của hạn mức; 0 = không giới hạn (hạn mức cuối cùng)</summary>
    public decimal MaxValue { get; set; }
    public int PlanDays { get; set; }
    public int ApprovalDays { get; set; }
    public int SelectionDays { get; set; }
    public int ContractDays { get; set; }
    public int ExecutionDays { get; set; }
    public int PaymentDays { get; set; }

    public int Days(int stage) => stage switch { 0 => PlanDays, 1 => ApprovalDays, 2 => SelectionDays, 3 => ContractDays, 4 => ExecutionDays, _ => PaymentDays };
    public int TotalDays => PlanDays + ApprovalDays + SelectionDays + ContractDays + ExecutionDays + PaymentDays;
}

public class PackageTierSettings
{
    public List<PackageTier> Tiers { get; set; } = Defaults();

    public static List<PackageTier> Defaults() => new()
    {
        // Bộ mặc định (mọi lĩnh vực, mọi hình thức) – dùng khi không có bộ riêng phù hợp
        new() { Name = "Gói nhỏ", MaxValue = 100_000_000, PlanDays = 3, ApprovalDays = 2, SelectionDays = 7, ContractDays = 3, ExecutionDays = 30, PaymentDays = 15 },
        new() { Name = "Gói vừa", MaxValue = 1_000_000_000, PlanDays = 5, ApprovalDays = 3, SelectionDays = 20, ContractDays = 7, ExecutionDays = 60, PaymentDays = 30 },
        new() { Name = "Gói lớn", MaxValue = 5_000_000_000, PlanDays = 7, ApprovalDays = 5, SelectionDays = 45, ContractDays = 10, ExecutionDays = 120, PaymentDays = 30 },
        new() { Name = "Gói rất lớn", MaxValue = 0, PlanDays = 10, ApprovalDays = 7, SelectionDays = 75, ContractDays = 15, ExecutionDays = 180, PaymentDays = 45 },
        // Theo hình thức mua sắm: càng cạnh tranh, giai đoạn lựa chọn NCC càng dài
        new() { Method = ProcurementMethod.DirectPurchase, Name = "Mua trực tiếp", MaxValue = 0, PlanDays = 1, ApprovalDays = 1, SelectionDays = 3, ContractDays = 1, ExecutionDays = 15, PaymentDays = 10 },
        new() { Method = ProcurementMethod.DirectAward, Name = "Chỉ định thầu nhỏ", MaxValue = 100_000_000, PlanDays = 2, ApprovalDays = 2, SelectionDays = 5, ContractDays = 2, ExecutionDays = 30, PaymentDays = 15 },
        new() { Method = ProcurementMethod.DirectAward, Name = "Chỉ định thầu lớn", MaxValue = 0, PlanDays = 3, ApprovalDays = 3, SelectionDays = 10, ContractDays = 3, ExecutionDays = 45, PaymentDays = 20 },
        new() { Method = ProcurementMethod.CompetitiveQuotation, Name = "Chào hàng nhỏ", MaxValue = 500_000_000, PlanDays = 3, ApprovalDays = 2, SelectionDays = 12, ContractDays = 4, ExecutionDays = 45, PaymentDays = 20 },
        new() { Method = ProcurementMethod.CompetitiveQuotation, Name = "Chào hàng lớn", MaxValue = 0, PlanDays = 5, ApprovalDays = 3, SelectionDays = 20, ContractDays = 6, ExecutionDays = 60, PaymentDays = 30 },
        new() { Method = ProcurementMethod.OpenTender, Name = "Đấu thầu vừa", MaxValue = 5_000_000_000, PlanDays = 7, ApprovalDays = 5, SelectionDays = 50, ContractDays = 10, ExecutionDays = 90, PaymentDays = 30 },
        new() { Method = ProcurementMethod.OpenTender, Name = "Đấu thầu lớn", MaxValue = 0, PlanDays = 10, ApprovalDays = 7, SelectionDays = 80, ContractDays = 15, ExecutionDays = 150, PaymentDays = 45 },
        // Xây lắp: thi công kéo dài hơn (ưu tiên hơn bộ theo hình thức)
        new() { Field = "Xây lắp", Name = "Xây lắp nhỏ", MaxValue = 1_000_000_000, PlanDays = 7, ApprovalDays = 3, SelectionDays = 25, ContractDays = 7, ExecutionDays = 120, PaymentDays = 30 },
        new() { Field = "Xây lắp", Name = "Xây lắp vừa", MaxValue = 10_000_000_000, PlanDays = 10, ApprovalDays = 5, SelectionDays = 45, ContractDays = 10, ExecutionDays = 240, PaymentDays = 45 },
        new() { Field = "Xây lắp", Name = "Xây lắp lớn", MaxValue = 0, PlanDays = 15, ApprovalDays = 7, SelectionDays = 75, ContractDays = 15, ExecutionDays = 400, PaymentDays = 60 }
    };

    private static string Key(string? field) => (field ?? "").Trim();
    private static bool SameField(PackageTier t, string? field) => string.Equals(Key(t.Field), Key(field), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Hạn mức áp dụng cho một gói theo lĩnh vực và hình thức: dùng bộ cụ thể nhất có sẵn, theo thứ tự
    /// (lĩnh vực + hình thức) → (lĩnh vực) → (hình thức) → (mặc định). Trong bộ: hạn mức đầu tiên có MaxValue ≥ giá trị (hoặc không giới hạn).
    /// </summary>
    public PackageTier TierFor(decimal value, string? field = null, ProcurementMethod? method = null)
    {
        var hasField = Key(field).Length > 0;
        List<PackageTier> Set(bool byField, bool byMethod) => Tiers.Where(t => (byField ? SameField(t, field) : Key(t.Field).Length == 0) && (byMethod ? t.Method == method : t.Method == null)).ToList();
        var group = new List<PackageTier>();
        if (hasField && method != null) group = Set(true, true);
        if (group.Count == 0 && hasField) group = Set(true, false);
        if (group.Count == 0 && method != null) group = Set(false, true);
        if (group.Count == 0) group = Set(false, false);
        if (group.Count == 0) group = Tiers.ToList();
        return group.OrderBy(t => t.MaxValue == 0 ? decimal.MaxValue : t.MaxValue).FirstOrDefault(t => t.MaxValue == 0 || value <= t.MaxValue) ?? group.Last();
    }

    /// <summary>Lĩnh vực có bộ hạn mức riêng</summary>
    public IEnumerable<string> Fields => Tiers.Select(t => Key(t.Field)).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Kiểm tra cấu hình theo từng bộ (lĩnh vực + hình thức): phải có bộ mặc định (cả hai để trống); trong bộ: tên không trống,
    /// giá trị tăng dần, chỉ hạn mức cuối không giới hạn, số ngày 0–1000, tổng &gt; 0. Trả về danh sách lỗi.
    /// </summary>
    public static List<string> Validate(IReadOnlyList<PackageTier> tiers)
    {
        var errors = new List<string>();
        if (tiers.Count == 0) { errors.Add(I18n.T("Cần ít nhất một hạn mức.")); return errors; }
        if (!tiers.Any(t => Key(t.Field).Length == 0 && t.Method == null)) errors.Add(I18n.T("Cần có bộ hạn mức mặc định (lĩnh vực và hình thức để trống) cho các gói chưa có bộ riêng."));
        foreach (var g in tiers.GroupBy(t => Key(t.Field).ToLowerInvariant() + "|" + (t.Method?.ToString() ?? "")))
        {
            var list = g.ToList(); var scope = list[0].Scope; var pre = scope.Length == 0 ? "" : "[" + scope + "] ";
            for (var i = 0; i < list.Count; i++)
            {
                var t = list[i];
                if (string.IsNullOrWhiteSpace(t.Name)) errors.Add(pre + I18n.T("Hạn mức {0}: nhập tên.", i + 1));
                if (t.MaxValue < 0) errors.Add(pre + I18n.T("Hạn mức {0}: giá trị tối đa không được âm.", i + 1));
                if (t.MaxValue == 0 && i != list.Count - 1) errors.Add(pre + I18n.T("Hạn mức {0}: chỉ hạn mức cuối cùng được để trống (không giới hạn).", i + 1));
                if (t.MaxValue != 0 && i == list.Count - 1) errors.Add(pre + I18n.T("Hạn mức cuối cùng phải không giới hạn (để trống giá trị tối đa) để mọi gói đều thuộc một hạn mức."));
                if (i > 0 && t.MaxValue != 0 && list[i - 1].MaxValue != 0 && t.MaxValue <= list[i - 1].MaxValue) errors.Add(pre + I18n.T("Hạn mức {0}: giá trị tối đa phải lớn hơn hạn mức trước.", i + 1));
                for (var st = 0; st < 6; st++) if (t.Days(st) < 0 || t.Days(st) > 1000) { errors.Add(pre + I18n.T("Hạn mức {0}: số ngày mỗi giai đoạn từ 0 đến 1000.", i + 1)); break; }
                if (t.TotalDays <= 0) errors.Add(pre + I18n.T("Hạn mức {0}: tổng thời gian phải lớn hơn 0.", i + 1));
            }
        }
        return errors;
    }
}

public enum StageStatus { Done, Current, Pending }

public record StageInfo(int Index, string Name, StageStatus Status, DateTime? Start, DateTime? Due, DateTime? CompletedAt, int PlannedDays, int DaysUsed, int DaysLate)
{
    public bool Late => DaysLate > 0;
}

public enum ProgressState { OnTrack, DueSoon, Overdue, Completed, Cancelled }

/// <summary>Các mốc thời gian của gói – đầu vào thuần để tính tiến độ (dễ kiểm thử)</summary>
public record PackageFacts(DateTime Created, bool Cancelled, DateTime? PlanSubmittedAt, DateTime? PlanApprovedAt, DateTime? SelectionDoneAt,
    DateTime? ContractAt, DateTime? ExecutionDoneAt, DateTime? PaymentDoneAt);

public class PackageProgressResult
{
    public PackageTier Tier { get; set; } = new();
    public List<StageInfo> Stages { get; set; } = new();
    public int CurrentIndex { get; set; } = -1;
    public ProgressState State { get; set; }
    public StageInfo? Current => CurrentIndex >= 0 ? Stages[CurrentIndex] : null;
    public int DoneCount => Stages.Count(s => s.Status == StageStatus.Done);
    /// <summary>Hạn hoàn thành toàn bộ (theo kế hoạch của hạn mức, tính chuỗi từ thời điểm lập gói)</summary>
    public DateTime? FinalDue { get; set; }
    public int ElapsedDays { get; set; }
}

public static class PackageProgress
{
    public static readonly string[] StageNames =
    {
        "Lập gói mua sắm", "Duyệt kế hoạch lựa chọn NCC", "Lựa chọn nhà cung cấp", "Ký hợp đồng / đặt hàng", "Thực hiện (giao nhận / nghiệm thu)", "Thanh toán"
    };

    /// <summary>Gần hạn: còn ≤ 20% thời gian giai đoạn (tối thiểu 1 ngày)</summary>
    public static bool IsDueSoon(StageInfo s, DateTime now) => s.Due.HasValue && s.Due.Value >= now && (s.Due.Value - now).TotalDays <= Math.Max(1, s.PlannedDays * 0.2);

    public static PackageProgressResult Compute(PackageFacts f, PackageTier tier, DateTime now)
    {
        var done = new DateTime?[] { f.PlanSubmittedAt, f.PlanApprovedAt, f.SelectionDoneAt, f.ContractAt, f.ExecutionDoneAt, f.PaymentDoneAt };
        // một giai đoạn sau đã xong thì các giai đoạn trước coi như xong (dữ liệu cũ thiếu mốc) – dùng mốc của giai đoạn sau làm thời điểm hoàn thành
        for (var i = done.Length - 2; i >= 0; i--) if (done[i] == null && done[i + 1] != null) done[i] = done[i + 1];
        var res = new PackageProgressResult { Tier = tier };
        DateTime? start = f.Created; DateTime? plannedCursor = f.Created;
        var currentFound = false;
        for (var i = 0; i < StageNames.Length; i++)
        {
            var days = tier.Days(i);
            var completed = done[i];
            // thời điểm hoàn thành không được trước thời điểm bắt đầu (mốc lệch giờ)
            if (completed.HasValue && start.HasValue && completed < start) completed = start;
            var due = start?.AddDays(days);
            StageStatus st; int used; int late;
            if (completed.HasValue)
            {
                st = StageStatus.Done; used = (int)Math.Ceiling(((completed.Value - (start ?? completed.Value)).TotalDays)); late = due.HasValue ? Math.Max(0, (int)Math.Ceiling((completed.Value - due.Value).TotalDays)) : 0;
            }
            else if (!currentFound && !f.Cancelled)
            {
                st = StageStatus.Current; currentFound = true; res.CurrentIndex = i;
                used = start.HasValue ? Math.Max(0, (int)Math.Floor((now - start.Value).TotalDays)) : 0;
                late = due.HasValue && now > due.Value ? (int)Math.Ceiling((now - due.Value).TotalDays) : 0;
            }
            else
            {
                st = StageStatus.Pending; used = 0; late = 0;
                due = plannedCursor?.AddDays(days);   // dự kiến: nối tiếp hạn của giai đoạn trước
            }
            res.Stages.Add(new StageInfo(i, StageNames[i], st, st == StageStatus.Pending ? plannedCursor : start, due, completed, days, used, late));
            // giai đoạn tiếp theo bắt đầu khi giai đoạn này hoàn thành; chưa xong thì tính theo hạn kế hoạch
            start = completed;
            plannedCursor = completed ?? due ?? plannedCursor;
            if (st == StageStatus.Current) { start = null; plannedCursor = due; }
            else if (st == StageStatus.Pending) start = null;
        }
        res.FinalDue = res.Stages.Last().Due;
        res.ElapsedDays = (int)Math.Max(0, ((res.Stages.Last().CompletedAt ?? now) - f.Created).TotalDays);
        res.State = f.Cancelled ? ProgressState.Cancelled
            : res.CurrentIndex < 0 ? ProgressState.Completed
            : res.Current!.Late ? ProgressState.Overdue
            : IsDueSoon(res.Current, now) ? ProgressState.DueSoon : ProgressState.OnTrack;
        return res;
    }
}

/// <summary>Nạp mốc thời gian của các gói từ dữ liệu (KHLCNT, hồ sơ chọn NCC, hợp đồng, đơn hàng, nhận hàng, thanh toán, nhật ký) rồi tính tiến độ</summary>
public class PackageProgressService
{
    private readonly EProcDbContext _db;
    private readonly SettingsStore _store;
    public PackageProgressService(EProcDbContext db, SettingsStore store) { _db = db; _store = store; }

    public Task<PackageTierSettings> TiersAsync() => _store.GetAsync(SettingsStore.PackageTiers, () => new PackageTierSettings());

    public async Task<List<(ProcurementPackage Package, PackageProgressResult Progress)>> ComputeAsync(IReadOnlyCollection<int>? ids = null, int take = 500)
    {
        var tiers = await TiersAsync();
        var q = _db.ProcurementPackages.AsNoTracking().Include(p => p.SelectionPlan).Include(p => p.Buyer).AsQueryable();
        if (ids != null) q = q.Where(p => ids.Contains(p.Id));
        var pkgs = await q.OrderByDescending(p => p.CreatedAt).Take(take).ToListAsync();
        var pids = pkgs.Select(p => p.Id).ToList();
        if (pids.Count == 0) return new();

        var tenders = await _db.Tenders.AsNoTracking().Where(t => t.PackageId != null && pids.Contains(t.PackageId.Value)).Select(t => new { t.Id, PackageId = t.PackageId!.Value, t.Status, t.ClosingAt, t.OpenedAt }).ToListAsync();
        var rfqs = await _db.Rfqs.AsNoTracking().Where(r => r.PackageId != null && pids.Contains(r.PackageId.Value)).Select(r => new { r.Id, PackageId = r.PackageId!.Value, r.Status, r.Deadline }).ToListAsync();
        var das = await _db.DirectAwards.AsNoTracking().Where(d => d.PackageId != null && pids.Contains(d.PackageId.Value)).Select(d => new { d.Id, PackageId = d.PackageId!.Value, d.Status, d.ApprovedAt, d.CreatedAt }).ToListAsync();
        var tenderIds = tenders.Select(t => t.Id).ToList();
        var contracts = await _db.Contracts.AsNoTracking().Where(c => c.TenderId != null && tenderIds.Contains(c.TenderId.Value)).Select(c => new { c.Id, TenderId = c.TenderId!.Value, c.SignedDate, c.EndDate, c.Status }).ToListAsync();
        var pos = await _db.PurchaseOrders.AsNoTracking().Where(p => p.PackageId != null && pids.Contains(p.PackageId.Value) && p.Status != PoStatus.Cancelled).Select(p => new { p.Id, PackageId = p.PackageId!.Value, p.OrderDate, p.Status, p.ContractId }).ToListAsync();
        var poIds = pos.Select(p => p.Id).ToList();
        var receipts = await _db.GoodsReceipts.AsNoTracking().Where(g => poIds.Contains(g.PurchaseOrderId)).Select(g => new { g.PurchaseOrderId, g.ReceiptDate }).ToListAsync();
        var contractIds = contracts.Select(c => c.Id).ToList();
        var invoices = await _db.Invoices.AsNoTracking().Where(i => poIds.Contains(i.PurchaseOrderId)).Select(i => new { i.Id, i.PurchaseOrderId }).ToListAsync();
        var invoiceIds = invoices.Select(i => i.Id).ToList();
        var pays = await _db.Payments.AsNoTracking().Where(p => p.Status != PaymentStatus.Rejected && ((p.ContractId != null && contractIds.Contains(p.ContractId.Value)) || (p.InvoiceId != null && invoiceIds.Contains(p.InvoiceId.Value))))
            .Select(p => new { p.ContractId, p.InvoiceId, p.Status, p.PaidAt }).ToListAsync();
        var tenderAward = (await _db.ApprovalHistories.AsNoTracking().Where(h => h.DocumentType == DocType.Tender && tenderIds.Contains(h.DocumentId) && h.Action.StartsWith("Giám đốc phê duyệt kết quả lựa chọn nhà thầu"))
            .Select(h => new { h.DocumentId, h.CreatedAt }).ToListAsync()).GroupBy(h => h.DocumentId).ToDictionary(g => g.Key, g => g.Min(x => x.CreatedAt));
        var rfqIds = rfqs.Select(r => r.Id).ToList();
        var rfqAward = (await _db.ApprovalHistories.AsNoTracking().Where(h => h.DocumentType == DocType.RFQ && rfqIds.Contains(h.DocumentId) && h.Action.StartsWith("Chọn NCC trúng"))
            .Select(h => new { h.DocumentId, h.CreatedAt }).ToListAsync()).GroupBy(h => h.DocumentId).ToDictionary(g => g.Key, g => g.Min(x => x.CreatedAt));
        var invoicePo = invoices.ToDictionary(i => i.Id, i => i.PurchaseOrderId);
        var poPkg = pos.ToDictionary(p => p.Id, p => p.PackageId);
        var contractPkg = contracts.ToDictionary(c => c.Id, c => tenders.First(t => t.Id == c.TenderId).PackageId);

        var result = new List<(ProcurementPackage, PackageProgressResult)>();
        var now = DateTime.Now;
        foreach (var p in pkgs)
        {
            var plan = p.SelectionPlan;
            DateTime? planSubmitted = plan == null ? null : plan.SubmittedAt ?? plan.CreatedAt;
            DateTime? planApproved = plan is { Status: SelectionPlanStatus.Approved } ? plan.DecidedAt ?? plan.SubmittedAt ?? plan.CreatedAt : null;

            var sel = new List<DateTime>();
            foreach (var t in tenders.Where(x => x.PackageId == p.Id && x.Status >= TenderStatus.Awarded && x.Status != TenderStatus.Cancelled)) sel.Add(tenderAward.TryGetValue(t.Id, out var a) ? a : t.OpenedAt ?? t.ClosingAt);
            foreach (var r in rfqs.Where(x => x.PackageId == p.Id && x.Status == RfqStatus.Awarded)) sel.Add(rfqAward.TryGetValue(r.Id, out var a) ? a : r.Deadline);
            foreach (var d in das.Where(x => x.PackageId == p.Id && x.Status == DirectAwardStatus.Approved)) sel.Add(d.ApprovedAt ?? d.CreatedAt);
            DateTime? selectionDone = sel.Count > 0 ? sel.Min() : null;

            var myContracts = contracts.Where(c => contractPkg[c.Id] == p.Id).ToList();
            var myPos = pos.Where(x => x.PackageId == p.Id).ToList();
            var contractDates = myContracts.Select(c => c.SignedDate).Concat(myPos.Select(x => x.OrderDate)).ToList();
            DateTime? contractAt = contractDates.Count > 0 ? contractDates.Min() : null;

            DateTime? execDone = null, payDone = null;
            if (contractAt != null)
            {
                var lastReceipt = receipts.Where(r => poPkg.TryGetValue(r.PurchaseOrderId, out var pp) && pp == p.Id).Select(r => (DateTime?)r.ReceiptDate).Max();
                var execComplete = myContracts.Count > 0 ? myContracts.All(c => c.Status != ContractStatus.Active) : myPos.Count > 0 && myPos.All(x => x.Status == PoStatus.Received);
                if (execComplete) execDone = lastReceipt ?? myContracts.Select(c => (DateTime?)c.EndDate).Max() ?? contractAt;
                if (execDone != null)
                {
                    var mine = pays.Where(x => (x.ContractId != null && contractPkg.TryGetValue(x.ContractId.Value, out var cp) && cp == p.Id)
                        || (x.InvoiceId != null && invoicePo.TryGetValue(x.InvoiceId.Value, out var po) && poPkg.TryGetValue(po, out var pp2) && pp2 == p.Id)).ToList();
                    if (mine.Count > 0 && mine.All(x => x.Status == PaymentStatus.Paid)) payDone = mine.Max(x => x.PaidAt) ?? execDone;
                }
            }
            var facts = new PackageFacts(p.CreatedAt, p.Status == PackageStatus.Cancelled, planSubmitted, planApproved, selectionDone, contractAt, execDone, payDone);
            result.Add((p, PackageProgress.Compute(facts, tiers.TierFor(p.Budget, p.Field, p.Method), now)));
        }
        return result;
    }
}
