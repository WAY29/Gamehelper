namespace ReforgeHelper
{
    using ClickableTransparentOverlay.Win32;
    using GameHelper.Plugin;

    public sealed class HotkeyBinding
    {
        public bool Enabled = true;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;
        public int Key = (int)VK.F7;
    }

    public sealed class ReforgeHelperSettings : IPSettings
    {
        public HotkeyBinding Toggle = new();
        public int HoverDelayMs = 50;
        public int ClickDelayMs = 50;
        public int ReforgeWaitMs = 2500;
        public int MouseAbortPx = 20;
        public bool ShowDebugWindow;
        public bool ShowLogWindow;
        public string TargetInternalName = string.Empty;
    }
}
