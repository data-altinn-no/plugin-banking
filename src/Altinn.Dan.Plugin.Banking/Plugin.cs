using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Exceptions;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Azure.Core.Serialization;
using Dan.Common;
using Dan.Common.Exceptions;
using Dan.Common.Extensions;
using Dan.Common.Models;
using Dan.Common.Util;
using FileHelpers;
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
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

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

        [Function("Banktransaksjoner")]
        public async Task<HttpResponseData> GetBanktransaksjoner(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();

            // For local debug, returns unenveloped JSON (but doesn't handle exceptions)
            /*
            var ret = await GetEvidenceValuesBankTransaksjoner(evidenceHarvesterRequest);
            var response =  HttpResponseData.CreateResponse(req);
            response.Headers.Add("Content-Type", "application/json");
            var list = new List<string>();

            ret.ForEach(item =>
            {
                list.Add(item.Value.ToString());
            });
            
            var responseItems = JsonConvert.SerializeObject(ret);

            await response.WriteStringAsync(responseItems);
            return response; */

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
            var ecb = new EvidenceBuilder(new Metadata(), "Kontrollinformasjon");
            ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(await GetEndpoints()), "BITS", false);

            return ecb.GetEvidenceValues();
        }

        private async Task<List<EndpointExternal>> GetEndpoints()
        {
            (bool hasCachedValue, var endpoints) = await _memCache.TryGetEndpoints(ENDPOINTS_KEY);

            if (!hasCachedValue)
            {
                endpoints = await ReadEndpointsAndCache();
            }

            return endpoints;
        }

        private async Task<List<EndpointExternal>> ReadEndpointsAndCache()
        {
            var file = await GetFileFromGithub();
           // var file = Encoding.UTF8.GetString(bytes,0, bytes.Length);

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

        private async Task<string> GetFileFromGithub()
        {
            _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw+json"));
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.GithubPAT);
            _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("plugin-banking"); // GitHub requires a User-Agent header

            // Make the GET request
            var url = $"https://api.github.com/repos/data-altinn-no/bits/contents/{_settings.EndpointsResourceFile}";
            HttpResponseMessage response = await _client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync();
            }
            else
            {
                return String.Empty;
            }
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

            var endpoints = await GetEndpoints();

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

                var filteredEndpoints = endpoints.Where(p => karResponse.Banks.Select(e => e.OrganizationID).ToHashSet().Contains(p.OrgNo)).Where(item =>_settings.ImplementedBanks.Contains(item.OrgNo)).ToList();

                //We are only legally allowed to use endpoints with version V2 due to the onlyPrimaryOwner flag
                filteredEndpoints.RemoveAll(p => p.Version == "V1");

                var ecb = new EvidenceBuilder(new Metadata(), "Banktransaksjoner");

                BankResponse bankResult = karResponse.Banks.Count > 0 ? await _bankService.GetTransactions(ssn, filteredEndpoints, fromDate, toDate, accountInfoRequestId, correlationId) : new() { BankAccounts = new()};
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
