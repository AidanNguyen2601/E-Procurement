using System.Text.Encodings.Web;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Mô tả một danh mục dùng chung</summary>
/// <param name="Strict">true = chỉ được chọn giá trị trong danh mục; false = gợi ý, vẫn nhập tự do được (điều khoản thương mại theo từng NCC)</param>
/// <param name="AdminOnly">Chỉ Quản trị hệ thống được sửa</param>
public record CatalogInfo(CatalogType Type, string Label, string Icon, string Description, int MaxLength, bool Strict, bool AdminOnly, string[] Defaults);

public static class Catalogs
{
    public static readonly CatalogInfo[] All =
    {
        new(CatalogType.Department, "Phòng ban", "network", "Phòng ban của người dùng và đề nghị mua hàng; dùng để phân luồng duyệt và lọc báo cáo.", 100, true, true, Array.Empty<string>()),
        new(CatalogType.Unit, "Đơn vị tính", "ruler", "Đơn vị tính của hàng hóa / dịch vụ và các dòng chứng từ.", 30, true, false,
            new[] { "Cái", "Chiếc", "Bộ", "Hộp", "Thùng", "Ram", "Quyển", "Đôi", "Kg", "Mét", "Lít", "Gói", "Lần", "Tháng", "Năm", "License" }),
        new(CatalogType.PaymentTerm, "Điều khoản thanh toán", "credit-card", "Gợi ý khi nhập điều khoản thanh toán trên NCC, PO, báo giá, tờ trình, hợp đồng.", 200, false, false,
            new[] { "Thanh toán 100% khi nhận hàng", "Thanh toán 30 ngày sau khi nhận hàng và hóa đơn", "Tạm ứng 30%, thanh toán 70% sau nghiệm thu",
                    "Thanh toán hằng tháng theo biên bản nghiệm thu" }),
        new(CatalogType.ContractType, "Loại hợp đồng", "clipboard", "Loại hợp đồng trong kế hoạch lựa chọn nhà thầu.", 100, true, false,
            new[] { "Trọn gói", "Theo đơn giá cố định", "Theo đơn giá điều chỉnh", "Theo thời gian" }),
        new(CatalogType.DeliveryLocation, "Địa điểm giao hàng", "map-pin", "Gợi ý địa điểm giao hàng trên đơn mua hàng.", 300, false, false,
            new[] { "Kho công ty – Tầng 1, tòa nhà văn phòng" }),
        new(CatalogType.Bank, "Ngân hàng", "landmark", "Gợi ý ngân hàng trong thông tin tài khoản của NCC.", 100, false, false,
            new[] { "Vietcombank", "VietinBank", "BIDV", "Agribank", "Techcombank", "ACB", "MB Bank", "VPBank", "Sacombank", "TPBank" }),
        new(CatalogType.Warranty, "Thời hạn bảo hành", "shield-check", "Gợi ý thời hạn bảo hành khi nhập báo giá và tờ trình chỉ định.", 200, false, false,
            new[] { "Không bảo hành", "6 tháng", "12 tháng", "24 tháng", "36 tháng" }),
    };

    public static CatalogInfo Info(CatalogType t) => All.First(x => x.Type == t);
    public static string ListId(CatalogType t) => "dl-" + t.ToString().ToLowerInvariant();

