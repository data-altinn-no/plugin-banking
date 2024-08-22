using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;
using AccountDto = Altinn.Dan.Plugin.Banking.Models.Account;
using AccountDtoV2 = Altinn.Dan.Plugin.Banking.Models.AccountV2;
using Bank_v2 = Altinn.Dan.Plugin.Banking.Clients.V2;

namespace Altinn.Dan.Plugin.Banking.Services
{
    public class BankService : IBankService
    {
        private readonly IMaskinportenService _maskinportenService;
        private readonly ILogger<BankService> _logger;
        private readonly ApplicationSettings _settings;

        private const int TransactionRequestTimeoutSecs = 30;
        private const int AccountDetailsRequestTimeoutSecs = 30;
        private const int AccountListRequestTimeoutSecs = 30;

        private static Dictionary<string, BankConfig> _bankConfigs;
        private static readonly object LockObject = new();

        public BankService(ILoggerFactory loggerFactory, IMaskinportenService maskinportenService, IOptions<ApplicationSettings> applicationSettings)
        {
            _maskinportenService = maskinportenService;
            _logger = loggerFactory.CreateLogger<BankService>();
            _settings = applicationSettings.Value;
        }

        public async Task<BankResponse> GetTransactions(string ssn, List<EndpointExternal> bankList, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId)
        {
            Configure(bankList);
            var correlationId = Guid.NewGuid();

            BankResponse bankResponse = new BankResponse { BankAccounts = new List<BankInfo>() };
            var bankTasks = new List<Task<BankInfo>>();

            foreach (var bank in bankList)
            {
                bankTasks.Add(Task.Run(async () =>
                {
                    string orgnr = bank.OrgNo;
                    string name = bank.Name;

                    BankInfo bankInfo;
                    try
                    {
                        bankList.ForEach(bank =>
                        _logger.LogInformation($"Preparing request to bank {bank.Name} with url {bank.Url} and version {bank.Version} and accountinforequestid {accountInfoRequestId}")
                            );
                        bankInfo = await InvokeBank(ssn, orgnr, fromDate, toDate, accountInfoRequestId, correlationId);
                    }
                    catch (Exception e)
                    {
                        bankInfo = new BankInfo { Accounts = new List<AccountDtoV2>(), HasErrors = true};
                        _logger.LogError(
                            "Banktransaksjoner failed while processing bank {Bank} ({OrgNo}) for {Subject}, error {Error}, accountInfoRequestId: {AccountInfoRequestId}, source: {source})",
                             name, orgnr, ssn[..6], e.Message, accountInfoRequestId, e.Source);

                        
                    }

                    bankInfo.BankName = name;
                    return bankInfo;
                }));
            }

            await Task.WhenAll(bankTasks);
            var takenOneEmptyBank = false;
            foreach (var bankTask in bankTasks)
            {
                // If we're skipping KAR (in test mode), just include one more additional "empty" bank
                if (_settings.SkipKAR && bankTask.Result.Accounts.Count == 0)
                {
                    if (takenOneEmptyBank)
                        continue;

                    takenOneEmptyBank = true;
                }

                bankResponse.BankAccounts.Add(bankTask.Result);
            }

            return bankResponse;
        }

        private async Task<BankInfo> InvokeBank(string ssn, string orgnr, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId, Guid correlationId)
        {
            if (!_bankConfigs.ContainsKey(orgnr))
                return new BankInfo { Accounts = new List<AccountDtoV2>(), IsImplemented = false };

            BankConfig bankConfig = _bankConfigs[orgnr];
            var token = await _maskinportenService.GetToken(_settings.Jwk, bankConfig.MaskinportenEnv, _settings.ClientId, _settings.BankScope, bankConfig.BankAudience);

            bankConfig.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var bankClient = new Bank_v2.Bank_v2(bankConfig.Client, _settings)
                {
                    BaseUrl = bankConfig.Client.BaseAddress?.ToString(),
                    DecryptionCertificate = _settings.OedDecryptCert
                };
            var accountListTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountListRequestTimeoutSecs));

            var accounts = await bankClient.ListAccountsAsync(accountInfoRequestId, correlationId, "OED", ssn, true, null, null, null, fromDate, toDate);

