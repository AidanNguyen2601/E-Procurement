using System.Text.Json;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Kết quả kiểm tra một yêu cầu năng lực theo kê khai: true đạt, false không đạt, null cần tổ chuyên gia đánh giá</summary>
public record QualificationResult(TenderRequirement Requirement, bool? Pass, string Detail);

/// <summary>Liên danh / thầu phụ và năng lực, kinh nghiệm của nhà thầu theo kê khai trong HSDT</summary>
public static class BidderProfile
{
    public static bool IsJointVenture(TenderBidder b) => b.Partners.Any(p => p.Role is BidPartnerRole.Member);

    /// <summary>Kiểm tra cơ cấu liên danh / thầu phụ theo điều kiện của HSMT</summary>
    public static List<string> PartnerProblems(Tender t, TenderBidder b, ICollection<int> bannedVendorIds)
    {
        var p = new List<string>();
        var members = b.Partners.Where(x => x.Role is BidPartnerRole.Lead or BidPartnerRole.Member).ToList();
        var subs = b.Partners.Where(x => x.Role is BidPartnerRole.Subcontractor or BidPartnerRole.SpecialSubcontractor).ToList();
        if (members.Any(m => m.Role == BidPartnerRole.Member))
        {
            if (!t.AllowJointVenture) p.Add(I18n.T("HSMT không cho phép liên danh."));
            var leads = members.Where(m => m.Role == BidPartnerRole.Lead).ToList();
            if (leads.Count != 1) p.Add(I18n.T("Liên danh phải có đúng một thành viên đứng đầu (là nhà thầu nộp HSDT)."));
            var total = members.Sum(m => m.SharePercent);
            if (total != 100) p.Add(I18n.T("Tổng tỷ lệ công việc của các thành viên liên danh là {0}% (phải bằng 100%).", total.ToString("0.##")));
            if (leads.Count == 1 && members.Any(m => m.Role == BidPartnerRole.Member && m.SharePercent > leads[0].SharePercent))
                p.Add(I18n.T("Thành viên đứng đầu đảm nhận tỷ lệ công việc thấp hơn thành viên khác – kiểm tra thỏa thuận liên danh."));
        }
        else if (members.Count > 0) p.Add(I18n.T("Có thành viên đứng đầu nhưng không có thành viên liên danh."));
        if (t.MaxSubcontractPercent.HasValue)
        {
            var sub = subs.Where(s => s.Role == BidPartnerRole.Subcontractor).Sum(s => s.SharePercent);
            if (sub > t.MaxSubcontractPercent) p.Add(I18n.T("Thầu phụ {0}% vượt mức tối đa {1}% của HSMT.", sub.ToString("0.##"), t.MaxSubcontractPercent.Value.ToString("0.##")));
        }
        foreach (var x in b.Partners.Where(x => x.VendorId.HasValue && bannedVendorIds.Contains(x.VendorId.Value)))
            p.Add(I18n.T("{0} đang bị tạm ngưng / trong danh sách đen.", x.Name));
        if (b.Partners.Any(x => x.VendorId.HasValue && x.VendorId == b.VendorId && x.Role != BidPartnerRole.Lead))
            p.Add(I18n.T("Nhà thầu nộp HSDT không thể đồng thời là thành viên / thầu phụ của chính mình."));
        return p;
    }

