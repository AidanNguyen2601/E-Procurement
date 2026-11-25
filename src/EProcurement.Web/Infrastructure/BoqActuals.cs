using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Một dòng so sánh dự toán – hợp đồng – thực tế (nhóm hoặc dòng lá)</summary>
public class BoqActualRow
{
    public bool IsGroup { get; set; }
    public int Depth { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Unit { get; set; }
    public bool Added { get; set; }
    // dự toán (BOQ khi lập gói thầu)
    public decimal EstQuantity { get; set; }
    public decimal EstUnitPrice { get; set; }
    public decimal EstAmount { get; set; }
    // hợp đồng
    public decimal ContractQuantity { get; set; }
    public decimal ContractUnitPrice { get; set; }
    public decimal ContractAmount { get; set; }
    // thực tế: khối lượng đã nghiệm thu × đơn giá hợp đồng
    public decimal ActualQuantity { get; set; }
    public decimal ActualAmount { get; set; }

    /// <summary>Hợp đồng so với dự toán (âm = tiết kiệm)</summary>
    public decimal ContractVsEstimate => ContractAmount - EstAmount;
    public decimal? ContractVsEstimatePct => EstAmount > 0 ? Math.Round((ContractAmount / EstAmount - 1) * 100, 1) : null;
    /// <summary>Thực tế đã thực hiện so với phần hợp đồng cùng khối lượng (tiến độ giá trị)</summary>
    public decimal Progress => ContractAmount > 0 ? Math.Round(ActualAmount * 100 / ContractAmount, 1) : 0;
    /// <summary>Dự toán của phần đã thực hiện: khối lượng thực tế × đơn giá dự toán – để biết thực tế rẻ / đắt hơn dự toán</summary>
    public decimal EstForActual { get; set; }
    public decimal ActualVsEstimate => ActualAmount - EstForActual;
}

public class BoqActualsResult
{
    public List<BoqActualRow> Rows { get; set; } = new();
    public decimal EstTotal { get; set; }
    public decimal ContractOriginal { get; set; }
    public decimal ContractCurrent { get; set; }
    public decimal ActualTotal { get; set; }
    public decimal EstForActualTotal { get; set; }
    /// <summary>Dự báo giá trị khi hoàn thành = thực tế + phần còn lại theo đơn giá hợp đồng = giá trị hợp đồng hiện hành (không phát sinh thêm)</summary>
    public decimal ForecastAtCompletion => ContractCurrent;
    public decimal AddedTotal { get; set; }
    public decimal PaidGross { get; set; }
    public decimal PaidNet { get; set; }
    public decimal RequestedGross { get; set; }
    /// <summary>Giá gói (ngân sách của gói) và giá trúng thầu của gói thầu – do nơi gọi điền</summary>
    /// <summary>Đã đặt hàng theo hợp đồng (trước VAT) – hợp đồng khung: giá trị đã đặt so với giá trị hợp đồng</summary>
    public decimal OrderedTotal { get; set; }
    public decimal PackageBudget { get; set; }
    public decimal AwardPrice { get; set; }
}

/// <summary>Tổng hợp giá dự toán – giá hợp đồng – giá thực tế (khối lượng nghiệm thu, tiền đã chi) của hợp đồng xây dựng theo BOQ</summary>
public static class BoqActuals
{
    public static BoqActualsResult Build(Contract contract, IReadOnlyCollection<TenderLine> tenderLines, IReadOnlyDictionary<int, decimal> cumulative, IEnumerable<Payment> payments)
    {
        var res = new BoqActualsResult();
        var est = tenderLines.Where(l => l.BoqCode != null).ToDictionary(l => l.BoqCode!);
        var used = new HashSet<int>();
        var groups = new Dictionary<string, BoqActualRow>();
        foreach (var l in contract.Lines.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            var segs = string.IsNullOrEmpty(l.BoqPath) ? new string[0] : l.BoqPath.Split(" › ");
            var chain = new List<BoqActualRow>();
            for (var i = 0; i < segs.Length; i++)
            {
                var sp = segs[i].IndexOf(' ');
                var code = sp > 0 ? segs[i][..sp] : segs[i];
                if (!groups.TryGetValue(code, out var g)) { g = new BoqActualRow { IsGroup = true, Depth = i, Code = code, Name = sp > 0 ? segs[i][(sp + 1)..] : "" }; groups[code] = g; res.Rows.Add(g); }
                chain.Add(g);
            }
            // dòng dự toán tương ứng: theo mã BOQ; không có thì theo hàng hóa (ItemId) hoặc tên mô tả
            TenderLine? t = l.BoqCode != null && est.TryGetValue(l.BoqCode, out var byCode) ? byCode
                : tenderLines.FirstOrDefault(x => x.BoqCode == null && !used.Contains(x.Id) && ((l.ItemId != null && x.ItemId == l.ItemId) || x.Description == l.Description));
            if (t != null) used.Add(t.Id);
            var added = l.AddedByAmendment != null || (t == null && tenderLines.Count > 0);
            var contractQty = l.Quantity; var origQty = l.OriginalQuantity ?? l.Quantity;
            var row = new BoqActualRow
            {
                Depth = segs.Length, Code = l.BoqCode ?? (res.Rows.Count + 1).ToString(), Name = l.Description, Unit = l.Unit, Added = added,
                EstQuantity = t?.Quantity ?? 0, EstUnitPrice = t?.EstimatedPrice ?? 0,
                EstAmount = t == null ? 0 : Math.Round(t.Quantity * t.EstimatedPrice, 0, MidpointRounding.AwayFromZero),
                ContractQuantity = contractQty, ContractUnitPrice = l.UnitPrice, ContractAmount = Math.Round(contractQty * l.UnitPrice, 0, MidpointRounding.AwayFromZero),
                ActualQuantity = cumulative.GetValueOrDefault(l.Id),
            };
            row.ActualAmount = Math.Round(row.ActualQuantity * l.UnitPrice, 0, MidpointRounding.AwayFromZero);
            row.EstForActual = Math.Round(row.ActualQuantity * row.EstUnitPrice, 0, MidpointRounding.AwayFromZero);
            res.ContractOriginal += Math.Round(origQty * l.UnitPrice, 0, MidpointRounding.AwayFromZero);
            if (added) res.AddedTotal += row.ContractAmount;
            foreach (var g in chain)
            {
                g.EstAmount += row.EstAmount; g.ContractAmount += row.ContractAmount; g.ActualAmount += row.ActualAmount; g.EstForActual += row.EstForActual;
            }
            res.EstTotal += row.EstAmount; res.ContractCurrent += row.ContractAmount; res.ActualTotal += row.ActualAmount; res.EstForActualTotal += row.EstForActual;
            res.Rows.Add(row);
        }
        var ps = payments.Where(p => p.Status != PaymentStatus.Rejected).ToList();
        res.PaidGross = ps.Where(p => p.Status == PaymentStatus.Paid && p.Type != PaymentType.Advance).Sum(p => p.Amount);
        res.PaidNet = ps.Where(p => p.Status == PaymentStatus.Paid).Sum(p => p.NetAmount);
        res.RequestedGross = ps.Where(p => p.Status != PaymentStatus.Paid && p.Type != PaymentType.Advance).Sum(p => p.Amount);
        return res;
    }

