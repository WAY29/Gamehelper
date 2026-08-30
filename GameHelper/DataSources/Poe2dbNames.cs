namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    internal static class Poe2dbNames
    {
        private static readonly string[] Categories =
        {
            "Unique_item", "Stackable_Currency", "Augment", "Omen", "Incubators",
            "Liquid_Emotions", "Essence", "Splinter", "Catalysts", "Map_Fragments",
            "Inscribed_Ultimatum", "Trial_Coins", "Pinnacle_Keys", "Jewels", "Vault_Keys",
            "Relics", "Strongbox", "Life_Flasks", "Mana_Flasks", "Charms", "Gem",
            "Skill_Gems", "Support_Gems", "Meta_Skill_Gem", "Spirit_Gems", "Lineage_Supports",
            "Waystones", "Tablet", "Hideout", "Quest",
        };

        private static readonly string[] Locales = { "us", "cn", "tw" };

        internal static int PageCount => Categories.Length * Locales.Length;

        private static readonly Regex SlugHref = new(
            @"href=""/(?:us|cn|tw)/([A-Za-z0-9_'\-]+)""",
            RegexOptions.Compiled);

        private static readonly Regex UniqueName = new(
            @"class=""[^""]*uniqueName[^""]*""[^>]*>([^<]+)",
            RegexOptions.Compiled);

        private static readonly HttpClient Http = CreateHttp();

        public static async Task<Dictionary<string, (string ZhCn, string ZhTw)>> FetchEnglishToLocalAsync(
            Action? onPage = null)
        {
            var bySlug = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            var jobs = new List<Task>();
            foreach (var category in Categories)
            {
                foreach (var locale in Locales)
                {
                    jobs.Add(FetchPageAsync(bySlug, category, locale, onPage));
                }
            }

            await Task.WhenAll(jobs).ConfigureAwait(false);

            var map = new Dictionary<string, (string ZhCn, string ZhTw)>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in bySlug.Values)
            {
                if (!rec.TryGetValue("us", out var en) || string.IsNullOrWhiteSpace(en))
                {
                    continue;
                }

                rec.TryGetValue("cn", out var cn);
                rec.TryGetValue("tw", out var tw);
                if (string.IsNullOrWhiteSpace(cn) && string.IsNullOrWhiteSpace(tw))
                {
                    continue;
                }

                map[en.Trim()] = ((cn ?? en).Trim(), (tw ?? en).Trim());
            }

            return map;
        }

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.Add("User-Agent", "GameHelper");
            client.DefaultRequestHeaders.Add("Accept", "text/html");
            return client;
        }

        private static Dictionary<string, string> ParsePage(string html)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in UniqueName.Matches(html))
            {
                var name = Collapse(m.Groups[1].Value);
                if (name.Length == 0)
                {
                    continue;
                }

                names[SlugFromName(name)] = name;
            }

            foreach (Match m in SlugHref.Matches(html))
            {
                var slug = m.Groups[1].Value;
                if (slug is "Items" or "Unique_item" or "Gem")
                {
                    continue;
                }

                if (names.ContainsKey(slug))
                {
                    continue;
                }

                var decoded = slug.Replace('_', ' ');
                names[slug] = decoded;
            }

            return names;
        }

        private static async Task FetchPageAsync(
            Dictionary<string, Dictionary<string, string>> bySlug,
            string category,
            string locale,
            Action? onPage)
        {
            try
            {
                var html = await Poe2dbHttp.GetHtmlAsync(Http, $"https://poe2db.tw/{locale}/{category}")
                    .ConfigureAwait(false);
                lock (bySlug)
                {
                    Merge(bySlug, locale, ParsePage(html));
                }
            }
            catch
            {
            }

            onPage?.Invoke();
        }

        private static void Merge(
            Dictionary<string, Dictionary<string, string>> store,
            string locale,
            Dictionary<string, string> page)
        {
            foreach (var (slug, name) in page)
            {
                if (!store.TryGetValue(slug, out var rec))
                {
                    rec = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    store[slug] = rec;
                }

                rec[locale] = name;
            }
        }

        private static string SlugFromName(string name)
        {
            var chars = new char[name.Length];
            var n = 0;
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    chars[n++] = ch;
                }
                else if (n > 0 && chars[n - 1] != '_')
                {
                    chars[n++] = '_';
                }
            }

            while (n > 0 && chars[n - 1] == '_')
            {
                n--;
            }

            return new string(chars, 0, n);
        }

        private static string Collapse(string text)
        {
            return string.Join(' ', text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
