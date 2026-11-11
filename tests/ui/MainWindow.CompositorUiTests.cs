#if CLEARCAST_UI_TESTS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.Imaging;
using Windows.Storage;
using WinRT.Interop;

namespace ClearCast;

public sealed partial class MainWindow
{
    private sealed record UiCompositorFrame(int Width, int Height, byte[] Pixels);

    private async Task UiTestOwnedCompositorCaptureAsync(string output, string prefix, FrameworkElement? staticContent = null)
    {
        const string method = "PrintWindow(PW_RENDERFULLCONTENT=2) of this test process's own HWND; exact DWM visible-frame pixels, excluding invisible resize gutters; no desktop capture.";
        const string limitation = "PrintWindow may omit or freeze WinUI hand-in Composition visuals. Unsupported capture is not a playback or application failure; XAML, palette, and animation lifecycle assertions remain separate.";
        if (!MusicBackground.IsAnimating)
        {
            _uiTestLayouts.Add(new { phase = "owned window compositor capture", prefix, status = "unsupported", method, limitation,
                reason = "Backdrop animation is inactive under the current window, motion, or accessibility policy." });
            return;
        }
        nint window = WindowNative.GetWindowHandle(this);
        UiCompositorGetWindowThreadProcessId(window, out uint owner);
        UiCheck(owner == (uint)Environment.ProcessId, "compositor capture is restricted to this test process's owned HWND");
        UiCheck(MusicBackground.HasArtworkFlow && MusicBackground.TextureFrameCount == 32,
            "flowing background uses a bounded sequence of warped and blurred artwork textures");
        UiCheck(MusicBackground.AnimationChannelCount == 36,
            "artwork textures share one phase clock, opacity expressions and background-only transforms");
        FrameworkElement pageContent = staticContent ?? MusicContent;
        var textBlocks = UiDescendants<TextBlock>(pageContent).ToArray();
        string[] labels = textBlocks.Select(text => text.Text).ToArray();
        string caption = Title;
        string framePrefix = prefix + (staticContent is null ? "-music-window" : "-window");
        try
        {
            UiNativeRect content = UiCompositorContentBounds(window, pageContent);
            await Task.Delay(250); // Let determinate progress/theme layout settle before comparing frames.
            var first = await Task.Run(() => UiCaptureOwnedWindow(window));
            await Task.Delay(2000);
            var second = await Task.Run(() => UiCaptureOwnedWindow(window));
            bool labelsUnchanged = labels.SequenceEqual(textBlocks.Select(text => text.Text));
            bool captionUnchanged = caption == Title;
            UiCheck(labelsUnchanged && captionUnchanged, "compositor comparison keeps page text and window caption static " + prefix);
            bool firstVisible = UiCompositorHasContent(first.Pixels), secondVisible = UiCompositorHasContent(second.Pixels);
            if (firstVisible)
                await UiSaveCompositorFrameAsync(output, framePrefix + "-1", first);
            if (secondVisible)
                await UiSaveCompositorFrameAsync(output, framePrefix + "-2", second);
            if (first.Width != second.Width || first.Height != second.Height)
            {
                _uiTestLayouts.Add(new { phase = "owned window compositor capture", prefix, status = "unsupported", method, limitation,
                    reason = "Window dimensions changed between the two owned-window frames.", first.Width, first.Height,
                    secondWidth = second.Width, secondHeight = second.Height });
                return;
            }
            int changed = 0, backgroundChanged = 0, backgroundStrongChanged = 0;
            int maximumChannelChange = 0, backgroundMaximumChannelChange = 0;
            long totalDifference = 0, backgroundPixels = 0, backgroundDifference = 0;
            for (int index = 0; index < first.Pixels.Length; index += 4)
            {
                int blueChange = Math.Abs(first.Pixels[index] - second.Pixels[index]);
                int greenChange = Math.Abs(first.Pixels[index + 1] - second.Pixels[index + 1]);
                int redChange = Math.Abs(first.Pixels[index + 2] - second.Pixels[index + 2]);
                int difference = blueChange + greenChange + redChange;
                int maximum = Math.Max(blueChange, Math.Max(greenChange, redChange));
                maximumChannelChange = Math.Max(maximumChannelChange, maximum);
                totalDifference += difference;
                if (difference >= 3)
                    changed++;
                int pixel = index / 4, x = pixel % first.Width, y = pixel / first.Width;
                if (x < content.Left || x >= content.Right || y < content.Top || y >= content.Bottom)
                {
                    backgroundPixels++;
                    backgroundDifference += difference;
                    backgroundMaximumChannelChange = Math.Max(backgroundMaximumChannelChange, maximum);
                    if (difference >= 3) backgroundChanged++;
                    if (difference >= 15) backgroundStrongChanged++;
                }
            }
            long pixels = (long)first.Width * first.Height;
            bool motionCaptured = firstVisible && secondVisible && MusicBackground.IsAnimating && changed >= Math.Max(1000, pixels / 1000);
            _uiTestLayouts.Add(new
            {
                phase = "owned window compositor capture", prefix, status = motionCaptured ? "captured" : "unsupported", method, limitation,
                first.Width, first.Height, intervalMilliseconds = 2000, firstVisible, secondVisible,
                changedRgbPixels = changed, changedFraction = (double)changed / pixels, labelsUnchanged, captionUnchanged,
                staticContent = pageContent.Name,
                meanAbsoluteChannelChange = totalDifference / (3.0 * pixels), maximumChannelChange,
                backgroundChangedRgbPixels = backgroundChanged,
                backgroundChangedFraction = backgroundPixels > 0 ? (double)backgroundChanged / backgroundPixels : 0,
                backgroundStrongChangedFraction = backgroundPixels > 0 ? (double)backgroundStrongChanged / backgroundPixels : 0,
                backgroundMeanAbsoluteChannelChange = backgroundPixels > 0 ? backgroundDifference / (3.0 * backgroundPixels) : 0,
                backgroundMaximumChannelChange,
                textureFrames = MusicBackground.TextureFrameCount,
                animationChannels = MusicBackground.AnimationChannelCount,
                rateControllers = MusicBackground.RateControlledChannelCount,
                motionRate = MusicBackground.MotionRate,
                animationStarts = MusicBackground.AnimationStartCount,
                animationStillRunning = MusicBackground.IsAnimating,
                reason = motionCaptured ? "Owned-window frames contain broad color changes while page text and caption stay static."
                    : "PrintWindow returned blank/frozen/insufficiently changing content, or animation stopped under current policy."
            });
        }
        catch (Exception error) when (error is Win32Exception or COMException or InvalidOperationException or IOException or ArgumentException)
        {
            _uiTestLayouts.Add(new { phase = "owned window compositor capture", prefix, status = "unsupported", method, limitation, reason = error.Message });
        }
    }

