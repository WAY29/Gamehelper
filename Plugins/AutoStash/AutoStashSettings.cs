namespace AutoStash
{
    using System.Collections.Generic;
    using GameHelper.Plugin;

    public sealed class ClickModifiers
    {
        public bool Ctrl = true;
        public bool Alt;
        public bool Shift;
    }

    public sealed class HotkeyBinding
    {
        public bool Enabled;
        public bool Ctrl;
        public bool Alt;
        public bool Shift;
        public int Key;
    }

    public sealed class ActionSettings
    {
        public ClickModifiers Click = new();
        public HotkeyBinding Hotkey = new();
    }

    public sealed class AutoStashSettings : IPSettings
    {
        public int SettingsVersion = 2;
        public bool ShowHudButtons = true;
        public bool ShowDebugWindow = true;
        public int HoverDelayMs = 50;
        public int StoreDelayMs = 50;
        public int MouseAbortPx = 48;
        public int HighlightThresholdPercent = 31;

        public ActionSettings Store = new();
        public bool ExcludeCorruptedWaystones;
        public ActionSettings Take = new();
        public ActionSettings TakeHighlighted = new();

        public int DisablePageIndex;
        public List<List<int>> DisablePages = new();
        public List<int> DisabledInventoryCells = new();
    }
}