            _logger.LogInformation("Found {0} accounts for {1} in bank {2} with accountinforequestid{3} and correlationid {4}", accounts.Accounts1.Count, ssn.Substring(0,6), orgnr, accountInfoRequestId, correlationId);
            return await GetAccountDetailsV2(bankClient, accounts, accountInfoRequestId, fromDate, toDate); //application/jose
        }
        private async Task<BankInfo> GetAccountDetailsV2(Bank_v2.Bank_v2 bankClient,Bank_v2.Accounts accounts, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            

            BankInfo bankInfo = new BankInfo() { Accounts = new List<AccountDtoV2>() };
            var accountDetailsTasks = new List<Task<AccountDtoV2>>();
            foreach (Bank_v2.Account account in accounts.Accounts1)
            {
                Guid correlationIdDetails = Guid.NewGuid();
                Guid correlationIdTransactions = Guid.NewGuid();

                accountDetailsTasks.Add(Task.Run(async () =>
                {
                    // Start fetching transactions concurrently
                    var transactionsTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));

                    _logger.LogInformation("Getting transactions: bank {0} account {1} dob {2} accountinforequestid {3} correlationid {4}",
                        account.Servicer.Name, account.AccountIdentifier, account.PrimaryOwner.Identifier.Value.Substring(0,6), accountInfoRequestId, correlationIdTransactions);

                    var transactionsTask = bankClient.ListTransactionsAsync(account.AccountReference, accountInfoRequestId,
                        correlationIdTransactions, "OED", null, null, null, fromDate, toDate, transactionsTimeout.Token);


                    _logger.LogInformation("Getting account details: bank {0} account {1} dob {2} accountinforequestid {4} correlationid {5}",
                        account.Servicer.Name, account.AccountIdentifier, account.PrimaryOwner.Identifier.Value.Substring(0, 6), accountInfoRequestId, correlationIdDetails);


                    var detailsTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(AccountDetailsRequestTimeoutSecs));
                    var details = await bankClient.ShowAccountByIdAsync(account.AccountReference, accountInfoRequestId,
                        correlationIdDetails, "OED", null, null, null, fromDate , toDate, detailsTimeout.Token);

                    _logger.LogInformation("Retrieved account details: bank {0} account {1} dob {2} details {3} accountinforequestid {4} correlationid {5}",
                        account.Servicer.Name, account.AccountIdentifier, account.PrimaryOwner.Identifier.Value.Substring(0, 6), details.ResponseDetails.Status, accountInfoRequestId, correlationIdDetails);

                    if (details.Account == null)
                    {
                        // Some test accounts come up with an empty response from the bank here (just '{ "responseStatus": "complete" }'.
                        // We skip those by returning an empty AccountDto.
                        return new AccountDtoV2();
                    }

                    var availableCredit = details.Account.Balances.FirstOrDefault(b =>
                            b.Type == Altinn.Dan.Plugin.Banking.Clients.V2.BalanceType.AvailableBalance && b.CreditDebitIndicator == Altinn.Dan.Plugin.Banking.Clients.V2.CreditOrDebit.Credit)
                        ?.Amount ?? 0;
                    var availableDebit = details.Account.Balances.FirstOrDefault(b =>
                            b.Type == Altinn.Dan.Plugin.Banking.Clients.V2.BalanceType.AvailableBalance && b.CreditDebitIndicator == Altinn.Dan.Plugin.Banking.Clients.V2.CreditOrDebit.Debit)
                        ?.Amount ?? 0;

                    var bookedCredit = details.Account.Balances.FirstOrDefault(b =>
                            b.Type == Altinn.Dan.Plugin.Banking.Clients.V2.BalanceType.BookedBalance && b.CreditDebitIndicator == Altinn.Dan.Plugin.Banking.Clients.V2.CreditOrDebit.Credit)
                        ?.Amount ?? 0;
                    var bookedDebit = details.Account.Balances.FirstOrDefault(b =>
                            b.Type == Altinn.Dan.Plugin.Banking.Clients.V2.BalanceType.BookedBalance && b.CreditDebitIndicator == Altinn.Dan.Plugin.Banking.Clients.V2.CreditOrDebit.Debit)
                        ?.Amount ?? 0;

                    await transactionsTask;

                    _logger.LogInformation("Retrieved transactions: bank {0} account {1} dob {2} transaction count {3} accountinforequestid {4} correlationid {5}",
                        account.Servicer.Name, account.AccountIdentifier, account.PrimaryOwner.Identifier.Value.Substring(0, 6), transactionsTask.Result.Transactions1.Count, accountInfoRequestId, correlationIdTransactions);
                    

                    return MapToInternalV2(details.Account, transactionsTask.Result.Transactions1, availableCredit - availableDebit, bookedCredit - bookedDebit);
                }));
            }

            await Task.WhenAll(accountDetailsTasks);

            foreach (var accountDetailsTask in accountDetailsTasks.Where(accountDetailsTask => accountDetailsTask.Result.AccountDetail != null))
            {
                bankInfo.Accounts.Add(accountDetailsTask.Result);
            }

            return bankInfo;
        }

        /*private AccountDto MapFromAccountDTOV2TOV1(AccountDtoV2 result)
        {
            var a =  new AccountDto()
            {
                AccountAvailableBalance = result.AccountAvailableBalance,
                AccountBookedBalance = result.AccountBookedBalance,
                AccountDetail = new AccountDetail()
                {
                    Name = result.AccountDetail.Name,
                    AccountIdentifier = result.AccountDetail.AccountIdentifier,
                    AccountReference = result.AccountDetail.AccountReference,
                    AdditionalProperties = result.AccountDetail.AdditionalProperties,
                    Balances = new List<Balance>(),
                    Currency = result.AccountDetail.Currency,
                    EndDate = result.AccountDetail.EndDate,
                    PrimaryOwner = new AccountRole()
                    {
                        Name = result.AccountDetail.PrimaryOwner.Name,
                        StartDate = result.AccountDetail.PrimaryOwner.StartDate,
                        EndDate = result.AccountDetail.PrimaryOwner.EndDate,
                        Identifier = new Identifier()
                        {
                            Value = result.AccountDetail.PrimaryOwner.Identifier.Value,
                            Type = result.AccountDetail.PrimaryOwner.Identifier.Type == Bank_v2.IdentifierType.CountryIdentificationCode ? IdentifierType.CountryIdentificationCode : IdentifierType.NationalIdentityNumber,
                            CountryOfResidence = result.AccountDetail.PrimaryOwner.Identifier.CountryOfResidence,
                            AdditionalProperties = result.AccountDetail.PrimaryOwner.Identifier.AdditionalProperties
                        },
                        AdditionalProperties = result.AccountDetail.PrimaryOwner.AdditionalProperties,
                        ElectronicAddresses = new List<ElectronicAddress>(),
                        PostalAddress = new PostalAddress()
                    },
                    Servicer = new FinancialInstitution()
                    {
                            AdditionalProperties = result.AccountDetail.Servicer.AdditionalProperties,
                            Identifier = new Identifier()
                            {
                                Value = result.AccountDetail.Servicer.Identifier.Value,
                                Type = (IdentifierType) result.AccountDetail.Servicer.Identifier.Type,
                                CountryOfResidence = result.AccountDetail.Servicer.Identifier.CountryOfResidence,
                                AdditionalProperties = result.AccountDetail.Servicer.Identifier.AdditionalProperties
                            },
                        Name = result.AccountDetail.Servicer.Name
                    },
                    StartDate = result.AccountDetail.StartDate,
                    Status = (AccountStatus) result.AccountDetail.Status,
                    Type = (AccountType) result.AccountDetail.Type,
                }
            };
            
            foreach (var accountDetailBalance in result.AccountDetail.Balances)
            {
                a.AccountDetail.Balances.Add(
                    new Balance()
                    {
                        Currency = accountDetailBalance.Currency,
                        CreditDebitIndicator = accountDetailBalance.CreditDebitIndicator == Bank_v2.CreditOrDebit.Credit ? CreditOrDebit.Credit : CreditOrDebit.Debit,
                        Type = accountDetailBalance.Type == Bank_v2.BalanceType.AvailableBalance ? BalanceType.AvailableBalance : BalanceType.BookedBalance,
                        Amount = accountDetailBalance.Amount,
                        AdditionalProperties = accountDetailBalance.AdditionalProperties,
                        CreditLineAmount = accountDetailBalance.CreditLineAmount,
                        Registered = accountDetailBalance.Registered,
                        CreditLineCurrency = accountDetailBalance.CreditLineCurrency,
                        CreditLineIncluded = accountDetailBalance.CreditLineIncluded
                    });
            }
            return a;
        } */

        private AccountDtoV2 MapToInternalV2(
           Bank_v2.AccountDetail detail,
            ICollection<Bank_v2.Transaction> transactions,
            decimal availableBalance,
            decimal bookedBalance)
        {
            // P.t. almost passthrough mapping
            return new AccountDtoV2
            {
                AccountNumber = detail.AccountIdentifier,
                AccountDetail = detail,
                Transactions = transactions,
                AccountAvailableBalance = availableBalance,
                AccountBookedBalance = bookedBalance
            };
        }

        private void Configure(List<EndpointExternal> banks)
        {
            if (_bankConfigs != null) return;
            lock (LockObject)
            {
                if (_bankConfigs != null) return;
                _bankConfigs = new Dictionary<string, BankConfig>();

                foreach (var bank in banks)
                {
                    _bankConfigs.Add(bank.OrgNo, new BankConfig()
                    {
                        BankAudience = bank.Url,//.ToUpper().Replace("V1", "V2"),
                        Client = new HttpClient { BaseAddress = new Uri(bank.Url) },
                        MaskinportenEnv = _settings.MaskinportenEnvironment,
                        ApiVersion = bank.Version
                    });
                }
            }
        }
    }

    internal class BankConfig
    {
        public HttpClient Client { get; init; }
        public string BankAudience { get; init; }

        public string MaskinportenEnv { get; init; }

        public string ApiVersion { get; init; }
    }
}