    private UiNativeRect UiCompositorContentBounds(nint window, FrameworkElement staticContent, double padding = 16)
    {
        var visible = UiCompositorVisibleBounds(window, out _);
        var client = new UiCompositorPoint();
        if (!UiCompositorClientToScreen(window, ref client))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var content = UiMeasure(staticContent);
        double scale = WindowLayout.XamlRoot.RasterizationScale;
        return new UiNativeRect
        {
            Left = (int)Math.Floor((content.X - padding) * scale) + client.X - visible.Left,
            Top = (int)Math.Floor((content.Y - padding) * scale) + client.Y - visible.Top,
            Right = (int)Math.Ceiling((content.X + content.Width + padding) * scale) + client.X - visible.Left,
            Bottom = (int)Math.Ceiling((content.Y + content.Height + padding) * scale) + client.Y - visible.Top
        };
    }

    private static UiNativeRect UiCompositorVisibleBounds(nint window, out UiNativeRect outer)
    {
        if (!UiGetWindowRect(window, out outer))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the owned window bounds.");
        int result = UiCompositorDwmGetWindowAttribute(window, 9, out var visible, Marshal.SizeOf<UiNativeRect>());
        if (result != 0 || visible.Width <= 0 || visible.Height <= 0 || visible.Left < outer.Left ||
            visible.Top < outer.Top || visible.Right > outer.Right || visible.Bottom > outer.Bottom)
            throw new InvalidOperationException("DWM did not supply bounded visible window coordinates.");
        return visible;
    }

