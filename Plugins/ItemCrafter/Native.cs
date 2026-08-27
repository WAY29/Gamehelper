namespace ItemCrafter
{
    using System.Runtime.InteropServices;

    internal static class Native
    {
        private const int MouseLeftDown = 0x0002;
        private const int MouseLeftUp = 0x0004;
        private const int MouseRightDown = 0x0008;
        private const int MouseRightUp = 0x0010;
        private const int KeyeventfKeyup = 0x0002;
        private const byte VkShift = 0x10;

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out Point lpPoint);

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

        public static void RightClick()
        {
            mouse_event(MouseRightDown, 0, 0, 0, 0);
            mouse_event(MouseRightUp, 0, 0, 0, 0);
        }

        public static void Shift(bool down) =>
            keybd_event(VkShift, 0, down ? 0 : KeyeventfKeyup, 0);
    }
}
