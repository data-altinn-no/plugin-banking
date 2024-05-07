using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Services

{
    public class MemoryCacheProvider : IMemoryCacheProvider
    {
        private readonly IMemoryCache _memoryCache;

        public MemoryCacheProvider(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache;
        }
        public Task<(bool success, List<EndpointV2> result)> TryGetEndpoints(string key)
        {
            bool success = _memoryCache.TryGetValue(key, out List<EndpointV2> result);
            return Task.FromResult((success, result));
        }

        public async Task Set(string key, List<EndpointV2> value, TimeSpan timeToLive)
        {
            MemoryCacheEntryOptions cacheEntryOptions = new MemoryCacheEntryOptions()
            {
                Priority = CacheItemPriority.High,
            };

            cacheEntryOptions.SetAbsoluteExpiration(timeToLive);
            _memoryCache.Set(key, value, cacheEntryOptions);

            await Task.CompletedTask;
        }
    }
}