    private static UiCompositorFrame UiCaptureOwnedWindow(nint window)
    {
        UiCompositorGetWindowThreadProcessId(window, out uint owner);
        if (owner != (uint)Environment.ProcessId)
            throw new InvalidOperationException("Refusing to capture a window not owned by this test process.");
        var visible = UiCompositorVisibleBounds(window, out var bounds);
        int width = bounds.Width, height = bounds.Height;
        if (width is <= 0 or > 8192 || height is <= 0 or > 8192 || (long)width * height > 64 * 1024 * 1024)
            throw new InvalidOperationException("Owned window dimensions exceed the bounded capture size.");
        var pixels = new byte[checked(width * height * 4)];
        nint memory = UiCompositorCreateCompatibleDC(0), bitmap = 0, previous = 0;
        if (memory == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned-window capture DC.");
        try
        {
            var info = new UiCompositorBitmapInfo
            {
                Header = new UiCompositorBitmapHeader
                {
                    Size = (uint)Marshal.SizeOf<UiCompositorBitmapHeader>(), Width = width, Height = -height,
                    Planes = 1, BitCount = 32
                }
            };
            bitmap = UiCompositorCreateDIBSection(memory, in info, 0, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the top-down capture DIB.");
            previous = UiCompositorSelectObject(memory, bitmap);
            if (previous == 0 || previous == -1)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not select the capture DIB.");
            Marshal.Copy(pixels, 0, bits, pixels.Length); // Never save uninitialized bitmap memory.
            if (!UiCompositorPrintWindow(window, memory, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow did not capture the owned window.");
            UiCompositorGdiFlush();
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            if (visible.Left == bounds.Left && visible.Top == bounds.Top && visible.Width == width && visible.Height == height)
                return new(width, height, pixels);
            // PrintWindow includes the invisible resize frame. Keep the exact
            // visible pixels reported by DWM; do not rescale or paint over them.
            byte[] frame = new byte[checked(visible.Width * visible.Height * 4)];
            int left = visible.Left - bounds.Left, top = visible.Top - bounds.Top;
            for (int row = 0; row < visible.Height; row++)
                Buffer.BlockCopy(pixels, ((top + row) * width + left) * 4,
                    frame, row * visible.Width * 4, visible.Width * 4);
            return new(visible.Width, visible.Height, frame);
        }
        finally
        {
            if (previous != 0 && previous != -1) UiCompositorSelectObject(memory, previous);
            if (bitmap != 0) UiDeleteObject(bitmap);
            UiCompositorDeleteDC(memory);
        }
    }

    private static bool UiCompositorHasContent(byte[] pixels)
    {
        int nonblack = 0, colors = 0;
        Span<bool> buckets = stackalloc bool[4096];
        buckets.Clear();
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int blue = pixels[index], green = pixels[index + 1], red = pixels[index + 2];
            if (red + green + blue > 12) nonblack++;
            int bucket = (red >> 4) * 256 + (green >> 4) * 16 + (blue >> 4);
            if (!buckets[bucket]) { buckets[bucket] = true; colors++; }
        }
        return nonblack > pixels.Length / 32 && colors >= 8;
    }

    private async Task UiSaveCompositorFrameAsync(string output, string name, UiCompositorFrame frame)
    {
        var folder = await StorageFolder.GetFolderFromPathAsync(output);
        var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync();
        _uiTestScreenshots.Add(file.Path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UiCompositorPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UiCompositorBitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UiCompositorBitmapInfo
    {
        public UiCompositorBitmapHeader Header;
        public uint Color;
    }

    [DllImport("user32.dll", EntryPoint = "PrintWindow", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiCompositorPrintWindow(nint window, nint memory, uint flags);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute", ExactSpelling = true)]
    private static extern int UiCompositorDwmGetWindowAttribute(nint window, uint attribute, out UiNativeRect value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", ExactSpelling = true)]
    private static extern uint UiCompositorGetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "ClientToScreen", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiCompositorClientToScreen(nint window, ref UiCompositorPoint point);

    [DllImport("gdi32.dll", EntryPoint = "CreateCompatibleDC", ExactSpelling = true, SetLastError = true)]
    private static extern nint UiCompositorCreateCompatibleDC(nint compatible);

    [DllImport("gdi32.dll", EntryPoint = "CreateDIBSection", ExactSpelling = true, SetLastError = true)]
    private static extern nint UiCompositorCreateDIBSection(nint memory, in UiCompositorBitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll", EntryPoint = "SelectObject", ExactSpelling = true, SetLastError = true)]
    private static extern nint UiCompositorSelectObject(nint memory, nint bitmap);

    [DllImport("gdi32.dll", EntryPoint = "DeleteDC", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiCompositorDeleteDC(nint memory);

    [DllImport("gdi32.dll", EntryPoint = "GdiFlush", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UiCompositorGdiFlush();
}
#endif
