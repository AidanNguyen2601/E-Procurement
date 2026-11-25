using System.Globalization;
using System.Text;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Giá một nhà thầu chào cho một dòng BOQ</summary>
public record BoqCell(decimal UnitPrice, decimal Amount, decimal? DeviationPct, bool Lowest, string? Flag);

/// <summary>Một dòng bảng so sánh: hạng mục (nhóm) hoặc dòng lá; Cells theo TenderBidder.Id</summary>
public class BoqCompareRow
{
    public bool IsGroup { get; set; }
    public int Depth { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Unit { get; set; }
    public decimal Quantity { get; set; }
    public decimal EstimatedUnit { get; set; }
    public decimal EstimatedAmount { get; set; }
    public Dictionary<int, BoqCell> Cells { get; set; } = new();
}

public class BoqCompareResult
{
    public List<BoqCompareRow> Rows { get; set; } = new();
    public List<TenderBidder> Bidders { get; set; } = new();
    /// <summary>Tổng giá chào theo các dòng (trước giảm giá, trước VAT) của từng nhà thầu</summary>
    public Dictionary<int, decimal> Totals { get; set; } = new();
    public decimal EstimatedTotal { get; set; }
    /// <summary>Số dòng bị cảnh báo của từng nhà thầu</summary>
    public Dictionary<int, int> FlagCounts { get; set; } = new();
    public bool HasBoq { get; set; }
}

/// <summary>So sánh giá dự thầu theo từng dòng BOQ: dòng thấp nhất, lệch so với dự toán, cảnh báo giá thấp / cao bất thường, thiếu giá</summary>
public static class BoqBid
{
    /// <summary>Đơn giá &lt; 70% dự toán → thấp bất thường; &gt; 130% → cao bất thường</summary>
    public const decimal LowPercent = 70, HighPercent = 130;

    public static BoqCompareResult Build(Tender t, IEnumerable<TenderBidder> bidders)
    {
        var res = new BoqCompareResult { Bidders = bidders.OrderBy(b => b.Vendor?.Name).ToList(), HasBoq = t.Lines.Any(l => l.BoqCode != null) };
        foreach (var b in res.Bidders) { res.Totals[b.Id] = 0; res.FlagCounts[b.Id] = 0; }
        var groups = new Dictionary<string, BoqCompareRow>();
        var order = 0;
        foreach (var line in t.Lines.OrderBy(l => l.SortOrder).ThenBy(l => l.Id))
        {
            order++;
            var segs = string.IsNullOrEmpty(line.BoqPath) ? new string[0] : line.BoqPath.Split(" › ");
            var chain = new List<BoqCompareRow>();
            for (var i = 0; i < segs.Length; i++)
            {
                var sp = segs[i].IndexOf(' ');
                var code = sp > 0 ? segs[i][..sp] : segs[i];
                if (!groups.TryGetValue(code, out var g))
                {
                    g = new BoqCompareRow { IsGroup = true, Depth = i, Code = code, Name = sp > 0 ? segs[i][(sp + 1)..] : "" };
                    foreach (var b in res.Bidders) g.Cells[b.Id] = new BoqCell(0, 0, null, false, null);
                    groups[code] = g; res.Rows.Add(g);
                }
                chain.Add(g);
            }
            var row = new BoqCompareRow
            {
                Depth = segs.Length, Code = line.BoqCode ?? order.ToString(), Name = line.Description, Unit = line.Unit,
                Quantity = line.Quantity, EstimatedUnit = line.EstimatedPrice,
                EstimatedAmount = Math.Round(line.Quantity * line.EstimatedPrice, 0, MidpointRounding.AwayFromZero)
            };
            var prices = res.Bidders.ToDictionary(b => b.Id, b => b.Lines.FirstOrDefault(l => l.TenderLineId == line.Id)?.UnitPrice ?? 0m);
            var low = prices.Values.Where(p => p > 0).DefaultIfEmpty(0).Min();
            foreach (var b in res.Bidders)
            {
                var p = prices[b.Id];
                var amount = Math.Round(line.Quantity * p, 0, MidpointRounding.AwayFromZero);
                decimal? dev = line.EstimatedPrice > 0 && p > 0 ? Math.Round((p / line.EstimatedPrice - 1) * 100, 1) : null;
                string? flag = p <= 0 ? "missing" : line.EstimatedPrice > 0 && p * 100 < line.EstimatedPrice * LowPercent ? "low" : line.EstimatedPrice > 0 && p * 100 > line.EstimatedPrice * HighPercent ? "high" : null;
                if (flag != null) res.FlagCounts[b.Id]++;
                row.Cells[b.Id] = new BoqCell(p, amount, dev, p > 0 && p == low && prices.Values.Count(x => x > 0) > 1, flag);
                res.Totals[b.Id] += amount;
                foreach (var g in chain) g.Cells[b.Id] = g.Cells[b.Id] with { Amount = g.Cells[b.Id].Amount + amount };
            }
            foreach (var g in chain) g.EstimatedAmount += row.EstimatedAmount;
            res.EstimatedTotal += row.EstimatedAmount;
            res.Rows.Add(row);
        }
        return res;
    }

    /// <summary>Xuất CSV: mỗi nhà thầu 2 cột (đơn giá, thành tiền)</summary>
    public static string ToCsv(BoqCompareResult r)
    {
        var sb = new StringBuilder("Mã;Hạng mục / công việc;ĐVT;Khối lượng;Đơn giá dự toán;Thành tiền dự toán");
        string Q(string? s) => s != null && (s.Contains(';') || s.Contains('"')) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s ?? "";
        foreach (var b in r.Bidders) sb.Append(';').Append(Q(b.Vendor?.Name + " – đơn giá")).Append(';').Append(Q(b.Vendor?.Name + " – thành tiền"));
        sb.Append("\r\n");
        var inv = CultureInfo.InvariantCulture;
        foreach (var row in r.Rows)
        {
            sb.Append(row.Code).Append(';').Append(Q(row.Name)).Append(';').Append(row.IsGroup ? "" : Q(row.Unit)).Append(';')
              .Append(row.IsGroup ? "" : row.Quantity.ToString("0.###", inv)).Append(';')
              .Append(row.IsGroup ? "" : row.EstimatedUnit.ToString("0", inv)).Append(';').Append(row.EstimatedAmount.ToString("0", inv));
            foreach (var b in r.Bidders)
            {
                var c = row.Cells[b.Id];
                sb.Append(';').Append(row.IsGroup ? "" : c.UnitPrice.ToString("0", inv)).Append(';').Append(c.Amount.ToString("0", inv));
            }
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
