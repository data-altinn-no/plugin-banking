using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Altinn.Dan.Plugin.Banking.Config;
using Jose;
using Microsoft.Extensions.Options;

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
    public async partial Task ProcessResponse(HttpClient client, HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            return;

        var jwt = await response.Content.ReadAsStringAsync();
        var decryptedContent = JWT.Decode(jwt, DecryptionCertificate.GetRSAPrivateKey()); //, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);
        response.Content = new StringContent(decryptedContent, Encoding.UTF8);
    }

    partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, string url)
    {
        if (_appSettings.UseProxy)
        {
            var proxyUrl = string.Format(_appSettings.ProxyUrl, Uri.EscapeDataString(url.Replace("https://", "").Replace("http://","")));
            request.RequestUri = new Uri(proxyUrl);
        }
    }

   
}
