using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Cơ cấu chi phí BOQ: chi phí trực tiếp (vật liệu / nhân công / máy / khác) → chi phí chung → lợi nhuận → dự phòng → VAT</summary>
public record BoqCostSummary(decimal Direct, decimal Material, decimal Labor, decimal Machine, decimal Other,
    decimal Overhead, decimal Profit, decimal Subtotal, decimal Contingency, decimal TotalExVat, decimal Vat, decimal Grand);

/// <summary>Một dòng trong ảnh chụp phiên bản BOQ</summary>
public record BoqSnapItem(string Code, string Name, string? Unit, decimal Quantity, decimal UnitPrice, decimal Material, decimal Labor, decimal Machine, string? Note);

public enum BoqDiffKind { Added, Removed, Changed }

public record BoqDiffRow(BoqDiffKind Kind, string Code, string Name, string? OldName, decimal OldQuantity, decimal NewQuantity, decimal OldUnitPrice, decimal NewUnitPrice)
{
    public decimal OldAmount => Math.Round(OldQuantity * OldUnitPrice, 0, MidpointRounding.AwayFromZero);
    public decimal NewAmount => Math.Round(NewQuantity * NewUnitPrice, 0, MidpointRounding.AwayFromZero);
}

public static class BoqCost
{
    public static BoqCostSummary Summary(IReadOnlyCollection<BoqItem> items, decimal overheadPct, decimal profitPct, decimal contingencyPct, decimal vatPct)
    {
        var nodes = Boq.Flatten(items);
        var direct = nodes.Where(n => n.Depth == 0).Sum(n => n.Amount);
        decimal Comp(Func<BoqItem, decimal> f) => nodes.Where(n => !n.IsGroup).Sum(n => Math.Round(n.Item.Quantity * f(n.Item), 0, MidpointRounding.AwayFromZero));
        var mat = Comp(i => i.MaterialPrice); var lab = Comp(i => i.LaborPrice); var mac = Comp(i => i.MachinePrice);
        decimal R(decimal v) => Math.Round(v, 0, MidpointRounding.AwayFromZero);
        var overhead = R(direct * overheadPct / 100m);
        var profit = R((direct + overhead) * profitPct / 100m);
        var subtotal = direct + overhead + profit;
        var contingency = R(subtotal * contingencyPct / 100m);
        var exVat = subtotal + contingency;
        var vat = R(exVat * vatPct / 100m);
        return new BoqCostSummary(direct, mat, lab, mac, Math.Max(0, direct - mat - lab - mac), overhead, profit, subtotal, contingency, exVat, vat, exVat + vat);
    }

    /// <summary>Đơn giá = vật liệu + nhân công + máy khi có nhập cơ cấu giá</summary>
    public static decimal PriceFromComponents(decimal unitPrice, decimal material, decimal labor, decimal machine) =>
        material + labor + machine > 0 ? material + labor + machine : unitPrice;

    // ---------- Phiên bản ----------
    public static string ToSnapshot(IReadOnlyCollection<BoqItem> items) =>
        JsonSerializer.Serialize(Boq.Flatten(items).Select(n => new BoqSnapItem(n.Code, n.Item.Name, n.Item.Unit, n.Item.Quantity, n.Item.UnitPrice, n.Item.MaterialPrice, n.Item.LaborPrice, n.Item.MachinePrice, n.Item.Note)));

    public static List<BoqSnapItem> FromSnapshot(string json)
    {
        try { return JsonSerializer.Deserialize<List<BoqSnapItem>>(json) ?? new(); } catch (JsonException) { return new(); }
    }

    /// <summary>Dựng lại danh sách BoqItem (có Id tạm, quan hệ cha–con theo mã) từ ảnh chụp – dùng để tính tổng / so sánh</summary>
    public static List<BoqItem> ToItems(int packageId, IEnumerable<BoqSnapItem> snap)
    {
        var (built, _) = Boq.Build(packageId, snap.Select(s => new Boq.ImportRow(s.Code, s.Name, s.Unit, s.Quantity, s.UnitPrice, s.Material, s.Labor, s.Machine, s.Note)));
        var byCode = new Dictionary<string, BoqItem>(); var id = 0;
        foreach (var (it, _) in built) { it.Id = ++id; byCode[it.Code] = it; }
        foreach (var (it, pc) in built) if (pc != null) it.ParentId = byCode[pc].Id;
        return built.Select(b => b.Item).ToList();
    }

    public static decimal SnapshotTotal(int packageId, IEnumerable<BoqSnapItem> snap) => Boq.Total(ToItems(packageId, snap));

