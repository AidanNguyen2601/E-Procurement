using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>File đính kèm chứng từ (lưu trong database). Hiện dùng cho tờ trình chỉ định thầu.</summary>
[Authorize(Roles = AppRoles.Purchasing + "," + AppRoles.Director)]
public class AttachmentsController : ProcControllerBase
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".jpg", ".jpeg", ".png", ".doc", ".docx", ".xls", ".xlsx", ".msg", ".eml", ".txt", ".zip" };

    public AttachmentsController(EProcDbContext db, IOptions<ProcurementSettings> s) : base(db, s) { }

    public async Task<IActionResult> Download(int id)
    {
        var a = await Db.Attachments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        return File(a.Data, a.ContentType, a.FileName);
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(64 * 1024 * 1024)]
    public async Task<IActionResult> Upload(DocType documentType, int documentId, IFormFile? file, string? note, string? returnUrl, bool isPublic = false)
    {
        var back = Back(returnUrl);
        if (!await CanEditAsync(documentType, documentId)) { TempData["Error"] = I18n.T("Chứng từ không còn ở trạng thái cho phép đính kèm."); return back; }
        if (file == null || file.Length == 0) { TempData["Error"] = I18n.T("Chọn file cần đính kèm."); return back; }
        if (file.Length > Settings.MaxAttachmentMb * 1024L * 1024L) { TempData["Error"] = I18n.T("File vượt quá {0} MB.", Settings.MaxAttachmentMb); return back; }
        var ext = Path.GetExtension(file.FileName);
        if (!AllowedExtensions.Contains(ext)) { TempData["Error"] = I18n.T("Không hỗ trợ định dạng {0}. Cho phép: {1}.", ext, string.Join(", ", AllowedExtensions)); return back; }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var name = Path.GetFileName(file.FileName);
        Db.Attachments.Add(new Attachment
        {
            DocumentType = documentType, DocumentId = documentId, FileName = name.Length > 260 ? name[^260..] : name,
            ContentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType,
            Size = file.Length, Data = ms.ToArray(), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IsPublic = isPublic && documentType == DocType.Tender, UploadedById = Me, UploadedAt = DateTime.Now
        });
        Log(documentType, documentId, isPublic && documentType == DocType.Tender ? "Đăng tải file HSMT cho nhà thầu" : "Đính kèm file", name);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã đính kèm {0}.", name);
        return back;
    }

    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? returnUrl)
    {
        var a = await Db.Attachments.FirstOrDefaultAsync(x => x.Id == id);
        if (a == null) return NotFound();
        if (!await CanEditAsync(a.DocumentType, a.DocumentId)) { TempData["Error"] = I18n.T("Chứng từ đã trình duyệt, không xóa file được."); return Back(returnUrl); }
        Db.Attachments.Remove(a);
        Log(a.DocumentType, a.DocumentId, "Xóa file đính kèm", a.FileName);
        await Db.SaveChangesAsync();
        TempData["Success"] = I18n.T("Đã xóa {0}.", a.FileName);
        return Back(returnUrl);
    }

    /// <summary>Gói thầu: chuyển file giữa "HSMT công khai cho nhà thầu" và "nội bộ" (chỉ trước đóng thầu)</summary>
    [Authorize(Roles = AppRoles.Purchasing)]
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TogglePublic(int id, string? returnUrl)
    {
        var a = await Db.Attachments.FirstOrDefaultAsync(x => x.Id == id && x.DocumentType == DocType.Tender);
        if (a == null) return NotFound();
        if (!await CanEditAsync(a.DocumentType, a.DocumentId)) { TempData["Error"] = I18n.T("Gói thầu đã đóng thầu – không đổi được."); return Back(returnUrl); }
        a.IsPublic = !a.IsPublic;
        Log(a.DocumentType, a.DocumentId, a.IsPublic ? "Công khai file HSMT cho nhà thầu" : "Ngừng công khai file", a.FileName);
        await Db.SaveChangesAsync();
        return Back(returnUrl);
    }

    /// <summary>Chỉ đính kèm / xóa khi chứng từ còn đang soạn</summary>
    private async Task<bool> CanEditAsync(DocType type, int id) => type switch
    {
        DocType.DirectAward => await Db.DirectAwards.AnyAsync(d => d.Id == id && d.Status == DirectAwardStatus.Draft),
        // HSMT, phụ lục: khi lập và trong thời gian mời thầu (trước đóng thầu)
        DocType.Tender => await Db.Tenders.AnyAsync(t => t.Id == id && (t.Status == TenderStatus.Draft || (t.Status == TenderStatus.Published && t.ClosingAt > DateTime.Now))),
        DocType.Contract => await Db.Contracts.AnyAsync(c => c.Id == id && c.Status == ContractStatus.Active),
        _ => false
    };

    private IActionResult Back(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home");
}
