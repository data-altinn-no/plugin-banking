using Altinn.Dan.Plugin.Banking.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Clients
{
    public partial class KAR
    {
        public async Task<KARResponse> Get(string ssn, string mpToken, string fromDate, string toDate, Guid accountInfoRequestID, Guid correlationID)
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", mpToken);
            var header = _httpClient.DefaultRequestHeaders.Authorization;

            if (!header.Scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                throw new Exception($"Invalid auth header {header.Scheme} found for {ssn} in KAR interface");

            return MapToInternal(await GetCustomerRelationAsync(header.Parameter, ssn, correlationID, "Prosjekt OED", accountInfoRequestID, fromDate, toDate));
        }

        private static KARResponse MapToInternal(ListCustomerRelation listCustomerRelation)
        {
            return new KARResponse() { Banks = listCustomerRelation.Banks };
        }
    }
}
