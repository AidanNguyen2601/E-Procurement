using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Đề nghị mua hàng (PR). Luồng: Nháp → Gửi duyệt → Trưởng bộ phận duyệt
/// → (nếu vượt ngưỡng) Giám đốc duyệt → Đã duyệt → Mua hàng xử lý (RFQ / PO) → Hoàn tất.
/// </summary>
public class PurchaseRequestsController : ProcControllerBase
{
    public PurchaseRequestsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    /// <param name="scope">mine | dept | all | approved</param>
    public async Task<IActionResult> Index(string? scope, PrStatus? status, string? keyword, int page = 1)
    {
        var q = Db.PurchaseRequests.Include(p => p.Requester).AsNoTracking();
        var seesAll = User.IsPurchasing() || User.IsInRole(AppRoles.Director);
        var isApprover = User.IsInRole(AppRoles.Approver);
        scope ??= seesAll ? "all" : isApprover ? "dept" : "mine";
        var dept = User.GetDepartment();
        q = scope switch
        {
            "all" when seesAll => q,
            "approved" when seesAll => q.Where(p => p.Status == PrStatus.Approved || p.Status == PrStatus.Processing),
            "dept" when isApprover || seesAll => q.Where(p => p.Department == dept),
            _ => q.Where(p => p.RequesterId == Me)
        };
        if (status.HasValue) q = q.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(keyword)) q = q.Where(p => p.Number.Contains(keyword) || p.Title.Contains(keyword));

