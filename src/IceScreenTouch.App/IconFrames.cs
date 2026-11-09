// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;

namespace IceScreenTouch.App;

/// <summary>Reads the frames of an .ico file (tools/build_icon.ps1 writes them as PNG).</summary>
internal static class IconFrames
{
    /// <summary>
    /// The bytes of the largest frame of <paramref name="ico"/> (a width byte of 0 means 256), or null when the file
    /// is not an icon or its largest frame is not a PNG.
    /// </summary>
    public static byte[]? LargestPng(ReadOnlySpan<byte> ico)
    {
        // ICONDIR: reserved 0, type 1 (icon), count; then one 16-byte ICONDIRENTRY per frame.
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        var best = -1;
        var bestWidth = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (16 * i);
            if (entry + 16 > ico.Length)
            {
                return null;
            }

            var width = ico[entry] == 0 ? 256 : ico[entry];
            if (width > bestWidth)
            {
                bestWidth = width;
                best = entry;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(ico[(best + 8)..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(ico[(best + 12)..]);
        if (offset + (ulong)size > (ulong)ico.Length || size < 8)
        {
            return null;
        }

        var frame = ico.Slice((int)offset, (int)size);
        ReadOnlySpan<byte> pngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
        return frame.StartsWith(pngSignature) ? frame.ToArray() : null;
    }
}