    /// <summary>So sánh hai ảnh chụp theo mã dòng: thêm, bớt, đổi (tên / khối lượng / đơn giá). Chỉ liệt kê dòng lá có khối lượng hoặc đơn giá.</summary>
    public static List<BoqDiffRow> Diff(IReadOnlyCollection<BoqSnapItem> oldSnap, IReadOnlyCollection<BoqSnapItem> newSnap)
    {
        var o = oldSnap.ToDictionary(x => x.Code); var n = newSnap.ToDictionary(x => x.Code);
        var rows = new List<BoqDiffRow>();
        bool IsLeafLine(BoqSnapItem s) => s.Quantity > 0 || s.UnitPrice > 0;
        foreach (var s in newSnap)
        {
            if (!o.TryGetValue(s.Code, out var old)) { if (IsLeafLine(s)) rows.Add(new(BoqDiffKind.Added, s.Code, s.Name, null, 0, s.Quantity, 0, s.UnitPrice)); continue; }
            if (old.Name != s.Name || old.Quantity != s.Quantity || old.UnitPrice != s.UnitPrice)
                if (IsLeafLine(s) || IsLeafLine(old)) rows.Add(new(BoqDiffKind.Changed, s.Code, s.Name, old.Name != s.Name ? old.Name : null, old.Quantity, s.Quantity, old.UnitPrice, s.UnitPrice));
        }
        foreach (var s in oldSnap) if (!n.ContainsKey(s.Code) && IsLeafLine(s)) rows.Add(new(BoqDiffKind.Removed, s.Code, s.Name, null, s.Quantity, 0, s.UnitPrice, 0));
        return rows.OrderBy(r => r.Code, Comparer<string>.Create(CompareCodes)).ToList();
    }

    /// <summary>So sánh mã 1.2.10 &gt; 1.2.9 theo số, không theo chữ</summary>
    public static int CompareCodes(string a, string b)
    {
        var x = a.Split('.'); var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var c = int.TryParse(x[i], out var xi) && int.TryParse(y[i], out var yi) ? xi.CompareTo(yi) : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}

/// <summary>Đọc sheet đầu tiên của file .xlsx (không cần thư viện ngoài): chuỗi dùng chung, chuỗi nội tuyến, số. Bỏ qua công thức / định dạng.</summary>
public static class XlsxReader
{
    public const int MaxRows = 5000;

    private static XDocument Load(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(r);
    }

    private static int ColIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef) { if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1); else break; }
        return n - 1;
    }

    /// <summary>Trả về các dòng (số dòng Excel, các cột). Lỗi định dạng → InvalidDataException.</summary>
    public static List<(int Line, string[] Cols)> Read(Stream xlsx)
    {
        try
        {
            using var zip = new ZipArchive(xlsx, ZipArchiveMode.Read);
            var sheet = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml"))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? throw new InvalidDataException("Không có sheet dữ liệu");
            if (sheet.Length > 30_000_000) throw new InvalidDataException("Sheet quá lớn");
            var shared = new List<string>();
            var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
            if (ssEntry != null)
                foreach (var si in Load(ssEntry).Descendants().Where(x => x.Name.LocalName == "si"))
                    shared.Add(string.Concat(si.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value)));
            var rows = new List<(int, string[])>();
            foreach (var row in Load(sheet).Descendants().Where(x => x.Name.LocalName == "row"))
            {
                var line = int.TryParse((string?)row.Attribute("r"), out var rn) ? rn : rows.Count + 1;
                var cells = new Dictionary<int, string>(); var max = -1;
                foreach (var c in row.Elements().Where(x => x.Name.LocalName == "c"))
                {
                    var col = ColIndex((string?)c.Attribute("r") ?? "");
                    if (col < 0 || col > 60) continue;
                    var type = (string?)c.Attribute("t");
                    var v = c.Elements().FirstOrDefault(x => x.Name.LocalName == "v")?.Value;
                    string? text = type switch
                    {
                        "s" => int.TryParse(v, out var si) && si >= 0 && si < shared.Count ? shared[si] : null,
                        "inlineStr" => string.Concat(c.Descendants().Where(x => x.Name.LocalName == "t").Select(x => x.Value)),
                        "str" or "n" or null => v,
                        _ => null
                    };
                    if (text == null) continue;
                    cells[col] = text; max = Math.Max(max, col);
                }
                if (max < 0) continue;
                var arr = new string[max + 1];
                for (var i = 0; i <= max; i++) arr[i] = cells.GetValueOrDefault(i, "");
                rows.Add((line, arr));
                if (rows.Count > MaxRows) throw new InvalidDataException("Tối đa " + MaxRows + " dòng");
            }
            return rows;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is XmlException or IOException or InvalidOperationException) { throw new InvalidDataException("File .xlsx không đọc được", ex); }
    }
}
