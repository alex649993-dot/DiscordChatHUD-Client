using System.Runtime.InteropServices;
using System.Text;

namespace DiscordChatHUD.Interop;

internal static class NativeMethods
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    public const int GwlExStyle = -20;
    public const int WsThickFrame = 0x00040000;
    public const long WsExLayered = 0x00080000L;
    public const long WsExTransparent = 0x00000020L;
    public const long WsExToolWindow = 0x00000080L;
    public const long WsExAppWindow = 0x00040000L;
    public const long WsExNoActivate = 0x08000000L;
    public const int UlwAlpha = 0x00000002;
    public const uint DibRgbColors = 0;
    public const uint BiRgb = 0;
    public const byte AcSrcOver = 0x00;
    public const byte AcSrcAlpha = 0x01;
    public const int SwHide = 0;
    public const int SwShowNoActivate = 8;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpFrameChanged = 0x0020;
    public const uint WdaNone = 0x00000000;
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const uint GaRootOwner = 3;
    public const uint MonitorDefaultToNearest = 2;
    public const int DwmwaCloaked = 14;
    public const int CursorShowing = 0x00000001;

    public const int WmNcHitTest = 0x0084;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const int WmSizing = 0x0214;
    public const int WmMoving = 0x0216;
    public const int WmEnterSizeMove = 0x0231;
    public const int WmExitSizeMove = 0x0232;
    public const int WmQueryEndSession = 0x0011;
    public const int WmEndSession = 0x0016;

    public const int HtTransparent = -1;
    public const int HtCaption = 2;
    public const int HtLeft = 10;
    public const int HtRight = 11;
    public const int HtTop = 12;
    public const int HtTopLeft = 13;
    public const int HtTopRight = 14;
    public const int HtBottom = 15;
    public const int HtBottomLeft = 16;
    public const int HtBottomRight = 17;
    public const int WhKeyboardLl = 13;

    public static readonly IntPtr HwndTopMost = new(-1);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    public delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct LowLevelKeyboardInput
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
        public Point(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Cx;
        public int Cy;
        public Size(int cx, int cy) { Cx = cx; Cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public Point ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPixelsPerMeter;
        public int YPixelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Color0;
        public uint Color1;
        public uint Color2;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr destinationDc,
        ref Point destinationPoint,
        ref Size size,
        IntPtr sourceDc,
        ref Point sourcePoint,
        int colorKey,
        ref BlendFunction blend,
        int flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct UpdateLayeredWindowInfo
    {
        public uint Size;
        public IntPtr DestinationDc, DestinationPoint, WindowSize, SourceDc, SourcePoint;
        public uint ColorKey;
        public IntPtr Blend;
        public uint Flags;
        public IntPtr DirtyRect;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UpdateLayeredWindowInfo info);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr dc,
        ref BitmapInfo bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr objectHandle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr dc);


    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hwnd, StringBuilder className, int count);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int valueSize);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(
        int hookId,
        LowLevelKeyboardProc callback,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorInfo(ref CursorInfo cursorInfo);

    public static bool IsKeyDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    public static bool IsCursorVisible()
    {
        var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0;
    }

    public static string GetWindowText(IntPtr hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        GetWindowTextW(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        GetClassNameW(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    public static bool IsCloaked(IntPtr hwnd)
        => DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
}
