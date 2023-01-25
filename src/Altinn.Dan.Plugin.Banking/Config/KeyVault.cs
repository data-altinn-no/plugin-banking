using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

namespace Altinn.Dan.Plugin.Banking.Config;

/// <summary>
/// Key Vault for Core
/// </summary>
public class PluginKeyVault
{
    private SecretClient SecretClient { get; }

    /// <summary>
    /// Key Vault for Core
    /// </summary>
    /// <param name="vaultName">Name of the Key Vault</param>
    public PluginKeyVault(string vaultName)
    {
        SecretClient = new SecretClient(new Uri($"https://{vaultName}.vault.azure.net/"), new DefaultAzureCredential());
    }

    /// <summary>
    /// Get a secret from the key vault
    /// </summary>
    /// <param name="key">Secret name</param>
    /// <returns>The secret value</returns>
    public async Task<string> Get(string key)
    {
        var secret = await SecretClient.GetSecretAsync(key);
        return secret.Value.Value;
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

        return await Task.FromResult(cert);
    }
}
