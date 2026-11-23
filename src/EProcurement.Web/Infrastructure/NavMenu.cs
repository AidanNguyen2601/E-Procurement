using System.Security.Claims;
using EProcurement.Web.Infrastructure.Reporting;
using EProcurement.Web.Models.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace EProcurement.Web.Infrastructure;

/// <summary>Một mục menu: liên kết (có Href) hoặc nhóm (có Children)</summary>
public class NavItem
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Icon { get; init; } = "dot";
    public string? Href { get; init; }
    public bool External { get; init; }
    public bool Active { get; set; }
    public List<NavItem> Children { get; } = new();
    public bool IsGroup => Href == null;
    public bool HasActive => Active || Children.Any(c => c.HasActive);
    /// <summary>Mọi liên kết trong nhóm (kể cả nhóm con)</summary>
    public IEnumerable<NavItem> Links => IsGroup ? Children.SelectMany(c => c.Links) : new[] { this };
}

/// <summary>
/// Menu chính theo vai trò, gom thành 7 nhóm nghiệp vụ. Dùng chung cho menu dọc (trái / phải) và menu ngang (trên / dưới).
/// </summary>
public static class NavMenu
{
    /// <summary>Khóa mục đang xem: theo controller, riêng Đấu thầu / Báo giá / Báo cáo nguồn cung theo action</summary>
    public static string CurrentKey(ViewContext vc)
    {
        var ctrl = vc.RouteData.Values["controller"]?.ToString();
        var act = vc.RouteData.Values["action"]?.ToString();
        var req = vc.HttpContext.Request;
        return (ctrl, act) switch
        {
            ("Tenders", "Evaluations" or "Screen" or "Score") => "td-eval",
            ("Tenders", "Negotiations" or "NegotiationMinutes") => "td-neg",
            ("Tenders", "EvaluationReports" or "Report") => "td-report",
            ("Tenders", "Appraisals" or "AppraisalReport") => "td-appraisal",
            ("Tenders", _) => "td",
            ("Quotes", "Direct") => "q-direct",
            ("Quotes", "Competitive") => "q-comp",
            ("Quotes", "Evaluations") or ("Rfqs", "Evaluate" or "Compare") => "q-eval",
            ("Reports", "Index") when req.Query["group"] == "sourcing" => "src-report",
            ("Reports", "Show") when StandardReports.SourcingKeys.Contains(vc.RouteData.Values["id"]?.ToString()) => "src-report",
            _ => ctrl ?? ""
        };
    }

