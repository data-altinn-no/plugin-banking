using System;
using System.Security.Cryptography.X509Certificates;
using Dan.Plugin.Banking;

namespace Altinn.Dan.Plugin.Banking.Config
{
    public class ApplicationSettings
    {
        public static ApplicationSettings ApplicationConfig;
        private static X509Certificate2 _altinnCertificate;
        private static X509Certificate2 _oedDecryptCert;

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

        public string KarUrl
        {
            get { return Environment.GetEnvironmentVariable("KarUrl"); }
        }

        public string[] BankUrls
        {
            get { return Environment.GetEnvironmentVariable("BankUrls").Split(";"); }
        }

        public bool IsUnitTest
        {
            get { return Convert.ToBoolean(Environment.GetEnvironmentVariable("IsUnitTest")); }
        }

        public bool IsDevelopment
        {
            get { return Convert.ToBoolean(Environment.GetEnvironmentVariable("IsDevelopment")); }
        }

        public string SBankenUri
        {
            get { return Environment.GetEnvironmentVariable("SBankenUri"); }
        }

        public string Sparebank1Uri
        {
            get { return Environment.GetEnvironmentVariable("Sparebank1Uri"); }
        }
        public string BankAudience
        {
            get { return Environment.GetEnvironmentVariable("BankAudience"); }
        }
        public static string KeyVaultSslCertificate => Environment.GetEnvironmentVariable("KeyVaultSslCertificate");

        public static string KeyVaultName => Environment.GetEnvironmentVariable("KeyVaultName");

        public static string KeyVaultClientId => Environment.GetEnvironmentVariable("KeyVaultClientId");

        public static string KeyVaultClientSecret { get; set; }

        public string ClientId { get; set; }

        public string MaskinportenEndpoint { get; set; }

        public static string DecryptCert { get; set; }

        public string Sparebank1Audience { get; set; }

        public string SBankenAudience { get; set; }

        public X509Certificate2 Certificate
        {
            get
            {
                return _altinnCertificate ?? new PluginKeyVault(ApplicationSettings.KeyVaultName).GetCertificate(ApplicationSettings.KeyVaultSslCertificate).Result;
            }
            set
            {
                _altinnCertificate = value;
            }
        }
        public X509Certificate2 OedDecryptCert
        {
            get
            {
                return _oedDecryptCert ?? new PluginKeyVault(ApplicationSettings.KeyVaultName).GetCertificate(ApplicationSettings.DecryptCert).Result;
            }
            set
            {
                _oedDecryptCert = value;
            }
        }

    }
}
