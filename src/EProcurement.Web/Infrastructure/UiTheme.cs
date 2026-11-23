using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using EProcurement.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EProcurement.Web.Infrastructure;

/// <summary>Tùy chọn giao diện: vị trí menu, màu chủ đạo, kiểu menu, font, cỡ chữ</summary>
public class UiPrefs
{
    /// <summary>left | right | top | bottom</summary>
    public string Nav { get; set; } = "left";
    /// <summary>Màu chủ đạo dạng #rrggbb</summary>
    public string Color { get; set; } = UiTheme.DefaultColor;
    /// <summary>dark | light – nền menu</summary>
    public string Menu { get; set; } = "dark";
    public string Font { get; set; } = "system";
    /// <summary>Cỡ chữ gốc (px)</summary>
    public int Size { get; set; } = 14;
    /// <summary>Chỉ mở một nhóm menu tại một thời điểm</summary>
    public bool Accordion { get; set; } = true;
}

public record FontOption(string Key, string Label, string Stack, string? GoogleFamily = null);

public static class UiTheme
{
    public const string DefaultColor = "#4f46e5";
    public const string DefaultKey = "ui.default";

    public static readonly (string Key, string Label)[] NavPositions =
        { ("left", "Menu bên trái"), ("right", "Menu bên phải"), ("top", "Menu ngang phía trên"), ("bottom", "Menu ngang phía dưới") };

    public static readonly (string Color, string Label)[] Colors =
    {
        ("#4f46e5", "Chàm (mặc định)"), ("#2563eb", "Xanh dương"), ("#0284c7", "Xanh biển"), ("#0d9488", "Xanh ngọc"),
        ("#059669", "Xanh lá"), ("#65a30d", "Xanh nõn chuối"), ("#ca8a04", "Vàng đồng"), ("#ea580c", "Cam"),
        ("#dc2626", "Đỏ"), ("#db2777", "Hồng"), ("#7c3aed", "Tím"), ("#475569", "Xám"),
    };

    public static readonly FontOption[] Fonts =
    {
        new("system", "Mặc định hệ thống (Segoe UI)", "\"Segoe UI\", Roboto, Arial, sans-serif"),
        new("be-vietnam", "Be Vietnam Pro", "\"Be Vietnam Pro\", \"Segoe UI\", Arial, sans-serif", "Be+Vietnam+Pro:wght@300;400;500;600;700"),
        new("inter", "Inter", "Inter, \"Segoe UI\", Arial, sans-serif", "Inter:wght@300;400;500;600;700"),
        new("roboto", "Roboto", "Roboto, \"Segoe UI\", Arial, sans-serif", "Roboto:wght@300;400;500;700"),
        new("open-sans", "Open Sans", "\"Open Sans\", \"Segoe UI\", Arial, sans-serif", "Open+Sans:wght@300;400;500;600;700"),
        new("nunito", "Nunito", "Nunito, \"Segoe UI\", Arial, sans-serif", "Nunito:wght@300;400;600;700"),
        new("arial", "Arial", "Arial, Helvetica, sans-serif"),
        new("tahoma", "Tahoma", "Tahoma, Verdana, sans-serif"),
        new("times", "Times New Roman", "\"Times New Roman\", Times, serif"),
    };

    public static readonly (int Px, string Label)[] Sizes = { (12, "Rất nhỏ"), (13, "Nhỏ"), (14, "Vừa (mặc định)"), (15, "Lớn"), (16, "Rất lớn"), (17, "Cực lớn") };

    private static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

    /// <summary>Chuẩn hóa: giá trị lạ → mặc định (giá trị được đưa vào style của trang nên phải hợp lệ tuyệt đối)</summary>
    public static UiPrefs Sanitize(UiPrefs? p)
    {
        p ??= new UiPrefs();
        return new UiPrefs
        {
            Nav = NavPositions.Any(n => n.Key == p.Nav) ? p.Nav : "left",
            Color = p.Color != null && Hex.IsMatch(p.Color) ? p.Color.ToLowerInvariant() : DefaultColor,
            Menu = p.Menu == "light" ? "light" : "dark",
            Font = Fonts.Any(f => f.Key == p.Font) ? p.Font : "system",
            Size = Math.Clamp(p.Size == 0 ? 14 : p.Size, 12, 17),
            Accordion = p.Accordion
        };
    }

    public static UiPrefs? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Sanitize(JsonSerializer.Deserialize<UiPrefs>(json)); } catch (JsonException) { return null; }
    }

    public static string Serialize(UiPrefs p) => JsonSerializer.Serialize(Sanitize(p));

    public static FontOption FontOf(UiPrefs p) => Fonts.FirstOrDefault(f => f.Key == p.Font) ?? Fonts[0];

    /// <summary>Thuộc tính style gắn vào thẻ html</summary>
    public static string Style(UiPrefs p) => $"--accent:{p.Color};--font:{FontOf(p).Stack};font-size:{p.Size}px";

    /// <summary>Đường dẫn Google Fonts cho font web (null nếu font có sẵn trên máy)</summary>
    public static string? FontHref(UiPrefs p) =>
        FontOf(p).GoogleFamily is { } fam ? $"https://fonts.googleapis.com/css2?family={fam}&display=swap" : null;
}

/// <summary>Đọc giao diện hiệu lực của người dùng: riêng → mặc định hệ thống → mặc định gốc (có cache)</summary>
public class UiThemeService
{
    private readonly EProcDbContext _db;
    private readonly IMemoryCache _cache;
    public UiThemeService(EProcDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    private static string UserKey(int id) => "ui:u:" + id;

    public async Task<UiPrefs> SystemDefaultAsync() =>
        await _cache.GetOrCreateAsync("ui:default", async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return UiTheme.Parse(await _db.AppSettings.Where(s => s.Key == UiTheme.DefaultKey).Select(s => s.Value).FirstOrDefaultAsync()) ?? new UiPrefs();
        }) ?? new UiPrefs();

    /// <summary>Giao diện riêng của người dùng (null = theo mặc định)</summary>
    public async Task<UiPrefs?> OwnAsync(int userId) =>
        await _cache.GetOrCreateAsync(UserKey(userId), async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30);
            return UiTheme.Parse(await _db.Users.Where(u => u.Id == userId).Select(u => u.UiPrefs).FirstOrDefaultAsync());
        });

    public async Task<UiPrefs> ForAsync(ClaimsPrincipal user)
    {
        var id = user.Identity?.IsAuthenticated == true ? user.GetUserId() : 0;
        return (id > 0 ? await OwnAsync(id) : null) ?? await SystemDefaultAsync();
    }

    public async Task SaveOwnAsync(int userId, UiPrefs? prefs)
    {
        var u = await _db.Users.FindAsync(userId);
        if (u == null) return;
        u.UiPrefs = prefs == null ? null : UiTheme.Serialize(prefs);
        await _db.SaveChangesAsync();
        _cache.Remove(UserKey(userId));
    }

    public async Task SaveDefaultAsync(UiPrefs prefs)
    {
        var s = await _db.AppSettings.FindAsync(UiTheme.DefaultKey);
        if (s == null) { s = new Models.Entities.AppSetting { Key = UiTheme.DefaultKey }; _db.AppSettings.Add(s); }
        s.Value = UiTheme.Serialize(prefs); s.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        _cache.Remove("ui:default");
    }
}
