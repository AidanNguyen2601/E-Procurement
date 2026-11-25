using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Dòng phát sinh đề xuất thêm vào hợp đồng; GroupPath = BoqPath của nhóm cha ("1 Móng › 1.1 Bê tông"), rỗng = hạng mục gốc</summary>
public record VariationAddition(string Description, string? Unit, decimal Quantity, decimal UnitPrice, string? GroupPath);

public record VariationPlan(List<ContractAmendmentLine> Lines, decimal ValueChange, List<string> Errors);

/// <summary>Phụ lục điều chỉnh khối lượng BOQ hợp đồng: lập phương án (kiểm tra), tính chênh lệch giá trị, áp dụng khi được duyệt</summary>
public static class BoqVariations
{
    /// <summary>Mã dòng mới dưới một nhóm: &lt;mã nhóm&gt;.&lt;số tiếp theo&gt;; không có nhóm → số nguyên tiếp theo ở cấp gốc</summary>
    public static string NextCode(IEnumerable<string?> existingCodes, string? groupPath)
    {
        var prefix = "";
        if (!string.IsNullOrWhiteSpace(groupPath))
        {
            var last = groupPath.Split(" › ").Last();
            var sp = last.IndexOf(' ');
            prefix = (sp > 0 ? last[..sp] : last) + ".";
        }
        var max = 0;
        foreach (var c in existingCodes)
        {
            if (string.IsNullOrEmpty(c) || !c.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = c[prefix.Length..];
            var first = rest.Split('.')[0];
            if (int.TryParse(first, out var n)) max = Math.Max(max, n);
        }
        return prefix + (max + 1);
    }

    /// <summary>
    /// Lập phương án: newQty = khối lượng MỚI (tổng) theo dòng hợp đồng – chỉ xét dòng đổi; không được thấp hơn khối lượng đã nghiệm thu (cumulative).
    /// additions = dòng phát sinh (cần mô tả, khối lượng &gt; 0, đơn giá &gt; 0).
    /// </summary>
    public static VariationPlan Plan(IReadOnlyCollection<ContractLine> lines, IReadOnlyDictionary<int, decimal> cumulative,
        IReadOnlyDictionary<int, decimal> newQty, IEnumerable<VariationAddition> additions)
    {
        var errors = new List<string>(); var result = new List<ContractAmendmentLine>();
        var map = lines.ToDictionary(l => l.Id);
        foreach (var (id, q) in newQty)
        {
            if (!map.TryGetValue(id, out var l)) { errors.Add(I18n.T("Dòng BOQ không thuộc hợp đồng.")); continue; }
            if (q == l.Quantity) continue;
            var label = (l.BoqCode != null ? l.BoqCode + " " : "") + l.Description;
            if (q < 0) { errors.Add(I18n.T("{0}: khối lượng không được âm.", label)); continue; }
            var done = cumulative.GetValueOrDefault(id);
            if (q < done) { errors.Add(I18n.T("{0}: khối lượng mới {1} thấp hơn khối lượng đã nghiệm thu {2}.", label, q.ToQty(), done.ToQty())); continue; }
            result.Add(new ContractAmendmentLine { ContractLineId = id, Description = l.Description, Unit = l.Unit, BoqCode = l.BoqCode, BoqPath = l.BoqPath, OldQuantity = l.Quantity, NewQuantity = q, UnitPrice = l.UnitPrice });
        }
        var codes = lines.Select(l => l.BoqCode).ToList();
        foreach (var a in additions)
        {
            var desc = (a.Description ?? "").Trim();
            if (desc.Length == 0 && a.Quantity == 0 && a.UnitPrice == 0) continue; // dòng trống
            if (desc.Length == 0 || desc.Length > 300) { errors.Add(I18n.T("Dòng phát sinh: nhập tên công việc (tối đa 300 ký tự).")); continue; }
            if (a.Quantity <= 0 || a.UnitPrice <= 0) { errors.Add(I18n.T("{0}: nhập khối lượng và đơn giá lớn hơn 0.", desc)); continue; }
            var code = NextCode(codes, a.GroupPath); codes.Add(code);
            result.Add(new ContractAmendmentLine { Description = desc, Unit = string.IsNullOrWhiteSpace(a.Unit) ? null : a.Unit.Trim(), BoqCode = code, BoqPath = string.IsNullOrWhiteSpace(a.GroupPath) ? null : a.GroupPath, OldQuantity = 0, NewQuantity = a.Quantity, UnitPrice = Math.Round(a.UnitPrice, 0) });
        }
        if (result.Count == 0 && errors.Count == 0) errors.Add(I18n.T("Chưa có điều chỉnh khối lượng nào."));
        return new VariationPlan(result, result.Sum(r => r.Delta), errors);
    }

    /// <summary>
    /// Áp dụng phụ lục đã duyệt lên dòng hợp đồng: đổi khối lượng (giữ khối lượng gốc lần đầu), thêm dòng phát sinh ngay sau dòng cuối của nhóm cha.
    /// Trả về lỗi nếu khối lượng mới thấp hơn đã nghiệm thu tại thời điểm duyệt.
    /// </summary>
    public static List<string> Apply(Contract c, ContractAmendment a, IReadOnlyDictionary<int, decimal> cumulative)
    {
        var errors = new List<string>();
        foreach (var al in a.Lines.Where(x => x.ContractLineId != null))
        {
            var l = c.Lines.FirstOrDefault(x => x.Id == al.ContractLineId);
            if (l == null) { errors.Add(I18n.T("Dòng BOQ không thuộc hợp đồng.")); continue; }
            if (al.NewQuantity < cumulative.GetValueOrDefault(l.Id)) errors.Add(I18n.T("{0}: khối lượng mới {1} thấp hơn khối lượng đã nghiệm thu {2}.", (l.BoqCode ?? "") + " " + l.Description, al.NewQuantity.ToQty(), cumulative[l.Id].ToQty()));
        }
        if (errors.Count > 0) return errors;
        foreach (var al in a.Lines.Where(x => x.ContractLineId != null))
        {
            var l = c.Lines.First(x => x.Id == al.ContractLineId);
            l.OriginalQuantity ??= l.Quantity;
            l.Quantity = al.NewQuantity;
        }
        foreach (var al in a.Lines.Where(x => x.ContractLineId == null))
        {
            var ordered = c.Lines.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList();
            var prefix = string.IsNullOrWhiteSpace(al.BoqPath) ? null : al.BoqPath.Split(" › ").Last().Split(' ')[0] + ".";
            var at = ordered.Count;
            if (prefix != null) { var lastIdx = ordered.FindLastIndex(x => x.BoqCode != null && x.BoqCode.StartsWith(prefix, StringComparison.Ordinal)); if (lastIdx >= 0) at = lastIdx + 1; }
            ordered.Insert(at, new ContractLine
            {
                ContractId = c.Id, Description = al.Description, Unit = al.Unit, Quantity = al.NewQuantity, UnitPrice = al.UnitPrice,
                BoqCode = al.BoqCode, BoqPath = al.BoqPath, OriginalQuantity = 0, AddedByAmendment = a.Number
            });
            for (var i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i + 1;
            if (!c.Lines.Contains(ordered[at])) c.Lines.Add(ordered[at]);
        }
        return errors;
    }
}
