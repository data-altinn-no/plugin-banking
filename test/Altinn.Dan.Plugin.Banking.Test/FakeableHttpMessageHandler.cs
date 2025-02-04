using System;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Test
{
    public abstract class FakeableHttpMessageHandler : HttpMessageHandler
    {
        public abstract Task<HttpResponseMessage> FakeSendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken);

        // sealed so FakeItEasy won't intercept calls to this method
        protected sealed override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            => this.FakeSendAsync(request, cancellationToken);
    }

    public static class CertificateGenerator
    {
        public static X509Certificate2 GenerateSelfSignedCertificate()
        {
            string subjectName = "Self-Signed-Cert-Example";
            using var rsa = RSA.Create(2048);
            var certRequest = new CertificateRequest($"CN={subjectName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // Add extensions to the request (just as an example)
            // Add keyUsage
            certRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            X509Certificate2 generatedCert = certRequest.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10)); // generate the cert and sign!

            X509Certificate2 pfxGeneratedCert = new X509Certificate2(generatedCert.Export(X509ContentType.Pfx)); // has to be turned into pfx or Windows at least throws a security credentials not found during sslStream.connectAsClient or HttpClient request...

            return pfxGeneratedCert;
        }
    }
}
