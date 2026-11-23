using System.Security.Claims;
using EProcurement.Web.Data;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EProcurement.Web.Infrastructure;

/// <summary>
/// Hạn mức phê duyệt: theo phòng ban → mức mặc định đã cấu hình trên hệ thống → appsettings (Procurement).
/// </summary>
public class ApprovalMatrix
{
    private readonly EProcDbContext _db;
    private readonly ProcurementSettings _s;
    private List<ApprovalLimit>? _limits;
    public ApprovalMatrix(EProcDbContext db, IOptions<ProcurementSettings> s) { _db = db; _s = s.Value; }

    public async Task<decimal> LimitAsync(ApprovalLimitType type, string? department)
    {
        _limits ??= await _db.ApprovalLimits.AsNoTracking().ToListAsync();
        return _limits.FirstOrDefault(l => l.Type == type && department != null && l.Department == department)?.Amount
               ?? _limits.FirstOrDefault(l => l.Type == type && l.Department == null)?.Amount
               ?? Default(type);
    }

    public decimal Default(ApprovalLimitType type) => type == ApprovalLimitType.PrDirector ? _s.PrDirectorThreshold : _s.PoApprovalThreshold;

    public Task<decimal> PrDirectorAsync(string department) => LimitAsync(ApprovalLimitType.PrDirector, department);
    public Task<decimal> PoDirectorAsync(string? department) => LimitAsync(ApprovalLimitType.PoDirector, department);
}

/// <summary>
/// Ủy quyền phê duyệt: trong thời gian ủy quyền, người nhận được cộng thêm vai trò duyệt của người ủy quyền
/// (Giám đốc hoặc Trưởng bộ phận) – mọi kiểm tra quyền hiện có tự áp dụng. Kết quả được cache 1 phút / người dùng.
/// </summary>
public class DelegationClaimsTransformation : IClaimsTransformation
{
    public const string DelegatedClaim = "eproc:delegated";
    public const string BaseRoleClaim = "eproc:base-role";
    private readonly EProcDbContext _db;
    private readonly IMemoryCache _cache;
    public DelegationClaimsTransformation(EProcDbContext db, IMemoryCache cache) { _db = db; _cache = cache; }

    public static void Invalidate(IMemoryCache cache, int userId) => cache.Remove("deleg:" + userId);

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true || principal.HasClaim(c => c.Type == BaseRoleClaim)) return principal;
        var me = principal.GetUserId();
        if (me == 0) return principal;
        var list = await _cache.GetOrCreateAsync("deleg:" + me, async e =>
        {
            e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            var today = DateTime.Today;
            return await _db.ApprovalDelegations.AsNoTracking()
                .Where(d => d.ToUserId == me && d.RevokedAt == null && d.StartDate <= today && d.EndDate >= today && d.FromUser!.IsActive)
                .Select(d => new { d.FromUser!.Role, d.FromUser.FullName }).ToListAsync();
        }) ?? new();

        var id = new ClaimsIdentity();
        id.AddClaim(new Claim(BaseRoleClaim, principal.FindFirstValue(ClaimTypes.Role) ?? ""));
        foreach (var d in list.Where(d => d.Role is AppRoles.Director or AppRoles.Approver))
        {
            if (!principal.IsInRole(d.Role)) id.AddClaim(new Claim(ClaimTypes.Role, d.Role));
            id.AddClaim(new Claim(DelegatedClaim, $"{d.Role}|{d.FullName}"));
        }
        var clone = principal.Clone();
        clone.AddIdentity(id);
        return clone;
    }
}

public static class DelegationExtensions
{
    /// <summary>Ghi chú "thay mặt" khi người dùng thao tác ở cấp duyệt có được nhờ ủy quyền (null nếu không)</summary>
    public static string? DelegationNote(this ClaimsPrincipal u, string action)
    {
        var baseRole = u.FindFirstValue(DelegationClaimsTransformation.BaseRoleClaim);
        foreach (var c in u.FindAll(DelegationClaimsTransformation.DelegatedClaim))
        {
            var parts = c.Value.Split('|', 2);
            if (parts.Length < 2 || parts[0] == baseRole) continue;
            var level = parts[0] == AppRoles.Director ? "Giám đốc" : "Trưởng bộ phận";
            if (action.Contains(level, StringComparison.OrdinalIgnoreCase)) return $"[Thay mặt {parts[1]} theo ủy quyền]";
        }
        return null;
    }

    public static IEnumerable<string> DelegatedFrom(this ClaimsPrincipal u) =>
        u.FindAll(DelegationClaimsTransformation.DelegatedClaim).Select(c => c.Value.Split('|', 2).Last());
}
