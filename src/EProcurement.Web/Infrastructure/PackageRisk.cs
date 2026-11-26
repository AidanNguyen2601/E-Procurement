using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Một yếu tố rủi ro của gói: tự động (từ dữ liệu) hoặc do người lập gói đánh giá</summary>
public record RiskFactor(string Key, string Name, int Weight, bool Hit, string Detail, bool Manual);

public class PackageRiskResult
{
    public List<RiskFactor> Factors { get; } = new();
    public int Score => Factors.Where(f => f.Hit).Sum(f => f.Weight);
    /// <summary>Thấp &lt; 20 ≤ Trung bình &lt; 40 ≤ Cao</summary>
    public string Level => Score >= PackageRisk.HighFrom ? "Cao" : Score >= PackageRisk.MediumFrom ? "Trung bình" : "Thấp";
    public bool IsHigh => Score >= PackageRisk.HighFrom;
}

/// <summary>
/// Chấm điểm rủi ro gói trước khi đưa vào KHLCNT / mời thầu: dấu hiệu né hình thức, chia nhỏ gói, dự toán thiếu căn cứ / lệch thị trường,
/// hình thức kém cạnh tranh, ít nhà cung cấp, cộng các yếu tố người lập gói tự đánh giá. Gói rủi ro cao phải nêu biện pháp kiểm soát trước khi trình.
/// </summary>
public static class PackageRisk
{
    public const int MediumFrom = 20, HighFrom = 40;

    /// <summary>Các yếu tố đánh giá thủ công (khóa, nội dung, trọng số)</summary>
    public static readonly (string Key, string Name, int Weight)[] ManualFactors =
    {
        ("brand", "Yêu cầu kỹ thuật nêu nhãn hiệu / xuất xứ cụ thể hoặc thông số chỉ một hãng đáp ứng", 15),
        ("qual", "Tiêu chí năng lực, kinh nghiệm cao hơn mức cần thiết so với quy mô gói", 10),
        ("market", "Thị trường hẹp – chỉ một vài nhà cung cấp có khả năng đáp ứng", 15),
        ("urgent", "Gói khẩn cấp / thời gian chuẩn bị và thực hiện bị rút ngắn", 10),
        ("scope", "Phạm vi, khối lượng chưa ổn định – dễ phát sinh, điều chỉnh hợp đồng", 10),
        ("conflict", "Có dấu hiệu xung đột lợi ích giữa người tham gia lập / đánh giá hồ sơ và nhà cung cấp", 20),
    };

