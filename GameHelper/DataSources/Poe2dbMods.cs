namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    internal static class Poe2dbMods
    {
        private static readonly string[] Slugs =
        {
            "Tablet", "Breach_Tablet", "Expedition_Tablet", "Delirium_Tablet",
            "Ritual_Tablet", "Irradiated_Tablet", "Overseer_Tablet", "Abyss_Tablet",
            "Temple_Tablet",
        };

        private static readonly string[] Locales = { "us", "cn", "tw" };

        internal static int PageCount => Slugs.Length * Locales.Length;

        private static readonly Regex Family = new(
            @"ModFamilyList"":\[""([^""]+)""\]",
            RegexOptions.Compiled);

        private static readonly Regex Str = new(
            @"""str"":""((?:\\.|[^""])*)""",
            RegexOptions.Compiled);

        private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly HttpClient Http = CreateHttp();

        public static async Task<Dictionary<string, (string En, string ZhCn, string ZhTw)>> FetchAsync(
            Action? onPage = null)
        {
            var byLocale = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var locale in Locales)
            {
                byLocale[locale] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var jobs = new List<Task>();
            foreach (var slug in Slugs)
            {
                foreach (var locale in Locales)
                {
                    jobs.Add(FetchPageAsync(byLocale[locale], slug, locale, onPage));
                }
            }

            await Task.WhenAll(jobs).ConfigureAwait(false);

            var us = byLocale["us"];
            var cn = byLocale["cn"];
            var tw = byLocale["tw"];
            var outMap = new Dictionary<string, (string En, string ZhCn, string ZhTw)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (family, en) in us)
            {
                cn.TryGetValue(family, out var zhCn);
                tw.TryGetValue(family, out var zhTw);
                outMap[family] = (en, zhCn ?? en, zhTw ?? en);
            }

            return outMap;
        }

        public static string StripPrefix(string family)
        {
            if (family.StartsWith("Tower", StringComparison.Ordinal))
            {
                return family[5..];
            }

            if (family.StartsWith("Map", StringComparison.Ordinal))
            {
                return family[3..];
            }

            return family;
        }

        private static async Task FetchPageAsync(
            Dictionary<string, string> dest,
            string slug,
            string locale,
            Action? onPage)
        {
            try
            {
                var html = await Poe2dbHttp.GetHtmlAsync(Http, $"https://poe2db.tw/{locale}/{slug}")
                    .ConfigureAwait(false);
                lock (dest)
                {
                    foreach (var (family, text) in Parse(html))
                    {
                        dest.TryAdd(family, text);
                        dest.TryAdd(StripPrefix(family), text);
                    }
                }
            }
            catch
            {
            }

            onPage?.Invoke();
        }

        private static IEnumerable<(string Family, string Text)> Parse(string html)
        {
            foreach (var chunk in html.Split("{\"Name\":", StringSplitOptions.None))
            {
                var fam = Family.Match(chunk);
                var str = Str.Match(chunk);
                if (!fam.Success || !str.Success)
                {
                    continue;
                }

                var text = Clean(str.Groups[1].Value);
                if (text.Length == 0)
                {
                    continue;
                }

                yield return (fam.Groups[1].Value, text);
            }
        }

        private static string Clean(string raw)
        {
            raw = raw.Replace("\\\"", "\"").Replace("\\/", "/").Replace("\\n", " ");
            raw = Tags.Replace(raw, string.Empty);
            raw = WebUtility.HtmlDecode(raw);
            return string.Join(' ', raw.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static HttpClient CreateHttp()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
            client.DefaultRequestHeaders.Add("User-Agent", "GameHelper");
            client.DefaultRequestHeaders.Add("Accept", "text/html");
            return client;
        }
    }
}
