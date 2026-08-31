namespace ReforgeHelper
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;

    internal readonly record struct NamedPos(string InternalName, Vector2 Pos);

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

        public static bool TryTakeThree(IReadOnlyList<NamedPos> matches, out NamedPos[] three)
        {
            three = [];
            if (matches.Count < 3)
            {
                return false;
            }

            var ordered = new List<NamedPos>(matches);
            ordered.Sort(static (a, b) =>
            {
                var dy = a.Pos.Y.CompareTo(b.Pos.Y);
                return dy != 0 ? dy : a.Pos.X.CompareTo(b.Pos.X);
            });
            three = [ordered[0], ordered[1], ordered[2]];
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

            var two = new[]
            {
                new NamedPos("A", new Vector2(0, 0)),
                new NamedPos("A", new Vector2(10, 0)),
            };
            if (TryTakeThree(two, out _))
            {
                throw new InvalidOperationException("fewer than 3 must not click");
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

            if (!TryTakeThree(matched, out var three) ||
                three.Length != 3 ||
                three[0].Pos != new Vector2(0, 0) ||
                three[1].Pos != new Vector2(10, 0) ||
                three[2].Pos != new Vector2(20, 0))
            {
                throw new InvalidOperationException("must pick the top-left three matching items");
            }
        }
    }
}
