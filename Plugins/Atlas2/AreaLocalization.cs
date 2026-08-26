namespace Atlas2
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using GameHelper.Localization;
    using GameHelper.Utils;
    using Newtonsoft.Json;

    internal static class AreaLocalization
    {
        private static Dictionary<string, Locale> byId = new(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, Locale> byAny = new(StringComparer.OrdinalIgnoreCase);

        public static void Load(string pluginDirectory)
        {
            byId = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
            byAny = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(pluginDirectory, "json", "area-localization.json");
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                var items = JsonConvert.DeserializeObject<Dictionary<string, Locale>>(File.ReadAllText(path));
                if (items == null)
                {
                    return;
                }

                foreach (var kv in items)
                {
                    var loc = kv.Value ?? new Locale();
                    var english = WorldAreaNames.GetDisplayName(kv.Key);
                    if (!string.IsNullOrEmpty(english) &&
                        !english.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        loc.En = english;
                    }

                    byId[kv.Key] = loc;
                    Link(kv.Key, loc);
                    Link(loc.En, loc);
                    Link(loc.ZhCn, loc);
                    Link(loc.ZhTw, loc);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Atlas2] Failed to load area-localization.json: {ex.Message}");
            }
        }

        public static string DisplayName(string mapIdOrEnglish, string fallback, int language)
        {
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

        public static IEnumerable<string> Aliases(string mapId, string fallback)
        {
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

            [JsonProperty("zh_CN")]
            public string ZhCn { get; set; } = string.Empty;

            [JsonProperty("zh_TW")]
            public string ZhTw { get; set; } = string.Empty;
        }
    }
}
