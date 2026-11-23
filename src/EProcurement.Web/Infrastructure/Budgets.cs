using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure;

/// <summary>Tình hình sử dụng một dòng ngân sách</summary>
public class BudgetStatus
{
    public Budget Budget { get; init; } = null!;
    /// <summary>Đã đặt hàng: dòng PO (trước VAT) đã duyệt / đang thực hiện, theo phòng ban của PR</summary>
    public decimal Ordered { get; set; }
    /// <summary>Giữ chỗ: phần dự toán của PR đã duyệt chưa lập PO</summary>
    public decimal Reserved { get; set; }
    /// <summary>PR đang chờ duyệt (chưa trừ vào ngân sách, chỉ để tham khảo)</summary>
    public decimal Pending { get; set; }
    public decimal Used => Ordered + Reserved;
    public decimal Available => Budget.Amount - Used;
    public decimal UsedPercent => Budget.Amount == 0 ? (Used > 0 ? 100 : 0) : Math.Round(Used * 100 / Budget.Amount, 1);
    public string Pool => Budget.Department + " · " + (Budget.Category?.Name ?? "Tất cả nhóm hàng");
}

/// <summary>Kết quả kiểm tra một PR với ngân sách (theo từng dòng ngân sách mà PR sử dụng)</summary>
public class BudgetCheckLine
{
    public string Pool { get; init; } = "";
    public BudgetStatus? Status { get; init; }
    /// <summary>Giá trị PR rơi vào dòng ngân sách này</summary>
    public decimal Request { get; init; }
    public decimal? AvailableBefore => Status?.Available;
    public decimal? AvailableAfter => Status == null ? null : Status.Available - Request;
    public bool NoBudget => Status == null;
    public bool Over => Status != null && Request > Status.Available;
}

public class BudgetCheck
{
    public int Year { get; init; }
    /// <summary>Năm đã lập ngân sách (chưa có dòng ngân sách nào cho năm → không kiểm soát)</summary>
    public bool Active { get; init; }
    public List<BudgetCheckLine> Lines { get; init; } = new();
    public bool AnyOver => Lines.Any(l => l.Over);
    public bool AnyMissing => Lines.Any(l => l.NoBudget);
    public bool Ok => !AnyOver && !AnyMissing;
}

/// <summary>
/// Kiểm soát ngân sách theo phòng ban × nhóm hàng × năm (trước VAT).
/// Mỗi dòng hàng được phân bổ vào ngân sách đúng nhóm hàng của phòng ban; không có thì vào ngân sách "tất cả nhóm hàng" của phòng ban.
/// Sử dụng = đã đặt hàng (PO) + giữ chỗ (PR đã duyệt chưa đặt hàng). PR thuộc gói thầu hợp đồng khung không giữ chỗ
/// (các đợt đặt hàng có PR riêng của từng phòng ban).
/// </summary>
public class BudgetService
{
    private readonly EProcDbContext _db;
    public BudgetService(EProcDbContext db) => _db = db;

    private static readonly PrStatus[] Reserving = { PrStatus.Approved, PrStatus.Processing };
    private static readonly PrStatus[] Waiting = { PrStatus.Submitted, PrStatus.PendingDirector };

    public async Task<List<BudgetStatus>> StatusAsync(int year, int? excludePrId = null)
    {
        var budgets = await _db.Budgets.AsNoTracking().Include(b => b.Category).Where(b => b.Year == year).ToListAsync();
        var result = budgets.Select(b => new BudgetStatus { Budget = b }).ToList();
        if (result.Count == 0) return result;
        BudgetStatus? PoolOf(string dept, int? cat) =>
            result.FirstOrDefault(s => s.Budget.Department == dept && cat != null && s.Budget.CategoryId == cat)
            ?? result.FirstOrDefault(s => s.Budget.Department == dept && s.Budget.CategoryId == null);

        // Đã đặt hàng
        var poLines = await _db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.PurchaseOrder!.Status != PoStatus.Draft && l.PurchaseOrder.Status != PoStatus.Cancelled
                        && l.PurchaseOrder.OrderDate.Year == year && l.PurchaseOrder.PurchaseRequestId != null && l.PurchaseOrder.PurchaseRequestId != excludePrId)
            .Select(l => new { Dept = l.PurchaseOrder!.PurchaseRequest!.Department, Cat = l.Item != null ? l.Item.CategoryId : null, l.Amount })
            .ToListAsync();
        foreach (var l in poLines) { var p = PoolOf(l.Dept, l.Cat); if (p != null) p.Ordered += l.Amount; }

