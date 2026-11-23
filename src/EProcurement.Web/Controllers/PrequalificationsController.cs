using System.ComponentModel.DataAnnotations;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

public class PrequalificationForm
{
    public int Id { get; set; }
    public string Number { get; set; } = "(tự sinh khi lưu)";
    [Required(ErrorMessage = "Nhập tên đợt sơ tuyển"), StringLength(200)][Display(Name = "Tên đợt sơ tuyển")] public string Title { get; set; } = string.Empty;
    [Display(Name = "Nhóm hàng")] public int CategoryId { get; set; }
    [StringLength(2000)][Display(Name = "Phạm vi, yêu cầu chung")] public string? Description { get; set; }
    [Display(Name = "Hạn nộp hồ sơ")] public DateTime Deadline { get; set; } = DateTime.Today.AddDays(14).AddHours(17);
    [Range(1, 60)][Display(Name = "Hiệu lực kết quả (tháng)")] public int ValidityMonths { get; set; } = 12;
    [Range(0, 100)][Display(Name = "Điểm tối thiểu để đạt (thang 100)")] public int MinScore { get; set; } = 60;
    public List<CriterionInput> Criteria { get; set; } = new();
}

public class CriterionInput
{
    [StringLength(200)] public string? Name { get; set; }
    [StringLength(1000)] public string? Requirement { get; set; }
    public bool IsMandatory { get; set; }
    [Range(0, 100)] public int MaxScore { get; set; }
    public VendorDocumentType? DocumentType { get; set; }
}

