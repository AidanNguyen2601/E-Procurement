using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Nhóm gói thầu theo luật để áp mốc thời gian: hàng hóa / phi tư vấn, xây lắp / hỗn hợp, tư vấn</summary>
public enum LegalWorkKind { Goods = 1, Construction = 2, Consulting = 3 }

/// <summary>
/// Mốc thời gian tối thiểu theo luật trong lựa chọn nhà thầu. Mặc định theo Điều 45 Luật Đấu thầu số 22/2023/QH15;
/// lưu ở cấu hình (Thiết lập › Mốc thời gian theo luật) để cập nhật khi văn bản được sửa đổi mà không phải sửa phần mềm.
/// </summary>
public class LegalTimelineSettings
{
    /// <summary>true: chặn trình / phát hành / sửa đổi khi vi phạm; false: chỉ cảnh báo</summary>
    public bool Enforce { get; set; } = true;
    public string LegalBasis { get; set; } = "Điều 45 Luật Đấu thầu số 22/2023/QH15";
    /// <summary>Đấu thầu rộng rãi / hạn chế: thời gian chuẩn bị HSDT tối thiểu (ngày), tính từ ngày đầu phát hành HSMT đến ngày đóng thầu</summary>
    public int OpenDomesticDays { get; set; } = 18;
    public int OpenInternationalDays { get; set; } = 35;
    /// <summary>Gói quy mô nhỏ (xây lắp / hỗn hợp ≤ ngưỡng; hàng hóa / phi tư vấn ≤ ngưỡng)</summary>
    public int SmallDomesticDays { get; set; } = 9;
    public int SmallInternationalDays { get; set; } = 18;
    public decimal SmallConstructionMax { get; set; } = 20_000_000_000;
    public decimal SmallGoodsMax { get; set; } = 10_000_000_000;
    /// <summary>Chào hàng cạnh tranh: tối thiểu N ngày làm việc từ ngày phát hành đến hạn nộp</summary>
    public int CompetitiveQuotationWorkingDays { get; set; } = 5;
    /// <summary>Sửa đổi HSMT phải gửi trước ngày đóng thầu tối thiểu N ngày (gói nhỏ: N ngày làm việc), nếu không phải gia hạn đóng thầu</summary>
    public int AmendmentDays { get; set; } = 10;
    public int AmendmentSmallWorkingDays { get; set; } = 3;
    /// <summary>Từ khóa nhận biết lĩnh vực của gói (danh mục Lĩnh vực) thuộc nhóm xây lắp / tư vấn; còn lại là hàng hóa – phi tư vấn</summary>
    public string ConstructionKeywords { get; set; } = "xây lắp, xây dựng, thi công, hỗn hợp, EPC";
    public string ConsultingKeywords { get; set; } = "tư vấn";
    /// <summary>Thời hạn lưu trữ hồ sơ lựa chọn nhà thầu / hợp đồng (năm, tính từ khi đóng hồ sơ) – kiểm tra theo quy định hiện hành</summary>
    public int ArchiveYears { get; set; } = 10;

    public List<string> Validate()
    {
        var e = new List<string>();
        foreach (var (v, n) in new[] { (OpenDomesticDays, "đấu thầu trong nước"), (OpenInternationalDays, "đấu thầu quốc tế"), (SmallDomesticDays, "gói nhỏ trong nước"),
                     (SmallInternationalDays, "gói nhỏ quốc tế"), (CompetitiveQuotationWorkingDays, "chào hàng cạnh tranh"), (AmendmentDays, "sửa đổi HSMT"), (AmendmentSmallWorkingDays, "sửa đổi HSMT gói nhỏ") })
            if (v is < 0 or > 365) e.Add(I18n.T("Số ngày {0} phải từ 0 đến 365.", I18n.T(n)));
        if (ArchiveYears is < 1 or > 100) e.Add(I18n.T("Thời hạn lưu trữ phải từ 1 đến 100 năm."));
        if (SmallConstructionMax < 0 || SmallGoodsMax < 0) e.Add(I18n.T("Ngưỡng gói quy mô nhỏ không được âm."));
        return e;
    }
}

