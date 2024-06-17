using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Jose;

namespace Altinn.Dan.Plugin.Banking.Clients.V2;
public partial class Bank_v2
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
        var Aa = 1;


        if (!response.IsSuccessStatusCode)
            return;

        bool isAppJose = false;

        if (response.Headers.TryGetValues("content-type", out IEnumerable<string?> headervalues))
        {
            isAppJose = headervalues.Any(x => x == "application/jose");
        }

        var jwt = response.Content.ReadAsStringAsync().Result;
        var decryptedContent =
            JWT.Decode(jwt,
                DecryptionCertificate
                    .GetRSAPrivateKey()); //, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

        response.Content = new StringContent(decryptedContent, Encoding.UTF8);
    }

    partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, string url)
    {
        var a = url;
    }
}
