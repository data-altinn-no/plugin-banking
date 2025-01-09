using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Exceptions;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services;
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger _logger;
        private readonly HttpClient _client;
        private readonly ApplicationSettings _settings;
        private readonly IMemoryCacheProvider _memCache;
        private const string ENDPOINTS_KEY = "endpoints_key";

        public Plugin(IOptions<ApplicationSettings> settings, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory, IBankService bankService, IKARService karService, IMemoryCacheProvider memCache)
        {
            _bankService = bankService;
            _karService = karService;
            _httpClientFactory = httpClientFactory;
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
            return await EvidenceSourceResponse.CreateResponse(req, () => GetEvidenceValuesBankTransaksjoner(evidenceHarvesterRequest));
        }

        [Function("Kundeforhold")]
        public async Task<HttpResponseData> GetKundeforhold(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();
            return await EvidenceSourceResponse.CreateResponse(req, () => GetKundeforhold(evidenceHarvesterRequest));
        }

        [Function("Kontotransaksjoner")]
        public async Task<HttpResponseData> GetKontotransaksjoner(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();
            return await EvidenceSourceResponse.CreateResponse(req, () => GetKontotransaksjoner(evidenceHarvesterRequest));
        }

        [Function("Kontodetaljer")]
        public async Task<HttpResponseData> GetBankRelasjon(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();
            return await EvidenceSourceResponse.CreateResponse(req, () => GetKontodetaljer(evidenceHarvesterRequest));
        }

        [Function("Kontrollinformasjon")]
        public async Task<HttpResponseData> GetKontrollinformasjon(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();

            return await EvidenceSourceResponse.CreateResponse(req, () => GetEvidenceValuesKontrollinformasjon());
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

        [Function(Constants.EvidenceSourceMetadataFunctionName)]
        public async Task<HttpResponseData> Metadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req, FunctionContext context)
        {
            _logger.LogInformation($"Running metadata for {Constants.EvidenceSourceMetadataFunctionName}");
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new Metadata().GetEvidenceCodes(), new NewtonsoftJsonObjectSerializer(new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }));
            return response;
        }

        private async Task<List<EvidenceValue>> GetKontotransaksjoner(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var accountInfoRequestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var bankEndpoints = await GetBankEndpoints();
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

                //accountinforequestid must be provided in parameter in order to maintain the correct use across requests from different users of digitalt dødsbo
                accountInfoRequestId = evidenceHarvesterRequest.TryGetParameter("ReferanseId", out string accountInfoRequestIdFromParam) ? new Guid(accountInfoRequestIdFromParam) : accountInfoRequestId;
                var accountRef = evidenceHarvesterRequest.TryGetParameter("Kontoreferanse", out string accountRefParam) ? accountRefParam : string.Empty;
                var orgno = evidenceHarvesterRequest.TryGetParameter("Organisasjonsnummer", out string orgnoParam) ? orgnoParam : string.Empty;

                var filteredEndpoint = bankEndpoints.Where(item => _settings.ImplementedBanks.Contains(item.OrgNo) && item.OrgNo == orgno && item.Version.ToUpper() == "V2").FirstOrDefault();
                var ecb = new EvidenceBuilder(new Metadata(), "Kontotransaksjoner");
              
                var bankConfig = CreateBankConfigurations(filteredEndpoint);
                var transactions = await _bankService.GetTransactionsForAccount(ssn, bankConfig, fromDate, toDate, accountInfoRequestId, accountRef);

                ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(transactions), "", false);
                return ecb.GetEvidenceValues();
            }
            catch (Exception e)
            {
                if (e is DanException) throw;

                _logger.LogError(
                    "BanktransaksjonerKonto failed unexpectedly for {Subject}, error {Error} (accountInfoRequestId: {AccountInfoRequestId}, correlationID: {CorrelationId})",
                    evidenceHarvesterRequest.SubjectParty.GetAsString(), e.Message, accountInfoRequestId, correlationId);
                throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_BANK_REQUEST_ERROR, "Could not retrieve bank transactions");

            }
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesKundeforhold(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var accountInfoRequestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var endpoints = await GetBankEndpoints();
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

                //accountinforequestid must be provided in parameter in order to maintain the correct use across requests from different users of digitalt dødsbo
                accountInfoRequestId = evidenceHarvesterRequest.TryGetParameter("ReferanseId", out string accountInfoRequestIdFromParam) ? new Guid(accountInfoRequestIdFromParam) : accountInfoRequestId;

                KARResponse karResponse;
                try
                {
                    DateTimeOffset fromDateDto = new DateTimeOffset(fromDate);
                    DateTimeOffset toDateDto = new DateTimeOffset(toDate);
                    //Skipping KAR lookups can be set both in requests and config, useful for testing in different environments to see if all banks are responding as expected 
                    karResponse = await _karService.GetBanksForCustomer(ssn, fromDateDto, toDateDto, accountInfoRequestId, correlationId, skipKAR || _settings.SkipKAR);
                }
                catch (ApiException e)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_KAR_NOT_AVAILABLE_ERROR, $"Request to KAR failed (HTTP status code: {e.StatusCode}, accountInfoRequestId: {accountInfoRequestId}, correlationID: {correlationId})");
                }
                catch (TaskCanceledException)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_KAR_NOT_AVAILABLE_ERROR, $"Request to KAR timed out (accountInfoRequestId: {accountInfoRequestId}, correlationID: {correlationId})");
                }

                var response = new BankRelations();               
                foreach(var relation in karResponse.Banks)
                {
                    response.Banks.Add(new BankRelation() { BankName = relation.BankName, OrganizationNumber = relation.OrganizationID, IsImplemented = _settings.ImplementedBanks.Contains(relation.OrganizationID) });
                }                

                var ecb = new EvidenceBuilder(new Metadata(), "Kundeforhold");
                ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(response), "", false);
                return ecb.GetEvidenceValues();
            }
            catch (Exception e)
            {
                if (e is DanException) throw;

                _logger.LogError(
                    "Kundeforhold failed unexpectedly for {Subject}, error {Error} (accountInfoRequestId: {AccountInfoRequestId}, correlationID: {CorrelationId})",
                    evidenceHarvesterRequest.SubjectParty.GetAsString(), e.Message, accountInfoRequestId, correlationId);
                throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_BANK_REQUEST_ERROR, "Could not retrieve bank transactions");
            }
        }

        private async Task<List<EvidenceValue>> GetKontodetaljer(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var accountInfoRequestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();
            var endpoints = await GetBankEndpoints();
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

                var orgno = evidenceHarvesterRequest.TryGetParameter("Organisasjonsnummer", out string paramOrgNo) ? paramOrgNo : null;
                var includeTransactions = evidenceHarvesterRequest.TryGetParameter("InkluderTransaksjoner", out bool paramIncludeTransactions) ? paramIncludeTransactions : true;

                //accountinforequestid must be provided in parameter in order to maintain the correct use across requests from different users of digitalt dødsbo
                accountInfoRequestId = evidenceHarvesterRequest.TryGetParameter("ReferanseId", out string accountInfoRequestIdFromParam) ? new Guid(accountInfoRequestIdFromParam) : accountInfoRequestId;
                var filteredEndpoints = endpoints.Where(item => _settings.ImplementedBanks.Contains(item.OrgNo) && item.OrgNo == orgno && item.Version.ToUpper() == "V2").ToList();

                if (string.IsNullOrEmpty(orgno) || filteredEndpoints.Count != 1)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_BANK_REQUEST_ERROR, "Invalid organisation number provided");
                }

                var ecb = new EvidenceBuilder(new Metadata(), "Kontodetaljer");
                var bankConfigs = CreateBankConfigurations(filteredEndpoints);
                var bankResult = await _bankService.GetAccounts(ssn, bankConfigs, fromDate, toDate, accountInfoRequestId, includeTransactions);

                ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(bankResult), "", false);
                return ecb.GetEvidenceValues();
            }
            catch (Exception e)
            {
                if (e is DanException) throw;

                _logger.LogError(
                    "Kontodetaljer failed unexpectedly for {Subject}, error {Error} (accountInfoRequestId: {AccountInfoRequestId}, correlationID: {CorrelationId})",
                    evidenceHarvesterRequest.SubjectParty.GetAsString(), e.Message, accountInfoRequestId, correlationId);
                throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_BANK_REQUEST_ERROR, "Could not retrieve account details");
            }
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesKontrollinformasjon()
        {
            var ecb = new EvidenceBuilder(new Metadata(), "Kontrollinformasjon");
            ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(await GetBankEndpoints()), "BITS", false);

            return ecb.GetEvidenceValues();
        }

        private async Task<List<EndpointExternal>> GetBankEndpoints()
        {
            (bool hasCachedValue, var endpoints) = await _memCache.TryGetEndpoints(ENDPOINTS_KEY);

            if (!hasCachedValue)
            {
                endpoints = await ReadEndpointsAndCache();
            }

            return endpoints;
        }

        private Dictionary<string, BankConfig> CreateBankConfigurations(List<EndpointExternal> banks)
        {

            Dictionary<string, BankConfig> bankConfigs = [];
            foreach (var bank in banks)
            {
                var httpClient = _httpClientFactory.CreateClient(bank.OrgNo);
                httpClient.BaseAddress = new Uri(bank.Url);

                bankConfigs.Add(bank.OrgNo, new BankConfig()
                {
                    BankAudience = bank.Url,
                    Client = httpClient,
                    MaskinportenEnv = _settings.MaskinportenEnvironment,
                    ApiVersion = bank.Version,
                    Name = bank.Name,
                    OrgNo = bank.OrgNo
                });
            }

            return bankConfigs;
        }

        private BankConfig CreateBankConfigurations(EndpointExternal bank)
            => CreateBankConfigurations([bank]).Single().Value;

        private async Task<List<EndpointExternal>> ReadEndpointsAndCache()
        {
            var file = await GetFileFromGithub();
            // var file = Encoding.UTF8.GetString(bytes,0, bytes.Length);

            var engine = new DelimitedFileEngine<EndpointV2>(Encoding.UTF8);
            var endpoints = engine.ReadString(file).ToList();

            List<EndpointExternal> result = new List<EndpointExternal>();
            _logger.LogInformation($"Endpoints parsed from csv - {engine.TotalRecords} to be cached");

            if (engine.TotalRecords > 0 && endpoints.Count > 0)
            {
                result = await _memCache.SetEndpointsCache(ENDPOINTS_KEY, endpoints, TimeSpan.FromMinutes(60));
                _logger.LogInformation($"Cache refresh completed - total of {engine.TotalRecords} cached");
            }
            else
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

        private async Task<List<EvidenceValue>> GetEvidenceValuesBankTransaksjoner(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var accountInfoRequestId = Guid.NewGuid();
            var correlationId = Guid.NewGuid();

            var endpoints = await GetBankEndpoints();

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

                //accountinforequestid must be provided in parameter in order to maintain the correct use across requests from different users of digitalt dødsbo
                accountInfoRequestId = evidenceHarvesterRequest.TryGetParameter("ReferanseId", out string accountInfoRequestIdFromParam) ? new Guid(accountInfoRequestIdFromParam) : accountInfoRequestId;

                KARResponse karResponse;
                try
                {
                    DateTimeOffset fromDateDto = new DateTimeOffset(fromDate);
                    DateTimeOffset toDateDto = new DateTimeOffset(toDate);
                    //Skipping KAR lookups can be set both in requests and config, useful for testing in different environments to see if all banks are responding as expected 
                    karResponse = await _karService.GetBanksForCustomer(ssn, fromDateDto, toDateDto, accountInfoRequestId, correlationId, skipKAR || _settings.SkipKAR);
                }
                catch (ApiException e)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_KAR_NOT_AVAILABLE_ERROR, $"Request to KAR failed (HTTP status code: {e.StatusCode}, accountInfoRequestId: {accountInfoRequestId}, correlationID: {correlationId})");
                }
                catch (TaskCanceledException)
                {
                    throw new EvidenceSourceTransientException(Banking.Metadata.ERROR_KAR_NOT_AVAILABLE_ERROR, $"Request to KAR timed out (accountInfoRequestId: {accountInfoRequestId}, correlationID: {correlationId})");
                }

                var filteredEndpoints = endpoints.Where(p => karResponse.Banks.Select(e => e.OrganizationID).ToHashSet().Contains(p.OrgNo)).Where(item => _settings.ImplementedBanks.Contains(item.OrgNo)).ToList();

                //We are only legally allowed to use endpoints with version V2 due to the onlyPrimaryOwner flag
                filteredEndpoints.RemoveAll(p => p.Version.ToUpper() == "V1");

                var ecb = new EvidenceBuilder(new Metadata(), "Banktransaksjoner");
                var bankConfigs = CreateBankConfigurations(filteredEndpoints);
                BankResponse bankResult = karResponse.Banks.Count > 0 ? await _bankService.GetAccounts(ssn, bankConfigs, fromDate, toDate, accountInfoRequestId) : new() { BankAccounts = new() };

                //Add banks with implemented = false if they are in the response from KAR but not supported by digitalt dødsbo
                foreach (var bank in karResponse.Banks.Where(p => !filteredEndpoints.Select(e => e.OrgNo).Contains(p.OrganizationID)))
                {
                    bankResult.BankAccounts.Add(new BankInfo() { BankName = bank.BankName, IsImplemented = false });
                }
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
    }
}
