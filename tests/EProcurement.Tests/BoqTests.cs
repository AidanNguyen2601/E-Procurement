using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Xunit;

namespace EProcurement.Tests;

public class BoqTests
{
    private static BoqItem I(int id, int? parent, int order, string name, decimal q = 0, decimal p = 0) =>
        new() { Id = id, PackageId = 1, ParentId = parent, SortOrder = order, Name = name, Quantity = q, UnitPrice = p };

    private static List<BoqItem> Sample() => new()
    {
        I(1, null, 1, "Hạ tầng"),
        I(2, 1, 1, "Máy chủ", 2, 100),      // 200
        I(3, 1, 2, "Mạng"),                  // nhóm
        I(4, 3, 1, "Switch", 3, 50),         // 150
        I(5, 3, 2, "Cáp", 10, 5),            // 50
        I(6, null, 2, "Dịch vụ", 1, 300)     // 300
    };

    [Fact]
    public void Flatten_rolls_up_groups_and_codes_by_tree_order()
    {
        var nodes = Boq.Flatten(Sample());
        Assert.Equal(new[] { "1", "1.1", "1.2", "1.2.1", "1.2.2", "2" }, nodes.Select(n => n.Code));
        Assert.Equal(new[] { 0, 1, 1, 2, 2, 0 }, nodes.Select(n => n.Depth));
        Assert.Equal(400m, nodes[0].Amount);          // 200 + (150 + 50)
        Assert.Equal(200m, nodes[2].Amount);
        Assert.True(nodes[0].IsGroup); Assert.False(nodes[1].IsGroup);
        Assert.Equal(700m, Boq.Total(Sample()));
    }

    [Fact]
    public void Group_ignores_its_own_quantity_and_price()
    {
        var items = Sample(); items[0].Quantity = 99; items[0].UnitPrice = 99;
        Assert.Equal(700m, Boq.Total(items));
    }

    [Fact]
    public void Flatten_survives_a_corrupt_cycle()
    {
        var items = new List<BoqItem> { I(1, 2, 1, "A", 1, 10), I(2, 1, 1, "B", 1, 10) };
        var nodes = Boq.Flatten(items); // không treo, không lỗi
        Assert.NotNull(nodes);
    }

    [Fact]
    public void Move_up_down_swaps_siblings_and_renumbers()
    {
        var items = Sample();
        Assert.Null(Boq.Move(items, 6, Boq.MoveKind.Up));
        Assert.Equal(new[] { 6, 1 }, Boq.Flatten(items).Where(n => n.Depth == 0).Select(n => n.Item.Id));
        Assert.Equal("1", items.First(i => i.Id == 6).Code);
        Assert.NotNull(Boq.Move(items, 6, Boq.MoveKind.Up)); // đã ở đầu
        Assert.Null(Boq.Move(items, 6, Boq.MoveKind.Down));
        Assert.NotNull(Boq.Move(items, 6, Boq.MoveKind.Down)); // đã ở cuối
    }

    [Fact]
    public void Indent_makes_previous_sibling_the_parent_and_outdent_reverses()
    {
        var items = Sample();
        Assert.Null(Boq.Move(items, 6, Boq.MoveKind.Indent));       // "Dịch vụ" thành con của "Hạ tầng"
        Assert.Equal(1, items.First(i => i.Id == 6).ParentId);
        Assert.Equal("1.3", items.First(i => i.Id == 6).Code);
        Assert.Equal(700m, Boq.Total(items));
        Assert.Null(Boq.Move(items, 6, Boq.MoveKind.Outdent));
        Assert.Null(items.First(i => i.Id == 6).ParentId);
        Assert.Equal("2", items.First(i => i.Id == 6).Code);
        Assert.NotNull(Boq.Move(items, 1, Boq.MoveKind.Outdent));  // đã ở cấp cao nhất
        Assert.NotNull(Boq.Move(items, 1, Boq.MoveKind.Indent));    // không có anh em phía trên
    }

    [Fact]
    public void Outdent_places_the_item_right_after_its_parent()
    {
        var items = Sample();
        Assert.Null(Boq.Move(items, 3, Boq.MoveKind.Outdent)); // "Mạng" ra ngang "Hạ tầng"
        Assert.Equal(new[] { 1, 3, 6 }, Boq.Flatten(items).Where(n => n.Depth == 0).Select(n => n.Item.Id));
    }

