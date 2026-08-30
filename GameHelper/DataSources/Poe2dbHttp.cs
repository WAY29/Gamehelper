namespace GameHelper.Data
{
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    internal static class Poe2dbHttp
    {
        private static readonly SemaphoreSlim Slots = new(4);

        internal static async Task<string> GetHtmlAsync(HttpClient http, string url)
        {
            await Slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await http.GetStringAsync(url).ConfigureAwait(false);
            }
            finally
            {
                Slots.Release();
            }
        }
    }
}
