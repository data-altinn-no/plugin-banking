using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Altinn.Dan.Plugin.Banking.Config;
using Microsoft.Extensions.Options;

namespace Altinn.Dan.Plugin.Banking.Services

{
    public class MemoryCacheProvider : IMemoryCacheProvider
    {
        private readonly IMemoryCache _memoryCache;
        private readonly ApplicationSettings _settings;

        public MemoryCacheProvider(IMemoryCache memoryCache, IOptions<ApplicationSettings> settings)
        {
            _memoryCache = memoryCache;
            _settings = settings.Value;
        }
        public Task<(bool success, List<EndpointExternal> result)> TryGetEndpoints(string key)
        {
            bool success = _memoryCache.TryGetValue(key, out List<EndpointExternal> result);
            return Task.FromResult((success, result));
        }

        public async Task<List<EndpointExternal>> SetEndpointsCache(string key, List<EndpointV2> value, TimeSpan timeToLive)
        {
            MemoryCacheEntryOptions cacheEntryOptions = new MemoryCacheEntryOptions()
            {
                Priority = CacheItemPriority.High,
            };

            cacheEntryOptions.SetAbsoluteExpiration(timeToLive);
            var result = _memoryCache.Set(key, MapToExternal(value), cacheEntryOptions);

            await Task.CompletedTask;

            return result;
        }

        private List<EndpointExternal> MapToExternal(List<EndpointV2> endpoints)
        {
            var query = from endpoint in endpoints
                select new EndpointExternal()
                {
                    Env = _settings.UseTestEndpoints ? "test" : "prod",
                    Name = endpoint.Navn,
                    OrgNo = endpoint.OrgNummer,
                    Url = _settings.UseTestEndpoints ? endpoint.EndepunktTest : endpoint.EndepunktProduksjon,
                    Version = _settings.UseTestEndpoints ?
                        (endpoint.EndepunktTest.ToUpper().Contains("V2") ? "V2" : "V1")
                        : "V1"
                };

            return query.ToList();
        }
    }


}
