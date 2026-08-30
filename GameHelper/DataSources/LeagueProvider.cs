namespace GameHelper.Data
{
    using System;
    using System.Collections.Generic;
    using System.Net.Http;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;

    public static class LeagueProvider
    {
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly object Gate = new();
        private static readonly List<string> leagues = new();
        private static bool isLoading;
        private static bool hasLoaded;

        public static IReadOnlyList<string> Leagues
        {
            get { lock (Gate) return leagues.ToArray(); }
        }

        public static bool IsLoading => isLoading;

        public static void EnsureLoaded()
        {
            lock (Gate)
            {
                if (hasLoaded || isLoading) return;
                isLoading = true;
            }

            Task.Run(LoadAsync);
        }

        public static void ForceReload()
        {
            lock (Gate)
            {
                hasLoaded = false;
                leagues.Clear();
                if (isLoading) return;
                isLoading = true;
            }

            Task.Run(LoadAsync);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "GameHelper");
            return client;
        }

        private static async Task LoadAsync()
        {
            var fetched = new List<string>();
            try
            {
                var json = await Http.GetStringAsync("https://poe2scout.com/api/poe2/Leagues").ConfigureAwait(false);
                var root = JObject.Parse(json);
                var arr = root["value"] as JArray ?? root["Value"] as JArray;
                if (arr != null)
                {
                    foreach (var league in arr)
                    {
                        var name = league["Value"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(name))
                            fetched.Add(name);
                    }
                }
            }
            catch
            {
            }

            lock (Gate)
            {
                leagues.Clear();
                if (fetched.Count > 0)
                    leagues.AddRange(fetched);
                hasLoaded = true;
                isLoading = false;
            }
        }
    }
}
