using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace ClearCast;

// The receiver renders to a Win32 child above the WinUI composition window.
internal sealed class NativeVideoHost : IDisposable
{
    private const uint ChildStyle = 0x40000000 | 0x04000000 | 0x02000000; // WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
    private const uint BlackRectangle = 0x00000004;
    private const uint NoActivate = 0x0010;
    private const uint KeepPlacement = 0x0001 | 0x0002 | 0x0004; // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER
    private const uint ShowWindowFlag = 0x0040;
    private const uint HideWindowFlag = 0x0080;
    private const int RegionDifference = 4;
    private const uint ChildWindow = 5; // GW_CHILD
    private const int GetMessageHook = 3;
    private const int DoubleClickWidth = 36;
    private const int DoubleClickHeight = 37;
    private const uint KeyDown = 0x0100;
    private const uint LeftButtonDown = 0x0201;
    private const uint LeftButtonDoubleClick = 0x0203;

    private (int X, int Y, int Width, int Height, bool Visible)? _lastLayout;
    private (uint Time, int X, int Y)? _lastClick;
    private (RectInt32? Bounds, RectInt32? Exclusion) _windowRegion;
    private bool _presentationSuspended;
    private readonly nint _parent;
    private readonly DispatcherQueue _dispatcher;
    private readonly HookProcedure _observeMessage;
    private readonly Dictionary<uint, nint> _inputHooks = [];

    public nint Handle { get; private set; }
    public event Action? DoubleClicked;
    public event Action? EscapePressed;

