using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

// =====================================================================
//  NGÂN HÀNG GIÁ: tập hợp mọi đơn giá đã phát sinh + giá thu thập thủ công
// =====================================================================
public record PriceObservation(int ItemId, DateTime Date, decimal UnitPrice, decimal Quantity, string? Vendor, string Source, string? DocNumber, string? Url);

public record PriceStats(int ItemId, int Count, decimal Min, decimal Avg, decimal Median, decimal Max, decimal Last, DateTime LastDate)
{
    public static PriceStats? From(int itemId, IReadOnlyCollection<PriceObservation> obs)
    {
        if (obs.Count == 0) return null;
        var sorted = obs.Select(o => o.UnitPrice).OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[mid] : Math.Round((sorted[mid - 1] + sorted[mid]) / 2, 0);
        var last = obs.OrderByDescending(o => o.Date).First();
        return new PriceStats(itemId, obs.Count, sorted[0], Math.Round(sorted.Average(), 0), median, sorted[^1], last.UnitPrice, last.Date);
    }
}

public static class PriceBank
{
    /// <summary>
    /// Đơn giá theo mặt hàng (trước VAT) từ: PO đã duyệt, báo giá chào hàng cạnh tranh đã mở, HSDT đã mở giá, hồ sơ chỉ định,
    /// hợp đồng và giá thu thập thủ công. <paramref name="itemIds"/> null = mọi mặt hàng.
    /// </summary>
    public static async Task<List<PriceObservation>> ObservationsAsync(EProcDbContext db, ICollection<int>? itemIds, DateTime from, DateTime to)
    {
        var end = to.Date.AddDays(1);
        bool In(int? id) => id.HasValue && (itemIds == null || itemIds.Contains(id.Value));
        var list = new List<PriceObservation>();

        var po = await db.PurchaseOrderLines.AsNoTracking().Where(l => l.ItemId != null && l.UnitPrice > 0
                && l.PurchaseOrder!.Status >= PoStatus.Approved && l.PurchaseOrder.Status != PoStatus.Cancelled
                && l.PurchaseOrder.OrderDate >= from && l.PurchaseOrder.OrderDate < end && (itemIds == null || itemIds.Contains(l.ItemId!.Value)))
            .Select(l => new { l.ItemId, l.PurchaseOrder!.OrderDate, l.UnitPrice, l.Quantity, V = l.PurchaseOrder.Vendor!.Name, l.PurchaseOrder.Number, l.PurchaseOrderId }).ToListAsync();
        list.AddRange(po.Select(x => new PriceObservation(x.ItemId!.Value, x.OrderDate, x.UnitPrice, x.Quantity, x.V, "Đơn hàng", x.Number, $"/PurchaseOrders/Details/{x.PurchaseOrderId}")));

        var quotes = await db.VendorQuoteLines.AsNoTracking().Where(q => q.UnitPrice > 0 && q.RfqLine!.ItemId != null
                && q.RfqVendor!.Rfq!.OpenedAt != null && q.RfqVendor.Rfq.OpenedAt >= from && q.RfqVendor.Rfq.OpenedAt < end
                && (itemIds == null || itemIds.Contains(q.RfqLine.ItemId!.Value)))
            .Select(q => new { q.RfqLine!.ItemId, D = q.RfqVendor!.RespondedAt ?? q.RfqVendor.Rfq!.OpenedAt!.Value, q.UnitPrice, q.RfqLine.Quantity, V = q.RfqVendor.Vendor!.Name, q.RfqVendor.Rfq!.Number, q.RfqVendor.RfqId }).ToListAsync();
        list.AddRange(quotes.Select(x => new PriceObservation(x.ItemId!.Value, x.D, x.UnitPrice, x.Quantity, x.V, "Báo giá chào hàng cạnh tranh", x.Number, $"/Rfqs/Details/{x.RfqId}")));

        // HSDT: chỉ khi giá đã được mở (một túi: sau mở thầu; hai túi: sau mở hồ sơ tài chính)
        var bids = await db.TenderBidLines.AsNoTracking().Where(b => b.UnitPrice > 0 && b.TenderLine!.ItemId != null
                && b.TenderBidder!.Tender!.OpenedAt != null && (b.TenderBidder.Tender.EnvelopeMethod == EnvelopeMethod.OneEnvelope || b.TenderBidder.Tender.FinancialOpenedAt != null)
                && b.TenderBidder.Tender.OpenedAt >= from && b.TenderBidder.Tender.OpenedAt < end && (itemIds == null || itemIds.Contains(b.TenderLine.ItemId!.Value)))
            .Select(b => new { b.TenderLine!.ItemId, D = b.TenderBidder!.Tender!.OpenedAt!.Value, P = b.UnitPrice * (1 - b.TenderBidder.DiscountPercent / 100m), b.TenderLine.Quantity, V = b.TenderBidder.Vendor!.Name, b.TenderBidder.Tender.Number, b.TenderBidder.TenderId }).ToListAsync();
        list.AddRange(bids.Select(x => new PriceObservation(x.ItemId!.Value, x.D, Math.Round(x.P, 0), x.Quantity, x.V, "Hồ sơ dự thầu", x.Number, $"/Tenders/Details/{x.TenderId}")));

        var das = await db.DirectAwardLines.AsNoTracking().Where(l => l.ItemId != null && (l.NegotiatedPrice > 0 || l.ProposedPrice > 0)
                && l.DirectAward!.Status != DirectAwardStatus.Cancelled && l.DirectAward.CreatedAt >= from && l.DirectAward.CreatedAt < end
                && (itemIds == null || itemIds.Contains(l.ItemId!.Value)))
            .Select(l => new { l.ItemId, D = l.DirectAward!.ApprovedAt ?? l.DirectAward.CreatedAt, P = l.NegotiatedPrice > 0 ? l.NegotiatedPrice : l.ProposedPrice, l.Quantity, V = l.DirectAward.Vendor!.Name, l.DirectAward.Number, l.DirectAwardId }).ToListAsync();
        list.AddRange(das.Select(x => new PriceObservation(x.ItemId!.Value, x.D, x.P, x.Quantity, x.V, "Chào giá chỉ định", x.Number, $"/DirectAwards/Details/{x.DirectAwardId}")));

        var contracts = await db.ContractLines.AsNoTracking().Where(l => l.ItemId != null && l.UnitPrice > 0 && l.Contract!.SignedDate >= from && l.Contract.SignedDate < end
                && (itemIds == null || itemIds.Contains(l.ItemId!.Value)))
            .Select(l => new { l.ItemId, l.Contract!.SignedDate, l.UnitPrice, l.Quantity, V = l.Contract.Vendor!.Name, l.Contract.Number, l.ContractId }).ToListAsync();
        list.AddRange(contracts.Select(x => new PriceObservation(x.ItemId!.Value, x.SignedDate, x.UnitPrice, x.Quantity, x.V, "Hợp đồng", x.Number, $"/Contracts/Details/{x.ContractId}")));

        // lịch sử giá nhập tay: chỉ giá bằng tiền Việt mới đưa vào thống kê (ngoại tệ xem ở trang Lịch sử giá)
        var manual = await db.PriceRecords.AsNoTracking().Where(r => r.PriceDate >= from && r.PriceDate < end && (itemIds == null || itemIds.Contains(r.ItemId))
                && (r.Currency == null || r.Currency == "VND"))
            .Select(r => new { r.Id, r.ItemId, r.PriceDate, r.UnitPrice, V = r.Vendor != null ? r.Vendor.Name : r.SupplierName, r.Source, r.SourceNote, r.Region }).ToListAsync();
        list.AddRange(manual.Select(x => new PriceObservation(x.ItemId, x.PriceDate, Math.Round(x.UnitPrice, 0), 0, x.V, x.Source.GetDisplayName(),
            string.Join(" · ", new[] { x.SourceNote, x.Region }.Where(t => !string.IsNullOrWhiteSpace(t))), $"/PriceBank/Record/{x.Id}")));

        return list.Where(o => In(o.ItemId)).OrderByDescending(o => o.Date).ToList();
    }
}

