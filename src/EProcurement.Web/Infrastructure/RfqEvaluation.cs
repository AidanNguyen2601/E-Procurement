using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Kết quả đánh giá một báo giá</summary>
public class QuoteScore
{
    public RfqVendor Quote { get; set; } = null!;
    /// <summary>Chào đủ tất cả các dòng</summary>
    public bool Complete { get; set; }
    /// <summary>Hợp lệ = đủ dòng + đạt kỹ thuật → được xếp hạng</summary>
    public bool Valid => Complete && Quote.TechnicalPass == true;
    public decimal PriceScore { get; set; }
    public decimal TechnicalScore { get; set; }
    public decimal DeliveryScore { get; set; }
    public decimal Total { get; set; }
    /// <summary>Hạng (1 = tốt nhất); null nếu không hợp lệ</summary>
    public int? Rank { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Đánh giá báo giá chào hàng cạnh tranh. So sánh theo giá sau VAT.
/// - Giá thấp nhất: xếp hạng theo giá trong các báo giá hợp lệ.
/// - Chấm điểm: điểm giá = giá thấp nhất / giá chào × 100; điểm tiến độ = (ngày giao nhanh nhất + 1) / (ngày giao + 1) × 100;
///   điểm kỹ thuật do tổ đánh giá chấm (0–100); tổng = Σ điểm × trọng số.
/// </summary>
public static class RfqEvaluation
{
    public static List<QuoteScore> Evaluate(Rfq r)
    {
        var lineCount = r.Lines.Count;
        var scores = r.Vendors.Where(v => v.Responded).Select(v =>
        {
            var s = new QuoteScore { Quote = v, Complete = lineCount > 0 && v.QuoteLines.Count(q => q.UnitPrice > 0) == lineCount };
            if (!s.Complete) s.Issues.Add("Không chào đủ các dòng hàng");
            if (v.TechnicalPass == false) s.Issues.Add("Không đạt kỹ thuật" + (string.IsNullOrEmpty(v.TechnicalNote) ? "" : ": " + v.TechnicalNote));
            if (v.TechnicalPass == null) s.Issues.Add("Chưa đánh giá kỹ thuật");
            return s;
        }).ToList();

        var valid = scores.Where(s => s.Valid).ToList();
        if (valid.Count > 0)
        {
            var minPrice = valid.Min(s => s.Quote.GrandTotal);
            var minLead = valid.Min(s => s.Quote.LeadTimeDays ?? int.MaxValue);
            foreach (var s in valid)
            {
                s.PriceScore = s.Quote.GrandTotal <= 0 ? 0 : Math.Round(minPrice * 100m / s.Quote.GrandTotal, 2);
                s.TechnicalScore = s.Quote.TechnicalScore ?? 0;
                s.DeliveryScore = s.Quote.LeadTimeDays is int lead && minLead != int.MaxValue ? Math.Round((minLead + 1) * 100m / (lead + 1), 2) : 0;
                s.Total = r.EvaluationMethod == EvaluationMethod.WeightedScore
                    ? Math.Round((s.PriceScore * r.PriceWeight + s.TechnicalScore * r.TechnicalWeight + s.DeliveryScore * r.DeliveryWeight) / 100m, 2)
                    : s.PriceScore;
            }
            var ordered = r.EvaluationMethod == EvaluationMethod.WeightedScore
                ? valid.OrderByDescending(s => s.Total).ThenBy(s => s.Quote.GrandTotal)
                : valid.OrderBy(s => s.Quote.GrandTotal).ThenByDescending(s => s.Quote.Vendor?.Rating ?? 0);
            var rank = 1;
            foreach (var s in ordered) s.Rank = rank++;
        }
        return scores.OrderBy(s => s.Rank ?? int.MaxValue).ThenBy(s => s.Quote.GrandTotal).ToList();
    }

    /// <summary>Báo giá đã nhận nhưng chưa đánh giá kỹ thuật</summary>
    public static bool AllTechnicallyEvaluated(Rfq r) => r.Vendors.Where(v => v.Responded).All(v => v.TechnicalPass.HasValue);

    /// <summary>Được mở báo giá: hết hạn nộp, hoặc tất cả NCC đã gửi báo giá</summary>
    public static bool CanOpen(Rfq r) => r.Status == RfqStatus.Open && r.Vendors.Any(v => v.Responded)
                                         && (r.Deadline < DateTime.Today || r.Vendors.All(v => v.Responded));

    /// <summary>Giá chào đã được công khai cho người xem (đã mở báo giá)</summary>
    public static bool IsUnsealed(Rfq r) => r.Status is RfqStatus.Opened or RfqStatus.PendingApproval or RfqStatus.Awarded
                                            || (r.Status == RfqStatus.Cancelled && r.OpenedAt.HasValue);
}
