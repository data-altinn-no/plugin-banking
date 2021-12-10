using System;

namespace Altinn.Dan.Plugin.DATASOURCENAME.Config
{
    public class ApplicationSettings : IApplicationSettings
    {
        public static ApplicationSettings ApplicationConfig;

        public ApplicationSettings()
        {
            ApplicationConfig = this;
        }

        public string RedisConnectionString
        {
            get { return Environment.GetEnvironmentVariable("RedisConnectionString"); }
        }

        public bool IsTest
        {
            get { return Environment.GetEnvironmentVariable("IsTest").ToLowerInvariant().Trim() == "true"; }
        }

        public TimeSpan Breaker_RetryWaitTime
        {
            get { return TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("Breaker_RetryWaitTime"))); }
        }

        public TimeSpan Breaker_OpenCircuitTime
        {
            get { return TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("Breaker_OpenCircuitTime"))); }
        }

        public string DATASETNAME1URL
        {
            get { return Environment.GetEnvironmentVariable("DATASETNAME1URL"); }
        }

        public string DATASETNAME2URL
        {
            get { return Environment.GetEnvironmentVariable("DATASETNAME2URL"); }
        }
    }
}