// =====================================================================
//  BẢNG GIÁ TIÊU CHUẨN
// =====================================================================
public record StandardPrice(int ItemId, decimal Price, decimal Ceiling, string ListNumber, int ListId);

public static class StandardPrices
{
    /// <summary>Giá tiêu chuẩn đang hiệu lực tại ngày <paramref name="at"/>: bảng giá đã duyệt mới nhất (theo ngày hiệu lực) có mặt hàng</summary>
    public static async Task<Dictionary<int, StandardPrice>> CurrentAsync(EProcDbContext db, ICollection<int>? itemIds, DateTime at)
    {
        var d = at.Date;
        var rows = await db.StandardPriceLines.AsNoTracking()
            .Where(l => l.List!.Status == StandardPriceStatus.Approved && l.List.EffectiveFrom <= d && (l.List.EffectiveTo == null || l.List.EffectiveTo >= d)
                        && (itemIds == null || itemIds.Contains(l.ItemId)))
            .Select(l => new { l.ItemId, l.StandardPrice, l.CeilingPrice, l.List!.Number, l.ListId, l.List.EffectiveFrom, l.List.DecidedAt }).ToListAsync();
        return rows.GroupBy(r => r.ItemId).ToDictionary(g => g.Key, g =>
        {
            var x = g.OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.DecidedAt).First();
            return new StandardPrice(x.ItemId, x.StandardPrice, x.CeilingPrice, x.Number, x.ListId);
        });
    }

    /// <summary>Đồng bộ giá tham khảo của mặt hàng theo giá tiêu chuẩn đang hiệu lực (bảng giá có ngày hiệu lực trong tương lai tới hạn)</summary>
    public static async Task<int> SyncReferenceAsync(EProcDbContext db)
    {
        var current = await CurrentAsync(db, null, DateTime.Today);
        if (current.Count == 0) return 0;
        var ids = current.Keys.ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id)).ToListAsync();
        var n = 0;
        foreach (var i in items.Where(i => i.ReferencePrice != current[i.Id].Price)) { i.ReferencePrice = current[i.Id].Price; n++; }
        if (n > 0) await db.SaveChangesAsync();
        return n;
    }

    /// <summary>Giá trần mặc định từ giá tiêu chuẩn</summary>
    public static decimal DefaultCeiling(decimal standard, decimal percent) => Math.Ceiling(standard * (1 + percent / 100m) / 1000m) * 1000m;

    /// <summary>Giá đề xuất cho bảng giá: trung vị đơn giá thực tế (nếu có), ngược lại giá tham khảo hiện tại</summary>
    public static decimal Suggest(PriceStats? stats, decimal reference) => stats != null ? Math.Round(stats.Median / 1000m, 0) * 1000m : reference;

    /// <summary>Các dòng vượt giá trần</summary>
    public static List<(string Description, decimal Price, StandardPrice Std)> Exceeding(IEnumerable<(int? ItemId, string Description, decimal Price)> lines, IReadOnlyDictionary<int, StandardPrice> current) =>
        lines.Where(l => l.ItemId.HasValue && current.TryGetValue(l.ItemId.Value, out var s) && l.Price > s.Ceiling)
            .Select(l => (l.Description, l.Price, current[l.ItemId!.Value])).ToList();
}

