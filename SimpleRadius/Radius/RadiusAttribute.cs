using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace SimpleRadius.Radius;

/// <summary>A single type-length-value attribute carried by a RADIUS packet.</summary>
public sealed class RadiusAttribute
{
    /// <summary>Attribute values are length-prefixed by a single byte covering type + length + value.</summary>
    public const int MaxValueLength = 253;

    public RadiusAttribute(byte type, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaxValueLength)
        {
            throw new ArgumentException($"RADIUS attribute values cannot exceed {MaxValueLength} bytes.", nameof(value));
        }

        Type = type;
        Value = value;
    }

    public byte Type { get; }

    public byte[] Value { get; }

    /// <summary>Total wire size of the attribute, including the type and length bytes.</summary>
    public int EncodedLength => Value.Length + 2;

    public string AsString() => Encoding.UTF8.GetString(Value);

    public uint? AsUInt32() =>
        Value.Length == 4 ? BinaryPrimitives.ReadUInt32BigEndian(Value) : null;

    public IPAddress? AsIpAddress() =>
        Value.Length == 4 ? new IPAddress(Value) : null;

    public static RadiusAttribute FromString(byte type, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaxValueLength)
        {
            bytes = bytes[..MaxValueLength];
        }

        return new RadiusAttribute(type, bytes);
    }

    public static RadiusAttribute FromUInt32(byte type, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new RadiusAttribute(type, bytes);
    }

    /// <summary>
    /// Builds a tagged integer attribute (RFC 2868): a one byte tag followed by a 24 bit value.
    /// Used for Tunnel-Type and Tunnel-Medium-Type so they bind to the tagged Tunnel-Private-Group-Id.
    /// </summary>
    public static RadiusAttribute TaggedUInt32(byte type, byte tag, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value & 0x00FFFFFF);
        bytes[0] = tag;
        return new RadiusAttribute(type, bytes);
    }

    /// <summary>Builds a tagged string attribute (RFC 2868), such as Tunnel-Private-Group-Id.</summary>
    public static RadiusAttribute TaggedString(byte type, byte tag, string value)
    {
        var text = Encoding.UTF8.GetBytes(value);
        if (text.Length > MaxValueLength - 1)
        {
            text = text[..(MaxValueLength - 1)];
        }

        var bytes = new byte[text.Length + 1];
        bytes[0] = tag;
        text.CopyTo(bytes, 1);
        return new RadiusAttribute(type, bytes);
    }
}
