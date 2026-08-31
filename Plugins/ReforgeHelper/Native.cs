namespace ReforgeHelper
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

        public static void Ctrl(bool down) =>
            keybd_event(VkControl, 0, down ? 0 : KeyeventfKeyup, 0);

        public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

        public static bool IsCtrlDown() =>
            IsKeyDown(VkControl) || IsKeyDown(VkLControl) || IsKeyDown(VkRControl);

        public static bool IsAltDown() =>
            IsKeyDown(VkMenu) || IsKeyDown(VkLMenu) || IsKeyDown(VkRMenu);

        public static bool IsShiftDown() =>
            IsKeyDown(VkShift) || IsKeyDown(VkLShift) || IsKeyDown(VkRShift);
    }
}
