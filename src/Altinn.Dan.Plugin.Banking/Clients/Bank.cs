using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Exceptions;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Utils;
using Dan.Common.Exceptions;
using Jose;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Clients
{
    public partial class Bank
    {
        private Accounts _accounts = null;
        private AccountDetails _details = null;
        private Transactions _transactions = null;
        private Guid _accountInfoRequestID;
        private Guid _correlationID;

        private static Dictionary<string, BankConfig> _bankConfigs = null;
        private static object _lockObject = new object();
        private ApplicationSettings _danSettings;

        /// <summary>
        /// Nswag doesn't provide a way to manipulate the response payload before json deserialization. This method implements a workaround.
        /// If the custom decryption and json deserialization succeed, the result is stored in the private field accounts or details,
        /// and a dummy exeption it thrown to abort processing in the genereated code BankClient. The dummy exception is caught
        /// in the Get method, and the result is restored from the private field accounts/details.
        /// </summary>
        /// <param name="client">Http client</param>
        /// <param name="response">Response from bank</param>
        partial void ProcessResponse(HttpClient client, HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
            {
                string decryptedResponse = string.Empty;


                try
                {
                    var jwt = response.Content.ReadAsStringAsync().Result;

                    decryptedResponse = JWT.Decode(response.Content.ReadAsStringAsync().Result, _danSettings.OedDecryptCert.GetRSAPrivateKey());//, JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);



                    if (response.RequestMessage.RequestUri.LocalPath.EndsWith("accounts", StringComparison.OrdinalIgnoreCase))
                        _accounts = Newtonsoft.Json.JsonConvert.DeserializeObject<Accounts>(decryptedResponse, JsonSerializerSettings);
                    else if (response.RequestMessage.RequestUri.LocalPath.EndsWith("transactions", StringComparison.OrdinalIgnoreCase))
                        _transactions = Newtonsoft.Json.JsonConvert.DeserializeObject<Transactions>(decryptedResponse, JsonSerializerSettings);
                    else if (response.RequestMessage.RequestUri.LocalPath.Contains("accounts", StringComparison.OrdinalIgnoreCase))
                        _details = Newtonsoft.Json.JsonConvert.DeserializeObject<AccountDetails>(decryptedResponse, JsonSerializerSettings);
                    else
                        throw new Exception(@"Unexpected uri {response.RequestMessage.RequestUri.LocalPath}");

                    throw new DecryptionCompletedDummyException(); // This is expected
                }
                catch (Newtonsoft.Json.JsonException exception)
                {
                    var message = $"Could not deserialize the response body string for {response.RequestMessage.RequestUri.LocalPath}";
                    throw new ApiException(message, (int)response.StatusCode, decryptedResponse, System.Linq.Enumerable.ToDictionary(response.Headers, h_ => h_.Key, h_ => h_.Value), exception);
                }
            }
        }

        public async Task<BankResponse> Get(string ssn, string bankList, ApplicationSettings settings, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestID, Guid correlationID, ILogger logger, KontoOpplysninger endpoints)
        {
            _danSettings = settings;
            _accountInfoRequestID = accountInfoRequestID;
            _correlationID = correlationID;
            Configure(endpoints);

            BankResponse bankResponse = new BankResponse() { BankAccounts = new List<BankInfo>() };
            foreach (string bank in bankList.Split(';'))
            {
                string orgnr = bank.Split(':')[0];
                string name = bank.Split(':')[1];
                BankInfo bankInfo = null;
                try
                {
                    bankInfo = await InvokeBank(ssn, orgnr, fromDate, toDate);
                }
                catch (Exception e)
                {
                    bankInfo = new BankInfo() { Exception = e };
                    logger.LogError(String.Format("Banktransaksjoner failed for {0}, error {1} (accountInfoRequestID: {2}, correlationID: {3})", ssn.Substring(0, 6), e.Message, _accountInfoRequestID, _correlationID));
                }

                bankInfo.BankName = name;
                bankResponse.BankAccounts.Add(bankInfo);
                bankResponse.TotalBalance += bankInfo.TotalBalance;
            }

            return bankResponse;
        }

        private async Task<BankInfo> InvokeBank(string ssn, string orgnr, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            if (!_bankConfigs.ContainsKey(orgnr))
                return new BankInfo { IsImplemented = false };

            BankConfig bankConfig = _bankConfigs[orgnr];
            _httpClient = bankConfig.Client;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new MaskinportenUtil(bankConfig.BankAudience, "bits:kontoinformasjon.oed", _danSettings.ClientId,
                false, bankConfig.MaskinportenAudience, _danSettings.Certificate, _danSettings.MaskinportenEndpoint, null).GetToken());

            BaseUrl = bankConfig.Client.BaseAddress.ToString();
            try
            {
                await this.ListAccountsAsync(_accountInfoRequestID, _correlationID, "OED", ssn, null, null, null, fromDate, toDate);
            }
            catch (DecryptionCompletedDummyException)
            {
                return await GetAccountDetails(_accounts);
            }

            throw new EvidenceSourceTransientException(Metadata.ERROR_CCR_UPSTREAM_ERROR, $"Code bug calling list accounts for {ssn}");
        }

        private async Task<BankInfo> GetAccountDetails(Accounts accounts)
        {
            //TODO: Take currency into account, expand model 
            BankInfo bankInfo = new BankInfo() { Accounts = new List<Altinn.Dan.Plugin.Banking.Models.Account>() };
            foreach (Account account in accounts.Accounts1)
            {
                try
                {
                    await this.ShowAccountByIdAsync(account.AccountReference, _accountInfoRequestID, _correlationID, "OED", null, null, null, null);
                }
                catch (DecryptionCompletedDummyException)
                {
                    var credit = _details.Account.Balances.FirstOrDefault(b => b.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)?.Amount ?? 0;
                    var debit = _details.Account.Balances.FirstOrDefault(b => b.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)?.Amount ?? 0;
                    try
                    {
                        await this.ListTransactionsAsync(account.AccountReference, _accountInfoRequestID, _correlationID, "OED", null, null, DateTime.Now.AddMonths(-3), DateTime.Now);
                    }
                    catch (DecryptionCompletedDummyException)
                    {
                        bankInfo.TotalBalance += credit;
                        bankInfo.TotalBalance -= debit;
                    }

                    bankInfo.Accounts.Add(MapToInternal(_details.Account, _transactions.Transactions1, credit - debit));
                }
            }

            return bankInfo;
        }

        private Altinn.Dan.Plugin.Banking.Models.Account MapToInternal(
            AccountDetail detail,
            ICollection<Transaction> transactions,
            double balance)
        {
            // P.t. almost passthrough mapping
            return new Altinn.Dan.Plugin.Banking.Models.Account()
            {
                AccountNumber = detail.AccountIdentifier,
                AccountDetail = detail,
                Transactions = transactions,
                AccountBalance = balance
            };
        }

        private void Configure(KontoOpplysninger banks)
        {
            if (_bankConfigs == null)
                lock (_lockObject)
                    if (_bankConfigs == null)
                    {
                        _bankConfigs = new Dictionary<string, BankConfig>();

                        foreach (var bank in banks.endpoints)
                        {
                            _bankConfigs.Add(bank.orgNo, new BankConfig()
                            {
                                BankUri = bank.url,
                                BankAudience = bank.url,
                                MaskinportenUri = _danSettings.MaskinportenEndpoint,
                                MaskinportenAudience = _danSettings.BankAudience,
                                Client = new HttpClient() { BaseAddress = new Uri(bank.url) }
                            });
                        }
                    }
        }
    }

    class DecryptionCompletedDummyException : Exception
    {
    }

    class BankConfig
    {
        public string BankUri { get; set; }
        public string MaskinportenUri { get; set; }
        public string MaskinportenAudience { get; set; }
        public HttpClient Client { get; set; }

        public string BankAudience { get; set; }
    }
}
