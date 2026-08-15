using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CarbonAware.Providers.Options
{
    public sealed class WattTimeOptions
    {
        public string BaseUrl { get; set; } = "https://api.watttime.org";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public int TokenCacheMinutes { get; set; } = 55; // access tokens typically ~1hr

        public int MaxConcurrentChecks { get; set; } = 12;
    }
}
