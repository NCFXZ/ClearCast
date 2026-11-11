using System.Runtime.InteropServices;
using Windows.Graphics;

namespace ClearCast;

// A per-pixel-alpha child of the app root blends WinUI-rasterized information
// over its sibling video host, including text and transparent rounded corners.
internal sealed class NativeMediaInfoOverlay(nint parent) : IDisposable
{
    private const uint LayeredTransparentNoActivate = 0x00080000 | 0x00000020 | 0x08000000;
    private const uint ChildStyle = 0x40000000;
    private const uint NoActivate = 0x0010;
    private const uint KeepPlacement = 0x0001 | 0x0002 | 0x0004;
    private const uint ShowWindowFlag = 0x0040;
    private const uint HideWindowFlag = 0x0080;
    private const int MaximumSide = 4096;
    private const int MaximumPixels = 4 * 1024 * 1024;

    private nint _memoryDc;
    private nint _bitmap;
    private nint _originalBitmap;
    private nint _bits;
    private int _width, _height;
    private bool _hasFrame, _visible, _disposed;
    private (int X, int Y, int Width, int Height)? _lastPosition;

    internal nint Handle { get; private set; }
    internal int LastError { get; private set; }

    internal bool Update(byte[] pixels, int width, int height)
    {
        if (_disposed || pixels is null || width is <= 0 or > MaximumSide || height is <= 0 or > MaximumSide ||
            (long)width * height > MaximumPixels || pixels.Length != (long)width * height * 4)
        {
            return Fail(_disposed ? 6 : 87); // ERROR_INVALID_HANDLE / ERROR_INVALID_PARAMETER
        }
        if (!EnsureSurface(width, height)) return Fail(LastError);
        _hasFrame = false;
        Marshal.Copy(pixels, 0, _bits, pixels.Length);
        var size = new SizeInt32(width, height);
        var source = new PointInt32(0, 0);
        var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER / AC_SRC_ALPHA
        // Leave the current destination in place. Child positioning is always
        // done in parent coordinates by Position, independently of the upload.
        _hasFrame = UpdateLayeredWindow(Handle, 0, 0, ref size, _memoryDc, ref source, 0, ref blend, 2);
        if (!_hasFrame) return Fail(Marshal.GetLastWin32Error());
        LastError = 0;
        return true;
    }

    internal void Position(RectInt32 bounds, bool visible)
    {
        if (_disposed || !visible || !_hasFrame || bounds.Width <= 0 || bounds.Height <= 0)
        {
            Hide();
            return;
        }
        // The raster is already at physical-pixel scale. Keep its exact size;
        // SetWindowPos also restores top sibling order after a video resize.
        var position = (bounds.X, bounds.Y, _width, _height);
        if (_visible && _lastPosition == position && GetWindow(Handle, 3) == 0) return; // GW_HWNDPREV
        if (SetWindowPos(Handle, 0, bounds.X, bounds.Y, _width, _height, NoActivate | ShowWindowFlag))
        {
            _visible = true;
            _lastPosition = position;
        }
        else Hide();
    }

    private bool Fail(int error)
    {
        LastError = error != 0 ? error : 31; // ERROR_GEN_FAILURE when GDI supplies no error code.
        _hasFrame = false;
        Hide();
        return false;
    }

    internal void Hide()
    {
        if (Handle != 0 && _visible)
            SetWindowPos(Handle, 0, 0, 0, 0, 0, KeepPlacement | NoActivate | HideWindowFlag);
        _visible = false;
    }

    private bool EnsureSurface(int width, int height)
    {
        if (Handle == 0)
        {
            Handle = CreateWindowExW(LayeredTransparentNoActivate, "STATIC", "ClearCastMediaInfoOverlay", ChildStyle,
                0, 0, 1, 1, parent, 0, 0, 0);
            if (Handle == 0) { LastError = Marshal.GetLastWin32Error(); return false; }
        }
        if (_memoryDc == 0)
        {
            _memoryDc = CreateCompatibleDC(0);
            if (_memoryDc == 0) { LastError = Marshal.GetLastWin32Error(); return false; }
        }
        if (_bitmap != 0 && _width == width && _height == height) return true;
        var info = new BitmapInfo
        {
            HeaderSize = 40, Width = width, Height = -height, Planes = 1, BitCount = 32,
            ImageSize = (uint)(width * height * 4)
        };
        nint bitmap = CreateDIBSection(_memoryDc, ref info, 0, out nint bits, 0, 0);
        if (bitmap == 0 || bits == 0)
        {
            LastError = Marshal.GetLastWin32Error();
            if (bitmap != 0) DeleteObject(bitmap);
            return false;
        }
        nint previous = SelectObject(_memoryDc, bitmap);
        if (previous == 0 || previous == -1)
        {
            LastError = Marshal.GetLastWin32Error();
            DeleteObject(bitmap);
            return false;
        }
        if (_bitmap == 0) _originalBitmap = previous;
        else DeleteObject(_bitmap);
        _bitmap = bitmap; _bits = bits; _width = width; _height = height;
        _hasFrame = false;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        if (Handle != 0) { DestroyWindow(Handle); Handle = 0; }
        if (_bitmap != 0)
        {
            SelectObject(_memoryDc, _originalBitmap);
            DeleteObject(_bitmap);
            _bitmap = _bits = _originalBitmap = 0;
        }
        if (_memoryDc != 0) { DeleteDC(_memoryDc); _memoryDc = 0; }
        _hasFrame = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlendFunction
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant, Color;
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(nint window, nint destinationDc, nint destination,
        ref SizeInt32 size, nint sourceDc, ref PointInt32 source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfo bitmapInfo, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(nint dc);
}
