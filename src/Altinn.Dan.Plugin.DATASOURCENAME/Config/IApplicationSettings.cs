using System;

namespace Altinn.Dan.Plugin.DATASOURCENAME.Config
{
    public interface IApplicationSettings
    {
        string RedisConnectionString { get; }
        TimeSpan Breaker_RetryWaitTime { get; }
        TimeSpan Breaker_OpenCircuitTime { get; }
        bool IsTest { get; }
        string DATASETNAME1URL { get; }

        string DATASETNAME2URL { get; }
    }
}
