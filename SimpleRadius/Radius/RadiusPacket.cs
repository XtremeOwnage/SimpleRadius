using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace SimpleRadius.Radius;

/// <summary>Result of checking a packet's Message-Authenticator attribute.</summary>
public enum MessageAuthenticatorState
{
    /// <summary>The packet carried no Message-Authenticator attribute.</summary>
    Absent,
    Valid,
    Invalid
}

/// <summary>
/// A parsed RADIUS packet plus the helpers needed to authenticate it and to build a signed reply.
/// </summary>
public sealed class RadiusPacket
{
    /// <summary>Fixed header size: code, identifier, length and the 16 byte authenticator.</summary>
    public const int HeaderLength = 20;

    /// <summary>Maximum packet size defined by RFC 2865.</summary>
    public const int MaxLength = 4096;

    private static readonly byte[] ZeroAuthenticator = new byte[16];

    private RadiusPacket(
        RadiusCode code,
        byte identifier,
        byte[] authenticator,
        IReadOnlyList<RadiusAttribute> attributes,
        byte[] raw,
        int messageAuthenticatorOffset)
    {
        Code = code;
        Identifier = identifier;
        Authenticator = authenticator;
        Attributes = attributes;
        Raw = raw;
        MessageAuthenticatorOffset = messageAuthenticatorOffset;
    }

    public RadiusCode Code { get; }

    public byte Identifier { get; }

    /// <summary>The 16 byte request authenticator, needed to sign the reply.</summary>
    public byte[] Authenticator { get; }

    public IReadOnlyList<RadiusAttribute> Attributes { get; }

    /// <summary>The exact bytes the packet was parsed from, trimmed to the declared length.</summary>
    public byte[] Raw { get; }

    private int MessageAuthenticatorOffset { get; }

    public bool TryGetAttribute(byte type, [NotNullWhen(true)] out RadiusAttribute? attribute)
    {
        foreach (var candidate in Attributes)
        {
            if (candidate.Type == type)
            {
                attribute = candidate;
                return true;
            }
        }

        attribute = null;
        return false;
    }

    public string? GetString(byte type) =>
        TryGetAttribute(type, out var attribute) ? attribute.AsString() : null;

    public uint? GetUInt32(byte type) =>
        TryGetAttribute(type, out var attribute) ? attribute.AsUInt32() : null;

    public static bool TryParse(ReadOnlySpan<byte> datagram, [NotNullWhen(true)] out RadiusPacket? packet, out string? error)
    {
        packet = null;
        error = null;

        if (datagram.Length < HeaderLength)
        {
            error = $"packet is {datagram.Length} bytes, smaller than the {HeaderLength} byte RADIUS header";
            return false;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..4]);
        if (declaredLength < HeaderLength || declaredLength > MaxLength)
        {
            error = $"declared length {declaredLength} is outside the valid range";
            return false;
        }

        if (declaredLength > datagram.Length)
        {
            error = $"declared length {declaredLength} exceeds the {datagram.Length} bytes received";
            return false;
        }

        // RFC 2865: octets beyond the declared length are padding and must be ignored.
        var raw = datagram[..declaredLength].ToArray();

        var attributes = new List<RadiusAttribute>();
        var messageAuthenticatorOffset = -1;
        var offset = HeaderLength;
        while (offset < raw.Length)
        {
            if (offset + 2 > raw.Length)
            {
                error = "truncated attribute header";
                return false;
            }

            var type = raw[offset];
            var length = raw[offset + 1];
            if (length < 2 || offset + length > raw.Length)
            {
                error = $"attribute {type} declares an invalid length of {length}";
                return false;
            }

            var value = raw[(offset + 2)..(offset + length)];
            if (type == RadiusAttributeType.MessageAuthenticator && value.Length == 16)
            {
                messageAuthenticatorOffset = offset + 2;
            }

            attributes.Add(new RadiusAttribute(type, value));
            offset += length;
        }

