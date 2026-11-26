using System.Text.Json;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace EProcurement.Web.Infrastructure.Reporting;

public partial class StandardReports
{
    private async Task<LegalTimelineSettings> LegalAsync()
    {
        var json = await _db.AppSettings.AsNoTracking().Where(s => s.Key == SettingsStore.LegalTimelines).Select(s => s.Value).FirstOrDefaultAsync();
        try { return string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<LegalTimelineSettings>(json) ?? new(); } catch (JsonException) { return new(); }
    }

    // ------------------------------------------------------------------ Báo cáo tình hình thực hiện công tác lựa chọn nhà thầu (kỳ / năm)
    private async Task<ReportResult> BiddingAsync(ReportFilter f)
    {
        var r = New("bidding");
        var pkgs = await _db.ProcurementPackages.AsNoTracking().Include(x => x.PurchaseRequests).ThenInclude(p => p.Lines)
            .Where(p => p.CreatedAt >= f.From && p.CreatedAt < f.ToExclusive && p.Status != PackageStatus.Draft)
            .AsSplitQuery().ToListAsync();
        if (f.Depts.Count > 0) pkgs = pkgs.Where(p => p.PurchaseRequests.Any(pr => f.Depts.Contains(pr.Department))).ToList();
        var svc = new PackageActualsService(_db);
        var rows = new List<(ProcurementPackage P, bool Done, decimal Selected)>();
        foreach (var p in pkgs)
        {
            if (p.Status == PackageStatus.Cancelled) { rows.Add((p, false, 0)); continue; }
            var v = await svc.BuildAsync(p);
            rows.Add((p, v.HasSelection && v.Result.AwardPrice > 0, v.HasSelection ? v.Result.AwardPrice : 0));
        }
        var live = rows.Where(x => x.P.Status != PackageStatus.Cancelled).ToList();
        var done = live.Where(x => x.Done).ToList();
        decimal budgetDone = done.Sum(x => x.P.Budget), selected = done.Sum(x => x.Selected);
        var competitive = live.Where(x => x.P.Method is ProcurementMethod.OpenTender or ProcurementMethod.CompetitiveQuotation).Sum(x => x.P.Budget);

        var tenders = await _db.Tenders.AsNoTracking().Include(t => t.Bidders).ThenInclude(b => b.Partners).Include(t => t.Complaints).Include(t => t.Clarifications).Include(t => t.Package)
            .Where(t => t.PublishedAt != null && t.PublishedAt >= f.From && t.PublishedAt < f.ToExclusive).ToListAsync();
        var legal = await LegalAsync();
        var violations = tenders.Select(t => (T: t, Bad: LegalTimelines.CheckTender(t, t.Package?.Field, t.Clarifications, legal, DateTime.Now).Where(c => c.Ok == false).ToList())).ToList();

        r.Kpis.Add(new("Số gói trong kỳ", live.Count.ToString(), $"{done.Count} gói đã có kết quả · {rows.Count - live.Count} gói hủy"));
        r.Kpis.Add(new("Tổng giá gói", live.Sum(x => x.P.Budget).ToShortMoney() + " đ"));
        r.Kpis.Add(new("Giá trúng thầu / giá chọn", selected.ToShortMoney() + " đ", $"trên {budgetDone.ToShortMoney()} đ giá gói đã có kết quả"));
        r.Kpis.Add(new("Tiết kiệm", (budgetDone - selected).ToShortMoney() + " đ", Pc(Pct(budgetDone - selected, budgetDone)) + " giá gói"));
        r.Kpis.Add(new("Tỷ trọng cạnh tranh (giá trị)", Pc(Pct(competitive, live.Sum(x => x.P.Budget))), "đấu thầu rộng rãi + chào hàng cạnh tranh"));
        r.Kpis.Add(new("Gói thầu 1 HSDT", tenders.Count(t => t.OpenedAt != null && t.Bidders.Count(b => b.Submitted) <= 1).ToString(), $"trên {tenders.Count(t => t.OpenedAt != null)} gói đã mở thầu", tenders.Any(t => t.OpenedAt != null && t.Bidders.Count(b => b.Submitted) <= 1)));
        r.Kpis.Add(new("Kiến nghị", tenders.Sum(t => t.Complaints.Count).ToString(), $"{tenders.Sum(t => t.Complaints.Count(c => c.Upheld == true))} được chấp nhận"));
        r.Kpis.Add(new("Vi phạm mốc thời gian", violations.Count(v => v.Bad.Count > 0).ToString(), "gói thầu không đạt mốc tối thiểu theo luật", violations.Any(v => v.Bad.Count > 0)));

        object?[] Line(string label, List<(ProcurementPackage P, bool Done, decimal Selected)> g)
        {
            var d = g.Where(x => x.Done).ToList();
            decimal b = d.Sum(x => x.P.Budget), s = d.Sum(x => x.Selected);
            return new object?[] { label, g.Count, d.Count, g.Sum(x => x.P.Budget), s, b - s, Pct(b - s, b) };
        }
        var cols = new List<ReportColumn> { new("Nhóm"), new("Số gói", ColumnKind.Number), new("Có kết quả", ColumnKind.Number), new("Giá gói", ColumnKind.Money), new("Giá trúng / chọn", ColumnKind.Money), new("Tiết kiệm", ColumnKind.Money), new("Tỷ lệ tiết kiệm", ColumnKind.Percent) };
        r.Tables.Add(new ReportTable
        {
            Title = "Theo hình thức lựa chọn nhà thầu", Width = 12, Chart = ChartKind.Bar, ChartValue = 3, Columns = cols.ToList(),
            Rows = live.GroupBy(x => x.P.Method).OrderByDescending(g => (int)g.Key).Select(g => Line(g.Key.GetDisplayName(), g.ToList())).ToList(),
            Totals = Line("Tổng cộng", live)
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Theo lĩnh vực", Width = 12, Columns = cols.ToList(),
            Rows = live.GroupBy(x => string.IsNullOrEmpty(x.P.Field) ? "(chưa phân loại)" : x.P.Field).OrderBy(g => g.Key).Select(g => Line(g.Key, g.ToList())).ToList(),
            Totals = Line("Tổng cộng", live)
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Đấu thầu trong kỳ (theo ngày phát hành HSMT)", Width = 12,
            Note = "Mốc thời gian theo luật: kiểm tra thời gian chuẩn bị HSDT và sửa đổi HSMT theo cấu hình Thiết lập › Mốc thời gian theo luật.",
            Columns = { new("Gói thầu"), new("Phạm vi"), new("Giá gói thầu", ColumnKind.Money), new("Nhận HSMT", ColumnKind.Number), new("Nộp HSDT", ColumnKind.Number),
                        new("Liên danh", ColumnKind.Number), new("Kiến nghị", ColumnKind.Number), new("Giá trúng thầu", ColumnKind.Money), new("Trạng thái"), new("Mốc thời gian theo luật") },
            Rows = violations.OrderBy(v => v.T.PublishedAt).Select(v => new object?[] { $"{v.T.Number} – {v.T.Title}", v.T.IsInternational ? "Quốc tế" : "Trong nước", v.T.PackageBudget,
                v.T.Bidders.Count, v.T.Bidders.Count(b => b.Submitted), v.T.Bidders.Count(b => b.Partners.Any(p => p.Role == BidPartnerRole.Member)), v.T.Complaints.Count,
                v.T.AwardPrice, v.T.Status.GetDisplayName(), v.Bad.Count == 0 ? "Đạt" : string.Join("; ", v.Bad.Select(b => b.Rule)) }).ToList()
        });
        var cancelled = rows.Where(x => x.P.Status == PackageStatus.Cancelled).ToList();
        if (cancelled.Count > 0)
            r.Tables.Add(new ReportTable
            {
                Title = "Gói hủy trong kỳ", Width = 12,
                Columns = { new("Gói"), new("Hình thức"), new("Lĩnh vực"), new("Giá gói", ColumnKind.Money), new("Ghi chú") },
                Rows = cancelled.Select(x => new object?[] { $"{x.P.Number} – {x.P.Title}", x.P.Method.GetDisplayName(), x.P.Field, x.P.Budget, x.P.Note }).ToList()
            });
        return r;
    }