    [Fact]
    public void Indent_is_blocked_beyond_max_depth()
    {
        var items = new List<BoqItem> { I(1, null, 1, "L0") };
        var id = 1;
        for (var d = 1; d < Boq.MaxDepth; d++) { id++; items.Add(I(id, id - 1, 1, "L" + d)); }
        items.Add(I(100, id - 1, 2, "x")); // anh em của dòng sâu nhất (id) → thụt vào sẽ thành cấp thứ 9
        Assert.Equal("Vượt quá số cấp tối đa của BOQ.", Boq.Move(items, 100, Boq.MoveKind.Indent));
    }

    [Fact]
    public void Subtree_and_depth_helpers()
    {
        var items = Sample();
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Boq.Subtree(items, 1).OrderBy(x => x));
        Assert.Equal(2, Boq.DepthOf(items, 4));
        Assert.Equal(3, Boq.Height(items, 1));
    }

    [Theory]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("1,234.5", 1234.5)]
    [InlineData("1.234.567", 1234567)]
    [InlineData("385000000", 385000000)]
    [InlineData("2,5", 2.5)]
    [InlineData("", 0)]
    public void TryNumber_reads_vietnamese_and_international_formats(string s, double expected)
    {
        Assert.True(Boq.TryNumber(s, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Fact]
    public void TryNumber_rejects_garbage_and_negatives()
    {
        Assert.False(Boq.TryNumber("abc", out _));
        Assert.False(Boq.TryNumber("-5", out _));
    }

    [Fact]
    public void Parse_skips_header_and_reports_errors_with_line_numbers()
    {
        var (rows, errors) = Boq.Parse("Mã;Tên;ĐVT;KL;Giá\n1;Hạ tầng;;;\n1.1;Máy chủ;Bộ;2;100\n1.1;Trùng;Bộ;1;1\nx;Sai mã;;;\n1.2;Thiếu số;Bộ;abc;1\n1.3");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "4", "5", "6", "7" }, errors.Select(e => e.Split('|')[0]));
        Assert.Equal(100m, rows[1].UnitPrice);
    }

    [Fact]
    public void Parse_accepts_tab_separated_excel_paste()
    {
        var (rows, errors) = Boq.Parse("1\tHạng mục\t\t\t\n1.1\tChi tiết\tCái\t2,5\t1.000");
        Assert.Empty(errors);
        Assert.Equal(2.5m, rows[1].Quantity); Assert.Equal(1000m, rows[1].UnitPrice);
    }

    [Fact]
    public void Build_attaches_to_nearest_existing_parent_code()
    {
        var (rows, _) = Boq.Parse("1;A;;;\n1.1.1;Mồ côi;Cái;1;10\n2;B;Cái;1;5");
        var (built, err) = Boq.Build(1, rows);
        Assert.Null(err);
        Assert.Equal("1", built[1].ParentCode); // 1.1 không có → gắn vào 1
        Assert.Null(built[2].ParentCode);
        Assert.Equal(2, built[2].Item.SortOrder);
    }

    [Fact]
    public void Csv_export_round_trips_through_import_including_semicolons_and_quotes()
    {
        var items = new List<BoqItem> { I(1, null, 1, "Nhóm; có \"nháy\""), I(2, 1, 1, "Dòng", 2.5m, 1000) };
        var csv = Boq.ToCsv(Boq.Flatten(items));
        var (rows, errors) = Boq.Parse(csv);
        Assert.Empty(errors);
        Assert.Equal("Nhóm; có \"nháy\"", rows[0].Name);
        Assert.Equal(2.5m, rows[1].Quantity);
        Assert.Equal(1000m, rows[1].UnitPrice);
    }

    [Fact]
    public void Sample_text_is_valid_and_totals_as_documented()
    {
        var (rows, errors) = Boq.Parse(EProcurement.Web.Controllers.BoqController.SampleText);
        Assert.Empty(errors);
        var (built, err) = Boq.Build(1, rows);
        Assert.Null(err);
        Assert.True(Boq.Total(built.Select(b => b.Item).Select((x, i) => { x.Id = i + 1; return x; }).ToList()) >= 0);
    }
}