        packet = new RadiusPacket(
            (RadiusCode)raw[0],
            raw[1],
            raw[4..20],
            attributes,
            raw,
            messageAuthenticatorOffset);

        return true;
    }

    /// <summary>
    /// Verifies the request authenticator of a request whose authenticator is a keyed digest rather
    /// than a nonce — Accounting-Request in this server. Access-Request authenticators are random
    /// values and cannot be verified this way.
    /// </summary>
    public bool HasValidRequestAuthenticator(string sharedSecret)
    {
        var buffer = (byte[])Raw.Clone();
        ZeroAuthenticator.CopyTo(buffer, 4);

        var expected = ComputeMd5(buffer, sharedSecret);
        return CryptographicOperations.FixedTimeEquals(expected, Authenticator);
    }

    /// <summary>
    /// Verifies the Message-Authenticator attribute (RFC 3579) when the client supplied one. UniFi and
    /// most modern NAS firmware include it on Access-Request, which is how the shared secret gets checked.
    /// </summary>
    public MessageAuthenticatorState VerifyMessageAuthenticator(string sharedSecret)
    {
        if (MessageAuthenticatorOffset < 0)
        {
            return MessageAuthenticatorState.Absent;
        }

        var supplied = Raw[MessageAuthenticatorOffset..(MessageAuthenticatorOffset + 16)];

        var buffer = (byte[])Raw.Clone();
        Array.Clear(buffer, MessageAuthenticatorOffset, 16);

        using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(sharedSecret));
        var expected = hmac.ComputeHash(buffer);

        return CryptographicOperations.FixedTimeEquals(expected, supplied)
            ? MessageAuthenticatorState.Valid
            : MessageAuthenticatorState.Invalid;
    }

    /// <summary>
    /// Serialises a reply to this packet and signs it: the Message-Authenticator is filled in first
    /// (RFC 3579) and the Response Authenticator is then computed over the finished packet (RFC 2865).
    /// </summary>
    public byte[] BuildResponse(
        RadiusCode code,
        IEnumerable<RadiusAttribute> attributes,
        string sharedSecret,
        bool includeMessageAuthenticator = true)
    {
        var payload = new List<RadiusAttribute>();
        if (includeMessageAuthenticator)
        {
            payload.Add(new RadiusAttribute(RadiusAttributeType.MessageAuthenticator, new byte[16]));
        }

        payload.AddRange(attributes);

        var length = HeaderLength + payload.Sum(a => a.EncodedLength);
        if (length > MaxLength)
        {
            throw new InvalidOperationException($"Response of {length} bytes exceeds the {MaxLength} byte RADIUS limit.");
        }

        var response = new byte[length];
        response[0] = (byte)code;
        response[1] = Identifier;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), (ushort)length);

        // Both digests below are computed with the request authenticator in place.
        Authenticator.CopyTo(response, 4);

        var offset = HeaderLength;
        var messageAuthenticatorOffset = -1;
        foreach (var attribute in payload)
        {
            response[offset] = attribute.Type;
            response[offset + 1] = (byte)attribute.EncodedLength;
            attribute.Value.CopyTo(response, offset + 2);

            if (attribute.Type == RadiusAttributeType.MessageAuthenticator && messageAuthenticatorOffset < 0)
            {
                messageAuthenticatorOffset = offset + 2;
            }

            offset += attribute.EncodedLength;
        }

        if (messageAuthenticatorOffset >= 0)
        {
            using var hmac = new HMACMD5(Encoding.UTF8.GetBytes(sharedSecret));
            hmac.ComputeHash(response).CopyTo(response, messageAuthenticatorOffset);
        }

        ComputeMd5(response, sharedSecret).CopyTo(response, 4);
        return response;
    }

    private static byte[] ComputeMd5(byte[] packet, string sharedSecret)
    {
        var secret = Encoding.UTF8.GetBytes(sharedSecret);
        var material = new byte[packet.Length + secret.Length];
        packet.CopyTo(material, 0);
        secret.CopyTo(material, packet.Length);
        return MD5.HashData(material);
    }
}