        // Giữ chỗ + chờ duyệt
        var framework = await _db.Contracts.Where(c => c.IsFramework && c.TenderId != null).Select(c => c.Tender!.PurchaseRequestId).ToListAsync();
        var prLines = await _db.PurchaseRequestLines.AsNoTracking()
            .Where(l => l.PurchaseRequest!.RequestDate.Year == year && l.PurchaseRequestId != excludePrId
                        && (Reserving.Contains(l.PurchaseRequest.Status) || Waiting.Contains(l.PurchaseRequest.Status))
                        && !framework.Contains(l.PurchaseRequestId))
            .Select(l => new { l.PurchaseRequestId, l.PurchaseRequest!.Department, l.PurchaseRequest.Status, Cat = l.Item != null ? l.Item.CategoryId : null, l.Amount })
            .ToListAsync();
        var prIds = prLines.Where(l => Reserving.Contains(l.Status)).Select(l => l.PurchaseRequestId).Distinct().ToList();
        var orderedByPr = (await _db.PurchaseOrderLines.AsNoTracking()
                .Where(l => l.PurchaseOrder!.PurchaseRequestId != null && prIds.Contains(l.PurchaseOrder.PurchaseRequestId.Value)
                            && l.PurchaseOrder.Status != PoStatus.Draft && l.PurchaseOrder.Status != PoStatus.Cancelled)
                .Select(l => new { Pr = l.PurchaseOrder!.PurchaseRequestId!.Value, Cat = l.Item != null ? l.Item.CategoryId : null, l.Amount }).ToListAsync())
            .GroupBy(x => (x.Pr, x.Cat)).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        foreach (var g in prLines.GroupBy(l => (l.PurchaseRequestId, l.Department, l.Status, l.Cat)))
        {
            var p = PoolOf(g.Key.Department, g.Key.Cat);
            if (p == null) continue;
            var amount = g.Sum(x => x.Amount);
            if (Waiting.Contains(g.Key.Status)) p.Pending += amount;
            else p.Reserved += Math.Max(0, amount - orderedByPr.GetValueOrDefault((g.Key.PurchaseRequestId, g.Key.Cat)));
        }
        return result.OrderBy(s => s.Budget.Department).ThenBy(s => s.Budget.CategoryId == null ? 0 : 1).ThenBy(s => s.Budget.Category?.Name).ToList();
    }

    /// <summary>Kiểm tra PR (đã nạp Lines) với ngân sách năm của ngày đề nghị; PR không tự giữ chỗ cho chính nó</summary>
    public async Task<BudgetCheck> CheckAsync(PurchaseRequest pr)
    {
        var year = pr.RequestDate.Year;
        var status = await StatusAsync(year, pr.Id == 0 ? null : pr.Id);
        var itemIds = pr.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();
        var cats = await _db.Items.Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.CategoryId);
        var catNames = await _db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name);
        var lines = new List<BudgetCheckLine>();
        foreach (var g in pr.Lines.GroupBy(l => l.ItemId.HasValue ? cats.GetValueOrDefault(l.ItemId.Value) : null))
        {
            var pool = status.FirstOrDefault(s => s.Budget.Department == pr.Department && g.Key != null && s.Budget.CategoryId == g.Key)
                       ?? status.FirstOrDefault(s => s.Budget.Department == pr.Department && s.Budget.CategoryId == null);
            var label = pool?.Pool ?? $"{pr.Department} · {(g.Key is int c ? catNames.GetValueOrDefault(c, "?") : "Hàng ngoài danh mục")}";
            var existing = lines.FirstOrDefault(x => x.Status != null && x.Status == pool);
            var amount = g.Sum(l => l.Amount);
            if (existing != null) { lines.Remove(existing); amount += existing.Request; }
            lines.Add(new BudgetCheckLine { Pool = label, Status = pool, Request = amount });
        }
        return new BudgetCheck { Year = year, Active = status.Count > 0, Lines = lines };
    }

    /// <summary>Thông điệp chặn / cảnh báo (null = trong ngân sách)</summary>
    public static string? Problem(BudgetCheck c)
    {
        if (!c.Active) return null;
        var parts = new List<string>();
        foreach (var l in c.Lines.Where(l => l.Over))
            parts.Add($"{l.Pool}: cần {l.Request.ToMoney()} đ, còn {l.AvailableBefore!.Value.ToMoney()} đ");
        foreach (var l in c.Lines.Where(l => l.NoBudget))
            parts.Add($"{l.Pool}: chưa có ngân sách năm {c.Year}");
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}
