using System.IO.Compression;
using Silk.NET.OpenGL;

namespace Mine.Rendering;

/// <summary>
/// Debug aid (MINE_SHOT=file.png, MINE_SHOT_AT=seconds, default 15): after that long, saves the
/// finished frame as a PNG and closes the game. Reads the game's own framebuffer, so it works even
/// where the screen cannot be captured (locked screen, no screen-recording permission).
/// </summary>
public static unsafe class Screenshot
{
    public static readonly string? Path = Environment.GetEnvironmentVariable("MINE_SHOT");
    public static readonly double At = double.TryParse(Environment.GetEnvironmentVariable("MINE_SHOT_AT"),
        System.Globalization.CultureInfo.InvariantCulture, out double at) ? at : 15;

    /// <summary>Saves the default framebuffer (width × height) as an RGB PNG.</summary>
    public static void Save(GL gl, int width, int height, string path)
    {
        var pixels = new byte[width * height * 4];
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
        fixed (byte* p = pixels)
            gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // PNG rows: a filter byte, then RGB, top row first (GL reads bottom up).
        var raw = new byte[height * (1 + width * 3)];
        for (int y = 0; y < height; y++)
        {
            int row = y * (1 + width * 3), source = (height - 1 - y) * width * 4;
            for (int x = 0; x < width; x++)
            {
                raw[row + 1 + x * 3] = pixels[source + x * 4];
                raw[row + 2 + x * 3] = pixels[source + x * 4 + 1];
                raw[row + 3 + x * 3] = pixels[source + x * 4 + 2];
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);

        using var file = File.Create(path);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        stream.Write(typed);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, Crc(typed));
        stream.Write(crc);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return ~crc;
    }
}
