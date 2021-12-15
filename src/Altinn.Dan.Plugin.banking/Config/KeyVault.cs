using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Nadobe.Common.Util;
using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Config
{
    /// <summary>
    /// Key Vault for Core
    /// </summary>
    public class CoreKeyVault
    {
        private KeyVaultClient KeyVaultClient { get; set; }

        private string VaultName { get; set; }

        private string ClientId { get; set; }

        private string ClientSecret { get; set; }

        /// <summary>
        /// Key Vault for Core
        /// </summary>
        /// <param name="vaultName">Name of the Key Vault</param>
        public CoreKeyVault(string vaultName)
        {
            VaultName = vaultName;
            AzureServiceTokenProvider azureServiceTokenProvider = new AzureServiceTokenProvider();
            KeyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(azureServiceTokenProvider.KeyVaultTokenCallback));
        }

        /// <summary>
        /// Key Vault for Core
        /// </summary>
        /// <param name="vaultName">Name of the Key Vault</param>
        /// <param name="clientId">Key vault client id</param>
        /// <param name="clientSecret">Key vault client secret</param>
        public CoreKeyVault(string vaultName, string clientId, string clientSecret)
        {
            VaultName = vaultName;

            ClientId = clientId;
            ClientSecret = clientSecret;

            KeyVaultClient = new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(GetToken));
        }

        /// <summary>
        /// Get a secret from the key vault
        /// </summary>
        /// <param name="key">Secret name</param>
        /// <returns>The secret value</returns>
        public async Task<string> Get(string key)
        {
            var secret = await GetBundle(key);
            return secret.Value;
        }

        /// <summary>
        /// Get a certificate from the key vault
        /// </summary>
        /// <param name="key">Certificate name</param>
        /// <returns>The certificate</returns>
        public async Task<X509Certificate2> GetCertificate(string key)
        {
            var base64Certificate = await Get(key);
            var certBytes = Convert.FromBase64String(base64Certificate);

            var cert = new X509Certificate2(certBytes, string.Empty, X509KeyStorageFlags.MachineKeySet);

            if (SslHelper.GetValidOrgNumberFromCertificate(cert) == null)
            {
                throw new Exceptions.InvalidCertificateException("Unable to validate chain or not an enterprise certificate");
            }

            return await Task.FromResult(cert);
        }

        /// <summary>
        /// Get a secret from the key vault
        /// </summary>
        /// <param name="key">Secret name</param>
        /// <returns>A bundle with information about the secret</returns>
        public Task<SecretBundle> GetBundle(string key)
        {
            var uri = $"https://{VaultName}.vault.azure.net";
            return KeyVaultClient.GetSecretAsync(uri, key);
        }

        private async Task<string> GetToken(string authority, string resource, string scope)
        {
            var authContext = new AuthenticationContext(authority);
            ClientCredential clientCred = new ClientCredential(ClientId, ClientSecret);
            AuthenticationResult result = await authContext.AcquireTokenAsync(resource, clientCred);

            if (result == null)
            {
                throw new InvalidOperationException("Failed to obtain the JWT token");
            }

            return result.AccessToken;
        }
    }
}
