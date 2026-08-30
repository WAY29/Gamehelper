namespace Atlas2
{
    using System;
    using System.Collections.Generic;
    using GameHelper.Data;
    using GameHelper.Localization;
    using GameHelper.Utils;

    internal static class AreaLocalization
    {
        private static Dictionary<string, Locale> byId = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, Locale> byAny = new(StringComparer.OrdinalIgnoreCase);
        private static List<(string English, string Display)> uniqueNames = [];
        private static DateTime loadedExtracted;
        private static DateTime loadedNames;
        private static int uniqueLang = int.MinValue;
        private static OverlayLanguage uniqueOverlay;

        public static void Load(string pluginDirectory)
        {
            Reload(force: true);
        }

        public static string DisplayName(string mapIdOrEnglish, string fallback, int language)
        {
            Reload(force: false);
            var loc = Resolve(mapIdOrEnglish) ?? Resolve(fallback);
            if (loc == null)
            {
                return fallback;
            }

            var mode = language;
            if (mode == 0)
            {
                mode = OverlayLocalization.CurrentLanguage switch
                {
                    OverlayLanguage.ChineseSimplified => 2,
                    OverlayLanguage.ChineseTraditional => 3,
                    _ => 1,
                };
            }

            var name = mode switch
            {
                2 => loc.ZhCn,
                3 => loc.ZhTw,
                _ => loc.En,
            };
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        public static IReadOnlyList<(string English, string Display)> UniqueNames(int language)
        {
            Reload(force: false);
            var overlay = OverlayLocalization.CurrentLanguage;
            if (uniqueNames.Count > 0 && uniqueLang == language && uniqueOverlay == overlay)
            {
                return uniqueNames;
            }

            uniqueLang = language;
            uniqueOverlay = overlay;
            uniqueNames = [];
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in byId)
            {
                var english = string.IsNullOrWhiteSpace(kv.Value.En) ? kv.Key : kv.Value.En;
                if (!seen.Add(english))
                {
                    continue;
                }

                uniqueNames.Add((english, DisplayName(kv.Key, english, language)));
            }

            uniqueNames.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
            return uniqueNames;
        }

        public static IEnumerable<string> Aliases(string mapId, string fallback)
        {
            Reload(force: false);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Add(names, mapId);
            Add(names, fallback);
            Add(names, WorldAreaNames.GetDisplayName(mapId));

            var loc = Resolve(mapId) ?? Resolve(fallback) ?? Resolve(WorldAreaNames.GetDisplayName(mapId));
            if (loc != null)
            {
                Add(names, loc.En);
                Add(names, loc.ZhCn);
                Add(names, loc.ZhTw);
            }

            return names;
        }

        private static void Reload(bool force)
        {
            ItemCatalog.Touch();
            if (!force &&
                loadedExtracted == ItemCatalog.ExtractedUtc &&
                loadedNames == ItemCatalog.NamesUtc &&
                byId.Count > 0)
            {
                return;
            }

            loadedExtracted = ItemCatalog.ExtractedUtc;
            loadedNames = ItemCatalog.NamesUtc;
            byId = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
            byAny = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
            uniqueNames = [];
            uniqueLang = int.MinValue;

            foreach (var row in ItemCatalog.SnapshotAreas())
            {
                if (string.IsNullOrEmpty(row.Id) || !row.Id.StartsWith("Map", StringComparison.Ordinal))
                {
                    continue;
                }

                var loc = new Locale
                {
                    En = string.IsNullOrEmpty(row.English) ? WorldAreaNames.GetDisplayName(row.Id) : row.English,
                    ZhCn = row.ZhCn ?? string.Empty,
                    ZhTw = row.ZhTw ?? string.Empty,
                };
                byId[row.Id] = loc;
                Link(row.Id, loc);
                Link(loc.En, loc);
                Link(loc.ZhCn, loc);
                Link(loc.ZhTw, loc);
            }
        }

        private static Locale Resolve(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            key = key.Trim();
            if (byId.TryGetValue(key, out var loc))
            {
                return loc;
            }

            return byAny.GetValueOrDefault(key);
        }

        private static void Link(string name, Locale loc)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                byAny[name.Trim()] = loc;
            }
        }

        private static void Add(HashSet<string> names, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                names.Add(value.Trim());
            }
        }

        private sealed class Locale
        {
            public string En { get; set; } = string.Empty;
            public string ZhCn { get; set; } = string.Empty;
            public string ZhTw { get; set; } = string.Empty;
        }
    }
}
