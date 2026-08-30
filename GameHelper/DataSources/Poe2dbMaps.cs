namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    internal static class Poe2dbMaps
    {
        private static readonly Regex Link = new(
            @"class=""[^""]*WorldAreas[^""]*""[^>]*href=""([^""]+)""[^>]*>([^<]+)",
            RegexOptions.Compiled);

        private static readonly HttpClient Http = CreateHttp();

        internal static int PageCount => 2;

        public static async Task<Dictionary<string, (string ZhCn, string ZhTw)>> FetchAsync(
            IEnumerable<CatalogArea> areas,
            Action? onPage = null)
        {
            var cnTask = FetchLocaleAsync("cn", onPage);
            var twTask = FetchLocaleAsync("tw", onPage);
            await Task.WhenAll(cnTask, twTask).ConfigureAwait(false);
            var cn = cnTask.Result;
            var tw = twTask.Result;

            var outMap = new Dictionary<string, (string ZhCn, string ZhTw)>(StringComparer.OrdinalIgnoreCase);
            foreach (var area in areas)
            {
                string zhCn = string.Empty;
                string zhTw = string.Empty;
                foreach (var slug in Slugs(area))
                {
                    if (zhCn.Length == 0 && cn.TryGetValue(slug, out var c) && !string.IsNullOrEmpty(c))
                    {
                        zhCn = c;
                    }

                    if (zhTw.Length == 0 && tw.TryGetValue(slug, out var t) && !string.IsNullOrEmpty(t))
                    {
                        zhTw = t;
                    }
                }

                if (zhCn.Length > 0 || zhTw.Length > 0)
                {
                    outMap[area.Id] = (zhCn, zhTw);
                }
            }

            return outMap;
        }

        private static async Task<Dictionary<string, string>> FetchLocaleAsync(string locale, Action? onPage)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var html = await Poe2dbHttp.GetHtmlAsync(Http, $"https://poe2db.tw/{locale}/Waystones")
                    .ConfigureAwait(false);
                var start = html.IndexOf("id=\"EndGameMaps\"", StringComparison.Ordinal);
                var chunk = start >= 0 ? html[start..] : html;
                foreach (Match m in Link.Matches(chunk))
                {
                    var href = m.Groups[1].Value;
                    var slash = href.LastIndexOf('/');
                    var slug = slash >= 0 ? href[(slash + 1)..] : href;
                    var name = string.Join(' ', m.Groups[2].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    if (slug.Length > 0 && name.Length > 0)
                    {
                        map.TryAdd(slug, name);
                    }
                }
            }
            catch
            {
            }

            onPage?.Invoke();
            return map;
        }

        private static IEnumerable<string> Slugs(CatalogArea area)
        {
            if (!string.IsNullOrEmpty(area.English))
            {
                yield return SlugFromName(area.English);
            }

            var rest = area.Id.StartsWith("Map", StringComparison.Ordinal) ? area.Id[3..] : area.Id;
            yield return Regex.Replace(rest, "([a-z])([A-Z])", "$1_$2");
        }

        private static string SlugFromName(string name)
        {
            name = name.Replace("'", string.Empty).Replace("’", string.Empty);
            return Regex.Replace(name.Trim(), @"[^\w]+", "_").Trim('_');
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
