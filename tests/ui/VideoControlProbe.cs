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
    private delegate bool EnumChild(IntPtr window, IntPtr data);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window, EnumChild callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr region);

    private static IntPtr VideoHost(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr data)
        {
            var title = new System.Text.StringBuilder(128);
            GetWindowText(child, title, title.Capacity);
            if (title.ToString() == "ClearCastVideoHost") found = child;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void Snapshot(Process target, string path)
    {
        Rect window;
        if (!GetWindowRect(target.MainWindowHandle, out window)) throw new InvalidOperationException("Cannot measure the owned window.");
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
            for (int y = bitmap.Height / 4; y < bitmap.Height * 2 / 3; y += Math.Max(1, bitmap.Height / 40))
                for (int x = bitmap.Width / 25; x < bitmap.Width / 9; x += Math.Max(1, bitmap.Width / 100))
                {
                    Color color = bitmap.GetPixel(x, y);
                    chroma += Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B));
                    ++count;
                }
            bitmap.Save(path, ImageFormat.Png);
        }
        Console.WriteLine("{\"passed\":true,\"videoWidth\":" + (video.Right - video.Left) +
            ",\"videoHeight\":" + (video.Bottom - video.Top) + ",\"rounded\":" + rounded.ToString().ToLowerInvariant() + ",\"backgroundChroma\":" +
            (chroma / Math.Max(1, count)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
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
            if (args[2] == "store-size")
            {
                Rect bounds;
                if (!GetWindowRect(target.MainWindowHandle, out bounds) ||
                    !MoveWindow(target.MainWindowHandle, bounds.Left, bounds.Top, 1920, 1080, true))
                    throw new InvalidOperationException("Cannot size the owned screenshot window.");
                Console.WriteLine("{\"passed\":true}");
                return 0;
            }
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
            string id = args[2] == "toggle" ? "VideoPlayPauseButton" : args[2] == "back10" ? "VideoSkipBackwardButton" :
                args[2] == "forward10" ? "VideoSkipForwardButton" : "VideoSeekSlider";
            var root = AutomationElement.FromHandle(target.MainWindowHandle);
            var control = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            if (control == null || !control.Current.IsEnabled)
                throw new InvalidOperationException("Visible enabled control unavailable: " + id);
            control.SetFocus();
            if (control.Current.IsOffscreen) throw new InvalidOperationException("Control is off screen: " + id);
            if (args[2] == "toggle" || args[2] == "back10" || args[2] == "forward10")
                ((InvokePattern)control.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            else if (args[2] == "seek")
            {
                var range = (RangeValuePattern)control.GetCurrentPattern(RangeValuePattern.Pattern);
                if (range.Current.IsReadOnly) throw new InvalidOperationException("Seeking is read-only.");
                range.SetValue(500);
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
