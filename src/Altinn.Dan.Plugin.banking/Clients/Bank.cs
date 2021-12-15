using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Exceptions;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Utils;
using Jose;
using OfficeOpenXml;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
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
                
                X509Certificate2 certificate = null;
                string decryptedResponse = JWT.Decode(response.Content.ReadAsStringAsync().Result, certificate.GetRSAPrivateKey(), JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256);

                try
                {
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

        public async Task<BankResponse> Get(string ssn, string bankList, ApplicationSettings settings)
        {
            _danSettings = settings;
            Configure();

            BankResponse bankResponse = new BankResponse() { BankAccounts = new List<BankInfo>() };
            foreach (string bank in bankList.Split(';'))
            {
                string orgnr = bank.Split(':')[0];
                string name = bank.Split(':')[1];
                BankInfo bankInfo = null;
                try
                {
                    bankInfo = await InvokeBank(ssn, orgnr);
                }
                catch (Exception e)
                {
                    bankInfo = new BankInfo() { Exception = e };
                }

                bankInfo.BankName = name;
                bankResponse.BankAccounts.Add(bankInfo);
                bankResponse.TotalBalance += bankInfo.TotalBalance;
            }

            return bankResponse;
        }

        private async Task<BankInfo> InvokeBank(string ssn, string orgnr)
        {
            if (!_bankConfigs.ContainsKey(orgnr))
                return new BankInfo { IsImplemented = false };

            BankConfig bankConfig = _bankConfigs[orgnr];
            _httpClient = bankConfig.Client;
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new MaskinportenUtil(bankConfig.BankUri, "bits:kontoinformasjon.oed", _danSettings.ClientId,
                false, bankConfig.MaskinportenAudience, _danSettings.Certificate, _danSettings.MaskinportenEndpoint, null).GetToken());

            BaseUrl = "/exec.dsop/api/v1";
            try
            {
                await this.ListAccountsAsync(Guid.NewGuid(), Guid.NewGuid(), "OED", ssn, null, null, null, null, null);
            }
            catch (DecryptionCompletedDummyException)
            {
                return await GetAccountDetails(_accounts);
            }

            throw new Exception($"Code bug calling list accounts for {ssn}");
        }

        private async Task<BankInfo> GetAccountDetails(Accounts accounts)
        {
            BankInfo bankInfo = new BankInfo() { Accounts = new List<Altinn.Dan.Plugin.Banking.Models.Account>() };
            foreach (Account account in accounts.Accounts1)
            {
                try
                {
                    await this.ShowAccountByIdAsync(account.AccountReference, Guid.NewGuid(), Guid.NewGuid(), "OED", null, null, null, null);
                }
                catch (DecryptionCompletedDummyException)
                {
                    var credit = _details.Account.Balances.FirstOrDefault(b => b.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)?.Amount ?? 0;
                    var debit = _details.Account.Balances.FirstOrDefault(b => b.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)?.Amount ?? 0;
                    try
                    {
                        await this.ListTransactionsAsync(account.AccountReference, Guid.NewGuid(), Guid.NewGuid(), "OED", null, null, DateTime.Now.AddMonths(-3), DateTime.Now);
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

        private void Configure()
        {
            if (_bankConfigs == null)
                lock (_lockObject)
                    if (_bankConfigs == null)
                    {
                        _bankConfigs = new Dictionary<string, BankConfig>();

                        _bankConfigs.Add("915287700", new BankConfig()
                        {
                            BankUri = _danSettings.SBankenURI,
                            MaskinportenUri = _danSettings.MaskinportenEndpoint, 
                            MaskinportenAudience = _danSettings.BankAudience,
                            Client = new HttpClient() { BaseAddress = new Uri(_danSettings.SBankenURI) }
                        });
                        // Add more banks here
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
    }
}