    /// <summary>Đánh giá một yêu cầu theo các dòng kê khai (liên danh: cộng gộp năng lực các thành viên)</summary>
    public static QualificationResult Check(TenderRequirement r, IEnumerable<TenderBidderCapability> caps)
    {
        var rows = caps.Where(c => c.RequirementId == r.Id).ToList();
        string M(decimal v) => v.ToMoney();
        switch (r.Kind)
        {
            case QualificationKind.SimilarContracts:
            {
                var ok = rows.Where(c => c.Value >= r.MinValue).Sum(c => Math.Max(1, c.Count));
                return new(r, ok >= Math.Max(1, r.MinCount), I18n.T("{0} hợp đồng đạt giá trị ≥ {1} đ / yêu cầu {2}", ok, M(r.MinValue), Math.Max(1, r.MinCount)));
            }
            case QualificationKind.AverageRevenue:
            case QualificationKind.FinancialResources:
            {
                var sum = rows.Sum(c => c.Value);
                return new(r, rows.Count > 0 && sum >= r.MinValue, I18n.T("Kê khai {0} đ / yêu cầu ≥ {1} đ", M(sum), M(r.MinValue)));
            }
            case QualificationKind.KeyPersonnel:
            {
                var ok = rows.Where(c => c.Years >= r.MinYears).Sum(c => Math.Max(1, c.Count));
                return new(r, ok >= Math.Max(1, r.MinCount), I18n.T("{0} nhân sự có ≥ {1} năm kinh nghiệm / yêu cầu {2}", ok, r.MinYears, Math.Max(1, r.MinCount)));
            }
            case QualificationKind.Equipment:
            {
                var n = rows.Sum(c => c.Count);
                return new(r, n >= Math.Max(1, r.MinCount), I18n.T("{0} thiết bị / yêu cầu {1}", n, Math.Max(1, r.MinCount)));
            }
            default:
                return new(r, null, rows.Count == 0 ? I18n.T("Chưa kê khai – tổ chuyên gia đánh giá") : string.Join("; ", rows.Select(c => c.Detail).Where(x => !string.IsNullOrWhiteSpace(x))));
        }
    }

    public static List<QualificationResult> Check(Tender t, TenderBidder b) =>
        t.Requirements.OrderBy(r => r.SortOrder).Select(r => Check(r, b.Capabilities)).ToList();

    /// <summary>Đề xuất kết luận năng lực: false nếu có yêu cầu không đạt, true nếu mọi yêu cầu đo được đều đạt, null nếu chưa có yêu cầu / cần đánh giá thủ công</summary>
    public static bool? Suggest(IReadOnlyCollection<QualificationResult> res) =>
        res.Count == 0 ? null : res.Any(r => r.Pass == false) ? false : res.All(r => r.Pass == true) ? true : null;
}

/// <summary>Một dòng hao phí trong phân tích đơn giá theo định mức</summary>
public class BoqNormLine
{
    /// <summary>1 vật liệu, 2 nhân công, 3 máy thi công</summary>
    public int Kind { get; set; } = 1;
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Unit { get; set; }
    /// <summary>Hao phí cho một đơn vị khối lượng công việc</summary>
    public decimal Norm { get; set; }
    /// <summary>Đơn giá tài nguyên (trước VAT)</summary>
    public decimal Price { get; set; }
    public decimal Amount => Math.Round(Norm * Price, 0, MidpointRounding.AwayFromZero);
}

/// <summary>Phân tích đơn giá chi tiết: đơn giá công việc = Σ hao phí × đơn giá tài nguyên theo vật liệu / nhân công / máy</summary>
public static class BoqNorms
{
    public static readonly string[] KindNames = { "", "Vật liệu", "Nhân công", "Máy thi công" };

    public static List<BoqNormLine> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<BoqNormLine>>(json) ?? new(); } catch (JsonException) { return new(); }
    }

    public static string? Serialize(IReadOnlyCollection<BoqNormLine> lines) => lines.Count == 0 ? null : JsonSerializer.Serialize(lines);

    public static (decimal Material, decimal Labor, decimal Machine) Components(IEnumerable<BoqNormLine> lines)
    {
        var l = lines.ToList();
        return (l.Where(x => x.Kind == 1).Sum(x => x.Amount), l.Where(x => x.Kind == 2).Sum(x => x.Amount), l.Where(x => x.Kind == 3).Sum(x => x.Amount));
    }

    public static List<string> Validate(IReadOnlyCollection<BoqNormLine> lines)
    {
        var e = new List<string>();
        if (lines.Any(x => x.Kind is < 1 or > 3)) e.Add(I18n.T("Loại hao phí không hợp lệ."));
        if (lines.Any(x => x.Norm < 0 || x.Price < 0)) e.Add(I18n.T("Hao phí và đơn giá không được âm."));
        if (lines.Any(x => string.IsNullOrWhiteSpace(x.Name))) e.Add(I18n.T("Nhập tên tài nguyên cho mọi dòng."));
        return e;
    }
}
