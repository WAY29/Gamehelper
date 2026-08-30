namespace LootValue
{
    using System;
    using System.Collections.Generic;
    using GameHelper.Data;

    /// <summary>
    /// English ↔ zh-CN/zh-TW item names from the core item catalog.
    /// </summary>
    internal static class ItemLocalization
    {
        private static readonly Dictionary<string, Locale> ByEnglish = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> KeysByPrefix = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> ToEnglish = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime loadedExtracted;
        private static DateTime loadedNames;

        public static void Load(string pluginDirectory)
        {
            Reload(force: true);
        }

        public static string ResolveEnglish(string name)
        {
            Reload(force: false);
            if (string.IsNullOrWhiteSpace(name)) return name;
            if (ToEnglish.TryGetValue(name.Trim(), out var english)) return english;
            return ItemCatalog.ResolveEnglish(name);
        }

        public static IEnumerable<string> NamesFor(string? englishName, string? localizedBase)
        {
            Reload(force: false);
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

        private static void Reload(bool force)
        {
            ItemCatalog.Touch();
            if (!force &&
                loadedExtracted == ItemCatalog.ExtractedUtc &&
                loadedNames == ItemCatalog.NamesUtc)
            {
                return;
            }

            loadedExtracted = ItemCatalog.ExtractedUtc;
            loadedNames = ItemCatalog.NamesUtc;
            ByEnglish.Clear();
            KeysByPrefix.Clear();
            ToEnglish.Clear();

            foreach (var row in ItemCatalog.ItemsWherePathContains("Metadata/Items/"))
            {
                if (string.IsNullOrWhiteSpace(row.English)) continue;
                var en = row.English.Trim();
                var zhCn = row.ZhCn?.Trim() ?? string.Empty;
                var zhTw = row.ZhTw?.Trim() ?? string.Empty;
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
            public string ZhCn { get; set; } = string.Empty;
            public string ZhTw { get; set; } = string.Empty;
        }
    }
}
