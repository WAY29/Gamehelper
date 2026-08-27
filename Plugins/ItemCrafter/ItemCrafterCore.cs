namespace ItemCrafter
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.IO;
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

    public sealed class ItemCrafterCore : PCore<ItemCrafterSettings>
    {
        private const int ItemPtrHint = 0x4F8;
        private const int BaseFlagsOffset = 0xC7;
        private const byte CorruptedBit = 0x01;
        private const int ToggleableOnOffset = 0x18;
        private static readonly FieldInfo? ComponentAddressesField =
            typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly int[][] StashTabsPaths =
        {
            new[] { 2, 0, 0, 0, 1, 1 },
            new[] { 2, 0, 0, 0, 0, 1, 1 },
        };
        private static readonly int[] InventoryPath = { 5, 36 };

        private object? handle;
        private MethodInfo? readPtr;
        private MethodInfo? readByte;
        private MethodInfo? readUi;
        private MethodInfo? readVec;

        private bool running;
        private int stepIndex = -1;
        private readonly List<Act> pending = new();
        private int pendingIndex;
        private long nextAtMs;
        private bool shiftDown;
        private Native.Point lastClick;
        private bool hasLastClick;
        private Vector2? lastCurrencyOverlay;
        private string status = "idle";
        private readonly List<Slot> highlights = new();
        private readonly List<Slot> stashSlots = new();
        private readonly List<Slot> invSlots = new();

        private Item? lastHovered;
        private Slot? lastHoveredSlot;
        private bool freezeHover;
        private int dragStep = -1;
        private byte[]? cleanBase;
        private byte[]? cleanMods;
        private byte[]? dirtyBase;
        private byte[]? dirtyMods;
        private byte[]? omenOffBase;
        private byte[]? omenOnBase;
        private byte[]? omenOffEl;
        private byte[]? omenOnEl;
        private string[]? omenOffBuffs;
        private string[]? omenOnBuffs;
        private Dictionary<string, byte[]> omenOffComps = new();
        private Dictionary<string, byte[]> omenOnComps = new();
        private string dumpNote = string.Empty;
        private readonly List<string> log = new();

        private string SettingPath => Path.Join(this.DllDirectory, "config", "settings.txt");

        private enum ActKind { Move, Left, Right, ShiftOn, ShiftOff }

        private readonly record struct Act(ActKind Kind, Vector2 Pos);

        private sealed class Slot
        {
            public required Item Item;
            public required Vector2 Pos;
            public required Vector2 Size;
            public required string Path;
            public required string InternalName;
            public string DisplayName = string.Empty;
            public Rarity Rarity;
            public int ExplicitCount;
            public int Stack = 1;
            public bool Corrupted;
            public bool OmenOn;
            public IntPtr El;
        }

        public override void OnEnable(bool isGameOpened)
        {
            Catalog.SelfCheck();
            if (File.Exists(this.SettingPath))
            {
                try
                {
                    this.Settings = JsonConvert.DeserializeObject<ItemCrafterSettings>(
                        File.ReadAllText(this.SettingPath),
                        new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace })
                        ?? new ItemCrafterSettings();
                }
                catch
                {
                    this.Settings = new ItemCrafterSettings();
                }
            }

            this.EnsureRecipes();
        }

        private void EnsureRecipes()
        {
            var seen = new HashSet<string>();
            this.Settings.Recipes.RemoveAll(r => string.IsNullOrEmpty(r.Name) || !seen.Add(r.Name));
            if (this.Settings.Recipes.Count == 0)
            {
                this.Settings.Recipes.Add(new CraftRecipe
                {
                    Name = "点金后崇高到6词",
                    Steps =
                    {
                        new CraftStep { InternalName = Catalog.Alchemy },
                        new CraftStep { InternalName = Catalog.Exalted, UntilAffixes = 6 },
                    },
                });
            }

            this.Settings.SelectedRecipe = Math.Clamp(this.Settings.SelectedRecipe, 0, this.Settings.Recipes.Count - 1);
        }

        public override void OnDisable()
        {
            this.Stop("插件关闭");
            this.SaveSettings();
        }

        public override void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPath) ?? string.Empty);
            File.WriteAllText(this.SettingPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
        }

        public override void DrawSettings()
        {
            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.toggle", "Toggle key"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGuiHelper.NonContinuousEnumComboBox("##ICToggle", ref this.Settings.ToggleKey);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.delay", "Click delay (ms)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##ICDelay", ref this.Settings.ClickDelayMs, 50, 1000);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.abort_px", "Mouse abort (px)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##ICAbortPx", ref this.Settings.MouseAbortPx, 5, 80);

            ImGui.Checkbox(this.PluginText.Label("settings.debug", "Show debug inspector", "ICDebug"), ref this.Settings.ShowDebugWindow);
            ImGui.SameLine();
            ImGui.Checkbox(this.PluginText.Label("settings.log", "Show action log", "ICLog"), ref this.Settings.ShowLogWindow);

            ImGui.Separator();
            if (this.Settings.Recipes.Count == 0)
            {
                this.Settings.Recipes.Add(new CraftRecipe { Name = this.PluginText.T("settings.new_recipe", "新配方") });
            }

            this.Settings.SelectedRecipe = Math.Clamp(this.Settings.SelectedRecipe, 0, this.Settings.Recipes.Count - 1);
            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.recipe", "Recipe"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220);
            var names = this.Settings.Recipes.ConvertAll(r => r.Name);
            var sel = this.Settings.SelectedRecipe;
            if (ImGui.Combo("##ICRecipe", ref sel, names.ToArray(), names.Count))
            {
                this.Settings.SelectedRecipe = sel;
            }

            ImGui.SameLine();
            if (this.IconButton("##addRecipe", DrawPlusIcon))
            {
                this.Settings.Recipes.Add(new CraftRecipe { Name = this.PluginText.T("settings.new_recipe", "新配方") });
                this.Settings.SelectedRecipe = this.Settings.Recipes.Count - 1;
            }

            ImGui.SameLine();
            if (this.IconButton("##delRecipe", DrawXIcon) && this.Settings.Recipes.Count > 1)
            {
                this.Settings.Recipes.RemoveAt(this.Settings.SelectedRecipe);
                this.Settings.SelectedRecipe = Math.Clamp(this.Settings.SelectedRecipe, 0, this.Settings.Recipes.Count - 1);
            }

            var recipe = this.Settings.Recipes[this.Settings.SelectedRecipe];
            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.recipe_name", "Name"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(280);
            ImGui.InputText("##ICName", ref recipe.Name, 64);

            ImGui.Spacing();
            ImGui.Text(this.PluginText.T("settings.steps", "Steps"));

            for (var i = 0; i < recipe.Steps.Count; i++)
            {
                var step = recipe.Steps[i];
                ImGui.PushID(i);
                this.IconButton("##grip", DrawGripIcon);
                if (ImGui.BeginDragDropSource())
                {
                    this.dragStep = i;
                    ImGui.SetDragDropPayload("ICStep", IntPtr.Zero, 0);
                    ImGui.Text(this.PluginText.T($"item.{step.InternalName}", step.InternalName));
                    ImGui.EndDragDropSource();
                }

                this.AcceptStepDrop(recipe.Steps, i);

                ImGui.SameLine();
                ImGui.SetNextItemWidth(220);
                var cur = IndexOf(step.InternalName);
                if (ImGui.Combo("##item", ref cur, CatalogLabels(), Catalog.All.Length))
                {
                    step.InternalName = Catalog.All[cur].InternalName;
                }

                this.AcceptStepDrop(recipe.Steps, i);

                if (Catalog.TryGet(step.InternalName, out var info) && info.Kind == StepKind.Exalt)
                {
                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(this.PluginText.T("settings.until", "Until"));
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(56);
                    ImGui.InputInt("##until", ref step.UntilAffixes, 0);
                    if (!ImGui.IsItemActive())
                    {
                        step.UntilAffixes = Catalog.ClampUntil(step.UntilAffixes);
                    }

                    ImGui.SameLine();
                    ImGui.AlignTextToFramePadding();
                    ImGui.Text(this.PluginText.T("settings.affixes", "mods"));
                }

                ImGui.SameLine();
                if (this.IconButton("##delStep", DrawXIcon))
                {
                    recipe.Steps.RemoveAt(i);
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }

            if (ImGui.Button(this.PluginText.T("settings.add_step", "Add step")))
            {
                recipe.Steps.Add(new CraftStep());
            }

            ImGui.Separator();
            ImGui.TextWrapped(this.running
                ? this.PluginText.F("settings.running", "Running: {0}", this.status)
                : this.PluginText.T("settings.idle", "Idle. Open stash + inventory, press toggle."));
        }

        private void AcceptStepDrop(List<CraftStep> steps, int i)
        {
            if (!ImGui.BeginDragDropTarget())
            {
                return;
            }

            ImGui.AcceptDragDropPayload("ICStep");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && this.dragStep >= 0 && this.dragStep != i)
            {
                Move(steps, this.dragStep, i);
                this.dragStep = -1;
            }

            ImGui.EndDragDropTarget();
        }

        private static void Move<T>(List<T> list, int from, int to)
        {
            if ((uint)from >= (uint)list.Count || (uint)to >= (uint)list.Count || from == to)
            {
                return;
            }

            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
        }

        private bool IconButton(string id, Action<ImDrawListPtr, Vector2, Vector2, uint> draw)
        {
            var size = ImGui.GetFrameHeight();
            var pressed = ImGui.Button(id, new Vector2(size, size));
            draw(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.Text));
            return pressed;
        }

        private static void DrawPlusIcon(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col)
        {
            var c = (min + max) * 0.5f;
            var r = (max.X - min.X) * 0.22f;
            dl.AddLine(new Vector2(c.X - r, c.Y), new Vector2(c.X + r, c.Y), col, 2f);
            dl.AddLine(new Vector2(c.X, c.Y - r), new Vector2(c.X, c.Y + r), col, 2f);
        }

        private static void DrawXIcon(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col)
        {
            var pad = (max.X - min.X) * 0.28f;
            dl.AddLine(min + new Vector2(pad, pad), max - new Vector2(pad, pad), col, 2f);
            dl.AddLine(new Vector2(max.X - pad, min.Y + pad), new Vector2(min.X + pad, max.Y - pad), col, 2f);
        }

        private static void DrawGripIcon(ImDrawListPtr dl, Vector2 min, Vector2 max, uint col)
        {
            var c = (min + max) * 0.5f;
            var w = (max.X - min.X) * 0.22f;
            for (var i = -1; i <= 1; i++)
            {
                var y = c.Y + (i * 4f);
                dl.AddLine(new Vector2(c.X - w, y), new Vector2(c.X + w, y), col, 1.5f);
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

            if (Utils.IsKeyPressedAndNotTimeout(this.Settings.ToggleKey, 300))
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

            try
            {
                if (!this.running && this.Settings.ShowDebugWindow)
                {
                    this.ScanPanels();
                }

                if (this.running)
                {
                    this.Tick();
                    this.DrawHighlights();
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
                Console.WriteLine($"[ItemCrafter] DrawUI {ex.Message}");
                if (this.running)
                {
                    this.Stop("异常");
                }
            }
        }

        private void DrawDebugWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(420, 520), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin(this.PluginText.Title("window.debug", "ItemCrafter Debug", "ICDbg"), ref this.Settings.ShowDebugWindow))
            {
                ImGui.End();
                return;
            }

            ImGui.Checkbox("Freeze hovered", ref this.freezeHover);
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                this.lastHovered = null;
                this.lastHoveredSlot = null;
                this.dumpNote = string.Empty;
            }

            ImGui.Text($"Stash slots: {this.stashSlots.Count}  Inv slots: {this.invSlots.Count}");
            this.DrawPlayerBuffs();
            if (!string.IsNullOrEmpty(this.dumpNote))
            {
                ImGui.TextWrapped(this.dumpNote);
            }

            var item = this.lastHovered;
            var slot = this.lastHoveredSlot;
            if (item == null)
            {
                ImGui.Text("悬停仓库/背包物品。");
            }
            else
            {
            ImGui.Separator();
            if (slot != null)
            {
                ImGuiHelper.DisplayTextAndCopyOnClick($"Path: {slot.Path}", slot.Path);
                ImGuiHelper.DisplayTextAndCopyOnClick($"Internal: {slot.InternalName}", slot.InternalName);
            }
            else
            {
                ImGuiHelper.DisplayTextAndCopyOnClick($"Path: {item.Path}", item.Path ?? string.Empty);
            }

            ImGuiHelper.IntPtrToImGui("Entity", item.Address);
            item.TryGetComponent<Base>(out var baseComp);
            item.TryGetComponent<Mods>(out var mods);
            if (baseComp != null)
            {
                ImGuiHelper.DisplayTextAndCopyOnClick($"Base: {baseComp.BaseItemName}", baseComp.BaseItemName ?? string.Empty);
                ImGuiHelper.DisplayTextAndCopyOnClick($"Base.Internal: {baseComp.InternalName}", baseComp.InternalName ?? string.Empty);
                var corrupted = this.IsCorrupted(baseComp);
                ImGui.TextColored(
                    corrupted ? new Vector4(1f, 0.35f, 0.2f, 1f) : new Vector4(0.35f, 0.9f, 0.35f, 1f),
                    corrupted ? "已腐化" : "未腐化");
                if (baseComp.Address != IntPtr.Zero && this.EnsureMem())
                {
                    ImGui.SameLine();
                    ImGui.Text($"(Base+0xC7={this.ReadByte(baseComp.Address + BaseFlagsOffset):X2})");
                }
            }

            if (this.TryGetCompAddr(item, "Toggleable", out var toggleAddr))
            {
                var omenOn = this.IsOmenOn(item);
                ImGui.TextColored(
                    omenOn ? new Vector4(1f, 0.55f, 0.2f, 1f) : new Vector4(0.35f, 0.9f, 0.35f, 1f),
                    omenOn ? "预兆已启用" : "预兆未启用");
                if (toggleAddr != IntPtr.Zero && this.EnsureMem())
                {
                    ImGui.SameLine();
                    ImGui.Text($"(Toggleable+0x18={this.ReadByte(toggleAddr + ToggleableOnOffset):X2})");
                }
            }

            if (mods != null)
            {
                ImGui.Text($"Rarity: {mods.Rarity}  Explicit: {mods.ExplicitMods.Count}");
                if (mods.ExplicitMods.Count > 0 && ImGui.TreeNode("Explicit Mods"))
                {
                    foreach (var m in mods.ExplicitMods)
                    {
                        ImGui.Text(m.name);
                    }

                    ImGui.TreePop();
                }
            }

            if (ImGui.Button("Mark clean"))
            {
                this.cleanBase = this.CopyBytes(baseComp?.Address ?? IntPtr.Zero, 0x100);
                this.cleanMods = this.CopyBytes(mods?.Address ?? IntPtr.Zero, 0x100);
            }

            ImGui.SameLine();
            if (ImGui.Button("Mark corrupted"))
            {
                this.dirtyBase = this.CopyBytes(baseComp?.Address ?? IntPtr.Zero, 0x100);
                this.dirtyMods = this.CopyBytes(mods?.Address ?? IntPtr.Zero, 0x100);
            }

            ImGui.SameLine();
            if (ImGui.Button("Dump memory to file"))
            {
                this.dumpNote = this.DumpItemFile(item);
            }

            this.DrawDiff("Base", this.cleanBase, this.dirtyBase, "clean", "corrupt", false);
            this.DrawDiff("Mods", this.cleanMods, this.dirtyMods, "clean", "corrupt", false);

            if (this.lastHoveredSlot is { El: var el } && el != IntPtr.Zero && this.EnsureMem())
            {
                ImGui.Text($"UI Flags=0x{this.ReadU32(el + 0x180):X8}");
            }

            if (ImGui.Button("Mark omen off"))
            {
                this.CaptureOmen(false, baseComp?.Address ?? IntPtr.Zero, item);
            }

            ImGui.SameLine();
            if (ImGui.Button("Mark omen on"))
            {
                this.CaptureOmen(true, baseComp?.Address ?? IntPtr.Zero, item);
            }

            this.DrawDiff("Omen Base", this.omenOffBase, this.omenOnBase, "off", "on", true);
            this.DrawDiff("Omen UI", this.omenOffEl, this.omenOnEl, "off", "on", true);
            this.DrawOmenCompDiff();
            this.DrawBuffDiff();

            var field = typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(item) is System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> dict &&
                ImGui.TreeNode("Components"))
            {
                foreach (var kv in dict)
                {
                    ImGuiHelper.IntPtrToImGui(kv.Key, kv.Value);
                }

                ImGui.TreePop();
            }
            }

            ImGui.Separator();
            ImGui.Text("背包:");
            foreach (var s in this.invSlots)
            {
                ImGuiHelper.DisplayTextAndCopyOnClick(
                    $"{s.DisplayName}  {s.InternalName}",
                    $"{s.InternalName}\n{s.Path}");
            }

            ImGui.End();
        }

        private void Start()
        {
            this.running = true;
            this.stepIndex = -1;
            this.pending.Clear();
            this.pendingIndex = 0;
            this.hasLastClick = false;
            this.lastCurrencyOverlay = null;
            this.status = "start";
            this.nextAtMs = 0;
            this.log.Clear();
            this.ScanPanels();
            var recipes = this.Settings.Recipes;
            var name = recipes.Count == 0
                ? "?"
                : recipes[Math.Clamp(this.Settings.SelectedRecipe, 0, recipes.Count - 1)].Name;
            this.Log($"开始 配方={name}");
        }

        private void Stop(string reason)
        {
            this.Log($"停止: {reason}");
            if (this.shiftDown)
            {
                Native.Shift(false);
                this.shiftDown = false;
                this.Log("Shift 松开");
            }

            if (this.running && this.lastCurrencyOverlay is { } pos)
            {
                this.MoveTo(pos);
                Native.RightClick();
                this.Log($"右键 → {this.Describe(pos)}  Shift=关");
            }

            this.running = false;
            this.pending.Clear();
            this.pendingIndex = 0;
            this.stepIndex = -1;
            this.hasLastClick = false;
            this.status = reason;
        }

        private void Tick()
        {
            if (!Core.Process.Foreground)
            {
                this.Stop("窗口失焦");
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || !ui.LeftPanel.IsVisible || !ui.RightPanel.IsVisible)
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
                if (!this.LoadNextStep())
                {
                    this.Stop("完成");
                }

                this.nextAtMs = Environment.TickCount64 + Math.Max(50, this.Settings.ClickDelayMs);
                return;
            }

            var act = this.pending[this.pendingIndex++];
            switch (act.Kind)
            {
                case ActKind.ShiftOn:
                    Native.Shift(true);
                    this.shiftDown = true;
                    this.Log("Shift 按下");
                    break;
                case ActKind.ShiftOff:
                    Native.Shift(false);
                    this.shiftDown = false;
                    this.Log("Shift 松开");
                    break;
                case ActKind.Move:
                    this.MoveTo(act.Pos);
                    break;
                case ActKind.Left:
                    Native.LeftClick();
                    this.Log($"左键 → {this.Describe(act.Pos)}  Shift={(this.shiftDown ? "开" : "关")}");
                    break;
                case ActKind.Right:
                    Native.RightClick();
                    this.Log($"右键 → {this.Describe(act.Pos)}  Shift={(this.shiftDown ? "开" : "关")}");
                    break;
            }

            this.nextAtMs = Environment.TickCount64 + Math.Max(50, this.Settings.ClickDelayMs);
        }

        private bool LoadNextStep()
        {
            var recipes = this.Settings.Recipes;
            if (recipes.Count == 0)
            {
                return false;
            }

            var recipe = recipes[Math.Clamp(this.Settings.SelectedRecipe, 0, recipes.Count - 1)];
            this.stepIndex++;
            while (this.stepIndex < recipe.Steps.Count)
            {
                this.ScanStash();
                this.ScanInv();
                var step = recipe.Steps[this.stepIndex];
                if (!Catalog.TryGet(step.InternalName, out var info))
                {
                    this.stepIndex++;
                    continue;
                }

                this.pending.Clear();
                this.pendingIndex = 0;
                this.highlights.Clear();

                if (info.Kind == StepKind.Omen)
                {
                    var omens = this.FindAllCurrency(info.InternalName);
                    if (omens.Count == 0)
                    {
                        this.Stop("通货用完");
                        return true;
                    }

                    var toClick = omens.FindAll(o => !o.OmenOn);
                    if (toClick.Count == 0)
                    {
                        this.Log($"步骤 {this.stepIndex + 1}: {this.PluginText.T($"item.{info.InternalName}", info.English)} 已启用，跳过");
                        this.stepIndex++;
                        continue;
                    }

                    toClick.Sort(GridOrder);
                    foreach (var omen in toClick)
                    {
                        var pos = Center(omen);
                        this.pending.Add(new Act(ActKind.Move, pos));
                        this.pending.Add(new Act(ActKind.Right, pos));
                        this.highlights.Add(omen);
                    }

                    this.Log($"步骤 {this.stepIndex + 1}: {this.PluginText.T($"item.{info.InternalName}", info.English)} 预兆 x{toClick.Count}");
                    this.status = info.English;
                    return true;
                }

                var currency = this.FindCurrency(info.InternalName);
                if (currency == null)
                {
                    this.Stop("通货用完");
                    return true;
                }

                var targets = new List<Slot>();
                foreach (var stone in this.stashSlots)
                {
                    if (!Catalog.IsWaystone(stone.Path, stone.InternalName))
                    {
                        continue;
                    }

                    if (!Catalog.IsEligible(info.Kind, stone.Rarity, stone.ExplicitCount, stone.Corrupted, step.UntilAffixes))
                    {
                        continue;
                    }

                    targets.Add(stone);
                }

                targets.Sort(GridOrder);
                if (targets.Count == 0)
                {
                    this.Log($"步骤 {this.stepIndex + 1}: {this.PluginText.T($"item.{info.InternalName}", info.English)} 无目标，跳过");
                    this.stepIndex++;
                    continue;
                }

                this.lastCurrencyOverlay = Center(currency);
                this.pending.Add(new Act(ActKind.Move, Center(currency)));
                this.pending.Add(new Act(ActKind.Right, Center(currency)));
                this.pending.Add(new Act(ActKind.ShiftOn, default));
                foreach (var t in targets)
                {
                    var clicks = info.Kind == StepKind.Exalt
                        ? Catalog.ExaltClicks(t.ExplicitCount, step.UntilAffixes)
                        : 1;
                    var pos = Center(t);
                    this.pending.Add(new Act(ActKind.Move, pos));
                    for (var i = 0; i < clicks; i++)
                    {
                        this.pending.Add(new Act(ActKind.Left, pos));
                    }

                    this.highlights.Add(t);
                }

                this.pending.Add(new Act(ActKind.ShiftOff, default));

                this.Log($"步骤 {this.stepIndex + 1}: {this.PluginText.T($"item.{info.InternalName}", info.English)} 目标 {targets.Count}");
                this.status = info.English;
                return true;
            }

            return false;
        }

        private void MoveTo(Vector2 overlay)
        {
            this.Log($"移动 → {this.Describe(overlay)}");
            var wa = Core.Process.WindowArea;
            var x = wa.X + (int)overlay.X;
            var y = wa.Y + (int)overlay.Y;
            Native.MoveTo(x, y);
            this.lastClick = new Native.Point { X = x, Y = y };
            this.hasLastClick = true;
        }

        private void DrawLogWindow()
        {
            ImGui.SetNextWindowSize(new Vector2(480, 280), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("ItemCrafter 日志", ref this.Settings.ShowLogWindow))
            {
                ImGui.End();
                return;
            }

            ImGui.Text($"状态: {this.status}  Shift: {(this.shiftDown ? "开" : "关")}  行数: {this.log.Count}");
            ImGui.BeginChild("ICLogBody", new Vector2(0, 0), ImGuiChildFlags.Borders);
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

        private void Log(string line)
        {
            if (this.log.Count > 200)
            {
                this.log.RemoveRange(0, this.log.Count - 150);
            }

            this.log.Add($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        }

        private string Describe(Vector2 overlay)
        {
            foreach (var s in this.stashSlots)
            {
                if (Contains(s, overlay))
                {
                    return $"仓库 {s.DisplayName} {s.Rarity} {s.ExplicitCount}词{(s.Corrupted ? " 已腐化" : string.Empty)}";
                }
            }

            foreach (var s in this.invSlots)
            {
                if (Contains(s, overlay))
                {
                    return $"背包 {s.DisplayName} x{s.Stack}";
                }
            }

            return $"({overlay.X:0},{overlay.Y:0})";
        }

        private static int GridOrder(Slot a, Slot b)
        {
            var dy = ((int)a.Pos.Y).CompareTo((int)b.Pos.Y);
            return dy != 0 ? dy : ((int)a.Pos.X).CompareTo((int)b.Pos.X);
        }

        private static bool Contains(Slot s, Vector2 p) =>
            p.X >= s.Pos.X && p.X <= s.Pos.X + s.Size.X &&
            p.Y >= s.Pos.Y && p.Y <= s.Pos.Y + s.Size.Y;

        private void DrawHighlights()
        {
            var dl = ImGui.GetForegroundDrawList();
            foreach (var s in this.highlights)
            {
                dl.AddRect(s.Pos, s.Pos + s.Size, 0xFF00FF00, 0f, ImDrawFlags.None, 2f);
            }
        }

        private void ScanPanels()
        {
            this.ScanStash();
            this.ScanInv();
        }

        private void ScanStash()
        {
            this.stashSlots.Clear();
            if (!this.EnsureMem())
            {
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || !ui.LeftPanel.IsVisible)
            {
                return;
            }

            foreach (var path in StashTabsPaths)
            {
                var tabs = this.ResolvePath(ui.LeftPanel.Address, path);
                if (tabs == IntPtr.Zero)
                {
                    continue;
                }

                this.ProcessStashTabs(tabs);
                if (this.stashSlots.Count > 0)
                {
                    break;
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
            if (grid != IntPtr.Zero)
            {
                this.ProcessGrid(grid, this.invSlots);
            }
        }

        private void ProcessStashTabs(IntPtr stashTabsContainer)
        {
            var tabs = this.ReadVec(this.ReadUi(stashTabsContainer).ChildrensPtr);
            IntPtr active = IntPtr.Zero;
            foreach (var tab in tabs)
            {
                if (tab != IntPtr.Zero && this.IsVisible(tab))
                {
                    active = tab;
                    break;
                }
            }

            if (active == IntPtr.Zero)
            {
                return;
            }

            var waystoneRoot = this.ResolvePath(active, new[] { 0, 1 });
            if (waystoneRoot != IntPtr.Zero)
            {
                var kids = this.ReadVec(this.ReadUi(waystoneRoot).ChildrensPtr);
                if (kids.Length == 16)
                {
                    this.ProcessWaystoneTab(kids);
                    return;
                }
            }

            var normal = this.ResolvePath(active, new[] { 0, 0 });
            if (normal != IntPtr.Zero)
            {
                this.ProcessGrid(normal, this.stashSlots);
            }
        }

        private void ProcessWaystoneTab(IntPtr[] tiers)
        {
            foreach (var tier in tiers)
            {
                if (tier == IntPtr.Zero || !this.IsVisible(tier))
                {
                    continue;
                }

                var t0 = this.ReadVec(this.ReadUi(tier).ChildrensPtr);
                if (t0.Length == 0 || t0[0] == IntPtr.Zero)
                {
                    continue;
                }

                var c0 = this.ReadVec(this.ReadUi(t0[0]).ChildrensPtr);
                if (c0.Length <= 1 || c0[1] == IntPtr.Zero)
                {
                    continue;
                }

                var pages = this.ReadVec(this.ReadUi(c0[1]).ChildrensPtr);
                foreach (var page in pages)
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

                    this.ProcessGrid(pageKids[0], this.stashSlots);
                }
            }
        }

        private void ProcessGrid(IntPtr gridRoot, List<Slot> dest)
        {
            if (gridRoot == IntPtr.Zero)
            {
                return;
            }

            var slots = this.ReadVec(this.ReadUi(gridRoot).ChildrensPtr);
            var mouse = ImGui.GetMousePos();
            foreach (var slot in slots)
            {
                if (slot == IntPtr.Zero || !this.IsVisible(slot))
                {
                    continue;
                }

                var itemAddr = this.ItemPtr(slot);
                if (itemAddr == IntPtr.Zero ||
                    !PluginUiElementReflection.TryValidateItemAddress(itemAddr, out var path, out _))
                {
                    continue;
                }

                var item = ReadItem(itemAddr);
                if (item == null)
                {
                    continue;
                }

                if (!PluginUiElementReflection.TryGetAbsoluteRect(slot, out var pos, out var size))
                {
                    continue;
                }

                var made = this.ToSlot(item, path, pos, size, slot);
                dest.Add(made);
                if (!this.freezeHover &&
                    mouse.X >= pos.X && mouse.X <= pos.X + size.X &&
                    mouse.Y >= pos.Y && mouse.Y <= pos.Y + size.Y)
                {
                    this.lastHovered = item;
                    this.lastHoveredSlot = made;
                }
            }
        }

        private Slot ToSlot(Item item, string path, Vector2 pos, Vector2 size, IntPtr el)
        {
            var internalName = item.TryGetComponent<Base>(out var b) ? b.InternalName : string.Empty;
            if (string.IsNullOrEmpty(internalName))
            {
                var slash = path.LastIndexOf('/');
                internalName = slash >= 0 ? path[(slash + 1)..] : path;
            }

            var rarity = Rarity.Normal;
            var explicitCount = 0;
            if (item.TryGetComponent<Mods>(out var mods, shouldCache: false))
            {
                rarity = mods.Rarity;
                explicitCount = mods.ExplicitMods.Count;
            }

            var stack = item.TryGetComponent<Stack>(out var st) ? Math.Max(1, st.Count) : 1;
            return new Slot
            {
                Item = item,
                Pos = pos,
                Size = size,
                Path = path,
                InternalName = internalName,
                DisplayName = b?.BaseItemName ?? internalName,
                Rarity = rarity,
                ExplicitCount = explicitCount,
                Stack = stack,
                Corrupted = this.IsCorrupted(b),
                OmenOn = this.IsOmenOn(item),
                El = el,
            };
        }

        private bool IsCorrupted(Base? baseComp)
        {
            if (baseComp == null || baseComp.Address == IntPtr.Zero || !this.EnsureMem())
            {
                return false;
            }

            return (this.ReadByte(baseComp.Address + BaseFlagsOffset) & CorruptedBit) != 0;
        }

        private bool TryGetCompAddr(Item item, string name, out IntPtr addr)
        {
            addr = IntPtr.Zero;
            if (ComponentAddressesField?.GetValue(item) is not ConcurrentDictionary<string, IntPtr> dict)
            {
                return false;
            }

            return dict.TryGetValue(name, out addr) && addr != IntPtr.Zero;
        }

        private bool IsOmenOn(Item item)
        {
            return this.TryGetCompAddr(item, "Toggleable", out var addr)
                && this.EnsureMem()
                && (this.ReadByte(addr + ToggleableOnOffset) & 1) != 0;
        }

        private IntPtr ItemPtr(IntPtr el)
        {
            var p = this.ReadPtr(el + ItemPtrHint);
            if (PluginUiElementReflection.TryValidateItemAddress(p, out _, out _))
            {
                return p;
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
                if ((off.Self != IntPtr.Zero && off.Self != cur) ||
                    !UiElementBaseFuncs.IsVisibleChecker(off.Flags))
                {
                    return false;
                }

                cur = off.ParentPtr;
            }

            return true;
        }

        private byte[]? CopyBytes(IntPtr addr, int len)
        {
            if (addr == IntPtr.Zero || !this.EnsureMem())
            {
                return null;
            }

            var buf = new byte[len];
            for (var i = 0; i < len; i++)
            {
                buf[i] = this.ReadByte(addr + i);
            }

            return buf;
        }

        private void DrawDiff(string label, byte[]? a, byte[]? b, string aName, string bName, bool skipPtrs)
        {
            if (a == null || b == null)
            {
                return;
            }

            var n = Math.Min(a.Length, b.Length);
            var hits = 0;
            ImGui.Text($"{label} diffs:");
            for (var i = 0; i < n; i++)
            {
                if (skipPtrs && (i & 7) == 0 && LooksPtr(a, i) && LooksPtr(b, i))
                {
                    i += 7;
                    continue;
                }

                if (a[i] == b[i])
                {
                    continue;
                }

                ImGui.Text($"  +0x{i:X2}: {aName} {a[i]:X2}  {bName} {b[i]:X2}");
                hits++;
                if (hits >= 24)
                {
                    ImGui.Text("  ...");
                    break;
                }
            }

            if (hits == 0)
            {
                ImGui.Text("  (none)");
            }
        }

        private static bool LooksPtr(byte[] buf, int i)
        {
            if (i + 8 > buf.Length)
            {
                return false;
            }

            var v = BitConverter.ToUInt64(buf, i);
            return v >= 0x10000 && v < 0x00007FFFFFFFFFFFUL;
        }

        private void CaptureOmen(bool on, IntPtr baseAddr, Item item)
        {
            var b = this.CopyBytes(baseAddr, 0x200);
            var el = this.CopyBytes(this.lastHoveredSlot?.El ?? IntPtr.Zero, 0x200);
            var buffs = this.SnapshotBuffs();
            var comps = this.CopyComponents(item);
            if (on)
            {
                this.omenOnBase = b;
                this.omenOnEl = el;
                this.omenOnBuffs = buffs;
                this.omenOnComps = comps;
            }
            else
            {
                this.omenOffBase = b;
                this.omenOffEl = el;
                this.omenOffBuffs = buffs;
                this.omenOffComps = comps;
            }
        }

        private Dictionary<string, byte[]> CopyComponents(Item item)
        {
            var result = new Dictionary<string, byte[]>();
            var field = typeof(Entity).GetField("componentAddresses", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.GetValue(item) is not System.Collections.Concurrent.ConcurrentDictionary<string, IntPtr> dict)
            {
                return result;
            }

            foreach (var kv in dict)
            {
                var bytes = this.CopyBytes(kv.Value, 0x80);
                if (bytes != null)
                {
                    result[kv.Key] = bytes;
                }
            }

            return result;
        }

        private void DrawOmenCompDiff()
        {
            if (this.omenOffComps.Count == 0 || this.omenOnComps.Count == 0)
            {
                return;
            }

            foreach (var kv in this.omenOffComps)
            {
                if (!this.omenOnComps.TryGetValue(kv.Key, out var on))
                {
                    ImGui.Text($"comp {kv.Key}: missing when on");
                    continue;
                }

                this.DrawDiff("comp " + kv.Key, kv.Value, on, "off", "on", true);
            }

            foreach (var kv in this.omenOnComps)
            {
                if (!this.omenOffComps.ContainsKey(kv.Key))
                {
                    ImGui.Text($"comp {kv.Key}: only when on");
                }
            }
        }

        private uint ReadU32(IntPtr addr)
        {
            if (addr == IntPtr.Zero || !this.EnsureMem())
            {
                return 0;
            }

            return this.ReadByte(addr)
                | ((uint)this.ReadByte(addr + 1) << 8)
                | ((uint)this.ReadByte(addr + 2) << 16)
                | ((uint)this.ReadByte(addr + 3) << 24);
        }

        private void DrawPlayerBuffs()
        {
            var player = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
            if (player == null || !player.TryGetComponent<Buffs>(out var buffs, shouldCache: false))
            {
                ImGui.Text("Player buffs: (none)");
                return;
            }

            if (!ImGui.TreeNodeEx($"Player buffs ({buffs.StatusEffects.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                return;
            }

            foreach (var kv in buffs.StatusEffects)
            {
                ImGuiHelper.DisplayTextAndCopyOnClick(kv.Key, kv.Key);
            }

            ImGui.TreePop();
        }

        private string[] SnapshotBuffs()
        {
            var player = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
            if (player == null || !player.TryGetComponent<Buffs>(out var buffs, shouldCache: false))
            {
                return Array.Empty<string>();
            }

            var keys = new string[buffs.StatusEffects.Count];
            var i = 0;
            foreach (var k in buffs.StatusEffects.Keys)
            {
                keys[i++] = k;
            }

            Array.Sort(keys, StringComparer.Ordinal);
            return keys;
        }

        private void DrawBuffDiff()
        {
            if (this.omenOffBuffs == null || this.omenOnBuffs == null)
            {
                return;
            }

            var off = new HashSet<string>(this.omenOffBuffs);
            ImGui.Text("Buffs added:");
            var n = 0;
            foreach (var k in this.omenOnBuffs)
            {
                if (off.Contains(k))
                {
                    continue;
                }

                ImGuiHelper.DisplayTextAndCopyOnClick("  " + k, k);
                n++;
            }

            if (n == 0)
            {
                ImGui.Text("  (none)");
            }

            var on = new HashSet<string>(this.omenOnBuffs);
            ImGui.Text("Buffs removed:");
            n = 0;
            foreach (var k in this.omenOffBuffs)
            {
                if (on.Contains(k))
                {
                    continue;
                }

                ImGuiHelper.DisplayTextAndCopyOnClick("  " + k, k);
                n++;
            }

            if (n == 0)
            {
                ImGui.Text("  (none)");
            }
        }

        private string DumpItemFile(Item item)
        {
            try
            {
                var dir = Path.Join(this.DllDirectory, "config");
                Directory.CreateDirectory(dir);
                var path = Path.Join(dir, "item_memory_dump.txt");
                var lines = new List<string>
                {
                    $"=== {DateTime.Now} ===",
                    $"Path: {item.Path}",
                    $"Entity: 0x{item.Address.ToInt64():X}",
                };

                item.TryGetComponent<Base>(out var b);
                item.TryGetComponent<Mods>(out var m);
                if (b != null)
                {
                    lines.Add($"Base {b.BaseItemName} / {b.InternalName} @ 0x{b.Address.ToInt64():X}");
                    lines.Add(this.FormatHex("Base", b.Address, 0x100));
                }

                if (m != null)
                {
                    lines.Add($"Mods rarity={m.Rarity} explicit={m.ExplicitMods.Count} @ 0x{m.Address.ToInt64():X}");
                    foreach (var mod in m.ExplicitMods)
                    {
                        lines.Add($"  {mod.name}");
                    }

                    lines.Add(this.FormatHex("Mods", m.Address, 0x100));
                }

                File.WriteAllLines(path, lines);
                return $"Wrote {path}";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private string FormatHex(string label, IntPtr addr, int len)
        {
            if (addr == IntPtr.Zero || !this.EnsureMem())
            {
                return $"{label}: (none)";
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{label} @ {addr.ToInt64():X}");
            for (var i = 0; i < len; i += 16)
            {
                sb.Append($"+{i:X2} ");
                for (var j = 0; j < 16 && i + j < len; j++)
                {
                    sb.Append($"{this.ReadByte(addr + i + j):X2} ");
                }

                sb.AppendLine();
            }

            return sb.ToString();
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

        private IntPtr ReadPtr(IntPtr addr) =>
            this.readPtr!.Invoke(this.handle, new object[] { addr }) is IntPtr p ? p : IntPtr.Zero;

        private byte ReadByte(IntPtr addr) =>
            this.readByte!.Invoke(this.handle, new object[] { addr }) is byte b ? b : (byte)0;

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

        private static Vector2 Center(Slot s) => s.Pos + (s.Size * 0.5f);

        private Slot? FindCurrency(string id)
        {
            var all = this.FindAllCurrency(id);
            return all.Count > 0 ? all[0] : null;
        }

        private List<Slot> FindAllCurrency(string id)
        {
            var list = new List<Slot>();
            foreach (var s in this.invSlots)
            {
                if (IsCurrency(s, id))
                {
                    list.Add(s);
                }
            }

            return list;
        }

        private static bool IsCurrency(Slot s, string id) =>
            s.InternalName.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(s.Path) && s.Path.Contains(id, StringComparison.OrdinalIgnoreCase));

        private static int IndexOf(string internalName)
        {
            for (var i = 0; i < Catalog.All.Length; i++)
            {
                if (Catalog.All[i].InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return 0;
        }

        private string[]? labelCache;

        private string[] CatalogLabels()
        {
            if (this.labelCache != null)
            {
                return this.labelCache;
            }

            this.labelCache = new string[Catalog.All.Length];
            for (var i = 0; i < Catalog.All.Length; i++)
            {
                var row = Catalog.All[i];
                this.labelCache[i] = this.PluginText.T($"item.{row.InternalName}", row.English);
            }

            return this.labelCache;
        }
    }
}
