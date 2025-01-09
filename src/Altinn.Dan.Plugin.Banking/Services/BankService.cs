using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.Dan.Plugin.Banking.Clients.V2;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
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

        public BankService(ILoggerFactory loggerFactory, IMaskinportenService maskinportenService, IOptions<ApplicationSettings> applicationSettings)
        {
            _maskinportenService = maskinportenService;
            _logger = loggerFactory.CreateLogger<BankService>();
            _settings = applicationSettings.Value;
        }


        public async Task<BankResponse> GetAccounts(string ssn, Dictionary<string, BankConfig> bankList, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId, bool includeTransactions = true)
        {
            BankResponse bankResponse = new BankResponse { BankAccounts = [] };
            var bankTasks = new List<Task<BankInfo>>();

            foreach (var bank in bankList)
            {
                bankTasks.Add(Task.Run(async () =>
                {
                    BankInfo bankInfo;
                    try
                    {
                        bankInfo = await InvokeBank(ssn, bank.Value, fromDate, toDate, accountInfoRequestId, includeTransactions);
                    }
                    catch (Exception e)
                    {
                        bankInfo = new BankInfo { Accounts = [], HasErrors = true };
                        string correlationId = string.Empty;
                        if (e is ApiException k)
                        {
                            correlationId = k.CorrelationId;
                        }
                        _logger.LogError(
                            "Banktransaksjoner failed while processing bank {Bank} ({OrgNo}) for {Subject}, error {Error}, accountInfoRequestId: {AccountInfoRequestId}, CorrelationId: {CorrelationId}, source: {source})",
                             bank.Value.Name, bank.Value.OrgNo, ssn[..6], e.Message, accountInfoRequestId, correlationId, e.Source);
                    }

                    bankInfo.BankName = bank.Value.Name;
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

        private async Task<BankInfo> InvokeBank(string ssn, BankConfig bank, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId, bool includeTransactions = true)
        {
            var token = await _maskinportenService.GetToken(_settings.Jwk, bank.MaskinportenEnv, _settings.ClientId, _settings.BankScope, bank.BankAudience);
            bank.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var bankClient = new Bank_v2.Bank_v2(bank.Client, _settings)
            {
                BaseUrl = bank.Client.BaseAddress?.ToString(),
                DecryptionCertificate = _settings.OedDecryptCert
            };
            var accountListTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountListRequestTimeoutSecs));
            var accounts = await GetAllAccounts(bankClient, bank, accountInfoRequestId, ssn, fromDate, toDate);
            var x = await GetAccountDetailsV2(bankClient, accounts, bank, accountInfoRequestId, fromDate, toDate, includeTransactions);
            return x;
        }

        private async Task<BankInfo> GetAccountDetailsV2(Bank_v2.Bank_v2 bankClient, Accounts accounts, BankConfig bank, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate, bool includeTransactions = true)
        {
            var bankInfo = new BankInfo() { Accounts = [] };
            var transactions = new Transactions();
            foreach (Bank_v2.Account account in accounts.Accounts1)
            {
                var details = await GetAccountById(bankClient, account, bank, accountInfoRequestId, fromDate, toDate);

                if (details.Account == null)
                {
                    // Some test accounts come up with an empty response from the bank here (just '{ "responseStatus": "complete" }'.
                    // We skip those by returning an empty AccountDto.
                    continue;
                }

                var availableCredit = details.Account.Balances.FirstOrDefault(b =>
                        b.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)
                    ?.Amount ?? 0;
                var availableDebit = details.Account.Balances.FirstOrDefault(b =>
                        b.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)
                    ?.Amount ?? 0;

                var bookedCredit = details.Account.Balances.FirstOrDefault(b =>
                        b.Type == BalanceType.BookedBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)
                    ?.Amount ?? 0;
                var bookedDebit = details.Account.Balances.FirstOrDefault(b =>
                        b.Type == BalanceType.BookedBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)
                    ?.Amount ?? 0;

                if (includeTransactions)
                {
                    transactions = await ListTransactionsForAccount(bankClient, account, bank, accountInfoRequestId, fromDate, toDate);
                }

                var internalAccount = MapToInternalV2(account, details.Account, transactions?.Transactions1, availableCredit - availableDebit, bookedCredit - bookedDebit);
                if (internalAccount.AccountDetail != null)
                {
                    bankInfo.Accounts.Add(internalAccount);
                }
            }

            return bankInfo;
        }

        private async Task<Accounts> GetAllAccounts(Bank_v2.Bank_v2 bankClient, BankConfig bank, Guid accountInfoRequestId, string ssn, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            var correlationId = Guid.NewGuid();

            _logger.LogInformation("Preparing request to {BankName}, url {BankAudience}, version {BankApiVersion}, accountinforequestid {AccountInfoRequestId}, correlationid {CorrelationId}, fromdate {FromDate}, todate {ToDate}",
                            bank.Name, bank.BankAudience, bank.ApiVersion, accountInfoRequestId, correlationId, fromDate, toDate);

            var accounts = await bankClient.ListAccountsAsync(accountInfoRequestId, correlationId, "OED", ssn, true, null, null, null, fromDate, toDate);

            _logger.LogInformation("Found {NumberOfAccounts} accounts for {DeceasedNin} in bank {OrganisationNumber} with accountinforequestid {AccountInfoRequestId} and correlationid {CorrelationId}",
                accounts.Accounts1.Count, ssn[..6], bank.OrgNo, accountInfoRequestId, correlationId);

            return accounts;
        }

        private async Task<AccountDetails> GetAccountById(Bank_v2.Bank_v2 bankClient, Bank_v2.Account account, BankConfig bank, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            Guid correlationIdDetails = Guid.NewGuid();

            _logger.LogInformation("Getting account details: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                        bank.Name, account.AccountReference, account.PrimaryOwner?.Identifier?.Value?[..6], accountInfoRequestId, correlationIdDetails);

            var detailsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountDetailsRequestTimeoutSecs));
            var details = await bankClient.ShowAccountByIdAsync(account.AccountReference, accountInfoRequestId,
                correlationIdDetails, "OED", null, null, null, fromDate, toDate, detailsTimeout.Token);

            _logger.LogInformation("Retrieved account details: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} responseDetailsStatus {ResponseDetailsStatus} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, account.AccountReference, account.PrimaryOwner?.Identifier?.Value?[..6], details.ResponseDetails.Status, accountInfoRequestId, correlationIdDetails);

            return details;
        }

        private async Task<Transactions> ListTransactionsForAccount(Bank_v2.Bank_v2 bankClient, Bank_v2.Account account, BankConfig bank, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            Guid correlationIdTransactions = Guid.NewGuid();

            // Start fetching transactions concurrently
            var transactionsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));

            _logger.LogInformation("Getting transactions: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, account.AccountReference, account.PrimaryOwner?.Identifier?.Value?[..6], accountInfoRequestId, correlationIdTransactions);

            var transactions = await bankClient.ListTransactionsAsync(account.AccountReference, accountInfoRequestId,
                correlationIdTransactions, "OED", null, null, null, fromDate, toDate, transactionsTimeout.Token);

            _logger.LogInformation("Retrieved transactions: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} transaction count {NumberOfTransactions} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, account.AccountIdentifier, account.PrimaryOwner?.Identifier?.Value?.Substring(0, 6), transactions.Transactions1?.Count, accountInfoRequestId, correlationIdTransactions);

            return transactions;
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
            Bank_v2.Account account,
            AccountDetail detail,
            ICollection<Transaction> transactions,
            decimal availableBalance,
            decimal bookedBalance)
        {
            detail.Type = account.Type;
            detail.AccountIdentifier = account.AccountIdentifier;
            detail.AccountReference = account.AccountReference;

            // P.t. almost passthrough mapping
            return new AccountDtoV2
            {
                AccountNumber = account.AccountIdentifier,
                AccountDetail = detail,
                Transactions = transactions,
                AccountAvailableBalance = availableBalance,
                AccountBookedBalance = bookedBalance
            };
        }

        public async Task<Transactions> GetTransactionsForAccount(string ssn, BankConfig bankConfig, DateTime fromDate, DateTime toDate, Guid accountInfoRequestId, string accountReference)
        {
            var correlationId = Guid.NewGuid();
            var transactionsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));

            _logger.LogInformation("Getting transactions: bank {BankName} accountrefence {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bankConfig.Name, accountReference, ssn[..6], accountInfoRequestId, correlationId);

            var token = await _maskinportenService.GetToken(_settings.Jwk, bankConfig.MaskinportenEnv, _settings.ClientId, _settings.BankScope, bankConfig.BankAudience);

            bankConfig.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var bankClient = new Bank_v2.Bank_v2(bankConfig.Client, _settings)
            {
                BaseUrl = bankConfig.Client.BaseAddress?.ToString(),
                DecryptionCertificate = _settings.OedDecryptCert
            };
            var transactions = await bankClient.ListTransactionsAsync(accountReference, accountInfoRequestId, correlationId, "OED", null, null, null, fromDate, toDate, transactionsTimeout.Token);

            _logger.LogInformation("Retrieved transactions: bank {BankName} accountrefence {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bankConfig.Name, accountReference, ssn[..6], accountInfoRequestId, correlationId);

            return transactions;
        }
    }

    public class BankConfig
    {
        public HttpClient Client { get; init; }
        public string BankAudience { get; init; }

        public string MaskinportenEnv { get; init; }

        public string ApiVersion { get; init; }

        public string Name { get; init; }

        public string OrgNo { get; init; }
    }
}
