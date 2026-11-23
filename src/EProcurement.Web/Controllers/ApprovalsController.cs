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

        ViewBag.PrThreshold = Settings.PrDirectorThreshold;
        return View(vm);
    }
}