    /// <summary>Gói thầu chưa có hợp đồng: chỉ có dự toán theo từng dòng (nhóm theo BOQ nếu có)</summary>
    public static BoqActualsResult EstimateOnly(IReadOnlyCollection<TenderLine> tenderLines)
    {
        var res = new BoqActualsResult();
        var groups = new Dictionary<string, BoqActualRow>();
        foreach (var l in tenderLines.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            var segs = string.IsNullOrEmpty(l.BoqPath) ? new string[0] : l.BoqPath.Split(" › ");
            var chain = new List<BoqActualRow>();
            for (var i = 0; i < segs.Length; i++)
            {
                var sp = segs[i].IndexOf(' ');
                var code = sp > 0 ? segs[i][..sp] : segs[i];
                if (!groups.TryGetValue(code, out var g)) { g = new BoqActualRow { IsGroup = true, Depth = i, Code = code, Name = sp > 0 ? segs[i][(sp + 1)..] : "" }; groups[code] = g; res.Rows.Add(g); }
                chain.Add(g);
            }
            var amount = Math.Round(l.Quantity * l.EstimatedPrice, 0, MidpointRounding.AwayFromZero);
            foreach (var g in chain) g.EstAmount += amount;
            res.EstTotal += amount;
            res.Rows.Add(new BoqActualRow { Depth = segs.Length, Code = l.BoqCode ?? (res.Rows.Count + 1).ToString(), Name = l.Description, Unit = l.Unit, EstQuantity = l.Quantity, EstUnitPrice = l.EstimatedPrice, EstAmount = amount });
        }
        return res;
    }
}
