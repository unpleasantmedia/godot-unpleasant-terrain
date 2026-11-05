using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Godot;

namespace UnpleasantMedia.UnpleasantTerrain;

/// <summary>
/// A heightmap read from a file: its values 0 to 1, row by row. Whole-number formats span their full range
/// (black 0, white 1); floating-point ones (EXR, floating-point TIFF) are scaled to their own lowest and highest.
/// Reads PNG (16-bit ones itself, as Godot would cut them to 8), EXR, 16-bit RAW, and strip-based TIFF
/// (uncompressed, LZW or Deflate; 8, 16 or 32-bit). Of several channels, the first.
/// </summary>
public sealed class HeightmapFile
{
    public int Width { get; private init; }

    public int Height { get; private init; }

    public float[] Values { get; private init; } = [];

    /// <summary>Whether its values are a floating-point file's own, not yet scaled (see keepFloats in <see cref="Read"/>).</summary>
    public bool Floating { get; private init; }

    /// <summary>Whether a file of this name is RAW, which needs its size and byte order given.</summary>
    public static bool IsRaw(string path) => path.GetExtension().ToLowerInvariant() is "raw" or "r16";

    /// <summary>
    /// Reads a file. Throws an <see cref="InvalidDataException"/>, with a message to show, if it cannot.
    /// With keepFloats, floating-point values are left as they are, for tiles to share one range.
    /// </summary>
    public static HeightmapFile Read(string path, int rawWidth = 0, int rawHeight = 0, bool rawBigEndian = false, bool keepFloats = false)
    {
        var extension = path.GetExtension().ToLowerInvariant();
        return extension switch
        {
            "png" => ReadPng(path, keepFloats),
            "exr" => ReadWithGodot(path, keepFloats),
            "tif" or "tiff" => ReadTiff(Bytes(path), keepFloats),
            "raw" or "r16" => ReadRaw(Bytes(path), rawWidth, rawHeight, rawBigEndian),
            _ => throw new InvalidDataException($"Unsupported heightmap type: .{extension}"),
        };
    }

    /// <summary>The value at a place, 0 to 1 across each way, blended from the four pixels round it; clamped to its edges.</summary>
    public float At(float u, float v)
    {
        var x = Math.Clamp(u, 0f, 1f) * (Width - 1);
        var y = Math.Clamp(v, 0f, 1f) * (Height - 1);
        var (x0, y0) = ((int)x, (int)y);
        var (x1, y1) = (Math.Min(x0 + 1, Width - 1), Math.Min(y0 + 1, Height - 1));
        var (fx, fy) = (x - x0, y - y0);
        var top = Values[y0 * Width + x0] * (1f - fx) + Values[y0 * Width + x1] * fx;
        var bottom = Values[y1 * Width + x0] * (1f - fx) + Values[y1 * Width + x1] * fx;
        return top * (1f - fy) + bottom * fy;
    }

    private static byte[] Bytes(string path)
    {
        var bytes = Godot.FileAccess.GetFileAsBytes(path);
        return bytes.Length > 0 ? bytes : throw new InvalidDataException($"Could not read {path}");
    }

    private static HeightmapFile ReadWithGodot(string path, bool keepFloats)
    {
        var image = Image.LoadFromFile(path) ?? throw new InvalidDataException($"Could not read {path}");
        if (image.IsEmpty())
            throw new InvalidDataException($"Could not read {path}");
        if (image.IsCompressed())
            image.Decompress();
        var floating = image.GetFormat() is Image.Format.Rf or Image.Format.Rgf or Image.Format.Rgbf or Image.Format.Rgbaf
            or Image.Format.Rh or Image.Format.Rgh or Image.Format.Rgbh or Image.Format.Rgbah;
        image.Convert(Image.Format.Rf);
        var data = image.GetData();
        var values = new float[data.Length / sizeof(float)];
        Buffer.BlockCopy(data, 0, values, 0, data.Length);
        return new HeightmapFile { Width = image.GetWidth(), Height = image.GetHeight(), Values = floating && !keepFloats ? ToOwnRange(values) : values, Floating = floating && keepFloats };
    }

