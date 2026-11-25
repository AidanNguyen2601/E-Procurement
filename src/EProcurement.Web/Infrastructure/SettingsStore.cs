using System.Text.Json;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace EProcurement.Web.Infrastructure;

/// <summary>Cấu hình hệ thống lưu trong bảng AppSettings (JSON theo khóa), có cache</summary>
public class SettingsStore
{
    private readonly EProcDbContext _db;
    private readonly IMemoryCache _cache;
    public SettingsStore(EProcDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    public const string Alerts = "alerts", Email = "email", Templates = "notify.templates", Numbering = "numbering",
        Permissions = "permissions", SupplierCriteria = "supplier.criteria", Print = "print", PackageTiers = "package.tiers";

    /// <summary>Đọc cấu hình; chưa lưu → giá trị mặc định do <paramref name="fallback"/> tạo</summary>
    public async Task<T> GetAsync<T>(string key, Func<T> fallback) where T : class
    {
        var json = await _cache.GetOrCreateAsync("set:" + key, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
            return await _db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync();
        });
        if (string.IsNullOrWhiteSpace(json)) return fallback();
        try { return JsonSerializer.Deserialize<T>(json) ?? fallback(); } catch (JsonException) { return fallback(); }
    }

    public async Task SaveAsync<T>(string key, T value)
    {
        var s = await _db.AppSettings.FindAsync(key);
        if (s == null) { s = new AppSetting { Key = key }; _db.AppSettings.Add(s); }
        s.Value = JsonSerializer.Serialize(value); s.UpdatedAt = DateTime.Now;
        await _db.SaveChangesAsync();
        _cache.Remove("set:" + key);
    }

    public async Task<bool> ExistsAsync(string key) => await _db.AppSettings.AnyAsync(s => s.Key == key);
}