/// <summary>
/// Quản lý nhà cung cấp đăng ký sơ tuyển: đợt sơ tuyển theo nhóm hàng (tiêu chí, hạn nộp) → NCC nộp hồ sơ trên cổng NCC
/// → chấm theo tiêu chí → Giám đốc duyệt danh sách đạt. Kèm danh sách NCC tự đăng ký qua cổng chờ thẩm định.
/// </summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class PrequalificationsController : ProcControllerBase
{
    public PrequalificationsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    private bool IsDirector => User.IsInRole(AppRoles.Director) || User.IsAdmin();

    public async Task<IActionResult> Index(string? tab, PrequalificationStatus? status)
    {
        ViewBag.Tab = tab == "registrations" ? "registrations" : "rounds";
        ViewBag.Status = status;
        // NCC tự đăng ký qua cổng, đang chờ thẩm định
        var pending = await Db.Vendors.AsNoTracking().Include(v => v.Categories).ThenInclude(c => c.Category)
            .Where(v => v.Status == VendorStatus.Pending).OrderBy(v => v.CreatedAt).ToListAsync();
        var ids = pending.Select(v => v.Id).ToList();
        var docs = await Db.VendorDocuments.Where(d => ids.Contains(d.VendorId)).GroupBy(d => d.VendorId)
            .Select(g => new { g.Key, N = g.Count(), V = g.Count(d => d.Status == VendorDocumentStatus.Verified) }).ToDictionaryAsync(x => x.Key);
        var apps = await Db.PrequalificationApplications.Where(a => ids.Contains(a.VendorId) && a.Status == PrequalificationResult.Submitted)
            .GroupBy(a => a.VendorId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N);
        ViewBag.Registrations = pending.Select(v => new RegistrationRow(v.Id, v.Code, v.Name, v.TaxCode, v.CreatedAt,
            v.Categories.Select(c => c.Category?.Name ?? "").ToList(), docs.TryGetValue(v.Id, out var d) ? d.N : 0, d?.V ?? 0, apps.GetValueOrDefault(v.Id))).ToList();
        var q = Db.Prequalifications.AsNoTracking().Include(p => p.Category).Include(p => p.Applications).AsQueryable();
        if (status.HasValue) q = q.Where(p => p.Status == status);
        return View(await q.OrderByDescending(p => p.CreatedAt).ToListAsync());
    }

    public record RegistrationRow(int Id, string Code, string Name, string TaxCode, DateTime CreatedAt, List<string> Categories, int Docs, int Verified, int Applications);

    private static List<CriterionInput> DefaultCriteria() => new()
    {
        new() { Name = "Tư cách pháp lý", Requirement = "Giấy chứng nhận đăng ký doanh nghiệp còn hiệu lực, ngành nghề phù hợp", IsMandatory = true, DocumentType = VendorDocumentType.BusinessLicense },
        new() { Name = "Năng lực tài chính", Requirement = "Báo cáo tài chính 2 năm gần nhất, không lỗ", IsMandatory = true, DocumentType = VendorDocumentType.Financial },
        new() { Name = "Kinh nghiệm", Requirement = "Số hợp đồng tương tự trong 3 năm gần nhất", MaxScore = 40, DocumentType = VendorDocumentType.Experience },
        new() { Name = "Chất lượng, ủy quyền hãng", Requirement = "Chứng chỉ ISO / ủy quyền của nhà sản xuất", MaxScore = 30, DocumentType = VendorDocumentType.Quality },
        new() { Name = "Dịch vụ, bảo hành", Requirement = "Mạng lưới bảo hành, thời gian đáp ứng", MaxScore = 30 }
    };

    private async Task LoadFormAsync() =>
        ViewBag.Categories = new SelectList(await Db.Categories.OrderBy(c => c.Name).ToListAsync(), "Id", "Name");

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Create()
    {
        await LoadFormAsync();
        return View("Form", new PrequalificationForm { Criteria = DefaultCriteria() });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Edit(int id)
    {
        var p = await Db.Prequalifications.Include(x => x.Criteria).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PrequalificationStatus.Draft) { TempData["Error"] = "Chỉ sửa đợt sơ tuyển ở trạng thái Nháp."; return RedirectToAction(nameof(Details), new { id }); }
        await LoadFormAsync();
        return View("Form", new PrequalificationForm
        {
            Id = p.Id, Number = p.Number, Title = p.Title, CategoryId = p.CategoryId, Description = p.Description, Deadline = p.Deadline,
            ValidityMonths = p.ValidityMonths, MinScore = p.MinScore,
            Criteria = p.Criteria.OrderBy(c => c.SortOrder).Select(c => new CriterionInput { Name = c.Name, Requirement = c.Requirement, IsMandatory = c.IsMandatory, MaxScore = c.MaxScore, DocumentType = c.DocumentType }).ToList()
        });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PrequalificationForm vm)
    {
        Prequalification? p = null;
        if (vm.Id != 0)
        {
            p = await Db.Prequalifications.Include(x => x.Criteria).FirstOrDefaultAsync(x => x.Id == vm.Id);
            if (p == null) return NotFound();
            if (p.Status != PrequalificationStatus.Draft) return BadRequest();
        }
        var criteria = vm.Criteria.Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (criteria.Count == 0) ModelState.AddModelError(string.Empty, "Cần ít nhất một tiêu chí sơ tuyển.");
        if (criteria.Any(c => !c.IsMandatory && c.MaxScore <= 0)) ModelState.AddModelError(string.Empty, "Tiêu chí chấm điểm cần điểm tối đa > 0 (hoặc đánh dấu là tiêu chí bắt buộc đạt / không đạt).");
        if (vm.Deadline <= DateTime.Now) ModelState.AddModelError(nameof(vm.Deadline), "Hạn nộp hồ sơ phải ở tương lai.");
        if (!await Db.Categories.AnyAsync(c => c.Id == vm.CategoryId)) ModelState.AddModelError(nameof(vm.CategoryId), "Chọn nhóm hàng.");
        if (!ModelState.IsValid) { await LoadFormAsync(); if (vm.Criteria.Count == 0) vm.Criteria = DefaultCriteria(); return View("Form", vm); }

        if (p == null)
        {
            p = new Prequalification { Number = await DocNumbers.NextAsync(Db.Prequalifications.Select(x => x.Number), "ST"), CreatedById = Me, CreatedAt = DateTime.Now };
            Db.Prequalifications.Add(p);
        }
        else Db.PrequalificationCriteria.RemoveRange(p.Criteria);
        p.Title = vm.Title.Trim(); p.CategoryId = vm.CategoryId; p.Description = vm.Description; p.Deadline = vm.Deadline;
        p.ValidityMonths = vm.ValidityMonths; p.MinScore = vm.MinScore;
        p.Criteria = criteria.Select((c, i) => new PrequalificationCriterion
        {
            Name = c.Name!.Trim(), Requirement = c.Requirement, IsMandatory = c.IsMandatory, MaxScore = c.IsMandatory ? 0 : c.MaxScore, DocumentType = c.DocumentType, SortOrder = i + 1
        }).ToList();
        await Db.SaveChangesAsync();
        if (vm.Id == 0) { Log(DocType.Prequalification, p.Id, "Lập đợt sơ tuyển nhà cung cấp"); await Db.SaveChangesAsync(); }
        TempData["Success"] = $"Đã lưu {p.Number}. Đăng tải để NCC nộp hồ sơ trên cổng NCC.";
        return RedirectToAction(nameof(Details), new { id = p.Id });
    }

    private Task<Prequalification?> LoadAsync(int id, bool track = false)
    {
        var q = Db.Prequalifications.Include(x => x.Category).Include(x => x.CreatedBy).Include(x => x.DecidedBy).Include(x => x.Criteria)
            .Include(x => x.Applications).ThenInclude(a => a.Vendor)
            .Include(x => x.Applications).ThenInclude(a => a.Scores)
            .Include(x => x.Applications).ThenInclude(a => a.EvaluatedBy)
            .Include(x => x.Applications).ThenInclude(a => a.Documents).ThenInclude(d => d.VendorDocument)
            .AsSplitQuery();
        return (track ? q : q.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<IActionResult> Details(int id)
    {
        var p = await LoadAsync(id);
        if (p == null) return NotFound();
        p.Criteria = p.Criteria.OrderBy(c => c.SortOrder).ToList();
        ViewBag.History = await HistoryAsync(DocType.Prequalification, id);
        ViewBag.CanDecide = IsDirector && p.Status == PrequalificationStatus.PendingApproval;
        if (User.IsPurchasing() && p.Status is PrequalificationStatus.Open or PrequalificationStatus.Evaluating)
            ViewBag.Vendors = new SelectList(await Db.Vendors.Where(v => v.Status != VendorStatus.Blacklisted && !p.Applications.Select(a => a.VendorId).Contains(v.Id))
                .OrderBy(v => v.Name).Select(v => new { v.Id, Name = v.Code + " - " + v.Name }).ToListAsync(), "Id", "Name");
        return View(p);
    }

    public async Task<IActionResult> Print(int id)
    {
        var p = await LoadAsync(id);
        if (p == null) return NotFound();
        p.Criteria = p.Criteria.OrderBy(c => c.SortOrder).ToList();
        return View(p);
    }

    private IActionResult Fail(int id, string m) { TempData["Error"] = m; return RedirectToAction(nameof(Details), new { id }); }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(int id)
    {
        var p = await Db.Prequalifications.Include(x => x.Criteria).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PrequalificationStatus.Draft) return Fail(id, "Đợt sơ tuyển đã đăng tải.");
        if (p.Deadline <= DateTime.Now.AddDays(1)) return Fail(id, "Hạn nộp hồ sơ phải còn ít nhất 1 ngày kể từ khi đăng tải.");
        p.Status = PrequalificationStatus.Open; p.PublishedAt = DateTime.Now;
        Log(DocType.Prequalification, id, "Đăng tải thông báo mời sơ tuyển trên cổng NCC", $"Hạn nộp {p.Deadline.ToDateTime()}");
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã đăng tải – NCC xem và nộp hồ sơ trên cổng NCC.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Mua hàng tiếp nhận hồ sơ giấy của NCC (khi NCC không nộp qua cổng)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddApplication(int id, int vendorId, string? note)
    {
        var p = await Db.Prequalifications.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status is not (PrequalificationStatus.Open or PrequalificationStatus.Evaluating)) return Fail(id, "Đợt sơ tuyển không còn nhận hồ sơ.");
        if (p.Status == PrequalificationStatus.Open && DateTime.Now > p.Deadline || p.Status == PrequalificationStatus.Evaluating) return Fail(id, "Đã quá hạn nộp hồ sơ.");
        if (await Db.PrequalificationApplications.AnyAsync(a => a.PrequalificationId == id && a.VendorId == vendorId)) return Fail(id, "NCC đã có hồ sơ trong đợt này.");
        var v = await Db.Vendors.FindAsync(vendorId);
        if (v == null || v.Status == VendorStatus.Blacklisted) return Fail(id, "NCC không hợp lệ.");
        Db.PrequalificationApplications.Add(new PrequalificationApplication { PrequalificationId = id, VendorId = vendorId, Note = note?.Trim(), SubmittedAt = DateTime.Now });
        Log(DocType.Prequalification, id, $"Tiếp nhận hồ sơ dự sơ tuyển (bản giấy) của {v.Name}");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"Đã tiếp nhận hồ sơ của {v.Name}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Đóng nhận hồ sơ, chuyển sang đánh giá (sau hạn nộp)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(int id)
    {
        var p = await Db.Prequalifications.Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PrequalificationStatus.Open) return Fail(id, "Đợt sơ tuyển không ở trạng thái nhận hồ sơ.");
        if (DateTime.Now < p.Deadline) return Fail(id, $"Chưa đến hạn nộp hồ sơ ({p.Deadline.ToDateTime()}).");
        p.Status = PrequalificationStatus.Evaluating;
        Log(DocType.Prequalification, id, $"Đóng nhận hồ sơ – {p.Applications.Count(a => a.Status != PrequalificationResult.Withdrawn)} hồ sơ");
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    public async Task<IActionResult> Evaluate(int id, int applicationId)
    {
        var p = await LoadAsync(id);
        if (p == null) return NotFound();
        var a = p.Applications.FirstOrDefault(x => x.Id == applicationId);
        if (a == null) return NotFound();
        if (p.Status != PrequalificationStatus.Evaluating || a.Status == PrequalificationResult.Withdrawn) return Fail(id, "Chỉ chấm hồ sơ khi đợt sơ tuyển đang đánh giá.");
        p.Criteria = p.Criteria.OrderBy(c => c.SortOrder).ToList();
        ViewBag.Application = a;
        ViewBag.VendorDocs = await Db.VendorDocuments.AsNoTracking().Where(d => d.VendorId == a.VendorId).OrderBy(d => d.Type).ToListAsync();
        return View(p);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Evaluate(int id, int applicationId, List<int> criterionIds, List<string?> passes, List<decimal?> scores, List<string?> comments, string? resultNote)
    {
        var p = await LoadAsync(id, track: true);
        if (p == null) return NotFound();
        var a = p.Applications.FirstOrDefault(x => x.Id == applicationId);
        if (a == null) return NotFound();
        if (p.Status != PrequalificationStatus.Evaluating || a.Status == PrequalificationResult.Withdrawn) return Fail(id, "Chỉ chấm hồ sơ khi đợt sơ tuyển đang đánh giá.");
        for (var i = 0; i < criterionIds.Count; i++)
        {
            var c = p.Criteria.FirstOrDefault(x => x.Id == criterionIds[i]);
            if (c == null) continue;
            var s = a.Scores.FirstOrDefault(x => x.CriterionId == c.Id);
            if (s == null) { s = new PrequalificationScore { CriterionId = c.Id }; a.Scores.Add(s); }
            var pass = i < passes.Count ? passes[i] : null;
            s.Pass = c.IsMandatory ? pass switch { "true" => true, "false" => false, _ => null } : null;
            var sc = i < scores.Count ? scores[i] : null;
            if (!c.IsMandatory && sc.HasValue && (sc < 0 || sc > c.MaxScore)) return Fail(id, $"\"{c.Name}\": điểm từ 0 đến {c.MaxScore}.");
            s.Score = c.IsMandatory ? null : sc;
            s.Comment = i < comments.Count ? comments[i]?.Trim() : null;
        }
        var r = PrequalificationRules.Evaluate(p, a.Scores.ToList());
        if (r == null) { await Db.SaveChangesAsync(); TempData["Error"] = "Đã lưu – còn tiêu chí chưa chấm."; return RedirectToAction(nameof(Evaluate), new { id, applicationId }); }
        a.Status = r.Value.Passed ? PrequalificationResult.Passed : PrequalificationResult.Failed;
        a.TotalScore = r.Value.Total; a.EvaluatedById = Me; a.EvaluatedAt = DateTime.Now; a.ResultNote = string.IsNullOrWhiteSpace(resultNote) ? null : resultNote.Trim();
        Log(DocType.Prequalification, id, $"Chấm hồ sơ {a.Vendor?.Name}: {(r.Value.Passed ? "đạt" : "không đạt")} ({r.Value.Total:0.##} điểm)");
        await Db.SaveChangesAsync();
        TempData["Success"] = $"{a.Vendor?.Name}: {(r.Value.Passed ? "ĐẠT" : "KHÔNG ĐẠT")} – {r.Value.Total:0.##}/100 điểm.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Trình Giám đốc phê duyệt kết quả sơ tuyển (mọi hồ sơ đã chấm)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitResult(int id)
    {
        var p = await Db.Prequalifications.Include(x => x.Applications).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PrequalificationStatus.Evaluating) return Fail(id, "Đợt sơ tuyển không ở bước đánh giá.");
        var active = p.Applications.Where(a => a.Status != PrequalificationResult.Withdrawn).ToList();
        if (active.Count == 0) return Fail(id, "Không có hồ sơ nào – hủy đợt sơ tuyển hoặc gia hạn.");
        if (active.Any(a => a.Status == PrequalificationResult.Submitted)) return Fail(id, "Còn hồ sơ chưa chấm.");
        p.Status = PrequalificationStatus.PendingApproval;
        Log(DocType.Prequalification, id, $"Trình duyệt kết quả sơ tuyển: {active.Count(a => a.Status == PrequalificationResult.Passed)}/{active.Count} NCC đạt");
        await Db.SaveChangesAsync();
        TempData["Success"] = "Đã trình Giám đốc phê duyệt kết quả sơ tuyển.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Giám đốc phê duyệt (ký): NCC đạt được công nhận cho nhóm hàng trong thời hạn hiệu lực; NCC chờ thẩm định chuyển Đã duyệt</summary>
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.Director)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequireSignature(DocType.Prequalification)]
    public async Task<IActionResult> Decide(int id, bool approve, string? comment)
    {
        var p = await Db.Prequalifications.Include(x => x.Applications).ThenInclude(a => a.Vendor).ThenInclude(v => v!.Categories).FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status != PrequalificationStatus.PendingApproval) return BadRequest();
        if (!approve && string.IsNullOrWhiteSpace(comment)) return Fail(id, "Nhập lý do trả lại.");
        p.DecidedById = Me; p.DecidedAt = DateTime.Now; p.DecisionNote = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (!approve)
        {
            p.Status = PrequalificationStatus.Evaluating;
            Log(DocType.Prequalification, id, "Giám đốc trả lại kết quả sơ tuyển", p.DecisionNote);
        }
        else
        {
            p.Status = PrequalificationStatus.Completed;
            var until = DateTime.Today.AddMonths(p.ValidityMonths);
            foreach (var a in p.Applications.Where(a => a.Status == PrequalificationResult.Passed))
            {
                a.ValidUntil = until;
                var v = a.Vendor!;
                if (v.Categories.All(c => c.CategoryId != p.CategoryId)) v.Categories.Add(new VendorCategory { CategoryId = p.CategoryId });
                if (v.Status == VendorStatus.Pending)
                {
                    v.Status = VendorStatus.Approved;
                    v.Note = (v.Note + $"\nĐạt sơ tuyển {p.Number} – phê duyệt {DateTime.Today:dd/MM/yyyy}.").Trim();
                }
            }
            Log(DocType.Prequalification, id, "Giám đốc phê duyệt kết quả sơ tuyển", $"{p.Applications.Count(a => a.Status == PrequalificationResult.Passed)} NCC đạt, hiệu lực đến {until:dd/MM/yyyy}");
        }
        await Db.SaveChangesAsync();
        TempData["Success"] = approve ? "Đã phê duyệt kết quả sơ tuyển." : "Đã trả lại kết quả sơ tuyển.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        var p = await Db.Prequalifications.FirstOrDefaultAsync(x => x.Id == id);
        if (p == null) return NotFound();
        if (p.Status is PrequalificationStatus.Completed or PrequalificationStatus.Cancelled) return Fail(id, "Không hủy được.");
        if (string.IsNullOrWhiteSpace(reason)) return Fail(id, "Nhập lý do hủy.");
        p.Status = PrequalificationStatus.Cancelled;
        Log(DocType.Prequalification, id, "Hủy đợt sơ tuyển", reason.Trim());
        await Db.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }
}
