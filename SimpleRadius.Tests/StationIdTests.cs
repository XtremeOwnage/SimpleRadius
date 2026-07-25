using SimpleRadius.Radius;

namespace SimpleRadius.Tests;

public class StationIdTests
{
    [Theory]
    [InlineData("24-A4-3C-00-11-22:IoT", "IoT")]           // RFC 3580 hyphen MAC + SSID
    [InlineData("AA-BB-CC-DD-EE-FF:Guest WiFi", "Guest WiFi")]
    [InlineData("aabbccddeeff:Home", "Home")]              // MAC with no separators
    [InlineData("24-A4-3C-00-11-22:Cameras:2", "Cameras:2")] // SSID containing a colon
    public void SsidIsTheTextAfterTheFirstColon(string called, string expected)
    {
        Assert.Equal(expected, StationId.ExtractSsid(called));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("AA-BB-CC-DD-EE-FF")]                        // wired / bare hyphen MAC
    [InlineData("aa:bb:cc:dd:ee:ff")]                        // colon-separated MAC, not an SSID
    [InlineData("24-A4-3C-00-11-22:")]                       // trailing colon, empty SSID
    public void NoSsidWhenThereIsNone(string? called)
    {
        Assert.Null(StationId.ExtractSsid(called));
    }

    [Theory]
    [InlineData(19u, "Wireless 802.11")]
    [InlineData(15u, "Ethernet")]
    [InlineData(5u, "Virtual")]
    [InlineData(99u, "Type 99")]
    public void NasPortTypeIsNamed(uint value, string expected)
    {
        Assert.Equal(expected, StationId.DescribeNasPortType(value));
    }

    [Fact]
    public void NasPortTypeIsNullWhenAbsent()
    {
        Assert.Null(StationId.DescribeNasPortType(null));
    }
}
