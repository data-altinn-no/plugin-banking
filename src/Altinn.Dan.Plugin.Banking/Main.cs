using Altinn.Dan.Plugin.Banking.Clients;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Utils;
using Azure.Core.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nadobe;
using Nadobe.Common.Exceptions;
using Nadobe.Common.Models;
using Nadobe.Common.Util;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking
{
    public class Main
    {
        private ILogger _logger;
        private HttpClient _client;
        private ApplicationSettings _settings;
        private Guid _accountInfoRequestID = Guid.NewGuid();
        private Guid _correlationID = Guid.NewGuid();

        public Main(IHttpClientFactory httpClientFactory, IOptions<ApplicationSettings> settings)
        {
            _client = httpClientFactory.CreateClient("SafeHttpClient");
            _settings = settings.Value;
        }

        [Function("Banktransaksjoner")]
        public async Task<HttpResponseData> Dataset1(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequestData req,
            FunctionContext context)
        {
            _logger = context.GetLogger(context.FunctionDefinition.Name);
            _logger.LogInformation("Running func 'BankTransaksjoner'");

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var evidenceHarvesterRequest = JsonConvert.DeserializeObject<EvidenceHarvesterRequest>(requestBody);

            var actionResult = await EvidenceSourceResponse.CreateResponse(null, () => GetEvidenceValuesBankTransaksjoner(evidenceHarvesterRequest)) as ObjectResult;
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(actionResult?.Value);
            return response;
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesBankTransaksjoner(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var mpToken = GetToken();
            var kar = new KAR(_client);
            kar.BaseUrl = _settings.KarUrl;

            try
            {
                string toDate = DateTime.Now.ToString("yyyy-MM-dd");
                string fromDate = DateTime.Now.AddMonths(-3).ToString("yyyy-MM-dd");

                var response = await kar.Get(evidenceHarvesterRequest.OrganizationNumber, mpToken, fromDate, toDate, _accountInfoRequestID, _correlationID);

                if (response.Banks.Count == 0)
                    return new List<EvidenceValue>();

                string bankList = null;
                foreach (var a in response.Banks)
                {
                    bankList += $"{a.OrganizationID}:{a.BankName};";
                }

                var banks = bankList.TrimEnd(';');

                var bank = new Bank(_client);
                var bankResult = await bank.Get(OEDUtils.MapSsn(evidenceHarvesterRequest.OrganizationNumber, "bank"), banks, _settings, DateTimeOffset.Parse(fromDate), DateTimeOffset.Parse(toDate), _accountInfoRequestID, _correlationID);

                var ecb = new EvidenceBuilder(new Metadata(), "Banktransaksjoner");
                ecb.AddEvidenceValue("default", JsonConvert.SerializeObject(bankResult));

                return ecb.GetEvidenceValues();
            }
            catch (Exception e)
            {
                _logger.LogError(String.Format("Banktransaksjoner failed for {0}, error {1} (accountInfoRequestID: {2}, correlationID: {3})",
                    evidenceHarvesterRequest.OrganizationNumber.Length == 11 ? evidenceHarvesterRequest.OrganizationNumber.Substring(0, 6) : evidenceHarvesterRequest.OrganizationNumber, e.Message, _accountInfoRequestID, _correlationID));
                throw new EvidenceSourceTransientException(Altinn.Dan.Plugin.Banking.Metadata.ERROR_CCR_UPSTREAM_ERROR, "Could not retrieve bank transactions");

            }
        }

        private string GetToken(string audience = null)
        {
            var mp = new MaskinportenUtil(audience, "bits:kundeforhold", _settings.ClientId, false, "https://ver2.maskinporten.no/", _settings.Certificate, "https://ver2.maskinporten.no/", null);
            return mp.GetToken();
        }



        [Function(Constants.EvidenceSourceMetadataFunctionName)]
        public async Task<HttpResponseData> Metadata(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequestData req, FunctionContext context)
        {
            _logger = context.GetLogger(context.FunctionDefinition.Name);
            _logger.LogInformation($"Running metadata for {Constants.EvidenceSourceMetadataFunctionName}");
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new Metadata().GetEvidenceCodes(), new NewtonsoftJsonObjectSerializer(new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }));
            return response;
        }
    }
}
