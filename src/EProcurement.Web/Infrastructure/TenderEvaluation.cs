using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Kết quả đánh giá một hồ sơ dự thầu</summary>
public class BidResult
{
    public TenderBidder Bid { get; set; } = null!;
    /// <summary>Đạt bước hợp lệ + năng lực → được chấm kỹ thuật</summary>
    public bool Responsive => Bid.Valid == true && Bid.Qualified == true;
    public int ScoreCount { get; set; }
    public decimal? TechnicalAverage { get; set; }
    /// <summary>Đủ điểm của mọi thành viên và đạt điểm kỹ thuật tối thiểu</summary>
    public bool TechnicalPass { get; set; }
    public decimal PriceScore { get; set; }
    public decimal Total { get; set; }
    public int? Rank { get; set; }
    public List<string> Issues { get; set; } = new();
}

/// <summary>
/// Đánh giá HSDT theo từng bước: hợp lệ → năng lực, kinh nghiệm → kỹ thuật (điểm trung bình tổ chuyên gia ≥ tối thiểu)
/// → giá đánh giá (sau giảm giá, gồm VAT) → xếp hạng.
/// </summary>
public static class TenderEvaluation
{
    public static List<BidResult> Evaluate(Tender t)
    {
        var committee = t.Committee.Count;
        var results = t.Bidders.Where(b => b.Submitted).Select(b =>
        {
            var r = new BidResult { Bid = b, ScoreCount = b.Scores.Count };
            if (b.Valid == null) r.Issues.Add(I18n.T("Chưa đánh giá tính hợp lệ"));
            else if (b.Valid == false) r.Issues.Add(I18n.T("Không hợp lệ") + (string.IsNullOrEmpty(b.ValidNote) ? "" : ": " + b.ValidNote));
            if (b.Valid == true && b.Qualified == null) r.Issues.Add(I18n.T("Chưa đánh giá năng lực"));
            else if (b.Qualified == false) r.Issues.Add(I18n.T("Không đạt năng lực") + (string.IsNullOrEmpty(b.QualifiedNote) ? "" : ": " + b.QualifiedNote));
            if (r.Responsive)
            {
                if (r.ScoreCount > 0) r.TechnicalAverage = Math.Round((decimal)b.Scores.Average(s => s.TechnicalScore), 2);
                if (r.ScoreCount < committee) r.Issues.Add(I18n.T("Mới có {0}/{1} thành viên chấm kỹ thuật", r.ScoreCount, committee));
                else if (r.TechnicalAverage < t.MinTechnicalScore) r.Issues.Add(I18n.T("Điểm kỹ thuật {0:0.##} < tối thiểu {1}", r.TechnicalAverage, t.MinTechnicalScore));
                r.TechnicalPass = committee > 0 && r.ScoreCount >= committee && r.TechnicalAverage >= t.MinTechnicalScore;
            }
            return r;
        }).ToList();

        var passed = results.Where(r => r.TechnicalPass).ToList();
        if (passed.Count > 0)
        {
            var min = passed.Min(r => r.Bid.EvaluatedPrice);
            foreach (var r in passed)
            {
                r.PriceScore = r.Bid.EvaluatedPrice <= 0 ? 0 : Math.Round(min * 100m / r.Bid.EvaluatedPrice, 2);
                r.Total = t.EvaluationMethod == TenderEvaluationMethod.Combined
                    ? Math.Round((r.TechnicalAverage!.Value * t.TechnicalWeight + r.PriceScore * t.PriceWeight) / 100m, 2)
                    : r.PriceScore;
            }
            var ordered = t.EvaluationMethod == TenderEvaluationMethod.Combined
                ? passed.OrderByDescending(r => r.Total).ThenBy(r => r.Bid.EvaluatedPrice)
                : passed.OrderBy(r => r.Bid.EvaluatedPrice).ThenByDescending(r => r.TechnicalAverage);
            var rank = 1;
            foreach (var r in ordered) r.Rank = rank++;
        }
        return results.OrderBy(r => r.Rank ?? int.MaxValue).ThenBy(r => r.Bid.Vendor?.Name).ToList();
    }

    /// <summary>Mọi thành viên đã hoàn thành chấm kỹ thuật (và đã có HSDT đạt bước hợp lệ + năng lực)</summary>
    public static bool ScoringComplete(Tender t) =>
        t.Committee.Count > 0 && t.Committee.All(m => m.ScoringCompletedAt.HasValue)
        && t.Bidders.Where(b => b.Submitted).All(b => b.Valid.HasValue && (b.Valid == false || b.Qualified.HasValue));

    /// <summary>Giá dự thầu được xem: một túi → sau mở thầu; hai túi → sau mở hồ sơ tài chính</summary>
    public static bool PricesVisible(Tender t) =>
        t.OpenedAt.HasValue && (t.EnvelopeMethod == EnvelopeMethod.OneEnvelope || t.FinancialOpenedAt.HasValue);

