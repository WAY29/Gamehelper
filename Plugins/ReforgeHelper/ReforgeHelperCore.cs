namespace ReforgeHelper
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Reflection;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using GameHelper.Data;
    using GameHelper.Localization;
    using GameHelper.Plugin;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameHelper.Utils;
    using GameOffsets.Natives;
    using GameOffsets.Objects.UiElement;
    using ImGuiNET;
    using Newtonsoft.Json;

    public sealed class ReforgeHelperCore : PCore<ReforgeHelperSettings>
    {
        private const int ItemPtrHint = 0x4F8;
        private const int IdentifiedOffset = 0x90;
        private static readonly int[] InventoryPath = { 5, 36 };
        private static readonly int[] ButtonInner = { 3, 1, 0, 0, 1 };
        private static readonly int[] ButtonParentInner = { 3, 1, 0, 0 };
        private static readonly int[] ButtonTextInner = { 3, 1, 0, 0, 0 };
        private static readonly int[] OutputInner = { 3, 1, 1, 0 };
        private static readonly FieldInfo? SettingsVisibleField =
            typeof(Core).Assembly.GetType("GameHelper.Settings.SettingsWindow")
                ?.GetField("isSettingsWindowVisible", BindingFlags.NonPublic | BindingFlags.Static);

        private object? handle;
        private MethodInfo? readPtr;
        private MethodInfo? readByte;
        private MethodInfo? readUi;
        private MethodInfo? readVec;
        private object? benchParents;
        private static readonly PropertyInfo? UiItem =
            PluginUiElementReflection.UiElementBaseType?.GetProperty("Item");
        private static readonly PropertyInfo? UiKids =
            PluginUiElementReflection.UiElementBaseType?.GetProperty("TotalChildrens");
        private static readonly PropertyInfo? UiVisible =
            PluginUiElementReflection.UiElementBaseType?.GetProperty("IsVisible");
        private static readonly FieldInfo? UiParentsField =
            PluginUiElementReflection.UiElementBaseType?.GetField(
                "parents", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<Slot> invSlots = new();
        private readonly List<Slot> highlights = new();
        private readonly List<InWell> inputWells = new();
        private readonly List<Act> pending = new();
        private readonly List<string> log = new();
        private readonly List<CatalogItem> tablets = new();
        private int pendingIndex;
        private long nextAtMs;
        private long waitDeadlineMs;
        private bool running;
        private bool ctrlDown;
        private bool hotkeyWasDown;
        private bool picking;
        private bool freezeHover;
        private long pickArmedAtMs;
        private Native.Point lastClick;
        private bool hasLastClick;
        private string status = "idle";
        private Slot? lastHovered;
        private IntPtr benchPanel;
        private Vector2 buttonPos;
        private Vector2 buttonSize;
        private Vector2 outputPos;
        private Vector2 outputSize;
        private bool hasButton;
        private bool hasOutput;
        private bool outputHasItem;
        private string benchNote = "idle";

        private string SettingPath => Path.Join(this.DllDirectory, "config", "settings.txt");

        private enum ActKind { Move, Left, CtrlOn, CtrlOff, WaitOutput, WaitClear, WaitWells }

        private readonly record struct Act(ActKind Kind, Vector2 Pos, string Name);

        private readonly record struct InWell(IntPtr Addr, Vector2 Pos, Vector2 Size, bool Occupied, int Count);

        private sealed class Slot
        {
            public required Item Item;
            public required Vector2 Pos;
            public required Vector2 Size;
            public required string Path;
            public required string InternalName;
            public string DisplayName = string.Empty;
            public bool Identified = true;
            public int Stack = 1;
            public IntPtr El;
        }

        public override void OnEnable(bool isGameOpened)
        {
            try
            {
                ReforgeLogic.SelfCheck();
            }
            catch (Exception ex)
            {
                this.log.Add($"SelfCheck: {ex.Message}");
            }

            if (File.Exists(this.SettingPath))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<ReforgeHelperSettings>(
                        File.ReadAllText(this.SettingPath),
                        new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace })
                        ?? new ReforgeHelperSettings();
                }
                catch
                {
                    this.Settings = new ReforgeHelperSettings();
                }
            }

            this.Settings.Toggle ??= new HotkeyBinding();
            if (this.Settings.Toggle.Key == 0 && this.Settings.Toggle.Enabled)
            {
                this.Settings.Toggle.Key = (int)VK.F7;
            }
        }

        public override void OnDisable()
        {
            this.Stop("插件关闭");
            this.SaveSettings();
        }

        public override void SaveSettings()
        {
            var dir = Path.GetDirectoryName(this.SettingPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(this.SettingPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            this.RefreshTablets();
            this.ScanInv();
            ImGui.Text(this.PluginText.T("settings.hotkey", "Toggle hotkey"));
            this.DrawHotkey("toggle", this.Settings.Toggle);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.hover_delay", "Hover delay (ms)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##RHHover", ref this.Settings.HoverDelayMs, 0, 1000);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.click_delay", "Click delay (ms)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##RHClick", ref this.Settings.ClickDelayMs, 0, 1000);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.reforge_wait", "Wait for result (ms)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##RHWait", ref this.Settings.ReforgeWaitMs, 200, 8000);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.abort_px", "Mouse abort (px)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##RHAbort", ref this.Settings.MouseAbortPx, 5, 80);

            ImGui.Checkbox(this.PluginText.Label("settings.debug", "Show debug inspector", "RHDebug"), ref this.Settings.ShowDebugWindow);
            ImGui.SameLine();
            ImGui.Checkbox(this.PluginText.Label("settings.log", "Show action log", "RHLog"), ref this.Settings.ShowLogWindow);

            ImGui.Separator();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.target", "Item type"));
            ImGui.SetNextItemWidth(280);
            if (ImGui.BeginCombo("##RHTarget", this.TargetPreview()))
            {
                var lastGroup = int.MinValue;
                foreach (var row in this.tablets)
                {
                    var group = ReforgeLogic.PresetGroup(row.Path);
                    if (group != lastGroup)
                    {
                        ImGui.SeparatorText(this.PresetGroupLabel(group));
                        lastGroup = group;
                    }

                    if (ImGui.Selectable(this.ItemLabel(row), this.IsTarget(row.InternalName)))
                    {
                        this.Settings.TargetInternalName = row.InternalName;
                    }
                }

                ImGui.EndCombo();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(this.PluginText.T("settings.pick", "Pick from inventory")))
            {
                this.BeginPick();
            }

            if (this.picking)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(this.PluginText.T("settings.pick_hint", "Click an inventory item"));
            }

            var matched = 0;
            foreach (var slot in this.invSlots)
            {
                if (this.IsSlotTarget(slot))
                {
                    matched++;
                }
            }

            ImGui.Text(this.PluginText.F("settings.matched", "Matching in inventory: {0}", matched));
            ImGui.TextWrapped(this.running
                ? this.PluginText.F("settings.running", "Running: {0}", this.status)
                : this.PluginText.T("settings.idle", "Idle. Open inventory + reforging bench, press hotkey."));
        }

        public override void DrawUI()
        {
            if (this.picking)
            {
                var io = ImGui.GetIO();
                io.WantCaptureMouse = true;
                io.WantCaptureKeyboard = true;
                if (this.IsPickEsc())
                {
                    this.EndPick();
                }
            }

            if (Core.States.GameCurrentState != GameStateTypes.InGameState)
            {
                if (this.running)
                {
                    this.Stop("不在游戏中");
                }

                return;
            }

            try
            {
                this.PollHotkey();
                if (!this.running && (this.Settings.ShowDebugWindow || this.picking))
                {
                    this.ScanInv();
                    this.FindBench();
                    this.RefreshHover();
                }

                if (this.picking)
                {
                    this.TickPick();
                }

                if (this.running)
                {
                    this.Tick();
                }

                if (this.running || this.Settings.ShowDebugWindow)
                {
                    this.DrawHighlights();
                }

                if (this.Settings.ShowDebugWindow || this.picking)
                {
                    this.DrawHoverRect();
                }

                if (this.Settings.ShowDebugWindow)
                {
                    this.DrawDebugWindow();
                }

                if (this.Settings.ShowLogWindow)
                {
                    this.DrawLogWindow();
                }
            }
            catch (Exception ex)
            {
                this.Log("异常 " + ex.Message);
                if (this.running)
                {
                    this.Stop("异常");
                }
            }
        }

        private void DrawHotkey(string id, HotkeyBinding binding)
        {
            ImGui.Checkbox(this.PluginText.Label("settings.enable", "Enable", id + "_en"), ref binding.Enabled);
            if (!binding.Enabled)
            {
                return;
            }

            ImGui.Checkbox($"Ctrl##{id}_ctrl", ref binding.Ctrl);
            ImGui.SameLine();
            ImGui.Checkbox($"Alt##{id}_alt", ref binding.Alt);
            ImGui.SameLine();
            ImGui.Checkbox($"Shift##{id}_shift", ref binding.Shift);
            var none = this.PluginText.T("settings.none", "(none)");
            var preview = binding.Key == 0 ? none : this.KeyLabel(binding.Key);
            ImGui.SetNextItemWidth(120);
            if (ImGui.BeginCombo($"Key##{id}_key", preview))
            {
                if (ImGui.Selectable(none, binding.Key == 0))
                {
                    binding.Key = 0;
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
                    }
                }

                ImGui.EndCombo();
            }
        }

        private void PollHotkey()
        {
            var down = this.IsPressed(this.Settings.Toggle);
            if (down && !this.hotkeyWasDown)
            {
                if (this.running)
                {
                    this.Stop("热键停止");
                }
                else
                {
                    this.Start();
                }
            }

            this.hotkeyWasDown = down;
        }

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

        private void Start()
        {
            if (string.IsNullOrEmpty(this.Settings.TargetInternalName))
            {
                this.status = "未选择物品类型";
                this.Log(this.status);
                return;
            }

            this.running = true;
            this.pending.Clear();
            this.pendingIndex = 0;
            this.hasLastClick = false;
            this.Log("开始 " + this.TargetPreview());
            this.PlanCycle();
        }

        private void PlanCycle()
        {
            this.pending.Clear();
            this.pendingIndex = 0;
            this.highlights.Clear();
            this.ScanInv();
            if (!this.FindBench(deep: true))
            {
                this.Stop("重铸台未打开 " + this.benchNote);
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || !ui.RightPanel.IsVisible)
            {
                this.Stop("背包未打开");
                return;
            }

            if (this.outputHasItem && this.hasOutput)
            {
                this.QueueTake();
                this.status = "取出结果";
                return;
            }

            if (this.inputWells.Count != 3)
            {
                this.Stop("输入井不是3个");
                return;
            }

            var total = this.WellTotal();
            if (ReforgeLogic.CanReforge(total))
            {
                var btn = CenterRect(this.buttonPos, this.buttonSize);
                this.pending.Add(new Act(ActKind.Move, btn, "獻祭"));
                this.pending.Add(new Act(ActKind.Left, btn, "重铸"));
                this.pending.Add(new Act(ActKind.WaitOutput, default, "等结果"));
                this.QueueTake();
                this.status = $"重铸 {total}";
                this.Log(this.status);
                return;
            }

            var need = 3 - total;
            var matches = new List<NamedPos>();
            foreach (var slot in this.invSlots)
            {
                if (!slot.Identified || !this.IsSlotTarget(slot))
                {
                    continue;
                }

                matches.Add(new NamedPos(slot.InternalName, Center(slot), slot.Stack));
                this.highlights.Add(slot);
            }

            if (!ReforgeLogic.TryTakeUntil(matches, need, out var taken))
            {
                this.Stop($"少于{need}个");
                return;
            }

            this.pending.Add(new Act(ActKind.CtrlOn, default, "Ctrl"));
            foreach (var item in taken)
            {
                this.pending.Add(new Act(ActKind.Move, item.Pos, "背包"));
                this.pending.Add(new Act(ActKind.Left, item.Pos, "放入"));
            }

            this.pending.Add(new Act(ActKind.CtrlOff, default, "Ctrl"));
            this.pending.Add(new Act(ActKind.WaitWells, default, "等补货"));
            this.status = $"补货 {need}";
            this.Log(this.status);
        }

        private void QueueTake()
        {
            this.pending.Add(new Act(ActKind.CtrlOn, default, "Ctrl"));
            this.pending.Add(new Act(ActKind.Move, CenterRect(this.outputPos, this.outputSize), "结果"));
            this.pending.Add(new Act(ActKind.Left, CenterRect(this.outputPos, this.outputSize), "取出"));
            this.pending.Add(new Act(ActKind.CtrlOff, default, "Ctrl"));
            this.pending.Add(new Act(ActKind.WaitClear, default, "等清空"));
        }

        private void Tick()
        {
            if (!Core.Process.Foreground)
            {
                this.Stop("窗口失焦");
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || !ui.RightPanel.IsVisible)
            {
                this.Stop("背包关闭");
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
                this.PlanCycle();
                this.nextAtMs = Environment.TickCount64 + this.ClickDelay();
                return;
            }

            var act = this.pending[this.pendingIndex];
            switch (act.Kind)
            {
                case ActKind.WaitOutput:
                    this.FindBench();
                    if (this.outputHasItem)
                    {
                        this.pendingIndex++;
                        this.Log("结果已出现");
                        break;
                    }

                    if (Environment.TickCount64 > this.waitDeadlineMs && this.waitDeadlineMs != 0)
                    {
                        this.Stop("等待结果超时");
                        return;
                    }

                    if (this.waitDeadlineMs == 0)
                    {
                        this.waitDeadlineMs = Environment.TickCount64 + Math.Max(200, this.Settings.ReforgeWaitMs);
                    }

                    this.nextAtMs = Environment.TickCount64 + this.HoverDelay();
                    return;
                case ActKind.WaitClear:
                    this.FindBench();
                    if (!this.outputHasItem)
                    {
                        this.pendingIndex++;
                        this.waitDeadlineMs = 0;
                        this.Log("结果已取出");
                        break;
                    }

                    if (Environment.TickCount64 > this.waitDeadlineMs && this.waitDeadlineMs != 0)
                    {
                        this.Stop("取出超时");
                        return;
                    }

                    if (this.waitDeadlineMs == 0)
                    {
                        this.waitDeadlineMs = Environment.TickCount64 + Math.Max(200, this.Settings.ReforgeWaitMs);
                    }

                    this.nextAtMs = Environment.TickCount64 + this.HoverDelay();
                    return;
                case ActKind.WaitWells:
                    this.FindBench();
                    if (this.WellsReady())
                    {
                        this.pendingIndex++;
                        this.Log("井已补满");
                        break;
                    }

                    if (Environment.TickCount64 > this.waitDeadlineMs && this.waitDeadlineMs != 0)
                    {
                        this.Stop("补货超时");
                        return;
                    }

                    if (this.waitDeadlineMs == 0)
                    {
                        this.waitDeadlineMs = Environment.TickCount64 + Math.Max(200, this.Settings.ReforgeWaitMs);
                    }

                    this.nextAtMs = Environment.TickCount64 + this.HoverDelay();
                    return;
                case ActKind.CtrlOn:
                    Native.Ctrl(true);
                    this.ctrlDown = true;
                    this.pendingIndex++;
                    this.Log("Ctrl 按下");
                    break;
                case ActKind.CtrlOff:
                    this.ReleaseCtrl();
                    this.pendingIndex++;
                    this.Log("Ctrl 松开");
                    break;
                case ActKind.Move:
                    this.MoveTo(act.Pos);
                    this.pendingIndex++;
                    this.Log($"移动 → {act.Name} ({act.Pos.X:0},{act.Pos.Y:0})");
                    break;
                case ActKind.Left:
                    Native.LeftClick();
                    this.pendingIndex++;
                    this.Log($"左键 → {act.Name}  Ctrl={(this.ctrlDown ? "开" : "关")}");
                    break;
            }

            if (act.Kind is ActKind.WaitOutput or ActKind.WaitClear or ActKind.WaitWells)
            {
                this.waitDeadlineMs = 0;
            }

            this.nextAtMs = Environment.TickCount64 +
                (act.Kind is ActKind.Left ? this.ClickDelay() : this.HoverDelay());
        }

        private void Stop(string reason)
        {
            this.ReleaseCtrl();
            if (this.running)
            {
                this.Log("停止: " + reason);
            }

            this.running = false;
            this.pending.Clear();
            this.pendingIndex = 0;
            this.waitDeadlineMs = 0;
            this.hasLastClick = false;
            this.highlights.Clear();
            this.status = reason;
        }

        private void ReleaseCtrl()
        {
            if (this.ctrlDown || Native.IsCtrlDown())
            {
                Native.Ctrl(false);
            }

            this.ctrlDown = false;
        }

        private bool FindBench(bool deep = false)
        {
            this.hasButton = false;
            this.hasOutput = false;
            this.outputHasItem = false;
            this.benchPanel = IntPtr.Zero;
            this.inputWells.Clear();
            if (!this.EnsureMem())
            {
                this.benchNote = "memory";
                return false;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || ui.Address == IntPtr.Zero)
            {
                this.benchNote = "no GameUi";
                return false;
            }

            var root = ui.Address;
            var skip = this.KnownPanels(ui);
            var kids = this.ReadVec(this.ReadUi(root).ChildrensPtr);
            var p113 = this.ResolvePath(root, new[] { 113 });
            var p3 = this.ResolvePath(root, new[] { 113, 3 });
            var p31 = this.ResolvePath(root, new[] { 113, 3, 1 });
            if (this.TryChain(p113, "113"))
            {
                return true;
            }

            for (var i = 0; i < kids.Length; i++)
            {
                if (i == 113 || skip.Contains(kids[i]))
                {
                    continue;
                }

                if (this.TryChain(kids[i], $"i={i}"))
                {
                    return true;
                }
            }

            var btn = this.ResolvePath(root, new[] { 113, 3, 1, 0, 0, 1 });
            var n311 = this.KidCount(this.ResolvePath(root, new[] { 113, 3, 1, 1 }));
            this.benchNote =
                $"kids={kids.Length} 113n={this.KidCount(p113)} n3={this.KidCount(p3)} n31={this.KidCount(p31)} n311={n311} btn={(btn != IntPtr.Zero)}";
            return false;
        }

        private int KidCount(IntPtr addr) =>
            addr == IntPtr.Zero ? -1 : this.ReadVec(this.ReadUi(addr).ChildrensPtr).Length;

        private bool TryChain(IntPtr panel, string tag)
        {
            if (panel == IntPtr.Zero)
            {
                return false;
            }

            var button = this.ResolvePath(panel, ButtonInner);
            if (button == IntPtr.Zero)
            {
                button = this.ResolvePath(panel, ButtonParentInner);
            }

            if (button == IntPtr.Zero)
            {
                button = this.ResolvePath(panel, ButtonTextInner);
            }

            var output = this.ResolvePath(panel, OutputInner);
            if (output == IntPtr.Zero)
            {
                output = this.FindOutputWell(panel);
            }

            return this.AcceptAddrs(panel, button, output, tag);
        }

        private IntPtr FindOutputWell(IntPtr panel)
        {
            var row = this.ResolvePath(panel, new[] { 3, 1 });
            if (row == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            foreach (var kid in this.ReadVec(this.ReadUi(row).ChildrensPtr))
            {
                if (this.IsWell(kid))
                {
                    return kid;
                }

                var inner = this.ResolvePath(kid, new[] { 0 });
                if (this.IsWell(inner))
                {
                    return inner;
                }
            }

            return IntPtr.Zero;
        }

        private bool IsWell(IntPtr addr) =>
            addr != IntPtr.Zero &&
            this.TryAddrRect(addr, out _, out var size) &&
            IsOutputSize(size);

        private int WellTotal()
        {
            var total = 0;
            foreach (var well in this.inputWells)
            {
                total += well.Count;
            }

            return total;
        }

        private bool WellsReady() =>
            this.inputWells.Count == 3 && ReforgeLogic.CanReforge(this.WellTotal());

        private void ScanInputWells()
        {
            this.inputWells.Clear();
            if (this.benchPanel == IntPtr.Zero || !this.hasOutput)
            {
                return;
            }

            var row = this.ResolvePath(this.benchPanel, new[] { 3, 1 });
            if (row == IntPtr.Zero)
            {
                return;
            }

            var addrs = new List<IntPtr>();
            var rects = new List<WellRect>();
            foreach (var kid in this.ReadVec(this.ReadUi(row).ChildrensPtr))
            {
                var well = IntPtr.Zero;
                if (this.IsWell(kid))
                {
                    well = kid;
                }
                else
                {
                    var inner = this.ResolvePath(kid, new[] { 0 });
                    if (this.IsWell(inner))
                    {
                        well = inner;
                    }
                }

                if (well == IntPtr.Zero ||
                    !this.TryAddrRect(well, out var pos, out var size) ||
                    this.OverlapsInventory(pos, size))
                {
                    continue;
                }

                addrs.Add(well);
                rects.Add(new WellRect(pos, size));
            }

            if (!ReforgeLogic.TryPickThreeInputs(
                    rects,
                    new WellRect(this.outputPos, this.outputSize),
                    out var idx))
            {
                return;
            }

            foreach (var i in idx)
            {
                var item = this.FindItemPtr(addrs[i], 2);
                var occupied = item != IntPtr.Zero &&
                    PluginUiElementReflection.TryValidateItemAddress(item, out _, out _);
                int? stack = null;
                if (occupied)
                {
                    var parsed = ReadItem(item);
                    if (parsed != null && parsed.TryGetComponent<Stack>(out var st))
                    {
                        stack = st.Count;
                    }
                }

                this.inputWells.Add(new InWell(
                    addrs[i],
                    rects[i].Pos,
                    rects[i].Size,
                    occupied,
                    ReforgeLogic.StackCount(occupied, stack)));
            }
        }

        private bool AcceptAddrs(IntPtr panel, IntPtr button, IntPtr output, string tag)
        {
            if (button == IntPtr.Zero || output == IntPtr.Zero)
            {
                return false;
            }

            if (!this.TryAddrRect(button, out this.buttonPos, out this.buttonSize) ||
                !this.TryAddrRect(output, out this.outputPos, out this.outputSize))
            {
                return false;
            }

            if (!IsButtonSize(this.buttonSize) || !IsOutputSize(this.outputSize) ||
                this.OverlapsInventory(this.buttonPos, this.buttonSize) ||
                this.OverlapsInventory(this.outputPos, this.outputSize))
            {
                return false;
            }

            this.benchPanel = panel;
            this.hasButton = true;
            this.hasOutput = true;
            var item = this.FindItemPtr(output, 2);
            this.outputHasItem = item != IntPtr.Zero &&
                PluginUiElementReflection.TryValidateItemAddress(item, out _, out _);
            this.ScanInputWells();
            this.benchNote = $"{tag} btn=({this.buttonPos.X:0},{this.buttonPos.Y:0}) {this.buttonSize.X:0}x{this.buttonSize.Y:0} wells={this.inputWells.Count}";
            return true;
        }

        private bool TryAddrRect(IntPtr addr, out Vector2 pos, out Vector2 size)
        {
            pos = Vector2.Zero;
            size = Vector2.Zero;
            if (addr == IntPtr.Zero)
            {
                return false;
            }

            if (PluginUiElementReflection.TryGetAbsoluteRect(addr, out pos, out size) &&
                this.InClient(pos, size))
            {
                return true;
            }

            return PluginUiElementReflection.TryGetAbsoluteRect(addr, out pos, out size, requireVisible: false) &&
                   this.InClient(pos, size);
        }

        private HashSet<IntPtr> KnownPanels(GameHelper.RemoteObjects.States.InGameStateObjects.ImportantUiElements ui)
        {
            var skip = new HashSet<IntPtr>();
            void Add(IntPtr p)
            {
                if (p != IntPtr.Zero)
                {
                    skip.Add(p);
                }
            }

            Add(ui.RightPanel.Address);
            Add(ui.LeftPanel.Address);
            Add(ui.CurrencyExchangePanel.Address);
            Add(ui.ChatParent.Address);
            Add(ui.LargeMap.Address);
            Add(ui.MiniMap.Address);
            Add(ui.WorldMapPanel.Address);
            return skip;
        }

        private bool TryPanel(object? panel, string tag)
        {
            if (panel == null)
            {
                return false;
            }

            var output = UiWalk(panel, OutputInner);
            return this.AcceptBench(panel, UiWalk(panel, ButtonInner), output, tag + " btn") ||
                   this.AcceptBench(panel, UiWalk(panel, ButtonParentInner), output, tag + " par") ||
                   this.AcceptBench(panel, UiWalk(panel, ButtonTextInner), output, tag + " txt");
        }

        private bool TrySizePanel(object? panel, string tag)
        {
            if (panel == null)
            {
                return false;
            }

            object? button = null;
            object? output = null;
            this.WalkUi(panel, 0, 8, el =>
            {
                if (!this.TryUiRect(el, out _, out var size))
                {
                    return;
                }

                if (button == null && size.X is >= 250 and <= 450 && size.Y is >= 50 and <= 110)
                {
                    button = el;
                }

                if (output == null && size.X is >= 150 and <= 280 && size.Y is >= 300 and <= 520)
                {
                    output = el;
                }
            });
            return this.AcceptBench(panel, button, output, tag);
        }

        private bool AcceptBench(object panel, object? button, object? output, string tag)
        {
            if (button == null || output == null)
            {
                return false;
            }

            if (!this.TryUiRect(button, out this.buttonPos, out this.buttonSize) ||
                !this.TryUiRect(output, out this.outputPos, out this.outputSize))
            {
                return false;
            }

            if (!IsButtonSize(this.buttonSize) || !IsOutputSize(this.outputSize) ||
                this.OverlapsInventory(this.buttonPos, this.buttonSize) ||
                this.OverlapsInventory(this.outputPos, this.outputSize))
            {
                return false;
            }

            this.benchPanel = UiAddr(panel);
            this.hasButton = true;
            this.hasOutput = true;
            var item = this.FindItemPtr(UiAddr(output), 2);
            this.outputHasItem = item != IntPtr.Zero &&
                PluginUiElementReflection.TryValidateItemAddress(item, out _, out _);
            this.benchNote = $"{tag} btn=({this.buttonPos.X:0},{this.buttonPos.Y:0}) {this.buttonSize.X:0}x{this.buttonSize.Y:0}";
            return true;
        }

        private object? SharedParents()
        {
            var ui = Core.States.InGameStateObject.GameUi;
            object? el = ui.RightPanel.Address != IntPtr.Zero ? ui.RightPanel : null;
            el ??= ui.LeftPanel.Address != IntPtr.Zero ? ui.LeftPanel : null;
            el ??= ui.LargeMap;
            return el == null ? null : UiParentsField?.GetValue(el);
        }

        private object? UiAt(IntPtr addr)
        {
            if (addr == IntPtr.Zero)
            {
                return null;
            }

            this.benchParents = this.SharedParents() ?? this.benchParents ??
                PluginUiElementReflection.CreateParents("Reforge");
            if (this.benchParents == null)
            {
                return null;
            }

            try
            {
                return PluginUiElementReflection.CreateUiElement(addr, this.benchParents);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsButtonSize(Vector2 s) =>
            s.X is >= 200 and <= 500 && s.Y is >= 40 and <= 120;

        private static bool IsOutputSize(Vector2 s) =>
            s.X is >= 140 and <= 300 && s.Y is >= 280 and <= 560;

        private static int UiCount(object? el)
        {
            if (el == null || UiKids == null)
            {
                return 0;
            }

            try
            {
                return UiKids.GetValue(el) is int n ? n : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static object? UiChild(object? el, int index)
        {
            if (el == null || UiItem == null || index < 0)
            {
                return null;
            }

            try
            {
                return UiItem.GetValue(el, new object[] { index });
            }
            catch
            {
                return null;
            }
        }

        private static object? UiWalk(object? el, int[] path)
        {
            var cur = el;
            foreach (var index in path)
            {
                cur = UiChild(cur, index);
                if (cur == null)
                {
                    return null;
                }
            }

            return cur;
        }

        private static IntPtr UiAddr(object? el) =>
            el is RemoteObjectBase remote ? remote.Address : IntPtr.Zero;

        private void WalkUi(object el, int depth, int maxDepth, Action<object> visit)
        {
            visit(el);
            if (depth >= maxDepth)
            {
                return;
            }

            var n = UiCount(el);
            for (var i = 0; i < n; i++)
            {
                var child = UiChild(el, i);
                if (child != null)
                {
                    this.WalkUi(child, depth + 1, maxDepth, visit);
                }
            }
        }

        private void ScanInv()
        {
            this.invSlots.Clear();
            if (!this.EnsureMem())
            {
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || !ui.RightPanel.IsVisible)
            {
                return;
            }

            var grid = this.ResolvePath(ui.RightPanel.Address, InventoryPath);
            if (grid == IntPtr.Zero)
            {
                return;
            }

            foreach (var slot in this.ReadVec(this.ReadUi(grid).ChildrensPtr))
            {
                this.TryAddSlot(slot);
            }
        }

        private void TryAddSlot(IntPtr slot)
        {
            if (slot == IntPtr.Zero || !this.IsVisible(slot))
            {
                return;
            }

            var itemAddr = this.FindItemPtr(slot, 2);
            if (itemAddr == IntPtr.Zero ||
                !PluginUiElementReflection.TryValidateItemAddress(itemAddr, out var path, out _))
            {
                return;
            }

            if (!PluginUiElementReflection.TryGetAbsoluteRect(slot, out var pos, out var size) ||
                size.X < 8f || size.Y < 8f || size.X > 280f || size.Y > 280f)
            {
                return;
            }

            var item = ReadItem(itemAddr);
            if (item == null)
            {
                return;
            }

            var internalName = item.TryGetComponent<Base>(out var b) ? b.InternalName : string.Empty;
            if (string.IsNullOrEmpty(internalName))
            {
                var slash = path.LastIndexOf('/');
                internalName = slash >= 0 ? path[(slash + 1)..] : path;
            }

            int? stack = null;
            if (item.TryGetComponent<Stack>(out var st))
            {
                stack = st.Count;
            }

            this.invSlots.Add(new Slot
            {
                Item = item,
                Pos = pos,
                Size = size,
                Path = path,
                InternalName = internalName,
                DisplayName = b?.BaseItemName ?? internalName,
                Identified = this.ReadIdentified(item),
                Stack = ReforgeLogic.StackCount(true, stack),
                El = slot,
            });
        }

        private void BeginPick()
        {
            this.picking = true;
            this.lastHovered = null;
            this.pickArmedAtMs = Environment.TickCount64 + 150;
            this.SetSettingsVisible(false);
        }

        private void EndPick()
        {
            this.picking = false;
            this.SetSettingsVisible(true);
            var io = ImGui.GetIO();
            io.WantCaptureMouse = true;
            io.WantCaptureKeyboard = false;
        }

        private bool IsPickEsc()
        {
            if (!this.picking || Environment.TickCount64 < this.pickArmedAtMs)
            {
                return false;
            }

            return ImGui.IsKeyPressed(ImGuiKey.Escape, false) ||
                   Utils.IsKeyPressedAndNotTimeout(VK.ESCAPE, 200) ||
                   Native.IsKeyDown(0x1B);
        }

        private void TickPick()
        {
            var io = ImGui.GetIO();
            io.WantCaptureMouse = true;
            io.WantCaptureKeyboard = true;
            if (Environment.TickCount64 < this.pickArmedAtMs || this.IsPickEsc())
            {
                return;
            }

            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                return;
            }

            if (this.lastHovered != null)
            {
                this.RefreshTablets();
                this.Settings.TargetInternalName = this.ResolveTargetId(this.lastHovered);
                this.Log("目标 " + this.TargetPreview());
            }

            this.EndPick();
        }

        private void SetSettingsVisible(bool visible)
        {
            SettingsVisibleField?.SetValue(null, visible);
        }

        private void RefreshHover()
        {
            if (this.freezeHover)
            {
                return;
            }

            var mouse = ImGui.GetMousePos();
            Slot? best = null;
            var bestArea = float.MaxValue;
            foreach (var slot in this.invSlots)
            {
                if (!Contains(slot, mouse))
                {
                    continue;
                }

                var area = slot.Size.X * slot.Size.Y;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = slot;
                }
            }

            this.lastHovered = best;
        }

        private void DrawHighlights()
        {
            foreach (var slot in this.highlights)
            {
                ImGuiHelper.DrawRect(slot.Pos, slot.Size, 0, 220, 80);
            }

            if (this.hasButton)
            {
                ImGuiHelper.DrawRect(this.buttonPos, this.buttonSize, 0, 220, 80);
            }

            if (this.hasOutput)
            {
                ImGuiHelper.DrawRect(this.outputPos, this.outputSize, 0, 220, 80);
            }

            foreach (var well in this.inputWells)
            {
                ImGuiHelper.DrawRect(well.Pos, well.Size, 0, 220, 80);
            }
        }

        private void DrawHoverRect()
        {
            if (this.lastHovered == null)
            {
                return;
            }

            ImGui.GetForegroundDrawList().AddRect(
                this.lastHovered.Pos,
                this.lastHovered.Pos + this.lastHovered.Size,
                0xFF0080FF,
                0f,
                ImDrawFlags.None,
                3f);
        }

        private void DrawDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(420, 360), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.PluginText.Title("window.debug", "ReforgeHelper Debug", "RHDbg"), ref this.Settings.ShowDebugWindow))
            {
                ImGui.End();
                return;
            }

            ImGui.Checkbox("Freeze hovered", ref this.freezeHover);
            ImGui.Text($"Inv {this.invSlots.Count}  bench={(this.benchPanel != IntPtr.Zero)}  outputItem={this.outputHasItem}  wells={this.inputWells.Count}  sum={this.WellTotal()}");
            ImGui.Text("bench " + this.benchNote);
            ImGui.Text($"Target {this.Settings.TargetInternalName}");
            ImGui.Text($"Status {this.status}");
            if (this.hasButton)
            {
                ImGui.Text($"Button {this.buttonPos} {this.buttonSize}");
            }

            if (this.hasOutput)
            {
                ImGui.Text($"Output {this.outputPos} {this.outputSize}");
            }

            for (var i = 0; i < this.inputWells.Count; i++)
            {
                var well = this.inputWells[i];
                ImGui.Text($"In{i} n={well.Count} occ={well.Occupied} addr={well.Addr:X} {well.Pos} {well.Size}");
            }

            if (this.lastHovered != null)
            {
                ImGui.Separator();
                ImGuiHelper.DisplayTextAndCopyOnClick($"Path: {this.lastHovered.Path}", this.lastHovered.Path);
                ImGuiHelper.DisplayTextAndCopyOnClick($"Internal: {this.lastHovered.InternalName}", this.lastHovered.InternalName);
                ImGuiHelper.DisplayTextAndCopyOnClick($"Name: {this.lastHovered.DisplayName}", this.lastHovered.DisplayName);
                ImGui.Text(this.lastHovered.Identified ? "已鉴定" : "未鉴定");
            }
            else
            {
                ImGui.Text("悬停背包物品。");
            }

            ImGui.End();
        }

        private void DrawLogWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(480, 280), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.PluginText.Title("window.log", "ReforgeHelper Log", "RHLogWin"), ref this.Settings.ShowLogWindow))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"状态: {this.status}  Ctrl: {(this.ctrlDown ? "开" : "关")}");
            ImGui.BeginChild("RHLogBody", new Vector2(0, 0), ImGuiChildFlags.Borders);
            foreach (var line in this.log)
            {
                ImGui.TextUnformatted(line);
            }

            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 12)
            {
                ImGui.SetScrollHereY(1f);
            }

            ImGui.EndChild();
            ImGui.End();
        }

        private void RefreshTablets()
        {
            ItemCatalog.Touch();
            this.tablets.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var fragment in ReforgeLogic.PresetPathFragments)
            {
                foreach (var row in ItemCatalog.ItemsWherePathContains(fragment))
                {
                    if (string.IsNullOrEmpty(row.InternalName) ||
                        (string.IsNullOrEmpty(row.English) &&
                         string.IsNullOrEmpty(row.ZhCn) &&
                         string.IsNullOrEmpty(row.ZhTw)) ||
                        !seen.Add(row.InternalName))
                    {
                        continue;
                    }

                    this.tablets.Add(row);
                }
            }

            this.tablets.Sort((a, b) =>
            {
                var group = ReforgeLogic.PresetGroup(a.Path).CompareTo(ReforgeLogic.PresetGroup(b.Path));
                return group != 0
                    ? group
                    : string.Compare(this.ItemLabel(a), this.ItemLabel(b), StringComparison.OrdinalIgnoreCase);
            });
        }

        private string TargetPreview()
        {
            if (string.IsNullOrEmpty(this.Settings.TargetInternalName))
            {
                return this.PluginText.T("settings.add_target", "Select tablet");
            }

            if (this.TryResolveCatalog(this.Settings.TargetInternalName, out var item) && item != null)
            {
                return this.ItemLabel(item);
            }

            return this.Settings.TargetInternalName;
        }

        private string PresetGroupLabel(int group) => group switch
        {
            0 => this.PluginText.T("settings.preset_tablet", "Tablets"),
            1 => this.PluginText.T("settings.preset_catalyst", "Catalysts"),
            2 => this.PluginText.T("settings.preset_emotion", "Liquid Emotions"),
            _ => string.Empty,
        };

        private string ItemLabel(CatalogItem row)
        {
            var lang = OverlayLocalization.CurrentLanguage;
            var cjk = lang == OverlayLanguage.ChineseTraditional
                ? FirstNonEmpty(row.ZhTw, row.ZhCn)
                : lang == OverlayLanguage.ChineseSimplified
                    ? FirstNonEmpty(row.ZhCn, row.ZhTw)
                    : string.Empty;
            return FirstNonEmpty(cjk, row.English, row.InternalName);
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private bool IsTarget(string internalName) =>
            this.IsSlotTarget(internalName, string.Empty, string.Empty);

        private bool IsSlotTarget(Slot slot) =>
            this.IsSlotTarget(slot.InternalName, slot.Path, slot.DisplayName);

        private bool IsSlotTarget(string internalName, string path, string displayName)
        {
            var target = this.Settings.TargetInternalName;
            if (ReforgeLogic.Matches(target, internalName, path, displayName))
            {
                return true;
            }

            return this.TryResolveCatalog(target, out var item) &&
                   item != null &&
                   ReforgeLogic.Matches(item.InternalName, internalName, path, displayName);
        }

        private string ResolveTargetId(Slot slot)
        {
            if (!string.IsNullOrEmpty(slot.InternalName) &&
                ItemCatalog.TryGet(slot.InternalName, out var byId) && byId != null)
            {
                return byId.InternalName;
            }

            var tail = ReforgeLogic.PathTail(slot.Path);
            if (!string.IsNullOrEmpty(tail) && ItemCatalog.TryGet(tail, out var byTail) && byTail != null)
            {
                return byTail.InternalName;
            }

            return string.IsNullOrEmpty(slot.InternalName) ? tail : slot.InternalName;
        }

        private bool TryResolveCatalog(string id, out CatalogItem? item)
        {
            item = null;
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            if (ItemCatalog.TryGet(id, out item) && item != null)
            {
                return true;
            }

            foreach (var row in this.tablets)
            {
                if (ReforgeLogic.Matches(id, row.InternalName, row.Path, row.English) ||
                    ReforgeLogic.Matches(id, row.ZhTw) ||
                    ReforgeLogic.Matches(id, row.ZhCn))
                {
                    item = row;
                    return true;
                }
            }

            return false;
        }

        private void MoveTo(Vector2 overlay)
        {
            var wa = Core.Process.WindowArea;
            var x = wa.X + (int)overlay.X;
            var y = wa.Y + (int)overlay.Y;
            Native.MoveTo(x, y);
            this.lastClick = Native.Cursor();
            this.hasLastClick = true;
        }

        private void Log(string line)
        {
            if (this.log.Count > 200)
            {
                this.log.RemoveRange(0, this.log.Count - 150);
            }

            this.log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        }

        private int HoverDelay() => Math.Max(0, this.Settings.HoverDelayMs);

        private int ClickDelay() => Math.Max(0, this.Settings.ClickDelayMs);

        private string KeyLabel(int key) => Enum.IsDefined(typeof(VK), key) ? ((VK)key).ToString() : $"0x{key:X}";

        private static Vector2 Center(Slot s) => s.Pos + (s.Size * 0.5f);

        private static Vector2 CenterRect(Vector2 pos, Vector2 size) => pos + (size * 0.5f);

        private static bool Contains(Slot s, Vector2 p) =>
            p.X >= s.Pos.X && p.X <= s.Pos.X + s.Size.X &&
            p.Y >= s.Pos.Y && p.Y <= s.Pos.Y + s.Size.Y;

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
                if ((off.Self != IntPtr.Zero && off.Self != cur) ||
                    !UiElementBaseFuncs.IsVisibleChecker(off.Flags))
                {
                    return false;
                }

                cur = off.ParentPtr;
            }

            return true;
        }

        private bool IsLocalVisible(IntPtr addr)
        {
            if (addr == IntPtr.Zero)
            {
                return false;
            }

            var off = this.ReadUi(addr);
            return (off.Self == IntPtr.Zero || off.Self == addr) &&
                   UiElementBaseFuncs.IsVisibleChecker(off.Flags);
        }

        private bool TryUiRect(object? el, out Vector2 pos, out Vector2 size)
        {
            pos = Vector2.Zero;
            size = Vector2.Zero;
            var addr = UiAddr(el);
            if (addr == IntPtr.Zero)
            {
                return false;
            }

            if (PluginUiElementReflection.TryGetAbsoluteRect(addr, out pos, out size) &&
                this.InClient(pos, size))
            {
                return true;
            }

            return PluginUiElementReflection.TryGetAbsoluteRect(addr, out pos, out size, requireVisible: false) &&
                   this.InClient(pos, size);
        }

        private bool OverlapsInventory(Vector2 pos, Vector2 size)
        {
            var ui = Core.States.InGameStateObject.GameUi;
            if (ui?.RightPanel.Address == IntPtr.Zero ||
                !PluginUiElementReflection.TryGetAbsoluteRect(ui.RightPanel.Address, out var ip, out var isz))
            {
                return false;
            }

            return pos.X < ip.X + isz.X && pos.X + size.X > ip.X &&
                   pos.Y < ip.Y + isz.Y && pos.Y + size.Y > ip.Y;
        }

        private bool InClient(Vector2 pos, Vector2 size)
        {
            var wa = Core.Process.WindowArea;
            if (wa.Width < 50 || wa.Height < 50)
            {
                return size.X > 8f && size.Y > 8f;
            }

            var cx = pos.X + (size.X * 0.5f);
            var cy = pos.Y + (size.Y * 0.5f);
            return cx >= 0f && cy >= 0f && cx < wa.Width && cy < wa.Height &&
                   size.X > 8f && size.Y > 8f;
        }

        private IntPtr FindItemPtr(IntPtr el, int depth)
        {
            var item = this.ItemPtr(el);
            if (item != IntPtr.Zero &&
                PluginUiElementReflection.TryValidateItemAddress(item, out _, out _))
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

        private IntPtr ItemPtr(IntPtr el)
        {
            var p = this.ReadPtr(el + ItemPtrHint);
            return PluginUiElementReflection.TryValidateItemAddress(p, out _, out _) ? p : IntPtr.Zero;
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

            var methods = this.handle.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? genericRead = null;
            foreach (var m in methods)
            {
                if (m.Name == "ReadMemory" && m.IsGenericMethod && m.GetParameters().Length == 1)
                {
                    genericRead = m;
                }

                if (m.Name == "ReadStdVector" && m.IsGenericMethod)
                {
                    this.readVec = m.MakeGenericMethod(typeof(IntPtr));
                }
            }

            if (genericRead == null || this.readVec == null)
            {
                return false;
            }

            this.readPtr = genericRead.MakeGenericMethod(typeof(IntPtr));
            this.readByte = genericRead.MakeGenericMethod(typeof(byte));
            this.readUi = genericRead.MakeGenericMethod(typeof(UiElementBaseOffset));
            return true;
        }

        private bool ReadIdentified(Item item)
        {
            if (!item.TryGetComponent<Mods>(out var mods) ||
                mods.Address == IntPtr.Zero ||
                this.readByte == null)
            {
                return true;
            }

            return this.ReadByte(mods.Address + IdentifiedOffset) != 0;
        }

        private byte ReadByte(IntPtr addr) =>
            this.readByte!.Invoke(this.handle, new object[] { addr }) is byte b ? b : (byte)0;

        private IntPtr ReadPtr(IntPtr addr) =>
            this.readPtr!.Invoke(this.handle, new object[] { addr }) is IntPtr p ? p : IntPtr.Zero;

        private UiElementBaseOffset ReadUi(IntPtr addr) =>
            this.readUi!.Invoke(this.handle, new object[] { addr }) is UiElementBaseOffset u ? u : default;

        private IntPtr[] ReadVec(StdVector v) =>
            this.readVec!.Invoke(this.handle, new object[] { v }) as IntPtr[] ?? Array.Empty<IntPtr>();

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
    }
}
