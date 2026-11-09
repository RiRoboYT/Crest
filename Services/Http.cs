using System;
using System.Net.Http;

namespace Crest.Services
{
    /// <summary>One shared HttpClient for lyrics, weather and calendar downloads.</summary>
    internal static class Http
    {
        public static readonly HttpClient Client = Create();

        private static HttpClient Create()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            // LRCLIB asks clients to identify themselves.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Crest/1.3 (+https://github.com/RiRoboYT/Crest)");
            return c;
        }
    }
}
