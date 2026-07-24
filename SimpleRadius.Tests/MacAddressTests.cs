using SimpleRadius.Radius;

namespace SimpleRadius.Tests;

public class MacAddressTests
{
    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa-bb-cc-dd-ee-ff")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("aabb.ccdd.eeff")]
    [InlineData(" AA:bb:CC:dd:EE:ff ")]
    public void EveryMacSpellingNormalisesToTheSameIdentity(string input)
    {
        Assert.Equal("aa:bb:cc:dd:ee:ff", MacAddress.Normalize(input));
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("host.example.com")]
    [InlineData("aa:bb:cc:dd:ee")]
    [InlineData("aa:bb:cc:dd:ee:ff:00")]
    [InlineData("zz:bb:cc:dd:ee:ff")]
    public void NonMacIdentitiesArePreserved(string input)
    {
        Assert.False(MacAddress.TryNormalize(input, out _));
        Assert.Equal(input.Trim(), MacAddress.Normalize(input));
    }
}
