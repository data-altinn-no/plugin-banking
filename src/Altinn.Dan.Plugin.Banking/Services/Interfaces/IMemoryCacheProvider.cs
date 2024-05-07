using Altinn.Dan.Plugin.Banking.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Services.Interfaces
{
    public interface IMemoryCacheProvider
    {
        public Task<(bool success, List<EndpointV2> result)> TryGetEndpoints(string key);

        public Task Set(string key, List<EndpointV2> value, TimeSpan timeToLive);
    }
}
