using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClonaDock.Core;

namespace ClonaDock.Native;

/// <summary>Large, alpha-correct icons for files, folders, apps and shell locations.</summary>
public static class IconLoader
{
    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [Flags]
    enum SIIGBF { ResizeToFit = 0, BiggerSizeOk = 1, MemoryOnly = 2, IconOnly = 4, ThumbnailOnly = 8, InCacheOnly = 0x10 }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindCtx,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public BITMAP dsBm; public BITMAPINFOHEADER dsBmih;
        public uint f0, f1, f2; public IntPtr dshSection; public uint dsOffset;
    }

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);

    static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    /// <param name="cache">Keep it for later; off for one-off lists (launcher, folder stacks) so they free their icons.</param>
    public static ImageSource? Get(string path, int size = 128, bool iconOnly = true, bool cache = true)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = $"{path}|{size}|{iconOnly}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var img = Load(path, size, iconOnly);
        if (cache) Cache[key] = img;
        return img;
    }

    /// <summary>Smallest icon size that stays sharp at the Dock's largest (magnified) size.</summary>
    public static int DockSize(double iconSize, double magnification, double dpiScale) =>
        iconSize * Math.Max(1, magnification) * dpiScale > 128 ? 256 : 128;

    public static void Forget(string path)
    {
        foreach (var k in Cache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            Cache.Remove(k);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    /// <summary>
    /// An exe's own icon at <paramref name="size"/>. The shell draws exes that lack a large icon as a tiny icon
    /// inside a white frame (e.g. ExitLag ships only 75 px); reading the resource directly avoids the frame.
    /// </summary>
    static ImageSource? ExeIcon(string path, int size)
    {
        var icons = new IntPtr[1];
        var ids = new int[1];
        if (PrivateExtractIcons(path, 0, size, size, icons, ids, 1, 0) == 0 || icons[0] == IntPtr.Zero) return null;
        try
        {
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icons[0], System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    static ImageSource? Load(string path, int size, bool iconOnly)
    {
        if (iconOnly && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(path))
        {
            try
            {
                if (ExeIcon(path, size) is ImageSource exe) return exe;
            }
            catch (Exception ex)
            {
                Log.Write($"exe icon not read for {path}: {ex.Message}");
            }
        }

        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            var flags = SIIGBF.BiggerSizeOk | (iconOnly ? SIIGBF.IconOnly : SIIGBF.ResizeToFit);
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbm);
            Marshal.ReleaseComObject(factory);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            return FromHBitmap(hbm);
        }
        catch (Exception ex)
        {
            Log.Write($"icon not read for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) Win32.DeleteObject(hbm);
        }
    }

    /// <summary>Copies a 32-bit DIB section keeping its alpha, which Imaging.CreateBitmapSourceFromHBitmap drops.</summary>
    static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero,
                System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        var raw = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, raw, 0, raw.Length);

        var pixels = new byte[stride * h];
        bool bottomUp = ds.dsBmih.biHeight > 0;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(raw, (bottomUp ? h - 1 - y : y) * stride, pixels, y * stride, stride);

        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha)
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        (pixels, w, h, stride) = TrimPadding(pixels, w, h, stride);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Apps without a large icon come back as a small icon in the corner of a big transparent canvas,
    /// which left them small and off-centre in the Dock. Crop such icons to their content, centred.
    /// Normal icons (content filling most of the canvas) are left alone.
    /// </summary>
    static (byte[] Pixels, int W, int H, int Stride) TrimPadding(byte[] px, int w, int h, int stride)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] > 8)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return (px, w, h, stride);
        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        if (cw >= w * 0.75 || ch >= h * 0.75) return (px, w, h, stride);

        // Square canvas with a little breathing room, like the padding real icons have.
        int side = (int)Math.Ceiling(Math.Max(cw, ch) * 1.12);
        int outStride = side * 4;
        var output = new byte[outStride * side];
        int ox = (side - cw) / 2, oy = (side - ch) / 2;
        for (int y = 0; y < ch; y++)
            Buffer.BlockCopy(px, (minY + y) * stride + minX * 4, output, (oy + y) * outStride + ox * 4, cw * 4);
        return (output, side, side, outStride);
    }
}