    // ------------------------------------------------------------------ Mua sắm xanh / ESG
    private async Task<ReportResult> GreenAsync(ReportFilter f)
    {
        var r = New("green");
        var lines = await SpendLinesAsync(f);
        var docs = await _db.VendorDocuments.AsNoTracking().Include(d => d.Vendor)
            .Where(d => (d.Type == VendorDocumentType.Environmental || d.Type == VendorDocumentType.SocialResponsibility) && d.Status == VendorDocumentStatus.Verified).ToListAsync();
        var today = DateTime.Today;
        var valid = docs.Where(d => d.ExpiryDate == null || d.ExpiryDate >= today).ToList();
        var envVendors = valid.Where(d => d.Type == VendorDocumentType.Environmental).Select(d => d.VendorId).ToHashSet();
        var socVendors = valid.Where(d => d.Type == VendorDocumentType.SocialResponsibility).Select(d => d.VendorId).ToHashSet();
        var total = lines.Sum(l => l.Amount);
        var green = lines.Where(l => l.VendorId.HasValue && envVendors.Contains(l.VendorId.Value)).Sum(l => l.Amount);
        var social = lines.Where(l => l.VendorId.HasValue && socVendors.Contains(l.VendorId.Value)).Sum(l => l.Amount);
        var sets = await _db.CriteriaSets.AsNoTracking().Include(s => s.Items).Where(s => s.IsActive).ToListAsync();
        r.Kpis.Add(new("Chi tiêu với NCC có chứng nhận môi trường", Pc(Pct(green, total)), green.ToShortMoney() + " đ / " + total.ToShortMoney() + " đ"));
        r.Kpis.Add(new("Chi tiêu với NCC có chứng nhận an toàn / xã hội", Pc(Pct(social, total)), social.ToShortMoney() + " đ"));
        r.Kpis.Add(new("NCC có chứng nhận ESG còn hiệu lực", envVendors.Union(socVendors).Count().ToString()));
        r.Kpis.Add(new("Chứng nhận hết hạn trong 90 ngày", valid.Count(d => d.ExpiryDate.HasValue && d.ExpiryDate <= today.AddDays(90)).ToString(), null, valid.Any(d => d.ExpiryDate.HasValue && d.ExpiryDate <= today.AddDays(90))));
        r.Kpis.Add(new("Bộ tiêu chí có tiêu chí xanh", sets.Count(s => s.Items.Any(i => i.IsSustainability)).ToString(), $"trên {sets.Count} bộ tiêu chí đang dùng"));
        r.Tables.Add(new ReportTable
        {
            Title = "Tỷ trọng chi tiêu xanh theo nhóm hàng", Width = 12, Chart = ChartKind.Bar, ChartValue = 3,
            Columns = { new("Nhóm hàng"), new("Chi tiêu", ColumnKind.Money), new("Với NCC chứng nhận môi trường", ColumnKind.Money), new("Tỷ trọng xanh", ColumnKind.Percent) },
            Rows = lines.GroupBy(l => l.Category).Select(g =>
            {
                var gt = g.Sum(x => x.Amount); var gg = g.Where(x => x.VendorId.HasValue && envVendors.Contains(x.VendorId.Value)).Sum(x => x.Amount);
                return new object?[] { g.Key, gt, gg, Pct(gg, gt) };
            }).OrderByDescending(x => (decimal)x[1]!).ToList(),
            Totals = new object?[] { "Tổng cộng", total, green, Pct(green, total) }
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Chứng nhận môi trường / xã hội của nhà cung cấp", Width = 12,
            Columns = { new("Nhà cung cấp"), new("Loại"), new("Hồ sơ"), new("Hiệu lực đến", ColumnKind.Date), new("Tình trạng") },
            Rows = docs.OrderBy(d => d.Vendor?.Name).Select(d => new object?[] { d.Vendor?.Name, d.Type == VendorDocumentType.Environmental ? "Môi trường" : "An toàn / xã hội", d.Number ?? d.FileName,
                d.ExpiryDate, d.ExpiryDate.HasValue && d.ExpiryDate < today ? "Hết hạn" : d.ExpiryDate.HasValue && d.ExpiryDate <= today.AddDays(90) ? "Sắp hết hạn" : "Còn hiệu lực" }).ToList()
        });
        r.Tables.Add(new ReportTable
        {
            Title = "Bộ tiêu chí đánh giá có tiêu chí xanh / ESG", Width = 12,
            Columns = { new("Bộ tiêu chí"), new("Dùng cho"), new("Số tiêu chí xanh", ColumnKind.Number), new("Điểm xanh / tổng điểm"), new("Tiêu chí xanh") },
            Rows = sets.Where(s => s.Items.Any(i => i.IsSustainability)).Select(s => new object?[] { s.Name, s.Kind.GetDisplayName(), s.Items.Count(i => i.IsSustainability),
                $"{s.Items.Where(i => i.IsSustainability).Sum(i => i.MaxScore)}/{s.Items.Sum(i => i.MaxScore)}", string.Join("; ", s.Items.Where(i => i.IsSustainability).Select(i => i.Name)) }).ToList()
        });
        return r;
    }
}
