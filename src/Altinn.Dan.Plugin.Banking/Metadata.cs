using Altinn.Dan.Plugin.Banking.Models;
using Newtonsoft.Json;
using System.Collections.Generic;
using Dan.Common.Enums;
using Dan.Common.Interfaces;
using Dan.Common.Models;
using NJsonSchema;
using Altinn.Dan.Plugin.Banking.Clients.V2;


namespace Altinn.Dan.Plugin.Banking
{
    public class Metadata : IEvidenceSourceMetadata
    {
        public const string SOURCE = "Bits";

        public static int ERROR_BANK_REQUEST_ERROR = 1;

        public static int ERROR_KAR_NOT_AVAILABLE_ERROR = 2;

        public static int ERROR_METADATA_LOOKUP_ERROR = 3;

        public static int ERROR_GITHUB_RETRIEVAL_FAILED = 4;
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
                            JsonSchemaDefintion = EvidenceValue.SchemaFromObject<BankResponse>(Formatting.Indented)
        }
                    },
                    Parameters = new List<EvidenceParameter>()
                    {
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "FraDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "TilDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "SkipKAR",
                            ParamType = EvidenceParamType.Boolean,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "ReferanseId",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        }
                    },
                    AuthorizationRequirements = new List<Requirement>()
                    {
                        new MaskinportenScopeRequirement()
                        {
                            RequiredScopes = new List<string> {"altinn:dataaltinnno/oed" }
                        }
                    }
                },
                new EvidenceCode()
                {
                    EvidenceCodeName = "Kundeforhold",
                    EvidenceSource = SOURCE,
                    BelongsToServiceContexts = new List<string> { "OED" },
                    RequiredScopes = "bits:kundeforhold",
                    Values = new List<EvidenceValue>()
                    {
                        new EvidenceValue()
                        {
                            EvidenceValueName = "default",
                            ValueType = EvidenceValueType.JsonSchema,
                            JsonSchemaDefintion = EvidenceValue.SchemaFromObject<BankRelations>(Formatting.Indented)
        }
                    },
                    Parameters = new List<EvidenceParameter>()
                    {
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "FraDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "TilDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "ReferanseId",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        }
                    },

                    AuthorizationRequirements = new List<Requirement>()
                    {
                        new MaskinportenScopeRequirement()
                        {
                            RequiredScopes = new List<string> {"altinn:dataaltinnno/oed" }
                        }
                    }
                }, new EvidenceCode()
                {
                    EvidenceCodeName = "Kontodetaljer",
                    EvidenceSource = SOURCE,
                    BelongsToServiceContexts = new List<string> { "OED" },
                    RequiredScopes = "bits:kundeforhold",
                    Timeout = 60,
                    Values = new List<EvidenceValue>()
                    {
                        new EvidenceValue()
                        {
                            EvidenceValueName = "default",
                            ValueType = EvidenceValueType.JsonSchema,
                            JsonSchemaDefintion = EvidenceValue.SchemaFromObject<BankResponse>(Formatting.Indented)
                        }
                    },
                    Parameters = new List<EvidenceParameter>()
                    {
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "FraDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "TilDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "ReferanseId",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "Organisasjonsnummer",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "InkluderTransaksjoner",
                            ParamType = EvidenceParamType.Boolean,
                            Required = false
                        }
                    },
                    AuthorizationRequirements = new List<Requirement>()
                    {
                        new MaskinportenScopeRequirement()
                        {
                            RequiredScopes = new List<string>(){"altinn:dataaltinnno/oed"}
                        }
                    }
                },
                new EvidenceCode()
                {
                    EvidenceCodeName = "Kontotransaksjoner",
                    EvidenceSource = SOURCE,
                    BelongsToServiceContexts = new List<string> { "OED" },
                    RequiredScopes = "bits:kundeforhold",
                    Values = new List<EvidenceValue>()
                    {
                        new EvidenceValue()
                        {
                            EvidenceValueName = "default",
                            ValueType = EvidenceValueType.JsonSchema,
                            JsonSchemaDefintion = EvidenceValue.SchemaFromObject<Transactions>(Formatting.Indented)
        }
                    },
                    Parameters = new List<EvidenceParameter>()
                    {
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "FraDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "TilDato",
                            ParamType = EvidenceParamType.DateTime,
                            Required = false
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "ReferanseId",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "Organisasjonsnummer",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        },
                        new EvidenceParameter()
                        {
                            EvidenceParamName = "Kontoreferanse",
                            ParamType = EvidenceParamType.String,
                            Required = true
                        }
                    },
                    AuthorizationRequirements = new List<Requirement>()
                    {
                        new MaskinportenScopeRequirement()
                        {
                            RequiredScopes = new List<string>(){"altinn:dataaltinnno/oed"}
                        }
                    }
                },
                new EvidenceCode()
                {
                    EvidenceCodeName = "Kontrollinformasjon",
                    EvidenceSource = SOURCE,
                    BelongsToServiceContexts = new List<string> { "Bits kontrollinformasjon" },
                    Values = new List<EvidenceValue>()
                    {
                        new EvidenceValue()
                        {
                            EvidenceValueName = "default",
                            ValueType = EvidenceValueType.JsonSchema,
                            JsonSchemaDefintion = EvidenceValue.SchemaFromObject<EndpointsList>(Formatting.Indented)
                        }
                    },
                    AuthorizationRequirements = new List<Requirement>()
                    {
                        new MaskinportenScopeRequirement()
                        {
                            RequiredScopes = new List<string>(){"altinn:dataaltinnno/kontrollinformasjon"}
                        }
                    }
                }
           };
        }
    }
}
