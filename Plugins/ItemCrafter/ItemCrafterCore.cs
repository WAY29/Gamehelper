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
    using GameHelper.Localization;
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
        private const int StashTabContainerOff = 0x458;
        private const int StashesBeginOff = 0x358;
        private const int StashesEndOff = 0x360;
        private const int VisibleStashIndexOff = 0x370;
        private const int StashEntryStride = 0x90;
        private const int StashEntryInvOff = 0x80;
        private const int BaseFlagsOffset = 0xC7;
        private const int IdentifiedOffset = 0x90;
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
        private MethodInfo? readI32;

        private bool running;
        private int stepIndex = -1;
        private readonly List<Op> ops = new();
        private List<CraftStep>? dragList;
        private string modComboFilter = string.Empty;
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
        private string stashKind = string.Empty;
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
            public int Quality;
            public bool Corrupted;
            public bool Identified = true;
            public bool OmenOn;
            public IntPtr El;
            public List<string> ModNames = new();
        }

        private sealed class Op
        {
            public required CraftStep Step;
            public required List<(CraftIf Cond, bool Invert)> Preds;
        }

        public override void OnEnable(bool isGameOpened)
        {
            Catalog.Load(this.DllDirectory);
            try
            {
                Catalog.SelfCheck();
            }
            catch (Exception ex)
            {
                this.log.Add($"SelfCheck: {ex.Message}");
            }
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
                this.Settings.Recipes.AddRange(DefaultRecipes());
            }

            foreach (var recipe in this.Settings.Recipes)
            {
                if (recipe.TargetIds.Count == 0)
                {
                    recipe.TargetIds.Add(string.IsNullOrEmpty(recipe.Target) ? Catalog.DefaultTarget : recipe.Target);
                }
            }

            foreach (var recipe in this.Settings.Recipes)
            {
                this.MigrateSteps(recipe.Steps);
            }

            this.Settings.SelectedRecipe = Math.Clamp(this.Settings.SelectedRecipe, 0, this.Settings.Recipes.Count - 1);
        }

        private void MigrateSteps(List<CraftStep> steps)
        {
            foreach (var step in steps)
            {
                if (step.If == null)
                {
                    continue;
                }

                if (step.If.Conds.Count > 0 && step.If.When.Items.Count == 0)
                {
                    step.If.When = Catalog.FromConds(step.If.Conds);
                    step.If.Conds.Clear();
                }
                else if (step.If.When.Items.Count == 0)
                {
                    step.If.When.Items.Add(new CraftExpr());
                }

                this.MigrateSteps(step.If.Then);
                this.MigrateSteps(step.If.Else);
            }
        }

        private static List<CraftRecipe> DefaultRecipes() =>
        [
            Recipe("普通图瓦尔",
                Step(Catalog.Alchemy),
                Step(Catalog.Exalted, 6),
                Step("CurrencyCorrupt")),
            Recipe("效用",
                Step("OmenOnChaosMapPackSize"),
                Step("OmenOnChaosMapItemRarity"),
                Step("OmenOnChaosMapMonsterRarity"),
                Step(Catalog.Alchemy),
                Step(Catalog.Exalted, 6),
                Step("CurrencyRerollRare"),
                Step("CurrencyCorrupt")),
            Recipe("103稀有",
                Step("OmenOnChaosMapPackSize"),
                Step("OmenOnChaosMapMonsterEffectiveness"),
                Step("OmenOnChaosMapItemRarity"),
                Step(Catalog.Alchemy),
                Step(Catalog.Exalted, 5),
                Step("CurrencyRerollRare"),
                Step(Catalog.Exalted, 6),
                Step("CurrencyCorrupt")),
        ];

        private static CraftRecipe Recipe(string name, params CraftStep[] steps) =>
            new() { Name = name, Steps = [.. steps] };

        private static CraftStep Step(string id, int until = 6) =>
            new() { InternalName = id, UntilAffixes = until };

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
            ImGui.Text(this.PluginText.T("settings.hover_delay", "Hover item delay (ms)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##ICHoverDelay", ref this.Settings.HoverDelayMs, 0, 1000);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.click_delay", "Click item delay (ms)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##ICClickDelay", ref this.Settings.ClickDelayMs, 0, 1000);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.abort_px", "Mouse abort (px)"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            ImGui.SliderInt("##ICAbortPx", ref this.Settings.MouseAbortPx, 5, 80);

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.affix_lang", "Name language"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(180);
            var affixLang = Math.Clamp(this.Settings.AffixLanguage, 0, 3);
            if (ImGui.Combo("##ICAffixLang", ref affixLang,
                    [this.PluginText.T("settings.affix_lang_overlay", "Follow overlay"), "English", "简体中文", "繁體中文"], 4))
            {
                this.Settings.AffixLanguage = affixLang;
                this.labelCache = null;
                this.targetLabelCache = null;
                this.nameLangCache = null;
            }

            ImGui.Checkbox(this.PluginText.Label("settings.debug", "Show debug inspector", "ICDebug"), ref this.Settings.ShowDebugWindow);
            ImGui.SameLine();
            ImGui.Checkbox(this.PluginText.Label("settings.log", "Show action log", "ICLog"), ref this.Settings.ShowLogWindow);

            ImGui.Separator();
            if (this.Settings.Recipes.Count == 0)
            {
                this.Settings.Recipes.Add(new CraftRecipe
                {
                    Name = this.PluginText.T("settings.new_recipe", "新配方"),
                    TargetIds = { Catalog.DefaultTarget },
                });
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
                this.Settings.Recipes.Add(new CraftRecipe
                {
                    Name = this.PluginText.T("settings.new_recipe", "新配方"),
                    TargetIds = { Catalog.DefaultTarget },
                });
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

            ImGui.AlignTextToFramePadding();
            ImGui.Text(this.PluginText.T("settings.target", "Items"));
            this.DrawTargetIds(recipe);

            ImGui.Spacing();
            ImGui.Text(this.PluginText.T("settings.steps", "Steps"));
            this.DrawStepList(recipe.Steps, true);

            ImGui.Separator();
            ImGui.TextWrapped(this.running
                ? this.PluginText.F("settings.running", "Running: {0}", this.status)
                : this.PluginText.T("settings.idle", "Idle. Open stash + inventory, press toggle."));
        }

        private void DrawTargetIds(CraftRecipe recipe)
        {
            var overlay = OverlayLocalization.CurrentLanguage;
            var zhHant = overlay == OverlayLanguage.ChineseTraditional;
            var zh = overlay == OverlayLanguage.ChineseSimplified;
            for (var i = 0; i < recipe.TargetIds.Count; i++)
            {
                var id = recipe.TargetIds[i];
                ImGui.PushID(i);
                var label = id;
                var idx = Catalog.IndexOfTarget(id);
                if (idx >= 0 && idx < Catalog.Targets.Length &&
                    Catalog.Targets[idx].Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    label = Catalog.TargetLabel(Catalog.Targets[idx], this.Settings.AffixLanguage, zhHant, zh);
                }

                ImGui.AlignTextToFramePadding();
                ImGui.BulletText(label);
                ImGui.SameLine();
                if (this.IconButton("##delTarget", DrawXIcon))
                {
                    recipe.TargetIds.RemoveAt(i);
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }

            ImGui.SetNextItemWidth(220);
            if (ImGui.BeginCombo("##addTarget", this.PluginText.T("settings.add_target", "Add item")))
            {
                for (var i = 0; i < Catalog.Targets.Length; i++)
                {
                    var row = Catalog.Targets[i];
                    if (recipe.TargetIds.Exists(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (ImGui.Selectable(this.TargetLabels()[i]))
                    {
                        recipe.TargetIds.Add(row.Id);
                    }
                }

                ImGui.EndCombo();
            }
        }

        private void DrawStepList(List<CraftStep> steps, bool addButtons)
        {
            for (var i = 0; i < steps.Count; i++)
            {
                ImGui.PushID(i);
                if (steps[i].If != null)
                {
                    this.DrawIf(steps, i);
                }
                else
                {
                    this.DrawCurrencyStep(steps, i);
                }

                ImGui.PopID();
            }

            if (addButtons)
            {
                this.DrawAddButtons(steps);
            }
        }

        private void DrawAddButtons(List<CraftStep> steps)
        {
            if (ImGui.SmallButton(this.PluginText.T("settings.add_step", "Add step")))
            {
                steps.Add(new CraftStep());
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(this.PluginText.T("settings.add_if", "Add if")))
            {
                steps.Add(new CraftStep
                {
                    InternalName = string.Empty,
                    If = new CraftIf { When = { Items = { new CraftExpr() } } },
                });
            }
        }

        private void DrawIf(List<CraftStep> steps, int i)
        {
            var block = steps[i].If!;
            this.DrawGrip(steps, i, this.PluginText.T("settings.if", "If"));
            ImGui.SameLine();
            var open = ImGui.TreeNodeEx(
                "##if",
                ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.Framed | ImGuiTreeNodeFlags.AllowOverlap,
                this.PluginText.T("settings.if", "If"));
            ImGui.SameLine();
            this.DrawJoin(ref block.When.All);
            ImGui.SameLine();
            if (this.IconButton("##delIf", DrawXIcon))
            {
                if (open)
                {
                    ImGui.TreePop();
                }

                steps.RemoveAt(i);
                return;
            }

            if (!open)
            {
                return;
            }

            this.DrawGroupItems(block.When);
            this.DrawIfBranch("then", this.PluginText.T("settings.then", "Then"), block.Then);
            this.DrawIfBranch("else", this.PluginText.T("settings.else", "Else"), block.Else);
            ImGui.TreePop();
        }

        private void DrawJoin(ref bool all)
        {
            ImGui.SetNextItemWidth(72);
            var join = all ? 0 : 1;
            if (ImGui.Combo("##join", ref join, [this.PluginText.T("settings.and", "AND"), this.PluginText.T("settings.or", "OR")], 2))
            {
                all = join == 0;
            }
        }

        private void DrawGroupItems(CraftExpr group)
        {
            for (var i = 0; i < group.Items.Count; i++)
            {
                var item = group.Items[i];
                ImGui.PushID(i);
                if (item.Items.Count > 0)
                {
                    this.DrawJoin(ref item.All);
                    ImGui.SameLine();
                    if (this.IconButton("##delGrp", DrawXIcon))
                    {
                        group.Items.RemoveAt(i);
                        ImGui.PopID();
                        break;
                    }

                    ImGui.Indent();
                    this.DrawGroupItems(item);
                    ImGui.Unindent();
                }
                else
                {
                    this.DrawCondOp(ref item.Not);
                    ImGui.SameLine();
                    this.DrawModCombo(ref item.Mod);
                    ImGui.SameLine();
                    if (this.IconButton("##delCond", DrawXIcon))
                    {
                        group.Items.RemoveAt(i);
                        ImGui.PopID();
                        break;
                    }
                }

                ImGui.PopID();
            }

            if (ImGui.SmallButton(this.PluginText.T("settings.add_cond", "Add condition")))
            {
                group.Items.Add(new CraftExpr());
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(this.PluginText.T("settings.add_group", "( )")))
            {
                group.Items.Add(new CraftExpr { All = false, Items = { new CraftExpr() } });
            }
        }

        private void DrawIfBranch(string id, string label, List<CraftStep> steps)
        {
            ImGui.PushID(id);
            var open = ImGui.TreeNodeEx("##br", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap, label);
            ImGui.SameLine();
            this.DrawAddButtons(steps);
            if (open)
            {
                this.DrawStepList(steps, false);
                ImGui.TreePop();
            }

            ImGui.PopID();
        }

        private void DrawCondOp(ref bool not)
        {
            ImGui.SetNextItemWidth(88);
            var op = not ? 1 : 0;
            if (ImGui.Combo("##condOp", ref op, [this.PluginText.T("settings.cond_has", "Has affix"), this.PluginText.T("settings.cond_not", "Lacks affix")], 2))
            {
                not = op == 1;
            }
        }

        private void DrawModCombo(ref string id)
        {
            ImGui.SetNextItemWidth(240);
            if (!ImGui.BeginCombo("##mod", this.ModPreview(id)))
            {
                return;
            }

            if (ImGui.IsWindowAppearing())
            {
                this.modComboFilter = string.Empty;
                ImGui.SetKeyboardFocusHere();
            }

            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##filter", this.PluginText.T("settings.mod_hint", "含词缀"), ref this.modComboFilter, 128);
            foreach (var mod in Catalog.FilterMods(this.modComboFilter))
            {
                var selected = mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{this.ModPreview(mod.Id)}##{mod.Id}", selected))
                {
                    id = mod.Id;
                }

                if (selected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }

        private string ModPreview(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return this.PluginText.T("settings.mod_hint", "含词缀");
            }

            var overlay = OverlayLocalization.CurrentLanguage;
            foreach (var mod in Catalog.Mods)
            {
                if (mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    return Catalog.ModLabel(
                        mod,
                        this.Settings.AffixLanguage,
                        overlay == OverlayLanguage.ChineseTraditional,
                        overlay == OverlayLanguage.ChineseSimplified);
                }
            }

            return id;
        }

        private void DrawCurrencyStep(List<CraftStep> steps, int i)
        {
            var step = steps[i];
            this.DrawGrip(steps, i, this.ItemLabel(step.InternalName));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(220);
            var cur = IndexOf(step.InternalName);
            if (ImGui.Combo("##item", ref cur, CatalogLabels(), Catalog.All.Length))
            {
                step.InternalName = Catalog.All[cur].InternalName;
            }

            this.AcceptStepDrop(steps, i);

            if (Catalog.TryGet(step.InternalName, out var info) && info.Kind is StepKind.Exalt or StepKind.Annul or StepKind.Augment)
            {
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.Text(this.PluginText.T("settings.until", "Until"));
                ImGui.SameLine();
                ImGui.SetNextItemWidth(56);
                ImGui.InputInt("##until", ref step.UntilAffixes, 0);
                if (!ImGui.IsItemActive())
                {
                    step.UntilAffixes = info.Kind switch
                    {
                        StepKind.Exalt => Catalog.ClampUntil(step.UntilAffixes),
                        StepKind.Annul => Catalog.ClampAnnul(step.UntilAffixes),
                        _ => Catalog.ClampAugment(step.UntilAffixes),
                    };
                }

                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.Text(this.PluginText.T("settings.affixes", "mods"));
            }

            ImGui.SameLine();
            if (this.IconButton("##delStep", DrawXIcon))
            {
                steps.RemoveAt(i);
            }
        }

        private void DrawGrip(List<CraftStep> steps, int i, string dragLabel)
        {
            this.IconButton("##grip", DrawGripIcon);
            if (ImGui.BeginDragDropSource())
            {
                this.dragList = steps;
                this.dragStep = i;
                ImGui.SetDragDropPayload("ICStep", IntPtr.Zero, 0);
                ImGui.Text(dragLabel);
                ImGui.EndDragDropSource();
            }

            this.AcceptStepDrop(steps, i);
        }

        private void AcceptStepDrop(List<CraftStep> steps, int i)
        {
            if (!ImGui.BeginDragDropTarget())
            {
                return;
            }

            ImGui.AcceptDragDropPayload("ICStep");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                this.dragList == steps &&
                this.dragStep >= 0 &&
                this.dragStep != i)
            {
                Move(steps, this.dragStep, i);
                this.dragStep = -1;
                this.dragList = null;
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
                    this.DrawHoverRect();
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

            ImGui.Text($"Stash slots: {this.stashSlots.Count} ({this.stashKind})  Inv slots: {this.invSlots.Count}");
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

            if (mods != null && mods.Address != IntPtr.Zero && this.EnsureMem())
            {
                var identified = this.ReadByte(mods.Address + IdentifiedOffset) != 0;
                ImGui.TextColored(
                    identified ? new Vector4(0.35f, 0.9f, 0.35f, 1f) : new Vector4(1f, 0.75f, 0.2f, 1f),
                    identified ? "已鉴定" : "未鉴定");
                ImGui.SameLine();
                ImGui.Text($"(Mods+0x90={this.ReadByte(mods.Address + IdentifiedOffset):X2})");
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
            this.ops.Clear();
            this.ScanPanels();
            var recipes = this.Settings.Recipes;
            var recipe = recipes.Count == 0
                ? null
                : recipes[Math.Clamp(this.Settings.SelectedRecipe, 0, recipes.Count - 1)];
            if (recipe != null)
            {
                this.Compile(recipe.Steps);
            }

            this.Log($"开始 配方={recipe?.Name ?? "?"} 步骤 {this.ops.Count}");
        }

        private bool Passes(Op op, Slot slot)
        {
            foreach (var (cond, invert) in op.Preds)
            {
                if (!Catalog.MatchesConds(slot.ModNames, cond.When, invert))
                {
                    return false;
                }
            }

            return true;
        }

        private void Compile(List<CraftStep> roots)
        {
            var q = new Queue<(List<CraftStep> Steps, List<(CraftIf Cond, bool Invert)> Pred)>();
            q.Enqueue((roots, []));
            while (q.Count > 0)
            {
                var (steps, pred) = q.Dequeue();
                foreach (var step in steps)
                {
                    if (step.If == null && Catalog.TryGet(step.InternalName, out _))
                    {
                        this.ops.Add(new Op { Step = step, Preds = pred.ToList() });
                    }
                }

                foreach (var step in steps)
                {
                    if (step.If == null)
                    {
                        continue;
                    }

                    var thenPred = pred.ToList();
                    thenPred.Add((step.If, false));
                    var elsePred = pred.ToList();
                    elsePred.Add((step.If, true));
                    q.Enqueue((step.If.Then, thenPred));
                    q.Enqueue((step.If.Else, elsePred));
                }
            }
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

                this.nextAtMs = Environment.TickCount64 + this.ClickDelay();
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

            this.nextAtMs = Environment.TickCount64 +
                (act.Kind is ActKind.Left or ActKind.Right ? this.ClickDelay() : this.HoverDelay());
        }

        private int HoverDelay() => Math.Max(0, this.Settings.HoverDelayMs);

        private int ClickDelay() => Math.Max(0, this.Settings.ClickDelayMs);

        private bool LoadNextStep()
        {
            var recipes = this.Settings.Recipes;
            if (recipes.Count == 0)
            {
                return false;
            }

            var recipe = recipes[Math.Clamp(this.Settings.SelectedRecipe, 0, recipes.Count - 1)];
            this.stepIndex++;
            while (this.stepIndex < this.ops.Count)
            {
                this.ScanStash();
                this.ScanInv();
                var op = this.ops[this.stepIndex];
                var step = op.Step;
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
                        this.Log($"步骤 {this.stepIndex + 1}: {this.ItemLabel(info.InternalName, info.English)} 已启用，跳过");
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

                    this.Log($"步骤 {this.stepIndex + 1}: {this.ItemLabel(info.InternalName, info.English)} 预兆 x{toClick.Count}");
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
                    if (!Catalog.MatchesAny(recipe.TargetIds, stone.Path, stone.InternalName, stone.DisplayName) ||
                        !Catalog.CanApply(info, stone.Path) ||
                        !this.Passes(op, stone) ||
                        !Catalog.IsEligible(info.Kind, stone.Rarity, stone.ExplicitCount, stone.Corrupted, step.UntilAffixes, stone.Quality, stone.Identified))
                    {
                        continue;
                    }

                    targets.Add(stone);
                }

                targets.Sort(GridOrder);
                if (targets.Count == 0)
                {
                    this.Log($"步骤 {this.stepIndex + 1}: {this.ItemLabel(info.InternalName, info.English)} 无目标，跳过");
                    this.stepIndex++;
                    continue;
                }

                this.lastCurrencyOverlay = Center(currency);
                this.pending.Add(new Act(ActKind.Move, Center(currency)));
                this.pending.Add(new Act(ActKind.Right, Center(currency)));
                this.pending.Add(new Act(ActKind.ShiftOn, default));
                foreach (var t in targets)
                {
                    var clicks = Catalog.Clicks(info.Kind, t.ExplicitCount, step.UntilAffixes, t.Quality);
                    var pos = Center(t);
                    this.pending.Add(new Act(ActKind.Move, pos));
                    for (var i = 0; i < clicks; i++)
                    {
                        this.pending.Add(new Act(ActKind.Left, pos));
                    }

                    this.highlights.Add(t);
                }

                this.pending.Add(new Act(ActKind.ShiftOff, default));

                this.Log($"步骤 {this.stepIndex + 1}: {this.ItemLabel(info.InternalName, info.English)} 目标 {targets.Count}");
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

        private void DrawHoverRect()
        {
            var dl = ImGui.GetForegroundDrawList();
            foreach (var slot in this.stashSlots)
            {
                dl.AddRect(slot.Pos, slot.Pos + slot.Size, 0xFFFFFF00, 0f, ImDrawFlags.None, 2f);
            }

            if (this.lastHoveredSlot is { } s)
            {
                dl.AddRect(s.Pos, s.Pos + s.Size, 0xFF00FF00, 0f, ImDrawFlags.None, 3f);
            }
        }

        private void ScanPanels()
        {
            this.ScanStash();
            this.ScanInv();
            this.RefreshHover();
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
            foreach (var s in this.stashSlots)
            {
                if (!Contains(s, mouse))
                {
                    continue;
                }

                var area = s.Size.X * s.Size.Y;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = s;
                }
            }

            foreach (var s in this.invSlots)
            {
                if (!Contains(s, mouse))
                {
                    continue;
                }

                var area = s.Size.X * s.Size.Y;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = s;
                }
            }

            if (best != null)
            {
                this.lastHovered = best.Item;
                this.lastHoveredSlot = best;
            }
        }

        private void ScanStash()
        {
            this.stashSlots.Clear();
            this.stashKind = string.Empty;
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
            var active = this.PickActiveTab(tabs);
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
                    this.stashKind = "waystone";
                    this.ProcessWaystoneTab(kids);
                    return;
                }
            }

            var normal = this.ResolvePath(active, new[] { 0, 0 });
            if (this.HasSize(normal))
            {
                this.ProcessGrid(normal, this.stashSlots);
                if (this.stashSlots.Count > 0)
                {
                    var n = this.ReadVec(this.ReadUi(normal).ChildrensPtr).Length;
                    this.stashKind = n >= 400 ? $"quad {n}" : n >= 60 ? $"normal {n}" : $"grid {n}";
                    return;
                }
            }

            var flat = this.ResolvePath(active, new[] { 0 });
            if (this.HasSize(flat))
            {
                this.ProcessGrid(flat, this.stashSlots);
                if (this.stashSlots.Count > 0)
                {
                    this.stashKind = $"flat {this.stashSlots.Count}";
                    return;
                }
            }

            var fragmentRoot = this.ResolvePath(active, new[] { 0, 0, 0, 1 });
            if (fragmentRoot != IntPtr.Zero)
            {
                var pages = this.ReadVec(this.ReadUi(fragmentRoot).ChildrensPtr);
                if (pages.Length == 6)
                {
                    this.ProcessFragmentTabletsTab(pages);
                    if (this.stashSlots.Count > 0)
                    {
                        return;
                    }
                }
            }

            this.CollectWellGroups(active, this.stashSlots);
            if (this.stashSlots.Count > 0)
            {
                this.stashKind = $"wells {this.stashSlots.Count}";
            }
        }

        private IntPtr PickActiveTab(IntPtr[] tabs)
        {
            foreach (var tab in tabs)
            {
                if (this.HasTabContent(tab))
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

            return IntPtr.Zero;
        }

        private bool HasTabContent(IntPtr tab)
        {
            if (tab == IntPtr.Zero || !this.IsVisible(tab))
            {
                return false;
            }

            var waystone = this.ResolvePath(tab, new[] { 0, 1 });
            if (waystone != IntPtr.Zero && this.ReadVec(this.ReadUi(waystone).ChildrensPtr).Length == 16)
            {
                return true;
            }

            var fragment = this.ResolvePath(tab, new[] { 0, 0, 0, 1 });
            if (fragment != IntPtr.Zero && this.ReadVec(this.ReadUi(fragment).ChildrensPtr).Length == 6)
            {
                return true;
            }

            return this.HasSize(this.ResolvePath(tab, new[] { 0, 0 })) || this.HasSize(this.ResolvePath(tab, new[] { 0 }));
        }

        private bool HasSize(IntPtr el) =>
            el != IntPtr.Zero && this.IsVisible(el) && this.ReadUi(el).UnscaledSize.X > 0f;

        private IntPtr FindVisibleStashInventory()
        {
            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null)
            {
                return IntPtr.Zero;
            }

            var left = ui.LeftPanel.Address;
            var inv = this.TryVisibleInventory(left);
            if (inv == IntPtr.Zero)
            {
                inv = this.TryVisibleInventory(this.ReadPtr(left + StashTabContainerOff));
            }

            if (inv != IntPtr.Zero)
            {
                return inv;
            }

            foreach (var path in StashTabsPaths)
            {
                var tabs = this.ResolvePath(left, path);
                if (tabs == IntPtr.Zero)
                {
                    continue;
                }

                inv = this.TryVisibleInventory(tabs);
                if (inv != IntPtr.Zero)
                {
                    return inv;
                }

                var cur = this.PickActiveTab(this.ReadVec(this.ReadUi(tabs).ChildrensPtr));
                for (var i = 0; i < 16 && cur != IntPtr.Zero; i++)
                {
                    inv = this.TryVisibleInventory(cur);
                    if (inv == IntPtr.Zero)
                    {
                        inv = this.TryVisibleInventory(this.ReadPtr(cur + StashTabContainerOff));
                    }

                    if (inv != IntPtr.Zero)
                    {
                        return inv;
                    }

                    cur = this.ReadUi(cur).ParentPtr;
                }
            }

            return IntPtr.Zero;
        }

        private IntPtr TryVisibleInventory(IntPtr stc)
        {
            if (stc == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var first = this.ReadPtr(stc + StashesBeginOff);
            var last = this.ReadPtr(stc + StashesEndOff);
            var span = last.ToInt64() - first.ToInt64();
            if (first == IntPtr.Zero || span <= 0 || span % StashEntryStride != 0)
            {
                return IntPtr.Zero;
            }

            var n = (int)(span / StashEntryStride);
            if (n < 1 || n > 400)
            {
                return IntPtr.Zero;
            }

            var vis = this.ReadI32(stc + VisibleStashIndexOff);
            if ((uint)vis >= (uint)n)
            {
                return IntPtr.Zero;
            }

            var inv = this.ReadPtr(first + (vis * StashEntryStride) + StashEntryInvOff);
            return inv != IntPtr.Zero && this.IsVisible(inv) ? inv : IntPtr.Zero;
        }

        private void ProcessFragmentTabletsTab(IntPtr[] pages)
        {
            for (var i = 0; i < pages.Length; i++)
            {
                var page = pages[i];
                if (page == IntPtr.Zero || !this.IsVisible(page))
                {
                    continue;
                }

                this.stashKind = $"fragment-tablet p{i + 1}/{pages.Length}";
                var grid = this.ResolvePath(page, new[] { 0, 0 });
                this.ProcessGrid(grid, this.stashSlots);
                return;
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

            foreach (var slot in this.ReadVec(this.ReadUi(gridRoot).ChildrensPtr))
            {
                this.TryAddSlot(slot, dest);
            }
        }

        private void CollectWellGroups(IntPtr panel, List<Slot> dest)
        {
            if (panel == IntPtr.Zero ||
                !PluginUiElementReflection.TryGetAbsoluteRect(panel, out var clipPos, out var clipSize) ||
                clipSize.X < 200f || clipSize.Y < 200f)
            {
                return;
            }

            this.WalkWellGroups(panel, dest, 0, clipPos, clipSize);
            this.WalkIsolatedWells(panel, dest, 0, clipPos, clipSize);
        }

        private void WalkWellGroups(IntPtr el, List<Slot> dest, int depth, Vector2 clipPos, Vector2 clipSize)
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
                    this.TryAddWell(kid, dest, clipPos, clipSize);
                }
            }

            foreach (var kid in kids)
            {
                this.WalkWellGroups(kid, dest, depth + 1, clipPos, clipSize);
            }
        }

        private void WalkIsolatedWells(IntPtr el, List<Slot> dest, int depth, Vector2 clipPos, Vector2 clipSize)
        {
            if (el == IntPtr.Zero || depth > 10 || !this.IsVisible(el))
            {
                return;
            }

            this.TryAddWell(el, dest, clipPos, clipSize);
            foreach (var kid in this.ReadVec(this.ReadUi(el).ChildrensPtr))
            {
                this.WalkIsolatedWells(kid, dest, depth + 1, clipPos, clipSize);
            }
        }

        private bool IsWellInClip(IntPtr el, Vector2 clipPos, Vector2 clipSize)
        {
            return this.IsVisible(el) &&
                   PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size) &&
                   IsWellSize(size) &&
                   ContainsClip(pos + (size * 0.5f), clipPos, clipSize);
        }

        private bool TryAddWell(IntPtr el, List<Slot> dest, Vector2 clipPos, Vector2 clipSize)
        {
            if (!this.IsWellInClip(el, clipPos, clipSize))
            {
                return false;
            }

            PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size);
            var itemAddr = this.FindItemPtr(el, 2);
            if (itemAddr == IntPtr.Zero ||
                !PluginUiElementReflection.TryValidateItemAddress(itemAddr, out var path, out _))
            {
                return false;
            }

            var area = size.X * size.Y;
            for (var i = 0; i < dest.Count; i++)
            {
                var existing = dest[i];
                if (existing.El == el)
                {
                    return false;
                }

                if (existing.Item.Address == itemAddr)
                {
                    if (area > existing.Size.X * existing.Size.Y)
                    {
                        var bigger = ReadItem(itemAddr);
                        if (bigger != null)
                        {
                            dest[i] = this.ToSlot(bigger, path, pos, size, el);
                        }
                    }

                    return true;
                }
            }

            var item = ReadItem(itemAddr);
            if (item == null)
            {
                return false;
            }

            dest.Add(this.ToSlot(item, path, pos, size, el));
            return true;
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

        private static bool IsWellSize(Vector2 size) =>
            size.X >= 32f && size.Y >= 32f && size.X <= 220f && size.Y <= 420f;

        private static bool ContainsClip(Vector2 p, Vector2 clipPos, Vector2 clipSize) =>
            p.X >= clipPos.X && p.Y >= clipPos.Y &&
            p.X <= clipPos.X + clipSize.X && p.Y <= clipPos.Y + clipSize.Y;

        private bool TryAddSlot(IntPtr slot, List<Slot> dest)
        {
            if (slot == IntPtr.Zero || !this.IsVisible(slot))
            {
                return false;
            }

            var itemAddr = this.ItemPtr(slot);
            if (itemAddr == IntPtr.Zero ||
                !PluginUiElementReflection.TryValidateItemAddress(itemAddr, out var path, out _))
            {
                return false;
            }

            var item = ReadItem(itemAddr);
            if (item == null || !PluginUiElementReflection.TryGetAbsoluteRect(slot, out var pos, out var size))
            {
                return false;
            }

            if (size.X < 8f || size.Y < 8f || size.X > 280f || size.Y > 280f)
            {
                return false;
            }

            var made = this.ToSlot(item, path, pos, size, slot);
            dest.Add(made);
            return true;
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
            var modNames = new List<string>();
            if (item.TryGetComponent<Mods>(out var mods, shouldCache: false))
            {
                rarity = mods.Rarity;
                explicitCount = mods.ExplicitMods.Count;
                foreach (var mod in mods.ExplicitMods)
                {
                    if (!string.IsNullOrEmpty(mod.name))
                    {
                        modNames.Add(mod.name);
                    }
                }
            }

            var stack = item.TryGetComponent<Stack>(out var st) ? Math.Max(1, st.Count) : 1;
            var quality = 0;
            if (this.TryGetCompAddr(item, "Quality", out var qualityAddr) && this.EnsureMem())
            {
                quality = (int)this.ReadU32(qualityAddr + 0x18);
            }

            var identified = true;
            if (mods != null && mods.Address != IntPtr.Zero && this.EnsureMem())
            {
                identified = this.ReadByte(mods.Address + IdentifiedOffset) != 0;
            }

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
                ModNames = modNames,
                Stack = stack,
                Quality = quality,
                Corrupted = this.IsCorrupted(b),
                Identified = identified,
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
            this.readI32 = genericRead.MakeGenericMethod(typeof(int));
            return true;
        }

        private int ReadI32(IntPtr addr)
        {
            if (this.readI32 == null || addr == IntPtr.Zero)
            {
                return 0;
            }

            try
            {
                return this.readI32.Invoke(this.handle, new object[] { addr }) is int v ? v : 0;
            }
            catch
            {
                return 0;
            }
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
            foreach (var s in this.stashSlots)
            {
                if (IsCurrency(s, id))
                {
                    list.Add(s);
                }
            }

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
        private string[]? targetLabelCache;
        private Dictionary<string, string>? nameLangCache;
        private int nameLangCacheFor = int.MinValue;
        private string nameLangFileCached = string.Empty;

        private string ItemLabel(string internalName, string? fallback = null)
        {
            if (Catalog.TryGet(internalName, out var info))
            {
                fallback ??= info.English;
            }

            return this.GameText($"item.{internalName}", fallback ?? internalName);
        }

        private string NameLangFile()
        {
            return Math.Clamp(this.Settings.AffixLanguage, 0, 3) switch
            {
                1 => "en-US",
                2 => "zh-CN",
                3 => "zh-Hant",
                _ => OverlayLocalization.LanguageCodes(OverlayLocalization.CurrentLanguage)[0],
            };
        }

        private string GameText(string key, string fallback)
        {
            var file = this.NameLangFile();
            if (this.nameLangCache == null ||
                this.nameLangCacheFor != this.Settings.AffixLanguage ||
                file != this.nameLangFileCached)
            {
                this.nameLangCacheFor = this.Settings.AffixLanguage;
                this.nameLangFileCached = file;
                var path = Path.Join(this.DllDirectory, "Localization", file + ".json");
                this.nameLangCache = File.Exists(path)
                    ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                    : null;
                this.nameLangCache ??= new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return this.nameLangCache.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)
                ? value
                : fallback;
        }

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
                this.labelCache[i] = this.ItemLabel(row.InternalName, row.English);
            }

            return this.labelCache;
        }

        private string[] TargetLabels()
        {
            if (this.targetLabelCache != null)
            {
                return this.targetLabelCache;
            }

            var overlay = OverlayLocalization.CurrentLanguage;
            var zhHant = overlay == OverlayLanguage.ChineseTraditional;
            var zh = overlay == OverlayLanguage.ChineseSimplified;
            this.targetLabelCache = new string[Catalog.Targets.Length];
            for (var i = 0; i < Catalog.Targets.Length; i++)
            {
                this.targetLabelCache[i] = Catalog.TargetLabel(
                    Catalog.Targets[i], this.Settings.AffixLanguage, zhHant, zh);
            }

            return this.targetLabelCache;
        }
    }
}
