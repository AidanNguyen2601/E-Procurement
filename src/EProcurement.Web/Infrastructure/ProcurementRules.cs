using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>PO đủ điều kiện làm căn cứ mua sắm trực tiếp cho một PR</summary>
public class ReferencePoCandidate
{
    public PurchaseOrder Po { get; set; } = null!;
    /// <summary>Khối lượng còn được mua trực tiếp theo từng mặt hàng (ItemId → SL)</summary>
    public Dictionary<int, decimal> RemainingByItem { get; set; } = new();
    /// <summary>Đơn giá tham chiếu theo từng mặt hàng (ItemId → đơn giá)</summary>
    public Dictionary<int, decimal> PriceByItem { get; set; } = new();
    /// <summary>Mọi dòng của PR nằm trong PO này và còn đủ khối lượng</summary>
    public bool CoversRequest { get; set; }
    public List<string> Problems { get; set; } = new();
}

/// <summary>Quy tắc chọn hình thức lựa chọn NCC và điều kiện mua sắm trực tiếp</summary>
public static class ProcurementRules
{
    /// <summary>Hình thức được hỗ trợ đầy đủ trên hệ thống (các hình thức khác sẽ bổ sung sau)</summary>
    public static readonly ProcurementMethod[] Supported = { ProcurementMethod.DirectPurchase, ProcurementMethod.DirectAward, ProcurementMethod.CompetitiveQuotation };

    /// <summary>Gợi ý: có PO tham chiếu hợp lệ → mua sắm trực tiếp; dự toán ≤ hạn mức → chào hàng cạnh tranh; còn lại → đấu thầu rộng rãi</summary>
    public static ProcurementMethod Suggest(PurchaseRequest pr, bool hasDirectPurchaseReference, ProcurementSettings s)
    {
        if (hasDirectPurchaseReference) return ProcurementMethod.DirectPurchase;
        return pr.EstimatedTotal <= s.CompetitiveQuotationMax ? ProcurementMethod.CompetitiveQuotation : ProcurementMethod.OpenTender;
    }

    /// <summary>
    /// Chọn hình thức kém cạnh tranh hơn gợi ý thì bắt buộc nêu lý do.
    /// Mua sắm trực tiếp không cần lý do vì đã có căn cứ là PO tham chiếu hợp lệ (bắt buộc khi chọn).
    /// </summary>
    public static bool NeedsReason(ProcurementMethod chosen, ProcurementMethod suggested) =>
        chosen == ProcurementMethod.DirectAward // chỉ định thầu là ngoại lệ → luôn phải nêu lý do
        || (chosen != ProcurementMethod.DirectPurchase && (int)chosen < (int)suggested);

    /// <summary>Ngày đặt hàng sớm nhất của PO tham chiếu còn hiệu lực</summary>
    public static DateTime ReferenceCutoff(ProcurementSettings s) => DateTime.Today.AddMonths(-s.DirectPurchaseMonths);