/// <summary>Một yêu cầu thời gian và kết quả kiểm tra</summary>
public record LegalCheck(string Rule, bool? Ok, string Detail);

/// <summary>Yêu cầu tối thiểu: N ngày (hoặc N ngày làm việc) cùng căn cứ</summary>
public record LegalMin(int Days, bool WorkingDays, string Basis)
{
    public string Text => WorkingDays ? I18n.T("{0} ngày làm việc", Days) : I18n.T("{0} ngày", Days);
    /// <summary>Ngày sớm nhất được phép (tính theo ngày lịch từ <paramref name="from"/>)</summary>
    public DateTime EarliestFrom(DateTime from) => WorkingDays ? LegalTimelines.AddWorkingDays(from.Date, Days) : from.Date.AddDays(Days);
    /// <summary>Ngày muộn nhất trước <paramref name="deadline"/> (ví dụ: hạn gửi sửa đổi HSMT)</summary>
    public DateTime LatestBefore(DateTime deadline) => WorkingDays ? LegalTimelines.AddWorkingDays(deadline.Date, -Days) : deadline.Date.AddDays(-Days);
}

public static class LegalTimelines
{
    static IEnumerable<string> Words(string? s) => (s ?? "").Split(',', ';').Select(x => x.Trim()).Where(x => x.Length > 0);

    /// <summary>Nhóm gói theo lĩnh vực: "phi tư vấn" luôn thuộc hàng hóa – phi tư vấn</summary>
    public static LegalWorkKind KindOf(string? field, LegalTimelineSettings s)
    {
        var f = (field ?? "").ToLowerInvariant();
        if (f.Length == 0) return LegalWorkKind.Goods;
        if (Words(s.ConstructionKeywords).Any(k => f.Contains(k.ToLowerInvariant()))) return LegalWorkKind.Construction;
        if (f.Contains("phi tư vấn")) return LegalWorkKind.Goods;
        if (Words(s.ConsultingKeywords).Any(k => f.Contains(k.ToLowerInvariant()))) return LegalWorkKind.Consulting;
        return LegalWorkKind.Goods;
    }

    public static bool IsSmall(LegalWorkKind kind, decimal budget, LegalTimelineSettings s) => kind switch
    {
        LegalWorkKind.Construction => budget > 0 && budget <= s.SmallConstructionMax,
        LegalWorkKind.Goods => budget > 0 && budget <= s.SmallGoodsMax,
        _ => false
    };

    static string SmallText(LegalWorkKind kind, LegalTimelineSettings s) => kind == LegalWorkKind.Construction
        ? I18n.T("xây lắp / hỗn hợp ≤ {0}", s.SmallConstructionMax.ToShortMoney()) : I18n.T("hàng hóa / phi tư vấn ≤ {0}", s.SmallGoodsMax.ToShortMoney());

    /// <summary>Thời gian chuẩn bị HSDT tối thiểu của đấu thầu rộng rãi</summary>
    public static LegalMin MinBidding(decimal budget, string? field, bool international, LegalTimelineSettings s)
    {
        var kind = KindOf(field, s);
        var scope = international ? I18n.T("quốc tế") : I18n.T("trong nước");
        if (IsSmall(kind, budget, s))
            return new(international ? s.SmallInternationalDays : s.SmallDomesticDays, false, $"{s.LegalBasis} – {scope}, {SmallText(kind, s)}");
        return new(international ? s.OpenInternationalDays : s.OpenDomesticDays, false, $"{s.LegalBasis} – {scope}");
    }

    /// <summary>Sửa đổi HSMT phải trước ngày đóng thầu tối thiểu</summary>
    public static LegalMin MinAmendment(decimal budget, string? field, LegalTimelineSettings s)
    {
        var kind = KindOf(field, s);
        return IsSmall(kind, budget, s)
            ? new(s.AmendmentSmallWorkingDays, true, $"{s.LegalBasis} – {SmallText(kind, s)}")
            : new(s.AmendmentDays, false, s.LegalBasis);
    }

