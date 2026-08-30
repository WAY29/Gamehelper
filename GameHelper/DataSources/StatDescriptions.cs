namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Text.RegularExpressions;

    internal static class StatDescriptions
    {
        private const int ModStat1Offset = 30;
        private const int ModStat1MinOffset = 126;
        private const int ModStat1MaxOffset = 130;

        private static readonly Regex Quoted = new("\"([^\"]*)\"", RegexOptions.Compiled);
        private static readonly Regex Tag = new(@"\[(?:[^|\]]+\|)?([^\]]+)\]", RegexOptions.Compiled);

        static StatDescriptions()
        {
            SelfCheck();
        }

        public static void Apply(
            List<CatalogMod> mods,
            ReadOnlySpan<byte> modsDat,
            ReadOnlySpan<byte> statsDat,
            params byte[][] csdFiles)
        {
            var text = Parse(csdFiles);
            if (text.Count == 0)
            {
                return;
            }

            var stats = ReadStatIds(statsDat);
            if (stats.Count == 0 || !TryDatHeader(modsDat, out var count, out var rowSize, out var bb))
            {
                return;
            }

            var byFamily = new Dictionary<string, CatalogMod>(StringComparer.OrdinalIgnoreCase);
            foreach (var mod in mods)
            {
                if (!string.IsNullOrEmpty(mod.Id))
                {
                    byFamily[mod.Id] = mod;
                }
            }

            for (var i = 0; i < count; i++)
            {
                var row = 4 + (i * rowSize);
                var id = ReadDatString(modsDat, bb, row);
                if (id.Length == 0)
                {
                    continue;
                }

                var family = Datc64Strings.FamilyId(id);
                if (family.Length == 0 || !byFamily.TryGetValue(family, out var mod))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(mod.English))
                {
                    continue;
                }

                if (row + ModStat1Offset + 4 > 4 + ((i + 1) * rowSize))
                {
                    continue;
                }

                var statIndex = BitConverter.ToInt32(modsDat.Slice(row + ModStat1Offset, 4));
                if ((uint)statIndex >= (uint)stats.Count)
                {
                    continue;
                }

                var statId = stats[statIndex];
                if (statId.Length == 0 ||
                    statId.StartsWith("dummy_stat", StringComparison.Ordinal) ||
                    !text.TryGetValue(statId, out var loc))
                {
                    continue;
                }

                var min = 0;
                var max = 0;
                if (row + ModStat1MaxOffset + 4 <= 4 + ((i + 1) * rowSize))
                {
                    min = BitConverter.ToInt32(modsDat.Slice(row + ModStat1MinOffset, 4));
                    max = BitConverter.ToInt32(modsDat.Slice(row + ModStat1MaxOffset, 4));
                }

                mod.English = FillRange(loc.English, min, max);
                mod.ZhCn = FillRange(loc.ZhCn, min, max);
                mod.ZhTw = FillRange(loc.ZhTw, min, max);
            }
        }

        public static Dictionary<string, CatalogText> Parse(params byte[][] files)
        {
            var map = new Dictionary<string, CatalogText>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (file is { Length: > 0 })
                {
                    ParseFile(file, map);
                }
            }

            return map;
        }

        private static void ParseFile(byte[] data, Dictionary<string, CatalogText> map)
        {
            var text = DecodeUtf16(data);
            var ids = new List<string>();
            var en = string.Empty;
            var zhCn = string.Empty;
            var zhTw = string.Empty;
            var lang = "English";
            var inDesc = false;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                var trimmed = line.TrimStart('\t', ' ');
                if (trimmed.StartsWith("no_description ", StringComparison.Ordinal))
                {
                    Flush(map, ids, en, zhCn, zhTw);
                    ids.Clear();
                    en = zhCn = zhTw = string.Empty;
                    lang = "English";
                    inDesc = false;
                    continue;
                }

                if (trimmed == "description")
                {
                    Flush(map, ids, en, zhCn, zhTw);
                    ids.Clear();
                    en = zhCn = zhTw = string.Empty;
                    lang = "English";
                    inDesc = true;
                    continue;
                }

                if (!inDesc)
                {
                    continue;
                }

                if (trimmed.StartsWith("lang \"", StringComparison.Ordinal))
                {
                    var end = trimmed.LastIndexOf('"');
                    lang = end > 6 ? trimmed[6..end] : "English";
                    continue;
                }

                if (ids.Count == 0 && LooksLikeStatList(trimmed))
                {
                    foreach (var part in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (part.Length > 1 && !char.IsDigit(part[0]))
                        {
                            ids.Add(part);
                        }
                    }

                    continue;
                }

                var quoted = Quoted.Match(line);
                if (!quoted.Success)
                {
                    continue;
                }

                var body = Clean(quoted.Groups[1].Value);
                if (body.Length == 0 || IsReduced(body))
                {
                    continue;
                }

                if (lang == "English" && en.Length == 0)
                {
                    en = body;
                }
                else if (lang == "Simplified Chinese" && zhCn.Length == 0)
                {
                    zhCn = body;
                }
                else if (lang == "Traditional Chinese" && zhTw.Length == 0)
                {
                    zhTw = body;
                }
            }

            Flush(map, ids, en, zhCn, zhTw);
        }

        private static void Flush(
            Dictionary<string, CatalogText> map,
            List<string> ids,
            string en,
            string zhCn,
            string zhTw)
        {
            if (ids.Count == 0 || en.Length == 0)
            {
                return;
            }

            foreach (var id in ids)
            {
                if (map.TryGetValue(id, out var old))
                {
                    old.English = en;
                    if (zhCn.Length > 0)
                    {
                        old.ZhCn = zhCn;
                    }

                    if (zhTw.Length > 0)
                    {
                        old.ZhTw = zhTw;
                    }
                }
                else
                {
                    map[id] = new CatalogText { English = en, ZhCn = zhCn, ZhTw = zhTw };
                }
            }
        }

        private static string FillRange(string text, int min, int max)
        {
            if (string.IsNullOrEmpty(text) || (min == 0 && max == 0))
            {
                return text;
            }

            if (max == 0)
            {
                max = min;
            }

            var n = min == max ? $"({min})" : $"({min}—{max})";
            text = text.Replace("{0:+d}", (min >= 0 && max >= 0 ? "+" : string.Empty) + n, StringComparison.Ordinal);
            return text.Replace("{0}", n, StringComparison.Ordinal);
        }

        private static bool LooksLikeStatList(string trimmed)
        {
            if (trimmed.Length < 3 || !char.IsDigit(trimmed[0]))
            {
                return false;
            }

            return trimmed.Contains('_') || trimmed.Contains('%');
        }

        private static bool IsReduced(string text) =>
            text.Contains(" reduced ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("降低") ||
            text.Contains("減少") ||
            text.Contains(" verringerte ", StringComparison.Ordinal);

        private static string Clean(string raw)
        {
            raw = raw.Replace("\\n", " ", StringComparison.Ordinal);
            raw = Tag.Replace(raw, "$1");
            return string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string DecodeUtf16(byte[] data)
        {
            var start = data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE ? 2 : 0;
            return Encoding.Unicode.GetString(data, start, data.Length - start);
        }

        private static List<string> ReadStatIds(ReadOnlySpan<byte> data)
        {
            var ids = new List<string>();
            if (!TryDatHeader(data, out var count, out var rowSize, out var bb))
            {
                return ids;
            }

            for (var i = 0; i < count; i++)
            {
                ids.Add(ReadDatString(data, bb, 4 + (i * rowSize)));
            }

            return ids;
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
            var body = Encoding.Unicode.GetBytes(
                "description\n" +
                "\t1 map_item_drop_rarity_+%\n" +
                "\t2\n" +
                "\t\t1|# \"{0}% increased [Rarity] of Items found\"\n" +
                "\t\t#|-1 \"{0}% reduced [Rarity] of Items found\" negate 1\n" +
                "\tlang \"Simplified Chinese\"\n" +
                "\t2\n" +
                "\t\t1|# \"该区域内物品[Rarity|稀有度]提高 {0}%\"\n" +
                "\tlang \"Traditional Chinese\"\n" +
                "\t2\n" +
                "\t\t1|# \"增加{0}%找到的物品[Rarity|稀有度]\"\n");
            var bom = Encoding.Unicode.GetPreamble();
            var csd = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, csd, 0, bom.Length);
            Buffer.BlockCopy(body, 0, csd, bom.Length, body.Length);

            var map = Parse(csd);
            if (!map.TryGetValue("map_item_drop_rarity_+%", out var loc) ||
                loc.English != "{0}% increased Rarity of Items found" ||
                !loc.ZhCn.Contains("稀有度") ||
                !loc.ZhTw.Contains("稀有度"))
            {
                throw new InvalidOperationException("csd parse");
            }

            var englishOnly = Encoding.Unicode.GetBytes(
                "description\n\t1 map_item_drop_rarity_+%\n\t1\n\t\t# \"{0}% increased Rarity of Items found\"\n");
            var later = new byte[bom.Length + englishOnly.Length];
            Buffer.BlockCopy(bom, 0, later, 0, bom.Length);
            Buffer.BlockCopy(englishOnly, 0, later, bom.Length, englishOnly.Length);
            map = Parse(csd, later);
            if (!map.TryGetValue("map_item_drop_rarity_+%", out loc) ||
                loc.ZhTw.Length == 0 ||
                loc.ZhCn.Length == 0)
            {
                throw new InvalidOperationException("csd merge");
            }

            if (FillRange("{0}% increased Rarity", 8, 12) != "(8\u201412)% increased Rarity" ||
                FillRange("{0:+d} to Life", 5, 5) != "+(5) to Life")
            {
                throw new InvalidOperationException("csd range");
            }
        }
    }
}