    /// <summary>
    /// Các PO có thể làm căn cứ mua sắm trực tiếp cho PR: đã nhận đủ hàng, đặt trong N tháng,
    /// NCC còn được phê duyệt, có chứa ít nhất một mặt hàng của PR.
    /// </summary>
    public static async Task<List<ReferencePoCandidate>> FindReferencesAsync(EProcDbContext db, PurchaseRequest pr, ProcurementSettings s, int? excludePoId = null)
    {
        var itemIds = pr.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).Distinct().ToList();
        if (itemIds.Count == 0) return new();
        var cutoff = ReferenceCutoff(s);
        var pos = await db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).AsNoTracking()
            .Where(p => p.Status == PoStatus.Received && p.OrderDate >= cutoff
                        && p.Vendor != null && p.Vendor.Status == VendorStatus.Approved
                        && p.Lines.Any(l => l.ItemId.HasValue && itemIds.Contains(l.ItemId.Value)))
            .OrderByDescending(p => p.OrderDate).ToListAsync();

        var result = new List<ReferencePoCandidate>();
        foreach (var po in pos)
        {
            var c = await BuildCandidateAsync(db, po, s, excludePoId);
            foreach (var l in pr.Lines)
            {
                if (!l.ItemId.HasValue) c.Problems.Add($"\"{l.Description}\" là hàng ngoài danh mục, không đối chiếu được.");
                else if (!c.RemainingByItem.TryGetValue(l.ItemId.Value, out var rem)) c.Problems.Add($"\"{l.Description}\" không có trong {po.Number}.");
                else if (l.Quantity > rem) c.Problems.Add($"\"{l.Description}\": cần {l.Quantity.ToQty()}, chỉ còn được mua {rem.ToQty()}.");
            }
            c.CoversRequest = c.Problems.Count == 0;
            result.Add(c);
        }
        return result.OrderByDescending(c => c.CoversRequest).ThenByDescending(c => c.Po.OrderDate).ToList();
    }

    /// <summary>Khối lượng còn lại và đơn giá tham chiếu theo mặt hàng của một PO gốc</summary>
    public static async Task<ReferencePoCandidate> BuildCandidateAsync(EProcDbContext db, PurchaseOrder refPo, ProcurementSettings s, int? excludePoId = null)
    {
        var used = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.ItemId.HasValue && l.PurchaseOrder!.ReferencePoId == refPo.Id
                        && l.PurchaseOrder.Status != PoStatus.Cancelled && l.PurchaseOrderId != excludePoId)
            .GroupBy(l => l.ItemId!.Value).Select(g => new { ItemId = g.Key, Qty = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Qty);
        var c = new ReferencePoCandidate { Po = refPo };
        foreach (var g in refPo.Lines.Where(l => l.ItemId.HasValue).GroupBy(l => l.ItemId!.Value))
        {
            var limit = Math.Floor(g.Sum(l => l.Quantity) * s.DirectPurchaseMaxQuantityPercent / 100m * 100) / 100;
            c.RemainingByItem[g.Key] = Math.Max(0, limit - used.GetValueOrDefault(g.Key));
            c.PriceByItem[g.Key] = g.Min(l => l.UnitPrice);
        }
        return c;
    }

    /// <summary>Căn cứ chỉ định thầu bắt buộc có tài liệu chứng minh đính kèm</summary>
    public static bool RequiresEvidence(DirectAwardReason r) => r is DirectAwardReason.SoleSource or DirectAwardReason.Emergency or DirectAwardReason.Compatibility;

    /// <summary>
    /// Phát hiện nghi chia nhỏ gói: các tờ trình chỉ định khác (chờ duyệt / đã duyệt) trong N ngày gần đây
    /// cùng NCC hoặc cùng nhóm hàng, cộng dồn với tờ trình này vượt hạn mức chỉ định.
    /// </summary>
    public static async Task<string?> SplitWarningAsync(EProcDbContext db, DirectAward da, ProcurementSettings s)
    {
        var since = DateTime.Today.AddDays(-s.DirectAwardSplitWindowDays);
        var itemIds = da.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();
        var catIds = await db.Items.Where(i => itemIds.Contains(i.Id) && i.CategoryId != null).Select(i => i.CategoryId!.Value).Distinct().ToListAsync();
        var others = await db.DirectAwards.AsNoTracking()
            .Where(x => x.Id != da.Id && (x.Status == DirectAwardStatus.PendingApproval || x.Status == DirectAwardStatus.Approved)
                        && (x.SubmittedAt ?? x.CreatedAt) >= since
                        && (x.VendorId == da.VendorId || x.Lines.Any(l => l.Item != null && l.Item.CategoryId != null && catIds.Contains(l.Item.CategoryId.Value))))
            .Select(x => new { x.Number, x.NegotiatedTotal }).ToListAsync();
        if (others.Count == 0) return null;
        var sum = others.Sum(x => x.NegotiatedTotal) + da.NegotiatedTotal;
        if (sum <= s.DirectAwardMax) return null;
        return $"{others.Count} tờ trình cùng NCC / nhóm hàng trong {s.DirectAwardSplitWindowDays} ngày ({string.Join(", ", others.Select(x => x.Number))}); " +
               $"cộng dồn {sum.ToMoney()} đ vượt hạn mức chỉ định {s.DirectAwardMax.ToMoney()} đ – nghi chia nhỏ gói.";
    }
}
