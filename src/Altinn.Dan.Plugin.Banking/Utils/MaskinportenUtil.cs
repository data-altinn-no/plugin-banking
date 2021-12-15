using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Altinn.Dan.Plugin.Banking.Utils
{
    /// <summary>
    /// Represents an entity used to request authorization from Maskinporten
    /// </summary>
    public class MaskinportenUtil
    {
        private const int _tokenTtl = 180;

        private static readonly object _lockObject = new object();
        private static Dictionary<string, HttpClient> _httpClients = new Dictionary<string, HttpClient>();

        private readonly string _clientId;
        private readonly string _resource;
        private readonly string _scopes;
        private readonly bool _useAltinnJwt;
        private readonly string _audience;
        private readonly X509Certificate2 _certificate;
        private readonly string _maskinportenEndpoint;
        private readonly string _altinnEndpoint;

        /// <summary>
        /// Initializes a new instance of the <see cref="MaskinportenUtil"/> class.
        /// </summary>
        /// <param name="resource">Which resource to request authorization for</param>
        /// <param name="scopes">Scope of the authorization request</param>
        /// <param name="clientId">Issuer of the authorization request</param>
        /// <param name="useAltinnJwt">Whether to exchange the MP token with an Altinn token</param>
        /// <param name="audience">Maskinporten audience</param>
        /// <param name="certificate">Certificate for maskinporten</param>
        /// <param name="maskinportenEndpoint">MaskinportenEndpoint</param>
        /// <param name="altinnEndpoint">AltinnEndpoint</param>
        public MaskinportenUtil(string resource, string scopes, string clientId, bool useAltinnJwt, string audience,
            X509Certificate2 certificate, string maskinportenEndpoint, string altinnEndpoint)
        {
            _resource = resource;
            _scopes = scopes;
            _clientId = clientId;
            _useAltinnJwt = useAltinnJwt;
            _audience = audience;
            _certificate = certificate;
            _maskinportenEndpoint = maskinportenEndpoint;
            _altinnEndpoint = altinnEndpoint;
        }

        /// <summary>
        /// Get a token to use for authorization the an http client
        /// </summary>
        /// <returns>The token</returns>
        public string GetToken()
        {
            return GetTokenFromAssertion(BuildJwtAssertion());
        }

        /// <summary>
        /// Retrieves accesstoken from Maskinporten
        /// </summary>
        private string GetTokenFromAssertion(string jwtassertion)
        {
            var formContent = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
                new KeyValuePair<string, string>("assertion", jwtassertion),
            });

            HttpResponseMessage response = GetMaskinportenClient().PostAsync("/token", formContent).Result;
            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"Error getting token from Maskinporten. Got http code {response.StatusCode}");
            }

            JObject _accessTokenObject = JsonConvert.DeserializeObject<JObject>(response.Content.ReadAsStringAsync().Result);

            if (_useAltinnJwt)
            {
                HttpClient altinnJwtClient = GetAltinnJwtClient();
                altinnJwtClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessTokenObject.Value<string>("access_token"));
                response = altinnJwtClient.GetAsync("/authentication/api/v1/exchange/maskinporten").Result;
                if (!response.IsSuccessStatusCode)
                {
                    throw new Exception($"Error calling Altinn exchange Maskinporten token service. Got http code {response.StatusCode}");
                }

                _accessTokenObject["access_token"] = response.Content.ReadAsStringAsync().Result;
            }

            return _accessTokenObject.Value<string>("access_token");
        }

        /// <summary>
        /// Builds a Jwt assertion to be used to retrieve accesstoken from Maskinporten. Saves it internally in object
        /// </summary>
        private string BuildJwtAssertion()
        {
            DateTime _authorizationDate = DateTime.UtcNow;
            X509SecurityKey securityKey = new X509SecurityKey(_certificate);
            JwtHeader header = new JwtHeader(new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256))
            {
                {
                    "x5c", new List<string>() { Convert.ToBase64String(_certificate.GetRawCertData()) }
                }
            };
            header.Remove("typ");
            header.Remove("kid");

            JwtPayload payload = new JwtPayload
            {
                { "aud", _audience },
                { "resource", _resource },
                { "scope", _scopes },
                { "iss", _clientId },
                { "exp", ToUnixTimeSeconds(_authorizationDate) + _tokenTtl },
                { "iat", ToUnixTimeSeconds(_authorizationDate) },
                { "jti", Guid.NewGuid().ToString() },
            };

            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        }

        /// <summary>
        /// Gets a Http Client that communicates with the Maskinporten
        /// </summary>
        /// <returns>The Maskinporten Http Client</returns>
        private HttpClient GetMaskinportenClient()
        {
            if (!_httpClients.ContainsKey(_maskinportenEndpoint))
            {
                lock (_lockObject)
                {
                    if (!_httpClients.ContainsKey(_maskinportenEndpoint))
                    {
                        _httpClients.Add(_maskinportenEndpoint, new HttpClient() { BaseAddress = new Uri(_maskinportenEndpoint) });
                    }
                }
            }

            return _httpClients[_maskinportenEndpoint];
        }

        /// <summary>
        /// Gets a Http Client that communicates with the Altinn Jwt
        /// </summary>
        /// <returns>The Altinn Jwt Http Client</returns>
        private HttpClient GetAltinnJwtClient()
        {
            if (!_httpClients.ContainsKey(_altinnEndpoint))
            {
                lock (_lockObject)
                {
                    if (!_httpClients.ContainsKey(_altinnEndpoint))
                    {
                        _httpClients.Add(_altinnEndpoint, new HttpClient() { BaseAddress = new Uri(_altinnEndpoint) });
                    }
                }
            }

            return _httpClients[_altinnEndpoint];
        }

        private long ToUnixTimeSeconds(DateTime d)
        {
            TimeSpan diff = d - new DateTime(1970, 1, 1, 0, 0, 0);
            return (long)diff.TotalSeconds;
        }
    }
}
