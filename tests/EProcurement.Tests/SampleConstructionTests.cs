using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EProcurement.Tests;

public class SampleConstructionTests
{
    private static async Task<Web.Data.EProcDbContext> LoadedAsync()
    {
        var db = TestDb.Create();
        var (buyer, vendor, _, _, _, _) = TestDb.Seed(db);
        db.Users.AddRange(
            new AppUser { Username = "admin", FullName = "QT", Role = AppRoles.Admin, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "director", FullName = "GĐ", Role = AppRoles.Director, Department = "BGĐ", PasswordHash = "x" },
            new AppUser { Username = "requester", FullName = "NV", Role = AppRoles.Requester, Department = "Kỹ thuật", PasswordHash = "x" },
            new AppUser { Username = "ketoan.tt", FullName = "KT", Role = AppRoles.Accountant, Department = "Kế toán", PasswordHash = "x" },
            new AppUser { Username = "approver", FullName = "TP1", Role = AppRoles.Approver, Department = "Kỹ thuật", PasswordHash = "x" },
            new AppUser { Username = "approver2", FullName = "TP2", Role = AppRoles.Approver, Department = "CNTT", PasswordHash = "x" },
            new AppUser { Username = "buyer2", FullName = "MH2", Role = AppRoles.Buyer, Department = "Mua hàng", PasswordHash = "x" });
        db.Vendors.AddRange(
            new Vendor { Code = "NCC002", Name = "NCC hai", TaxCode = "0100000002", Status = VendorStatus.Approved },
            new Vendor { Code = "NCC003", Name = "NCC ba", TaxCode = "0100000003", Status = VendorStatus.Approved });
        db.SaveChanges();
        await SampleConstruction.LoadAsync(db);
        return db;
    }

    [Fact]
    public async Task Load_creates_a_coherent_construction_scenario()
    {
        var db = await LoadedAsync();
        var a = await db.ProcurementPackages.SingleAsync(p => p.Number == SampleConstruction.PackageA);
        var b = await db.ProcurementPackages.SingleAsync(p => p.Number == SampleConstruction.PackageB);
        Assert.Equal(PackageStatus.Draft, a.Status); Assert.Equal(PackageStatus.Executing, b.Status);
        Assert.Equal(15, await db.BoqItems.CountAsync(i => i.PackageId == a.Id));          // 14 dòng mẫu + 1 dòng bổ sung
        Assert.Equal(2, await db.BoqVersions.CountAsync(v => v.PackageId == a.Id));         // phiên bản để so sánh
        Assert.Equal(1, await db.BoqVersions.CountAsync(v => v.PackageId == b.Id));

        var tender = await db.Tenders.Include(t => t.Lines).Include(t => t.Bidders).ThenInclude(x => x.Lines).SingleAsync();
        Assert.Equal(TenderStatus.Contracted, tender.Status); Assert.Equal(3, tender.Bidders.Count(x => x.Submitted));
        Assert.All(tender.Lines, l => Assert.NotNull(l.BoqCode));
        Assert.All(tender.Bidders, bd => Assert.Equal(tender.Lines.Count, bd.Lines.Count));
        // bảng so sánh có cả cảnh báo thấp và cao bất thường
        var cmp = BoqBid.Build(tender, tender.Bidders);
        Assert.Contains(cmp.Rows.Where(r => !r.IsGroup).SelectMany(r => r.Cells.Values), c => c.Flag == "low");
        Assert.Contains(cmp.Rows.Where(r => !r.IsGroup).SelectMany(r => r.Cells.Values), c => c.Flag == "high");
    }

    [Fact]
    public async Task Contract_boq_acceptances_and_payments_are_consistent()
    {
        var db = await LoadedAsync();
        var c = await db.Contracts.Include(x => x.Lines).Include(x => x.Amendments).ThenInclude(x => x.Lines).SingleAsync();
        Assert.Equal(ContractStatus.Active, c.Status);
        var pl = Assert.Single(c.Amendments);
        Assert.Equal(AmendmentStatus.Approved, pl.Status); Assert.Equal(2, pl.Lines.Count);
        Assert.Contains(c.Lines, l => l.AddedByAmendment == "PL01"); Assert.Contains(c.Lines, l => l.OriginalQuantity != null && l.OriginalQuantity != l.Quantity);
        // giá trị hợp đồng = tổng thành tiền các dòng (sai số làm tròn đơn giá ≤ 1 đ / dòng)
        var linesTotal = c.Lines.Sum(l => l.Quantity * l.UnitPrice);
        Assert.InRange(Math.Abs(linesTotal - c.Value), 0, c.Lines.Count * Math.Max(1, c.Lines.Max(l => l.Quantity)));
        // thứ tự dòng liền nhau theo nhóm: dòng phát sinh nằm trong nhóm 1.1
        var codes = c.Lines.OrderBy(l => l.SortOrder).Select(l => l.BoqCode!).ToList();
        Assert.True(codes.IndexOf("1.1.3") == codes.IndexOf("1.1.2") + 1, string.Join(",", codes));

        var accs = await db.BoqAcceptances.Include(a => a.Lines).Where(a => a.ContractId == c.Id).OrderBy(a => a.Period).ToListAsync();
        Assert.Equal(new[] { AcceptanceStatus.Confirmed, AcceptanceStatus.Confirmed, AcceptanceStatus.Draft }, accs.Select(a => a.Status));
        var cum = BoqAcceptances.Cumulative(accs);
        Assert.All(cum, kv => Assert.True(kv.Value <= c.Lines.First(l => l.Id == kv.Key).Quantity));
        // biên bản nháp vẫn hợp lệ nếu được xác nhận ngay bây giờ
        Assert.Empty(BoqAcceptances.Validate(c.Lines.ToList(), accs[2].Lines.ToDictionary(l => l.ContractLineId, l => l.Quantity), cum));

        var pays = await db.Payments.Where(p => p.ContractId == c.Id).OrderBy(p => p.Number).ToListAsync();
        Assert.Equal(new[] { PaymentType.Advance, PaymentType.Acceptance, PaymentType.Acceptance }, pays.Select(p => p.Type));
        Assert.Equal(new[] { PaymentStatus.Paid, PaymentStatus.Paid, PaymentStatus.Requested }, pays.Select(p => p.Status));
        Assert.All(pays.Where(p => p.Type == PaymentType.Acceptance), p => { Assert.NotNull(p.AcceptanceId); Assert.Equal(p.Amount - p.AdvanceDeduction, p.NetAmount); Assert.True(p.AdvanceDeduction > 0); });
        // không thanh toán vượt giá trị hợp đồng (gồm VAT)
        Assert.True(pays.Where(p => p.Type != PaymentType.Advance).Sum(p => p.Amount) < Math.Round(c.Value * 1.1m));
    }

    [Fact]
    public async Task Load_is_idempotent()
    {
        var db = await LoadedAsync();
        Assert.Empty(await SampleConstruction.LoadAsync(db));
        Assert.Equal(2, await db.ProcurementPackages.CountAsync(p => p.Number.StartsWith("MAU-GM-XD")));
    }
}
