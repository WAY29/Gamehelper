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
        private static Dictionary<string, Locale> byId = new(StringComparer.Ordinal);
        private static Dictionary<string, Locale> byEnglish = new(StringComparer.OrdinalIgnoreCase);

        public static void Load(string pluginDirectory)
        {
            byId = new Dictionary<string, Locale>(StringComparer.Ordinal);
            byEnglish = new Dictionary<string, Locale>(StringComparer.OrdinalIgnoreCase);
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

                byId = items;
                foreach (var kv in items)
                {
                    var english = WorldAreaNames.GetDisplayName(kv.Key);
                    if (!string.IsNullOrEmpty(english))
                    {
                        byEnglish[english] = kv.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Atlas2] Failed to load area-localization.json: {ex.Message}");
            }
        }

        public static string DisplayName(string mapIdOrEnglish, string fallback, int language)
        {
            Locale loc = null;
            if (!string.IsNullOrEmpty(mapIdOrEnglish))
            {
                byId.TryGetValue(mapIdOrEnglish, out loc);
                loc ??= byEnglish.GetValueOrDefault(mapIdOrEnglish);
            }

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
                _ => string.Empty,
            };
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }

        public static IEnumerable<string> Aliases(string mapId, string fallback)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                names.Add(fallback.Trim());
            }

            if (!string.IsNullOrEmpty(mapId) && byId.TryGetValue(mapId, out var loc) && loc != null)
            {
                if (!string.IsNullOrWhiteSpace(loc.ZhCn))
                {
                    names.Add(loc.ZhCn.Trim());
                }

                if (!string.IsNullOrWhiteSpace(loc.ZhTw))
                {
                    names.Add(loc.ZhTw.Trim());
                }
            }

            return names;
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
