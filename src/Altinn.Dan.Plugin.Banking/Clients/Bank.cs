using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jose;

namespace Altinn.Dan.Plugin.Banking.Clients;
public partial class Bank
{
    public X509Certificate2 DecryptionCertificate { get; set; }

    /// <summary>
    /// The bank sends a encrypted payload, use the ProcessResponse-mechanism to decrypt before further processing
    /// </summary>
    /// <param name="client">Http client</param>
    /// <param name="response">Response from bank</param>
    // ReSharper disable once UnusedParameterInPartialMethod
    partial void ProcessResponse(HttpClient client, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) return;

        var jwt = response.Content.ReadAsStringAsync().Result;
        var decryptedContent =
            JWT.Decode(jwt,
                DecryptionCertificate
                    .GetRSAPrivateKey()); //, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

        response.Content = new StringContent(decryptedContent, Encoding.UTF8);
    }
}
