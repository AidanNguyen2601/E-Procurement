using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Infrastructure.Reporting;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Báo cáo chuẩn về mua sắm: xem, lọc, in, xuất Excel</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director + "," + AppRoles.Accountant)]
public class ReportsController : ProcControllerBase
{
    private readonly StandardReports _reports;
    public ReportsController(EProcDbContext db, IOptions<ProcurementSettings> s, StandardReports reports) : base(db, s) => _reports = reports;

    public IActionResult Index() => View(StandardReports.All);

    public async Task<IActionResult> Show(string id, ReportFilter f)
    {
        if (StandardReports.All.All(r => r.Key != id)) return NotFound();
        if (f.To < f.From) (f.From, f.To) = (f.To, f.From);
        var result = await _reports.BuildAsync(id, f);
        await LoadFiltersAsync(f);
        ViewBag.Filter = f;
        return View(result);
    }

    public async Task<IActionResult> Export(string id, ReportFilter f)
    {
        if (StandardReports.All.All(r => r.Key != id)) return NotFound();
        if (f.To < f.From) (f.From, f.To) = (f.To, f.From);
        var result = await _reports.BuildAsync(id, f);
        var (cats, vendors) = await LookupsAsync();
        var kpis = new ReportTable { Title = "Chỉ tiêu", Columns = { new("Chỉ tiêu"), new("Giá trị"), new("Ghi chú") },
            Rows = result.Kpis.Select(k => new object?[] { k.Label, k.Value, k.Hint }).ToList() };
        var bytes = XlsxWriter.Write(result.Title, f.Describe(cats, vendors), new[] { kpis }.Concat(result.Tables));
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"bao-cao-{id}-{f.From:yyyyMMdd}-{f.To:yyyyMMdd}.xlsx");
    }

    private async Task<(Dictionary<int, string>, Dictionary<int, string>)> LookupsAsync() =>
        (await Db.Categories.ToDictionaryAsync(c => c.Id, c => c.Name), await Db.Vendors.ToDictionaryAsync(v => v.Id, v => v.Name));

    private async Task LoadFiltersAsync(ReportFilter f)
    {
        var (cats, vendors) = await LookupsAsync();
        ViewBag.FilterText = f.Describe(cats, vendors);
        var depts = await Db.CatalogEntries.Where(e => e.Type == CatalogType.Department).OrderBy(e => e.SortOrder).ThenBy(e => e.Name).Select(e => e.Name).ToListAsync();
        depts.AddRange((await Db.PurchaseRequests.Select(p => p.Department).Distinct().ToListAsync()).Except(depts, StringComparer.OrdinalIgnoreCase).OrderBy(d => d));
        ViewBag.Departments = new SelectList(depts, f.Department);
        ViewBag.Categories = new SelectList(cats.OrderBy(c => c.Value), "Key", "Value", f.CategoryId);
        ViewBag.Vendors = new SelectList(vendors.OrderBy(v => v.Value), "Key", "Value", f.VendorId);
    }
}
