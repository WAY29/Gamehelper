namespace ItemCrafter
{
    using System;
    using System.Collections.Generic;
    using GameHelper.Data;
    using GameHelper.RemoteEnums;

    internal enum StepKind
    {
        Alchemy,
        Regal,
        Exalt,
        Chaos,
        Vaal,
        Omen,
        Transmute,
        Augment,
        Annul,
        Fracture,
        Chance,
        Artificer,
        Quality,
        Identify,
    }

    internal readonly record struct CurrencyInfo(string InternalName, string English, StepKind Kind);

    internal readonly record struct TargetInfo(string Id, string English, string ZhCN, string ZhTW);

    internal readonly record struct ModInfo(string Id, string English, string ZhCN, string ZhTW);

    internal static class Catalog
    {
        public const string Alchemy = "CurrencyUpgradeToRare";
        public const string Exalted = "CurrencyAddModToRare";
        public const string DefaultTarget = "MapKeyTier15";

        public static TargetInfo[] Targets { get; private set; } =
        {
            new("MapKeyTier15", "Waystone (Tier 15)", "Waystone (Tier 15)", "Waystone (Tier 15)"),
        };

        public static ModInfo[] Mods { get; private set; } = [];

        public static void Load(string directory)
        {
            OverlayFromItemCatalog();
        }

        private static void OverlayFromItemCatalog()
        {
            ItemCatalog.Touch();
            var map = new TargetInfo(DefaultTarget, "Waystone (Tier 15)", "Waystone (Tier 15)", "Waystone (Tier 15)");
            if (ItemCatalog.TryGet(DefaultTarget, out var waystone) && waystone != null)
            {
                map = new TargetInfo(
                    waystone.InternalName,
                    string.IsNullOrEmpty(waystone.English) ? map.English : waystone.English,
                    string.IsNullOrEmpty(waystone.ZhCn) ? map.ZhCN : waystone.ZhCn,
                    string.IsNullOrEmpty(waystone.ZhTw) ? map.ZhTW : waystone.ZhTw);
            }

            var next = new List<TargetInfo> { map };
            foreach (var row in ItemCatalog.ItemsWherePathContains("/TowerAugment/"))
            {
                if (string.IsNullOrEmpty(row.InternalName) || string.IsNullOrEmpty(row.English))
                {
                    continue;
                }

                next.Add(new TargetInfo(
                    row.InternalName,
                    row.English,
                    string.IsNullOrEmpty(row.ZhCn) ? row.English : row.ZhCn,
                    string.IsNullOrEmpty(row.ZhTw) ? row.English : row.ZhTw));
            }

            Targets = next.ToArray();

            var namedMods = new List<ModInfo>();
            foreach (var row in ItemCatalog.SnapshotMods())
            {
                if (string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.English))
                {
                    continue;
                }

                namedMods.Add(new ModInfo(
                    row.Id,
                    row.English,
                    string.IsNullOrEmpty(row.ZhCn) ? row.English : row.ZhCn,
                    string.IsNullOrEmpty(row.ZhTw) ? row.English : row.ZhTw));
            }

            Mods = namedMods.ToArray();
        }

        public static string PickName(string en, string zhcn, string zhtw, int language, bool overlayZhHant, bool overlayZh)
        {
            return language switch
            {
                1 => en,
                2 => zhcn,
                3 => zhtw,
                _ => overlayZhHant ? zhtw : overlayZh ? zhcn : en,
            };
        }

        public static string ModLabel(ModInfo mod, int affixLanguage, bool overlayZhHant, bool overlayZh) =>
            PickName(mod.English, mod.ZhCN, mod.ZhTW, affixLanguage, overlayZhHant, overlayZh);

        public static string TargetLabel(TargetInfo target, int language, bool overlayZhHant, bool overlayZh) =>
            PickName(target.English, target.ZhCN, target.ZhTW, language, overlayZhHant, overlayZh);

        public static List<ModInfo> FilterMods(string query)
        {
            var q = query.Trim();
            var hits = new List<ModInfo>();
            foreach (var mod in Mods)
            {
                if (q.Length == 0 || Contains(mod.Id, q) || Contains(mod.English, q) || Contains(mod.ZhCN, q) || Contains(mod.ZhTW, q))
                {
                    hits.Add(mod);
                }
            }

            return hits;
        }

        public static readonly CurrencyInfo[] All =
        {
            new("CurrencyUpgradeToRare", "Orb of Alchemy", StepKind.Alchemy),
            new("CurrencyUpgradeMagicToRare", "Regal Orb", StepKind.Regal),
            new("CurrencyUpgradeMagicToRare2", "Greater Regal Orb", StepKind.Regal),
            new("CurrencyUpgradeMagicToRare3", "Perfect Regal Orb", StepKind.Regal),
            new("CurrencyAddModToRare", "Exalted Orb", StepKind.Exalt),
            new("CurrencyAddModToRare2", "Greater Exalted Orb", StepKind.Exalt),
            new("CurrencyAddModToRare3", "Perfect Exalted Orb", StepKind.Exalt),
            new("CurrencyRerollRare", "Chaos Orb", StepKind.Chaos),
            new("CurrencyRerollRare2", "Greater Chaos Orb", StepKind.Chaos),
            new("CurrencyRerollRare3", "Perfect Chaos Orb", StepKind.Chaos),
            new("CurrencyCorrupt", "Vaal Orb", StepKind.Vaal),
            new("CurrencyUpgradeToMagic", "Orb of Transmutation", StepKind.Transmute),
            new("CurrencyUpgradeToMagic2", "Greater Orb of Transmutation", StepKind.Transmute),
            new("CurrencyUpgradeToMagic3", "Perfect Orb of Transmutation", StepKind.Transmute),
            new("CurrencyAddModToMagic", "Orb of Augmentation", StepKind.Augment),
            new("CurrencyAddModToMagic2", "Greater Orb of Augmentation", StepKind.Augment),
            new("CurrencyAddModToMagic3", "Perfect Orb of Augmentation", StepKind.Augment),
            new("CurrencyRemoveMod", "Orb of Annulment", StepKind.Annul),
            new("CurrencyFractureRare", "Fracturing Orb", StepKind.Fracture),
            new("CurrencyUpgradeRandomly", "Orb of Chance", StepKind.Chance),
            new("CurrencyAddEquipmentSocket", "Artificer's Orb", StepKind.Artificer),
            new("CurrencyMagicQuality", "Arcanist's Etcher", StepKind.Quality),
            new("CurrencyArmourQuality", "Armourer's Scrap", StepKind.Quality),
            new("CurrencyWeaponQuality", "Blacksmith's Whetstone", StepKind.Quality),
            new("CurrencyFlaskQuality", "Glassblower's Bauble", StepKind.Quality),
            new("CurrencyIdentification", "Scroll of Wisdom", StepKind.Identify),
            new("OmenOnChaosMapItemRarity", "Omen of Chaotic Rarity", StepKind.Omen),
            new("OmenOnChaosMapPackSize", "Omen of Chaotic Quantity", StepKind.Omen),
            new("OmenOnChaosMapMonsterEffectiveness", "Omen of Chaotic Effectiveness", StepKind.Omen),
            new("OmenOnChaosMapMonsterRarity", "Omen of Chaotic Monsters", StepKind.Omen),
            new("OmenOnChaosLowestLevelMod", "Omen of Whittling", StepKind.Omen),
            new("OmenOnChaosPrefix", "Omen of Sinistral Erasure", StepKind.Omen),
            new("OmenOnChaosSuffix", "Omen of Dextral Erasure", StepKind.Omen),
            new("OmenOnExaltAddTwoMods", "Omen of Greater Exaltation", StepKind.Omen),
            new("OmenOnExaltAddPrefixes", "Omen of Sinistral Exaltation", StepKind.Omen),
            new("OmenOnExaltAddSuffixes", "Omen of Dextral Exaltation", StepKind.Omen),
            new("OmenOnAnnulRemovePrefixes", "Omen of Sinistral Annulment", StepKind.Omen),
            new("OmenOnAnnulRemoveSuffixes", "Omen of Dextral Annulment", StepKind.Omen),
            new("OmenOnDivineRerollImplicits", "Omen of the Blessed", StepKind.Omen),
            new("OmenOnChanceNotDestroy", "Omen of Chance", StepKind.Omen),
            new("OmenOnChanceAncientOrb", "Omen of the Ancients", StepKind.Omen),
            new("OmenOnDivineSanctify", "Omen of Sanctification", StepKind.Omen),
            new("OmenOnPerfectEssenceSuffix", "Omen of Dextral Crystallisation", StepKind.Omen),
            new("OmenOnPerfectEssencePrefix", "Omen of Sinistral Crystallisation", StepKind.Omen),
            new("OmenOnExaltConsumeQuality", "Omen of Catalysing Exaltation", StepKind.Omen),
            new("OmenOnAbyssRerollOptions", "Omen of Abyssal Echoes", StepKind.Omen),
            new("OmenOnAnnulRemoveAbyssMod", "Omen of Light", StepKind.Omen),
            new("OmenOnAbyssAddPrefixes", "Omen of Sinistral Necromancy", StepKind.Omen),
            new("OmenOnAbyssAddSuffixes", "Omen of Dextral Necromancy", StepKind.Omen),
        };

        public static bool TryGet(string? internalName, out CurrencyInfo info)
        {
            if (!string.IsNullOrEmpty(internalName))
            {
                foreach (var row in All)
                {
                    if (row.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        info = row;
                        return true;
                    }
                }
            }

            info = default;
            return false;
        }

        public static bool IsWaystone(string path, string internalName)
        {
            if (path.Contains("Waystone", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("MapKey", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return internalName.Contains("Waystone", StringComparison.OrdinalIgnoreCase);
        }

        public static bool MatchesTarget(string? targetId, string path, string internalName, string displayName)
        {
            var id = string.IsNullOrEmpty(targetId) ? DefaultTarget : targetId;
            return HasName(path, internalName, displayName, id) || HasTargetAlias(id, path, internalName, displayName);
        }

        public static bool MatchesAny(IReadOnlyList<string> ids, string path, string internalName, string displayName)
        {
            if (ids == null || ids.Count == 0)
            {
                return true;
            }

            foreach (var id in ids)
            {
                if (MatchesTarget(id, path, internalName, displayName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTargetAlias(string id, string path, string internalName, string displayName)
        {
            foreach (var row in Targets)
            {
                if (!row.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return HasName(path, internalName, displayName, row.Id) ||
                       HasName(path, internalName, displayName, row.English) ||
                       HasName(path, internalName, displayName, row.ZhCN) ||
                       HasName(path, internalName, displayName, row.ZhTW);
            }

            return false;
        }

        private static bool HasName(string path, string internalName, string displayName, string name) =>
            !string.IsNullOrEmpty(name) &&
            (internalName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             displayName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             path.Equals(name, StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrEmpty(displayName) &&
              displayName.EndsWith(name, StringComparison.OrdinalIgnoreCase)));

        public static bool CanApply(CurrencyInfo info, string path)
        {
            return info.Kind switch
            {
                StepKind.Quality => MatchesQualityItem(info.InternalName, path),
                StepKind.Artificer => IsArtificerItem(path),
                _ => true,
            };
        }

        public static int IndexOfTarget(string? targetId)
        {
            if (!string.IsNullOrEmpty(targetId))
            {
                for (var i = 0; i < Targets.Length; i++)
                {
                    if (Targets[i].Id.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        public static bool IsEligible(StepKind kind, Rarity rarity, int explicitCount, bool corrupted, int untilAffixes, int quality = 0, bool identified = true)
        {
            if (kind == StepKind.Identify)
            {
                return !identified;
            }

            if (corrupted)
            {
                return false;
            }

            return kind switch
            {
                StepKind.Alchemy => rarity is Rarity.Normal or Rarity.Magic,
                StepKind.Regal => rarity == Rarity.Magic,
                StepKind.Exalt => rarity == Rarity.Rare && explicitCount < ClampUntil(untilAffixes),
                StepKind.Chaos => rarity == Rarity.Rare,
                StepKind.Vaal => rarity is Rarity.Magic or Rarity.Rare,
                StepKind.Omen => true,
                StepKind.Transmute => rarity == Rarity.Normal,
                StepKind.Augment => rarity == Rarity.Magic && explicitCount < ClampAugment(untilAffixes),
                StepKind.Annul => (rarity is Rarity.Magic or Rarity.Rare) && explicitCount > ClampAnnul(untilAffixes),
                StepKind.Fracture => rarity == Rarity.Rare && explicitCount >= 4,
                StepKind.Chance => rarity == Rarity.Normal,
                StepKind.Artificer => true,
                StepKind.Quality => quality < 20,
                _ => false,
            };
        }

        public static int Clicks(StepKind kind, int explicitCount, int untilAffixes, int quality)
        {
            return kind switch
            {
                StepKind.Exalt => ExaltClicks(explicitCount, untilAffixes),
                StepKind.Augment => AugmentClicks(explicitCount, untilAffixes),
                StepKind.Annul => AnnulClicks(explicitCount, untilAffixes),
                StepKind.Quality => quality < 20 ? 20 - quality : 0,
                _ => 1,
            };
        }

        public static int ExaltClicks(int explicitCount, int untilAffixes)
        {
            var n = ClampUntil(untilAffixes);
            var clicks = n - explicitCount;
            return clicks > 0 ? clicks : 0;
        }

        public static int AnnulClicks(int explicitCount, int untilAffixes)
        {
            var n = ClampAnnul(untilAffixes);
            var clicks = explicitCount - n;
            return clicks > 0 ? clicks : 0;
        }

        public static int AugmentClicks(int explicitCount, int untilAffixes)
        {
            var n = ClampAugment(untilAffixes);
            var clicks = n - explicitCount;
            return clicks > 0 ? clicks : 0;
        }

        public static int ClampUntil(int untilAffixes) => Math.Clamp(untilAffixes, 3, 6);

        public static int ClampAnnul(int untilAffixes) => Math.Clamp(untilAffixes, 0, 5);

        public static int ClampAugment(int untilAffixes) => Math.Clamp(untilAffixes, 1, 2);

        public static bool MatchesConds(IReadOnlyList<string> names, CraftExpr expr, bool invert)
        {
            var hit = Eval(expr, names);
            return invert ? !hit : hit;
        }

        public static CraftExpr FromConds(List<CraftCond> conds)
        {
            if (conds.Count == 0)
            {
                return new CraftExpr { Items = { new CraftExpr() } };
            }

            CraftExpr acc = new() { Mod = conds[0].Mod };
            for (var i = 1; i < conds.Count; i++)
            {
                var leaf = new CraftExpr { Mod = conds[i].Mod };
                if (acc.Items.Count > 0 && acc.All == conds[i].And)
                {
                    acc.Items.Add(leaf);
                    continue;
                }

                acc = new CraftExpr { All = conds[i].And, Items = { acc, leaf } };
            }

            if (acc.Items.Count == 0)
            {
                return new CraftExpr { Items = { acc } };
            }

            return acc;
        }

        private static bool Eval(CraftExpr expr, IReadOnlyList<string> names)
        {
            if (expr.Items.Count == 0)
            {
                var hit = HasMod(names, expr.Mod);
                return expr.Not ? !hit : hit;
            }

            if (expr.All)
            {
                foreach (var item in expr.Items)
                {
                    if (!Eval(item, names))
                    {
                        return false;
                    }
                }

                return true;
            }

            foreach (var item in expr.Items)
            {
                if (Eval(item, names))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasMod(IReadOnlyList<string> names, string needle)
        {
            if (string.IsNullOrWhiteSpace(needle))
            {
                return false;
            }

            foreach (var name in names)
            {
                if (FamilyId(name).Equals(needle, StringComparison.OrdinalIgnoreCase) ||
                    Contains(name, needle))
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

        private static bool IsCatalogId(string needle)
        {
            foreach (var mod in Mods)
            {
                if (mod.Id.Equals(needle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

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

        private static bool Contains(string text, string needle) =>
            !string.IsNullOrEmpty(text) &&
            !string.IsNullOrEmpty(needle) &&
            text.Contains(needle, StringComparison.OrdinalIgnoreCase);

        public static void SelfCheck()
        {
            if (!IsWaystone("Metadata/Items/Maps/Waystone11", "Waystone11") ||
                !TryGet("OmenOnChaosMapPackSize", out _) ||
                !TryGet("CurrencyUpgradeToMagic3", out _) ||
                !TryGet("CurrencyRemoveMod", out _) ||
                !TryGet("CurrencyFlaskQuality", out _) ||
                !TryGet("CurrencyIdentification", out _) ||
                !TryGet("OmenOnChaosLowestLevelMod", out _) ||
                TryGet("CurrencyUpgradeToRare2", out _))
            {
                throw new InvalidOperationException("catalog");
            }

            if (IsEligible(StepKind.Alchemy, Rarity.Rare, 4, false, 6) ||
                !IsEligible(StepKind.Alchemy, Rarity.Normal, 0, false, 6) ||
                IsEligible(StepKind.Exalt, Rarity.Rare, 6, false, 6) ||
                !IsEligible(StepKind.Exalt, Rarity.Rare, 4, false, 6) ||
                IsEligible(StepKind.Vaal, Rarity.Rare, 4, true, 6) ||
                ExaltClicks(4, 6) != 2 ||
                ExaltClicks(6, 6) != 0 ||
                !IsEligible(StepKind.Transmute, Rarity.Normal, 0, false, 6) ||
                IsEligible(StepKind.Transmute, Rarity.Magic, 1, false, 6) ||
                !IsEligible(StepKind.Augment, Rarity.Magic, 1, false, 2) ||
                IsEligible(StepKind.Augment, Rarity.Magic, 2, false, 2) ||
                IsEligible(StepKind.Augment, Rarity.Normal, 0, false, 2) ||
                !IsEligible(StepKind.Augment, Rarity.Magic, 0, false, 1) ||
                IsEligible(StepKind.Augment, Rarity.Magic, 1, false, 1) ||
                AugmentClicks(0, 2) != 2 ||
                AugmentClicks(1, 2) != 1 ||
                ClampAugment(6) != 2 ||
                !IsEligible(StepKind.Annul, Rarity.Rare, 6, false, 4) ||
                IsEligible(StepKind.Annul, Rarity.Rare, 4, false, 4) ||
                ClampAnnul(6) != 5 ||
                ClampAnnul(-1) != 0 ||
                AnnulClicks(6, 4) != 2 ||
                AnnulClicks(3, 0) != 3 ||
                !IsEligible(StepKind.Annul, Rarity.Magic, 1, false, 0) ||
                IsEligible(StepKind.Annul, Rarity.Rare, 5, false, 5) ||
                !IsEligible(StepKind.Fracture, Rarity.Rare, 4, false, 6) ||
                IsEligible(StepKind.Fracture, Rarity.Rare, 3, false, 6) ||
                !IsEligible(StepKind.Chance, Rarity.Normal, 0, false, 6) ||
                IsEligible(StepKind.Chance, Rarity.Magic, 1, false, 6) ||
                !IsEligible(StepKind.Quality, Rarity.Rare, 4, false, 6, 10) ||
                IsEligible(StepKind.Quality, Rarity.Rare, 4, false, 6, 20) ||
                Clicks(StepKind.Quality, 0, 6, 15) != 5 ||
                IsEligible(StepKind.Identify, Rarity.Rare, 4, false, 6, 0, true) ||
                !IsEligible(StepKind.Identify, Rarity.Rare, 4, false, 6, 0, false) ||
                !IsEligible(StepKind.Identify, Rarity.Normal, 0, true, 6, 0, false))
            {
                throw new InvalidOperationException("craft rules");
            }

            if (!MatchesTarget("MapKeyTier15", "Metadata/Items/Maps/MapKeyTier15", "MapKeyTier15", "Waystone (Tier 15)") ||
                MatchesTarget("MapKeyTier1", "Metadata/Items/Maps/MapKeyTier15", "MapKeyTier15", "Waystone (Tier 15)") ||
                MatchesTarget("MapKeyTier15", "Metadata/Items/Maps/MapKeyTier11", "MapKeyTier11", "Waystone (Tier 11)") ||
                MatchesTarget("MapKeyTier15", "Metadata/Items/Armours/BodyArmours/Foo", "Foo", "Foo") ||
                !CanApply(MustGet("CurrencyArmourQuality"), "Metadata/Items/Armours/BodyArmours/Foo") ||
                CanApply(MustGet("CurrencyArmourQuality"), "Metadata/Items/Maps/Waystone15") ||
                !CanApply(MustGet("CurrencyWeaponQuality"), "Metadata/Items/Weapons/TwoHandWeapons/Bows/Bow") ||
                CanApply(MustGet("CurrencyWeaponQuality"), "Metadata/Items/Weapons/OneHandWeapons/Wands/Wand") ||
                !MatchesConds(["Foo", "Bar"], new CraftExpr { All = false, Items = { new() { Mod = "Foo" }, new() { Mod = "Nope" } } }, false) ||
                MatchesConds(["Foo"], new CraftExpr { Items = { new() { Mod = "Foo" }, new() { Mod = "Bar" } } }, false) ||
                !MatchesConds(["Foo"], new CraftExpr { Items = { new() { Mod = "Foo" }, new() { Mod = "Bar" } } }, true) ||
                MatchesConds(["Bar"], new CraftExpr { Items = { new() { Mod = "Foo" }, new() { All = false, Items = { new() { Mod = "Bar" }, new() { Mod = "Baz" } } } } }, false) ||
                !MatchesConds(["Foo", "Bar"], new CraftExpr { Items = { new() { Mod = "Foo" }, new() { All = false, Items = { new() { Mod = "Bar" }, new() { Mod = "Baz" } } } } }, false) ||
                !MatchesConds(["TowerDroppedItemRarityIncrease3"], new CraftExpr { Mod = "TowerDroppedItemRarityIncrease" }, false) ||
                MatchesConds(["TowerAdditionalEssenceChance"], new CraftExpr { Mod = "TowerAdditionalEssence" }, false) ||
                (TryCatalog("TowerDroppedItemRarityIncrease3", out var rarityMod) &&
                    !string.IsNullOrEmpty(rarityMod.ZhCN) &&
                    !MatchesConds(["TowerDroppedItemRarityIncrease3"], new CraftExpr { Mod = "稀有度" }, false)) ||
                MatchesConds(["TowerDroppedItemRarityIncrease3"], new CraftExpr { Mod = "TowerDropped" }, false) ||
                MatchesConds(["Foo"], new CraftExpr { Mod = "Foo", Not = true }, false) ||
                !MatchesConds(["Bar"], new CraftExpr { Mod = "Foo", Not = true }, false) ||
                !MatchesAny(Array.Empty<string>(), "Metadata/Items/TowerAugment/GenericAugment", "GenericAugment", "Irradiated Tablet") ||
                !MatchesTarget("GenericAugment", "Metadata/Items/TowerAugment/GenericAugment", "GenericAugment", "Collector's Irradiated Tablet") ||
                !MatchesTarget("BreachAugment", "Metadata/Items/TowerAugment/BreachAugment", "BreachAugment", "Breach Tablet") ||
                MatchesTarget("MapKeyTier15", "Metadata/Items/TowerAugment/BreachAugment", "BreachAugment", "Breach Tablet"))
            {
                throw new InvalidOperationException("craft targets");
            }
        }

        private static CurrencyInfo MustGet(string id)
        {
            if (!TryGet(id, out var info))
            {
                throw new InvalidOperationException(id);
            }

            return info;
        }

        private static bool MatchesQualityItem(string internalName, string path)
        {
            if (internalName.Equals("CurrencyArmourQuality", StringComparison.OrdinalIgnoreCase))
            {
                return IsArmour(path);
            }

            if (internalName.Equals("CurrencyWeaponQuality", StringComparison.OrdinalIgnoreCase))
            {
                return IsMartialWeapon(path);
            }

            if (internalName.Equals("CurrencyMagicQuality", StringComparison.OrdinalIgnoreCase))
            {
                return IsCasterWeapon(path);
            }

            if (internalName.Equals("CurrencyFlaskQuality", StringComparison.OrdinalIgnoreCase))
            {
                return IsFlask(path);
            }

            return false;
        }

        private static bool IsArtificerItem(string path) =>
            IsArmour(path) || IsMartialWeapon(path) || IsWand(path) || IsStaff(path);

        private static bool IsArmour(string path) =>
            path.Contains("Armours", StringComparison.OrdinalIgnoreCase);

        private static bool IsFlask(string path) =>
            path.Contains("Flask", StringComparison.OrdinalIgnoreCase);

        private static bool IsWand(string path) =>
            path.Contains("Wand", StringComparison.OrdinalIgnoreCase);

        private static bool IsStaff(string path) =>
            path.Contains("Staff", StringComparison.OrdinalIgnoreCase) &&
            !path.Contains("Quarterstaff", StringComparison.OrdinalIgnoreCase);

        private static bool IsSceptre(string path) =>
            path.Contains("Sceptre", StringComparison.OrdinalIgnoreCase);

        private static bool IsCasterWeapon(string path) =>
            IsWand(path) || IsStaff(path) || IsSceptre(path);

        private static bool IsMartialWeapon(string path) =>
            path.Contains("Weapons", StringComparison.OrdinalIgnoreCase) && !IsCasterWeapon(path);
    }
}
