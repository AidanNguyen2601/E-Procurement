using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class HomeController : ProcControllerBase
{
    public HomeController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index()
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);
        var vm = new DashboardViewModel();

        // Việc chờ tôi duyệt
        var dept = User.GetDepartment();
        var prQ = Db.PurchaseRequests.AsQueryable();
        if (User.IsAdmin()) vm.MyPendingApprovals = await prQ.CountAsync(p => p.Status == PrStatus.Submitted || p.Status == PrStatus.PendingDirector)
                                                     + await Db.PurchaseOrders.CountAsync(p => p.Status == PoStatus.PendingApproval)
                                                     + await Db.Rfqs.CountAsync(r => r.Status == RfqStatus.PendingApproval)
                                                     + await Db.DirectAwards.CountAsync(d => d.Status == DirectAwardStatus.PendingApproval)
                                                     + await Db.Tenders.CountAsync(t => t.Status == TenderStatus.PendingPlanApproval || t.Status == TenderStatus.PendingResultApproval)
                                                     + await Db.TenderComplaints.CountAsync(c => c.ResolvedAt == null && c.Tender!.Status == TenderStatus.Awarded);
        else if (User.IsInRole(AppRoles.Director)) vm.MyPendingApprovals = await prQ.CountAsync(p => p.Status == PrStatus.PendingDirector)
                                                     + await Db.PurchaseOrders.CountAsync(p => p.Status == PoStatus.PendingApproval)
                                                     + await Db.Rfqs.CountAsync(r => r.Status == RfqStatus.PendingApproval)
                                                     + await Db.DirectAwards.CountAsync(d => d.Status == DirectAwardStatus.PendingApproval)
                                                     + await Db.Tenders.CountAsync(t => t.Status == TenderStatus.PendingPlanApproval || t.Status == TenderStatus.PendingResultApproval)
                                                     + await Db.TenderComplaints.CountAsync(c => c.ResolvedAt == null && c.Tender!.Status == TenderStatus.Awarded);
        else if (User.IsInRole(AppRoles.Approver)) vm.MyPendingApprovals = await prQ.CountAsync(p => p.Status == PrStatus.Submitted && p.Department == dept && p.RequesterId != Me);

        vm.PrWaitingPurchase = await prQ.CountAsync(p => p.Status == PrStatus.Approved);
        vm.OpenRfqs = await Db.Rfqs.CountAsync(r => r.Status == RfqStatus.Open);

        // Chi tiêu = PO đã duyệt trở lên (không tính nháp / chờ duyệt / hủy)
        var pos = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Lines).ThenInclude(l => l.Item!).ThenInclude(i => i.Category)
            .Where(p => p.Status >= PoStatus.Approved && p.Status != PoStatus.Cancelled).AsNoTracking().ToListAsync();
        vm.LatePos = pos.Count(p => p.IsLate);
        vm.SpendThisMonth = pos.Where(p => p.OrderDate >= monthStart).Sum(p => p.SubTotal);
        vm.SpendYtd = pos.Where(p => p.OrderDate >= yearStart).Sum(p => p.SubTotal);
        for (var i = 5; i >= 0; i--)
        {
            var m = monthStart.AddMonths(-i);
            vm.SpendByMonth.Add(($"T{m.Month}/{m:yy}", pos.Where(p => p.OrderDate >= m && p.OrderDate < m.AddMonths(1)).Sum(p => p.SubTotal)));
        }
        vm.SpendByCategory = pos.Where(p => p.OrderDate >= yearStart).SelectMany(p => p.Lines)
            .GroupBy(l => l.Item?.Category?.Name ?? "Khác").Select(g => (g.Key, g.Sum(l => l.Amount)))
            .OrderByDescending(x => x.Item2).ToList();
        vm.TopVendors = pos.Where(p => p.OrderDate >= yearStart && p.Vendor != null).GroupBy(p => p.Vendor!)
            .Select(g => (g.Key.Name, g.Sum(p => p.SubTotal), g.Key.Rating)).OrderByDescending(x => x.Item2).Take(5).ToList();

        // Tiết kiệm = dự toán PR – giá trị PO tương ứng
        var prIds = pos.Where(p => p.PurchaseRequestId != null).Select(p => p.PurchaseRequestId!.Value).Distinct().ToList();
        var prEstimates = await Db.PurchaseRequests.Where(p => prIds.Contains(p.Id)).SumAsync(p => p.EstimatedTotal);
        vm.Savings = prEstimates - pos.Where(p => p.PurchaseRequestId != null).Sum(p => p.SubTotal);

        vm.MyRequests = await Db.PurchaseRequests.Where(p => p.RequesterId == Me).OrderByDescending(p => p.CreatedAt).Take(6).ToListAsync();
        if (User.IsPurchasing() || User.IsInRole(AppRoles.Director))
            vm.OpenOrders = pos.Where(p => p.Status is PoStatus.Approved or PoStatus.Issued or PoStatus.PartiallyReceived)
                .OrderBy(p => p.DeliveryDate).Take(8).ToList();
        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = System.Diagnostics.Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
