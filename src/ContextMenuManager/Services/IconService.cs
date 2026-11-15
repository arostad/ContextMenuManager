using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ContextMenuManager.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace ContextMenuManager.Services;

/// <summary>
/// Reads entry icons off the UI thread (SHDefExtractIcon / package logos) and hands back a SoftwareBitmapSource.
/// Cache key is path + index + physical pixel size + theme.
/// </summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static double DipSize(double fontSize) => Math.Max(16, fontSize * 20.0 / 14.0);

    public static int PhysicalPixels(double dip, double rasterizationScale) =>
        Math.Max(16, (int)Math.Ceiling(dip * Math.Max(rasterizationScale, 1)));

    public static byte[]? LoadPixels(IconSpec spec, int physicalPx, bool dark, int scalePercent)
    {
        if (spec.IsEmpty) return null;
        if (spec.Origin == IconOrigin.PackageLogo)
        {
            var logo = IconProtocol.PickPackageLogo(spec.Path, spec.PackageLogoBase ?? "", scalePercent, dark, physicalPx);
            if (logo is null) return null;
            return LoadFromCache("logo|" + logo + "|" + physicalPx, () => LoadImageFile(logo, physicalPx));
        }
        return LoadFromCache($"{spec.Path}|{spec.Index}|{physicalPx}", () => ExtractIcon(spec.Path, spec.Index, physicalPx));
    }

    public static byte[]? LoadPixels(MenuEntry entry, int physicalPx, bool dark, int scalePercent) =>
        LoadPixels(entry.Icon, physicalPx, dark, scalePercent);

    public static async Task<SoftwareBitmapSource?> ToImageSourceAsync(byte[] bgra, int physicalPx)
    {
        var software = new SoftwareBitmap(BitmapPixelFormat.Bgra8, physicalPx, physicalPx, BitmapAlphaMode.Premultiplied);
        software.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(bgra));
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(software);
        return source;
    }

    private static byte[]? LoadFromCache(string key, Func<byte[]?> load) =>
        Cache.GetOrAdd(key, _ => { try { return load(); } catch (Exception ex) { Log.Write("icon: " + ex.Message); return null; } });

    private static byte[]? LoadImageFile(string path, int physicalPx)
    {
        if (!File.Exists(path)) return null;
        var ext = Path.GetExtension(path);
        if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cpl", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractIcon(path, 0, physicalPx);
        }

        // Decode with WIC so we can scale to the physical size without tying this to the UI thread.
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 8) return null;
        return DecodeImageToBgra(bytes, physicalPx);
    }

    private static byte[]? ExtractIcon(string path, int index, int physicalPx)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) && !LooksLikeSystemFile(path))
            return null;

        var size = (uint)((physicalPx & 0xFFFF) | (physicalPx << 16));
        var hr = SHDefExtractIconW(path, index, 0, out var large, IntPtr.Zero, size);
        if (hr < 0 || large == IntPtr.Zero)
        {
            var icons = new IntPtr[1];
            var ids = new uint[1];
            var got = PrivateExtractIconsW(path, index, physicalPx, physicalPx, icons, ids, 1, 0);
            if (got == 0 || icons[0] == IntPtr.Zero) return null;
            large = icons[0];
        }
        try { return HiconToBgra(large, physicalPx); }
        finally { if (large != IntPtr.Zero) DestroyIcon(large); }
    }

    private static bool LooksLikeSystemFile(string path) =>
        !Path.IsPathRooted(path) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static byte[]? HiconToBgra(IntPtr hicon, int physicalPx)
    {
        if (!GetIconInfo(hicon, out var info)) return null;
        try
        {
            if (info.hbmColor == IntPtr.Zero) return null;
            GetObject(info.hbmColor, Marshal.SizeOf<BITMAP>(), out BITMAP bm);
            var width = bm.bmWidth;
            var height = Math.Abs(bm.bmHeight);
            if (width <= 0 || height <= 0) return null;
            var hdr = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };
            var pixels = new byte[width * height * 4];
            var hdc = CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(hdc, info.hbmColor, 0, (uint)height, pixels, ref hdr, 0) == 0)
                    return null;
                if (!HasAlpha(pixels) && info.hbmMask != IntPtr.Zero)
                    ApplyMask(hdc, info.hbmMask, pixels, width, height);
                else if (!HasAlpha(pixels))
                    for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            }
            finally { DeleteDC(hdc); }

            if (width == physicalPx && height == physicalPx) return pixels;
            return ResizeBgra(pixels, width, height, physicalPx, physicalPx);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
        }
    }

    private static bool HasAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) return true;
        return false;
    }

    private static void ApplyMask(IntPtr hdc, IntPtr mask, byte[] pixels, int width, int height)
    {
        var hdr = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        var bits = new byte[width * height * 4];
        if (GetDIBits(hdc, mask, 0, (uint)height, bits, ref hdr, 0) == 0) return;
        // Color icons often store the mask as a double-height XOR/AND bitmap; use the first height rows.
        for (var i = 0; i < width * height; i++)
        {
            var maskOn = bits[i * 4] != 0 || bits[i * 4 + 1] != 0 || bits[i * 4 + 2] != 0;
            pixels[i * 4 + 3] = maskOn ? (byte)0 : (byte)255;
        }
    }

    private static byte[] ResizeBgra(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dest = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var sy = Math.Min(sh - 1, y * sh / dh);
            for (var x = 0; x < dw; x++)
            {
                var sx = Math.Min(sw - 1, x * sw / dw);
                var di = (y * dw + x) * 4;
                var si = (sy * sw + sx) * 4;
                dest[di] = src[si];
                dest[di + 1] = src[si + 1];
                dest[di + 2] = src[si + 2];
                dest[di + 3] = src[si + 3];
            }
        }
        return dest;
    }

    private static byte[]? DecodeImageToBgra(byte[] fileBytes, int physicalPx) => DecodeWithSoftwareBitmap(fileBytes, physicalPx);

    private static byte[]? DecodeWithSoftwareBitmap(byte[] fileBytes, int physicalPx)
    {
        try
        {
            using var mem = new InMemoryRandomAccessStream();
            var writer = new DataWriter(mem);
            writer.WriteBytes(fileBytes);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
            writer.Dispose();
            mem.Seek(0);
            var decoder = BitmapDecoder.CreateAsync(mem).AsTask().GetAwaiter().GetResult();
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)physicalPx,
                ScaledHeight = (uint)physicalPx,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var pixelData = decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();
            var pixels = pixelData.DetachPixelData();
            if (pixels.Length == physicalPx * physicalPx * 4) return pixels;
            // Decoder may have kept aspect; letterbox into a square.
            var w = (int)(transform.ScaledWidth == 0 ? decoder.PixelWidth : transform.ScaledWidth);
            var h = (int)(transform.ScaledHeight == 0 ? decoder.PixelHeight : transform.ScaledHeight);
            if (pixels.Length != w * h * 4) return pixels.Length >= 4 ? pixels : null;
            return ResizeBgra(pixels, w, h, physicalPx, physicalPx);
        }
        catch (Exception ex)
        {
            Log.Write("icon decode: " + ex.Message);
            return null;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHDefExtractIconW(string pszIconFile, int iIndex, uint uFlags, out IntPtr phiconLarge, IntPtr phiconSmall, uint nIconSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PrivateExtractIconsW(string szFileName, int nIconIndex, int cxIcon, int cyIcon, [Out] IntPtr[] phicon, [Out] uint[] piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr hObject, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
}