    public static List<NavItem> Build(IUrlHelper url, ClaimsPrincipal user, string currentKey)
    {
        var purchasing = user.IsPurchasing();
        var director = user.IsInRole(AppRoles.Director);
        var admin = user.IsAdmin();
        var accountant = user.IsInRole(AppRoles.Accountant);
        var approver = user.IsInRole(AppRoles.Approver) || director || admin;
        var pd = purchasing || director;
        var finance = pd || accountant;

        NavItem L(string key, string label, string icon, string controller, string action = "Index", object? route = null, bool external = false) =>
            new() { Key = key, Label = label, Icon = icon, Href = url.Action(action, controller, route), External = external };
        NavItem G(string key, string label, string icon, params NavItem?[] children)
        {
            var g = new NavItem { Key = key, Label = label, Icon = icon };
            g.Children.AddRange(children.Where(c => c != null && (!c.IsGroup || c.Children.Count > 0))!);
            return g;
        }

        var menu = new List<NavItem?>
        {
            L("Home", "Dashboard", "dashboard", "Home"),
            approver ? L("Approvals", "Chờ tôi duyệt", "inbox", "Approvals") : null,

            G("plan", "Kế hoạch & yêu cầu", "clipboard",
                L("Plans", "Kế hoạch mua sắm năm", "clipboard", "Plans"),
                purchasing || approver ? L("Budgets", "Ngân sách", "wallet", "Budgets") : null,
                L("PurchaseRequests", "Yêu cầu mua sắm", "file-text", "PurchaseRequests"),
                pd ? L("Packages", "Gói mua sắm", "package", "Packages") : null,
                pd ? L("SelectionPlans", "Kế hoạch lựa chọn NCC", "clipboard", "SelectionPlans") : null),

            G("select", "Lựa chọn nhà cung cấp", "trophy",
                pd ? G("select-direct", "Mua sắm trực tiếp", "award",
                    L("DirectAwards", "Hồ sơ chào giá chỉ định", "file-text", "DirectAwards"),
                    L("q-direct", "Báo giá nhà cung cấp", "receipt", "Quotes", "Direct")) : null,
                pd ? G("select-cq", "Chào giá cạnh tranh", "send",
                    L("Rfqs", "Hồ sơ chào giá cạnh tranh", "file-text", "Rfqs"),
                    L("q-comp", "Báo giá chào hàng cạnh tranh", "receipt", "Quotes", "Competitive"),
                    L("q-eval", "Đánh giá hồ sơ chào giá", "scale", "Quotes", "Evaluations")) : null,
                G("select-td", pd ? "Đấu thầu" : "Đấu thầu (tổ chuyên gia)", "gavel",
                    L("td", "Hồ sơ mời thầu / yêu cầu", "file-text", "Tenders"),
                    L("td-eval", "Đánh giá HSDT/HSCG", "scale", "Tenders", "Evaluations"),
                    L("td-neg", "Đàm phán hợp đồng", "users", "Tenders", "Negotiations"),
                    L("td-report", "Báo cáo đánh giá LCNT", "bar-chart", "Tenders", "EvaluationReports"),
                    L("td-appraisal", "Thẩm định kết quả LCNT", "shield-check", "Tenders", "Appraisals")),
                L("public-notices", "Thông báo mời thầu", "megaphone", "Tenders", "Public", external: true)),

            G("exec", "Hợp đồng & thanh toán", "file-check",
                finance ? L("Contracts", "Hợp đồng mua sắm", "file-check", "Contracts") : null,
                finance ? L("PurchaseOrders", "Đơn mua hàng", "cart", "PurchaseOrders") : null,
                L("Ordering", "Mua theo hợp đồng khung", "cart", "Ordering"),
                pd ? L("GoodsReceipts", "Giao nhận hàng", "truck", "GoodsReceipts") : null,
                finance ? L("Invoices", "Hoá đơn", "receipt", "Invoices") : null,
                finance ? L("Payments", "Đề nghị thanh toán", "coins", "Payments") : null),

            pd ? G("src", "Nguồn cung", "network",
                L("Vendors", "Nhà cung cấp", "building", "Vendors"),
                L("Prequalifications", "Sơ tuyển nhà cung cấp", "clipboard", "Prequalifications"),
                L("SupplierEvaluations", "Đánh giá nhà cung cấp", "users", "SupplierEvaluations"),
                L("PriceBank", "Ngân hàng giá", "database", "PriceBank"),
                L("StandardPrices", "Bảng giá tiêu chuẩn", "tag", "StandardPrices"),
                L("src-report", "Báo cáo nguồn cung", "bar-chart", "Reports", route: new { group = "sourcing" })) : null,

            finance ? G("rpt", "Báo cáo", "bar-chart",
                L("Reports", "Báo cáo mua sắm", "bar-chart", "Reports"),
                L("ReportBuilder", "Báo cáo động", "sliders", "ReportBuilder")) : null,

            G("cat", "Danh mục", "book",
                L("OrgUnits", "Cơ cấu tổ chức", "network", "OrgUnits"),
                L("Items", "Hàng hóa & nhóm hàng", "package", "Items"),
                purchasing ? L("Catalogs", "Danh mục dùng chung", "book", "Catalogs") : null),

            admin ? G("sys", "Hệ thống", "settings",
                L("Users", "Người dùng & phân quyền", "users", "Users"),
                L("ApprovalSettings", "Hạn mức & ủy quyền", "shield-check", "ApprovalSettings"),
                L("Appearance", "Giao diện", "palette", "Appearance"),
                L("Integrations", "Tích hợp ERP (API)", "network", "Integrations"),
                L("SampleData", "Dữ liệu mẫu", "database", "SampleData")) : null,
        };
        var result = menu.Where(m => m != null && (!m.IsGroup || m.Children.Count > 0)).Select(m => m!).ToList();
        foreach (var link in result.SelectMany(m => m.Links)) link.Active = link.Key == currentKey;
        return result;
    }
}
