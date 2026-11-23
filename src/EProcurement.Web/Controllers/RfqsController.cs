using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>
/// Yêu cầu báo giá (RFQ): mời nhiều NCC → nhập báo giá từng NCC → so sánh → chọn NCC → sinh PO.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class RfqsController : ProcControllerBase
{
    public RfqsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Index(RfqStatus? status)
    {
        var q = Db.Rfqs.Include(r => r.Buyer).Include(r => r.Vendors).Include(r => r.AwardedVendor).Include(r => r.PurchaseRequest).AsNoTracking();
        if (status.HasValue) q = q.Where(r => r.Status == status);
        ViewBag.Status = status;
        return View(await q.OrderByDescending(r => r.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        ViewBag.History = await HistoryAsync(DocType.RFQ, id);
        ViewBag.Po = await Db.PurchaseOrders.FirstOrDefaultAsync(p => p.RfqId == id && p.Status != PoStatus.Cancelled);
        return View(r);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create(int? prId)
    {
        var vm = new RfqCreateViewModel { PurchaseRequestId = prId };
        if (prId.HasValue)
        {
            var pr = await Db.PurchaseRequests.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == prId);
            if (pr == null) return NotFound();
            if (pr.Status is not (PrStatus.Approved or PrStatus.Processing))
            {
                TempData["Error"] = "Chỉ lập RFQ cho đề nghị đã được duyệt.";
                return RedirectToAction("Details", "PurchaseRequests", new { id = prId });
            }
            vm.PurchaseRequest = pr;
            vm.Title = pr.Title;
            vm.Lines = pr.Lines.OrderBy(l => l.SortOrder)
                .Select(l => new LineInput { ItemId = l.ItemId, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity, Price = l.EstimatedPrice }).ToList();
        }
        if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
        await LoadVendorChoicesAsync(vm);
        vm.VendorIds = vm.SuggestedVendors.Take(Settings.MinVendorsPerRfq).Select(v => v.Id).ToList();
        await LoadItemsJsonAsync();
        return View(vm);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RfqCreateViewModel vm, bool publish = false)
    {
        vm.Lines = CleanLines(vm.Lines);
        vm.VendorIds = vm.VendorIds.Distinct().ToList();
        if (vm.VendorIds.Count < Settings.MinVendorsPerRfq)
            ModelState.AddModelError(nameof(vm.VendorIds), $"Cần mời tối thiểu {Settings.MinVendorsPerRfq} nhà cung cấp để đảm bảo cạnh tranh.");
        if (vm.Deadline < DateTime.Today) ModelState.AddModelError(nameof(vm.Deadline), "Hạn nộp báo giá không được ở quá khứ");
        var invalid = await Db.Vendors.Where(v => vm.VendorIds.Contains(v.Id) && v.Status != VendorStatus.Approved).Select(v => v.Name).ToListAsync();
        if (invalid.Count > 0) ModelState.AddModelError(nameof(vm.VendorIds), "NCC chưa được phê duyệt: " + string.Join(", ", invalid));
        if (!ModelState.IsValid)
        {
            if (vm.Lines.Count == 0) vm.Lines.Add(new LineInput());
            if (vm.PurchaseRequestId.HasValue) vm.PurchaseRequest = await Db.PurchaseRequests.FindAsync(vm.PurchaseRequestId);
            await LoadVendorChoicesAsync(vm);
            await LoadItemsJsonAsync();
            return View(vm);
        }

        var rfq = new Rfq
        {
            Number = await DocNumbers.NextAsync(Db.Rfqs.Select(r => r.Number), "RFQ"), Title = vm.Title, PurchaseRequestId = vm.PurchaseRequestId,
            BuyerId = Me, Deadline = vm.Deadline, Requirements = vm.Requirements, IssueDate = DateTime.Today,
            Status = publish ? RfqStatus.Open : RfqStatus.Draft, CreatedAt = DateTime.Now
        };
        var s = 1;
        foreach (var l in vm.Lines)
            rfq.Lines.Add(new RfqLine { ItemId = l.ItemId, Description = l.Description.Trim(), Unit = l.Unit, Quantity = l.Quantity, SortOrder = s++ });
        foreach (var vid in vm.VendorIds) rfq.Vendors.Add(new RfqVendor { VendorId = vid });
        Db.Rfqs.Add(rfq);
        await Db.SaveChangesAsync();

        Log(DocType.RFQ, rfq.Id, publish ? "Phát hành RFQ" : "Tạo RFQ nháp");
        if (vm.PurchaseRequestId.HasValue)
        {
            var pr = await Db.PurchaseRequests.FindAsync(vm.PurchaseRequestId);
            if (pr is { Status: PrStatus.Approved })
            {
                pr.Status = PrStatus.Processing;
                Log(DocType.PR, pr.Id, $"Mua hàng lập {rfq.Number}");
            }
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã tạo {rfq.Number}, mời {vm.VendorIds.Count} nhà cung cấp.";
        return RedirectToAction(nameof(Details), new { id = rfq.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id)
    {
        var r = await Db.Rfqs.FindAsync(id);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.Draft) return BadRequest();
        r.Status = RfqStatus.Open;
        r.IssueDate = DateTime.Today;
        Log(DocType.RFQ, id, "Phát hành RFQ");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã phát hành {r.Number}. In thư mời gửi NCC từ trang chi tiết.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Thư mời báo giá (in / gửi NCC)</summary>
    public async Task<IActionResult> Invitation(int id, int vendorId)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        var rv = r.Vendors.FirstOrDefault(v => v.VendorId == vendorId);
        if (rv == null) return NotFound();
        ViewBag.Vendor = rv.Vendor;
        return View(r);
    }

    /// <summary>Nhập báo giá nhận được từ một NCC</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> EnterQuote(int rfqVendorId)
    {
        var rv = await Db.RfqVendors.Include(x => x.Vendor).Include(x => x.QuoteLines).Include(x => x.Rfq).ThenInclude(r => r!.Lines)
            .FirstOrDefaultAsync(x => x.Id == rfqVendorId);
        if (rv == null) return NotFound();
        if (rv.Rfq!.Status != RfqStatus.Open)
        {
            TempData["Error"] = "Chỉ nhập báo giá khi RFQ đang mở.";
            return RedirectToAction(nameof(Details), new { id = rv.RfqId });
        }
        return View(new QuoteEntryViewModel
        {
            RfqVendorId = rv.Id, RfqVendor = rv, LeadTimeDays = rv.LeadTimeDays, PaymentTerms = rv.PaymentTerms ?? rv.Vendor?.PaymentTerms,
            Warranty = rv.Warranty, VatPercent = Math.Round(rv.VatPercent, 0), Note = rv.Note,
            Lines = rv.Rfq.Lines.OrderBy(l => l.SortOrder).Select(l => new QuoteEntryLine
            {
                RfqLineId = l.Id, Description = l.Description, Unit = l.Unit, Quantity = l.Quantity,
                UnitPrice = rv.QuoteLines.FirstOrDefault(q => q.RfqLineId == l.Id)?.UnitPrice ?? 0,
                Note = rv.QuoteLines.FirstOrDefault(q => q.RfqLineId == l.Id)?.Note
            }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnterQuote(QuoteEntryViewModel vm)
    {
        var rv = await Db.RfqVendors.Include(x => x.QuoteLines).Include(x => x.Rfq).ThenInclude(r => r!.Lines)
            .FirstOrDefaultAsync(x => x.Id == vm.RfqVendorId);
        if (rv == null) return NotFound();
        if (rv.Rfq!.Status != RfqStatus.Open) return BadRequest();
        if (vm.Lines.All(l => l.UnitPrice <= 0)) ModelState.AddModelError(string.Empty, "Nhập đơn giá cho ít nhất một dòng.");
        if (!ModelState.IsValid)
        {
            vm.RfqVendor = await Db.RfqVendors.Include(x => x.Vendor).Include(x => x.Rfq).FirstOrDefaultAsync(x => x.Id == vm.RfqVendorId);
            return View(vm);
        }

        Db.VendorQuoteLines.RemoveRange(rv.QuoteLines);
        rv.QuoteLines.Clear();
        foreach (var l in vm.Lines.Where(l => l.UnitPrice > 0))
        {
            var line = rv.Rfq.Lines.FirstOrDefault(x => x.Id == l.RfqLineId);
            if (line == null) continue;
            rv.QuoteLines.Add(new VendorQuoteLine { RfqLineId = line.Id, UnitPrice = l.UnitPrice, Amount = Math.Round(l.UnitPrice * line.Quantity, 0), Note = l.Note });
        }
        rv.TotalAmount = rv.QuoteLines.Sum(q => q.Amount);
        rv.Responded = true;
        rv.RespondedAt = DateTime.Now;
        rv.LeadTimeDays = vm.LeadTimeDays; rv.PaymentTerms = vm.PaymentTerms; rv.Warranty = vm.Warranty;
        rv.VatPercent = vm.VatPercent; rv.Note = vm.Note;
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã lưu báo giá của nhà cung cấp.";
        return RedirectToAction(nameof(Details), new { id = rv.RfqId });
    }

    /// <summary>Bảng so sánh báo giá</summary>
    public async Task<IActionResult> Compare(int id)
    {
        var r = await LoadRfqAsync(id);
        if (r == null) return NotFound();
        var vm = new ComparisonViewModel { Rfq = r, Responded = r.Vendors.Where(v => v.Responded).OrderBy(v => v.TotalAmount).ToList() };
        foreach (var line in r.Lines)
        {
            var prices = vm.Responded.SelectMany(v => v.QuoteLines).Where(q => q.RfqLineId == line.Id && q.UnitPrice > 0).Select(q => q.UnitPrice).ToList();
            if (prices.Count > 0) vm.LowestByLine[line.Id] = prices.Min();
        }
        // Chỉ xét NCC báo giá đủ tất cả các dòng cho "giá tổng thấp nhất"
        vm.LowestTotalVendorId = vm.Responded.Where(v => v.QuoteLines.Count(q => q.UnitPrice > 0) == r.Lines.Count)
            .OrderBy(v => v.TotalAmount).ThenByDescending(v => v.Vendor?.Rating ?? 0).FirstOrDefault()?.VendorId;
        if (r.PurchaseRequestId.HasValue)
            vm.ReferenceTotal = (await Db.PurchaseRequests.FindAsync(r.PurchaseRequestId))?.EstimatedTotal ?? 0;
        else
            vm.ReferenceTotal = r.Lines.Sum(l => l.Quantity * (l.Item?.ReferencePrice ?? 0));
        return View(vm);
    }

    /// <summary>Chọn NCC trúng → đóng RFQ → sinh PO nháp theo giá đã báo</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Award(int id, int vendorId, string? reason)
    {
        var r = await LoadRfqAsync(id, track: true);
        if (r == null) return NotFound();
        if (r.Status != RfqStatus.Open) return BadRequest();
        var rv = r.Vendors.FirstOrDefault(v => v.VendorId == vendorId && v.Responded);
        if (rv == null) return BadRequest();
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Nhập lý do chọn nhà cung cấp.";
            return RedirectToAction(nameof(Compare), new { id });
        }
        var notLowest = r.Vendors.Where(v => v.Responded && v.QuoteLines.Count(q => q.UnitPrice > 0) == r.Lines.Count)
            .Any(v => v.TotalAmount < rv.TotalAmount);

        r.Status = RfqStatus.Awarded;
        r.AwardedVendorId = vendorId;
        r.AwardReason = reason;
        Log(DocType.RFQ, id, "Chọn NCC trúng: " + rv.Vendor!.Name + (notLowest ? " (không phải giá thấp nhất)" : ""), reason);

        var po = new PurchaseOrder
        {
            Number = await DocNumbers.NextAsync(Db.PurchaseOrders.Select(p => p.Number), "PO"),
            VendorId = vendorId, RfqId = r.Id, PurchaseRequestId = r.PurchaseRequestId, BuyerId = Me,
            DeliveryDate = DateTime.Today.AddDays(rv.LeadTimeDays ?? 7), PaymentTerms = rv.PaymentTerms ?? rv.Vendor.PaymentTerms,
            DeliveryAddress = Settings.DefaultDeliveryAddress, VatPercent = rv.VatPercent,
            Notes = string.IsNullOrEmpty(rv.Warranty) ? null : "Bảo hành: " + rv.Warranty, CreatedAt = DateTime.Now
        };
        var s = 1;
        foreach (var line in r.Lines.OrderBy(l => l.SortOrder))
        {
            var q = rv.QuoteLines.FirstOrDefault(x => x.RfqLineId == line.Id);
            if (q == null || q.UnitPrice <= 0) continue;
            po.Lines.Add(new PurchaseOrderLine { ItemId = line.ItemId, Description = line.Description, Unit = line.Unit, Quantity = line.Quantity, UnitPrice = q.UnitPrice, SortOrder = s++ });
        }
        po.Recalculate();
        Db.PurchaseOrders.Add(po);
        await Db.SaveChangesAsync();
        Log(DocType.PO, po.Id, $"Tạo từ {r.Number}");
        await Db.SaveChangesAsync();

        TempData["Success"] = $"Đã chọn {rv.Vendor.Name} và tạo đơn mua hàng nháp {po.Number}. Kiểm tra rồi trình duyệt / gửi NCC.";
        return RedirectToAction("Details", "PurchaseOrders", new { id = po.Id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var r = await Db.Rfqs.FindAsync(id);
        if (r == null) return NotFound();
        if (r.Status is RfqStatus.Awarded or RfqStatus.Cancelled) return BadRequest();
        r.Status = RfqStatus.Cancelled;
        Log(DocType.RFQ, id, "Hủy RFQ", reason);
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã hủy {r.Number}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<Rfq?> LoadRfqAsync(int id, bool track = false)
    {
        var q = Db.Rfqs.Include(r => r.Buyer).Include(r => r.PurchaseRequest).Include(r => r.AwardedVendor)
            .Include(r => r.Lines).ThenInclude(l => l.Item)
            .Include(r => r.Vendors).ThenInclude(v => v.Vendor)
            .Include(r => r.Vendors).ThenInclude(v => v.QuoteLines);
        var r = track ? await q.FirstOrDefaultAsync(x => x.Id == id) : await q.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (r != null) r.Lines = r.Lines.OrderBy(l => l.SortOrder).ToList();
        return r;
    }

    private async Task LoadVendorChoicesAsync(RfqCreateViewModel vm)
    {
        var itemIds = vm.Lines.Where(l => l.ItemId.HasValue).Select(l => l.ItemId!.Value).ToList();
        var catIds = await Db.Items.Where(i => itemIds.Contains(i.Id) && i.CategoryId != null).Select(i => i.CategoryId!.Value).Distinct().ToListAsync();
        var approved = await Db.Vendors.Include(v => v.Categories).Where(v => v.Status == VendorStatus.Approved)
            .OrderByDescending(v => v.Rating).ToListAsync();
        vm.SuggestedVendors = approved.Where(v => v.Categories.Any(c => catIds.Contains(c.CategoryId))).ToList();
        vm.OtherVendors = approved.Except(vm.SuggestedVendors).ToList();
    }
}
