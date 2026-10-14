using System.IO;
using System.Runtime.InteropServices;
using Overlay.Core.Beatmap;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Drawing = System.Drawing;

namespace Overlay.App;

/// <summary>
/// Hit object images from an osu! skin folder, decoded once and turned into (combo-tinted) Direct2D bitmaps on demand.
/// Lookups follow osu!: "name@2x.png" is preferred over "name.png" (and drawn at half size), file names ignore case.
/// </summary>
public sealed class SkinImages : IDisposable
{
    /// <summary>Decoded image: premultiplied BGRA pixels, and how many @1x skin pixels one image pixel covers.</summary>
    private sealed record Image(int Width, int Height, byte[] Pixels, double PxScale)
    {
        public double Width1x => Width * PxScale;
        public double Height1x => Height * PxScale;
    }

    private readonly Dictionary<string, string> files; // lower-case relative path → full path
    private readonly Dictionary<string, Image?> decoded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, uint), ID2D1Bitmap1> bitmaps = new();
    private ID2D1DeviceContext? owner;

    public string Folder { get; }
    public SkinIni Ini { get; }

    private SkinImages(string folder)
    {
        Folder = folder;
        Ini = SkinIni.Load(folder);
        files = Directory.EnumerateFiles(folder, "*.png", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetRelativePath(folder, f).Replace('\\', '/').ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>Loads a skin folder, or returns null if it doesn't exist.</summary>
    public static SkinImages? Load(string folder) => Directory.Exists(folder) ? new SkinImages(folder) : null;

    /// <summary>Whether the skin has the image at all (a 1×1 placeholder counts: skins use those to hide elements).</summary>
    public bool Has(string name) => Decode(name) != null;

    /// <summary>Size in @1x skin pixels, or null if the skin doesn't have the image.</summary>
    public (double Width, double Height)? Size(string name) => Decode(name) is { } i ? (i.Width1x, i.Height1x) : null;

    /// <summary>
    /// Draws the image centred on (<paramref name="x"/>, <paramref name="y"/>), <paramref name="scale"/> DIPs per @1x pixel,
    /// tinted by multiplying with <paramref name="tint"/> (as osu! tints hit circles with the combo colour).
    /// </summary>
    public bool Draw(ID2D1DeviceContext dc, string name, float x, float y, double scale, float opacity, Rgb? tint = null)
    {
        var bitmap = Bitmap(dc, name, tint);
        if (bitmap == null || Decode(name) is not { } img) return false;
        float w = (float)(img.Width1x * scale), h = (float)(img.Height1x * scale);
        dc.DrawBitmap(bitmap, new Rect(x - w / 2, y - h / 2, w, h), opacity, BitmapInterpolationMode.Linear,
            new Rect(0, 0, img.Width, img.Height));
        return true;
    }

    private ID2D1Bitmap1? Bitmap(ID2D1DeviceContext dc, string name, Rgb? tint)
    {
        if (!ReferenceEquals(owner, dc)) { DisposeBitmaps(); owner = dc; }
        uint tintKey = tint is { } t ? 0x1000000u | (uint)(t.R << 16 | t.G << 8 | t.B) : 0;
        if (bitmaps.TryGetValue((name, tintKey), out var cached)) return cached;
        if (Decode(name) is not { } img) return null;

        var pixels = img.Pixels;
        if (tint is { } c)
        {
            pixels = (byte[])pixels.Clone();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)(pixels[i] * c.B / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * c.G / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * c.R / 255);
            }
        }
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var bitmap = dc.CreateBitmap(new SizeI(img.Width, img.Height), handle.AddrOfPinnedObject(), (uint)(img.Width * 4),
                new BitmapProperties1(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
            bitmaps[(name, tintKey)] = bitmap;
            return bitmap;
        }
        finally { handle.Free(); }
    }

    private readonly object cacheLock = new(); // decoding happens on the render thread, status queries on the UI thread

    private Image? Decode(string name)
    {
        lock (cacheLock) return DecodeLocked(name);
    }

    private Image? DecodeLocked(string name)
    {
        if (decoded.TryGetValue(name, out var cached)) return cached;
        Image? img = null;
        string key = name.Replace('\\', '/').ToLowerInvariant();
        if (files.TryGetValue(key + "@2x.png", out var hd)) img = Read(hd, 0.5);
        else if (files.TryGetValue(key + ".png", out var sd)) img = Read(sd, 1);
        decoded[name] = img;
        return img;
    }

    private static Image? Read(string path, double pxScale)
    {
        try
        {
            using var src = new Drawing.Bitmap(path);
            var rect = new Drawing.Rectangle(0, 0, src.Width, src.Height);
            // PArgb gives premultiplied BGRA in memory, which is what Direct2D wants.
            var data = src.LockBits(rect, Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var pixels = new byte[src.Width * src.Height * 4];
                for (int y = 0; y < src.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * src.Width * 4, src.Width * 4);
                return new Image(src.Width, src.Height, pixels, pxScale);
            }
            finally { src.UnlockBits(data); }
        }
        catch (Exception e) when (e is ArgumentException or IOException or OutOfMemoryException)
        {
            return null; // unreadable image: treat as missing
        }
    }

    private void DisposeBitmaps()
    {
        foreach (var b in bitmaps.Values) b.Dispose();
        bitmaps.Clear();
    }

    public void Dispose() => DisposeBitmaps();
}
