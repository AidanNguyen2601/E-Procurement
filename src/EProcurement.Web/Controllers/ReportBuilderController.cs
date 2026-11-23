using System.Text;
using System.Text.Json;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Infrastructure.Reporting;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;


/// <summary>
/// Báo cáo động: thiết kế (chọn nguồn, cột, tổng hợp, lọc, sắp xếp, biểu đồ) → chạy → xuất Excel / xuất SQL;
/// lưu thiết kế để dùng lại, chia sẻ, xuất / nhập thiết kế dạng JSON.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class ReportBuilderController : ProcControllerBase
{
    private const string DesignFileType = "eproc-report-design";
    public ReportBuilderController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index() =>
        View(await Db.ReportDefinitions.Include(r => r.Owner).AsNoTracking()
            .Where(r => r.OwnerId == Me || r.IsShared).OrderByDescending(r => r.UpdatedAt).ToListAsync());

    /// <summary>Mở trình thiết kế: báo cáo mới (chọn sẵn nguồn) hoặc báo cáo đã lưu (run = chạy luôn)</summary>
    public async Task<IActionResult> Designer(int? id, string? dataset, bool run = false)
    {
        var vm = new DesignerViewModel();
        if (id.HasValue)
        {
            var def = await Db.ReportDefinitions.FirstOrDefaultAsync(r => r.Id == id && (r.OwnerId == Me || r.IsShared));
            if (def == null) return NotFound();
            var design = DynamicReports.ParseJson(def.DesignJson);
            if (design == null) { TempData["Error"] = "Thiết kế đã lưu bị lỗi định dạng."; return RedirectToAction(nameof(Index)); }
            vm = new DesignerViewModel { Id = def.Id, Name = def.Name, Description = def.Description, IsShared = def.IsShared, CanEdit = CanEdit(def), Design = design };
            if (run) await RunInto(vm);
        }
        else
        {
            var ds = DynamicReports.Dataset(dataset ?? "") ?? DynamicReports.Datasets[0];
            vm.Design = DefaultDesign(ds);
        }
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Run(DesignerForm form)
    {
        var vm = await FromFormAsync(form);
        if (vm.Errors.Count == 0) await RunInto(vm);
        return View(nameof(Designer), vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportExcel(DesignerForm form)
    {
        var vm = await FromFormAsync(form);
        if (vm.Errors.Count > 0) return View(nameof(Designer), vm);
        var r = await DynamicReports.RunAsync(Db.Database.GetDbConnection(), vm.Design);
        var title = string.IsNullOrWhiteSpace(vm.Name) ? "Báo cáo động" : vm.Name;
        var ds = DynamicReports.Dataset(vm.Design.Dataset)!;
        var table = new ReportTable { Title = title, Columns = r.Columns, Rows = r.Rows, Note = r.Truncated ? $"Đã giới hạn {vm.Design.Top} dòng đầu." : null };
        var bytes = XlsxWriter.Write(title, $"Nguồn: {ds.Label} · xuất lúc {DateTime.Now:dd/MM/yyyy HH:mm}", new[] { table });
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", FileName(title, "xlsx"));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportSql(DesignerForm form)
    {
        var vm = await FromFormAsync(form);
        if (vm.Errors.Count > 0) return View(nameof(Designer), vm);
        var sql = DynamicReports.ExportSql(vm.Design, vm.Name);
        return File(new UTF8Encoding(true).GetBytes(sql), "application/sql", FileName(string.IsNullOrWhiteSpace(vm.Name) ? "bao-cao" : vm.Name, "sql"));
    }

    /// <summary>Xuất thiết kế ra file JSON (để sao lưu / chuyển sang hệ thống khác / nhập lại)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportDesign(DesignerForm form)
    {
        var vm = await FromFormAsync(form);
        if (vm.Errors.Count > 0) return View(nameof(Designer), vm);
        var json = JsonSerializer.Serialize(new DesignFile { Type = DesignFileType, Name = vm.Name, Description = vm.Description, Design = vm.Design }, DynamicReports.FileJson);
        return File(new UTF8Encoding(false).GetBytes(json), "application/json", FileName(string.IsNullOrWhiteSpace(vm.Name) ? "thiet-ke-bao-cao" : vm.Name, "json"));
    }

    /// <summary>Lưu thiết kế: chủ sở hữu ghi đè; người khác (báo cáo chia sẻ) → lưu thành bản sao của mình</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(DesignerForm form, bool asCopy = false)
    {
        var vm = await FromFormAsync(form);
        if (string.IsNullOrWhiteSpace(vm.Name)) vm.Errors.Add("Đặt tên báo cáo trước khi lưu.");
        if (vm.Name.Length > 150) vm.Errors.Add("Tên báo cáo tối đa 150 ký tự.");
        if (vm.Errors.Count > 0) return View(nameof(Designer), vm);
        ReportDefinition? def = null;
        if (vm.Id.HasValue && !asCopy)
        {
            def = await Db.ReportDefinitions.FirstOrDefaultAsync(r => r.Id == vm.Id);
            if (def != null && !CanEdit(def)) def = null; // không phải chủ sở hữu → lưu bản sao
        }
        if (def == null) { def = new ReportDefinition { OwnerId = Me, CreatedAt = DateTime.Now }; Db.ReportDefinitions.Add(def); }
        def.Name = vm.Name.Trim(); def.Description = string.IsNullOrWhiteSpace(vm.Description) ? null : vm.Description.Trim()[..Math.Min(500, vm.Description.Trim().Length)];
        def.IsShared = vm.IsShared; def.Dataset = vm.Design.Dataset; def.DesignJson = JsonSerializer.Serialize(vm.Design, DynamicReports.Json); def.UpdatedAt = DateTime.Now;
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã lưu báo cáo \"{def.Name}\".";
        return RedirectToAction(nameof(Designer), new { id = def.Id, run = true });
    }

    /// <summary>Nhập thiết kế từ file JSON (đã xuất trước đó) – mở trong trình thiết kế, chưa lưu</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(1024 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file)
    {
        if (file == null || file.Length == 0 || file.Length > 512 * 1024) { TempData["Error"] = "Chọn file thiết kế (.json, tối đa 512 KB)."; return RedirectToAction(nameof(Index)); }
        string json;
        using (var r = new StreamReader(file.OpenReadStream(), Encoding.UTF8)) json = await r.ReadToEndAsync();
        DesignFile? df = null;
        try { df = JsonSerializer.Deserialize<DesignFile>(json, DynamicReports.Json); } catch (JsonException) { }
        if (df?.Design == null || df.Type != DesignFileType) { TempData["Error"] = "File không phải thiết kế báo cáo E-Procurement."; return RedirectToAction(nameof(Index)); }
        var vm = new DesignerViewModel { Name = df.Name ?? "", Description = df.Description, Design = df.Design };
        vm.Errors = DynamicReports.Validate(vm.Design);
        if (vm.Errors.Count == 0) await RunInto(vm);
        TempData["Success"] = "Đã nhập thiết kế. Bấm \"Lưu thiết kế\" để lưu vào danh sách báo cáo.";
        return View(nameof(Designer), vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var def = await Db.ReportDefinitions.FindAsync(id);
        if (def == null) return NotFound();
        if (!CanEdit(def)) return Forbid();
        Db.ReportDefinitions.Remove(def);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã xóa báo cáo \"{def.Name}\".";
        return RedirectToAction(nameof(Index));
    }

    // ------------------------------------------------------------------
    public class DesignerForm
    {
        public int? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool IsShared { get; set; }
        public string? DesignJson { get; set; }
    }

    private class DesignFile
    {
        public string? Type { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public ReportDesign? Design { get; set; }
    }

    private bool CanEdit(ReportDefinition d) => d.OwnerId == Me || User.IsAdmin();

    private async Task<DesignerViewModel> FromFormAsync(DesignerForm form)
    {
        var vm = new DesignerViewModel { Id = form.Id, Name = form.Name?.Trim() ?? "", Description = form.Description, IsShared = form.IsShared };
        if (form.Id.HasValue)
        {
            var def = await Db.ReportDefinitions.AsNoTracking().FirstOrDefaultAsync(r => r.Id == form.Id && (r.OwnerId == Me || r.IsShared));
            if (def == null) vm.Id = null; else vm.CanEdit = CanEdit(def);
        }
        var design = string.IsNullOrWhiteSpace(form.DesignJson) ? null : DynamicReports.ParseJson(form.DesignJson);
        if (design == null) { vm.Errors.Add("Thiết kế không hợp lệ."); vm.Design = DefaultDesign(DynamicReports.Datasets[0]); return vm; }
        vm.Design = design;
        vm.Errors = DynamicReports.Validate(design);
        return vm;
    }

    private async Task RunInto(DesignerViewModel vm)
    {
        try
        {
            vm.Result = await DynamicReports.RunAsync(Db.Database.GetDbConnection(), vm.Design);
            vm.Sql = DynamicReports.ExportSql(vm.Design, vm.Name);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex)
        {
            vm.Errors.Add("Lỗi khi chạy báo cáo: " + ex.Message);
        }
    }

    private static ReportDesign DefaultDesign(DatasetDef ds)
    {
        var d = new ReportDesign { Dataset = ds.Key };
        var dim = ds.Fields.FirstOrDefault(f => f.Type == FieldType.Text) ?? ds.Fields[0];
        var measure = ds.Fields.FirstOrDefault(f => f.Type == FieldType.Money);
        d.Columns.Add(new DesignColumn { Field = dim.Key });
        if (measure != null)
        {
            d.Columns.Add(new DesignColumn { Field = measure.Key, Agg = "sum" });
            d.Sorts.Add(new DesignSort { Column = 1, Desc = true });
            d.Chart = "bar"; d.ChartLabel = 0; d.ChartValue = 1;
        }
        else d.Columns.Add(new DesignColumn { Field = dim.Key, Agg = "count", Label = "Số lượng" });
        return d;
    }

    private static string FileName(string title, string ext)
    {
        var name = new string(title.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (name.Contains("--")) name = name.Replace("--", "-");
        return (name.Length == 0 ? "bao-cao" : name[..Math.Min(60, name.Length)]) + "." + ext;
    }
}
