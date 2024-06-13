using Altinn.Dan.Plugin.Banking.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Services.Interfaces
{
    public interface IMemoryCacheProvider
    {
        public Task<(bool success, List<EndpointExternal> result)> TryGetEndpoints(string key);

        public Task<List<EndpointExternal>> SetEndpointsCache(string key, List<EndpointV2> value, TimeSpan timeToLive);
    }
}
