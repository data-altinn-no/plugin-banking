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
        [FieldOptional]
        public string OrgNummer { get; set; }

        [FieldOptional]
        public string Navn { get; set; }

        [FieldOptional]
        [FieldValueDiscarded]
        public string Filnavn { get; set; }

        [FieldOptional]
        public string EndepunktProduksjon { get; set; }

        [FieldOptional]
        public string EndepunktTest { get; set; }

        [FieldOptional]
        [FieldValueDiscarded]
        public string Id { get; set; }

        [FieldOptional]
        [FieldValueDiscarded]
        public string TestId { get; set; }

        [FieldOptional]
        [FieldValueDiscarded]
        public string TestId2 { get; set; }

        [FieldOptional]
        public string Version { get; set; }
    }

    public class EndpointsList
    {
        [JsonProperty("endpoints")]
        public List<EndpointV2> Endpoints { get; set; }

        [JsonProperty("total")]
        public int Total { get; set; }
    }
}
