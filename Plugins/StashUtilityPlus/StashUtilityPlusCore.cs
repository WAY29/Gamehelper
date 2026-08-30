namespace StashUtilityPlus
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Numerics;
    using System.Reflection;
    using GameHelper;
    using GameHelper.Data;
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

    public sealed class StashUtilityPlusCore : PCore<StashUtilityPlusSettings>
    {
        private const int ItemPtrHint = 0x4F8;
        private const float BorderMargin = 4f;
        private static readonly int[][] StashTabsPaths =
        {
            new[] { 2, 0, 0, 0, 1, 1 },
            new[] { 2, 0, 0, 0, 0, 1, 1 },
        };
        private static readonly int[] InventoryPath = { 5, 36 };

        private object? handle;
        private MethodInfo? readPtr;
        private MethodInfo? readUi;
        private MethodInfo? readVec;

        private readonly List<Slot> slots = new();
        private List<HighlightRule>? dragList;
        private int dragRule = -1;
        private string modComboFilter = string.Empty;
        private string SettingPath => Path.Join(this.DllDirectory, "config", "settings.txt");

        private sealed class Slot
        {
            public required Vector2 Pos;
            public required Vector2 Size;
            public required string TypeId;
            public required List<string> ModNames;
        }

        private readonly record struct ModInfo(string Id, string English, string ZhCN, string ZhTW);

        private static ModInfo[] Mods { get; set; } = [];
        private static ModInfo[] Tablets { get; set; } = [];

        private sealed class JsonRow
        {
            public string id = "";
            public string en = "";
            public string zh_CN = "";
            public string zh_TW = "";
        }

        [Flags]
        private enum HighSlot
        {
            None = 0,
            Border = 1,
            TL = 2,
            TR = 4,
            BL = 8,
            BR = 16,
            All = Border | TL | TR | BL | BR,
        }

        public override void OnEnable(bool isGameOpened)
        {
            LoadMods(this.DllDirectory);
            SelfCheck();
            if (!File.Exists(this.SettingPath))
            {
                return;
            }

            try
            {
                this.Settings = JsonConvert.DeserializeObject<StashUtilityPlusSettings>(
                    File.ReadAllText(this.SettingPath),
                    new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace })
                    ?? new StashUtilityPlusSettings();
            }
            catch
            {
                this.Settings = new StashUtilityPlusSettings();
            }

            this.Settings.Rules ??= new List<HighlightRule>();
            foreach (var rule in this.Settings.Rules)
            {
                rule.When ??= new RuleExpr();
                rule.When.Items ??= new List<RuleExpr>();
                rule.TabletTypes ??= new List<string>();
                rule.Action = Math.Clamp(rule.Action, 0, 4);
            }
        }

        public override void OnDisable()
        {
        }

        public override void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(this.SettingPath)!);
                File.WriteAllText(this.SettingPath, JsonConvert.SerializeObject(this.Settings, Formatting.Indented));
            }
            catch
            {
            }
        }

        public override void DrawSettings()
        {
            var affixLang = Math.Clamp(this.Settings.AffixLanguage, 0, 3);
            var names = new[]
            {
                this.PluginText.T("settings.affix_lang_overlay", "Follow overlay"),
                "English",
                "简体中文",
                "繁體中文",
            };
            if (ImGui.Combo(this.PluginText.T("settings.affix_lang", "Name language"), ref affixLang, names, names.Length))
            {
                this.Settings.AffixLanguage = affixLang;
            }

            if (ImGui.SmallButton(this.PluginText.T("settings.add_rule", "Add rule")))
            {
                this.Settings.Rules.Add(this.NewRule());
            }

            for (var i = 0; i < this.Settings.Rules.Count; i++)
            {
                var rule = this.Settings.Rules[i];
                ImGui.PushID(i);
                this.DrawGrip(i, rule.Name);
                ImGui.SameLine();
                ImGui.Checkbox("##on", ref rule.Enabled);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(180);
                ImGui.InputText("##name", ref rule.Name, 64);
                ImGui.SameLine();
                if (this.IconButton("##del", DrawXIcon))
                {
                    this.Settings.Rules.RemoveAt(i);
                    ImGui.PopID();
                    break;
                }

                this.DrawJoin(ref rule.When.All);
                this.DrawGroupItems(rule.When);
                this.DrawTypeRow(rule);
                this.DrawHighlightRow(rule);

                ImGui.Separator();
                ImGui.PopID();
            }
        }

        public override void DrawUI()
        {
            if (Core.States.GameCurrentState != GameStateTypes.InGameState ||
                !Core.Process.Foreground ||
                this.Settings.Rules.Count == 0)
            {
                return;
            }

            this.slots.Clear();
            this.ScanStash();
            this.ScanInv();
            foreach (var slot in this.slots)
            {
                this.DrawSlot(slot);
            }
        }

        private HighlightRule NewRule() => new()
        {
            Name = this.PluginText.T("settings.new_rule", "新规则"),
            When = { Items = { new RuleExpr() } },
        };

        private void DrawTypeRow(HighlightRule rule)
        {
            if (this.Chip("all", this.TypeLabel("all", "全"), rule.TabletTypes.Count == 0))
            {
                rule.TabletTypes.Clear();
            }

            foreach (var tablet in Tablets)
            {
                ImGui.SameLine();
                var on = ContainsType(rule.TabletTypes, tablet.Id);
                if (!this.Chip(tablet.Id, this.TypeLabel(tablet.Id, tablet.English), on))
                {
                    continue;
                }

                if (on)
                {
                    rule.TabletTypes.RemoveAll(t => t.Equals(tablet.Id, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    rule.TabletTypes.Add(tablet.Id);
                }
            }
        }

        private void DrawHighlightRow(HighlightRule rule)
        {
            var labels = new[]
            {
                this.PluginText.T("settings.border", "边框"),
                this.PluginText.T("settings.arrow_tl", "左上"),
                this.PluginText.T("settings.arrow_tr", "右上"),
                this.PluginText.T("settings.arrow_bl", "左下"),
                this.PluginText.T("settings.arrow_br", "右下"),
            };
            for (var a = 0; a < labels.Length; a++)
            {
                if (a > 0)
                {
                    ImGui.SameLine();
                }

                if (this.Chip("act" + a, labels[a], rule.Action == a))
                {
                    rule.Action = a;
                }
            }

            ImGui.SameLine();
            ImGui.ColorEdit4("##c", ref rule.Color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            if (rule.Action == 0)
            {
                var thickness = (int)Math.Round(rule.Thickness);
                if (ImGui.SliderInt("##thick", ref thickness, 1, 10))
                {
                    rule.Thickness = thickness;
                }
            }
            else
            {
                var size = (int)Math.Round(rule.ArrowSize);
                if (ImGui.SliderInt("##thick", ref size, 5, 40))
                {
                    rule.ArrowSize = size;
                }
            }
        }

        private bool Chip(string id, string label, bool on)
        {
            ImGui.PushID(id);
            if (on)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.32f, 0.42f, 0.72f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.40f, 0.50f, 0.82f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.28f, 0.38f, 0.68f, 1f));
            }

            var hit = ImGui.SmallButton(label);
            if (on)
            {
                ImGui.PopStyleColor(3);
            }

            ImGui.PopID();
            return hit;
        }

        private string TypeLabel(string id, string fallback)
        {
            if (id == "all")
            {
                return this.PluginText.T("type.all", "全");
            }

            var overlay = OverlayLocalization.CurrentLanguage;
            foreach (var tablet in Tablets)
            {
                if (tablet.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    return PickName(
                        tablet.English,
                        tablet.ZhCN,
                        tablet.ZhTW,
                        this.Settings.AffixLanguage,
                        overlay == OverlayLanguage.ChineseTraditional,
                        overlay == OverlayLanguage.ChineseSimplified);
                }
            }

            return fallback;
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

        private void DrawGroupItems(RuleExpr group)
        {
            group.Items ??= new List<RuleExpr>();
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
                group.Items.Add(new RuleExpr());
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(this.PluginText.T("settings.add_group", "( )")))
            {
                group.Items.Add(new RuleExpr { All = false, Items = { new RuleExpr() } });
            }
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
            var hint = this.PluginText.T("settings.mod_hint", "contains affix");
            var text = string.IsNullOrEmpty(id) ? string.Empty : this.ModPreview(id);
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputTextWithHint("##modText", hint, ref text, 128))
            {
                id = text.Trim();
            }

            ImGui.SameLine(0, 4);
            ImGui.SetNextItemWidth(ImGui.GetFrameHeight());
            if (!ImGui.BeginCombo("##modPick", string.Empty, ImGuiComboFlags.NoPreview | ImGuiComboFlags.HeightLarge))
            {
                return;
            }

            if (ImGui.IsWindowAppearing())
            {
                this.modComboFilter = string.Empty;
                ImGui.SetKeyboardFocusHere();
            }

            ImGui.SetNextItemWidth(240f);
            ImGui.InputTextWithHint("##filter", hint, ref this.modComboFilter, 128);
            foreach (var mod in FilterMods(this.modComboFilter))
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
                return this.PluginText.T("settings.mod_hint", "contains affix");
            }

            var overlay = OverlayLocalization.CurrentLanguage;
            foreach (var mod in Mods)
            {
                if (mod.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    return PickName(
                        mod.English,
                        mod.ZhCN,
                        mod.ZhTW,
                        this.Settings.AffixLanguage,
                        overlay == OverlayLanguage.ChineseTraditional,
                        overlay == OverlayLanguage.ChineseSimplified);
                }
            }

            return id;
        }

        private void DrawGrip(int i, string dragLabel)
        {
            this.IconButton("##grip", DrawGripIcon);
            if (ImGui.BeginDragDropSource())
            {
                this.dragList = this.Settings.Rules;
                this.dragRule = i;
                ImGui.SetDragDropPayload("SUPRule", IntPtr.Zero, 0);
                ImGui.Text(dragLabel);
                ImGui.EndDragDropSource();
            }

            if (!ImGui.BeginDragDropTarget())
            {
                return;
            }

            ImGui.AcceptDragDropPayload("SUPRule");
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) &&
                this.dragList == this.Settings.Rules &&
                this.dragRule >= 0 &&
                this.dragRule != i)
            {
                var list = this.Settings.Rules;
                var item = list[this.dragRule];
                list.RemoveAt(this.dragRule);
                list.Insert(i, item);
                this.dragRule = -1;
                this.dragList = null;
            }

            ImGui.EndDragDropTarget();
        }

        private bool IconButton(string id, Action<ImDrawListPtr, Vector2, Vector2, uint> draw)
        {
            var size = ImGui.GetFrameHeight();
            var pressed = ImGui.Button(id, new Vector2(size, size));
            draw(ImGui.GetWindowDrawList(), ImGui.GetItemRectMin(), ImGui.GetItemRectMax(), ImGui.GetColorU32(ImGuiCol.Text));
            return pressed;
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

        private void DrawSlot(Slot slot)
        {
            var claimed = HighSlot.None;
            float borderPad = 0f;
            var scale = slot.Size.X / 52f;
            var margin = BorderMargin * scale;
            var dl = ImGui.GetBackgroundDrawList();
            foreach (var rule in this.Settings.Rules)
            {
                if (!rule.Enabled || !TypeMatch(rule.TabletTypes, slot.TypeId) || !Match(rule.When, slot.ModNames))
                {
                    continue;
                }

                var flag = ActionFlag(rule.Action);
                if ((claimed & flag) != 0)
                {
                    continue;
                }

                claimed |= flag;
                if (flag == HighSlot.Border)
                {
                    var thickness = Math.Max(1f, rule.Thickness);
                    borderPad = thickness;
                    var inset = margin + (thickness / 2f);
                    dl.AddRect(
                        slot.Pos + new Vector2(inset, inset),
                        slot.Pos + slot.Size - new Vector2(inset, inset),
                        ImGuiHelper.Color(rule.Color),
                        3f,
                        ImDrawFlags.RoundCornersAll,
                        thickness);
                }
                else
                {
                    this.DrawArrow(dl, slot, rule.Color, rule.ArrowSize, scale, margin, borderPad, rule.Action - 1);
                }
                if (claimed == HighSlot.All)
                {
                    return;
                }
            }
        }

        private static HighSlot ActionFlag(int action) => action switch
        {
            1 => HighSlot.TL,
            2 => HighSlot.TR,
            3 => HighSlot.BL,
            4 => HighSlot.BR,
            _ => HighSlot.Border,
        };

        private void DrawArrow(
            ImDrawListPtr dl,
            Slot slot,
            Vector4 color,
            float size,
            float scale,
            float margin,
            float borderPad,
            int pos)
        {
            var arrowSize = Math.Max(4f, size) * scale;
            var pad = margin + borderPad + (4f * scale);
            var tip = pos switch
            {
                1 => slot.Pos + new Vector2(slot.Size.X - pad - (arrowSize / 2f), pad),
                2 => slot.Pos + new Vector2(pad + (arrowSize / 2f), slot.Size.Y - pad - arrowSize),
                3 => slot.Pos + new Vector2(slot.Size.X - pad - (arrowSize / 2f), slot.Size.Y - pad - arrowSize),
                _ => slot.Pos + new Vector2(pad + (arrowSize / 2f), pad),
            };
            dl.AddTriangleFilled(
                tip,
                tip + new Vector2(-arrowSize / 2f, arrowSize),
                tip + new Vector2(arrowSize / 2f, arrowSize),
                ImGuiHelper.Color(color));
            dl.AddTriangle(
                tip,
                tip + new Vector2(-arrowSize / 2f, arrowSize),
                tip + new Vector2(arrowSize / 2f, arrowSize),
                0xFF000000,
                Math.Max(1f, 1.5f * scale));
        }

        internal static bool Match(RuleExpr expr, IReadOnlyList<string> names)
        {
            if (expr.Items.Count > 0)
            {
                if (expr.All)
                {
                    foreach (var child in expr.Items)
                    {
                        if (!Match(child, names))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                foreach (var child in expr.Items)
                {
                    if (Match(child, names))
                    {
                        return true;
                    }
                }

                return false;
            }

            var has = HasMod(names, expr.Mod);
            return expr.Not ? !has : has;
        }

        private static bool HasMod(IReadOnlyList<string> names, string needle)
        {
            if (string.IsNullOrWhiteSpace(needle))
            {
                return false;
            }

            foreach (var name in names)
            {
                if (FamilyId(name).Equals(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (TryCatalog(name, out var mod) &&
                    (Contains(mod.English, needle) || Contains(mod.ZhCN, needle) || Contains(mod.ZhTW, needle)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryCatalog(string itemName, out ModInfo mod)
        {
            var id = FamilyId(itemName);
            foreach (var row in Mods)
            {
                if (row.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    mod = row;
                    return true;
                }
            }

            mod = default;
            return false;
        }

        private static string FamilyId(string name)
        {
            while (name.Length > 0 && char.IsDigit(name[^1]))
            {
                name = name[..^1];
            }

            return name;
        }

        internal static bool TypeMatch(List<string> types, string typeId) =>
            types == null || types.Count == 0 || ContainsType(types, typeId);

        private static bool ContainsType(List<string> types, string typeId)
        {
            foreach (var t in types)
            {
                if (t.Equals(typeId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void SelfCheck()
        {
            string[] a = ["A"];
            string[] ab = ["A", "B"];
            string[] rarity = ["TowerDroppedItemRarityIncrease3"];
            string[] essenceChance = ["TowerAdditionalEssenceChance"];
            string[] effect = ["TowerMonsterEffectiveness"];
            if (Match(new RuleExpr(), a) ||
                !Match(new RuleExpr { Mod = "A" }, a) ||
                Match(new RuleExpr { Mod = "B" }, a) ||
                !Match(new RuleExpr { Mod = "A", Not = true }, ["B"]) ||
                Match(new RuleExpr { Mod = "A", Not = true }, a) ||
                Match(new RuleExpr { All = true, Items = { new RuleExpr { Mod = "A" }, new RuleExpr { Mod = "B" } } }, a) ||
                !Match(new RuleExpr { All = true, Items = { new RuleExpr { Mod = "A" }, new RuleExpr { Mod = "B" } } }, ab) ||
                !Match(new RuleExpr { All = false, Items = { new RuleExpr { Mod = "A" }, new RuleExpr { Mod = "B" } } }, a) ||
                Match(new RuleExpr { Items = { new RuleExpr() } }, a) ||
                !Match(new RuleExpr { Mod = "TowerDroppedItemRarityIncrease" }, rarity) ||
                Match(new RuleExpr { Mod = "TowerAdditionalEssence" }, essenceChance) ||
                !Match(new RuleExpr { Mod = "稀有度" }, rarity) ||
                !Match(new RuleExpr { Mod = "效用" }, effect) ||
                Match(new RuleExpr { Mod = "TowerDropped" }, rarity))
            {
                throw new InvalidOperationException("match");
            }

            if (!TypeMatch(new List<string>(), "BreachAugment") ||
                !TypeMatch(new List<string> { "BreachAugment", "AbyssAugment" }, "AbyssAugment") ||
                TypeMatch(new List<string> { "BreachAugment" }, "AbyssAugment"))
            {
                throw new InvalidOperationException("type");
            }
        }

        private void ScanStash()
        {
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

                var active = this.PickActiveTab(this.ReadVec(this.ReadUi(tabs).ChildrensPtr));
                if (active == IntPtr.Zero)
                {
                    continue;
                }

                this.ProcessStashTabs(tabs);
                return;
            }
        }

        private void ScanInv()
        {
            if (!this.EnsureMem())
            {
                return;
            }

            var ui = Core.States.InGameStateObject.GameUi;
            if (ui == null || !ui.RightPanel.IsVisible)
            {
                return;
            }

            this.ProcessGrid(this.ResolvePath(ui.RightPanel.Address, InventoryPath));
        }

        private void ProcessStashTabs(IntPtr stashTabsContainer)
        {
            var active = this.PickActiveTab(this.ReadVec(this.ReadUi(stashTabsContainer).ChildrensPtr));
            if (active == IntPtr.Zero)
            {
                return;
            }

            var before = this.slots.Count;
            var waystoneRoot = this.ResolvePath(active, new[] { 0, 1 });
            if (waystoneRoot != IntPtr.Zero)
            {
                var kids = this.ReadVec(this.ReadUi(waystoneRoot).ChildrensPtr);
                if (kids.Length == 16)
                {
                    this.ProcessWaystoneTab(kids);
                    if (this.slots.Count > before)
                    {
                        return;
                    }
                }
            }

            var normal = this.ResolvePath(active, new[] { 0, 0 });
            if (this.HasSize(normal))
            {
                this.ProcessGrid(normal);
                if (this.slots.Count > before)
                {
                    return;
                }
            }

            var flat = this.ResolvePath(active, new[] { 0 });
            if (this.HasSize(flat))
            {
                this.ProcessGrid(flat);
                if (this.slots.Count > before)
                {
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
                    if (this.slots.Count > before)
                    {
                        return;
                    }
                }
            }

            this.CollectWellGroups(active);
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

        private void ProcessFragmentTabletsTab(IntPtr[] pages)
        {
            foreach (var page in pages)
            {
                if (page == IntPtr.Zero || !this.IsVisible(page))
                {
                    continue;
                }

                this.ProcessGrid(this.ResolvePath(page, new[] { 0, 0 }));
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

                    this.ProcessGrid(pageKids[0]);
                }
            }
        }

        private void ProcessGrid(IntPtr gridRoot)
        {
            if (gridRoot == IntPtr.Zero)
            {
                return;
            }

            foreach (var slot in this.ReadVec(this.ReadUi(gridRoot).ChildrensPtr))
            {
                this.TryAddSlot(slot);
            }
        }

        private void CollectWellGroups(IntPtr panel)
        {
            if (panel == IntPtr.Zero ||
                !PluginUiElementReflection.TryGetAbsoluteRect(panel, out var clipPos, out var clipSize) ||
                clipSize.X < 200f || clipSize.Y < 200f)
            {
                return;
            }

            this.WalkWellGroups(panel, 0, clipPos, clipSize);
            this.WalkIsolatedWells(panel, 0, clipPos, clipSize);
        }

        private void WalkWellGroups(IntPtr el, int depth, Vector2 clipPos, Vector2 clipSize)
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
                    this.TryAddWell(kid, clipPos, clipSize);
                }
            }

            foreach (var kid in kids)
            {
                this.WalkWellGroups(kid, depth + 1, clipPos, clipSize);
            }
        }

        private void WalkIsolatedWells(IntPtr el, int depth, Vector2 clipPos, Vector2 clipSize)
        {
            if (el == IntPtr.Zero || depth > 10 || !this.IsVisible(el))
            {
                return;
            }

            this.TryAddWell(el, clipPos, clipSize);
            foreach (var kid in this.ReadVec(this.ReadUi(el).ChildrensPtr))
            {
                this.WalkIsolatedWells(kid, depth + 1, clipPos, clipSize);
            }
        }

        private bool IsWellInClip(IntPtr el, Vector2 clipPos, Vector2 clipSize)
        {
            return this.IsVisible(el) &&
                   PluginUiElementReflection.TryGetAbsoluteRect(el, out var pos, out var size) &&
                   size.X >= 32f && size.Y >= 32f && size.X <= 220f && size.Y <= 420f &&
                   pos.X + (size.X * 0.5f) >= clipPos.X && pos.Y + (size.Y * 0.5f) >= clipPos.Y &&
                   pos.X + (size.X * 0.5f) <= clipPos.X + clipSize.X && pos.Y + (size.Y * 0.5f) <= clipPos.Y + clipSize.Y;
        }

        private void TryAddWell(IntPtr el, Vector2 clipPos, Vector2 clipSize)
        {
            if (!this.IsWellInClip(el, clipPos, clipSize))
            {
                return;
            }

            this.TryAddSlot(el);
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

            var item = ReadItem(itemAddr);
            if (item == null || !PluginUiElementReflection.TryGetAbsoluteRect(slot, out var pos, out var size))
            {
                return;
            }

            if (size.X < 8f || size.Y < 8f)
            {
                return;
            }

            var internalName = item.TryGetComponent<Base>(out var b) ? b.InternalName ?? string.Empty : string.Empty;
            if (!IsTablet(path, internalName))
            {
                return;
            }

            this.slots.Add(new Slot
            {
                Pos = pos,
                Size = size,
                TypeId = TabletType(path, internalName),
                ModNames = ReadModNames(item),
            });
        }

        private static bool IsTablet(string path, string internalName) =>
            !string.IsNullOrEmpty(TabletType(path, internalName)) ||
            path.Contains("TowerAugment", StringComparison.OrdinalIgnoreCase) ||
            internalName.Contains("TowerAugment", StringComparison.OrdinalIgnoreCase);

        private static string TabletType(string path, string internalName)
        {
            foreach (var tablet in Tablets)
            {
                if (internalName.Equals(tablet.Id, StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(tablet.Id, StringComparison.OrdinalIgnoreCase))
                {
                    return tablet.Id;
                }
            }

            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : internalName;
        }

        private static List<string> ReadModNames(Item item)
        {
            var names = new List<string>();
            if (!item.TryGetComponent<Mods>(out var mods, shouldCache: false))
            {
                return names;
            }

            AddMods(names, mods.ImplicitMods);
            AddMods(names, mods.ExplicitMods);
            AddMods(names, mods.EnchantMods);
            return names;
        }

        private static void AddMods(List<string> dest, List<(string name, (float value0, float value1) values)> mods)
        {
            foreach (var mod in mods)
            {
                if (!string.IsNullOrEmpty(mod.name))
                {
                    dest.Add(mod.name);
                }
            }
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

        private IntPtr ItemPtr(IntPtr el)
        {
            var p = this.ReadPtr(el + ItemPtrHint);
            return PluginUiElementReflection.TryValidateItemAddress(p, out _, out _) ? p : IntPtr.Zero;
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
            this.readUi = genericRead.MakeGenericMethod(typeof(UiElementBaseOffset));
            return true;
        }

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

        private static void LoadMods(string directory)
        {
            var en = ReadLoc(directory, "en-US");
            var cn = ReadLoc(directory, "zh-CN");
            var tw = ReadLoc(directory, "zh-Hant");
            var rows = JsonConvert.DeserializeObject<List<JsonRow>>(File.ReadAllText(Path.Join(directory, "tablet-mods.json")))
                ?? throw new InvalidOperationException("tablet-mods.json");
            Mods = rows.ConvertAll(r => new ModInfo(
                r.id,
                Loc(en, "mod." + r.id, r.en),
                Loc(cn, "mod." + r.id, r.en),
                Loc(tw, "mod." + r.id, r.en))).ToArray();
            if (Mods.Length == 0)
            {
                throw new InvalidOperationException("tablet-mods.json");
            }

            var tablets = JsonConvert.DeserializeObject<List<JsonRow>>(File.ReadAllText(Path.Join(directory, "tablets.json")))
                ?? throw new InvalidOperationException("tablets.json");
            Tablets = tablets.ConvertAll(r => new ModInfo(
                r.id,
                Loc(en, "type." + r.id, r.en),
                Loc(cn, "type." + r.id, string.IsNullOrEmpty(r.zh_CN) ? r.en : r.zh_CN),
                Loc(tw, "type." + r.id, string.IsNullOrEmpty(r.zh_TW) ? r.en : r.zh_TW))).ToArray();
            if (Tablets.Length == 0)
            {
                throw new InvalidOperationException("tablets.json");
            }

            OverlayFromItemCatalog();
        }

        private static void OverlayFromItemCatalog()
        {
            ItemCatalog.Touch();
            var named = new List<ModInfo>();
            foreach (var row in ItemCatalog.SnapshotMods())
            {
                if (string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.English))
                {
                    continue;
                }

                named.Add(new ModInfo(
                    row.Id,
                    row.English,
                    string.IsNullOrEmpty(row.ZhCn) ? row.English : row.ZhCn,
                    string.IsNullOrEmpty(row.ZhTw) ? row.English : row.ZhTw));
            }

            if (named.Count > 0)
            {
                Mods = named.ToArray();
            }

            var tablets = new List<ModInfo>();
            foreach (var row in ItemCatalog.ItemsWherePathContains("/TowerAugment/"))
            {
                if (string.IsNullOrEmpty(row.InternalName) || string.IsNullOrEmpty(row.English))
                {
                    continue;
                }

                tablets.Add(new ModInfo(
                    row.InternalName,
                    row.English,
                    string.IsNullOrEmpty(row.ZhCn) ? row.English : row.ZhCn,
                    string.IsNullOrEmpty(row.ZhTw) ? row.English : row.ZhTw));
            }

            if (tablets.Count > 0)
            {
                Tablets = tablets.ToArray();
            }
        }

        private static Dictionary<string, string> ReadLoc(string directory, string lang)
        {
            var path = Path.Join(directory, "Localization", lang + ".json");
            if (!File.Exists(path))
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static string Loc(Dictionary<string, string> map, string key, string fallback) =>
            map.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : fallback;

        private static string PickName(string en, string zhcn, string zhtw, int language, bool overlayZhHant, bool overlayZh) =>
            language switch
            {
                1 => en,
                2 => zhcn,
                3 => zhtw,
                _ => overlayZhHant ? zhtw : overlayZh ? zhcn : en,
            };

        private static List<ModInfo> FilterMods(string query)
        {
            var q = query.Trim();
            var hits = new List<ModInfo>();
            foreach (var mod in Mods)
            {
                if (q.Length == 0 ||
                    Contains(mod.Id, q) ||
                    Contains(mod.English, q) ||
                    Contains(mod.ZhCN, q) ||
                    Contains(mod.ZhTW, q))
                {
                    hits.Add(mod);
                }
            }

            return hits;
        }

        private static bool Contains(string value, string q) =>
            !string.IsNullOrEmpty(value) &&
            !string.IsNullOrEmpty(q) &&
            value.Contains(q, StringComparison.OrdinalIgnoreCase);
    }
}
