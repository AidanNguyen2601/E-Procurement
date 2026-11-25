using System.ComponentModel.DataAnnotations;
using System.Text;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class BoqForm
{
    public int Id { get; set; }
    public int PackageId { get; set; }
    public int? ParentId { get; set; }
    [Required(ErrorMessage = "Nhập tên hạng mục / công việc"), StringLength(250)][Display(Name = "Hạng mục / công việc")] public string Name { get; set; } = string.Empty;
    [StringLength(30)][Display(Name = "ĐVT")] public string? Unit { get; set; }
    [Range(0, 1_000_000_000)][Display(Name = "Khối lượng")] public decimal Quantity { get; set; }
    [Range(0, 1_000_000_000_000)][Display(Name = "Đơn giá (trước VAT)")] public decimal UnitPrice { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
}

public class BoqPage
{
    public ProcurementPackage Package { get; set; } = null!;
    public List<BoqNode> Nodes { get; set; } = new();
    public decimal Total { get; set; }
    public bool CanEdit { get; set; }
    public bool Locked { get; set; }
    public BoqForm Form { get; set; } = new();
}

/// <summary>BOQ phân cấp của gói mua sắm: hạng mục → công việc → chi tiết; nhóm tự cộng dồn; nhập / xuất văn bản; áp tổng vào giá gói</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class BoqController : ProcControllerBase
{
    public BoqController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public const string SampleText =
        "1\tHạ tầng máy chủ\t\t\t\n1.1\tMáy chủ & lưu trữ\t\t\t\n1.1.1\tMáy chủ rack 2U (2 x Xeon Silver / 256GB RAM)\tBộ\t2\t385000000\n1.1.2\tThiết bị lưu trữ NAS 4 khay\tBộ\t1\t58000000\n" +
        "1.2\tThiết bị mạng\t\t\t\n1.2.1\tSwitch 48 cổng PoE\tCái\t2\t42000000\n1.2.2\tCáp mạng Cat6 (thùng 305m)\tThùng\t6\t2850000\n" +
        "2\tPhòng máy chủ\t\t\t\n2.1\tNguồn điện & làm mát\t\t\t\n2.1.1\tBộ lưu điện UPS 3kVA online\tBộ\t2\t36500000\n2.1.2\tĐiều hòa chính xác 12.000 BTU\tBộ\t2\t74000000\n" +
        "3\tDịch vụ triển khai\t\t\t\n3.1\tLắp đặt, cấu hình, chuyển dữ liệu\tTrọn gói\t1\t95000000\n3.2\tĐào tạo vận hành\tBuổi\t3\t6000000";

    private static bool IsLocked(ProcurementPackage p) => p.Status != PackageStatus.Draft;

    public async Task<IActionResult> Index(int packageId, int? edit, int? parent)
    {
        var p = await Db.ProcurementPackages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == packageId);
        if (p == null) return NotFound();
        var items = await Db.BoqItems.AsNoTracking().Where(i => i.PackageId == packageId).ToListAsync();
        var nodes = Boq.Flatten(items);
        var page = new BoqPage
        {
            Package = p, Nodes = nodes, Total = nodes.Where(n => n.Depth == 0).Sum(n => n.Amount),
            Locked = IsLocked(p), CanEdit = !IsLocked(p) && await PackageAccess.CanEditAsync(Db, User, packageId) && User.IsPurchasing()
        };
        page.Form = new BoqForm { PackageId = packageId, ParentId = parent };
        if (edit != null && items.FirstOrDefault(i => i.Id == edit) is { } e)
            page.Form = new BoqForm { Id = e.Id, PackageId = packageId, ParentId = e.ParentId, Name = e.Name, Unit = e.Unit, Quantity = e.Quantity, UnitPrice = e.UnitPrice, Note = e.Note };
        ViewBag.Depths = Boq.MaxDepth;
        return View(page);
    }

    /// <summary>Nạp gói + kiểm tra quyền sửa; trả về lỗi (đã nhớ vào TempData) hoặc null</summary>
    private async Task<(ProcurementPackage? Pkg, IActionResult? Fail)> EditableAsync(int packageId)
    {
        var p = await Db.ProcurementPackages.FirstOrDefaultAsync(x => x.Id == packageId);
        if (p == null) return (null, NotFound());
        IActionResult Fail(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { packageId }); }
        if (!User.IsPurchasing()) return (p, Fail(I18n.T("Chỉ nhân viên mua hàng được chỉnh sửa BOQ.")));
        if (IsLocked(p)) return (p, Fail(I18n.T("Chỉ sửa BOQ khi gói ở trạng thái Nháp (chưa trình kế hoạch).")));
        if (!await PackageAccess.CanEditAsync(Db, User, packageId)) return (p, Fail(PackageAccess.Explain));
        return (p, null);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(BoqForm f)
    {
        var (p, fail) = await EditableAsync(f.PackageId);
        if (fail != null) return fail;
        IActionResult Back(string? err = null) { if (err != null) TempData["Error"] = err; return RedirectToAction(nameof(Index), new { packageId = f.PackageId, edit = err != null && f.Id > 0 ? (int?)f.Id : null, parent = err != null && f.Id == 0 ? f.ParentId : null }); }
        if (!ModelState.IsValid) return Back(ModelState.Values.SelectMany(v => v.Errors).First().ErrorMessage);
        var items = await Db.BoqItems.Where(i => i.PackageId == f.PackageId).ToListAsync();
        f.Name = f.Name.Trim();
        BoqItem it;
        if (f.Id > 0)
        {
            it = items.FirstOrDefault(i => i.Id == f.Id)!;
            if (it == null) return Back(I18n.T("Không tìm thấy dòng BOQ."));
        }
        else
        {
            if (f.ParentId != null)
            {
                var par = items.FirstOrDefault(i => i.Id == f.ParentId);
                if (par == null) return Back(I18n.T("Không tìm thấy hạng mục cha."));
                if (Boq.DepthOf(items, par.Id) + 1 >= Boq.MaxDepth) return Back(I18n.T("Vượt quá số cấp tối đa của BOQ."));
                par.Quantity = 0; par.UnitPrice = 0; // thành nhóm: thành tiền = tổng các dòng con
            }
            it = new BoqItem { PackageId = f.PackageId, ParentId = f.ParentId, SortOrder = int.MaxValue };
            Db.BoqItems.Add(it); items.Add(it);
        }
        it.Name = f.Name; it.Unit = string.IsNullOrWhiteSpace(f.Unit) ? null : f.Unit.Trim(); it.Note = string.IsNullOrWhiteSpace(f.Note) ? null : f.Note.Trim();
        var hasChildren = f.Id > 0 && items.Any(i => i.ParentId == it.Id);
        it.Quantity = hasChildren ? 0 : f.Quantity; it.UnitPrice = hasChildren ? 0 : f.UnitPrice;
        await Db.SaveChangesAsync();
        Boq.Renumber(items);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu dòng BOQ {0}.", it.Code);
        return RedirectToAction(nameof(Index), new { packageId = f.PackageId, parent = f.ParentId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var it = await Db.BoqItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (it == null) return NotFound();
        var (_, fail) = await EditableAsync(it.PackageId);
        if (fail != null) return fail;
        var items = await Db.BoqItems.Where(i => i.PackageId == it.PackageId).ToListAsync();
        var gone = Boq.Subtree(items, id);
        // xóa từ lá lên gốc để không vi phạm khóa ngoại tự tham chiếu
        foreach (var x in items.Where(i => gone.Contains(i.Id)).OrderByDescending(i => Boq.DepthOf(items, i.Id))) Db.BoqItems.Remove(x);
        await Db.SaveChangesAsync();
        var rest = items.Where(i => !gone.Contains(i.Id)).ToList();
        Boq.Renumber(rest);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa {0} dòng BOQ.", gone.Count);
        return RedirectToAction(nameof(Index), new { packageId = it.PackageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveItem(int id, Boq.MoveKind kind)
    {
        var it = await Db.BoqItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id);
        if (it == null) return NotFound();
        var (_, fail) = await EditableAsync(it.PackageId);
        if (fail != null) return fail;
        var items = await Db.BoqItems.Where(i => i.PackageId == it.PackageId).ToListAsync();
        var err = Boq.Move(items, id, kind);
        if (err != null) TempData["Error"] = I18n.T(err);
        else await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Index), new { packageId = it.PackageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(int packageId, string? text, bool replace = true, bool sample = false)
    {
        var (p, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { packageId }); }
        if (sample) text = SampleText;
        if (string.IsNullOrWhiteSpace(text)) return Back(I18n.T("Dán nội dung BOQ cần nhập (Mã, Tên, ĐVT, Khối lượng, Đơn giá)."));
        var (rows, errors) = Boq.Parse(text);
        if (errors.Count > 0)
        {
            var e = errors[0].Split('|', 3); // dòng | nội dung lỗi | giá trị (nếu có)
            return Back(I18n.T("Dòng {0}: {1}", e[0], I18n.T(e[1]) + (e.Length > 2 ? ": " + e[2] : "")) + (errors.Count > 1 ? I18n.T(" (và {0} lỗi khác)", errors.Count - 1) : ""));
        }
        if (rows.Count == 0) return Back(I18n.T("Không có dòng BOQ hợp lệ để nhập."));
        if (rows.Count > 2000) return Back(I18n.T("Tối đa 2.000 dòng mỗi lần nhập."));
        var (built, err) = Boq.Build(packageId, rows);
        if (err != null) return Back(I18n.T(err));
        var existing = await Db.BoqItems.Where(i => i.PackageId == packageId).ToListAsync();
        if (replace)
        {
            foreach (var x in existing.OrderByDescending(i => Boq.DepthOf(existing, i.Id))) Db.BoqItems.Remove(x);
            await Db.SaveChangesAsync();
            existing.Clear();
        }
        // lưu lần lượt theo thứ tự mã (cha trước con) để có Id của cha; khi nối thêm, các hạng mục gốc mới xếp sau hạng mục cũ
        var baseOrder = existing.Where(i => i.ParentId == null).Select(i => i.SortOrder).DefaultIfEmpty(0).Max();
        var byCode = new Dictionary<string, BoqItem>();
        foreach (var (item, parentCode) in built)
        {
            if (parentCode != null) item.Parent = byCode[parentCode]; else item.SortOrder += baseOrder;
            byCode[item.Code] = item; Db.BoqItems.Add(item);
        }
        await Db.SaveChangesAsync();
        var all = await Db.BoqItems.Where(i => i.PackageId == packageId).ToListAsync();
        Boq.Renumber(all);
        await Db.SaveChangesAsync();
        Log(DocType.Package, packageId, I18n.T("Nhập BOQ"), I18n.T("{0} dòng, tổng {1} đ", rows.Count, Boq.Total(all).ToMoney()));
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã nhập {0} dòng BOQ.", rows.Count);
        return RedirectToAction(nameof(Index), new { packageId });
    }

    public async Task<IActionResult> Export(int packageId)
    {
        var p = await Db.ProcurementPackages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == packageId);
        if (p == null) return NotFound();
        var nodes = Boq.Flatten(await Db.BoqItems.AsNoTracking().Where(i => i.PackageId == packageId).ToListAsync());
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Boq.ToCsv(nodes))).ToArray();
        return File(bytes, "text/csv; charset=utf-8", $"BOQ-{p.Number}.csv");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyBudget(int packageId)
    {
        var (p, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        var total = Boq.Total(await Db.BoqItems.AsNoTracking().Where(i => i.PackageId == packageId).ToListAsync());
        if (total <= 0) { TempData["Error"] = I18n.T("BOQ chưa có giá trị để áp vào giá gói."); return RedirectToAction(nameof(Index), new { packageId }); }
        var old = p!.Budget;
        p.Budget = total;
        Log(DocType.Package, packageId, I18n.T("Áp tổng BOQ vào giá gói"), $"{old.ToMoney()} → {total.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Giá gói đã cập nhật theo tổng BOQ: {0} đ.", total.ToMoney());
        return RedirectToAction(nameof(Index), new { packageId });
    }
}