    private static float[] ToOwnRange(float[] values)
    {
        var finite = values.Where(float.IsFinite).ToArray();
        if (finite.Length == 0)
            throw new InvalidDataException("The heightmap has no usable values.");
        var (low, high) = (finite.Min(), finite.Max());
        var range = high > low ? high - low : 1f;
        return [.. values.Select(v => float.IsFinite(v) ? (v - low) / range : 0f)];
    }

    private static HeightmapFile ReadRaw(byte[] bytes, int width, int height, bool bigEndian)
    {
        if (width <= 0 || height <= 0 || bytes.Length != width * height * 2)
            throw new InvalidDataException($"A {bytes.Length} byte RAW file does not match {width} x {height} at 16 bits a pixel.");
        return new HeightmapFile { Width = width, Height = height, Values = Sixteen(bytes, 0, width * height, 2, bigEndian) };
    }

    private static float[] Sixteen(byte[] bytes, int start, int count, int stride, bool bigEndian)
    {
        var values = new float[count];
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(start + i * stride, 2);
            values[i] = (bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span)) / 65535f;
        }

        return values;
    }

    // ---- PNG ----

    private static HeightmapFile ReadPng(string path, bool keepFloats)
    {
        var bytes = Bytes(path);
        if (bytes.Length < 33 || bytes[24] != 16)
            return ReadWithGodot(path, keepFloats);
        var (width, height) = (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20)));
        var channels = bytes[25] switch
        {
            0 => 1,
            2 => 3,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("Unsupported PNG colour type."),
        };
        if (bytes[28] != 0)
            throw new InvalidDataException("Interlaced 16-bit PNGs are not supported.");

        using var compressed = new MemoryStream();
        for (var at = 8; at + 8 <= bytes.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            if (bytes[at + 4] == 'I' && bytes[at + 5] == 'D' && bytes[at + 6] == 'A' && bytes[at + 7] == 'T')
                compressed.Write(bytes, at + 8, length);
            at += 12 + length;
        }

        compressed.Position = 0;
        using var inflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        var raw = output.ToArray();

        var pixel = channels * 2;
        var row = width * pixel;
        if (raw.Length < (row + 1) * height)
            throw new InvalidDataException("The PNG's image data is too short.");
        var image = new byte[row * height];
        for (var y = 0; y < height; y++)
        {
            var from = y * (row + 1);
            var filter = raw[from];
            for (var x = 0; x < row; x++)
            {
                int left = x >= pixel ? image[y * row + x - pixel] : 0;
                int up = y > 0 ? image[(y - 1) * row + x] : 0;
                int corner = x >= pixel && y > 0 ? image[(y - 1) * row + x - pixel] : 0;
                var add = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => throw new InvalidDataException("Unsupported PNG filter."),
                };
                image[y * row + x] = (byte)(raw[from + 1 + x] + add);
            }
        }

        return new HeightmapFile { Width = width, Height = height, Values = Sixteen(image, 0, width * height, pixel, true) };
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    // ---- TIFF ----

    private static HeightmapFile ReadTiff(byte[] b, bool keepFloats)
    {
        if (b.Length < 8 || !((b[0] == 'I' && b[1] == 'I') || (b[0] == 'M' && b[1] == 'M')))
            throw new InvalidDataException("Not a TIFF file.");
        var big = b[0] == 'M';
        int U16(int at) => big ? BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
        int U32(int at) => big ? BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(at)) : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
        if (U16(2) != 42)
            throw new InvalidDataException("Unsupported TIFF (BigTIFF?).");

        var directory = U32(4);
        int[] Tag(int id)
        {
            var count = U16(directory);
            for (var i = 0; i < count; i++)
            {
                var entry = directory + 2 + i * 12;
                if (U16(entry) != id)
                    continue;
                var (type, n) = (U16(entry + 2), U32(entry + 4));
                var size = type == 3 ? 2 : type == 4 ? 4 : 1;
                var at = n * size <= 4 ? entry + 8 : U32(entry + 8);
                return [.. Enumerable.Range(0, n).Select(k => size == 2 ? U16(at + k * 2) : size == 4 ? U32(at + k * 4) : b[at + k])];
            }

            return [];
        }

        int One(int id, int fallback) => Tag(id) is { Length: > 0 } values ? values[0] : fallback;

        var (width, height) = (One(256, 0), One(257, 0));
        var bits = One(258, 1);
        var compression = One(259, 1);
        var samples = One(277, 1);
        var rowsPerStrip = One(278, height);
        var format = One(339, 1);
        var predictor = One(317, 1);
        var (offsets, counts) = (Tag(273), Tag(279));
        if (Tag(324).Length > 0)
            throw new InvalidDataException("Tiled TIFFs are not supported: save it with strips.");
        if (offsets.Length == 0 || width <= 0 || height <= 0 || bits is not (8 or 16 or 32) || One(284, 1) != 1)
            throw new InvalidDataException("Unsupported TIFF layout: use 8, 16 or 32 bits, interleaved, with strips.");
        if (compression is not (1 or 5 or 8 or 32946))
            throw new InvalidDataException($"Unsupported TIFF compression ({compression}): use none, LZW or Deflate.");

        var pixel = samples * bits / 8;
        var row = width * pixel;
        var image = new byte[row * height];
        for (var s = 0; s < offsets.Length; s++)
        {
            var strip = compression switch
            {
                1 => b.AsSpan(offsets[s], counts[s]).ToArray(),
                5 => Lzw(b.AsSpan(offsets[s], counts[s])),
                _ => Inflate(b, offsets[s], counts[s]),
            };
            strip.AsSpan(0, Math.Min(strip.Length, image.Length - s * rowsPerStrip * row)).CopyTo(image.AsSpan(s * rowsPerStrip * row));
        }

        var values = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            var previous = new long[samples];
            for (var x = 0; x < width; x++)
            {
                var at = y * row + x * pixel;
                if (bits == 32 && format == 3)
                {
                    var word = U32Of(image, at, big);
                    values[y * width + x] = BitConverter.Int32BitsToSingle(word);
                    continue;
                }

                // Whole numbers: undoing the predictor (each sample stored as the change from the last) channel by channel.
                for (var c = samples - 1; c >= 0; c--)
                {
                    var from = at + c * bits / 8;
                    long sample = bits switch
                    {
                        8 => image[from],
                        16 => big ? BinaryPrimitives.ReadUInt16BigEndian(image.AsSpan(from)) : BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(from)),
                        _ => (uint)U32Of(image, from, big),
                    };
                    if (predictor == 2)
                        sample = (sample + previous[c]) & ((1L << bits) - 1);
                    previous[c] = sample;
                }

                values[y * width + x] = (float)(previous[0] / (double)((1L << bits) - 1));
            }
        }

        var floating = bits == 32 && format == 3;
        return new HeightmapFile { Width = width, Height = height, Values = floating && !keepFloats ? ToOwnRange(values) : values, Floating = floating && keepFloats };
    }

    private static int U32Of(byte[] bytes, int at, bool big) =>
        big ? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at)) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));

    private static byte[] Inflate(byte[] bytes, int at, int count)
    {
        using var input = new MemoryStream(bytes, at, count);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Lzw(ReadOnlySpan<byte> data)
    {
        const int Clear = 256, End = 257;
        var table = new byte[4096][];
        for (var i = 0; i < 256; i++)
            table[i] = [(byte)i];
        using var output = new MemoryStream();
        var (next, width, bit) = (258, 9, 0);
        byte[]? previous = null;
        while (bit + width <= data.Length * 8)
        {
            var code = 0;
            for (var k = 0; k < width; k++, bit++)
                code = code << 1 | (data[bit / 8] >> (7 - bit % 8) & 1);
            if (code == End)
                break;
            if (code == Clear)
            {
                (next, width, previous) = (258, 9, null);
                continue;
            }

            byte[] entry;
            if (previous is null)
                entry = table[code];
            else if (code < next)
                entry = table[code];
            else
                entry = [.. previous, previous[0]];
            output.Write(entry);
            if (previous is not null && next < 4096)
                table[next++] = [.. previous, entry[0]];
            previous = entry;
            // TIFF's LZW widens its codes one early.
            width = next >= 2047 ? 12 : next >= 1023 ? 11 : next >= 511 ? 10 : 9;
        }

        return output.ToArray();
    }
}
