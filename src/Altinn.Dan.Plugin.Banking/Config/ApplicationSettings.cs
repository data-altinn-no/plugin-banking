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

        public string KarUrl
        {
            get { return Environment.GetEnvironmentVariable("KarUrl"); }
        }

        public static string KeyVaultSslCertificate => Environment.GetEnvironmentVariable("KeyVaultSslCertificate");

        public static string KeyVaultName => Environment.GetEnvironmentVariable("KeyVaultName");

        public string ClientId { get; set; }

        public string MaskinportenEnvironment { get; set; }

        public static string DecryptCert { get; set; }

        public string FDKEndpointsUrl { get; set; }

        public KontoOpplysninger Endpoints { get; set; }

        public string ImplementedBanks { get; set; }

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
