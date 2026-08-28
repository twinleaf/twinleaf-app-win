// SPDX-License-Identifier: Apache-2.0

using System;
using System.Buffers.Binary;
using System.Text;

namespace Twinleaf.Interop;

/// <summary>
/// Thrown when an event payload does not match the expected wire layout. This
/// means the C# decoder and the Rust <c>TypedEventWriter</c> have drifted apart,
/// so it is a bug rather than a runtime condition to recover from — callers log
/// it and drop the event.
/// </summary>
public sealed class EventDecodeException(string message) : Exception(message);

/// <summary>
/// Cursor over one binary event payload, mirroring the Rust
/// <c>TypedEventWriter</c> in <c>rust/tio-bridge/src/main.rs</c>. Every integer
/// is little-endian; strings are a <c>u32</c> byte count followed by UTF-8;
/// optionals are a <c>bool</c> flag followed by the value when present.
///
/// A ref struct over the callback's buffer: decoding a plot frame at streaming
/// rates must not copy the payload before parsing it.
/// </summary>
public ref struct BinaryEventReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _offset = 0;

    public readonly int Remaining => _data.Length - _offset;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Remaining < count)
        {
            throw new EventDecodeException(
                $"event payload truncated: wanted {count} bytes at offset {_offset}, {Remaining} remain");
        }

        var slice = _data.Slice(_offset, count);
        _offset += count;
        return slice;
    }

    public byte ReadU8() => Take(1)[0];

    public bool ReadBool() => ReadU8() != 0;

    public ushort ReadU16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

    public uint ReadU32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public ulong ReadU64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    public long ReadI64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    public double ReadF64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>Element/byte counts are written as <c>u32</c> by the Rust writer.</summary>
    public int ReadLength()
    {
        var length = ReadU32();
        if (length > int.MaxValue)
        {
            throw new EventDecodeException($"length {length} exceeds addressable range");
        }

        return (int)length;
    }

    public string ReadString()
    {
        var length = ReadLength();
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(Take(length));
    }

    public string? ReadOptionalString() => ReadBool() ? ReadString() : null;

    public ulong? ReadOptionalU64() => ReadBool() ? ReadU64() : null;

    /// <summary>
    /// The Rust side writes <c>false</c> for a non-finite value, so a present
    /// flag always implies a usable number.
    /// </summary>
    public double? ReadOptionalF64() => ReadBool() ? ReadF64() : null;
}
