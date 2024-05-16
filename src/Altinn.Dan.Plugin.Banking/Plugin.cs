using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Exceptions;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Altinn.Dan.Plugin.Banking.Utils;
using Azure.Core.Serialization;
using Dan.Common;
using Dan.Common.Exceptions;
using Dan.Common.Extensions;
using Dan.Common.Models;
using Dan.Common.Util;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection.Metadata.Ecma335;
using System.Threading.Tasks;
using System.Web;
using FileHelpers;
using System.Text;
using FileHelpers.Options;

namespace Altinn.Dan.Plugin.Banking
{
    public class Plugin
    {
        private readonly IBankService _bankService;
        private readonly IKARService _karService;
        private readonly ILogger _logger;
        private readonly HttpClient _client;
        private readonly ApplicationSettings _settings;
        private readonly IMemoryCacheProvider _memCache;
        private const string ENDPOINTS_KEY = "endpoints_key";

        public Plugin(IOptions<ApplicationSettings> settings, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, IBankService bankService, IKARService karService, IMemoryCacheProvider memCache)
        {
            _bankService = bankService;
            _karService = karService;
            _client = httpClientFactory.CreateClient("SafeHttpClient");
            _settings = settings.Value;
            _logger = loggerFactory.CreateLogger<Plugin>();
            _memCache = memCache;
        }

        private async Task SetEndpoints()
        {
            try
            {
                if (_settings.Endpoints == null || _settings.Endpoints.endpoints?.Length < 1)
                {
                    var implemented = _settings.ImplementedBanks.Split(",");

                    var response = await _client.GetAsync(_settings.FDKEndpointsUrl).ConfigureAwait(false);
                    var temp = JsonConvert.DeserializeObject<KontoOpplysninger>(await response.Content.ReadAsStringAsync());
                    var result = new KontoOpplysninger
                    {
                        endpoints = new Endpoint[implemented.Length]
                    };


                    int i = 0;
                    if (temp != null)
                        foreach (var ep in temp.endpoints)
                        {
                            if (implemented.Contains(ep.orgNo) && (!result.endpoints.Any(x => x.AreEqual(ep.orgNo))))
                            {
                                ep.url = _settings.UseProxy ? string.Format(string.Format(_settings.ProxyUrl, HttpUtility.HtmlEncode(ep.url.Replace("https://", "")))) : ep.url;
                                result.endpoints[i] = ep;
                                i++;
                            }
                        }

                    _settings.Endpoints = result;
                    _logger.LogInformation("Fetched list of banks from FDK: {@Banks}", _settings.Endpoints);

                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed retrieving endpoints from the api catalogue at {FdkEndpointUrl}: {Error}", _settings.FDKEndpointsUrl, ex.Message);
                throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_METADATA_LOOKUP_ERROR, $"Failed retrieving endpoints from the api catalogue");
            }
        }


        [Function("Banktransaksjoner")]
        public async Task<HttpResponseData> GetBanktransaksjoner(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            await SetEndpoints();
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();

            /* // For local debug, returns unenveloped JSON (but doesn't handle exceptions)
            var ret = await GetEvidenceValuesBankTransaksjoner(evidenceHarvesterRequest);
            var response =  HttpResponseData.CreateResponse(req);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(ret.First().Value.ToString());
            return response;
            */

            return await EvidenceSourceResponse.CreateResponse(req, () => GetEvidenceValuesBankTransaksjoner(evidenceHarvesterRequest));
        }

        [Function("Kontrollinformasjon")]
        public async Task<HttpResponseData> GetKontrollinformasjon(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();

            return await EvidenceSourceResponse.CreateResponse(req, () => GetEvidenceValuesKontrollinformasjon());
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesKontrollinformasjon()
        {
            (bool hasCachedValue, var endpoints) = await _memCache.TryGetEndpoints(ENDPOINTS_KEY);

            if (!hasCachedValue)
            {
                endpoints = await ReadEndpointsAndCache();
            }

            var ecb = new EvidenceBuilder(new Metadata(), "Kontrollinformasjon");
            ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(endpoints), "BITS", false);

            return ecb.GetEvidenceValues();
        }

