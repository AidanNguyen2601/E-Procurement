using System.Globalization;
using Microsoft.Extensions.Localization;

namespace EProcurement.Web.Infrastructure;

/// <summary>
/// Song ngữ Việt – Anh. Câu gốc là tiếng Việt; khi người dùng chọn tiếng Anh, câu được tra trong từ điển
/// <see cref="I18n.En"/>, chưa có bản dịch thì giữ tiếng Việt (dịch dần từng phần được, không lỗi trang).
/// Chỉ đổi ngôn ngữ hiển thị (UI culture); định dạng số, tiền, ngày giữ kiểu Việt Nam.
/// </summary>
public static partial class I18n
{
    public const string Vi = "vi", EnCode = "en", CookieName = "eproc.lang";
    public static readonly CultureInfo ViCulture = new("vi-VN"), EnCulture = new("en");

    public static bool IsEn => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == EnCode;
    public static string Lang => IsEn ? EnCode : Vi;

    /// <summary>Dịch câu tiếng Việt (khóa) sang ngôn ngữ đang chọn</summary>
    public static string T(string vi) => IsEn && vi != null && En.TryGetValue(vi, out var e) ? e : vi!;

    /// <summary>Dịch mẫu câu có tham số {0}, {1}…</summary>
    public static string T(string vi, params object?[] args) => string.Format(T(vi), args);

    /// <summary>Bản dịch của câu (dùng khi cần chắc chắn tiếng Anh, ví dụ email cho NCC nước ngoài)</summary>
    public static string ToEn(string vi) => En.TryGetValue(vi, out var e) ? e : vi;
}

/// <summary>Dùng trong view: @L["Đăng nhập"], @L["Còn {0} ngày", n]</summary>
public class Localizer
{
    public string this[string vi] => I18n.T(vi);
    public string this[string vi, params object?[] args] => I18n.T(vi, args);
    public bool IsEn => I18n.IsEn;
    public string Lang => I18n.Lang;
}

/// <summary>Dịch tên hiển thị [Display(Name)] và thông báo kiểm tra dữ liệu của model theo từ điển</summary>
public class DictionaryStringLocalizer : IStringLocalizer
{
    public LocalizedString this[string name] => new(name, I18n.T(name), resourceNotFound: !I18n.IsEn || !I18n.En.ContainsKey(name));
    public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(I18n.T(name), arguments));
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
        I18n.En.Select(kv => new LocalizedString(kv.Key, I18n.IsEn ? kv.Value : kv.Key));
}

public class DictionaryStringLocalizerFactory : IStringLocalizerFactory
{
    private static readonly DictionaryStringLocalizer Instance = new();
    public IStringLocalizer Create(Type resourceSource) => Instance;
    public IStringLocalizer Create(string baseName, string location) => Instance;
}