// =====================================================================
//  ĐÁNH GIÁ, XẾP LOẠI NHÀ CUNG CẤP
// =====================================================================
public class SupplierScore
{
    public Vendor Vendor { get; set; } = null!;
    public int Orders { get; set; }
    public decimal Value { get; set; }
    public int Received { get; set; }
    public decimal? OnTimePercent { get; set; }
    public decimal? RejectPercent { get; set; }
    public int Evaluations { get; set; }
    public decimal? Quality { get; set; }
    public decimal? Delivery { get; set; }
    public decimal? Price { get; set; }
    public decimal? Service { get; set; }
    /// <summary>Điểm tổng hợp 1–5: trung bình phiếu đánh giá; chưa có phiếu thì suy từ số liệu giao hàng</summary>
    public decimal? Score { get; set; }
    /// <summary>Ký hiệu loại theo thang của danh mục "Xếp loại nhà cung cấp" ("—" = chưa đủ dữ liệu)</summary>
    public string Grade { get; set; } = "—";
    public string GradeLabel { get; set; } = "Chưa đủ dữ liệu";
    public DateTime? LastPeriodic { get; set; }
}

/// <summary>Một bậc xếp loại: đạt khi điểm ≥ Min</summary>
public record GradeLevel(string Code, string Label, decimal Min);

public static class SupplierScorecard
{
    /// <summary>Thang mặc định: A ≥ 4,5 · B ≥ 3,5 · C ≥ 2,5 · D còn lại</summary>
    public static readonly GradeLevel[] DefaultLevels =
    {
        new("A", "Tốt – ưu tiên mời", 4.5m), new("B", "Khá – duy trì", 3.5m), new("C", "Trung bình – cần cải thiện", 2.5m), new("D", "Yếu – xem xét tạm ngưng", 0m)
    };