        const int size = 15;
        page = Math.Max(1, page);
        var total = await q.CountAsync();
        var items = await q.OrderByDescending(p => p.CreatedAt).Skip((page - 1) * size).Take(size).ToListAsync();
        ViewBag.Scope = scope; ViewBag.Status = status; ViewBag.Keyword = keyword;
        ViewBag.SeesAll = seesAll; ViewBag.IsApprover = isApprover;
        return View(new PagedList<PurchaseRequest>(items, total, page, size));
    }

    public async Task<IActionResult> Details(int id)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Requester).Include(p => p.Lines).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (pr == null) return NotFound();
        if (!CanViewPr(pr)) return Forbid();
        pr.Lines = pr.Lines.OrderBy(l => l.SortOrder).ToList();
        ViewBag.History = await HistoryAsync(DocType.PR, id);
        ViewBag.Rfqs = await Db.Rfqs.Where(r => r.PurchaseRequestId == id).ToListAsync();
        ViewBag.Orders = await Db.PurchaseOrders.Include(p => p.Vendor).Where(p => p.PurchaseRequestId == id).ToListAsync();
        ViewBag.CanApprove = CanApprove(pr);
        ViewBag.Threshold = Settings.PrDirectorThreshold;
        return View(pr);
    }

    public async Task<IActionResult> Create()
    {
        await LoadItemsJsonAsync();
        var vm = new PrFormViewModel { Number = "(tự sinh khi lưu)" };
        vm.Lines.Add(new LineInput());
        return View("Form", vm);
    }

    public async Task<IActionResult> Edit(int id)
    {
        var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id);
        if (pr == null) return NotFound();
        if (pr.RequesterId != Me || pr.Status is not (PrStatus.Draft or PrStatus.Rejected))
        {
            TempData["Error"] = "Chỉ người đề nghị được sửa PR ở trạng thái Nháp hoặc Bị từ chối.";
            return RedirectToAction(nameof(Details), new { id });
        }
        await LoadItemsJsonAsync();
        return View("Form", new PrFormViewModel
        {
            Id = pr.Id, Number = pr.Number, Title = pr.Title, NeededDate = pr.NeededDate, Priority = pr.Priority, Purpose = pr.Purpose,
            Lines = pr.Lines.OrderBy(l => l.SortOrder).Select(l => new LineInput
                { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = l.EstimatedPrice }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PrFormViewModel vm, bool submit = false)
    {
        vm.Lines = CleanLines(vm.Lines);
        if (vm.NeededDate < DateTime.Today) ModelState.AddModelError(nameof(vm.NeededDate), "Ngày cần hàng không được ở quá khứ");
        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
            await LoadItemsJsonAsync();
            return View("Form", vm);
        }

        PurchaseRequest pr;
        if (vm.Id == 0)
        {
            pr = new PurchaseRequest
            {
                Number = await DocNumbers.NextAsync(Db.PurchaseRequests.Select(p => p.Number), "PR"),
                RequesterId = Me, Department = User.GetDepartment(), RequestDate = DateTime.Today, CreatedAt = DateTime.Now
            };
            Db.PurchaseRequests.Add(pr);
        }
        else
        {
            pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == vm.Id) ?? throw new InvalidOperationException();
            if (pr.RequesterId != Me || pr.Status is not (PrStatus.Draft or PrStatus.Rejected)) return Forbid();
            Db.PurchaseRequestLines.RemoveRange(pr.Lines);
            pr.Lines.Clear();
            pr.Status = PrStatus.Draft;
        }
        pr.Title = vm.Title; pr.NeededDate = vm.NeededDate; pr.Priority = vm.Priority; pr.Purpose = vm.Purpose;
        var s = 1;
        foreach (var l in vm.Lines)
            pr.Lines.Add(new PurchaseRequestLine { ItemId = l.ItemId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, EstimatedPrice = l.Price, SortOrder = s++ });
        pr.Recalculate();
        await Db.SaveChangesAsync();

        if (submit) return await DoSubmit(pr);
        TempData["Success"] = $"Đã lưu nháp {pr.Number}.";
        return RedirectToAction(nameof(Details), new { id = pr.Id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(int id)
    {
        var pr = await Db.PurchaseRequests.FindAsync(id);
        if (pr == null) return NotFound();
        if (pr.RequesterId != Me || pr.Status != PrStatus.Draft) return Forbid();
        return await DoSubmit(pr);
    }

    private async Task<IActionResult> DoSubmit(PurchaseRequest pr)
    {
        pr.SubmittedAt = DateTime.Now;
        Log(DocType.PR, pr.Id, "Gửi duyệt");
        // Người đề nghị là Trưởng bộ phận → bỏ qua cấp 1; là Giám đốc / Quản trị → duyệt luôn
        if (User.IsInRole(AppRoles.Director) || User.IsAdmin())
        {
            pr.Status = PrStatus.Approved;
            Log(DocType.PR, pr.Id, "Tự động duyệt (người đề nghị là Ban Giám đốc / Quản trị)");
        }
        else if (User.IsInRole(AppRoles.Approver))
        {
            pr.Status = pr.EstimatedTotal > Settings.PrDirectorThreshold ? PrStatus.PendingDirector : PrStatus.Approved;
            Log(DocType.PR, pr.Id, "Tự động duyệt cấp trưởng bộ phận (người đề nghị là trưởng bộ phận)");
        }
        else pr.Status = PrStatus.Submitted;
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã gửi {pr.Number} – {pr.Status.GetDisplayName()}.";
        return RedirectToAction(nameof(Details), new { id = pr.Id });
    }

    /// <summary>Duyệt / từ chối (cấp trưởng bộ phận hoặc Giám đốc tùy trạng thái)</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment, string? returnUrl)
    {
        var pr = await Db.PurchaseRequests.FindAsync(id);
        if (pr == null) return NotFound();
        if (!CanApprove(pr)) return Forbid();
        if (!approve && string.IsNullOrWhiteSpace(comment))
        {
            TempData["Error"] = "Vui lòng nhập lý do từ chối.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var level = pr.Status == PrStatus.Submitted ? "Trưởng bộ phận" : "Giám đốc";
        if (!approve)
        {
            pr.Status = PrStatus.Rejected;
            Log(DocType.PR, id, $"{level} từ chối", comment);
        }
        else if (pr.Status == PrStatus.Submitted)
        {
            pr.Status = pr.EstimatedTotal > Settings.PrDirectorThreshold ? PrStatus.PendingDirector : PrStatus.Approved;
            Log(DocType.PR, id, "Trưởng bộ phận duyệt", comment);
        }
        else
        {
            pr.Status = PrStatus.Approved;
            Log(DocType.PR, id, "Giám đốc duyệt", comment);
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = $"{pr.Number}: {pr.Status.GetDisplayName()}.";
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? comment)
    {
        var pr = await Db.PurchaseRequests.FindAsync(id);
        if (pr == null) return NotFound();
        var allowed = (pr.RequesterId == Me && pr.Status is PrStatus.Draft or PrStatus.Submitted or PrStatus.Rejected)
                      || (User.IsPurchasing() && pr.Status is PrStatus.Approved);
        if (!allowed) return Forbid();
        pr.Status = PrStatus.Cancelled;
        Log(DocType.PR, id, "Hủy đề nghị", comment);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã hủy {pr.Number}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private bool CanApprove(PurchaseRequest pr)
    {
        if (pr.RequesterId == Me && !User.IsAdmin()) return false; // không tự duyệt
        return pr.Status switch
        {
            PrStatus.Submitted => User.IsAdmin() || (User.IsInRole(AppRoles.Approver) && pr.Department == User.GetDepartment()),
            PrStatus.PendingDirector => User.IsAdmin() || User.IsInRole(AppRoles.Director),
            _ => false
        };
    }
}
