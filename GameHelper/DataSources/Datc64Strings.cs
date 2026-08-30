namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Text;

    /// <summary>
    /// Schema-free scan of PoE2 datc64 string tables (UTF-16).
    /// BaseItemTypes packs Path then English name; Mods packs the mod Id.
    /// </summary>
    internal static class Datc64Strings
    {
        static Datc64Strings()
        {
            SelfCheck();
        }

        public static List<CatalogItem> ParseBaseItems(ReadOnlySpan<byte> data)
        {
            var strings = ReadUtf16Strings(data);
            var items = new List<CatalogItem>();
            for (var i = 0; i < strings.Count; i++)
            {
                var path = strings[i];
                if (!path.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    continue;
                }

                var english = string.Empty;
                if (i + 1 < strings.Count && !strings[i + 1].StartsWith("Metadata/", StringComparison.Ordinal))
                {
                    english = strings[i + 1];
                    i++;
                    if (english.Length <= 1)
                    {
                        english = string.Empty;
                    }
                }

                var slash = path.LastIndexOf('/');
                items.Add(new CatalogItem
                {
                    Path = path,
                    InternalName = slash >= 0 ? path[(slash + 1)..] : path,
                    English = english,
                });
            }

            return items;
        }

        public static List<string> ParseModIds(ReadOnlySpan<byte> data)
        {
            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in ReadUtf16Strings(data))
            {
                if (!LooksLikeModId(s) || !seen.Add(s))
                {
                    continue;
                }

                ids.Add(s);
            }

            return ids;
        }

        public static List<string> ParseModFamilies(ReadOnlySpan<byte> data)
        {
            var families = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ParseModIds(data))
            {
                var family = FamilyId(id);
                if (family.Length == 0 || !seen.Add(family))
                {
                    continue;
                }

                families.Add(family);
            }

            return families;
        }

        public static string FamilyId(string name)
        {
            while (name.Length > 0 && char.IsDigit(name[^1]))
            {
                name = name[..^1];
            }

            return name;
        }

        private static List<string> ReadUtf16Strings(ReadOnlySpan<byte> data)
        {
            var list = new List<string>();
            var i = 0;
            while (i + 3 < data.Length)
            {
                var c = (char)(data[i] | (data[i + 1] << 8));
                if (c is < (char)32 or '\uFFFD')
                {
                    i += 2;
                    continue;
                }

                var sb = new StringBuilder();
                while (i + 1 < data.Length)
                {
                    c = (char)(data[i] | (data[i + 1] << 8));
                    i += 2;
                    if (c == 0)
                    {
                        break;
                    }

                    sb.Append(c);
                }

                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                }
            }

            return list;
        }

        private static bool LooksLikeModId(string s)
        {
            if (s.Length < 6 || s.Length > 80)
            {
                return false;
            }

            if (s[0] is < 'A' or > 'Z')
            {
                return false;
            }

            var hasLower = false;
            foreach (var ch in s)
            {
                if (ch is >= 'a' and <= 'z')
                {
                    hasLower = true;
                    continue;
                }

                if (ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
                {
                    continue;
                }

                return false;
            }

            return hasLower;
        }

        private static void SelfCheck()
        {
            static byte[] Utf16(params string[] parts)
            {
                var bytes = new List<byte>();
                foreach (var part in parts)
                {
                    foreach (var ch in part)
                    {
                        bytes.Add((byte)ch);
                        bytes.Add(0);
                    }

                    bytes.Add(0);
                    bytes.Add(0);
                }

                return bytes.ToArray();
            }

            var items = ParseBaseItems(Utf16(
                "Metadata/Items/Currency/CurrencyRerollRare",
                "Chaos Orb",
                "Metadata/Items/Currency/StackableCurrency",
                "X"));
            if (items.Count != 2 ||
                items[0].InternalName != "CurrencyRerollRare" ||
                items[0].English != "Chaos Orb" ||
                items[1].English != string.Empty)
            {
                throw new InvalidOperationException("datc64 items");
            }

            if (FamilyId("TowerDroppedItemRarityIncrease1") != "TowerDroppedItemRarityIncrease")
            {
                throw new InvalidOperationException("datc64 family");
            }
        }
    }
}