    /// <summary>Kiểm tra bảo đảm dự thầu: đủ số tiền, hiệu lực ≥ thời điểm đóng thầu + hiệu lực HSDT + 30 ngày</summary>
    public static List<string> SecurityProblems(Tender t, TenderBidder b)
    {
        var p = new List<string>();
        if (t.BidSecurityAmount > 0)
        {
            if (b.BidSecurityAmount < t.BidSecurityAmount) p.Add($"Bảo đảm dự thầu {b.BidSecurityAmount.ToMoney()} < yêu cầu {t.BidSecurityAmount.ToMoney()} đ");
            var need = t.ClosingAt.Date.AddDays(t.BidValidityDays + 30);
            if (b.BidSecurityExpiry == null || b.BidSecurityExpiry < need) p.Add($"Bảo lãnh phải hiệu lực đến ít nhất {need.ToDate()}");
        }
        if (b.ValidityDays < t.BidValidityDays) p.Add($"Hiệu lực HSDT {b.ValidityDays} ngày < yêu cầu {t.BidValidityDays} ngày");
        return p;
    }

    /// <summary>Kiểm tra HSDT trước khi nhận online: thiếu số bảo lãnh / bảo đảm, hiệu lực HSDT ngắn hơn HSMT yêu cầu…</summary>
    public static List<string> BidProblems(Tender t, decimal bidSecurityAmount, string? bidSecurityRef, DateTime? bidSecurityExpiry, int? validityDays)
    {
        var p = SecurityProblems(t, new TenderBidder { BidSecurityAmount = bidSecurityAmount, BidSecurityExpiry = bidSecurityExpiry, ValidityDays = validityDays ?? 0 });
        if (t.BidSecurityAmount > 0 && string.IsNullOrWhiteSpace(bidSecurityRef)) p.Add("Nhập số bảo lãnh / chứng từ bảo đảm dự thầu");
        return p;
    }

    /// <summary>Hạn nhà thầu gửi đề nghị làm rõ HSMT</summary>
    public static DateTime QuestionDeadline(Tender t, int minDays) => t.ClosingAt.AddDays(-minDays);

    /// <summary>Sửa đổi HSMT đăng tải sau thời điểm nhà thầu nộp HSDT – nhà thầu cần xem lại và nộp lại nếu cần</summary>
    public static List<TenderClarification> AmendmentsAfter(Tender t, DateTime? submittedAt) =>
        submittedAt == null ? new() : t.Clarifications.Where(c => c.IsAmendment && c.CreatedAt > submittedAt).OrderBy(c => c.CreatedAt).ToList();

    /// <summary>Đàm phán thành công còn hiệu lực (căn cứ đề xuất trúng thầu)</summary>
    public static TenderNegotiation? SuccessfulNegotiation(IEnumerable<TenderNegotiation> negotiations) =>
        negotiations.Where(n => !n.IsVoid && n.Success).OrderByDescending(n => n.CreatedAt).FirstOrDefault();

    /// <summary>
    /// Nhà thầu được mời đàm phán tiếp theo: xếp hạng cao nhất trong các HSDT đạt kỹ thuật, chưa đàm phán không thành công.
    /// Null nếu đã có đàm phán thành công hoặc không còn nhà thầu nào.
    /// </summary>
    public static BidResult? NegotiationCandidate(List<BidResult> results, IEnumerable<TenderNegotiation> negotiations)
    {
        var active = negotiations.Where(n => !n.IsVoid).ToList();
        if (active.Any(n => n.Success)) return null;
        var failed = active.Where(n => !n.Success).Select(n => n.TenderBidderId).ToHashSet();
        return results.Where(r => r.Rank.HasValue && !failed.Contains(r.Bid.Id)).OrderBy(r => r.Rank).FirstOrDefault();
    }

    /// <summary>Thẩm định kết quả còn hiệu lực gần nhất</summary>
    public static TenderAppraisal? CurrentAppraisal(IEnumerable<TenderAppraisal> appraisals) =>
        appraisals.Where(a => !a.IsVoid).OrderByDescending(a => a.AssignedAt).FirstOrDefault();

    /// <summary>Lý do Giám đốc chưa được phê duyệt kết quả (thiếu thẩm định đồng ý); null = được duyệt</summary>
    public static string? ApprovalBlocker(Tender t, bool appraisalRequired)
    {
        if (!appraisalRequired) return null;
        var a = CurrentAppraisal(t.Appraisals);
        if (a == null) return "Chưa giao thẩm định kết quả lựa chọn nhà thầu.";
        if (a.CompletedAt == null) return $"Đang chờ {a.Appraiser?.FullName ?? "người thẩm định"} lập báo cáo thẩm định.";
        if (a.Agree != true) return "Báo cáo thẩm định không thống nhất với đề xuất – trả lại bên mời thầu để xem xét.";
        return null;
    }

    /// <summary>Nhà thầu được kiến nghị: đã nộp HSDT, kết quả đã công bố và còn trong thời hạn kiến nghị</summary>
    public static bool CanComplain(Tender t, TenderBidder? b, DateTime now) =>
        b?.Submitted == true && t.Status == TenderStatus.Awarded && t.ComplaintDeadline.HasValue && now <= t.ComplaintDeadline;
}
