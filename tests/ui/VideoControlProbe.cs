// .NET Framework UI Automation probe, restricted to a task-owned process.
// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;

public static class VideoControlProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretBounds;
    }
    private delegate bool EnumChild(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out Rect rect, int size);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumChild callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);

    private static IntPtr NamedChild(IntPtr parent, string name)
    {
        IntPtr found = IntPtr.Zero;
        if (parent == IntPtr.Zero) return found;
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr data)
        {
            var title = new System.Text.StringBuilder(128);
            GetWindowText(child, title, title.Capacity);
            if (title.ToString() == name) found = child;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr VideoHost(IntPtr parent) { return NamedChild(parent, "ClearCastVideoHost"); }

    private static IntPtr WaitNativeDiagnostics(Process target, bool visible)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < (visible ? 5000 : 1500))
        {
            IntPtr host = VideoHost(target.MainWindowHandle);
            IntPtr overlay = NamedChild(target.MainWindowHandle, "ClearCastMediaInfoOverlay");
            if (!visible && (overlay == IntPtr.Zero || !IsWindowVisible(overlay))) return overlay;
            if (visible && overlay != IntPtr.Zero && IsWindowVisible(overlay))
            {
                uint owner;
                const long styles = 0x00080000 | 0x00000020 | 0x08000000;
                if (GetWindowThreadProcessId(overlay, out owner) == 0 || owner != target.Id || GetParent(overlay) != target.MainWindowHandle ||
                    (GetWindowLongPtr(overlay, -20).ToInt64() & styles) != styles)
                    throw new InvalidOperationException("Diagnostics are not an owned layered, click-through, non-activating root child.");
                return overlay;
            }
            Thread.Sleep(40);
        }
        throw new InvalidOperationException("Native alpha diagnostics did not become " + (visible ? "visible." : "hidden."));
    }

    private static AutomationElement VisibleDiagnostics(AutomationElement root)
    {
        var element = root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "VideoDiagnosticsText"));
        if (element == null || element.Current.IsOffscreen || element.Current.BoundingRectangle.IsEmpty ||
            element.Current.BoundingRectangle.Width <= 0 || element.Current.BoundingRectangle.Height <= 0) return null;
        return element;
    }

    private static void VideoF12(Process target)
    {
        IntPtr host = VideoHost(target.MainWindowHandle);
        uint owner;
        if (host == IntPtr.Zero || GetWindowThreadProcessId(host, out owner) == 0 || owner != target.Id ||
            !PostMessage(host, 0x0100, new IntPtr(0x7B), new IntPtr(1)) ||
            !PostMessage(host, 0x0101, new IntPtr(0x7B), new IntPtr(unchecked((int)0xC0000001))))
            throw new InvalidOperationException("Cannot route F12 through the owned native video host.");
    }

    private static string WaitDiagnostics(AutomationElement root, bool visible, bool exactTestRate)
    {
        var elapsed = Stopwatch.StartNew();
        string lastText = "";
        while (elapsed.ElapsedMilliseconds < (visible ? 5000 : 1500))
        {
            var element = VisibleDiagnostics(root);
            if (!visible && element == null) return "";
            if (visible && element != null)
            {
                lastText = element.Current.Name;
                bool sourceRate = exactTestRate
                    ? lastText.Contains("Source frame rate: 15 fps") || lastText.Contains("\u6E90\u5E27\u7387: 15 fps")
                    : lastText.Contains("Source frame rate:") || lastText.Contains("\u6E90\u5E27\u7387:");
                bool renderedRate = lastText.Contains("Rendered frame rate:") || lastText.Contains("\u6E32\u67D3\u5E27\u7387:");
                if (lastText.Contains("1280 \u00D7 720") && lastText.Contains("H.264") && sourceRate && renderedRate)
                    return lastText;
            }
            Thread.Sleep(40);
        }
        throw new InvalidOperationException(visible ? "Expected decoded video diagnostics not visible: " + lastText :
            "F12 did not hide the video diagnostics.");
    }

    private static void Diagnostics(Process target, bool exactTestRate, string screenshot, bool fullScreen)
    {
        var root = AutomationElement.FromHandle(target.MainWindowHandle);
        if (VisibleDiagnostics(root) != null) throw new InvalidOperationException("Video diagnostics should initially be hidden.");
        string information = "";
        for (int cycle = 0; cycle < 2; ++cycle)
        {
            VideoF12(target);
            information = WaitDiagnostics(root, true, exactTestRate);
            IntPtr overlay = WaitNativeDiagnostics(target, true);
            var text = VisibleDiagnostics(root);
            Rect host;
            if (text == null || !GetWindowRect(VideoHost(target.MainWindowHandle), out host))
                throw new InvalidOperationException("Cannot measure the owned diagnostic video.");
            double fit = Math.Min((host.Right - host.Left) / 1280.0, (host.Bottom - host.Top) / 720.0);
            double width = 1280 * fit, height = 720 * fit;
            double left = host.Left + (host.Right - host.Left - width) / 2;
            double top = host.Top + (host.Bottom - host.Top - height) / 2;
            if (fullScreen)
            {
                Rect screen;
                if (!GetWindowRect(target.MainWindowHandle, out screen))
                    throw new InvalidOperationException("Cannot measure the owned full-screen window.");
                left = screen.Left; top = screen.Top;
                width = screen.Right - screen.Left; height = screen.Bottom - screen.Top;
            }
            var bounds = text.Current.BoundingRectangle;
            double scale = GetDpiForWindow(target.MainWindowHandle) / 96.0;
            double inset = fullScreen ? 16 : 0;
            if (bounds.Left < left - 1 || bounds.Top < top - 1 || bounds.Right > left + width + 1 || bounds.Bottom > top + height + 1 ||
                Math.Abs(bounds.Left - left - (inset + 8) * scale) > 2 || Math.Abs(bounds.Top - top - (inset + 6) * scale) > 2)
                throw new InvalidOperationException(fullScreen ? "Video diagnostics are not at the screen upper-left inset." :
                    "Video diagnostics are not flush with the actual video upper-left corner.");
            Rect overlayBounds;
            if (!GetWindowRect(overlay, out overlayBounds) ||
                Math.Abs(overlayBounds.Left - left - inset * scale) > 2 || Math.Abs(overlayBounds.Top - top - inset * scale) > 2 ||
                Math.Abs(overlayBounds.Right - overlayBounds.Left - bounds.Width - 16 * scale) > 2 ||
                Math.Abs(overlayBounds.Bottom - overlayBounds.Top - bounds.Height - 12 * scale) > 2)
                throw new InvalidOperationException("Native alpha diagnostics do not track their XAML panel bounds.");
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                int x = (overlayBounds.Left + overlayBounds.Right) / 2 - host.Left;
                int y = (overlayBounds.Top + overlayBounds.Bottom) / 2 - host.Top;
                if (GetWindowRgn(VideoHost(target.MainWindowHandle), region) > 0 && !PtInRegion(region, x, y))
                    throw new InvalidOperationException("The native video was cut away below alpha diagnostics.");
            }
            finally { DeleteObject(region); }
            if (cycle == 0 && !string.IsNullOrEmpty(screenshot)) Snapshot(target, screenshot, false);
            VideoF12(target);
            WaitDiagnostics(root, false, exactTestRate);
            WaitNativeDiagnostics(target, false);
        }
        var escaped = new System.Text.StringBuilder();
        foreach (char character in information)
        {
            if (character < 32 || character > 126) escaped.Append("\\u" + ((int)character).ToString("x4"));
            else if (character == '\\' || character == '"') escaped.Append('\\').Append(character);
            else escaped.Append(character);
        }
        Console.WriteLine("{\"passed\":true,\"visibleToggles\":4,\"text\":\"" + escaped + "\"}");
    }

    private static Rect VisibleBounds(IntPtr handle, out Rect outer)
    {
        Rect visible;
        if (!GetWindowRect(handle, out outer) ||
            DwmGetWindowAttribute(handle, 9, out visible, Marshal.SizeOf(typeof(Rect))) != 0 ||
            visible.Right <= visible.Left || visible.Bottom <= visible.Top || visible.Left < outer.Left ||
            visible.Top < outer.Top || visible.Right > outer.Right || visible.Bottom > outer.Bottom)
            throw new InvalidOperationException("Cannot read bounded DWM visible-frame coordinates.");
        return visible;
    }

    private static void Snapshot(Process target, string path, bool emit = true)
    {
        Rect window;
        Rect visible = VisibleBounds(target.MainWindowHandle, out window);
        var monitor = new MonitorInfo { Size = (uint)Marshal.SizeOf(typeof(MonitorInfo)) };
        bool coversMonitor = GetMonitorInfo(MonitorFromWindow(target.MainWindowHandle, 2), ref monitor) && visible.Equals(monitor.Monitor);
        Rect video = new Rect();
        IntPtr host = VideoHost(target.MainWindowHandle);
        bool rounded = false;
        if (host != IntPtr.Zero)
        {
            GetWindowRect(host, out video);
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                int width = video.Right - video.Left, height = video.Bottom - video.Top;
                rounded = GetWindowRgn(host, region) > 0 && PtInRegion(region, width / 2, height / 2) &&
                    !PtInRegion(region, 0, 0) && !PtInRegion(region, width - 1, 0) &&
                    !PtInRegion(region, 0, height - 1) && !PtInRegion(region, width - 1, height - 1);
            }
            finally { DeleteObject(region); }
        }
        double chroma = 0;
        int count = 0;
        using (var bitmap = new Bitmap(window.Right - window.Left, window.Bottom - window.Top, PixelFormat.Format32bppRgb))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { if (!PrintWindow(target.MainWindowHandle, dc, 2)) throw new InvalidOperationException("Owned window capture unavailable."); }
                finally { graphics.ReleaseHdc(dc); }
            }
            // Save the exact visible window pixels, not GetWindowRect's
            // invisible resize gutters. No resampling or background painting.
            using (var frame = bitmap.Clone(new Rectangle(visible.Left - window.Left, visible.Top - window.Top,
                visible.Right - visible.Left, visible.Bottom - visible.Top), PixelFormat.Format32bppRgb))
            {
                for (int y = frame.Height / 4; y < frame.Height * 2 / 3; y += Math.Max(1, frame.Height / 40))
                    for (int x = frame.Width / 25; x < frame.Width / 9; x += Math.Max(1, frame.Width / 100))
                    {
                        Color color = frame.GetPixel(x, y);
                        chroma += Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
                        ++count;
                    }
                frame.Save(path, ImageFormat.Png);
            }
        }
        if (!emit) return;
        Console.WriteLine("{\"passed\":true,\"videoWidth\":" + (video.Right - video.Left) +
            ",\"videoHeight\":" + (video.Bottom - video.Top) + ",\"rounded\":" + rounded.ToString().ToLowerInvariant() + ",\"backgroundChroma\":" +
            (chroma / Math.Max(1, count)).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"captureWidth\":" + (visible.Right - visible.Left) + ",\"captureHeight\":" + (visible.Bottom - visible.Top) +
            ",\"coversMonitor\":" + coversMonitor.ToString().ToLowerInvariant() +
            ",\"invisibleFrameLeft\":" + (visible.Left - window.Left) + ",\"invisibleFrameTop\":" + (visible.Top - window.Top) + "}");
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            if (args.Length < 3) throw new ArgumentException("Expected PID, executable path and action.");
            var target = Process.GetProcessById(int.Parse(args[0]));
            if (!string.Equals(target.MainModule.FileName, Path.GetFullPath(args[1]), StringComparison.OrdinalIgnoreCase) ||
                target.MainWindowHandle == IntPtr.Zero) throw new InvalidOperationException("The owned app window is unavailable.");
            if (args[2] == "snapshot") { Snapshot(target, args[3]); return 0; }
            if (args[2] == "diagnostics") { Diagnostics(target, args.Length < 5 || args[4] != "any-rate",
                args.Length > 3 ? args[3] : null, args.Length > 5 && args[5] == "fullscreen"); return 0; }
            if (args[2] == "live-layout")
            {
                var liveRoot = AutomationElement.FromHandle(target.MainWindowHandle);
                foreach (string controlId in new[] { "VideoPlayPauseButton", "VideoSkipBackwardButton", "VideoSkipForwardButton", "VideoSeekSlider" })
                {
                    var hidden = liveRoot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, controlId));
                    if (hidden != null && !hidden.Current.IsOffscreen && !hidden.Current.BoundingRectangle.IsEmpty && hidden.Current.BoundingRectangle.Width > 0)
                        throw new InvalidOperationException("Unexpected live transport: " + controlId);
                }
                foreach (string controlId in new[] { "PlayerStopButton", "RotateLeftButton", "RotateRightButton", "FullScreenButton" })
                {
                    var visible = liveRoot.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, controlId));
                    if (visible == null || visible.Current.IsOffscreen || !visible.Current.IsEnabled)
                        throw new InvalidOperationException("Missing live action: " + controlId);
                }
                Console.WriteLine("{\"passed\":true}");
                return 0;
            }
            if (args[2] == "video-space")
            {
                IntPtr host = VideoHost(target.MainWindowHandle);
                if (host == IntPtr.Zero || !PostMessage(host, 0x0100, new IntPtr(0x20), new IntPtr(1)) ||
                    !PostMessage(host, 0x0101, new IntPtr(0x20), new IntPtr(unchecked((int)0xC0000001))))
                    throw new InvalidOperationException("Cannot route Space through the owned native video host.");
                Console.WriteLine("{\"passed\":true}");
                return 0;
            }
            if (args[2] == "focused-space")
            {
                if (args.Length < 4 || Array.IndexOf(new[] { "RotateLeftButton", "RotateRightButton", "FullScreenButton", "PlayerStopButton" }, args[3]) < 0)
                    throw new ArgumentException("Expected an owned player action for the Space focus guard.");
                var focusRoot = AutomationElement.FromHandle(target.MainWindowHandle);
                var focusedControl = focusRoot.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, args[3]));
                if (focusedControl == null || !focusedControl.Current.IsEnabled || focusedControl.Current.IsOffscreen)
                    throw new InvalidOperationException("Player focus target unavailable: " + args[3]);
                focusedControl.SetFocus();
                uint owner;
                uint thread = GetWindowThreadProcessId(target.MainWindowHandle, out owner);
                var gui = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
                Rect windowBefore, hostBefore;
                IntPtr host = VideoHost(target.MainWindowHandle);
                if (owner != target.Id || !GetGUIThreadInfo(thread, ref gui) || gui.Focus == IntPtr.Zero ||
                    GetWindowThreadProcessId(gui.Focus, out owner) == 0 || owner != target.Id ||
                    !GetWindowRect(target.MainWindowHandle, out windowBefore) || !GetWindowRect(host, out hostBefore))
                    throw new InvalidOperationException("Cannot inspect the owned focused player window.");
                if (!PostMessage(gui.Focus, 0x0100, new IntPtr(0x20), new IntPtr(1)) ||
                    !PostMessage(gui.Focus, 0x0100, new IntPtr(0x20), new IntPtr(0x40000001)) ||
                    !PostMessage(gui.Focus, 0x0101, new IntPtr(0x20), new IntPtr(unchecked((int)0xC0000001))))
                    throw new InvalidOperationException("Cannot send Space down, repeat and up to the owned XAML focus target.");
                Thread.Sleep(300);
                Rect windowAfter, hostAfter;
                if (!IsWindowVisible(host) || !GetWindowRect(target.MainWindowHandle, out windowAfter) || !GetWindowRect(host, out hostAfter) ||
                    !windowBefore.Equals(windowAfter) || !hostBefore.Equals(hostAfter))
                    throw new InvalidOperationException("Focused Space rotated, disconnected or changed the full-screen player.");
                Console.WriteLine("{\"passed\":true}");
                return 0;
            }
            string id = args[2] == "fullscreen" ? "FullScreenButton" : args[2] == "toggle" ? "VideoPlayPauseButton" : args[2] == "back10" ? "VideoSkipBackwardButton" :
                args[2] == "forward10" ? "VideoSkipForwardButton" : "VideoSeekSlider";
            var root = AutomationElement.FromHandle(target.MainWindowHandle);
            var control = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (control == null || !control.Current.IsEnabled)
                throw new InvalidOperationException("Visible enabled control unavailable: " + id);
            control.SetFocus();
            if (control.Current.IsOffscreen) throw new InvalidOperationException("Control is off screen: " + id);
            if (args[2] == "fullscreen" || args[2] == "toggle" || args[2] == "back10" || args[2] == "forward10")
                ((InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            else if (args[2] == "seek" || args[2] == "seek-start")
            {
                var range = (RangeValuePattern)control.GetCurrentPattern(RangeValuePattern.Pattern);
                if (range.Current.IsReadOnly) throw new InvalidOperationException("Seeking is read-only.");
                range.SetValue(args[2] == "seek-start" ? 0 : 500);
            }
            else if (args[2] == "key" || args[2] == "drag" || args[2] == "space")
            {
                ShowWindow(target.MainWindowHandle, 9);
                SetForegroundWindow(target.MainWindowHandle);
                control.SetFocus();
                uint foreground = 0;
                for (int attempt = 0; attempt < 20; ++attempt)
                {
                    GetWindowThreadProcessId(GetForegroundWindow(), out foreground);
                    if (foreground == target.Id) break;
                    SetForegroundWindow(target.MainWindowHandle);
                    Thread.Sleep(25);
                }
                if (foreground != target.Id)
                    throw new InvalidOperationException("The owned app is not in the foreground.");
                if (args[2] == "key" || args[2] == "space")
                {
                    byte key = args[2] == "space" ? (byte)0x20 : (byte)0x24;
                    keybd_event(key, 0, 0, UIntPtr.Zero);
                    keybd_event(key, 0, 2, UIntPtr.Zero);
                }
                else
                {
                    Point previous;
                    if (!GetCursorPos(out previous)) throw new InvalidOperationException("Cannot read cursor position.");
                    var rect = control.Current.BoundingRectangle;
                    int y = (int)(rect.Top + rect.Height / 2);
                    try
                    {
                        if (!SetCursorPos((int)(rect.Left + rect.Width / 4), y)) throw new InvalidOperationException("Cannot target the owned seek bar.");
                        mouse_event(2, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(80);
                        for (int step = 1; step <= 5; ++step)
                        {
                            if (!SetCursorPos((int)Math.Ceiling(rect.Left + rect.Width * (.25 + .05 * step)), y))
                                throw new InvalidOperationException("Cannot drag the owned seek bar.");
                            Thread.Sleep(30);
                        }
                    }
                    finally
                    {
                        mouse_event(4, 0, 0, 0, UIntPtr.Zero);
                        Thread.Sleep(100);
                        SetCursorPos(previous.X, previous.Y);
                    }
                }
            }
            else throw new ArgumentException("Unknown action.");
            Console.WriteLine("{\"passed\":true}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
