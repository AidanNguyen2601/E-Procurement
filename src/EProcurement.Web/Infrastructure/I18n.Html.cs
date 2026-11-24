using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EProcurement.Web.Infrastructure;

/// <summary>
/// Dịch ở tầng xuất HTML khi người dùng chọn tiếng Anh: văn bản (và title / placeholder / aria-label / data-confirm)
/// trùng khớp CẢ CÂU với từ điển được thay bằng bản tiếng Anh. Bổ sung cho @L["..."] để các nhãn nằm trong dữ liệu tĩnh
/// của C# (tên cảnh báo, mẫu mã tự sinh, bước quy trình…) cũng được dịch mà không phải sửa từng nơi.
/// Không đụng tới &lt;script&gt;, &lt;style&gt;, &lt;textarea&gt; và dữ liệu người dùng nhập (chỉ dịch khi trùng nguyên câu trong từ điển).
/// </summary>
public static partial class I18n
{
    private static readonly Regex RawBlocks = new(@"(<script\b.*?</script>|<style\b.*?</style>|<textarea\b.*?</textarea>)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TextNode = new(@">([^<>]+)<", RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"\b(title|placeholder|aria-label|data-confirm|alt)=""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static bool TryEn(string raw, out string en)
    {
        en = "";
        var key = Spaces.Replace(WebUtility.HtmlDecode(raw).Trim(), " ");
        return key.Length > 1 && En.TryGetValue(key, out en!);
    }

    /// <summary>Dịch một đoạn HTML sang tiếng Anh theo từ điển (khớp nguyên câu)</summary>
    public static string TranslateHtml(string html)
    {
        var parts = RawBlocks.Split(html);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i % 2 == 1) continue; // khối script / style / textarea
            parts[i] = TextNode.Replace(parts[i], m =>
            {
                var raw = m.Groups[1].Value;
                if (!TryEn(raw, out var en)) return m.Value;
                var lead = raw[..(raw.Length - raw.TrimStart().Length)];
                var trail = raw[raw.TrimEnd().Length..];
                return ">" + lead + WebUtility.HtmlEncode(en) + trail + "<";
            });
            parts[i] = Attr.Replace(parts[i], m => TryEn(m.Groups[2].Value, out var en) ? $"{m.Groups[1].Value}=\"{WebUtility.HtmlEncode(en)}\"" : m.Value);
        }
        return string.Concat(parts);
    }
}

/// <summary>Chỉ chạy khi chọn tiếng Anh: đệm phản hồi HTML, dịch rồi gửi đi</summary>
public class EnglishHtmlMiddleware
{
    private readonly RequestDelegate _next;
    public EnglishHtmlMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!I18n.IsEn) { await _next(ctx); return; }
        var original = ctx.Response.Body;
        await using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await _next(ctx); }
        finally { ctx.Response.Body = original; }

        buffer.Position = 0;
        var isHtml = ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;
        if (isHtml && buffer.Length > 0 && !ctx.Response.Headers.ContainsKey("Content-Encoding"))
        {
            var bytes = Encoding.UTF8.GetBytes(I18n.TranslateHtml(Encoding.UTF8.GetString(buffer.ToArray())));
            ctx.Response.ContentLength = bytes.Length;
            await original.WriteAsync(bytes);
        }
        else if (buffer.Length > 0) await buffer.CopyToAsync(original);
    }
}
