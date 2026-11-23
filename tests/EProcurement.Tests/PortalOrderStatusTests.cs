using EProcurement.Web.Controllers;
using EProcurement.Web.Models.Entities;

namespace EProcurement.Tests;

/// <summary>Trạng thái đơn hàng theo góc nhìn nhà cung cấp trên cổng NCC</summary>
public class PortalOrderStatusTests
{
    private static string Key(PoStatus s, bool confirmed = false, bool rejected = false, bool shipped = false) =>
        PortalOrdersController.VendorStatus(new PurchaseOrder
        {
            Status = s, VendorConfirmedAt = confirmed ? DateTime.Now : null, VendorRejectedAt = rejected ? DateTime.Now : null
        }, shipped).Key;

    [Fact] public void Issued_NotAnswered_IsWaiting() => Assert.Equal("waiting", Key(PoStatus.Issued));
    [Fact] public void Issued_Confirmed_IsConfirmed() => Assert.Equal("confirmed", Key(PoStatus.Issued, confirmed: true));
    [Fact] public void Issued_ConfirmedAndShipped_IsShipping() => Assert.Equal("shipping", Key(PoStatus.Issued, confirmed: true, shipped: true));
    [Fact] public void Issued_Rejected_IsRejected() => Assert.Equal("rejected", Key(PoStatus.Issued, rejected: true));
    [Fact] public void Partial_And_Received_And_Cancelled()
    {
        Assert.Equal("partial", Key(PoStatus.PartiallyReceived, confirmed: true, shipped: true));
        Assert.Equal("done", Key(PoStatus.Received, confirmed: true));
        Assert.Equal("cancelled", Key(PoStatus.Cancelled, confirmed: true));
    }
    [Fact] public void EveryStatusKey_HasFilterOption() =>
        Assert.All(new[] { "waiting", "confirmed", "shipping", "partial", "done", "rejected", "cancelled" },
            k => Assert.Contains(PortalOrdersController.StatusOptions, o => o.Key == k));
}
