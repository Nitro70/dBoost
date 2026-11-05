using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DBoost.App.Services;

/// <summary>
/// Asks Windows for the same thumbnail Explorer shows: a frame for videos, album art for music,
/// or the file type's icon. Runs on one background STA thread because the shell wants COM.
/// </summary>
public static class ShellThumbnails
{
    private static readonly BlockingCollection<(string Path, int Size, TaskCompletionSource<ImageSource?> Done)> Queue = new();
    private static readonly Lazy<Thread> Worker = new(() =>
    {
        var t = new Thread(Work) { IsBackground = true, Name = "dBoost thumbnails" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return t;
    });

    public static Task<ImageSource?> GetAsync(string path, int pixelSize)
    {
        _ = Worker.Value;
        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Add((path, pixelSize, tcs));
        return tcs.Task;
    }

    private static void Work()
    {
        foreach (var (path, size, done) in Queue.GetConsumingEnumerable())
        {
            try { done.TrySetResult(Load(path, size)); }
            catch (Exception) { done.TrySetResult(null); }
        }
    }

    private static ImageSource? Load(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null) return null;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0 /* SIIGBF_RESIZETOFIT */, out IntPtr hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            try { return FromHBitmap(hbmp); }
            finally { DeleteObject(hbmp); }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32-bit DIB section keeping its alpha (CreateBitmapSourceFromHBitmap would drop it).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbmp)
    {
        var ds = new DIBSECTION();
        if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
        {
            var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            fallback.Freeze();
            return fallback;
        }

        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        var pixels = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, pixels, 0, pixels.Length);

        if (ds.dsBmih.biHeight > 0) // bottom-up rows: flip
        {
            var row = new byte[stride];
            for (int y = 0; y < h / 2; y++)
            {
                Buffer.BlockCopy(pixels, y * stride, row, 0, stride);
                Buffer.BlockCopy(pixels, (h - 1 - y) * stride, pixels, y * stride, stride);
                Buffer.BlockCopy(row, 0, pixels, (h - 1 - y) * stride, stride);
            }
        }

        // Some thumbnails come back with alpha all zero; treat those as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int size, ref DIBSECTION ds);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);
}
