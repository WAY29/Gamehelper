namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Net;
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

        private static readonly Regex UniqueName = new(
            @"class=""[^""]*uniqueName[^""]*""[^>]*>([^<]+)",
            RegexOptions.Compiled);

        private static readonly Regex Anchor = new(
            @"<a\b[^>]*href=""(?:/(?:us|cn|tw)/)?([A-Za-z0-9_'\-]+)""[^>]*>(.*?)</a>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

        private static readonly HttpClient Http = CreateHttp();

        static Poe2dbNames()
        {
            SelfCheck();
        }

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

            foreach (Match m in Anchor.Matches(html))
            {
                var slug = m.Groups[1].Value;
                if (IsSkippedSlug(slug))
                {
                    continue;
                }

                var text = Collapse(Tags.Replace(m.Groups[2].Value, " "));
                if (text.Length == 0)
                {
                    continue;
                }

                if (!names.TryGetValue(slug, out var old) || text.Length > old.Length)
                {
                    names[slug] = text;
                }
            }

            return names;
        }

        private static bool IsSkippedSlug(string slug)
        {
            if (slug is "Items" or "Unique_item" or "Gem" or "Modifiers" or "Keywords")
            {
                return true;
            }

            foreach (var category in Categories)
            {
                if (category.Equals(slug, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void SelfCheck()
        {
            var parsed = ParsePage(
                "<a href=\"Flesh_Catalyst\"><img alt=\"BreachCatalystLife\" class=\"w1\" /></a>" +
                "<a href=\"Flesh_Catalyst\"><img />血肉催化剂</a>" +
                "<a href=\"/us/Uul-Netols_Catalyst\">Uul-Netol's Catalyst</a>" +
                "<a href=\"Catalysts\">催化剂</a>");
            if (parsed.Count != 2 ||
                parsed["Flesh_Catalyst"] != "血肉催化剂" ||
                parsed["Uul-Netols_Catalyst"] != "Uul-Netol's Catalyst" ||
                parsed.ContainsKey("Catalysts"))
            {
                throw new InvalidOperationException("poe2db list pages must keep visible names, not slugs");
            }
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
            text = WebUtility.HtmlDecode(text);
            return string.Join(' ', text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