    public static string Grade(decimal? score) => Grade(score, DefaultLevels);

    /// <summary>Bậc cao nhất có điểm tối thiểu ≤ điểm; thấp hơn mọi bậc → bậc thấp nhất; null → "—"</summary>
    public static string Grade(decimal? score, IReadOnlyCollection<GradeLevel> levels)
    {
        if (score == null || levels.Count == 0) return "—";
        return (levels.OrderByDescending(l => l.Min).FirstOrDefault(l => score >= l.Min) ?? levels.OrderBy(l => l.Min).First()).Code;
    }

    public static string GradeText(string grade) => GradeText(grade, DefaultLevels);
    public static string GradeText(string grade, IReadOnlyCollection<GradeLevel> levels) =>
        levels.FirstOrDefault(l => string.Equals(l.Code, grade, StringComparison.OrdinalIgnoreCase))?.Label ?? "Chưa đủ dữ liệu";

    /// <summary>
    /// Thang xếp loại từ danh mục "Xếp loại nhà cung cấp" (Mã = ký hiệu, Tên = khuyến nghị, Ghi chú = điểm tối thiểu).
    /// Danh mục trống / thiếu mã / điểm không đọc được → thang mặc định.
    /// </summary>
    public static async Task<List<GradeLevel>> LevelsAsync(EProcDbContext db)
    {
        var entries = await db.CatalogEntries.AsNoTracking().Where(e => e.Type == CatalogType.SupplierGrade && e.IsActive).ToListAsync();
        var levels = new List<GradeLevel>();
        foreach (var e in entries)
        {
            var note = e.Note?.Trim().Replace(',', '.');
            if (string.IsNullOrWhiteSpace(e.Code) || !decimal.TryParse(note, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var min)) continue;
            levels.Add(new GradeLevel(e.Code.Trim(), e.Name, min));
        }
        return levels.Count > 0 ? levels.OrderByDescending(l => l.Min).ToList() : DefaultLevels.ToList();
    }

    /// <summary>Điểm tiến độ gợi ý (1–5) theo % giao đúng hạn</summary>
    public static int DeliveryScore(decimal onTimePercent) => onTimePercent switch { >= 95 => 5, >= 85 => 4, >= 70 => 3, >= 50 => 2, _ => 1 };

    /// <summary>Điểm chất lượng gợi ý (1–5) theo % hàng không đạt</summary>
    public static int QualityScore(decimal rejectPercent) => rejectPercent switch { <= 0 => 5, <= 2 => 4, <= 5 => 3, <= 10 => 2, _ => 1 };

