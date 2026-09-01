namespace ReforgeHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    internal readonly record struct NamedPos(string InternalName, Vector2 Pos, int Count = 1);

    internal readonly record struct WellRect(Vector2 Pos, Vector2 Size);

    internal static class ReforgeLogic
    {
        public static string PathTail(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        public static bool Matches(
            string target,
            string internalName,
            string path = "",
            string displayName = "")
        {
            if (string.IsNullOrEmpty(target))
            {
                return false;
            }

            return Eq(target, internalName) ||
                   Eq(target, displayName) ||
                   Eq(target, path) ||
                   Eq(target, PathTail(path)) ||
                   (!string.IsNullOrEmpty(path) &&
                    path.EndsWith("/" + target, StringComparison.OrdinalIgnoreCase));
        }

        private static bool Eq(string a, string b) =>
            !string.IsNullOrEmpty(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);

        public static bool ShouldFeed(bool identified) => identified;

        internal static readonly string[] PresetPathFragments =
        {
            "/TowerAugment/",
            "CurrencyJewelleryQuality",
            "CurrencyJewelQuality",
            "DistilledEmotion",
        };

        public static bool IsPresetPath(string path) => PresetGroup(path) < int.MaxValue;

        public static int PresetGroup(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return int.MaxValue;
            }

            if (path.Contains("/TowerAugment/", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (path.Contains("CurrencyJewelleryQuality", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("CurrencyJewelQuality", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (path.Contains("DistilledEmotion", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return int.MaxValue;
        }

        public static int StackCount(bool hasItem, int? stack)
        {
            if (!hasItem)
            {
                return 0;
            }

            return stack is int n && n > 0 ? n : 1;
        }

        public static bool CanReforge(int total) => total >= 3;

        public static bool IsExhausted(int wellTotal, int inventoryCount) =>
            wellTotal + Math.Max(0, inventoryCount) < 3;

        public static bool OccupiedIsCurrent(
            bool occupied,
            string target,
            string internalName,
            string path = "",
            string displayName = "") =>
            !occupied || Matches(target, internalName, path, displayName);

        public static bool TryAddUnique(List<string> list, string id)
        {
            if (list == null || string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            foreach (var existing in list)
            {
                if (string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            list.Add(id);
            return true;
        }

        public static List<string> NormalizeTargets(IEnumerable<string>? names, string? legacy)
        {
            var list = new List<string>();
            if (names != null)
            {
                foreach (var name in names)
                {
                    TryAddUnique(list, name);
                }
            }

            if (list.Count == 0)
            {
                TryAddUnique(list, legacy ?? string.Empty);
            }

            return list;
        }

        public static bool TryTakeUntil(IReadOnlyList<NamedPos> matches, int need, out NamedPos[] taken)
        {
            taken = [];
            if (need <= 0)
            {
                return true;
            }

            var ordered = new List<NamedPos>(matches);
            ordered.Sort(static (a, b) =>
            {
                var dy = a.Pos.Y.CompareTo(b.Pos.Y);
                return dy != 0 ? dy : a.Pos.X.CompareTo(b.Pos.X);
            });
            var picked = new List<NamedPos>();
            var got = 0;
            foreach (var item in ordered)
            {
                picked.Add(item);
                got += StackCount(true, item.Count);
                if (got >= need)
                {
                    taken = picked.ToArray();
                    return true;
                }
            }

            return false;
        }

        public static bool RectsOverlap(Vector2 aPos, Vector2 aSize, Vector2 bPos, Vector2 bSize) =>
            aPos.X < bPos.X + bSize.X &&
            aPos.X + aSize.X > bPos.X &&
            aPos.Y < bPos.Y + bSize.Y &&
            aPos.Y + aSize.Y > bPos.Y;

        public static bool TryPickThreeInputs(
            IReadOnlyList<WellRect> wells,
            WellRect output,
            out int[] idx)
        {
            idx = [];
            if (wells.Count == 0)
            {
                return false;
            }

            var order = new List<int>(wells.Count);
            for (var i = 0; i < wells.Count; i++)
            {
                order.Add(i);
            }

            order.Sort((a, b) =>
            {
                var da = wells[a].Size.X * wells[a].Size.Y;
                var db = wells[b].Size.X * wells[b].Size.Y;
                return da.CompareTo(db);
            });

            var kept = new List<int>();
            foreach (var i in order)
            {
                var well = wells[i];
                if (RectsOverlap(well.Pos, well.Size, output.Pos, output.Size))
                {
                    continue;
                }

                var overlapKept = false;
                foreach (var k in kept)
                {
                    if (RectsOverlap(well.Pos, well.Size, wells[k].Pos, wells[k].Size))
                    {
                        overlapKept = true;
                        break;
                    }
                }

                if (!overlapKept)
                {
                    kept.Add(i);
                }
            }

            if (kept.Count != 3)
            {
                return false;
            }

            kept.Sort((a, b) => wells[a].Pos.X.CompareTo(wells[b].Pos.X));
            idx = [kept[0], kept[1], kept[2]];
            return true;
        }

        public static void SelfCheck()
        {
            if (ShouldFeed(false) || !ShouldFeed(true))
            {
                throw new InvalidOperationException("unidentified items must not be fed");
            }

            if (Matches(string.Empty, "Tablet"))
            {
                throw new InvalidOperationException("empty target must not match");
            }

            if (!Matches("TabletA", "tableta"))
            {
                throw new InvalidOperationException("InternalName match is case-insensitive");
            }

            if (Matches("TabletA", "TabletB"))
            {
                throw new InvalidOperationException("different InternalName must not match");
            }

            if (!Matches(
                    "GenericAugment",
                    "TowerAugmentGeneric_",
                    "Metadata/Items/TowerAugment/GenericAugment",
                    string.Empty))
            {
                throw new InvalidOperationException("must match catalog id via item path");
            }

            if (Matches(
                    "GenericAugment",
                    "AbyssAugment",
                    "Metadata/Items/TowerAugment/AbyssAugment",
                    string.Empty))
            {
                throw new InvalidOperationException("different tablet path must not match");
            }

            if (!IsPresetPath("Metadata/Items/TowerAugment/GenericAugment") ||
                !IsPresetPath("Metadata/Items/Currency/CurrencyJewelleryQualityLife") ||
                !IsPresetPath("Metadata/Items/Currency/CurrencyJewelQualityLife") ||
                !IsPresetPath("Metadata/Items/Currency/DistilledEmotion1") ||
                !IsPresetPath("Metadata/Items/Currency/EndgameDistilledEmotionTimeLost3"))
            {
                throw new InvalidOperationException("tablet, catalyst, and liquid emotion paths must be presets");
            }

            if (IsPresetPath("Metadata/Items/Currency/CurrencyRerollRemnant") ||
                IsPresetPath("Metadata/Items/Currency/CurrencyEssenceLife") ||
                IsPresetPath("Metadata/Items/TowerAugments/TowerAugment"))
            {
                throw new InvalidOperationException("unrelated currency must not be a preset");
            }

            if (PresetGroup("Metadata/Items/TowerAugment/GenericAugment") != 0 ||
                PresetGroup("Metadata/Items/Currency/CurrencyJewelleryQualityLife") != 1 ||
                PresetGroup("Metadata/Items/Currency/CurrencyJewelQualityLife") != 1 ||
                PresetGroup("Metadata/Items/Currency/DistilledEmotion1") != 2)
            {
                throw new InvalidOperationException("presets must group tablet, catalyst, then liquid emotion");
            }

            if (StackCount(false, 10) != 0 ||
                StackCount(true, null) != 1 ||
                StackCount(true, 10) != 10 ||
                StackCount(true, 0) != 1)
            {
                throw new InvalidOperationException("no item=0, no stack=1, stack uses Count");
            }

            if (CanReforge(2) || !CanReforge(3) || !CanReforge(10))
            {
                throw new InvalidOperationException("reforge when total >= 3");
            }

            if (!IsExhausted(2, 0) || !IsExhausted(0, 2) || !IsExhausted(1, 1) ||
                IsExhausted(2, 1) || IsExhausted(0, 3) || IsExhausted(3, 0))
            {
                throw new InvalidOperationException("exhausted when well+inventory < 3");
            }

            if (!OccupiedIsCurrent(false, "A", string.Empty) ||
                !OccupiedIsCurrent(true, "A", "A") ||
                OccupiedIsCurrent(true, "A", string.Empty) ||
                OccupiedIsCurrent(true, "A", "B"))
            {
                throw new InvalidOperationException("empty well is current; occupied must match; unread is not current");
            }

            var targets = new List<string> { "A" };
            if (TryAddUnique(targets, "A") ||
                TryAddUnique(targets, "a") ||
                TryAddUnique(targets, " ") ||
                !TryAddUnique(targets, "B") ||
                targets.Count != 2)
            {
                throw new InvalidOperationException("add unique InternalName, skip dup/empty");
            }

            var migrated = NormalizeTargets(null, "Old");
            if (migrated.Count != 1 || migrated[0] != "Old")
            {
                throw new InvalidOperationException("empty list must migrate legacy target");
            }

            var kept = NormalizeTargets(new[] { "A", "", "A", "B" }, "Old");
            if (kept.Count != 2 || kept[0] != "A" || kept[1] != "B")
            {
                throw new InvalidOperationException("non-empty list keeps order, skips dup/empty, ignores legacy");
            }

            if (!TryTakeUntil([], 0, out var none) || none.Length != 0)
            {
                throw new InvalidOperationException("need=0 must succeed with no items");
            }

            var two = new[]
            {
                new NamedPos("A", new Vector2(0, 0)),
                new NamedPos("A", new Vector2(10, 0)),
            };
            if (TryTakeUntil(two, 3, out _))
            {
                throw new InvalidOperationException("count 1+1 must not cover need 3");
            }

            if (!TryTakeUntil(two, 1, out var one) ||
                one.Length != 1 ||
                one[0].Pos != new Vector2(0, 0))
            {
                throw new InvalidOperationException("need=1 must pick the top-left item");
            }

            var stacked = new[] { new NamedPos("A", new Vector2(40, 20), 10) };
            if (!TryTakeUntil(stacked, 3, out var oneStack) || oneStack.Length != 1)
            {
                throw new InvalidOperationException("one stack of 10 must cover need 3");
            }

            var five = new[]
            {
                new NamedPos("A", new Vector2(40, 20)),
                new NamedPos("B", new Vector2(0, 0)),
                new NamedPos("A", new Vector2(10, 0)),
                new NamedPos("A", new Vector2(0, 0)),
                new NamedPos("A", new Vector2(20, 0)),
            };
            var matched = new List<NamedPos>();
            foreach (var item in five)
            {
                if (Matches("A", item.InternalName))
                {
                    matched.Add(item);
                }
            }

            if (!TryTakeUntil(matched, 3, out var three) ||
                three.Length != 3 ||
                three[0].Pos != new Vector2(0, 0) ||
                three[1].Pos != new Vector2(10, 0) ||
                three[2].Pos != new Vector2(20, 0))
            {
                throw new InvalidOperationException("must pick the top-left items until need is covered");
            }

            var output = new WellRect(new Vector2(800, 0), new Vector2(200, 400));
            var wells = new[]
            {
                new WellRect(new Vector2(0, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(210, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(420, 0), new Vector2(200, 400)),
                output,
            };
            if (!TryPickThreeInputs(wells, output, out var picked) ||
                picked.Length != 3 ||
                picked[0] != 0 ||
                picked[1] != 1 ||
                picked[2] != 2)
            {
                throw new InvalidOperationException("must pick three inputs left-to-right and skip output");
            }

            var nested = new[]
            {
                new WellRect(new Vector2(0, 0), new Vector2(220, 420)),
                new WellRect(new Vector2(10, 10), new Vector2(200, 400)),
                new WellRect(new Vector2(210, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(420, 0), new Vector2(200, 400)),
            };
            if (!TryPickThreeInputs(nested, output, out var deduped) ||
                deduped.Length != 3 ||
                deduped[0] != 1 ||
                deduped[1] != 2 ||
                deduped[2] != 3)
            {
                throw new InvalidOperationException("must keep the inner well when rects overlap");
            }

            if (TryPickThreeInputs(twoWells(), output, out _))
            {
                throw new InvalidOperationException("fewer than 3 inputs must fail");
            }

            var four = new[]
            {
                new WellRect(new Vector2(0, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(210, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(420, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(630, 0), new Vector2(200, 400)),
            };
            if (TryPickThreeInputs(four, output, out _))
            {
                throw new InvalidOperationException("more than 3 inputs must fail");
            }

            static WellRect[] twoWells() =>
            [
                new WellRect(new Vector2(0, 0), new Vector2(200, 400)),
                new WellRect(new Vector2(210, 0), new Vector2(200, 400)),
            ];
        }
    }
}
