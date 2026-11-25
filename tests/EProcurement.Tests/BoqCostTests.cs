using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using EProcurement.Web.Controllers;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

public class BoqCostTests
{
    private static BoqItem I(int id, int? parent, int order, string name, decimal q = 0, decimal p = 0, decimal m = 0, decimal l = 0, decimal mc = 0) =>
        new() { Id = id, PackageId = 1, ParentId = parent, SortOrder = order, Name = name, Quantity = q, UnitPrice = p, MaterialPrice = m, LaborPrice = l, MachinePrice = mc };

    [Fact]
    public void Summary_applies_overhead_profit_contingency_and_vat_in_order()
    {
        var items = new List<BoqItem> { I(1, null, 1, "A", 10, 1000, 600, 300, 100), I(2, null, 2, "B", 5, 200) };  // 10.000 + 1.000 = 11.000
        var s = BoqCost.Summary(items, overheadPct: 10, profitPct: 5, contingencyPct: 2, vatPct: 10);
        Assert.Equal(11_000m, s.Direct);
        Assert.Equal(6_000m, s.Material); Assert.Equal(3_000m, s.Labor); Assert.Equal(1_000m, s.Machine); Assert.Equal(1_000m, s.Other);
        Assert.Equal(1_100m, s.Overhead);                       // 10% × 11.000
        Assert.Equal(605m, s.Profit);                           // 5% × (11.000 + 1.100)
        Assert.Equal(12_705m, s.Subtotal);
        Assert.Equal(254m, s.Contingency);                      // 2% × 12.705 = 254,1
        Assert.Equal(12_959m, s.TotalExVat);
        Assert.Equal(1_296m, s.Vat);                            // 10% × 12.959 = 1.295,9
        Assert.Equal(14_255m, s.Grand);
    }

    [Fact]
    public void Summary_with_zero_percentages_equals_direct_cost()
    {
        var s = BoqCost.Summary(new List<BoqItem> { I(1, null, 1, "A", 2, 500) }, 0, 0, 0, 0);
        Assert.Equal(1_000m, s.TotalExVat); Assert.Equal(1_000m, s.Grand);
    }

    [Fact]
    public void Price_from_components_wins_only_when_entered()
    {
        Assert.Equal(1_000m, BoqCost.PriceFromComponents(0, 600, 300, 100));
        Assert.Equal(777m, BoqCost.PriceFromComponents(777, 0, 0, 0));
    }

    [Fact]
    public void Snapshot_round_trips_structure_and_total()
    {
        var items = new List<BoqItem> { I(1, null, 1, "Phần móng"), I(2, 1, 1, "Bê tông", 12.345m, 900_000, 700_000, 150_000, 50_000), I(3, null, 2, "Hoàn thiện", 1, 5_000_000) };
        var json = BoqCost.ToSnapshot(items);
        var snap = BoqCost.FromSnapshot(json);
        Assert.Equal(new[] { "1", "1.1", "2" }, snap.Select(s => s.Code));
        Assert.Equal(Boq.Total(items), BoqCost.SnapshotTotal(1, snap));
        var back = BoqCost.ToItems(1, snap);
        Assert.Equal(700_000m, back.First(b => b.Name == "Bê tông").MaterialPrice);
        Assert.Equal(12.345m, back.First(b => b.Name == "Bê tông").Quantity);
        Assert.Empty(BoqCost.FromSnapshot("không phải json"));
    }

    [Fact]
    public void Diff_lists_added_removed_and_changed_leaf_lines_sorted_numerically()
    {
        BoqSnapItem S(string code, string name, decimal q, decimal p) => new(code, name, "m3", q, p, 0, 0, 0, null);
        var oldSnap = new[] { S("1", "Nhóm", 0, 0), S("1.1", "Bê tông", 10, 1000), S("1.2", "Thép", 5, 200), S("1.9", "Cát", 1, 100) };
        var newSnap = new[] { S("1", "Nhóm", 0, 0), S("1.1", "Bê tông", 12, 1000), S("1.2", "Thép", 5, 200), S("1.10", "Đá", 2, 50) };
        var d = BoqCost.Diff(oldSnap, newSnap);
        Assert.Equal(new[] { "1.1", "1.9", "1.10" }, d.Select(x => x.Code));           // 1.10 sau 1.9
        Assert.Equal(BoqDiffKind.Changed, d[0].Kind); Assert.Equal(2_000m, d[0].NewAmount - d[0].OldAmount);
        Assert.Equal(BoqDiffKind.Removed, d[1].Kind);
        Assert.Equal(BoqDiffKind.Added, d[2].Kind);
        Assert.Empty(BoqCost.Diff(oldSnap, oldSnap));
    }

    [Fact]
    public void Parse_reads_cost_breakdown_columns_and_sums_into_unit_price()
    {
        var (rows, errors) = Boq.Parse("1;Móng;;;;;;;\n1.1;Bê tông;m3;10;;;700000;200000;100000\n1.2;Cốt thép;tấn;2;15000000;;;;");
        Assert.Empty(errors);
        Assert.Equal(1_000_000m, rows[1].UnitPrice); Assert.Equal(700_000m, rows[1].Material); Assert.Equal(200_000m, rows[1].Labor); Assert.Equal(100_000m, rows[1].Machine);
        Assert.Equal(15_000_000m, rows[2].UnitPrice); Assert.Equal(0m, rows[2].Material);
    }

