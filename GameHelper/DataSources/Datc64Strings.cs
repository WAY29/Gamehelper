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
            CollectModIds(data, ids, seen);
            if (data.Length > 1)
            {
                CollectModIds(data[1..], ids, seen);
            }

            return ids;
        }

        private static void CollectModIds(
            ReadOnlySpan<byte> data,
            List<string> ids,
            HashSet<string> seen)
        {
            foreach (var s in ReadUtf16Strings(data))
            {
                if (!LooksLikeModId(s) || !seen.Add(s))
                {
                    continue;
                }

                ids.Add(s);
            }
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

        public static void ApplyArt(List<CatalogItem> items, ReadOnlySpan<byte> baseItems, ReadOnlySpan<byte> visuals)
        {
            if (!TryDatHeader(baseItems, out var bitCount, out var bitRow, out var bitBb) ||
                !TryDatHeader(visuals, out var iviCount, out var iviRow, out var iviBb) ||
                bitRow < 0x80 ||
                iviRow < 0x10)
            {
                return;
            }

            var art = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < bitCount; i++)
            {
                var row = 4 + (i * bitRow);
                var path = ReadDatString(baseItems, bitBb, row);
                if (!path.StartsWith("Metadata/Items/", StringComparison.Ordinal))
                {
                    continue;
                }

                var fk = BitConverter.ToInt32(baseItems.Slice(row + 0x7C, 4));
                if ((uint)fk >= (uint)iviCount)
                {
                    continue;
                }

                var dds = ReadDatString(visuals, iviBb, 4 + (fk * iviRow) + 8);
                var slash = dds.LastIndexOf('/');
                var file = slash >= 0 ? dds[(slash + 1)..] : dds;
                if (file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    file = file[..^4];
                }

                if (file.Length == 0)
                {
                    continue;
                }

                var nameSlash = path.LastIndexOf('/');
                var internalName = nameSlash >= 0 ? path[(nameSlash + 1)..] : path;
                art[internalName] = file;
            }

            if (!art.TryGetValue("CurrencyRerollRare", out var chaosArt) ||
                chaosArt != "CurrencyRerollRare")
            {
                return;
            }

            foreach (var item in items)
            {
                if (art.TryGetValue(item.InternalName, out var id))
                {
                    item.Art = id;
                }
            }
        }

        private static bool TryDatHeader(ReadOnlySpan<byte> data, out int count, out int rowSize, out int bb)
        {
            count = 0;
            rowSize = 0;
            bb = -1;
            if (data.Length < 16)
            {
                return false;
            }

            count = BitConverter.ToInt32(data[..4]);
            if (count <= 0 || count > 200_000)
            {
                return false;
            }

            ReadOnlySpan<byte> magic = [0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB, 0xBB];
            bb = data.IndexOf(magic);
            if (bb < 4 || (bb - 4) % count != 0)
            {
                return false;
            }

            rowSize = (bb - 4) / count;
            return rowSize >= 8;
        }

        private static string ReadDatString(ReadOnlySpan<byte> data, int bb, int fieldOffset)
        {
            if (fieldOffset < 0 || fieldOffset + 4 > data.Length)
            {
                return string.Empty;
            }

            var rel = BitConverter.ToInt32(data.Slice(fieldOffset, 4));
            var abs = bb + rel;
            if (rel <= 0 || abs < 0 || abs + 1 >= data.Length)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            var i = abs;
            while (i + 1 < data.Length)
            {
                var c = (char)(data[i] | (data[i + 1] << 8));
                i += 2;
                if (c == 0)
                {
                    break;
                }

                sb.Append(c);
                if (sb.Length > 180)
                {
                    break;
                }
            }

            return sb.ToString();
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

            var odd = new List<byte> { 0xFF };
            odd.AddRange(Utf16("TowerDroppedItemRarityIncrease3"));
            if (!ParseModIds(odd.ToArray()).Contains("TowerDroppedItemRarityIncrease3"))
            {
                throw new InvalidOperationException("datc64 mods");
            }

            var areas = ParseWorldAreas(Utf16("MapHiddenGrotto", "Hidden Grotto", "G1_1", "The Riverbank"));
            if (areas.Count != 2 ||
                areas[0].Id != "MapHiddenGrotto" ||
                areas[0].English != "Hidden Grotto" ||
                areas[1].Id != "G1_1")
            {
                throw new InvalidOperationException("datc64 areas");
            }

            OverlayItemZhTw(items, Utf16(
                "Metadata/Items/Currency/CurrencyRerollRare",
                "Chaos Orb TW",
                "Metadata/Items/Currency/StackableCurrency",
                "X"));
            if (items[0].ZhTw != "Chaos Orb TW")
            {
                throw new InvalidOperationException("datc64 zh-tw items");
            }
        }

        public static void OverlayItemZhTw(List<CatalogItem> items, ReadOnlySpan<byte> localized)
        {
            if (localized.Length == 0 || items.Count == 0)
            {
                return;
            }

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ParseBaseItems(localized))
            {
                if (row.InternalName.Length > 0 && row.English.Length > 0)
                {
                    names[row.InternalName] = row.English;
                }
            }

            foreach (var item in items)
            {
                if (names.TryGetValue(item.InternalName, out var zhTw))
                {
                    item.ZhTw = zhTw;
                }
            }
        }

        public static void OverlayAreaZhTw(List<CatalogArea> areas, ReadOnlySpan<byte> localized)
        {
            if (localized.Length == 0 || areas.Count == 0)
            {
                return;
            }

            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in ParseWorldAreas(localized))
            {
                if (row.Id.Length > 0 && row.English.Length > 0)
                {
                    names[row.Id] = row.English;
                }
            }

            foreach (var area in areas)
            {
                if (names.TryGetValue(area.Id, out var zhTw))
                {
                    area.ZhTw = zhTw;
                }
            }
        }

        public static List<CatalogArea> ParseWorldAreas(ReadOnlySpan<byte> data)
        {
            var strings = ReadUtf16Strings(data);
            var areas = new List<CatalogArea>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < strings.Count; i++)
            {
                var id = strings[i];
                if (!LooksLikeAreaId(id) || !seen.Add(id))
                {
                    continue;
                }

                var english = string.Empty;
                if (i + 1 < strings.Count && LooksLikeAreaName(strings[i + 1]))
                {
                    english = strings[i + 1];
                    i++;
                }

                areas.Add(new CatalogArea { Id = id, English = english });
            }

            return areas;
        }

        private static bool LooksLikeAreaId(string s)
        {
            if (s.Length < 3 || s.Length > 80 || s[0] is < 'A' or > 'Z')
            {
                return false;
            }

            foreach (var ch in s)
            {
                if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool LooksLikeAreaName(string s)
        {
            if (s.Length < 2 || s.Length > 80 || s.StartsWith("Metadata/", StringComparison.Ordinal))
            {
                return false;
            }

            if (s.Contains(' '))
            {
                return true;
            }

            return !LooksLikeAreaId(s) || (s[0] is >= 'A' and <= 'Z' && !s.StartsWith("Map", StringComparison.Ordinal));
        }
    }
}