    /// <summary>
    /// Đồng bộ danh mục với dữ liệu đang có: danh mục trống → nạp giá trị mặc định + giá trị đang dùng;
    /// phòng ban / đơn vị tính (bắt buộc chọn) → luôn bổ sung giá trị đang dùng còn thiếu để không chứng từ nào "mồ côi".
    /// Gọi lại nhiều lần vẫn an toàn.
    /// </summary>
    public static async Task EnsureAsync(EProcDbContext db)
    {
        var existing = await db.CatalogEntries.AsNoTracking().Select(e => new { e.Type, e.Name }).ToListAsync();
        foreach (var info in All)
        {
            var have = existing.Where(e => e.Type == info.Type).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (have.Count > 0 && !info.Strict) continue;
            var candidates = (have.Count == 0 ? info.Defaults : Array.Empty<string>()).Concat(await InUseAsync(db, info.Type));
            var order = have.Count;
            foreach (var raw in candidates)
            {
                var name = raw?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > info.MaxLength || !have.Add(name)) continue;
                db.CatalogEntries.Add(new CatalogEntry { Type = info.Type, Name = name, SortOrder = ++order * 10 });
            }
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Giá trị đang được dùng trên dữ liệu (để nạp ban đầu / kiểm tra trước khi xóa)</summary>
    public static async Task<List<string>> InUseAsync(EProcDbContext db, CatalogType t) => t switch
    {
        CatalogType.Department => await db.Users.Select(u => u.Department).Union(db.PurchaseRequests.Select(p => p.Department)).Distinct().ToListAsync(),
        CatalogType.Unit => await db.Items.Select(i => i.Unit).Distinct().ToListAsync(),
        CatalogType.PaymentTerm => (await db.Vendors.Where(v => v.PaymentTerms != null).Select(v => v.PaymentTerms!).Distinct().ToListAsync()),
        CatalogType.ContractType => (await db.Tenders.Where(x => x.ContractType != null).Select(x => x.ContractType!).Distinct().ToListAsync()),
        CatalogType.DeliveryLocation => await db.PurchaseOrders.Where(p => p.DeliveryAddress != null).GroupBy(p => p.DeliveryAddress!)
            .OrderByDescending(g => g.Count()).Select(g => g.Key).Take(20).ToListAsync(),
        CatalogType.Bank => (await db.Vendors.Where(v => v.BankName != null).Select(v => v.BankName!).Distinct().ToListAsync()),
        CatalogType.Warranty => (await db.RfqVendors.Where(v => v.Warranty != null).Select(v => v.Warranty!)
            .Union(db.DirectAwards.Where(d => d.Warranty != null).Select(d => d.Warranty!)).Distinct().ToListAsync()),
        _ => new()
    };
}

/// <summary>Đọc danh mục cho view / controller (cache trong phạm vi một request)</summary>
public class CatalogService
{
    private readonly EProcDbContext _db;
    private readonly Dictionary<CatalogType, List<string>> _cache = new();
    public CatalogService(EProcDbContext db) => _db = db;

    /// <summary>Các giá trị đang sử dụng, theo thứ tự hiển thị</summary>
    public async Task<List<string>> NamesAsync(CatalogType t)
    {
        if (!_cache.TryGetValue(t, out var list))
            _cache[t] = list = await _db.CatalogEntries.AsNoTracking().Where(e => e.Type == t && e.IsActive)
                .OrderBy(e => e.SortOrder).ThenBy(e => e.Name).Select(e => e.Name).ToListAsync();
        return list;
    }

    /// <summary>&lt;datalist&gt; gợi ý cho ô nhập (gắn bằng list="@Catalogs.ListId(...)")</summary>
    public async Task<IHtmlContent> DatalistAsync(CatalogType t)
    {
        var enc = HtmlEncoder.Default;
        var sb = new System.Text.StringBuilder($"<datalist id=\"{Catalogs.ListId(t)}\">");
        foreach (var n in await NamesAsync(t)) sb.Append("<option value=\"").Append(enc.Encode(n)).Append("\"></option>");
        return new HtmlString(sb.Append("</datalist>").ToString());
    }

    /// <summary>Danh sách chọn; giữ giá trị hiện tại nếu đã ngưng sử dụng để không mất dữ liệu khi sửa</summary>
    public async Task<SelectList> SelectAsync(CatalogType t, string? current)
    {
        var names = new List<string>(await NamesAsync(t));
        if (!string.IsNullOrWhiteSpace(current) && !names.Contains(current, StringComparer.OrdinalIgnoreCase)) names.Insert(0, current);
        return new SelectList(names, current);
    }

    /// <summary>Giá trị hợp lệ: có trong danh mục đang dùng, hoặc giữ nguyên giá trị cũ</summary>
    public async Task<bool> IsValidAsync(CatalogType t, string? value, string? previous = null) =>
        !string.IsNullOrWhiteSpace(value)
        && ((await NamesAsync(t)).Contains(value.Trim(), StringComparer.OrdinalIgnoreCase) || string.Equals(value.Trim(), previous, StringComparison.OrdinalIgnoreCase));
}
