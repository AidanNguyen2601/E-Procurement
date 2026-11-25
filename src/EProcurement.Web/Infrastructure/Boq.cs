using System.Globalization;
using System.Text;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Web.Infrastructure;

/// <summary>Một dòng BOQ đã dàn phẳng theo thứ tự cây: độ sâu, là nhóm hay dòng lá, thành tiền (nhóm = tổng các dòng con)</summary>
public record BoqNode(BoqItem Item, int Depth, bool IsGroup, decimal Amount, string Code);

/// <summary>Logic cây BOQ phân cấp (thuần bộ nhớ để dễ kiểm thử): dàn phẳng + cộng dồn, đánh mã, di chuyển, nhập / xuất văn bản.</summary>
public static class Boq
{
    public const int MaxDepth = 8;

    /// <summary>Dàn phẳng theo thứ tự (cha trước con, anh em theo SortOrder) kèm thành tiền cộng dồn và mã 1, 1.1, 1.1.1…</summary>
    public static List<BoqNode> Flatten(IReadOnlyCollection<BoqItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        var kids = items.ToLookup(i => i.ParentId.HasValue && ids.Contains(i.ParentId.Value) ? i.ParentId : null);
        var amount = new Dictionary<int, decimal>();
        decimal Sum(BoqItem n, HashSet<int> path)
        {
            if (!path.Add(n.Id)) return 0; // chặn vòng lặp do dữ liệu hỏng
            var ch = kids[n.Id];
            var v = ch.Any() ? ch.Sum(c => Sum(c, path)) : Math.Round(n.Quantity * n.UnitPrice, 0, MidpointRounding.AwayFromZero);
            path.Remove(n.Id);
            amount[n.Id] = v;
            return v;
        }
        foreach (var r in kids[null]) Sum(r, new HashSet<int>());
        var result = new List<BoqNode>();
        void Walk(int? parent, int depth, string prefix)
        {
            var n = 0;
            foreach (var it in kids[parent].OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
            {
                n++;
                var code = prefix.Length == 0 ? n.ToString() : prefix + "." + n;
                result.Add(new BoqNode(it, depth, kids[it.Id].Any(), amount.GetValueOrDefault(it.Id), code));
                if (depth < 64) Walk(it.Id, depth + 1, code);
            }
        }
        Walk(null, 0, "");
        return result;
    }

    /// <summary>Tổng giá trị BOQ = tổng các hạng mục gốc</summary>
    public static decimal Total(IReadOnlyCollection<BoqItem> items) => Flatten(items).Where(n => n.Depth == 0).Sum(n => n.Amount);

    /// <summary>Ghi lại Code và SortOrder theo vị trí hiện tại trong cây</summary>
    public static void Renumber(IReadOnlyCollection<BoqItem> items)
    {
        foreach (var grp in items.GroupBy(i => i.ParentId))
        {
            var s = 0;
            foreach (var it in grp.OrderBy(x => x.SortOrder).ThenBy(x => x.Id)) it.SortOrder = ++s;
        }
        foreach (var n in Flatten(items)) n.Item.Code = n.Code;
    }

    public static int DepthOf(IReadOnlyCollection<BoqItem> items, int id)
    {
        var map = items.ToDictionary(i => i.Id);
        var d = 0; var cur = map.GetValueOrDefault(id);
        while (cur?.ParentId is int p && map.TryGetValue(p, out cur) && d < 64) d++;
        return d;
    }

    /// <summary>Số tầng của cả nhánh con bắt đầu từ node (1 = chỉ chính nó)</summary>
    public static int Height(IReadOnlyCollection<BoqItem> items, int id)
    {
        var kids = items.ToLookup(i => i.ParentId);
        int H(int x, int guard) => guard > 64 ? 1 : 1 + (kids[x].Any() ? kids[x].Max(c => H(c.Id, guard + 1)) : 0);
        return H(id, 0);
    }

    /// <summary>Tập id của node và mọi hậu duệ</summary>
    public static HashSet<int> Subtree(IReadOnlyCollection<BoqItem> items, int id)
    {
        var kids = items.ToLookup(i => i.ParentId);
        var set = new HashSet<int>();
        var stack = new Stack<int>(); stack.Push(id);
        while (stack.Count > 0) { var x = stack.Pop(); if (!set.Add(x)) continue; foreach (var c in kids[x]) stack.Push(c.Id); }
        return set;
    }

    public record BoqLeaf(BoqNode Node, string Path);

    /// <summary>Các dòng lá có khối lượng &gt; 0 theo thứ tự cây, kèm đường dẫn hạng mục cha ("1 Hạ tầng › 1.2 Mạng") – dùng sinh dòng gói thầu</summary>
    public static List<BoqLeaf> Leaves(IReadOnlyCollection<BoqItem> items)
    {
        var nodes = Flatten(items);
        var byId = nodes.ToDictionary(n => n.Item.Id);
        var result = new List<BoqLeaf>();
        foreach (var n in nodes.Where(n => !n.IsGroup && n.Item.Quantity > 0))
        {
            var chain = new List<string>(); var cur = n.Item.ParentId;
            for (var guard = 0; cur != null && byId.TryGetValue(cur.Value, out var p) && guard < 64; guard++)
            {
                chain.Insert(0, p.Code + " " + (p.Item.Name.Length > 60 ? p.Item.Name[..60] : p.Item.Name));
                cur = p.Item.ParentId;
            }
            var path = string.Join(" › ", chain);
            result.Add(new BoqLeaf(n, path.Length > 500 ? path[..500] : path));
        }
        return result;
    }

    public enum MoveKind { Up, Down, Indent, Outdent }

    /// <summary>Di chuyển trong cây: lên / xuống (đổi chỗ với anh em), thụt vào (thành con của dòng anh ngay trước), lùi ra (thành anh của cha)</summary>
    public static string? Move(IReadOnlyCollection<BoqItem> items, int id, MoveKind kind)
    {
        var it = items.FirstOrDefault(i => i.Id == id);
        if (it == null) return "Không tìm thấy dòng BOQ.";
        Renumber(items);
        var sibs = items.Where(i => i.ParentId == it.ParentId).OrderBy(i => i.SortOrder).ToList();
        var pos = sibs.IndexOf(it);
        switch (kind)
        {
            case MoveKind.Up:
                if (pos <= 0) return "Dòng đã ở đầu danh sách cùng cấp.";
                (sibs[pos - 1].SortOrder, it.SortOrder) = (it.SortOrder, sibs[pos - 1].SortOrder); break;
            case MoveKind.Down:
                if (pos >= sibs.Count - 1) return "Dòng đã ở cuối danh sách cùng cấp.";
                (sibs[pos + 1].SortOrder, it.SortOrder) = (it.SortOrder, sibs[pos + 1].SortOrder); break;
            case MoveKind.Indent:
                if (pos <= 0) return "Không có dòng cùng cấp phía trên để thụt vào.";
                var np = sibs[pos - 1];
                if (DepthOf(items, np.Id) + Height(items, id) >= MaxDepth) return "Vượt quá số cấp tối đa của BOQ.";
                it.ParentId = np.Id; it.SortOrder = int.MaxValue; break;
            case MoveKind.Outdent:
                if (it.ParentId == null) return "Dòng đã ở cấp cao nhất.";
                var parent = items.First(i => i.Id == it.ParentId);
                var after = parent.SortOrder + 1; // ngay sau cha, đẩy các anh em phía sau lùi một bậc
                foreach (var s in items.Where(i => i.ParentId == parent.ParentId && i != it && i.SortOrder >= after)) s.SortOrder++;
                it.ParentId = parent.ParentId; it.SortOrder = after;
                break;
        }
        Renumber(items);
        return null;
    }

    // ---------- Nhập / xuất văn bản ----------
    public record ImportRow(string Code, string Name, string? Unit, decimal Quantity, decimal UnitPrice);

    /// <summary>Đọc số kiểu Việt (1.234,5) hoặc quốc tế (1,234.5 / 1234.5)</summary>
    public static bool TryNumber(string? s, out decimal v)
    {
        v = 0;
        if (string.IsNullOrWhiteSpace(s)) return true;
        s = s.Trim().Replace(" ", "").Replace(" ", "");
        var c = s.LastIndexOf(','); var d = s.LastIndexOf('.');
        if (c >= 0 && d >= 0) s = c > d ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (c >= 0) s = System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(,\d{3})+$") ? s.Replace(",", "") : s.Replace(',', '.');
        else if (d >= 0 && System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+$")) s = s.Replace(".", "");
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v) && v >= 0;
    }

    /// <summary>
    /// Mỗi dòng: Mã | Tên | ĐVT | Khối lượng | Đơn giá (phân tách bằng Tab hoặc dấu ;). Mã dạng 1, 1.1, 1.1.1 quyết định cấp;
    /// dòng đầu tiên là tiêu đề thì bỏ qua. Dòng nhóm có thể để trống ĐVT / khối lượng / đơn giá.
    /// </summary>
    public static (List<ImportRow> Rows, List<string> Errors) Parse(string text)
    {
        var rows = new List<ImportRow>(); var errors = new List<string>();
        var seen = new HashSet<string>(); var n = 0;
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
        {
            n++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var sep = raw.Contains('\t') ? '\t' : ';';
            var p = SplitLine(raw, sep);
            if (p.Length < 2) { errors.Add($"{n}|Thiếu cột (cần tối thiểu Mã và Tên)"); continue; }
            var code = p[0].TrimEnd('.');
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, @"^\d+(\.\d+)*$"))
            {
                if (rows.Count == 0 && n == 1) continue; // dòng tiêu đề
                errors.Add($"{n}|Mã không hợp lệ|{p[0]}"); continue;
            }
            if (string.IsNullOrWhiteSpace(p[1])) { errors.Add($"{n}|Thiếu tên hạng mục"); continue; }
            if (p[1].Length > 250) { errors.Add($"{n}|Tên quá dài (tối đa 250 ký tự)"); continue; }
            if (!TryNumber(p.ElementAtOrDefault(3), out var q) || !TryNumber(p.ElementAtOrDefault(4), out var price)) { errors.Add($"{n}|Khối lượng / đơn giá không hợp lệ"); continue; }
            if (!seen.Add(code)) { errors.Add($"{n}|Trùng mã|{code}"); continue; }
            var unit = p.ElementAtOrDefault(2);
            rows.Add(new ImportRow(code, p[1], string.IsNullOrWhiteSpace(unit) ? null : unit[..Math.Min(unit.Length, 30)], q, price));
        }
        return (rows, errors);
    }

    private static string[] SplitLine(string line, char sep)
    {
        var cols = new List<string>(); var sb = new StringBuilder(); var quoted = false;
        for (var k = 0; k < line.Length; k++)
        {
            var ch = line[k];
            if (quoted) { if (ch == '"' && k + 1 < line.Length && line[k + 1] == '"') { sb.Append('"'); k++; } else if (ch == '"') quoted = false; else sb.Append(ch); }
            else if (ch == '"' && sb.Length == 0) quoted = true;
            else if (ch == sep) { cols.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        cols.Add(sb.ToString().Trim());
        return cols.ToArray();
    }

    /// <summary>Dựng các BoqItem (chưa có Id) theo mã nhập; mã cha phải có trước, không có thì gắn vào cấp gần nhất. Trả về lỗi nếu quá số cấp.</summary>
    public static (List<(BoqItem Item, string? ParentCode)> Built, string? Error) Build(int packageId, IEnumerable<ImportRow> rows)
    {
        var built = new List<(BoqItem, string?)>(); var byCode = new Dictionary<string, BoqItem>();
        var order = new Dictionary<string, int>();
        foreach (var r in rows)
        {
            var parts = r.Code.Split('.');
            if (parts.Length > MaxDepth) return (built, $"Mã {r.Code} vượt quá {MaxDepth} cấp.");
            string? parent = null;
            for (var k = parts.Length - 1; k >= 1; k--)
            {
                var c = string.Join('.', parts.Take(k));
                if (byCode.ContainsKey(c)) { parent = c; break; }
            }
            var key = parent ?? "";
            order[key] = order.GetValueOrDefault(key) + 1;
            var it = new BoqItem { PackageId = packageId, Code = r.Code, Name = r.Name, Unit = r.Unit, Quantity = r.Quantity, UnitPrice = r.UnitPrice, SortOrder = order[key] };
            byCode[r.Code] = it; built.Add((it, parent));
        }
        return (built, null);
    }

    /// <summary>Xuất văn bản (dùng được để nhập lại): Mã ; Tên ; ĐVT ; Khối lượng ; Đơn giá ; Thành tiền</summary>
    public static string ToCsv(IEnumerable<BoqNode> nodes)
    {
        var sb = new StringBuilder("Mã;Hạng mục / công việc;ĐVT;Khối lượng;Đơn giá;Thành tiền\r\n");
        string Q(string? s) => s != null && (s.Contains(';') || s.Contains('"') || s.Contains('\n')) ? "\"" + s.Replace("\"", "\"\"") + "\"" : s ?? "";
        foreach (var n in nodes)
        {
            var i = n.Item;
            sb.Append(n.Code).Append(';').Append(Q(i.Name)).Append(';').Append(n.IsGroup ? "" : Q(i.Unit)).Append(';')
              .Append(n.IsGroup ? "" : i.Quantity.ToString("0.###", CultureInfo.InvariantCulture)).Append(';')
              .Append(n.IsGroup ? "" : i.UnitPrice.ToString("0", CultureInfo.InvariantCulture)).Append(';')
              .Append(n.Amount.ToString("0", CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return sb.ToString();
    }
}
