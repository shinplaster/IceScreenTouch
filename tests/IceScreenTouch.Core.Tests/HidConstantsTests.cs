using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class HidConstantsTests
{
    [Fact]
    public void AxisRangeMatchesThePoc()
    {
        // Must match AXIS_MAX in poc/poc2-ble-hid/ble_hid_poc.py (verified 1:1 on a real iPhone).
        Assert.Equal(10000, HidConstants.AxisMax);
        Assert.Equal(2, HidConstants.MaxContacts);
    }
}
