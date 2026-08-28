namespace AutoStash
{
    using System.Runtime.InteropServices;

    internal static class Native
    {
        private const int MouseLeftDown = 0x0002;
        private const int MouseLeftUp = 0x0004;
        private const int KeyeventfKeyup = 0x0002;
        private const byte VkShift = 0x10;
        private const byte VkControl = 0x11;
        private const byte VkMenu = 0x12;
        private const int VkLButton = 0x01;
        private const int VkLShift = 0xA0;
        private const int VkRShift = 0xA1;
        private const int VkLControl = 0xA2;
        private const int VkRControl = 0xA3;
        private const int VkLMenu = 0xA4;
        private const int VkRMenu = 0xA5;

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        public static bool IsPidForeground(uint pid)
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return false;
            }

            GetWindowThreadProcessId(fg, out var fgPid);
            return fgPid == pid;
        }

        public static bool FocusWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            var fg = GetForegroundWindow();
            if (fg == hwnd)
            {
                return true;
            }

            var current = GetCurrentThreadId();
            var fgTid = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
            if (fgTid != 0 && fgTid != current)
            {
                AttachThreadInput(current, fgTid, true);
            }

            var ok = SetForegroundWindow(hwnd);
            if (fgTid != 0 && fgTid != current)
            {
                AttachThreadInput(current, fgTid, false);
            }

            return ok;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdi);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BitmapInfo bmi, uint usage);

        private const uint SrcCopy = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader bmiHeader;
            public uint bmiColors;
        }

        public sealed class Grab
        {
            public required byte[] Bgra;
            public required int X;
            public required int Y;
            public required int W;
            public required int H;
        }

        public static Grab? Capture(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
            {
                return null;
            }

            var screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return null;
            }

            var mem = CreateCompatibleDC(screen);
            var bmp = CreateCompatibleBitmap(screen, w, h);
            var old = SelectObject(mem, bmp);
            try
            {
                if (mem == IntPtr.Zero || bmp == IntPtr.Zero ||
                    !BitBlt(mem, 0, 0, w, h, screen, x, y, SrcCopy))
                {
                    return null;
                }

                SelectObject(mem, old);
                old = IntPtr.Zero;
                var info = new BitmapInfo
                {
                    bmiHeader = new BitmapInfoHeader
                    {
                        biSize = 40,
                        biWidth = w,
                        biHeight = -h,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = 0,
                    },
                };
                var bits = new byte[checked(w * h * 4)];
                if (GetDIBits(mem, bmp, 0, (uint)h, bits, ref info, 0) == 0)
                {
                    return null;
                }

                return new Grab { Bgra = bits, X = x, Y = y, W = w, H = h };
            }
            finally
            {
                if (old != IntPtr.Zero)
                {
                    SelectObject(mem, old);
                }

                if (bmp != IntPtr.Zero)
                {
                    DeleteObject(bmp);
                }

                if (mem != IntPtr.Zero)
                {
                    DeleteDC(mem);
                }

                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        public static bool IsHighlighted(Grab grab, int screenX, int screenY, int radius, int threshold, int minHits)
        {
            var hits = 0;
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = screenX + dx - grab.X;
                    var y = screenY + dy - grab.Y;
                    if ((uint)x >= (uint)grab.W || (uint)y >= (uint)grab.H)
                    {
                        continue;
                    }

                    var i = ((y * grab.W) + x) * 4;
                    var b = grab.Bgra[i];
                    var g = grab.Bgra[i + 1];
                    var r = grab.Bgra[i + 2];
                    var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    if (spread >= threshold && ++hits >= minHits)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        public static void MoveTo(int x, int y) => SetCursorPos(x, y);

        public static Point Cursor()
        {
            GetCursorPos(out var p);
            return p;
        }

        public static void LeftClick()
        {
            mouse_event(MouseLeftDown, 0, 0, 0, 0);
            mouse_event(MouseLeftUp, 0, 0, 0, 0);
        }

        public static void SetModifiers(ClickModifiers modifiers, bool down)
        {
            if (modifiers.Ctrl)
            {
                keybd_event(VkControl, 0, down ? 0 : KeyeventfKeyup, 0);
            }

            if (modifiers.Alt)
            {
                keybd_event(VkMenu, 0, down ? 0 : KeyeventfKeyup, 0);
            }

            if (modifiers.Shift)
            {
                keybd_event(VkShift, 0, down ? 0 : KeyeventfKeyup, 0);
            }
        }

        public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

        public static bool IsLeftMouseDown() => IsKeyDown(VkLButton);

        public static bool IsCtrlDown() => IsKeyDown(VkLControl) || IsKeyDown(VkRControl) || IsKeyDown(VkControl);

        public static bool IsAltDown() => IsKeyDown(VkLMenu) || IsKeyDown(VkRMenu) || IsKeyDown(VkMenu);

        public static bool IsShiftDown() => IsKeyDown(VkLShift) || IsKeyDown(VkRShift) || IsKeyDown(VkShift);
    }
}
