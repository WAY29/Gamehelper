namespace LootValue
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Newtonsoft.Json;

    /// <summary>
    /// English ↔ zh-CN/zh-TW item names from poe2db.tw.
    /// Refresh: <c>python3 Plugins/LootValue/scripts/fetch_item_localization.py</c>.
    /// </summary>
    internal static class ItemLocalization
    {
        private static readonly Dictionary<string, Locale> ByEnglish = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> KeysByPrefix = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ToEnglish = new(StringComparer.OrdinalIgnoreCase);

        public static void Load(string pluginDirectory)
        {
            ByEnglish.Clear();
            KeysByPrefix.Clear();
            ToEnglish.Clear();

            var path = Path.Combine(pluginDirectory, "item-localization.json");
            if (!File.Exists(path)) return;

            Dictionary<string, Locale>? items;
            try
            {
                items = JsonConvert.DeserializeObject<Dictionary<string, Locale>>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LootValue] Failed to load item-localization.json: {ex.Message}");
                return;
            }

            if (items == null) return;

            foreach (var (english, loc) in items)
            {
                if (string.IsNullOrWhiteSpace(english) || loc == null) continue;
                var en = english.Trim();
                var zhCn = string.IsNullOrWhiteSpace(loc.ZhCn) ? string.Empty : loc.ZhCn.Trim();
                var zhTw = string.IsNullOrWhiteSpace(loc.ZhTw) ? string.Empty : loc.ZhTw.Trim();
                ByEnglish[en] = new Locale { ZhCn = zhCn, ZhTw = zhTw };
                IndexName(en, en);
                IndexName(zhCn, en);
                IndexName(zhTw, en);

                var space = 0;
                while ((space = en.IndexOf(' ', space)) > 0)
                {
                    var prefix = en[..space];
                    if (prefix.Length >= 4)
                    {
                        if (!KeysByPrefix.TryGetValue(prefix, out var list))
                        {
                            list = new List<string>();
                            KeysByPrefix[prefix] = list;
                        }

                        if (!list.Contains(en)) list.Add(en);
                    }

                    space++;
                }
            }
        }

        public static string ResolveEnglish(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            return ToEnglish.TryGetValue(name.Trim(), out var english) ? english : name;
        }

        public static IEnumerable<string> NamesFor(string? englishName, string? localizedBase)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(names, englishName);
            Add(names, localizedBase);
            if (string.IsNullOrWhiteSpace(englishName)) return names;

            if (ByEnglish.TryGetValue(englishName, out var exact))
                AddLocale(names, englishName, exact, localizedBase);

            if (KeysByPrefix.TryGetValue(englishName, out var keys))
            {
                foreach (var key in keys)
                {
                    if (ByEnglish.TryGetValue(key, out var loc))
                        AddLocale(names, key, loc, localizedBase);
                }
            }

            return names;
        }

        private static void AddLocale(HashSet<string> names, string english, Locale loc, string? localizedBase)
        {
            Add(names, english);
            Add(names, loc.ZhCn);
            Add(names, loc.ZhTw);
            Add(names, StripBase(loc.ZhCn, localizedBase));
            Add(names, StripBase(loc.ZhTw, localizedBase));
        }

        private static string? StripBase(string? localized, string? baseName)
        {
            if (string.IsNullOrWhiteSpace(localized) || string.IsNullOrWhiteSpace(baseName)) return null;
            var name = localized.Trim();
            var bas = baseName.Trim();
            if (name.Length <= bas.Length) return null;
            if (!name.EndsWith(bas, StringComparison.OrdinalIgnoreCase)) return null;
            return name[..^bas.Length].Trim();
        }

        private static void IndexName(string? name, string english)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            ToEnglish[name.Trim()] = english;
        }

        private static void Add(HashSet<string> names, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) names.Add(value.Trim());
        }

        private sealed class Locale
        {
            [JsonProperty("zh_CN")]
            public string ZhCn { get; set; } = string.Empty;

            [JsonProperty("zh_TW")]
            public string ZhTw { get; set; } = string.Empty;
        }
    }
}
