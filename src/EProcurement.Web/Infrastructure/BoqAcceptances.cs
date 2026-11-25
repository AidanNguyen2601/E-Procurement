using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Tiến độ khối lượng của một dòng BOQ hợp đồng</summary>
public record BoqProgressRow(ContractLine Line, bool IsGroup, int Depth, string Code, string Name, string? Unit, decimal Contracted, decimal Accepted,
    decimal UnitPrice, decimal ContractAmount, decimal AcceptedAmount)
{
    public decimal Remaining => Math.Max(0, Contracted - Accepted);
    public decimal Percent => Contracted > 0 ? Math.Round(Accepted * 100m / Contracted, 1) : 0;
}

/// <summary>Nghiệm thu khối lượng theo BOQ hợp đồng: lũy kế, kiểm tra không vượt khối lượng hợp đồng, giá trị nghiệm thu, đề xuất khấu trừ tạm ứng</summary>
public static class BoqAcceptances
{
    /// <summary>Khối lượng lũy kế đã xác nhận theo dòng hợp đồng (bỏ qua một biên bản khi đang sửa chính nó)</summary>
    public static Dictionary<int, decimal> Cumulative(IEnumerable<BoqAcceptance> acceptances, int? excludeId = null) =>
        acceptances.Where(a => a.Status == AcceptanceStatus.Confirmed && a.Id != excludeId)
            .SelectMany(a => a.Lines).GroupBy(l => l.ContractLineId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

    /// <summary>Kiểm tra khối lượng kỳ này: ≥ 0, ít nhất một dòng &gt; 0, lũy kế không vượt khối lượng hợp đồng. Trả về danh sách lỗi.</summary>
    public static List<string> Validate(IReadOnlyCollection<ContractLine> lines, IReadOnlyDictionary<int, decimal> thisPeriod, IReadOnlyDictionary<int, decimal> cumulative)
    {
        var errors = new List<string>();
        var map = lines.ToDictionary(l => l.Id);
        foreach (var (id, q) in thisPeriod)
        {
            if (!map.TryGetValue(id, out var l)) { errors.Add(I18n.T("Dòng BOQ không thuộc hợp đồng.")); continue; }
            if (q < 0) { errors.Add(I18n.T("{0}: khối lượng không được âm.", Label(l))); continue; }
            var done = cumulative.GetValueOrDefault(id);
            if (done + q > l.Quantity) errors.Add(I18n.T("{0}: lũy kế {1} vượt khối lượng hợp đồng {2} (đã nghiệm thu {3}).", Label(l), (done + q).ToQty(), l.Quantity.ToQty(), done.ToQty()));
        }
        if (!thisPeriod.Values.Any(q => q > 0)) errors.Add(I18n.T("Nhập khối lượng nghiệm thu cho ít nhất một dòng."));
        return errors;
    }

    private static string Label(ContractLine l) => (l.BoqCode != null ? l.BoqCode + " " : "") + l.Description;

    /// <summary>Giá trị nghiệm thu (trước VAT) theo đơn giá hợp đồng</summary>
    public static decimal SubTotal(IReadOnlyCollection<ContractLine> lines, IReadOnlyDictionary<int, decimal> thisPeriod) =>
        lines.Sum(l => Math.Round(thisPeriod.GetValueOrDefault(l.Id) * l.UnitPrice, 0, MidpointRounding.AwayFromZero));

    public static decimal Gross(decimal subTotal, decimal vatPercent) => Math.Round(subTotal * (1 + vatPercent / 100m), 0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Khấu trừ tạm ứng của một đợt thanh toán theo khối lượng: tỷ lệ tạm ứng đã chi / giá trị HĐ (gồm VAT) × số tiền đợt này,
    /// không vượt phần tạm ứng còn phải thu hồi.
    /// </summary>
    public static decimal AdvanceDeduction(decimal amountGross, decimal contractGross, decimal advancePaid, decimal advanceRecovered)
    {
        var outstanding = advancePaid - advanceRecovered;
        if (outstanding <= 0 || contractGross <= 0) return 0;
        return Math.Min(outstanding, Math.Round(amountGross * (advancePaid / contractGross), 0, MidpointRounding.AwayFromZero));
    }

    /// <summary>Dàn bảng tiến độ BOQ hợp đồng theo cây (nhóm cộng dồn), dùng cho trang hợp đồng và biên bản nghiệm thu</summary>
    public static List<BoqProgressRow> Progress(IEnumerable<ContractLine> lines, IReadOnlyDictionary<int, decimal> cumulative)
    {
        var rows = new List<BoqProgressRow>();
        var groups = new Dictionary<string, (int Index, decimal Contract, decimal Accepted)>();
        foreach (var l in lines.OrderBy(l => l.SortOrder).ThenBy(l => l.Id))
        {
            var segs = string.IsNullOrEmpty(l.BoqPath) ? new string[0] : l.BoqPath.Split(" › ");
            for (var i = 0; i < segs.Length; i++)
            {
                var sp = segs[i].IndexOf(' ');
                var code = sp > 0 ? segs[i][..sp] : segs[i];
                if (!groups.ContainsKey(code))
                {
                    groups[code] = (rows.Count, 0, 0);
                    rows.Add(new BoqProgressRow(l, true, i, code, sp > 0 ? segs[i][(sp + 1)..] : "", null, 0, 0, 0, 0, 0));
                }
            }
            var done = Math.Min(cumulative.GetValueOrDefault(l.Id), l.Quantity);
            var amount = Math.Round(l.Quantity * l.UnitPrice, 0, MidpointRounding.AwayFromZero);
            var doneAmount = Math.Round(cumulative.GetValueOrDefault(l.Id) * l.UnitPrice, 0, MidpointRounding.AwayFromZero);
            foreach (var seg in segs)
            {
                var sp = seg.IndexOf(' '); var code = sp > 0 ? seg[..sp] : seg;
                var g = groups[code];
                rows[g.Index] = rows[g.Index] with { ContractAmount = rows[g.Index].ContractAmount + amount, AcceptedAmount = rows[g.Index].AcceptedAmount + doneAmount };
            }
            rows.Add(new BoqProgressRow(l, false, segs.Length, l.BoqCode ?? (rows.Count + 1).ToString(), l.Description, l.Unit, l.Quantity, done, l.UnitPrice, amount, doneAmount));
        }
        return rows;
    }
}
