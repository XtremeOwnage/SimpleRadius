using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using SimpleRadius.Radius;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class RadiusPacketTests
{
    private const string Secret = "secret123";

    [Fact]
    public void AccessRequestRoundTripsThroughTheParser()
    {
        var datagram = RadiusRequestBuilder.BuildAccessRequest(
            identifier: 42,
            Secret,
            [
                RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff"),
                RadiusAttribute.FromUInt32(RadiusAttributeType.NasPort, 7)
            ]);

        Assert.True(RadiusPacket.TryParse(datagram, out var packet, out var error));
        Assert.Null(error);
        Assert.Equal(RadiusCode.AccessRequest, packet!.Code);
        Assert.Equal(42, packet.Identifier);
        Assert.Equal(16, packet.Authenticator.Length);
        Assert.Equal("aa:bb:cc:dd:ee:ff", packet.GetString(RadiusAttributeType.UserName));
        Assert.Equal(7u, packet.GetUInt32(RadiusAttributeType.NasPort));
    }

    [Fact]
    public void DeclaredLengthWinsOverTrailingPadding()
    {
        var datagram = RadiusRequestBuilder.BuildAccessRequest(1, Secret,
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "alice")]);
        var padded = datagram.Concat(new byte[8]).ToArray();

        Assert.True(RadiusPacket.TryParse(padded, out var packet, out _));
        Assert.Equal(datagram.Length, packet!.Raw.Length);
        Assert.Equal("alice", packet.GetString(RadiusAttributeType.UserName));
    }

    [Fact]
    public void PacketShorterThanTheHeaderIsRejected()
    {
        Assert.False(RadiusPacket.TryParse(new byte[19], out _, out var error));
        Assert.Contains("smaller", error);
    }

    [Fact]
    public void DeclaredLengthLongerThanTheDatagramIsRejected()
    {
        var datagram = new byte[20];
        datagram[0] = (byte)RadiusCode.AccessRequest;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2, 2), 200);

        Assert.False(RadiusPacket.TryParse(datagram, out _, out var error));
        Assert.Contains("exceeds", error);
    }

    [Fact]
    public void AttributeRunningPastTheEndOfThePacketIsRejected()
    {
        var datagram = new byte[24];
        datagram[0] = (byte)RadiusCode.AccessRequest;
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(2, 2), 24);
        datagram[20] = RadiusAttributeType.UserName;
        datagram[21] = 40; // claims 40 bytes but only 4 remain

        Assert.False(RadiusPacket.TryParse(datagram, out _, out var error));
        Assert.Contains("invalid length", error);
    }

    [Fact]
    public void MessageAuthenticatorValidatesWithTheMatchingSecret()
    {
        var datagram = RadiusRequestBuilder.BuildAccessRequest(3, Secret,
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "alice")]);

        Assert.True(RadiusPacket.TryParse(datagram, out var packet, out _));
        Assert.Equal(MessageAuthenticatorState.Valid, packet!.VerifyMessageAuthenticator(Secret));
        Assert.Equal(MessageAuthenticatorState.Invalid, packet.VerifyMessageAuthenticator("wrong-secret"));
    }

    [Fact]
    public void MessageAuthenticatorIsReportedAbsentWhenTheNasOmitsIt()
    {
        var datagram = RadiusRequestBuilder.BuildAccessRequest(3, Secret,
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "alice")],
            includeMessageAuthenticator: false);

        Assert.True(RadiusPacket.TryParse(datagram, out var packet, out _));
        Assert.Equal(MessageAuthenticatorState.Absent, packet!.VerifyMessageAuthenticator(Secret));
    }

    [Fact]
    public void AccountingRequestAuthenticatorDetectsAWrongSecret()
    {
        var datagram = RadiusRequestBuilder.BuildAccountingRequest(9, Secret,
        [
            RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start),
            RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, "session-1")
        ]);

        Assert.True(RadiusPacket.TryParse(datagram, out var packet, out _));
        Assert.True(packet!.HasValidRequestAuthenticator(Secret));
        Assert.False(packet.HasValidRequestAuthenticator("wrong-secret"));
    }

    [Fact]
    public void ResponseAuthenticatorMatchesTheDigestDefinedByRfc2865()
    {
        var datagram = RadiusRequestBuilder.BuildAccessRequest(17, Secret,
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "alice")]);
        Assert.True(RadiusPacket.TryParse(datagram, out var request, out _));

        var response = request!.BuildResponse(
            RadiusCode.AccessAccept,
            AccessAcceptAttributes.Build(20, new ServerSettings()),
            Secret);

        Assert.Equal((byte)RadiusCode.AccessAccept, response[0]);
        Assert.Equal(request.Identifier, response[1]);
        Assert.Equal(response.Length, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2)));

        // Recompute the way a NAS would: MD5(code + id + length + request authenticator + attributes + secret).
        var verification = (byte[])response.Clone();
        request.Authenticator.CopyTo(verification, 4);
        var expected = MD5.HashData(verification.Concat(Encoding.UTF8.GetBytes(Secret)).ToArray());

        Assert.Equal(expected, response[4..20]);
    }

    [Fact]
    public void ReplyMessageAuthenticatorIsSignedOverTheFinishedPacket()
    {
        var datagram = RadiusRequestBuilder.BuildAccessRequest(18, Secret,
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "alice")]);
        Assert.True(RadiusPacket.TryParse(datagram, out var request, out _));

        var response = request!.BuildResponse(RadiusCode.AccessAccept, [], Secret);

        Assert.True(RadiusPacket.TryParse(response, out var parsed, out _));
        Assert.True(parsed!.TryGetAttribute(RadiusAttributeType.MessageAuthenticator, out _));

        // The digest covers the request authenticator, so restore it before checking, as a NAS does.
        var verification = (byte[])response.Clone();
        request.Authenticator.CopyTo(verification, 4);
        Assert.True(RadiusPacket.TryParse(verification, out var forVerification, out _));
        Assert.Equal(MessageAuthenticatorState.Valid, forVerification!.VerifyMessageAuthenticator(Secret));
    }

    [Fact]
    public void VlanAttributesUseTheTaggedRfc2868Encoding()
    {
        var attributes = AccessAcceptAttributes.Build(1234, new ServerSettings());

        var tunnelType = Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelType);
        Assert.Equal(new byte[] { TunnelValues.VlanTag, 0, 0, (byte)TunnelValues.TypeVlan }, tunnelType.Value);

        var medium = Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelMediumType);
        Assert.Equal(new byte[] { TunnelValues.VlanTag, 0, 0, (byte)TunnelValues.MediumIeee802 }, medium.Value);

        // The VLAN itself travels as ASCII digits, not as a binary integer.
        var groupId = Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelPrivateGroupId);
        Assert.Equal(TunnelValues.VlanTag, groupId.Value[0]);
        Assert.Equal("1234", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public void OverlongAttributeValuesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new RadiusAttribute(RadiusAttributeType.ReplyMessage, new byte[254]));
    }
}
