using System.Text.Json;
using EProcurement.Web.Data;
using EProcurement.Web.Infrastructure;
using EProcurement.Web.Models.Entities;
using EProcurement.Web.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Controllers;

/// <summary>Hàm dùng chung: nhật ký phê duyệt, danh mục hàng cho form dòng chứng từ, ngưỡng duyệt.</summary>
public abstract class ProcControllerBase : Controller
{
    protected readonly EProcDbContext Db;
    protected readonly ProcurementSettings Settings;

    protected ProcControllerBase(EProcDbContext db, IOptions<ProcurementSettings> settings)
    {
        Db = db;
        Settings = settings.Value;
    }

    protected int Me => User.GetUserId();

    protected void Log(DocType type, int id, string action, string? comment = null) =>
        Db.ApprovalHistories.Add(new ApprovalHistory { DocumentType = type, DocumentId = id, UserId = Me, Action = action, Comment = comment, CreatedAt = DateTime.Now });

    protected Task<List<ApprovalHistory>> HistoryAsync(DocType type, int id) =>
        Db.ApprovalHistories.Include(h => h.User).Where(h => h.DocumentType == type && h.DocumentId == id)
            .OrderBy(h => h.CreatedAt).ToListAsync();

    /// <summary>Danh mục hàng hóa (JSON) cho bộ soạn dòng chứng từ</summary>
    protected async Task LoadItemsJsonAsync()
    {
        ViewBag.ItemsJson = JsonSerializer.Serialize(await Db.Items.Include(i => i.Category).Where(i => i.IsActive).OrderBy(i => i.Code)
            .Select(i => new { id = i.Id, code = i.Code, name = i.Name, unit = i.Unit, price = i.ReferencePrice }).ToListAsync());
    }

    /// <summary>Bỏ dòng trống, kiểm tra số lượng</summary>
    protected List<LineInput> CleanLines(List<LineInput> lines, string prefix = "Lines")
    {
        var result = lines.Where(l => l.ItemId.HasValue || !string.IsNullOrWhiteSpace(l.Description)).ToList();
        if (result.Count == 0) ModelState.AddModelError(string.Empty, "Chứng từ cần ít nhất một dòng hàng hóa / dịch vụ.");
        foreach (var l in result)
        {
            if (string.IsNullOrWhiteSpace(l.Description)) ModelState.AddModelError(string.Empty, "Mỗi dòng cần có diễn giải.");
            if (l.Quantity <= 0) ModelState.AddModelError(string.Empty, $"Số lượng dòng \"{l.Description}\" phải lớn hơn 0.");
        }
        return result;
    }

    /// <summary>Quyền xem PR: người đề nghị, trưởng bộ phận cùng phòng, Giám đốc, mua hàng, quản trị</summary>
    protected bool CanViewPr(PurchaseRequest pr) =>
        pr.RequesterId == Me || User.IsInRole(AppRoles.Director) || User.IsPurchasing()
        || (User.IsInRole(AppRoles.Approver) && pr.Department == User.GetDepartment());
}
