using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace EProcurement.Web.Infrastructure.Reporting;

/// <summary>
/// Ghi file Excel .xlsx tối giản (Office Open XML) không cần thư viện ngoài:
/// mỗi ReportTable một sheet, dòng tiêu đề in đậm, số giữ kiểu số (lọc / tính toán được trong Excel), ngày giữ kiểu ngày.
/// </summary>
public static class XlsxWriter
{
    public static byte[] Write(string title, string? subtitle, IEnumerable<ReportTable> tables)
    {
        var list = tables.ToList();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var names = new List<string>();
            foreach (var t in list) names.Add(UniqueSheetName(t.Title, names));

            Add(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                string.Concat(names.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>")) +
                "</Types>");
            Add(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(zip, "xl/workbook.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>" +
                string.Concat(names.Select((n, i) => $"<sheet name=\"{Esc(n)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) +
                "</sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                string.Concat(names.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>")) +
                $"<Relationship Id=\"rId{names.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
            // Kiểu: 0 thường · 1 đậm (tiêu đề) · 2 số #,##0 · 3 số #,##0.## · 4 ngày dd/mm/yyyy · 5 đậm + #,##0 (dòng tổng)
            Add(zip, "xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"#,##0.##\"/><numFmt numFmtId=\"165\" formatCode=\"dd/mm/yyyy\"/></numFmts>" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs>" +
                "<cellXfs count=\"6\"><xf/><xf fontId=\"1\" applyFont=\"1\"/><xf numFmtId=\"3\" applyNumberFormat=\"1\"/><xf numFmtId=\"164\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"165\" applyNumberFormat=\"1\"/><xf numFmtId=\"3\" fontId=\"1\" applyNumberFormat=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>");

            for (var i = 0; i < list.Count; i++) Add(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(list[i], i == 0 ? title : null, i == 0 ? subtitle : null));
        }
        return ms.ToArray();
    }

    private static string Sheet(ReportTable t, string? title, string? subtitle)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
        sb.Append("<cols>");
        for (var c = 0; c < t.Columns.Count; c++)
            sb.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{(ReportFormat.IsNumeric(t.Columns[c].Kind) ? 16 : c == 0 ? 36 : 22)}\" customWidth=\"1\"/>");
        sb.Append("</cols><sheetData>");
        var r = 1;
        void Row(IEnumerable<string> cells) { sb.Append($"<row r=\"{r}\">").Append(string.Concat(cells)).Append("</row>"); r++; }
        if (title != null) Row(new[] { Str(r, 0, title, 1) });
        if (!string.IsNullOrEmpty(subtitle)) Row(new[] { Str(r, 0, subtitle!, 0) });
        Row(new[] { Str(r, 0, t.Title, 1) });
        if (!string.IsNullOrEmpty(t.Note)) Row(new[] { Str(r, 0, t.Note!, 0) });
        Row(t.Columns.Select((c, i) => Str(r, i, c.Title, 1)));
        foreach (var row in t.Rows) Row(row.Select((v, i) => Cell(r, i, v, i < t.Columns.Count ? t.Columns[i].Kind : ColumnKind.Text, false)));
        if (t.Totals != null) Row(t.Totals.Select((v, i) => Cell(r, i, v, i < t.Columns.Count ? t.Columns[i].Kind : ColumnKind.Text, true)));
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string Cell(int r, int c, object? v, ColumnKind kind, bool bold)
    {
        var n = ReportFormat.ToDecimal(v);
        if (n.HasValue)
        {
            var style = bold ? 5 : kind is ColumnKind.Money || (kind == ColumnKind.Number && n == Math.Round(n.Value)) ? 2 : 3;
            return $"<c r=\"{Ref(r, c)}\" s=\"{style}\"><v>{n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>";
        }
        if (v is DateTime d) return $"<c r=\"{Ref(r, c)}\" s=\"4\"><v>{d.ToOADate().ToString(System.Globalization.CultureInfo.InvariantCulture)}</v></c>";
        return v == null ? "" : Str(r, c, ReportFormat.Cell(v, kind), bold ? 1 : 0);
    }

    private static string Str(int r, int c, string s, int style) =>
        $"<c r=\"{Ref(r, c)}\" t=\"inlineStr\" s=\"{style}\"><is><t xml:space=\"preserve\">{Esc(s)}</t></is></c>";

    private static string Ref(int r, int c)
    {
        var col = "";
        for (var n = c + 1; n > 0; n = (n - 1) / 26) col = (char)('A' + (n - 1) % 26) + col;
        return col + r;
    }

    /// <summary>Thoát XML và bỏ ký tự điều khiển không hợp lệ trong XML</summary>
    private static string Esc(string s) => SecurityElement.Escape(Regex.Replace(s, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "")) ?? "";

    private static string UniqueSheetName(string title, List<string> used)
    {
        var name = Regex.Replace(title, @"[\[\]\:\*\?\/\\]", " ").Trim();
        if (name.Length == 0) name = "Sheet";
        if (name.Length > 28) name = name[..28].Trim();
        var candidate = name; var i = 2;
        while (used.Contains(candidate, StringComparer.OrdinalIgnoreCase)) candidate = $"{name} {i++}";
        return candidate;
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        using var w = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
