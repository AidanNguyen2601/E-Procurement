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
    [Range(0, 1_000_000_000_000)][Display(Name = "Vật liệu")] public decimal MaterialPrice { get; set; }
    [Range(0, 1_000_000_000_000)][Display(Name = "Nhân công")] public decimal LaborPrice { get; set; }
    [Range(0, 1_000_000_000_000)][Display(Name = "Máy thi công")] public decimal MachinePrice { get; set; }
    [StringLength(500)][Display(Name = "Ghi chú")] public string? Note { get; set; }
}

public class BoqPage
{
    public ProcurementPackage Package { get; set; } = null!;
    public List<BoqNode> Nodes { get; set; } = new();
    public decimal Total { get; set; }
    public BoqCostSummary Cost { get; set; } = null!;
    public List<BoqVersion> Versions { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool Locked { get; set; }
    public BoqForm Form { get; set; } = new();
}

public class BoqCompareModel
{
    public ProcurementPackage Package { get; set; } = null!;
    public BoqVersion Version { get; set; } = null!;
    public List<BoqDiffRow> Rows { get; set; } = new();
    public decimal OldTotal { get; set; }
    public decimal NewTotal { get; set; }
    public bool CanEdit { get; set; }
}

/// <summary>BOQ phân cấp của gói mua sắm: hạng mục → công việc → chi tiết; cơ cấu giá, chi phí chung / lợi nhuận / dự phòng / VAT; nhập (văn bản, CSV, xlsx) / xuất; phiên bản</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public partial class BoqController : ProcControllerBase
{
    public BoqController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public const long MaxFileBytes = 5 * 1024 * 1024;

    public const string SampleText =
        "1\tHạ tầng máy chủ\t\t\t\n1.1\tMáy chủ & lưu trữ\t\t\t\n1.1.1\tMáy chủ rack 2U (2 x Xeon Silver / 256GB RAM)\tBộ\t2\t385000000\n1.1.2\tThiết bị lưu trữ NAS 4 khay\tBộ\t1\t58000000\n" +
        "1.2\tThiết bị mạng\t\t\t\n1.2.1\tSwitch 48 cổng PoE\tCái\t2\t42000000\n1.2.2\tCáp mạng Cat6 (thùng 305m)\tThùng\t6\t2850000\n" +
        "2\tPhòng máy chủ\t\t\t\n2.1\tNguồn điện & làm mát\t\t\t\n2.1.1\tBộ lưu điện UPS 3kVA online\tBộ\t2\t36500000\n2.1.2\tĐiều hòa chính xác 12.000 BTU\tBộ\t2\t74000000\n" +
        "3\tDịch vụ triển khai\t\t\t\n3.1\tLắp đặt, cấu hình, chuyển dữ liệu\tTrọn gói\t1\t95000000\n3.2\tĐào tạo vận hành\tBuổi\t3\t6000000";

    /// <summary>BOQ mẫu xây dựng có cơ cấu giá: Mã ; Tên ; ĐVT ; KL ; Đơn giá ; (Thành tiền) ; Vật liệu ; Nhân công ; Máy</summary>
    public const string SampleConstructionText =
        "1\tPhần móng\t\t\t\t\t\t\t\n1.1\tĐào đất hố móng\t\t\t\t\t\t\t\n1.1.1\tĐào móng bằng máy đào\tm3\t320\t\t\t0\t18000\t42000\n1.1.2\tĐắp đất hoàn trả\tm3\t90\t\t\t12000\t35000\t18000\n" +
        "1.2\tBê tông móng\t\t\t\t\t\t\t\n1.2.1\tBê tông lót M100 đá 4x6\tm3\t24\t\t\t985000\t210000\t55000\n1.2.2\tBê tông móng M250 đá 1x2\tm3\t96\t\t\t1320000\t260000\t78000\n1.2.3\tCốt thép móng D≤18\ttấn\t7.5\t\t\t15200000\t2800000\t450000\n" +
        "2\tPhần thân\t\t\t\t\t\t\t\n2.1\tXây tường gạch\t\t\t\t\t\t\t\n2.1.1\tXây tường gạch 220 vữa XM M75\tm3\t140\t\t\t1250000\t520000\t35000\n2.1.2\tTô trát tường trong vữa XM M75 dày 15\tm2\t980\t\t\t58000\t72000\t6000\n" +
        "3\tHoàn thiện\t\t\t\t\t\t\t\n3.1\tSơn nước nội thất 1 nước lót 2 nước phủ\tm2\t980\t\t\t42000\t26000\t3000";

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
            Cost = BoqCost.Summary(items, p.BoqOverheadPercent, p.BoqProfitPercent, p.BoqContingencyPercent, p.BoqVatPercent),
            Versions = await Db.BoqVersions.AsNoTracking().Include(v => v.CreatedBy).Where(v => v.PackageId == packageId).OrderByDescending(v => v.Number).Take(30).Select(v => new BoqVersion { Id = v.Id, Number = v.Number, Label = v.Label, ItemCount = v.ItemCount, Total = v.Total, CreatedAt = v.CreatedAt, CreatedBy = v.CreatedBy }).ToListAsync(),
            Locked = IsLocked(p), CanEdit = !IsLocked(p) && await PackageAccess.CanEditAsync(Db, User, packageId) && User.IsPurchasing()
        };
        page.Form = new BoqForm { PackageId = packageId, ParentId = parent };
        if (edit != null && items.FirstOrDefault(i => i.Id == edit) is { } e)
            page.Form = new BoqForm { Id = e.Id, PackageId = packageId, ParentId = e.ParentId, Name = e.Name, Unit = e.Unit, Quantity = e.Quantity, UnitPrice = e.UnitPrice, MaterialPrice = e.MaterialPrice, LaborPrice = e.LaborPrice, MachinePrice = e.MachinePrice, Note = e.Note };
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
                par.Quantity = 0; par.UnitPrice = 0; par.MaterialPrice = 0; par.LaborPrice = 0; par.MachinePrice = 0; // thành nhóm: thành tiền = tổng các dòng con
            }
            it = new BoqItem { PackageId = f.PackageId, ParentId = f.ParentId, SortOrder = int.MaxValue };
            Db.BoqItems.Add(it); items.Add(it);
        }
        it.Name = f.Name; it.Unit = string.IsNullOrWhiteSpace(f.Unit) ? null : f.Unit.Trim(); it.Note = string.IsNullOrWhiteSpace(f.Note) ? null : f.Note.Trim();
        var hasChildren = f.Id > 0 && items.Any(i => i.ParentId == it.Id);
        it.Quantity = hasChildren ? 0 : f.Quantity;
        it.MaterialPrice = hasChildren ? 0 : f.MaterialPrice; it.LaborPrice = hasChildren ? 0 : f.LaborPrice; it.MachinePrice = hasChildren ? 0 : f.MachinePrice;
        it.UnitPrice = hasChildren ? 0 : BoqCost.PriceFromComponents(f.UnitPrice, f.MaterialPrice, f.LaborPrice, f.MachinePrice);
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

    // ------------------------------------------------------------------ Nhập
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Import(int packageId, string? text, bool replace = true, string? sample = null)
    {
        var (p, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { packageId }); }
        if (sample == "construction") text = SampleConstructionText; else if (sample != null) text = SampleText;
        if (string.IsNullOrWhiteSpace(text)) return Back(I18n.T("Dán nội dung BOQ cần nhập (Mã, Tên, ĐVT, Khối lượng, Đơn giá)."));
        var (rows, errors) = Boq.Parse(text);
        return await ApplyImportAsync(packageId, rows, errors, replace, sample != null ? I18n.T("BOQ mẫu") : I18n.T("văn bản dán"));
    }

    /// <summary>Nhập từ file .xlsx (sheet đầu) hoặc .csv / .txt: cùng cột như nhập văn bản</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportFile(int packageId, IFormFile? file, bool replace = true)
    {
        var (p, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { packageId }); }
        if (file == null || file.Length == 0) return Back(I18n.T("Chọn file BOQ (.xlsx, .csv hoặc .txt)."));
        if (file.Length > MaxFileBytes) return Back(I18n.T("File tối đa {0} MB.", MaxFileBytes / 1024 / 1024));
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        List<Boq.ImportRow> rows; List<string> errors;
        if (ext == ".xlsx")
        {
            try
            {
                await using var ms = new MemoryStream(); await file.CopyToAsync(ms); ms.Position = 0;
                (rows, errors) = Boq.ParseColumns(XlsxReader.Read(ms), invariantNumbers: true);
            }
            catch (InvalidDataException) { return Back(I18n.T("File .xlsx không đọc được hoặc quá lớn (tối đa {0} dòng).", XlsxReader.MaxRows)); }
        }
        else if (ext is ".csv" or ".txt")
        {
            using var sr = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false), true);
            (rows, errors) = Boq.Parse(await sr.ReadToEndAsync());
        }
        else return Back(I18n.T("Chỉ nhận file .xlsx, .csv hoặc .txt."));
        return await ApplyImportAsync(packageId, rows, errors, replace, file.FileName);
    }

    private async Task<IActionResult> ApplyImportAsync(int packageId, List<Boq.ImportRow> rows, List<string> errors, bool replace, string source)
    {
        IActionResult Back(string m) { TempData["Error"] = m; return RedirectToAction(nameof(Index), new { packageId }); }
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
        if (existing.Count > 0) { await BoqVersions.SnapshotAsync(Db, packageId, I18n.T("Trước khi nhập ({0})", source), Me); await Db.SaveChangesAsync(); }
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

    // ------------------------------------------------------------------ Cơ cấu chi phí
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCostSettings(int packageId, decimal overhead, decimal profit, decimal contingency, decimal vat)
    {
        var (p, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        if (new[] { overhead, profit, contingency, vat }.Any(x => x < 0 || x > 100)) { TempData["Error"] = I18n.T("Các tỷ lệ phải từ 0 đến 100%."); return RedirectToAction(nameof(Index), new { packageId }); }
        p!.BoqOverheadPercent = Math.Round(overhead, 2); p.BoqProfitPercent = Math.Round(profit, 2); p.BoqContingencyPercent = Math.Round(contingency, 2); p.BoqVatPercent = Math.Round(vat, 2);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu cơ cấu chi phí.");
        return RedirectToAction(nameof(Index), new { packageId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyBudget(int packageId)
    {
        var (p, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        var items = await Db.BoqItems.AsNoTracking().Where(i => i.PackageId == packageId).ToListAsync();
        var total = BoqCost.Summary(items, p!.BoqOverheadPercent, p.BoqProfitPercent, p.BoqContingencyPercent, p.BoqVatPercent).TotalExVat;
        if (total <= 0) { TempData["Error"] = I18n.T("BOQ chưa có giá trị để áp vào giá gói."); return RedirectToAction(nameof(Index), new { packageId }); }
        var old = p.Budget;
        p.Budget = total;
        Log(DocType.Package, packageId, I18n.T("Áp tổng BOQ vào giá gói"), $"{old.ToMoney()} → {total.ToMoney()} đ");
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Giá gói đã cập nhật theo tổng BOQ: {0} đ.", total.ToMoney());
        return RedirectToAction(nameof(Index), new { packageId });
    }

    // ------------------------------------------------------------------ Phiên bản
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SnapshotVersion(int packageId, string? label)
    {
        var (_, fail) = await EditableAsync(packageId);
        if (fail != null) return fail;
        var v = await BoqVersions.SnapshotAsync(Db, packageId, string.IsNullOrWhiteSpace(label) ? I18n.T("Chụp thủ công") : label.Trim(), Me);
        if (v == null) { TempData["Error"] = I18n.T("BOQ chưa có dòng nào để lưu phiên bản."); return RedirectToAction(nameof(Index), new { packageId }); }
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã lưu phiên bản {0}.", v.Number);
        return RedirectToAction(nameof(Index), new { packageId });
    }

    /// <summary>So sánh một phiên bản đã lưu với BOQ hiện tại</summary>
    public async Task<IActionResult> Compare(int versionId)
    {
        var v = await Db.BoqVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId);
        if (v == null) return NotFound();
        var p = await Db.ProcurementPackages.AsNoTracking().FirstAsync(x => x.Id == v.PackageId);
        var items = await Db.BoqItems.AsNoTracking().Where(i => i.PackageId == v.PackageId).ToListAsync();
        var current = Boq.Flatten(items).Select(n => new BoqSnapItem(n.Code, n.Item.Name, n.Item.Unit, n.Item.Quantity, n.Item.UnitPrice, n.Item.MaterialPrice, n.Item.LaborPrice, n.Item.MachinePrice, n.Item.Note)).ToList();
        var old = BoqCost.FromSnapshot(v.Snapshot);
        return View(new BoqCompareModel
        {
            Package = p, Version = v, Rows = BoqCost.Diff(old, current), OldTotal = v.Total, NewTotal = Boq.Total(items),
            CanEdit = !IsLocked(p) && User.IsPurchasing() && await PackageAccess.CanEditAsync(Db, User, p.Id)
        });
    }

    /// <summary>Khôi phục phiên bản: BOQ hiện tại được chụp lại trước rồi thay bằng nội dung phiên bản</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int versionId)
    {
        var v = await Db.BoqVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId);
        if (v == null) return NotFound();
        var (_, fail) = await EditableAsync(v.PackageId);
        if (fail != null) return fail;
        var snap = BoqCost.FromSnapshot(v.Snapshot);
        var (built, err) = Boq.Build(v.PackageId, snap.Select(s => new Boq.ImportRow(s.Code, s.Name, s.Unit, s.Quantity, s.UnitPrice, s.Material, s.Labor, s.Machine, s.Note)));
        if (err != null || built.Count == 0) { TempData["Error"] = I18n.T("Phiên bản không khôi phục được."); return RedirectToAction(nameof(Index), new { packageId = v.PackageId }); }
        var existing = await Db.BoqItems.Where(i => i.PackageId == v.PackageId).ToListAsync();
        if (existing.Count > 0) { await BoqVersions.SnapshotAsync(Db, v.PackageId, I18n.T("Trước khi khôi phục phiên bản {0}", v.Number), Me); await Db.SaveChangesAsync(); }
        foreach (var x in existing.OrderByDescending(i => Boq.DepthOf(existing, i.Id))) Db.BoqItems.Remove(x);
        await Db.SaveChangesAsync();
        var byCode = new Dictionary<string, BoqItem>();
        foreach (var (item, parentCode) in built) { if (parentCode != null) item.Parent = byCode[parentCode]; byCode[item.Code] = item; Db.BoqItems.Add(item); }
        await Db.SaveChangesAsync();
        var all = await Db.BoqItems.Where(i => i.PackageId == v.PackageId).ToListAsync();
        Boq.Renumber(all);
        Log(DocType.Package, v.PackageId, I18n.T("Khôi phục BOQ phiên bản {0}", v.Number), v.Label);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã khôi phục phiên bản {0}.", v.Number);
        return RedirectToAction(nameof(Index), new { packageId = v.PackageId });
    }
}
