using System.Buffers.Binary;

namespace Scry;

/// <summary>
/// Where one piece of disclosed content is kept: thirty-two bytes of SHA-256 over the content's kind
/// and its canonical bytes — of HMAC-SHA-256 where <see cref="ScryDisclosureOptions.AddressKey"/> is
/// set. The same content has the same address, which is what stores it once however often it is sent.
/// </summary>
/// <remarks>
/// A value rather than a string or an array: one is made for every row a response carries, and it is
/// compared and hashed far more often than it is read.
/// </remarks>
public readonly struct ScryDisclosureAddress :
    IEquatable<ScryDisclosureAddress>
{
    /// <summary>The length of an address in bytes.</summary>
    public const int Size = 32;

    // What Size bytes come to as base64url with no padding.
    const int TextLength = 43;

    readonly ulong first;
    readonly ulong second;
    readonly ulong third;
    readonly ulong fourth;

    ScryDisclosureAddress(ulong first, ulong second, ulong third, ulong fourth)
    {
        this.first = first;
        this.second = second;
        this.third = third;
        this.fourth = fourth;
    }

    /// <summary>Reads an address from the <see cref="Size"/> bytes a store kept it as.</summary>
    public static ScryDisclosureAddress From(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"A disclosure address is {Size} bytes; {bytes.Length} were given.", nameof(bytes));
        }

        return new(
            BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[24..]));
    }

    /// <summary>Writes the address as the <see cref="Size"/> bytes a store keeps.</summary>
    public void CopyTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, first);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], second);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], third);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], fourth);
    }

    /// <summary>The address as an array of its own, for a store's parameter.</summary>
    public byte[] ToArray()
    {
        var bytes = new byte[Size];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Reads what <see cref="ToString"/> wrote, or answers false for anything else: text that is not
    /// base64url, or does not decode to <see cref="Size"/> bytes.
    /// </summary>
    public static bool TryParse(string? text, out ScryDisclosureAddress address)
    {
        address = default;

        // Thirty-two bytes are forty-three characters and nothing else, so anything of another length
        // is turned away before it is decoded. Text reaches this from outside.
        if (text is not {Length: TextLength})
        {
            return false;
        }

        // The decoder that reports rather than throws: what is asked of it here is whether this is
        // an address, and text that is not one is an answer rather than a fault.
        Span<byte> bytes = stackalloc byte[Size];
        var status = Base64Url.DecodeFromChars(text, bytes, out var consumed, out var written);
        if (status != OperationStatus.Done ||
            consumed != TextLength ||
            written != Size)
        {
            return false;
        }

        address = From(bytes);
        return true;
    }

    public bool Equals(ScryDisclosureAddress other) =>
        first == other.first &&
        second == other.second &&
        third == other.third &&
        fourth == other.fourth;

    public override bool Equals(object? obj) =>
        obj is ScryDisclosureAddress other &&
        Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(first, second, third, fourth);

    public static bool operator ==(ScryDisclosureAddress left, ScryDisclosureAddress right) =>
        left.Equals(right);

    public static bool operator !=(ScryDisclosureAddress left, ScryDisclosureAddress right) =>
        !left.Equals(right);

    /// <summary>The address as forty-three characters of base64url: how a log or a screen shows one.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        CopyTo(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