    public static HashSet<string> ParseChecklist(string? s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dữ liệu đầu vào đã tính sẵn (để tách phần truy vấn khỏi phần chấm điểm, dễ kiểm thử)</summary>
    public record Facts(decimal Budget, ProcurementMethod Method, string? MethodReason, bool HasBoq, int LinesWithItem, int LinesWithPriceData,
        decimal? PriceDeviationPercent, int QualifiedVendors, decimal RelatedBudget, int RelatedPackages, HashSet<string> Manual);

    public static PackageRiskResult Score(Facts f, ProcurementSettings s)
    {
        var r = new PackageRiskResult();
        // 1. Giá gói sát dưới ngưỡng hạn mức hình thức (≥ 90% ngưỡng)
        var thresholds = new[] { (s.DirectAwardMax, I18n.T("hạn mức chỉ định thầu")), (s.CompetitiveQuotationMax, I18n.T("hạn mức chào hàng cạnh tranh")) };
        var near = thresholds.FirstOrDefault(t => t.Item1 > 0 && f.Budget <= t.Item1 && f.Budget >= t.Item1 * 0.9m);
        r.Factors.Add(new("threshold", I18n.T("Giá gói sát ngưỡng hạn mức hình thức"), 15, near.Item1 > 0,
            near.Item1 > 0 ? I18n.T("{0} đ = {1}% {2} ({3} đ) – dấu hiệu điều chỉnh giá để chọn hình thức đơn giản hơn", f.Budget.ToMoney(), Math.Round(f.Budget * 100 / near.Item1, 1), near.Item2, near.Item1.ToMoney()) : I18n.T("Không"), false));
        // 2. Chia nhỏ gói: các gói cùng lĩnh vực, cùng đơn vị trong cửa sổ thời gian cộng dồn vượt ngưỡng của hình thức đang chọn
        var limit = f.Method switch { ProcurementMethod.DirectAward or ProcurementMethod.DirectPurchase => s.DirectAwardMax, ProcurementMethod.CompetitiveQuotation => s.CompetitiveQuotationMax, _ => 0 };
        var split = limit > 0 && f.RelatedPackages > 0 && f.Budget <= limit && f.Budget + f.RelatedBudget > limit;
        r.Factors.Add(new("split", I18n.T("Dấu hiệu chia nhỏ gói"), 20, split,
            f.RelatedPackages == 0 ? I18n.T("Không có gói cùng lĩnh vực, cùng đơn vị trong {0} ngày", s.DirectAwardSplitWindowDays)
                : I18n.T("{0} gói cùng lĩnh vực, cùng đơn vị trong {1} ngày: cộng dồn {2} đ so với ngưỡng {3} đ", f.RelatedPackages, s.DirectAwardSplitWindowDays, (f.Budget + f.RelatedBudget).ToMoney(), limit > 0 ? limit.ToMoney() : "–"), false));
        // 3. Dự toán thiếu căn cứ
        var noBasis = !f.HasBoq && f.LinesWithPriceData == 0;
        r.Factors.Add(new("basis", I18n.T("Dự toán thiếu căn cứ giá"), 10, noBasis,
            f.HasBoq ? I18n.T("Có BOQ") : I18n.T("{0}/{1} dòng hàng có dữ liệu ngân hàng giá 12 tháng", f.LinesWithPriceData, f.LinesWithItem), false));
        // 4. Dự toán lệch giá thị trường
        var dev = f.PriceDeviationPercent;
        r.Factors.Add(new("deviation", I18n.T("Dự toán lệch ngân hàng giá > 20%"), 10, dev.HasValue && Math.Abs(dev.Value) > 20,
            dev.HasValue ? I18n.T("Dự toán so với trung vị ngân hàng giá: {0}{1}%", dev > 0 ? "+" : "", dev.Value.ToString("0.#")) : I18n.T("Không đủ dữ liệu so sánh"), false));
        // 5. Hình thức không cạnh tranh
        var nonComp = f.Method is ProcurementMethod.DirectAward or ProcurementMethod.DirectPurchase;
        r.Factors.Add(new("method", I18n.T("Hình thức không cạnh tranh"), 15, nonComp,
            nonComp ? f.Method.GetDisplayName() + (string.IsNullOrWhiteSpace(f.MethodReason) ? "" : " – " + f.MethodReason) : f.Method.GetDisplayName(), false));
        // 6. Ít nhà cung cấp đủ điều kiện
        r.Factors.Add(new("vendors", I18n.T("Ít nhà cung cấp đủ điều kiện trong nhóm hàng (< 3)"), 10, f.QualifiedVendors is >= 0 and < 3,
            f.QualifiedVendors < 0 ? I18n.T("Dòng hàng chưa gắn mã hàng hóa – không xác định được nhóm hàng") : I18n.T("{0} nhà cung cấp đã duyệt thuộc nhóm hàng của gói", f.QualifiedVendors), false));
        foreach (var (k, n, w) in ManualFactors)
            r.Factors.Add(new(k, I18n.T(n), w, f.Manual.Contains(k), f.Manual.Contains(k) ? I18n.T("Người lập gói xác định có") : I18n.T("Không"), true));
        return r;
    }

    public static async Task<PackageRiskResult> AssessAsync(EProcDbContext db, ProcurementPackage p, ProcurementSettings s)
    {
        var lines = Packages.Lines(p);
        var itemIds = lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).Distinct().ToList();
        var obs = itemIds.Count == 0 ? new List<PriceObservation>() : await PriceBank.ObservationsAsync(db, itemIds, DateTime.Today.AddMonths(-12), DateTime.Today);
        var medians = itemIds.Select(id => PriceStats.From(id, obs.Where(o => o.ItemId == id).ToList())).Where(x => x != null).ToDictionary(x => x!.ItemId, x => x!.Median);
        decimal est = 0, mkt = 0;
        foreach (var l in lines.Where(l => l.ItemId.HasValue && medians.ContainsKey(l.ItemId!.Value) && l.EstimatedPrice > 0))
        { est += l.EstimatedPrice * l.Quantity; mkt += medians[l.ItemId!.Value] * l.Quantity; }
        var cats = itemIds.Count == 0 ? new List<int>() : await db.Items.Where(i => itemIds.Contains(i.Id) && i.CategoryId != null).Select(i => i.CategoryId!.Value).Distinct().ToListAsync();
        var vendors = cats.Count == 0 ? -1 : await db.Vendors.CountAsync(v => v.Status == VendorStatus.Approved && v.Categories.Any(c => cats.Contains(c.CategoryId)));
        var dept = p.PurchaseRequests.Select(r => r.Department).FirstOrDefault();
        var from = p.CreatedAt.AddDays(-s.DirectAwardSplitWindowDays); var to = p.CreatedAt.AddDays(s.DirectAwardSplitWindowDays);
        var related = await db.ProcurementPackages.AsNoTracking()
            .Where(x => x.Id != p.Id && x.Status != PackageStatus.Cancelled && x.Field == p.Field && x.CreatedAt >= from && x.CreatedAt <= to
                        && x.PurchaseRequests.Any(r => r.Department == dept))
            .Select(x => x.Budget).ToListAsync();
        var hasBoq = await db.BoqItems.AnyAsync(b => b.PackageId == p.Id);
        return Score(new Facts(p.Budget, p.Method, p.MethodReason, hasBoq, lines.Count(l => l.ItemId.HasValue), lines.Count(l => l.ItemId.HasValue && medians.ContainsKey(l.ItemId!.Value)),
            mkt > 0 ? Math.Round((est - mkt) * 100 / mkt, 1) : null, vendors, related.Sum(), related.Count, ParseChecklist(p.RiskChecklist)), s);
    }
}
