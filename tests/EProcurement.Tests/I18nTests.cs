using System.Globalization;
using System.Text.RegularExpressions;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Xunit;

namespace EProcurement.Tests;

public class I18nTests
{
    private static T With<T>(string ui, Func<T> f)
    {
        var old = CultureInfo.CurrentUICulture;
        try { CultureInfo.CurrentUICulture = ui == "en" ? I18n.EnCulture : I18n.ViCulture; return f(); }
        finally { CultureInfo.CurrentUICulture = old; }
    }

    [Fact]
    public void Translates_only_when_english_selected()
    {
        Assert.Equal("Đăng nhập", With("vi", () => I18n.T("Đăng nhập")));
        Assert.Equal("Sign in", With("en", () => I18n.T("Đăng nhập")));
        Assert.Equal("Câu chưa dịch", With("en", () => I18n.T("Câu chưa dịch"))); // chưa dịch → giữ tiếng Việt
        Assert.Equal("Minimum 5 days", With("en", () => I18n.T("Tối thiểu {0} ngày", 5)));
        Assert.Equal("Tối thiểu 5 ngày", With("vi", () => I18n.T("Tối thiểu {0} ngày", 5)));
    }

    [Fact]
    public void Enum_and_role_names_follow_language()
    {
        Assert.Equal("Đã duyệt", With("vi", () => PrStatus.Approved.GetDisplayName()));
        Assert.Equal("Approved", With("en", () => PrStatus.Approved.GetDisplayName()));
        Assert.Equal("Buyer", With("en", () => AppRoles.DisplayName(AppRoles.Buyer)));
    }

    [Fact]
    public void Every_translation_keeps_the_same_placeholders()
    {
        static string[] Holes(string s) => Regex.Matches(s, @"\{(\d+)(:[^}]*)?\}").Select(m => m.Value).OrderBy(x => x).ToArray();
        var bad = I18n.En.Where(kv => !Holes(kv.Key).SequenceEqual(Holes(kv.Value))).Select(kv => kv.Key).ToList();
        Assert.True(bad.Count == 0, "Lệch tham số: " + string.Join(" | ", bad));
        Assert.All(I18n.En, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Fact]
    public void All_status_enums_have_english_names()
    {
        var enums = new[] { typeof(PrStatus), typeof(PoStatus), typeof(RfqStatus), typeof(DirectAwardStatus), typeof(TenderStatus), typeof(ContractStatus),
                            typeof(InvoiceStatus), typeof(PaymentStatus), typeof(VendorStatus), typeof(PrequalificationStatus), typeof(PrequalificationResult) };
        var missing = enums.SelectMany(t => Enum.GetValues(t).Cast<Enum>())
            .Select(v => With("vi", () => v.GetDisplayName())).Where(vi => !I18n.En.ContainsKey(vi)).Distinct().ToList();
        Assert.True(missing.Count == 0, "Thiếu bản dịch: " + string.Join(", ", missing));
    }
}
