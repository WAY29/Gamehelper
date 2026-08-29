namespace AutoStash
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Reflection;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class AutoStashCore : PCore<AutoStashSettings>
    {
        private const int ItemPtrHint = 0x4F8;
        private const int InventoryCols = 12;
        private const int InventoryRows = 5;
        private const int ScanMs = 120;
        private const int HighlightRadius = 12;
        private const int HighlightMinPixels = 4;
        private const float HudHeight = 44f;
        private const float FallbackSize = 30f;
        private const float ButtonGap = 4f;
        private const float InvGapX = 6f;
        private const float InvOffsetY = 6f;
        private const float StashInsetX = -10f;
        private const float StashBelowFrame = 10f;
        private const int HudHideDelayMs = 400;
        private const int StashTabContainerOff = 0x458;
        private const int StashesBeginOff = 0x358;
        private const int StashesEndOff = 0x360;
        private const int VisibleStashIndexOff = 0x370;
        private const int StashEntryStride = 0x90;
        private const int PlayerServerDataPtrOff = 0x48;
        private const int ServerStashTabSize = 104;
        private const int BaseFlagsOffset = 0xC7;
        private const byte CorruptedBit = 0x01;
        private static readonly int[][] StashFramePaths =
        {
            new[] { 2, 0, 0, 0 },
            new[] { 2 },
        };
        private static readonly int[] InventoryPath = { 5, 36 };
        private static readonly int[][] StashTabsPaths =
        {
            new[] { 2, 0, 0, 0, 1, 1 },
            new[] { 2, 0, 0, 0, 0, 1, 1 },
        };

        private readonly Stopwatch scanTimer = Stopwatch.StartNew();
        private readonly Dictionary<ActionKind, bool> hotkeyWasDown = new();
        private readonly List<string> log = new();
        private readonly List<Act> pending = new();
        private readonly List<Grid> stashGrids = new();
        private readonly List<string> stashTabNames = new();
        private readonly List<int> stashTabIds = new();
        private int stashTabSkipped;
        private string stashTabFlagInfo = string.Empty;
        private IntPtr serverTabsObj;
        private int serverTabsVecOff = -1;
        private int serverTabAddOff = -1;

        private object? handle;
        private MethodInfo? readPtr;
        private MethodInfo? readUi;
        private MethodInfo? readVec;
        private MethodInfo? readWs;
        private MethodInfo? readStdWString;
        private MethodInfo? readU32;
        private MethodInfo? readByte;

        private Grid? inventoryGrid;
        private Vector2 stashPanelPos;
        private Vector2 stashPanelSize;
        private Vector2 inventoryPanelPos;
        private Vector2 inventoryPanelSize;
        private string scanStatus = "idle";
        private string lastStop = "idle";
        private int storeCandidates;
        private int takeCandidates;
        private int takeHighlightCandidates;
        private int pendingIndex;
        private long nextAtMs;
        private bool modifiersDown;
        private ClickModifiers? activeModifiers;
        private Native.Point lastClick;
        private bool hasLastClick;
        private bool running;
        private ActionKind runningAction;
        private ActionKind? queuedAction;
        private long queuedAtMs;
        private bool stashOpen;
        private long hudForegroundAtMs;
        private bool texturesTried;
        private IntPtr texLeft;
        private IntPtr texRight;
        private Vector2 texLeftSize;
        private Vector2 texRightSize;
        private int stashTabIndex = -1;
        private IntPtr stashTabContainer;

        private string SettingsPath => Path.Join(this.DllDirectory, "config", "settings.txt");

        private enum ActionKind { Store, Take, TakeHighlighted }
        private enum ActKind { FocusGame, ModOn, ModOff, Move, Left }

        private sealed class Cell
        {
            public IntPtr Slot;
            public IntPtr Item;
            public Vector2 Pos;
            public Vector2 Size;
            public int Row;
            public int Col;
        }

        private sealed class ItemEntry
        {
            public IntPtr Address;
            public string Name = string.Empty;
            public bool CorruptedWaystone;
            public List<Cell> Cells = new();
        }

        private sealed class Grid
        {
            public string Name = string.Empty;
            public Vector2 Pos;
            public Vector2 Size;
            public int Rows;
            public int Cols;
            public float CellW;
            public float CellH;
            public List<Cell> Cells = new();
            public List<ItemEntry> Items = new();
        }

        private sealed class Act
        {
            public ActKind Kind;
            public Vector2 Overlay;
            public string Name = string.Empty;
        }

        public override void OnEnable(bool isGameOpened)
        {
            if (File.Exists(this.SettingsPath))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<AutoStashSettings>(File.ReadAllText(this.SettingsPath))
                        ?? new AutoStashSettings();
                }
                catch
                {
                    this.Settings = new AutoStashSettings();
                }
            }

            this.ClampSettings();
        }

        public override void OnDisable()
        {
            this.Stop("插件关闭");
            this.SaveSettings();
        }

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingsPath) ?? string.Empty);
            File.WriteAllText(this.SettingsPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            this.ClampSettings();
            var changed = false;
            changed |= ImGui.Checkbox(this.PluginText.Label("settings.hud", "Show in-game buttons", "AutoStashHud"), ref this.Settings.ShowHudButtons);
            changed |= ImGui.Checkbox(this.PluginText.Label("settings.debug", "Show debug window", "AutoStashDebug"), ref this.Settings.ShowDebugWindow);
            changed |= ImGui.SliderInt(this.PluginText.Label("settings.hover_delay", "Hover item delay (ms)", "AutoStashHoverDelay"), ref this.Settings.HoverDelayMs, 0, 1000);
            changed |= ImGui.SliderInt(this.PluginText.Label("settings.store_delay", "Store item delay (ms)", "AutoStashStoreDelay"), ref this.Settings.StoreDelayMs, 0, 1000);
            changed |= ImGui.SliderInt(this.PluginText.Label("settings.abort", "Mouse abort (px)", "AutoStashAbort"), ref this.Settings.MouseAbortPx, 5, 120);

            if (ImGui.CollapsingHeader(this.PluginText.Title("settings.store", "Store to stash", "AutoStashStore"), ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= this.DrawActionSettings(this.Settings.Store, "store", "settings.store.click", "Store click modifiers", "settings.store.hotkey", "Store hotkey");
                ImGui.Separator();
                ImGui.Text(this.PluginText.T("settings.store.disabled_cells", "Disabled inventory cells"));
                changed |= this.DrawDisableGrid();
            }

            if (ImGui.CollapsingHeader(this.PluginText.Title("settings.store.options", "Store settings", "AutoStashStoreOptions"), ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= ImGui.Checkbox(
                    this.PluginText.Label("settings.store.exclude_corrupted_waystones", "Skip corrupted waystones", "AutoStashSkipCorruptWs"),
                    ref this.Settings.ExcludeCorruptedWaystones);
            }

            if (ImGui.CollapsingHeader(this.PluginText.Title("settings.take", "Take from stash", "AutoStashTake"), ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= this.DrawActionSettings(this.Settings.Take, "take", "settings.take.click", "Take click modifiers", "settings.take.hotkey", "Take hotkey");
            }

            if (ImGui.CollapsingHeader(this.PluginText.Title("settings.take_highlight", "Take highlighted only", "AutoStashTakeHighlight"), ImGuiTreeNodeFlags.DefaultOpen))
            {
                changed |= this.DrawActionSettings(this.Settings.TakeHighlighted, "takehl", "settings.take_highlight.click", "Highlight-only click modifiers", "settings.take_highlight.hotkey", "Highlight-only hotkey");
                changed |= ImGui.SliderInt(this.PluginText.Label("settings.highlight_threshold", "Highlight threshold (%)", "AutoStashThreshold"), ref this.Settings.HighlightThresholdPercent, 1, 100);
                ImGuiHelper.ToolTip(this.PluginText.T("settings.highlight_threshold.tooltip", "Colored filtered-in items pass, dim filtered-out items skip."));
            }

            if (changed)
            {
                this.ClampSettings();
                this.SaveSettings();
            }
        }

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                if (this.running)
                {
                    this.Stop("不在游戏中");
                }

                return;
            }

            this.ClampSettings();
            try
            {
                if (this.ShouldScan())
                {
                    this.Scan();
                    this.scanTimer.Restart();
                }
            }
            catch (Exception ex)
            {
                this.Log("scan: " + ex);
            }

            try
            {
                if (this.Settings.ShowHudButtons && this.CanUseStashUi() && this.ShouldDrawHud())
                {
                    this.DrawHudButtons();
                }

                this.PollHotkeys();
                this.TryStartQueued();
                if (this.running)
                {
                    this.Tick();
                }
            }
            catch (Exception ex)
            {
                this.Log("run: " + ex);
                if (this.running)
                {
                    this.Stop("异常");
                }
            }

            if (this.Settings.ShowDebugWindow)
            {
                this.DrawDebugWindow();
            }
        }

        private bool DrawActionSettings(ActionSettings settings, string id, string clickKey, string clickFallback, string hotkeyKey, string hotkeyFallback)
        {
            ImGui.Text(this.PluginText.T(clickKey, clickFallback));
            var changed = this.DrawMods(id + "_click", settings.Click);
            ImGui.Separator();
            ImGui.Text(this.PluginText.T(hotkeyKey, hotkeyFallback));
            changed |= this.DrawHotkey(id + "_hotkey", settings.Hotkey);
            return changed;
        }

        private bool DrawMods(string id, ClickModifiers mods)
        {
            var changed = ImGui.Checkbox($"Ctrl##{id}_ctrl", ref mods.Ctrl);
            ImGui.SameLine();
            changed |= ImGui.Checkbox($"Alt##{id}_alt", ref mods.Alt);
            ImGui.SameLine();
            changed |= ImGui.Checkbox($"Shift##{id}_shift", ref mods.Shift);
            return changed;
        }

        private bool DrawHotkey(string id, HotkeyBinding binding)
        {
            var changed = ImGui.Checkbox(this.PluginText.Label("settings.enable", "Enable", id + "_en"), ref binding.Enabled);
            if (!binding.Enabled)
            {
                return changed;
            }

            changed |= ImGui.Checkbox($"Ctrl##{id}_ctrl", ref binding.Ctrl);
            ImGui.SameLine();
            changed |= ImGui.Checkbox($"Alt##{id}_alt", ref binding.Alt);
            ImGui.SameLine();
            changed |= ImGui.Checkbox($"Shift##{id}_shift", ref binding.Shift);
            var none = this.PluginText.T("settings.none", "(none)");
            var preview = binding.Key == 0 ? none : this.KeyLabel(binding.Key);
            if (ImGui.BeginCombo($"Key##{id}_key", preview))
            {
                if (ImGui.Selectable(none, binding.Key == 0))
                {
                    binding.Key = 0;
                    changed = true;
                }

                foreach (var key in Enum.GetValues<VK>())
                {
                    var value = (int)key;
                    if (value <= 0)
                    {
                        continue;
                    }

                    if (ImGui.Selectable(this.KeyLabel(value), binding.Key == value))
                    {
                        binding.Key = value;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            return changed;
        }

        private bool DrawDisableGrid()
        {
            var changed = false;
            ImGui.Text(this.PluginText.T("settings.store.disable_page", "Preset"));
            ImGui.SameLine();
            for (var page = 0; page < 3; page++)
            {
                var selected = this.Settings.DisablePageIndex == page;
                if (selected)
                {
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.54f, 0.22f, 1f));
                }

                if (ImGui.Button($"{page + 1}##disable_page_{page}"))
                {
                    this.Settings.DisablePageIndex = page;
                    return true;
                }

                if (selected)
                {
                    ImGui.PopStyleColor();
                }

                if (page < 2)
                {
                    ImGui.SameLine();
                }
            }

            var cells = this.CurrentDisablePage();
            var disabled = new HashSet<int>(cells);
            var size = new Vector2(22f, 22f);
            for (var row = 0; row < InventoryRows; row++)
            {
                for (var col = 0; col < InventoryCols; col++)
                {
                    var index = (row * InventoryCols) + col;
                    var off = disabled.Contains(index);
                    var color = off ? new Vector4(0.28f, 0.28f, 0.28f, 1f) : new Vector4(0.18f, 0.54f, 0.22f, 1f);
                    ImGui.PushStyleColor(ImGuiCol.Button, color);
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, color);
                    if (ImGui.Button($"##cell_{index}", size))
                    {
                        if (off)
                        {
                            disabled.Remove(index);
                        }
                        else
                        {
                            disabled.Add(index);
                        }

                        changed = true;
                    }

                    ImGui.PopStyleColor(3);
                    if (col + 1 < InventoryCols)
                    {
                        ImGui.SameLine();
                    }
                }
            }

            if (changed)
            {
                this.CurrentDisablePage().Clear();
                this.CurrentDisablePage().AddRange(disabled.OrderBy(x => x));
            }

            return changed;
        }

        private void DrawHudButtons()
        {
            if (this.DrawHudButton("store", this.InventoryButtonPos(), ActionKind.Store))
            {
                this.Queue(ActionKind.Store);
            }

            if (this.DrawHudButton("take", this.StashButtonPos(0, ActionKind.Take), ActionKind.Take))
            {
                this.Queue(ActionKind.Take);
            }

            if (this.DrawHudButton("takehl", this.StashButtonPos(1, ActionKind.TakeHighlighted), ActionKind.TakeHighlighted))
            {
                this.Queue(ActionKind.TakeHighlighted);
            }
        }

        private bool DrawHudButton(string id, Vector2 pos, ActionKind action)
        {
            var size = this.ButtonSize(action);
            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(size, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
                        ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoBackground |
                        ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBringToFrontOnFocus;
            if (!ImGui.Begin("###AutoStashHud_" + id, flags))
            {
                ImGui.End();
                ImGui.PopStyleColor();
                ImGui.PopStyleVar(2);
                return false;
            }

            var clicked = ImGui.InvisibleButton("##" + id, size);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var hovered = ImGui.IsItemHovered();
            if (this.TryTexture(action, out var ptr))
            {
                var tint = action == ActionKind.TakeHighlighted
                    ? new Vector4(1f, 0.93f, 0.48f, hovered ? 1f : 0.98f)
                    : new Vector4(1f, 1f, 1f, hovered ? 1f : 0.94f);
                ImGui.GetWindowDrawList().AddImage(ptr, min, max, Vector2.Zero, Vector2.One, ImGuiHelper.Color(tint));
            }

            if (hovered)
            {
                ImGui.BeginTooltip();
                ImGui.Text(this.ActionLabel(action));
                ImGui.EndTooltip();
            }

            ImGui.End();
            ImGui.PopStyleColor();
            ImGui.PopStyleVar(2);
            return clicked;
        }

        private Vector2 InventoryButtonPos()
        {
            var size = this.ButtonSize(ActionKind.Store);
            var grid = this.inventoryGrid;
            Vector2 pos;
            if (grid != null)
            {
                var cellH = grid.CellH > 0f ? grid.CellH : grid.Size.Y / InventoryRows;
                pos = new Vector2(grid.Pos.X - size.X - InvGapX, grid.Pos.Y + ((cellH - size.Y) * 0.5f) + InvOffsetY);
            }
            else
            {
                pos = new Vector2(this.inventoryPanelPos.X - size.X - InvGapX, this.inventoryPanelPos.Y + InvOffsetY);
            }

            return this.Clamp(pos, size);
        }

        private Vector2 StashButtonPos(int index, ActionKind action)
        {
            var size = this.ButtonSize(action);
            var pos = new Vector2(
                this.stashPanelPos.X + this.stashPanelSize.X - size.X - StashInsetX,
                this.stashPanelPos.Y + this.stashPanelSize.Y + StashBelowFrame + (index * (size.Y + ButtonGap)));
            return this.Clamp(pos, size);
        }

        private void Queue(ActionKind action)
        {
            if (this.running)
            {
                this.Stop("重触发");
            }

            this.queuedAction = action;
            this.queuedAtMs = Environment.TickCount64 + Math.Max(50, this.HoverDelay());
            this.Log("queued " + this.ActionLabel(action));
        }

        private void PollHotkeys()
        {
            var can = this.CanUseStashUi();
            foreach (var action in Enum.GetValues<ActionKind>())
            {
                var binding = this.GetAction(action).Hotkey;
                var down = can && this.IsPressed(binding);
                this.hotkeyWasDown.TryGetValue(action, out var was);
                if (down && !was)
                {
                    this.Queue(action);
                }

                this.hotkeyWasDown[action] = down;
            }
        }

        private void TryStartQueued()
        {
            if (this.queuedAction == null)
            {
                return;
            }

            if (Environment.TickCount64 < this.queuedAtMs)
            {
                return;
            }

            if (Native.IsLeftMouseDown())
            {
                return;
            }

            var action = this.queuedAction.Value;
            this.queuedAction = null;
            this.Start(action);
        }

        private void Start(ActionKind action)
        {
            this.FocusGame();
            if (action == ActionKind.TakeHighlighted)
            {
                Thread.Sleep(40);
            }

            this.Scan();
            var targets = this.BuildTargets(action);
            if (targets.Count == 0)
            {
                this.Log("no targets for " + this.ActionLabel(action));
                this.lastStop = "无目标";
                return;
            }

            this.pending.Clear();
            this.pendingIndex = 0;
            this.hasLastClick = false;
            this.modifiersDown = false;
            this.activeModifiers = this.Clone(this.GetAction(action).Click);
            this.pending.Add(new Act { Kind = ActKind.ModOn });
            foreach (var t in targets)
            {
                this.pending.Add(new Act { Kind = ActKind.Move, Overlay = t.Pos, Name = t.Name });
                this.pending.Add(new Act { Kind = ActKind.Left, Overlay = t.Pos, Name = t.Name });
            }

            this.pending.Add(new Act { Kind = ActKind.ModOff });
            this.running = true;
            this.runningAction = action;
            this.nextAtMs = Environment.TickCount64 + Math.Max(this.HoverDelay(), 180);
            this.Log($"start {this.ActionLabel(action)} targets={targets.Count} acts={this.pending.Count}");
        }

        private void Tick()
        {
            if (!this.CanUseStashUi())
            {
                this.Stop("仓库或背包关闭");
                return;
            }

            if (this.hasLastClick && this.Settings.MouseAbortPx > 0)
            {
                var cur = Native.Cursor();
                var dx = cur.X - this.lastClick.X;
                var dy = cur.Y - this.lastClick.Y;
                if ((dx * dx) + (dy * dy) > this.Settings.MouseAbortPx * this.Settings.MouseAbortPx)
                {
                    this.Stop("鼠标偏离");
                    return;
                }
            }

            if (Environment.TickCount64 < this.nextAtMs)
            {
                return;
            }

            if (this.pendingIndex >= this.pending.Count)
            {
                this.Stop("完成");
                return;
            }

            var act = this.pending[this.pendingIndex];
            if ((act.Kind == ActKind.Move || act.Kind == ActKind.Left) && !Core.Process.Foreground)
            {
                this.FocusGame();
                this.nextAtMs = Environment.TickCount64 + 50;
                return;
            }

            this.pendingIndex++;
            switch (act.Kind)
            {
                case ActKind.FocusGame:
                    this.FocusGame();
                    break;
                case ActKind.ModOn:
                    if (this.activeModifiers != null)
                    {
                        Native.SetModifiers(this.activeModifiers, true);
                        this.modifiersDown = true;
                    }

                    this.Log("modifiers on");
                    break;
                case ActKind.ModOff:
                    if (this.modifiersDown && this.activeModifiers != null)
                    {
                        Native.SetModifiers(this.activeModifiers, false);
                        this.modifiersDown = false;
                    }

                    this.Log("modifiers off");
                    break;
                case ActKind.Move:
                    this.MoveTo(act.Overlay);
                    this.Log($"move {this.pendingIndex}/{this.pending.Count} {act.Name}");
                    break;
                case ActKind.Left:
                    Native.LeftClick();
                    this.Log($"click {act.Name}");
                    break;
            }

            this.nextAtMs = Environment.TickCount64 + (act.Kind == ActKind.Left ? this.StoreDelay() : this.HoverDelay());
            if (this.pendingIndex >= this.pending.Count)
            {
                this.Stop("完成");
            }
        }

        private void Stop(string reason)
        {
            if (this.modifiersDown && this.activeModifiers != null)
            {
                Native.SetModifiers(this.activeModifiers, false);
                this.modifiersDown = false;
            }

            if (this.running)
            {
                this.Log("stop " + this.ActionLabel(this.runningAction) + ": " + reason);
            }

            this.running = false;
            this.pending.Clear();
            this.pendingIndex = 0;
            this.hasLastClick = false;
            this.activeModifiers = null;
            this.lastStop = reason;
        }

        private void MoveTo(Vector2 overlay)
        {
            var wa = Core.Process.WindowArea;
            var x = wa.X + (int)overlay.X;
            var y = wa.Y + (int)overlay.Y;
            Native.MoveTo(x, y);
            this.lastClick = new Native.Point { X = x, Y = y };
            this.hasLastClick = true;
        }

        private void FocusGame()
        {
            try
            {
                var pid = Core.Process.Pid;
                if (pid == 0)
                {
                    return;
                }

                using var proc = Process.GetProcessById((int)pid);
                var hwnd = proc.MainWindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    Native.FocusWindow(hwnd);
                }
            }
            catch (Exception ex)
            {
                this.Log("focus failed: " + ex.Message);
            }
        }

        private void DrawDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(460f, 420f), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.PluginText.Title("window.debug", "AutoStash Debug", "AutoStashDebug"), ref this.Settings.ShowDebugWindow))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"Status: {(this.running ? this.ActionLabel(this.runningAction) : "idle")}");
            ImGui.Text($"Last stop: {this.lastStop}");
            ImGui.Text($"Scan: {this.scanStatus}");
            ImGui.Text($"Foreground game={Core.Process.Foreground} overlay={this.IsGameOrOverlayForeground()} stashOpen={this.stashOpen}");
            ImGui.Separator();
            ImGui.Text($"Inv panel {(int)this.inventoryPanelPos.X},{(int)this.inventoryPanelPos.Y} {(int)this.inventoryPanelSize.X}x{(int)this.inventoryPanelSize.Y}");
            ImGui.Text($"Stash panel {(int)this.stashPanelPos.X},{(int)this.stashPanelPos.Y} {(int)this.stashPanelSize.X}x{(int)this.stashPanelSize.Y}");
            ImGui.Text($"Inv items {this.inventoryGrid?.Items.Count ?? 0}  store {this.storeCandidates}");
            ImGui.Text($"Stash grids {this.stashGrids.Count} items {this.stashGrids.Sum(g => g.Items.Count)} take {this.takeCandidates} hl {this.takeHighlightCandidates}");
            if (this.stashTabNames.Count == 0)
            {
                ImGui.Text("Stash tabs: (none)");
            }
            else
            {
                ImGui.Text($"Stash tabs vis={this.stashTabIndex} usable={this.stashTabNames.Count} skipped={this.stashTabSkipped} {this.stashTabFlagInfo}  stc=0x{this.stashTabContainer.ToInt64():X}");
                for (var i = 0; i < this.stashTabNames.Count; i++)
                {
                    var id = this.stashTabIds[i];
                    ImGui.Text($"{(id == this.stashTabIndex ? ">" : " ")} [{id}] {this.stashTabNames[i]}");
                }
            }
            if (this.queuedAction != null)
            {
                ImGui.Text("Queued: " + this.ActionLabel(this.queuedAction.Value));
            }

            if (this.running)
            {
                ImGui.Text($"Progress {this.pendingIndex}/{this.pending.Count}");
            }

            ImGui.Separator();
            if (ImGui.Button("Clear log"))
            {
                this.log.Clear();
            }

            ImGui.Separator();
            foreach (var line in this.log)
            {
                ImGui.TextUnformatted(line);
            }

            ImGui.End();
        }

        private bool ShouldScan() =>
            this.scanTimer.ElapsedMilliseconds >= ScanMs &&
            (this.running || this.queuedAction != null || this.Settings.ShowDebugWindow || this.CanUseStashUi());

        private void Scan()
        {
            this.inventoryGrid = null;
            this.stashGrids.Clear();
            this.stashTabNames.Clear();
            this.stashTabIds.Clear();
            this.stashTabSkipped = 0;
            this.stashTabFlagInfo = string.Empty;
            this.stashTabIndex = -1;
            this.stashTabContainer = IntPtr.Zero;
            this.stashOpen = false;
            this.storeCandidates = 0;
            this.takeCandidates = 0;
            this.scanStatus = "waiting";
            if (!this.EnsureMem())
            {
                this.scanStatus = "memory bridge unavailable";
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null)
            {
                this.scanStatus = "game ui unavailable";
                return;
            }

            if (!this.TryResolveStashFrame(ui.LeftPanel.Address, out this.stashPanelPos, out this.stashPanelSize))
            {
                PluginUiElementReflection.TryGetAbsoluteRect(ui.LeftPanel.Address, out this.stashPanelPos, out this.stashPanelSize);
            }

            PluginUiElementReflection.TryGetAbsoluteRect(ui.RightPanel.Address, out this.inventoryPanelPos, out this.inventoryPanelSize);
            if (ui.RightPanel.IsVisible)
            {
                var root = this.ResolvePath(ui.RightPanel.Address, InventoryPath);
                this.inventoryGrid = this.BuildGrid("inventory", root, true);
            }

            if (ui.LeftPanel.IsVisible)
            {
                this.CollectStashTabs(ui.LeftPanel.Address);
                var tab = this.ResolveActiveTab(ui.LeftPanel.Address);
                this.stashOpen = tab != IntPtr.Zero;
                if (this.stashOpen)
                {
                    this.CollectStash(tab);
                }
            }

            this.storeCandidates = this.BuildTargets(ActionKind.Store, false).Count;
            this.takeCandidates = this.BuildTargets(ActionKind.Take, false).Count;
            var kind = this.stashGrids.Count == 0 ? string.Empty : " " + string.Join(",", this.stashGrids.Select(g => g.Name).Distinct());
            this.scanStatus = $"inv={this.inventoryGrid?.Items.Count ?? 0} stash={this.stashGrids.Sum(g => g.Items.Count)}{kind}";
        }

        private bool TryResolveStashFrame(IntPtr leftPanel, out Vector2 pos, out Vector2 size)
        {
            pos = Vector2.Zero;
            size = Vector2.Zero;
            if (leftPanel == IntPtr.Zero)
            {
                return false;
            }

            foreach (var path in StashFramePaths)
            {
                var el = this.ResolvePath(leftPanel, path);
                if (this.IsUsableStashFrame(el, out pos, out size))
                {
                    return true;
                }
            }

            var bestArea = 0f;
            var found = false;
            foreach (var child in this.ReadVec(this.ReadUi(leftPanel).ChildrensPtr))
            {
                if (!this.IsUsableStashFrame(child, out var childPos, out var childSize))
                {
                    continue;
                }

                var area = childSize.X * childSize.Y;
                if (area > bestArea)
                {
                    bestArea = area;
                    pos = childPos;
                    size = childSize;
                    found = true;
                }
            }

            return found;
        }

        private bool IsUsableStashFrame(IntPtr el, out Vector2 pos, out Vector2 size)
        {
            pos = Vector2.Zero;
            size = Vector2.Zero;
            if (el == IntPtr.Zero || !this.IsVisible(el) ||
                !PluginUiElementReflection.TryGetAbsoluteRect(el, out pos, out size) ||
                size.X < 280f || size.Y < 280f)
            {
                return false;
            }

            var display = ImGui.GetIO().DisplaySize;
            return display.Y <= 0f || pos.Y + size.Y < display.Y - 90f;
        }

        private void CollectStashTabs(IntPtr leftPanel)
        {
            var seen = new HashSet<long>();
            foreach (var cand in this.StashTabCandidates(leftPanel))
            {
                if (cand == IntPtr.Zero || !seen.Add(cand.ToInt64()))
                {
                    continue;
                }

                if (this.TryReadStashTabContainer(cand) ||
                    this.TryReadStashTabContainer(this.ReadPtr(cand + StashTabContainerOff)))
                {
                    return;
                }
            }
        }

        private IEnumerable<IntPtr> StashTabCandidates(IntPtr leftPanel)
        {
            yield return leftPanel;
            foreach (var path in StashFramePaths)
            {
                yield return this.ResolvePath(leftPanel, path);
            }

            foreach (var path in StashTabsPaths)
            {
                yield return this.ResolvePath(leftPanel, path);
            }

            var cur = this.ResolveActiveTab(leftPanel);
            for (var i = 0; i < 16 && cur != IntPtr.Zero; i++)
            {
                yield return cur;
                cur = this.ReadUi(cur).ParentPtr;
            }
        }

        private bool TryReadStashTabContainer(IntPtr stc)
        {
            if (stc == IntPtr.Zero)
            {
                return false;
            }

            var first = this.ReadPtr(stc + StashesBeginOff);
            var last = this.ReadPtr(stc + StashesEndOff);
            var span = last.ToInt64() - first.ToInt64();
            if (first == IntPtr.Zero || span <= 0 || span % StashEntryStride != 0)
            {
                return false;
            }

            var n = (int)(span / StashEntryStride);
            if (n < 1 || n > 400)
            {
                return false;
            }

            var vis = (int)this.ReadPtr(stc + VisibleStashIndexOff).ToInt64();
            if (vis < 0 || vis >= n)
            {
                return false;
            }

            var uiNames = new List<string>(n);
            var named = 0;
            for (var i = 0; i < n; i++)
            {
                var name = this.ReadWString(first + (i * StashEntryStride));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    named++;
                }
                else
                {
                    name = $"#{i}";
                }

                uiNames.Add(name);
            }

            if (named == 0)
            {
                return false;
            }

            this.TryLocateServerTabs(uiNames);
            var names = new List<string>();
            var ids = new List<int>();
            var skipped = 0;
            for (var i = 0; i < n; i++)
            {
                if (this.IsBlockedTab(i, uiNames[i]))
                {
                    skipped++;
                    continue;
                }

                ids.Add(i);
                names.Add(uiNames[i]);
            }

            this.stashTabContainer = stc;
            this.stashTabIndex = vis;
            this.stashTabSkipped = skipped;
            this.stashTabNames.Clear();
            this.stashTabNames.AddRange(names);
            this.stashTabIds.Clear();
            this.stashTabIds.AddRange(ids);
            return true;
        }

        private bool IsBlockedTab(int index, string name)
        {
            if (this.serverTabsVecOff >= 0 && this.serverTabAddOff >= 0 && this.TryServerTabEntry(index, name, out var entry))
            {
                return (this.ReadU32(entry + this.serverTabAddOff) & 2) == 0;
            }

            return IsBlockedTabName(name);
        }

        private static bool IsBlockedTabName(string name) =>
            name.Contains("只可取出", StringComparison.Ordinal) ||
            name.Contains("无法使用", StringComparison.Ordinal) ||
            name.Contains("無法使用", StringComparison.Ordinal) ||
            name.Contains("Remove-only", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Remove Only", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Unusable", StringComparison.OrdinalIgnoreCase);

        private static string TabBaseName(string name)
        {
            if (name.EndsWith(')') )
            {
                var i = name.LastIndexOf(" (", StringComparison.Ordinal);
                if (i > 0)
                {
                    return name[..i];
                }
            }

            return name;
        }

        private void TryLocateServerTabs(List<string> uiNames)
        {
            if (this.serverTabsVecOff >= 0 && this.TryMatchServerVec(this.serverTabsObj, this.serverTabsVecOff, uiNames, out _))
            {
                this.stashTabFlagInfo = $"flags add@+0x{this.serverTabAddOff:X}";
                return;
            }

            this.serverTabsVecOff = -1;
            this.serverTabAddOff = -1;
            this.serverTabsObj = IntPtr.Zero;
            this.stashTabFlagInfo = "flags (name)";
            var sd = Core.States.InGameStateObject.CurrentAreaInstance.ServerDataObject.Address;
            if (sd == IntPtr.Zero)
            {
                return;
            }

            var vecFirst = this.ReadPtr(sd + PlayerServerDataPtrOff);
            if (vecFirst == IntPtr.Zero)
            {
                return;
            }

            var player0 = this.ReadPtr(vecFirst);
            if (player0 == IntPtr.Zero)
            {
                return;
            }

            var seen = new HashSet<long>();
            if (this.TryScanServerTabsObj(player0, uiNames, seen))
            {
                return;
            }

            for (var off = 0x2E0; off <= 0x360; off += 8)
            {
                if (this.TryScanServerTabsObj(this.ReadPtr(player0 + off), uiNames, seen))
                {
                    return;
                }
            }
        }

        private bool TryScanServerTabsObj(IntPtr obj, List<string> uiNames, HashSet<long> seen)
        {
            if (obj == IntPtr.Zero || !seen.Add(obj.ToInt64()))
            {
                return false;
            }

            for (var off = 0x80; off <= 0x580; off += 8)
            {
                if (!this.TryMatchServerVec(obj, off, uiNames, out var begin))
                {
                    continue;
                }

                var last = this.ReadPtr(obj + off + 8);
                var count = (int)((last.ToInt64() - begin.ToInt64()) / ServerStashTabSize);
                var addOff = this.FindAddFlagOffset(begin, count, uiNames);
                if (addOff < 0)
                {
                    continue;
                }

                this.serverTabsObj = obj;
                this.serverTabsVecOff = off;
                this.serverTabAddOff = addOff;
                this.stashTabFlagInfo = $"flags add@+0x{addOff:X} vec@+0x{off:X}";
                return true;
            }

            return false;
        }

        private bool TryMatchServerVec(IntPtr obj, int off, List<string> uiNames, out IntPtr begin)
        {
            begin = IntPtr.Zero;
            if (obj == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                begin = this.ReadPtr(obj + off);
                var last = this.ReadPtr(obj + off + 8);
                var span = last.ToInt64() - begin.ToInt64();
                if (begin == IntPtr.Zero || span <= 0 || span % ServerStashTabSize != 0)
                {
                    return false;
                }

                var count = (int)(span / ServerStashTabSize);
                if (count < 4 || count > 400 || Math.Abs(count - uiNames.Count) > 40)
                {
                    return false;
                }

                var serverNames = new HashSet<string>(StringComparer.Ordinal);
                var n = Math.Min(count, 64);
                for (var i = 0; i < n; i++)
                {
                    var s = TabBaseName(this.ReadWString(begin + (i * ServerStashTabSize)));
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        serverNames.Add(s);
                    }
                }

                var hits = 0;
                var check = Math.Min(uiNames.Count, 16);
                for (var i = 0; i < check; i++)
                {
                    if (serverNames.Contains(TabBaseName(uiNames[i])))
                    {
                        hits++;
                    }
                }

                return hits >= Math.Min(check, 4);
            }
            catch
            {
                begin = IntPtr.Zero;
                return false;
            }
        }

        private int FindAddFlagOffset(IntPtr begin, int count, List<string> uiNames)
        {
            var pairs = new List<(IntPtr Entry, bool Blocked)>();
            var map = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var entry = begin + (i * ServerStashTabSize);
                var s = TabBaseName(this.ReadWString(entry));
                if (!string.IsNullOrWhiteSpace(s) && !map.ContainsKey(s))
                {
                    map[s] = entry;
                }
            }

            foreach (var name in uiNames)
            {
                if (map.TryGetValue(TabBaseName(name), out var entry))
                {
                    pairs.Add((entry, IsBlockedTabName(name)));
                }
            }

            if (pairs.Count < 8 || !pairs.Any(p => p.Blocked) || !pairs.Any(p => !p.Blocked))
            {
                return -1;
            }

            for (var off = 0x20; off < ServerStashTabSize; off++)
            {
                var ok = true;
                foreach (var (entry, blocked) in pairs)
                {
                    var noAdd = (this.ReadU32(entry + off) & 2) == 0;
                    if (noAdd != blocked)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    return off;
                }
            }

            return -1;
        }

        private bool TryServerTabEntry(int index, string uiName, out IntPtr entry)
        {
            entry = IntPtr.Zero;
            if (this.serverTabsObj == IntPtr.Zero || this.serverTabsVecOff < 0)
            {
                return false;
            }

            var begin = this.ReadPtr(this.serverTabsObj + this.serverTabsVecOff);
            var last = this.ReadPtr(this.serverTabsObj + this.serverTabsVecOff + 8);
            var span = last.ToInt64() - begin.ToInt64();
            if (begin == IntPtr.Zero || span <= 0 || span % ServerStashTabSize != 0)
            {
                return false;
            }

            var count = (int)(span / ServerStashTabSize);
            var baseName = TabBaseName(uiName);
            if ((uint)index < (uint)count)
            {
                var atIndex = begin + (index * ServerStashTabSize);
                if (TabBaseName(this.ReadWString(atIndex)) == baseName)
                {
                    entry = atIndex;
                    return true;
                }
            }

            for (var i = 0; i < count; i++)
            {
                var cur = begin + (i * ServerStashTabSize);
                if (TabBaseName(this.ReadWString(cur)) == baseName)
                {
                    entry = cur;
                    return true;
                }
            }

            return false;
        }

        private IntPtr ResolveActiveTab(IntPtr leftPanel)
        {
            foreach (var path in StashTabsPaths)
            {
                var tabsRoot = this.ResolvePath(leftPanel, path);
                if (tabsRoot == IntPtr.Zero)
                {
                    continue;
                }

                var tabs = this.ReadVec(this.ReadUi(tabsRoot).ChildrensPtr);
                foreach (var tab in tabs)
                {
                    if (this.IsActiveTab(tab))
                    {
                        return tab;
                    }
                }

                foreach (var tab in tabs)
                {
                    if (tab != IntPtr.Zero && this.IsVisible(tab))
                    {
                        return tab;
                    }
                }
            }

            return IntPtr.Zero;
        }

        private bool IsActiveTab(IntPtr tab)
        {
            if (tab == IntPtr.Zero || !this.IsVisible(tab))
            {
                return false;
            }

            if (this.HasGrid(this.ResolvePath(tab, new[] { 0, 0 })))
            {
                return true;
            }

            var waystones = this.ResolvePath(tab, new[] { 0, 1 });
            if (waystones != IntPtr.Zero && this.ReadVec(this.ReadUi(waystones).ChildrensPtr).Length == 16)
            {
                return true;
            }

            var fragments = this.ResolvePath(tab, new[] { 0, 0, 0, 1 });
            return fragments != IntPtr.Zero && this.ReadVec(this.ReadUi(fragments).ChildrensPtr).Length == 6;
        }

        private void CollectStash(IntPtr tab)
        {
            var waystones = this.ResolvePath(tab, new[] { 0, 1 });
            if (waystones != IntPtr.Zero)
            {
                var tiers = this.ReadVec(this.ReadUi(waystones).ChildrensPtr);
                if (tiers.Length == 16)
                {
                    this.CollectWaystoneTab(tiers);
                    if (this.stashGrids.Count > 0)
                    {
                        return;
                    }
                }
            }

            if (this.TryAddStashGrid("stash", this.ResolvePath(tab, new[] { 0, 0 })) ||
                this.TryAddStashGrid("flat", this.ResolvePath(tab, new[] { 0 })))
            {
                return;
            }

            this.CollectFragmentTab(tab);
            if (this.stashGrids.Count == 0)
            {
                this.CollectWells(tab);
            }
        }

        private void CollectWaystoneTab(IntPtr[] tiers)
        {
            foreach (var tier in tiers)
            {
                if (tier == IntPtr.Zero || !this.IsVisible(tier))
                {
                    continue;
                }

                var kids = this.ReadVec(this.ReadUi(tier).ChildrensPtr);
                if (kids.Length == 0 || kids[0] == IntPtr.Zero)
                {
                    continue;
                }

                var c0 = this.ReadVec(this.ReadUi(kids[0]).ChildrensPtr);
                if (c0.Length <= 1 || c0[1] == IntPtr.Zero)
                {
                    continue;
                }

                foreach (var page in this.ReadVec(this.ReadUi(c0[1]).ChildrensPtr))
                {
                    if (page == IntPtr.Zero || !this.IsVisible(page))
                    {
                        continue;
                    }

                    var pageKids = this.ReadVec(this.ReadUi(page).ChildrensPtr);
                    if (pageKids.Length == 0 || pageKids[0] == IntPtr.Zero)
                    {
                        continue;
                    }

                    this.TryAddStashGrid("waystone", pageKids[0]);
                }
            }
        }

        private void CollectFragmentTab(IntPtr tab)
        {
            var fragments = this.ResolvePath(tab, new[] { 0, 0, 0, 1 });
            if (fragments == IntPtr.Zero)
            {
                return;
            }

            var pages = this.ReadVec(this.ReadUi(fragments).ChildrensPtr);
            if (pages.Length != 6)
            {
                return;
            }

            foreach (var page in pages)
            {
                if (page == IntPtr.Zero || !this.IsVisible(page))
                {
                    continue;
                }

                this.TryAddStashGrid("fragment", this.ResolvePath(page, new[] { 0, 0 }));
            }
        }

        private bool TryAddStashGrid(string name, IntPtr root)
        {
            if (!this.HasGrid(root))
            {
                return false;
            }

            var grid = this.BuildGrid(name, root, false);
            if (grid == null || grid.Items.Count == 0)
            {
                return false;
            }

            this.stashGrids.Add(grid);
            return true;
        }

        private bool HasGrid(IntPtr root)
        {
            if (root == IntPtr.Zero || !this.IsVisible(root))
            {
                return false;
            }

            return this.ReadVec(this.ReadUi(root).ChildrensPtr)
                .Any(slot => slot != IntPtr.Zero && this.IsVisible(slot) && PluginUiElementReflection.TryGetAbsoluteRect(slot, out _, out _));
        }

        private Grid? BuildGrid(string name, IntPtr root, bool inventory)
        {
            if (root == IntPtr.Zero)
            {
                return null;
            }

            var cells = new List<Cell>();
            foreach (var slot in this.ReadVec(this.ReadUi(root).ChildrensPtr))
            {
                if (slot == IntPtr.Zero || !this.IsVisible(slot) ||
                    !PluginUiElementReflection.TryGetAbsoluteRect(slot, out var pos, out var size))
                {
                    continue;
                }

                cells.Add(new Cell
                {
                    Slot = slot,
                    Item = this.FindItemPtr(slot, 2),
                    Pos = pos,
                    Size = size,
                });
            }

            if (cells.Count == 0)
            {
                return null;
            }

            var gridPos = new Vector2(cells.Min(c => c.Pos.X), cells.Min(c => c.Pos.Y));
            var gridSize = new Vector2(cells.Max(c => c.Pos.X + c.Size.X) - gridPos.X, cells.Max(c => c.Pos.Y + c.Size.Y) - gridPos.Y);
            if (PluginUiElementReflection.TryGetAbsoluteRect(root, out var rootPos, out var rootSize) &&
                rootSize.X > 0f && rootSize.Y > 0f)
            {
                gridPos = rootPos;
                gridSize = rootSize;
            }

            if (inventory)
            {
                this.AssignFixed(cells, InventoryCols, InventoryRows, gridPos, gridSize);
            }
            else
            {
                this.AssignClustered(cells);
            }

            var grid = new Grid
            {
                Name = name,
                Pos = gridPos,
                Size = gridSize,
                Rows = cells.Max(c => c.Row) + 1,
                Cols = cells.Max(c => c.Col) + 1,
                CellW = cells.Min(c => c.Size.X),
                CellH = cells.Min(c => c.Size.Y),
                Cells = cells,
            };
            this.AttachItems(grid);
            return grid;
        }

        private void AttachItems(Grid grid)
        {
            foreach (var group in grid.Cells.Where(c => c.Item != IntPtr.Zero).GroupBy(c => c.Item))
            {
                var entry = new ItemEntry { Address = group.Key, Cells = group.ToList() };
                var path = string.Empty;
                if (PluginUiElementReflection.TryValidateItemAddress(group.Key, out path, out _))
                {
                    entry.Name = path;
                }

                var item = ReadItem(group.Key);
                if (item?.TryGetComponent<Base>(out var b) == true)
                {
                    entry.Name = b.BaseItemName ?? b.InternalName ?? entry.Name;
                    entry.CorruptedWaystone = IsWaystone(path, b.InternalName) && this.IsCorrupted(b);
                }

                grid.Items.Add(entry);
            }
        }

        private void CollectWells(IntPtr panel)
        {
            if (panel == IntPtr.Zero ||
                !PluginUiElementReflection.TryGetAbsoluteRect(panel, out var clipPos, out var clipSize) ||
                clipSize.X < 200f || clipSize.Y < 200f)
            {
                return;
            }

            var cells = new List<Cell>();
            this.WalkWellGroups(panel, cells, 0, clipPos, clipSize);
            this.WalkIsolatedWells(panel, cells, 0, clipPos, clipSize);
            if (cells.Count == 0)
            {
                return;
            }

            this.AssignClustered(cells);
            var grid = new Grid
            {
                Name = "wells",
                Pos = new Vector2(cells.Min(c => c.Pos.X), cells.Min(c => c.Pos.Y)),
                Size = new Vector2(
                    cells.Max(c => c.Pos.X + c.Size.X) - cells.Min(c => c.Pos.X),
                    cells.Max(c => c.Pos.Y + c.Size.Y) - cells.Min(c => c.Pos.Y)),
                Rows = cells.Max(c => c.Row) + 1,
                Cols = cells.Max(c => c.Col) + 1,
                CellW = cells.Min(c => c.Size.X),
                CellH = cells.Min(c => c.Size.Y),
                Cells = cells,
            };
            this.AttachItems(grid);
            if (grid.Items.Count > 0)
            {
                this.stashGrids.Add(grid);
            }
        }

        private void WalkWellGroups(IntPtr el, List<Cell> dest, int depth, Vector2 clipPos, Vector2 clipSize)
        {
            if (el == IntPtr.Zero || depth > 10 || !this.IsVisible(el))
            {
                return;
            }

            var kids = this.ReadVec(this.ReadUi(el).ChildrensPtr);
            var wells = 0;
            foreach (var kid in kids)
            {
                if (this.IsWellInClip(kid, clipPos, clipSize))
                {
                    wells++;
                }
            }

            if (wells >= 2)
            {
                foreach (var kid in kids)
                {
                    this.TryAddWellCell(kid, dest, clipPos, clipSize);
                }
            }

            foreach (var kid in kids)
            {
                this.WalkWellGroups(kid, dest, depth + 1, clipPos, clipSize);
            }
        }

        private void WalkIsolatedWells(IntPtr el, List<Cell> dest, int depth, Vector2 clipPos, Vector2 clipSize)
        {
            if (el == IntPtr.Zero || depth > 10 || !this.IsVisible(el))
            {
                return;
            }

            this.TryAddWellCell(el, dest, clipPos, clipSize);
            foreach (var kid in this.ReadVec(this.ReadUi(el).ChildrensPtr))
            {
                this.WalkIsolatedWells(kid, dest, depth + 1, clipPos, clipSize);
            }
        }

        private bool IsWellInClip(IntPtr el, Vector2 clipPos, Vector2 clipSize)
        {
            return this.IsVisible(el) &&
                   PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size) &&
                   size.X >= 32f && size.Y >= 32f && size.X <= 220f && size.Y <= 420f &&
                   pos.X + (size.X * 0.5f) >= clipPos.X &&
                   pos.Y + (size.Y * 0.5f) >= clipPos.Y &&
                   pos.X + (size.X * 0.5f) <= clipPos.X + clipSize.X &&
                   pos.Y + (size.Y * 0.5f) <= clipPos.Y + clipSize.Y;
        }

        private bool TryAddWellCell(IntPtr el, List<Cell> dest, Vector2 clipPos, Vector2 clipSize)
        {
            if (!this.IsWellInClip(el, clipPos, clipSize))
            {
                return false;
            }

            PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size);
            var item = this.FindItemPtr(el, 2);
            if (item == IntPtr.Zero)
            {
                return false;
            }

            var area = size.X * size.Y;
            for (var i = 0; i < dest.Count; i++)
            {
                if (dest[i].Slot == el)
                {
                    return false;
                }

                if (dest[i].Item == item)
                {
                    if (area > dest[i].Size.X * dest[i].Size.Y)
                    {
                        dest[i] = new Cell { Slot = el, Item = item, Pos = pos, Size = size };
                    }

                    return true;
                }
            }

            dest.Add(new Cell { Slot = el, Item = item, Pos = pos, Size = size });
            return true;
        }

        private void AssignFixed(List<Cell> cells, int cols, int rows, Vector2 pos, Vector2 size)
        {
            var w = Math.Max(1f, size.X / cols);
            var h = Math.Max(1f, size.Y / rows);
            foreach (var cell in cells)
            {
                var cx = cell.Pos.X + (cell.Size.X * 0.5f);
                var cy = cell.Pos.Y + (cell.Size.Y * 0.5f);
                cell.Col = Math.Clamp((int)Math.Floor((cx - pos.X) / w), 0, cols - 1);
                cell.Row = Math.Clamp((int)Math.Floor((cy - pos.Y) / h), 0, rows - 1);
            }
        }

        private void AssignClustered(List<Cell> cells)
        {
            var h = Math.Max(4f, cells.Average(c => c.Size.Y) * 0.5f);
            var w = Math.Max(4f, cells.Average(c => c.Size.X) * 0.5f);
            var rows = this.Cluster(cells.Select(c => c.Pos.Y + (c.Size.Y * 0.5f)).OrderBy(v => v), h);
            var cols = this.Cluster(cells.Select(c => c.Pos.X + (c.Size.X * 0.5f)).OrderBy(v => v), w);
            foreach (var cell in cells)
            {
                cell.Row = this.Nearest(rows, cell.Pos.Y + (cell.Size.Y * 0.5f));
                cell.Col = this.Nearest(cols, cell.Pos.X + (cell.Size.X * 0.5f));
            }
        }

        private List<float> Cluster(IEnumerable<float> values, float tolerance)
        {
            var anchors = new List<float>();
            var group = new List<float>();
            foreach (var value in values)
            {
                if (group.Count == 0 || Math.Abs(value - group[^1]) <= tolerance)
                {
                    group.Add(value);
                    continue;
                }

                anchors.Add(group.Average());
                group.Clear();
                group.Add(value);
            }

            if (group.Count > 0)
            {
                anchors.Add(group.Average());
            }

            return anchors;
        }

        private int Nearest(List<float> anchors, float value)
        {
            var best = 0;
            var bestD = float.MaxValue;
            for (var i = 0; i < anchors.Count; i++)
            {
                var d = Math.Abs(anchors[i] - value);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            return best;
        }

        private readonly struct Target
        {
            public readonly Vector2 Pos;
            public readonly string Name;
            public readonly int Order;
            public Target(Vector2 pos, string name, int order)
            {
                this.Pos = pos;
                this.Name = name;
                this.Order = order;
            }
        }

        private List<Target> BuildTargets(ActionKind action, bool highlight = true)
        {
            var list = new List<Target>();
            if (action == ActionKind.Store)
            {
                if (this.inventoryGrid == null)
                {
                    return list;
                }

                var disabled = new HashSet<int>(this.CurrentDisablePage());
                foreach (var item in this.inventoryGrid.Items)
                {
                    var origin = item.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col).First();
                    if (disabled.Contains((origin.Row * InventoryCols) + origin.Col))
                    {
                        continue;
                    }

                    if (this.Settings.ExcludeCorruptedWaystones && item.CorruptedWaystone)
                    {
                        continue;
                    }

                    list.Add(new Target(Center(origin), item.Name, (origin.Row * InventoryCols) + origin.Col));
                }
            }
            else
            {
                var probe = action == ActionKind.TakeHighlighted && highlight;
                Native.Grab? grab = null;
                if (probe)
                {
                    grab = this.CaptureStash(this.stashGrids);
                    if (grab == null)
                    {
                        this.Log("highlight capture failed");
                        this.takeHighlightCandidates = 0;
                        return list;
                    }
                }

                var wa = Core.Process.WindowArea;
                var threshold = Math.Clamp((this.Settings.HighlightThresholdPercent * 255 + 50) / 100, 1, 255);
                var grids = this.stashGrids.OrderBy(g => g.Pos.Y).ThenBy(g => g.Pos.X).ToList();
                for (var gi = 0; gi < grids.Count; gi++)
                {
                    var grid = grids[gi];
                    foreach (var item in grid.Items)
                    {
                        var origin = item.Cells.OrderBy(c => c.Row).ThenBy(c => c.Col).First();
                        var center = Center(origin);
                        if (probe && !Native.IsHighlighted(
                                grab!,
                                wa.X + (int)Math.Round(center.X),
                                wa.Y + (int)Math.Round(center.Y),
                                HighlightRadius,
                                threshold,
                                HighlightMinPixels))
                        {
                            continue;
                        }

                        list.Add(new Target(center, item.Name, (gi * 10000) + (origin.Row * Math.Max(1, grid.Cols)) + origin.Col));
                    }
                }

                if (probe)
                {
                    this.takeHighlightCandidates = list.Count;
                }
            }

            return list.OrderBy(t => t.Order).ToList();
        }

        private Native.Grab? CaptureStash(IReadOnlyList<Grid> grids)
        {
            var cells = grids.SelectMany(g => g.Cells).ToList();
            if (cells.Count == 0)
            {
                return null;
            }

            var wa = Core.Process.WindowArea;
            var left = wa.X + (int)Math.Floor(cells.Min(c => c.Pos.X)) - HighlightRadius;
            var top = wa.Y + (int)Math.Floor(cells.Min(c => c.Pos.Y)) - HighlightRadius;
            var right = wa.X + (int)Math.Ceiling(cells.Max(c => c.Pos.X + c.Size.X)) + HighlightRadius;
            var bottom = wa.Y + (int)Math.Ceiling(cells.Max(c => c.Pos.Y + c.Size.Y)) + HighlightRadius;
            try
            {
                return Native.Capture(left, top, right - left, bottom - top);
            }
            catch (Exception ex)
            {
                this.Log("highlight capture: " + ex.Message);
                return null;
            }
        }

        private bool CanUseStashUi()
        {
            var ui = Core.States.InGameStateObject.GameUi;
            return ui != null && ui.LeftPanel.IsVisible && ui.RightPanel.IsVisible;
        }

        private bool ShouldDrawHud()
        {
            if (this.running || this.queuedAction != null)
            {
                return false;
            }

            if (this.IsGameOrOverlayForeground())
            {
                this.hudForegroundAtMs = Environment.TickCount64;
                return true;
            }

            return this.hudForegroundAtMs != 0 &&
                   Environment.TickCount64 - this.hudForegroundAtMs < HudHideDelayMs;
        }

        private bool IsGameOrOverlayForeground()
        {
            if (Core.Process.Foreground)
            {
                return true;
            }

            try
            {
                return Native.IsPidForeground((uint)Environment.ProcessId);
            }
            catch
            {
                return false;
            }
        }

        private ActionSettings GetAction(ActionKind action) => action switch
        {
            ActionKind.Store => this.Settings.Store,
            ActionKind.Take => this.Settings.Take,
            _ => this.Settings.TakeHighlighted,
        };

        private bool IsPressed(HotkeyBinding binding)
        {
            if (!binding.Enabled || binding.Key == 0 || !Native.IsKeyDown(binding.Key))
            {
                return false;
            }

            return Native.IsCtrlDown() == binding.Ctrl &&
                   Native.IsAltDown() == binding.Alt &&
                   Native.IsShiftDown() == binding.Shift;
        }

        private string ActionLabel(ActionKind action) => action switch
        {
            ActionKind.Store => this.PluginText.T("action.store", "Store to stash"),
            ActionKind.Take => this.PluginText.T("action.take", "Take from stash"),
            _ => this.PluginText.T("action.take_highlight", "Take highlighted only"),
        };

        private string KeyLabel(int key) => Enum.IsDefined(typeof(VK), key) ? ((VK)key).ToString() : $"0x{key:X}";

        private Vector2 ButtonSize(ActionKind action)
        {
            this.EnsureTextures();
            if (action == ActionKind.Store)
            {
                return this.texLeft != IntPtr.Zero ? this.texLeftSize : new Vector2(FallbackSize, FallbackSize);
            }

            return this.texRight != IntPtr.Zero ? this.texRightSize : new Vector2(FallbackSize, FallbackSize);
        }

        private bool TryTexture(ActionKind action, out IntPtr ptr)
        {
            this.EnsureTextures();
            ptr = action == ActionKind.Store ? this.texLeft : this.texRight;
            return ptr != IntPtr.Zero;
        }

        private void EnsureTextures()
        {
            if (this.texturesTried)
            {
                return;
            }

            this.texturesTried = true;
            this.LoadTexture("button-arrow-left.png", out this.texLeft, out this.texLeftSize);
            this.LoadTexture("button-arrow-right.png", out this.texRight, out this.texRightSize);
        }

        private void LoadTexture(string file, out IntPtr ptr, out Vector2 size)
        {
            ptr = IntPtr.Zero;
            size = Vector2.Zero;
            var path = Path.Join(this.DllDirectory, file);
            if (!File.Exists(path))
            {
                return;
            }

            Core.Overlay.AddOrGetImagePointer(path, false, out ptr, out var w, out var h);
            if (ptr == IntPtr.Zero || w <= 0 || h <= 0)
            {
                ptr = IntPtr.Zero;
                return;
            }

            var scale = HudHeight / h;
            size = new Vector2(w * scale, HudHeight);
        }

        private Vector2 Clamp(Vector2 pos, Vector2 size)
        {
            var display = ImGui.GetIO().DisplaySize;
            if (display.X <= 0f || display.Y <= 0f)
            {
                return pos;
            }

            return new Vector2(
                Math.Clamp(pos.X, 0f, Math.Max(0f, display.X - size.X)),
                Math.Clamp(pos.Y, 0f, Math.Max(0f, display.Y - size.Y)));
        }

        private ClickModifiers Clone(ClickModifiers mods) => new()
        {
            Ctrl = mods.Ctrl,
            Alt = mods.Alt,
            Shift = mods.Shift,
        };

        private int HoverDelay() => Math.Max(0, this.Settings.HoverDelayMs);

        private int StoreDelay() => Math.Max(0, this.Settings.StoreDelayMs);

        private void ClampSettings()
        {
            this.Settings ??= new AutoStashSettings();
            this.Settings.HoverDelayMs = Math.Clamp(this.Settings.HoverDelayMs, 0, 5000);
            this.Settings.StoreDelayMs = Math.Clamp(this.Settings.StoreDelayMs, 0, 5000);
            this.Settings.MouseAbortPx = Math.Clamp(this.Settings.MouseAbortPx <= 0 ? 48 : this.Settings.MouseAbortPx, 5, 200);
            this.Settings.HighlightThresholdPercent = Math.Clamp(this.Settings.HighlightThresholdPercent <= 0 ? 31 : this.Settings.HighlightThresholdPercent, 1, 100);
            this.Settings.Store ??= new ActionSettings();
            this.Settings.Take ??= new ActionSettings();
            this.Settings.TakeHighlighted ??= new ActionSettings();
            this.Settings.Store.Click ??= new ClickModifiers();
            this.Settings.Take.Click ??= new ClickModifiers();
            this.Settings.TakeHighlighted.Click ??= new ClickModifiers();
            this.Settings.Store.Hotkey ??= new HotkeyBinding();
            this.Settings.Take.Hotkey ??= new HotkeyBinding();
            this.Settings.TakeHighlighted.Hotkey ??= new HotkeyBinding();
            this.Settings.DisablePages ??= new List<List<int>>();
            while (this.Settings.DisablePages.Count < 3)
            {
                this.Settings.DisablePages.Add(new List<int>());
            }

            if (this.Settings.DisablePages.Count > 3)
            {
                this.Settings.DisablePages = this.Settings.DisablePages.Take(3).ToList();
            }

            var legacy = (this.Settings.DisabledInventoryCells ?? new List<int>())
                .Where(i => i >= 0 && i < 60)
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            this.Settings.DisabledInventoryCells = legacy;
            if (legacy.Count > 0 && this.Settings.DisablePages.All(p => p == null || p.Count == 0))
            {
                this.Settings.DisablePages[0] = legacy;
            }

            for (var i = 0; i < this.Settings.DisablePages.Count; i++)
            {
                this.Settings.DisablePages[i] = (this.Settings.DisablePages[i] ?? new List<int>())
                    .Where(cell => cell >= 0 && cell < 60)
                    .Distinct()
                    .OrderBy(cell => cell)
                    .ToList();
            }

            this.Settings.DisablePageIndex = Math.Clamp(this.Settings.DisablePageIndex, 0, 2);
        }

        private List<int> CurrentDisablePage() => this.Settings.DisablePages[this.Settings.DisablePageIndex];

        private void Log(string line)
        {
            var text = $"{DateTime.Now:HH:mm:ss.fff} {line}";
            this.log.Insert(0, text);
            if (this.log.Count > 80)
            {
                this.log.RemoveAt(this.log.Count - 1);
            }

            try
            {
                var dir = Path.Join(this.DllDirectory, "config");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Join(dir, "autostash.log"), text + Environment.NewLine);
            }
            catch
            {
            }
        }

        private bool EnsureMem()
        {
            if (this.handle != null)
            {
                return true;
            }

            var prop = typeof(GameProcess).GetProperty("Handle", BindingFlags.Instance | BindingFlags.NonPublic);
            this.handle = prop?.GetValue(Core.Process);
            if (this.handle == null)
            {
                return false;
            }

            MethodInfo? genericRead = null;
            foreach (var m in this.handle.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1)
                {
                    genericRead = m;
                }

                if (m.Name == "ReadStdVector" && m.IsGenericMethod)
                {
                    this.readVec = m.MakeGenericMethod(typeof(IntPtr));
                }

                if (m.Name == "ReadStdWString" && m.GetParameters().Length == 1)
                {
                    this.readStdWString = m;
                }
            }

            if (genericRead == null || this.readVec == null)
            {
                return false;
            }

            this.readPtr = genericRead.MakeGenericMethod(typeof(IntPtr));
            this.readUi = genericRead.MakeGenericMethod(typeof(UiElementBaseOffset));
            this.readWs = genericRead.MakeGenericMethod(typeof(StdWString));
            this.readU32 = genericRead.MakeGenericMethod(typeof(uint));
            this.readByte = genericRead.MakeGenericMethod(typeof(byte));
            return true;
        }

        private static bool IsWaystone(string path, string? internalName)
        {
            if (path.Contains("Waystone", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("MapKey", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrEmpty(internalName) &&
                   internalName.Contains("Waystone", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsCorrupted(Base? baseComp)
        {
            if (baseComp == null || baseComp.Address == IntPtr.Zero || this.readByte == null)
            {
                return false;
            }

            return (this.ReadByte(baseComp.Address + BaseFlagsOffset) & CorruptedBit) != 0;
        }

        private uint ReadU32(IntPtr addr)
        {
            if (this.readU32 == null || addr == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return this.readU32.Invoke(this.handle, new object[] { addr }) is uint v ? v : 0;
            }
            catch
            {
                return 0;
            }
        }

        private byte ReadByte(IntPtr addr)
        {
            if (this.readByte == null || addr == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return this.readByte.Invoke(this.handle, new object[] { addr }) is byte v ? v : (byte)0;
            }
            catch
            {
                return 0;
            }
        }

        private string ReadWString(IntPtr addr)
        {
            if (this.readWs == null || this.readStdWString == null || addr == IntPtr.Zero)
            {
                return string.Empty;
            }

            try
            {
                if (this.readWs.Invoke(this.handle, new object[] { addr }) is not StdWString ws)
                {
                    return string.Empty;
                }

                return this.readStdWString.Invoke(this.handle, new object[] { ws }) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private IntPtr ReadPtr(IntPtr addr) =>
            this.readPtr!.Invoke(this.handle, new object[] { addr }) is IntPtr p ? p : IntPtr.Zero;

        private UiElementBaseOffset ReadUi(IntPtr addr) =>
            this.readUi!.Invoke(this.handle, new object[] { addr }) is UiElementBaseOffset v ? v : default;

        private IntPtr[] ReadVec(StdVector v) =>
            this.readVec!.Invoke(this.handle, new object[] { v }) as IntPtr[] ?? Array.Empty<IntPtr>();

        private IntPtr ItemPtr(IntPtr el)
        {
            var p = this.ReadPtr(el + ItemPtrHint);
            return PluginUiElementReflection.TryValidateItemAddress(p, out _, out _) ? p : IntPtr.Zero;
        }

        private IntPtr FindItemPtr(IntPtr el, int depth)
        {
            var item = this.ItemPtr(el);
            if (item != IntPtr.Zero)
            {
                return item;
            }

            if (depth <= 0)
            {
                return IntPtr.Zero;
            }

            foreach (var kid in this.ReadVec(this.ReadUi(el).ChildrensPtr))
            {
                item = this.FindItemPtr(kid, depth - 1);
                if (item != IntPtr.Zero)
                {
                    return item;
                }
            }

            return IntPtr.Zero;
        }

        private IntPtr ResolvePath(IntPtr root, int[] path)
        {
            var cur = root;
            foreach (var idx in path)
            {
                if (cur == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var kids = this.ReadVec(this.ReadUi(cur).ChildrensPtr);
                if (idx < 0 || idx >= kids.Length)
                {
                    return IntPtr.Zero;
                }

                cur = kids[idx];
            }

            return cur;
        }

        private bool IsVisible(IntPtr addr)
        {
            var cur = addr;
            for (var i = 0; i < 20 && cur != IntPtr.Zero; i++)
            {
                var off = this.ReadUi(cur);
                if ((off.Self != IntPtr.Zero && off.Self != cur) || !UiElementBaseFuncs.IsVisibleChecker(off.Flags))
                {
                    return false;
                }

                cur = off.ParentPtr;
            }

            return true;
        }

        private static Item? ReadItem(IntPtr addr)
        {
            try
            {
                return Activator.CreateInstance(
                    typeof(Item),
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new object[] { addr },
                    null) as Item;
            }
            catch
            {
                return null;
            }
        }

        private static Vector2 Center(Cell c) => c.Pos + (c.Size * 0.5f);
    }
}