    public static async Task<List<SupplierScore>> BuildAsync(EProcDbContext db, DateTime from, DateTime to, int? vendorId = null)
    {
        var end = to.Date.AddDays(1);
        var vendors = await db.Vendors.AsNoTracking().Where(v => vendorId == null || v.Id == vendorId).ToListAsync();
        var pos = await db.PurchaseOrders.AsNoTracking().Where(p => p.VendorId != null && p.Status >= PoStatus.Approved && p.Status != PoStatus.Cancelled
                && p.OrderDate >= from && p.OrderDate < end && (vendorId == null || p.VendorId == vendorId))
            .Select(p => new { p.Id, VendorId = p.VendorId!.Value, p.SubTotal, p.DeliveryDate, p.Status }).ToListAsync();
        var poIds = pos.Select(p => p.Id).ToList();
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(g => poIds.Contains(g.PurchaseOrderId))
            .Select(g => new { g.PurchaseOrderId, g.ReceiptDate, Acc = g.Lines.Sum(l => l.QuantityAccepted), Rej = g.Lines.Sum(l => l.QuantityRejected) }).ToListAsync();
        var evals = await db.VendorEvaluations.AsNoTracking().Where(e => e.CreatedAt >= from && e.CreatedAt < end && (vendorId == null || e.VendorId == vendorId)).ToListAsync();

        var crit = await SupplierCriteriaLoader.LoadAsync(db); // trọng số tiêu chí (Thiết lập → Tiêu chí đánh giá NCC)
        var result = new List<SupplierScore>();
        foreach (var v in vendors)
        {
            var vp = pos.Where(p => p.VendorId == v.Id).ToList();
            var ve = evals.Where(e => e.VendorId == v.Id).ToList();
            if (vp.Count == 0 && ve.Count == 0) continue;
            var done = vp.Where(p => p.Status == PoStatus.Received).ToList();
            var onTime = done.Count(p => receipts.Where(r => r.PurchaseOrderId == p.Id).Select(r => r.ReceiptDate).DefaultIfEmpty(DateTime.MaxValue).Max() <= p.DeliveryDate);
            var rc = receipts.Where(r => vp.Any(p => p.Id == r.PurchaseOrderId)).ToList();
            var qtyTotal = rc.Sum(r => r.Acc + r.Rej);
            var s = new SupplierScore
            {
                Vendor = v, Orders = vp.Count, Value = vp.Sum(p => p.SubTotal), Received = done.Count,
                OnTimePercent = done.Count == 0 ? null : Math.Round(onTime * 100m / done.Count, 1),
                RejectPercent = qtyTotal == 0 ? null : Math.Round(rc.Sum(r => r.Rej) * 100m / qtyTotal, 1),
                Evaluations = ve.Count, LastPeriodic = ve.Where(e => e.Period != null).Select(e => (DateTime?)e.CreatedAt).Max()
            };
            if (ve.Count > 0)
            {
                s.Quality = Math.Round((decimal)ve.Average(e => e.Quality), 2); s.Delivery = Math.Round((decimal)ve.Average(e => e.Delivery), 2);
                s.Price = Math.Round((decimal)ve.Average(e => e.Price), 2); s.Service = Math.Round((decimal)ve.Average(e => e.Service), 2);
                s.Score = Math.Round(crit.Score(s.Quality.Value, s.Delivery.Value, s.Price.Value, s.Service.Value), 2);
            }
            else if (s.OnTimePercent.HasValue)
                s.Score = Math.Round((DeliveryScore(s.OnTimePercent.Value) + QualityScore(s.RejectPercent ?? 0)) / 2m, 2);
            result.Add(s);
        }
        var levels = await LevelsAsync(db);
        foreach (var s in result)
        {
            s.Grade = Grade(s.Score, levels);
            s.GradeLabel = GradeText(s.Grade, levels);
        }
        return result.OrderByDescending(x => x.Score ?? -1).ThenByDescending(x => x.Value).ToList();
    }
}

// =====================================================================
//  SƠ TUYỂN NHÀ CUNG CẤP
// =====================================================================
public static class PrequalificationRules
{
    /// <summary>
    /// Kết quả một hồ sơ: không đạt nếu có tiêu chí bắt buộc không đạt; ngược lại tổng điểm các tiêu chí chấm điểm quy đổi thang 100
    /// so với điểm tối thiểu (không có tiêu chí chấm điểm → đạt khi mọi tiêu chí bắt buộc đạt). Null nếu chưa chấm đủ.
    /// </summary>
    public static (bool Passed, decimal Total)? Evaluate(Prequalification p, IReadOnlyCollection<PrequalificationScore> scores)
    {
        var byCrit = scores.ToDictionary(s => s.CriterionId);
        foreach (var c in p.Criteria)
        {
            if (!byCrit.TryGetValue(c.Id, out var s)) return null;
            if (c.IsMandatory && s.Pass == null) return null;
            if (!c.IsMandatory && c.MaxScore > 0 && s.Score == null) return null;
        }
        var mandatoryOk = p.Criteria.Where(c => c.IsMandatory).All(c => byCrit[c.Id].Pass == true);
        var scored = p.Criteria.Where(c => !c.IsMandatory && c.MaxScore > 0).ToList();
        var max = scored.Sum(c => c.MaxScore);
        var total = max == 0 ? 100m : Math.Round(scored.Sum(c => Math.Min(byCrit[c.Id].Score ?? 0, c.MaxScore)) * 100m / max, 2);
        return (mandatoryOk && total >= p.MinScore, total);
    }

    /// <summary>NCC còn hiệu lực sơ tuyển đạt cho nhóm hàng (đợt đã được Giám đốc duyệt)</summary>
    public static IQueryable<int> PrequalifiedVendorIds(EProcDbContext db, int? categoryId, DateTime at) =>
        db.PrequalificationApplications.Where(a => a.Status == PrequalificationResult.Passed && a.Prequalification!.Status == PrequalificationStatus.Completed
                && (a.ValidUntil == null || a.ValidUntil >= at) && (categoryId == null || a.Prequalification.CategoryId == categoryId))
            .Select(a => a.VendorId);
}