        private async Task<List<EndpointExternal>> ReadEndpointsAndCache()
        {
            var bytes = await _client.GetByteArrayAsync(_settings.EndpointsResourceFile);
            var file = Encoding.UTF8.GetString(bytes,0, bytes.Length);

            var engine = new DelimitedFileEngine<EndpointV2>(Encoding.UTF8);
            var endpoints = engine.ReadString(file).ToList();

            List<EndpointExternal> result = new List<EndpointExternal>();
            _logger.LogInformation($"Endpoints parsed from csv - {engine.TotalRecords} to be cached");

            if (engine.TotalRecords > 0 && endpoints.Count>0)
            {               
                result = await _memCache.SetEndpointsCache(ENDPOINTS_KEY, endpoints, TimeSpan.FromMinutes(60));
                _logger.LogInformation($"Cache refresh completed - total of {engine.TotalRecords} cached");
            } else
            {
                _logger.LogCritical($"Plugin func-es-banking no endpoints found in csv!!!");
            }

            return result;
        }

        //Env =
        private List<EndpointExternal> MapToExternal(List<EndpointV2> endpoints)
        {
            var query = from endpoint in endpoints
                        select new EndpointExternal()
                        {
                            Env = _settings.UseTestEndpoints ? "test" : "prod",
                            Name = endpoint.Navn,
                            OrgNo = endpoint.OrgNummer,
                            Url = _settings.UseTestEndpoints ? endpoint.EndepunktTest : endpoint.EndepunktProduksjon,
                            Version = endpoint.Version,
                        };

            return query.ToList();
        }

        [Function("OppdaterKontrollinformasjon")]
        public async Task<HttpResponseData> UpdateKontrollinformasjon(
            [HttpTrigger(AuthorizationLevel.Function, "get")] HttpRequestData req,
            FunctionContext context)
        {
            var endpoints = await ReadEndpointsAndCache();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new EndpointsList() { Endpoints = endpoints, Total = endpoints.Count });
            return response;
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesBankTransaksjoner(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var accountInfoRequestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();

            var ssn = evidenceHarvesterRequest.SubjectParty?.NorwegianSocialSecurityNumber;

            try
            {
                // TODO! Add DateTimeOffset overload for TryGetParameter
                var fromDate = evidenceHarvesterRequest.TryGetParameter("FraDato", out DateTime paramFromDate)
                    ? paramFromDate
                    : DateTime.Now.AddMonths(-3);

                var toDate = evidenceHarvesterRequest.TryGetParameter("TilDato", out DateTime paramToDate)
                    ? paramToDate
                    : DateTime.Now;

                bool skipKAR = evidenceHarvesterRequest.TryGetParameter("SkipKAR", out bool paramSkipKAR) ? paramSkipKAR : false;

                KARResponse karResponse;
                try
                {
                    DateTimeOffset todayDto = new DateTimeOffset(DateTime.Now);
                    //Skipping KAR lookups can be set both in requests and config, useful for testing in different environments to see if all banks are responding as expected 
                    karResponse = await _karService.GetBanksForCustomer(ssn, todayDto, todayDto, accountInfoRequestId, correlationId, skipKAR || _settings.SkipKAR);
                }
                catch (ApiException e)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_KAR_NOT_AVAILABLE_ERROR, $"Request to KAR failed (HTTP status code: {e.StatusCode}, accountInfoRequestId: {accountInfoRequestId}, correlationID: {correlationId})");
                }
                catch (TaskCanceledException)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_KAR_NOT_AVAILABLE_ERROR, $"Request to KAR timed out (accountInfoRequestId: {accountInfoRequestId}, correlationID: {correlationId})");
                }

                if (karResponse.Banks.Count == 0)
                    return new List<EvidenceValue>();

                var bankResult = await _bankService.GetTransactions(ssn, karResponse, fromDate, toDate, accountInfoRequestId, correlationId);

                var ecb = new EvidenceBuilder(new Metadata(), "Banktransaksjoner");
                ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(bankResult), "", false);

                return ecb.GetEvidenceValues();
            }
            catch (Exception e)
            {
                if (e is DanException) throw;

                _logger.LogError(
                    "Banktransaksjoner failed unexpectedly for {Subject}, error {Error} (accountInfoRequestId: {AccountInfoRequestId}, correlationID: {CorrelationId})",
                    evidenceHarvesterRequest.SubjectParty.GetAsString(), e.Message, accountInfoRequestId, correlationId);
                throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_BANK_REQUEST_ERROR, "Could not retrieve bank transactions");

            }
        }

        [Function(Constants.EvidenceSourceMetadataFunctionName)]
        public async Task<HttpResponseData> Metadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req, FunctionContext context)
        {
            _logger.LogInformation($"Running metadata for {Constants.EvidenceSourceMetadataFunctionName}");
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new Metadata().GetEvidenceCodes(), new NewtonsoftJsonObjectSerializer(new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }));
            return response;
        }
    }
}
