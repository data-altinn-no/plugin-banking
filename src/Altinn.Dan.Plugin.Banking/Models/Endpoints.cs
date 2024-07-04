using FileHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Altinn.Dan.Plugin.Banking.Models
{
    [DelimitedRecord(",")]
    [IgnoreFirst]
    public class EndpointV2
    {
        public string OrgNummer { get; set; }

        [FieldQuoted(QuoteMode.OptionalForBoth)]
        public string Navn { get; set; }

        public string Url { get; set; }

        public string Version { get; set; }
    }

    public class EndpointsList
    {
        [JsonProperty("endpoints")]
        public List<EndpointExternal> Endpoints { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }
    }

    public class EndpointExternal
    {
        [JsonProperty("orgNo")]
        public string OrgNo { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("env")]
        public string Env { get; set; }
    }
}
