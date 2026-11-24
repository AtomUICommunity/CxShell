using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace CxShell.Services;

/// <summary>
/// Listens for real-time Windows clipboard changes via <c>AddClipboardFormatListener</c>
/// on a dedicated hidden message-only window thread.
/// </summary>
public sealed class WindowsClipboardListener : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmQuit = 0x0012;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly WndProcDelegate _wndProc;
    private readonly Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private bool _disposed;

    public event Action? ClipboardChanged;

    public WindowsClipboardListener()
    {
        _wndProc = WndProc;
        if (!OperatingSystem.IsWindows())
            return;

        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "CxShell.Win32ClipboardListener"
        };
        _thread.Start();
    }

    private void RunMessageLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            var className = $"CxShellClipboardListener_{Guid.NewGuid():N}";
            var hInstance = GetModuleHandleW(null);

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInstance,
                lpszClassName = className
            };

            var atom = RegisterClassExW(ref wc);
            if (atom == 0)
                return;

            _hwnd = CreateWindowExW(
                0,
                className,
                string.Empty,
                0,
                0, 0, 0, 0,
                HwndMessage,
                IntPtr.Zero,
                hInstance,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                UnregisterClassW(className, hInstance);
                return;
            }

            AddClipboardFormatListener(_hwnd);

            try
            {
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            finally
            {
                RemoveClipboardFormatListener(_hwnd);
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                UnregisterClassW(className, hInstance);
            }
        }
        catch
        {
            // Prevent background clipboard listener failures from crashing the process.
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate)
        {
            ClipboardChanged?.Invoke();
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_threadId != 0)
            PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