    public NativeVideoHost(nint parent, DispatcherQueue dispatcher)
    {
        _parent = parent;
        _dispatcher = dispatcher;
        _observeMessage = ObserveMessage;
        Handle = CreateWindowExW(0, "STATIC", "ClearCastVideoHost", ChildStyle | BlackRectangle,
            0, 0, 1, 1, parent, 0, 0, 0);
        if (Handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建屏幕镜像画面区域。");
    }

    public bool Update(RectInt32 bounds, bool visible, RectInt32? clip = null, RectInt32? exclusion = null)
    {
        if (Handle == 0)
            return false;

        visible &= !_presentationSuspended;
        var layout = (X: bounds.X, Y: bounds.Y, Width: Math.Max(1, bounds.Width),
            Height: Math.Max(1, bounds.Height), Visible: visible);
        RectInt32? localExclusion = null;
        if (exclusion is { } area)
        {
            localExclusion = new RectInt32(area.X - layout.X, area.Y - layout.Y, area.Width, area.Height);
            clip ??= new RectInt32(0, 0, layout.Width, layout.Height);
        }
        if (!ApplyRegion(clip, localExclusion))
            return false;
        if (_lastLayout == layout)
            return true;

        // HWND_TOP keeps the video slot above the sibling WinUI composition window.
        if (!SetWindowPos(Handle, 0, layout.X, layout.Y, layout.Width, layout.Height,
            NoActivate | (visible ? ShowWindowFlag : HideWindowFlag)))
            return false;

        bool visibilityChanged = _lastLayout?.Visible != visible;
        _lastLayout = layout;
        if (visibilityChanged && !visible && !_presentationSuspended)
            RemoveInputHooks();
        return true;
    }

    public void Hide()
    {
        HideWindow();
        RemoveInputHooks();
    }

    public void SuspendPresentation()
    {
        _presentationSuspended = true;
        HideWindow();
    }

    public void ResumePresentation() => _presentationSuspended = false;

    public bool HasCurrentSurfaceSize
    {
        get
        {
            if (_lastLayout is not { } layout)
                return true;

            nint surface = GetWindow(Handle, ChildWindow);
            if (surface == 0)
                return true;

            // The native host resizes on its own thread. Its renderer can lag one more message.
            nint renderer = GetWindow(surface, ChildWindow);
            return HasClientSize(surface, layout.Width, layout.Height) &&
                (renderer == 0 || HasClientSize(renderer, layout.Width, layout.Height));
        }
    }

    private void HideWindow()
    {
        if (_lastLayout is not { Visible: true } layout ||
            !SetWindowPos(Handle, 0, 0, 0, 0, 0, KeepPlacement | NoActivate | HideWindowFlag))
            return;

        layout.Visible = false;
        _lastLayout = layout;
    }

    private static bool HasClientSize(nint window, int width, int height) =>
        GetClientRect(window, out var bounds) && bounds.Width == width && bounds.Height == height;

    private bool ApplyRegion(RectInt32? bounds, RectInt32? exclusion)
    {
        if (_windowRegion.Equals((bounds, exclusion)))
            return true;

        nint region = 0;
        if (bounds is { } area)
        {
            region = CreateRegion(area);
            if (region == 0)
                return false;
        }

        // The region leaves the XAML full-screen button visible and clickable above video.
        if (exclusion is { } button)
        {
            nint cutout = CreateRegion(button);
            int combined = cutout == 0 ? 0 : CombineRgn(region, region, cutout, RegionDifference);
            if (cutout != 0)
                DeleteObject(cutout);
            if (combined == 0)
            {
                DeleteObject(region);
                return false;
            }
        }
        if (SetWindowRgn(Handle, region, true) == 0)
        {
            if (region != 0)
                DeleteObject(region);
            return false;
        }
        // A successful SetWindowRgn transfers ownership of the region to the system.
        _windowRegion = (bounds, exclusion);
        return true;
    }

    private static nint CreateRegion(RectInt32 area) =>
        CreateRectRgn(area.X, area.Y, area.X + area.Width, area.Y + area.Height);

    public void RefreshInputHooks()
    {
        if (Handle == 0 || _lastLayout is not { Visible: true })
            return;

        // GStreamer uses its own HWND threads. Observe messages only on our current
        // video descendants and the WinUI thread, without putting a surface over the video.
        var threads = new HashSet<uint>();
        void CollectThread(nint window)
        {
            uint thread = GetWindowThreadProcessId(window, out uint process);
            if (thread != 0 && process == (uint)Environment.ProcessId)
                threads.Add(thread);
        }

        CollectThread(Handle);
        EnumChildWindows(Handle, (window, _) =>
        {
            CollectThread(window);
            return true;
        }, 0);
        foreach (uint thread in _inputHooks.Keys.Where(thread => !threads.Contains(thread)).ToArray())
        {
            UnhookWindowsHookEx(_inputHooks[thread]);
            _inputHooks.Remove(thread);
        }
        foreach (uint thread in threads)
        {
            if (_inputHooks.ContainsKey(thread))
                continue;
            nint hook = SetWindowsHookExW(GetMessageHook, _observeMessage, 0, thread);
            if (hook != 0)
                _inputHooks.Add(thread, hook);
        }
    }

    private void RemoveInputHooks()
    {
        foreach (nint hook in _inputHooks.Values)
            UnhookWindowsHookEx(hook);
        _inputHooks.Clear();
        _lastClick = null;
    }

    private nint ObserveMessage(int code, nuint operation, nint data)
    {
        // PM_REMOVE means the window will process this queued message only once.
        if (code >= 0 && operation == 1 && data != 0)
        {
            var message = Marshal.PtrToStructure<WindowMessage>(data);
            if (message.Message is LeftButtonDown or LeftButtonDoubleClick &&
                (message.Window == Handle || IsChild(Handle, message.Window)))
            {
                _dispatcher.TryEnqueue(() => CheckDoubleClick(message));
            }
            else if (message.Message == KeyDown && message.WParam == 0x1B &&
                (message.LParam & (1 << 30)) == 0 &&
                (message.Window == _parent || IsChild(_parent, message.Window)))
            {
                _dispatcher.TryEnqueue(() => EscapePressed?.Invoke());
            }
        }
        return CallNextHookEx(0, code, operation, data);
    }

    private void CheckDoubleClick(WindowMessage message)
    {
        if (_lastLayout is not { Visible: true })
            return;

        uint dpi = GetDpiForWindow(Handle);
        bool doubleClick = message.Message == LeftButtonDoubleClick ||
            (_lastClick is { } previous &&
                unchecked(message.Time - previous.Time) <= GetDoubleClickTime() &&
                Math.Abs(message.X - previous.X) <= GetSystemMetricsForDpi(DoubleClickWidth, dpi) / 2 &&
                Math.Abs(message.Y - previous.Y) <= GetSystemMetricsForDpi(DoubleClickHeight, dpi) / 2);
        if (doubleClick)
        {
            _lastClick = null;
            DoubleClicked?.Invoke();
        }
        else
        {
            _lastClick = (message.Time, message.X, message.Y);
        }
    }

    public void Dispose()
    {
        if (Handle == 0)
            return;
        RemoveInputHooks();
        DestroyWindow(Handle);
        Handle = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        private uint _reserved;
    }

    private delegate nint HookProcedure(int code, nuint operation, nint data);
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool ChildProcedure(nint window, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out RectInt32 bounds); // RECT origin is (0, 0).

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int CombineRgn(nint destination, nint first, nint second, int mode);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint window, ChildProcedure callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parent, nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowsHookExW(int hook, HookProcedure callback, nint module, uint thread);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CallNextHookEx(nint hook, int code, nuint operation, nint data);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetricsForDpi(int metric, uint dpi);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDoubleClickTime();
}