    public static LegalMin MinQuotation(LegalTimelineSettings s) => new(s.CompetitiveQuotationWorkingDays, true, s.LegalBasis + " – " + I18n.T("chào hàng cạnh tranh"));

    /// <summary>Cộng / trừ ngày làm việc (bỏ thứ Bảy, Chủ nhật; ngày lễ do người dùng tự cân nhắc)</summary>
    public static DateTime AddWorkingDays(DateTime d, int n)
    {
        var step = n >= 0 ? 1 : -1; var left = Math.Abs(n);
        while (left > 0) { d = d.AddDays(step); if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) left--; }
        return d;
    }

    /// <summary>Số ngày giữa hai ngày (ngày lịch hoặc ngày làm việc)</summary>
    public static int Between(DateTime from, DateTime to, bool working)
    {
        from = from.Date; to = to.Date;
        if (!working) return (to - from).Days;
        var n = 0; var sign = to >= from ? 1 : -1;
        for (var d = from; d != to; d = d.AddDays(sign)) { var nx = d.AddDays(sign); if (nx.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) n += sign; }
        return n;
    }

    /// <summary>
    /// Kiểm tra mốc thời gian của gói thầu. Trước khi phát hành, ngày phát hành dự kiến = <paramref name="issueDate"/> (mặc định hôm nay).
    /// </summary>
    public static List<LegalCheck> CheckTender(Tender t, string? field, IEnumerable<TenderClarification> clarifications, LegalTimelineSettings s, DateTime now, DateTime? issueDate = null)
    {
        var res = new List<LegalCheck>();
        var issued = t.PublishedAt ?? issueDate ?? now;
        var bid = MinBidding(t.PackageBudget, field, t.IsInternational, s);
        var have = Between(issued, t.ClosingAt, false);
        var earliest = bid.EarliestFrom(issued);
        res.Add(new(I18n.T("Thời gian chuẩn bị HSDT tối thiểu {0}", bid.Text), have >= bid.Days,
            I18n.T("{0} → đóng thầu {1}: {2} ngày", t.PublishedAt.HasValue ? I18n.T("Phát hành {0}", issued.ToDate()) : I18n.T("Nếu phát hành {0}", issued.ToDate()), t.ClosingAt.ToDateTime(), have)
            + (have < bid.Days ? " · " + I18n.T("đóng thầu sớm nhất {0}", earliest.ToDate()) : "") + " · " + bid.Basis));
        var am = MinAmendment(t.PackageBudget, field, s);
        var amends = clarifications.Where(c => c.IsAmendment).OrderBy(c => c.CreatedAt).ToList();
        if (amends.Count == 0)
            res.Add(new(I18n.T("Sửa đổi HSMT trước đóng thầu tối thiểu {0}", am.Text), null,
                I18n.T("Chưa có sửa đổi. Hạn cuối gửi sửa đổi mà không phải gia hạn: {0}", am.LatestBefore(t.ClosingAt).ToDate())));
        foreach (var a in amends)
        {
            var gap = Between(a.CreatedAt, t.ClosingAt, am.WorkingDays);
            res.Add(new(I18n.T("Sửa đổi HSMT ngày {0}", a.CreatedAt.ToDate()), gap >= am.Days,
                I18n.T("cách đóng thầu {0} {1} (yêu cầu ≥ {2})", gap, am.WorkingDays ? I18n.T("ngày làm việc") : I18n.T("ngày"), am.Text)));
        }
        if (t.BidValidityDays > 0)
            res.Add(new(I18n.T("Hiệu lực HSDT {0} ngày", t.BidValidityDays), null, I18n.T("đến {0} – kết quả và ký hợp đồng cần xong trước ngày này (hoặc yêu cầu gia hạn hiệu lực HSDT).", t.ClosingAt.Date.AddDays(t.BidValidityDays).ToDate())));
        return res;
    }
}
