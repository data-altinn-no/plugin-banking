using Altinn.Dan.Plugin.Banking.Clients;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Utils;
using Azure.Core.Serialization;
using Dan.Common;
using Dan.Common.Exceptions;
using Dan.Common.Models;
using Dan.Common.Util;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dan.Common.Extensions;

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

            //adjust for frequent KAR timeouts
            _client.Timeout = new TimeSpan(0, 0, 10, 30);
            _settings = settings.Value;
        }

        [Function("Banktransaksjoner")]
        public async Task<HttpResponseData> GetBanktransaksjoner(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
            FunctionContext context)
        {
            _logger = context.GetLogger(context.FunctionDefinition.Name);
            _logger.LogInformation("Running func 'BankTransaksjoner'");

            var evidenceHarvesterRequest = await req.ReadFromJsonAsync<EvidenceHarvesterRequest>();

            return await EvidenceSourceResponse.CreateResponse(req, () => GetEvidenceValuesBankTransaksjoner(evidenceHarvesterRequest));
        }

        private async Task<List<EvidenceValue>> GetEvidenceValuesBankTransaksjoner(EvidenceHarvesterRequest evidenceHarvesterRequest)
        {
            var mpToken = GetToken();
            var kar = new KAR(_client);
            kar.BaseUrl = _settings.KarUrl;


            try
            {
                string fromDate;
                string toDate;

                if (evidenceHarvesterRequest.TryGetParameter("FraDato", out DateTime paramDate))
                {
                    fromDate = paramDate.ToString("yyyy-MM-dd");
                }
                else
                {
                    fromDate = DateTime.Now.AddMonths(-3).ToString("yyyy-MM-dd");
                }

                if (evidenceHarvesterRequest.TryGetParameter("TilDato", out paramDate))
                {
                    toDate = paramDate.ToString("yyyy-MM-dd");
                }
                else
                {
                    toDate = DateTime.Now.ToString("yyyy-MM-dd");
                }

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
                var bankResult = await bank.Get(OEDUtils.MapSsn(evidenceHarvesterRequest.OrganizationNumber, "bank"), banks, _settings, DateTimeOffset.Parse(fromDate), DateTimeOffset.Parse(toDate), _accountInfoRequestID, _correlationID, _logger);

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
            [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req, FunctionContext context)
        {
            _logger = context.GetLogger(context.FunctionDefinition.Name);
            _logger.LogInformation($"Running metadata for {Constants.EvidenceSourceMetadataFunctionName}");
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new Metadata().GetEvidenceCodes(), new NewtonsoftJsonObjectSerializer(new JsonSerializerSettings() { TypeNameHandling = TypeNameHandling.Auto }));
            return response;
        }
    }
}
