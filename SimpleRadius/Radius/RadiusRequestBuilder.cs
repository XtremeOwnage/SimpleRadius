using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SimpleRadius.Radius;

/// <summary>
/// Builds signed RADIUS request datagrams. The server itself never sends requests; this exists so the
/// listener can be exercised end to end by tests and by the bundled smoke check.
/// </summary>
public static class RadiusRequestBuilder
{
    /// <summary>
    /// Builds an Access-Request with a random request authenticator and a valid Message-Authenticator,
    /// which is what a modern NAS such as a UniFi gateway sends for MAC authentication.
    /// </summary>
    public static byte[] BuildAccessRequest(
        byte identifier,
        string sharedSecret,
        IEnumerable<RadiusAttribute> attributes,
        bool includeMessageAuthenticator = true,
        byte[]? requestAuthenticator = null)
    {
        var authenticator = requestAuthenticator ?? RandomNumberGenerator.GetBytes(16);
        return Build(RadiusCode.AccessRequest, identifier, authenticator, attributes, sharedSecret, includeMessageAuthenticator, signAuthenticator: false);
    }

    /// <summary>
    /// Builds an Accounting-Request whose request authenticator is the keyed digest defined by RFC 2866,
    /// so the server can verify the shared secret from the packet alone.
    /// </summary>
    public static byte[] BuildAccountingRequest(
        byte identifier,
        string sharedSecret,
        IEnumerable<RadiusAttribute> attributes)
    {
        return Build(RadiusCode.AccountingRequest, identifier, new byte[16], attributes, sharedSecret, includeMessageAuthenticator: false, signAuthenticator: true);
    }

    private static byte[] Build(
        RadiusCode code,
        byte identifier,
        byte[] authenticator,
        IEnumerable<RadiusAttribute> attributes,
        string sharedSecret,
        bool includeMessageAuthenticator,
        bool signAuthenticator)
    {
        var payload = new List<RadiusAttribute>(attributes);
        var messageAuthenticator = includeMessageAuthenticator
            ? new RadiusAttribute(RadiusAttributeType.MessageAuthenticator, new byte[16])
            : null;

        if (messageAuthenticator is not null)
        {
            payload.Add(messageAuthenticator);
        }

        var length = RadiusPacket.HeaderLength + payload.Sum(a => a.EncodedLength);
        var packet = new byte[length];
        packet[0] = (byte)code;
        packet[1] = identifier;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)length);
        authenticator.CopyTo(packet, 4);

        var offset = RadiusPacket.HeaderLength;
        var messageAuthenticatorOffset = -1;
        foreach (var attribute in payload)
        {
            packet[offset] = attribute.Type;
            packet[offset + 1] = (byte)attribute.EncodedLength;
            attribute.Value.CopyTo(packet, offset + 2);

            if (ReferenceEquals(attribute, messageAuthenticator))
            {
                messageAuthenticatorOffset = offset + 2;
            }

            offset += attribute.EncodedLength;
        }

        if (messageAuthenticatorOffset >= 0)
        {
            using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(sharedSecret));
            hmac.ComputeHash(packet).CopyTo(packet, messageAuthenticatorOffset);
        }

        if (signAuthenticator)
        {
            var secret = Encoding.UTF8.GetBytes(sharedSecret);
            var material = new byte[packet.Length + secret.Length];
            packet.CopyTo(material, 0);
            secret.CopyTo(material, packet.Length);
            MD5.HashData(material).CopyTo(packet, 4);
        }

        return packet;
    }
}