    [Fact]
    public void Csv_export_includes_breakdown_and_round_trips()
    {
        var items = new List<BoqItem> { I(1, null, 1, "Móng"), I(2, 1, 1, "Bê tông", 10, 1_000_000, 700_000, 200_000, 100_000) };
        var (rows, errors) = Boq.Parse(Boq.ToCsv(Boq.Flatten(items)));
        Assert.Empty(errors);
        Assert.Equal(700_000m, rows[1].Material); Assert.Equal(1_000_000m, rows[1].UnitPrice);
    }

    // ----- xlsx -----
    private static byte[] MakeXlsx(params string[] rowsXml)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string name, string content) { using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); w.Write(content); }
            Add("xl/sharedStrings.xml", "<?xml version=\"1.0\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Mã</t></si><si><t>Hạng mục</t></si><si><t>Móng</t></si><si><r><t>Bê </t></r><r><t>tông</t></r></si><si><t>m3</t></si></sst>");
            Add("xl/worksheets/sheet1.xml", "<?xml version=\"1.0\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" + string.Concat(rowsXml) + "</sheetData></worksheet>");
        }
        return ms.ToArray();
    }

    [Fact]
    public void Xlsx_reader_reads_shared_strings_numbers_and_gaps()
    {
        var bytes = MakeXlsx(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c></row>",
            "<row r=\"2\"><c r=\"A2\"><v>1</v></c><c r=\"B2\" t=\"s\"><v>2</v></c></row>",
            "<row r=\"3\"><c r=\"A3\"><v>1.1</v></c><c r=\"B3\" t=\"s\"><v>3</v></c><c r=\"C3\" t=\"s\"><v>4</v></c><c r=\"D3\"><v>12.345</v></c><c r=\"F3\"><v>900000</v></c></row>");
        var lines = XlsxReader.Read(new MemoryStream(bytes));
        Assert.Equal(3, lines.Count);
        Assert.Equal("Bê tông", lines[2].Cols[1]);
        Assert.Equal("12.345", lines[2].Cols[3]);
        Assert.Equal("", lines[2].Cols[4]);          // ô trống giữa các cột
        Assert.Equal("900000", lines[2].Cols[5]);
        // số đọc theo kiểu quốc tế: 12.345 là 12,345 chứ không phải 12.345 (hàng nghìn)
        var (rows, errors) = Boq.ParseColumns(lines, invariantNumbers: true);
        Assert.Empty(errors);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1.1", rows[1].Code); Assert.Equal(12.345m, rows[1].Quantity);
    }

    [Fact]
    public void Xlsx_reader_rejects_non_zip_and_dtd()
    {
        Assert.Throws<InvalidDataException>(() => XlsxReader.Read(new MemoryStream(Encoding.UTF8.GetBytes("không phải xlsx"))));
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open());
            w.Write("<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><worksheet><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&e;</t></is></c></row></sheetData></worksheet>");
        }
        Assert.Throws<InvalidDataException>(() => XlsxReader.Read(new MemoryStream(ms.ToArray())));
    }

    // ----- controller: phiên bản -----
    private class NoTempData : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private static BoqController As(EProcDbContext db, int userId)
    {
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Role, AppRoles.Buyer) }, "t")) };
        return new BoqController(db, TestDb.Settings()) { ControllerContext = new ControllerContext { HttpContext = http }, TempData = new TempDataDictionary(http, new NoTempData()) };
    }

    [Fact]
    public async Task Import_replace_snapshots_first_and_restore_brings_back_the_old_boq()
    {
        var db = TestDb.Create();
        var (buyer, _, _, _, _, _) = TestDb.Seed(db);
        var pkg = new ProcurementPackage { Number = "GM-1", Title = "Gói", BuyerId = buyer.Id, Status = PackageStatus.Draft };
        db.ProcurementPackages.Add(pkg); db.SaveChanges();
        var c = As(db, buyer.Id);

        await c.Import(pkg.Id, "1;Móng;;;;\n1.1;Bê tông;m3;10;1000000", true);
        Assert.Equal(0, await db.BoqVersions.CountAsync());             // chưa có BOQ nên chưa có gì để chụp
        Assert.Equal(10_000_000m, Boq.Total(await db.BoqItems.ToListAsync()));

        await c.Import(pkg.Id, "1;Hoàn thiện;;;;\n1.1;Sơn;m2;100;50000\n1.2;Bả;m2;100;30000", true);
        var v = await db.BoqVersions.SingleAsync();                      // bản cũ được chụp trước khi thay
        Assert.Equal(1, v.Number); Assert.Equal(10_000_000m, v.Total);
        Assert.Equal(8_000_000m, Boq.Total(await db.BoqItems.ToListAsync()));

        await c.Restore(v.Id);                                           // khôi phục bản đầu
        Assert.Equal(10_000_000m, Boq.Total(await db.BoqItems.ToListAsync()));
        Assert.Equal(2, await db.BoqVersions.CountAsync());              // bản "trước khi khôi phục" cũng được giữ lại
        Assert.Equal(8_000_000m, (await db.BoqVersions.OrderBy(x => x.Number).LastAsync()).Total);
    }

    [Fact]
    public async Task Locked_package_cannot_be_imported_or_restored()
    {
        var db = TestDb.Create();
        var (buyer, _, _, _, _, _) = TestDb.Seed(db);
        var pkg = new ProcurementPackage { Number = "GM-2", Title = "Gói", BuyerId = buyer.Id, Status = PackageStatus.Approved };
        db.ProcurementPackages.Add(pkg); db.SaveChanges();
        await As(db, buyer.Id).Import(pkg.Id, "1;A;Cái;1;100", true);
        Assert.Equal(0, await db.BoqItems.CountAsync());
    }
}
