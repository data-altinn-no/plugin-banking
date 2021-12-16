using System.Collections.Generic;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Nadobe.Common.Interfaces;
using Nadobe.Common.Models;
using Nadobe.Common.Models.Enums;
using Newtonsoft.Json;

namespace Altinn.Dan.Plugin.Banking
{
    public class Metadata : IEvidenceSourceMetadata
    {
        public const string SOURCE = "Bits";

        public const int ERROR_ORGANIZATION_NOT_FOUND = 1;

        public static int ERROR_CCR_UPSTREAM_ERROR = 2;
  

        public List<EvidenceCode> GetEvidenceCodes()
        {
            return new List<EvidenceCode>()
           {
                new EvidenceCode()
                {
                    EvidenceCodeName = "Banktransaksjoner",
                    EvidenceSource = SOURCE,
                    BelongsToServiceContexts = new List<string> { "OED" },
                    RequiredScopes = "bits:kundeforhold",
                    Values = new List<EvidenceValue>()
                    {
                        new EvidenceValue()
                        {
                            EvidenceValueName = "default",
                            ValueType = EvidenceValueType.JsonSchema,
                            JsonSchemaDefintion = JsonConvert.SerializeObject(new BankResponse())
                        }
                    },
                    AuthorizationRequirements = new List<Requirement>()
                    {
                        new MaskinportenScopeRequirement()
                        {
                            RequiredScopes = new List<string> {"altinn:dataaltinnno/oed" }
                        }
                    }
                }
           };
        }
    }
}
