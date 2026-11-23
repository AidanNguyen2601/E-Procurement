using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Hộp thư chờ duyệt của người dùng hiện tại (PR + PO)</summary>
[Authorize(Roles = AppRoles.Approvers)]
public class ApprovalsController : ProcControllerBase
{
    public ApprovalsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index()
    {
        var vm = new ApprovalInboxViewModel();
        var prQ = Db.PurchaseRequests.Include(p => p.Requester).Include(p => p.Lines).AsNoTracking();
        if (User.IsAdmin())
            vm.Requests = await prQ.Where(p => p.Status == PrStatus.Submitted || p.Status == PrStatus.PendingDirector).ToListAsync();
        else if (User.IsInRole(AppRoles.Director))
            vm.Requests = await prQ.Where(p => p.Status == PrStatus.PendingDirector).ToListAsync();
        else
        {
            var dept = User.GetDepartment();
            vm.Requests = await prQ.Where(p => p.Status == PrStatus.Submitted && p.Department == dept && p.RequesterId != Me).ToListAsync();
        }
        vm.Requests = vm.Requests.OrderByDescending(p => p.Priority).ThenBy(p => p.SubmittedAt).ToList();

        if (User.IsAdmin() || User.IsInRole(AppRoles.Director))
            vm.Orders = await Db.PurchaseOrders.Include(p => p.Vendor).Include(p => p.Buyer).Include(p => p.PurchaseRequest)
                .Where(p => p.Status == PoStatus.PendingApproval).OrderBy(p => p.CreatedAt).ToListAsync();
        if (User.IsAdmin() || User.IsInRole(AppRoles.Director))
            vm.Rfqs = await Db.Rfqs.Include(r => r.ProposedVendor).Include(r => r.Buyer).Include(r => r.PurchaseRequest).Include(r => r.Vendors)
                .Where(r => r.Status == RfqStatus.PendingApproval).OrderBy(r => r.CreatedAt).ToListAsync();
        if (User.IsAdmin() || User.IsInRole(AppRoles.Director))
            vm.DirectAwards = await Db.DirectAwards.Include(d => d.Vendor).Include(d => d.Buyer).Include(d => d.PurchaseRequest)
                .Where(d => d.Status == DirectAwardStatus.PendingApproval).OrderBy(d => d.SubmittedAt).ToListAsync();
        if (User.IsAdmin() || User.IsInRole(AppRoles.Director))
        {
            var tenders = await Db.Tenders.Include(t => t.PurchaseRequest).Include(t => t.Buyer).Include(t => t.AwardedVendor).Include(t => t.Committee)
                .Where(t => t.Status == TenderStatus.PendingPlanApproval || t.Status == TenderStatus.PendingResultApproval).OrderBy(t => t.CreatedAt).ToListAsync();
            vm.TenderPlans = tenders.Where(t => t.Status == TenderStatus.PendingPlanApproval).ToList();
            vm.TenderResults = tenders.Where(t => t.Status == TenderStatus.PendingResultApproval).ToList();
            vm.Complaints = await Db.TenderComplaints.Include(c => c.Tender).Include(c => c.Vendor)
                .Where(c => c.ResolvedAt == null && c.Tender!.Status == TenderStatus.Awarded).OrderBy(c => c.ReceivedAt).ToListAsync();
        }

        ViewBag.PrThreshold = Settings.PrDirectorThreshold;
        return View(vm);
    }
}
