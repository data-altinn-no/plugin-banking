#nullable enable
using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.Dan.Plugin.Banking.Clients.V2;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Extensions;
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
                        string innerExceptionMsg = string.Empty;
                        if (e is ApiException k)
                        {
                            correlationId = k.CorrelationId;
                            innerExceptionMsg = k.InnerException?.Message ?? string.Empty;
                        }
                        _logger.LogError(
                            "Bank failed while processing bank {Bank} ({OrgNo}) for {Subject}, error {Error}, accountInfoRequestId: {AccountInfoRequestId}, CorrelationId: {CorrelationId}, source: {source}, innerExceptionMessage: {innerExceptionMessage}",
                             bank.Value.Name, bank.Value.OrgNo, ssn[..6], e.Message, accountInfoRequestId, correlationId, e.Source, innerExceptionMsg);
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
                BaseUrl = bank.Client.BaseAddress!.ToString(),
                DecryptionCertificate = _settings.OedDecryptCert
            };
            var accountListTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountListRequestTimeoutSecs));
            var accounts = await GetAllAccounts(bankClient, bank, accountInfoRequestId, ssn, fromDate, toDate);
            return await GetAccountDetailsV2(bankClient, accounts, bank, accountInfoRequestId, fromDate, toDate, includeTransactions);
        }

        private async Task<BankInfo> GetAccountDetailsV2(Bank_v2.Bank_v2 bankClient, Accounts accounts, BankConfig bank, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate, bool includeTransactions = true)
        {
            var bankInfo = new BankInfo() { Accounts = [] };
            var transactions = new Transactions();

            if (accounts.Accounts1 == null ||
                accounts.Accounts1.Count == 0)
            {
                return bankInfo;
            }

            /*
             * TODO: Could this only be in Nordea test system?
             * 
             * Nordea has issues with parallell requests, so we need to fetch them one by one
             * We have tried using the following:
             * accountTasks.Add(Task.Run(async () => { ... })) but it does not work
             * Task<AccountDetails>[] accountsDetailsTasks = accounts.Accounts1.Select(x => GetAccountById(bankClient, x, bank, accountInfoRequestId, fromDate, toDate)).ToArray();
             * 
             * Task.WhenAll(...)
             * 
             * Both of the functions will result in a 200 OK Internal Server Error from Nordea for some of the requests
             * This only occurs when talking to Nordea
            */
            foreach (var account in accounts.Accounts1)
            {
                try
                {
                    var result = await GetAccountById(bankClient, account, bank, accountInfoRequestId, fromDate, toDate);
                    if (result?.Account == null) continue;

                    var balances = result.Account?.Balances;

                    var availableCredit = balances?.FirstOrDefault(b =>
                            b?.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)
                        ?.Amount ?? 0;
                    var availableDebit = balances?.FirstOrDefault(b =>
                            b?.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)
                        ?.Amount ?? 0;

                    var bookedCredit = balances?.FirstOrDefault(b =>
                            b?.Type == BalanceType.BookedBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)
                        ?.Amount ?? 0;
                    var bookedDebit = balances?.FirstOrDefault(b =>
                            b?.Type == BalanceType.BookedBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)
                        ?.Amount ?? 0;

                    if (includeTransactions)
                    {
                        transactions = await ListTransactionsForAccount(bankClient, result, bank, accountInfoRequestId, fromDate, toDate);
                    }

                    var internalAccount = MapToInternalV2(result.Account!, transactions?.Transactions1, availableCredit - availableDebit, bookedCredit - bookedDebit);
                    if (internalAccount.AccountDetail == null) continue;

                    bankInfo.Accounts.Add(internalAccount);
                }
                catch (Exception e)
                {
                    if (e is ApiException k && k.StatusCode >= 400)
                    {
                        account.LogGetAccountByIdError(_logger, k, bank, accountInfoRequestId);
                        bankInfo.Accounts.Add(account.ToDefaultDto());
                    }
                    else
                    {
                        /* 
                         * TODO:
                         * Will rethrow if a non-API exception is thrown.
                         */
                        throw;
                    }
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
                accounts.Accounts1?.Count, ssn[..6], bank.OrgNo, accountInfoRequestId, correlationId);

            return accounts;
        }

        private async Task<AccountDetails> GetAccountById(Bank_v2.Bank_v2 bankClient, Bank_v2.Account account, BankConfig bank, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            Guid correlationIdDetails = Guid.NewGuid();

            var primaryOwnerNin = account.PrimaryOwner?.Identifier?.Value != null ? account.PrimaryOwner.Identifier.Value[..6] : string.Empty;
            _logger.LogInformation("Getting account details: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                        bank.Name, account.AccountReference, primaryOwnerNin, accountInfoRequestId, correlationIdDetails);

            var detailsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountDetailsRequestTimeoutSecs));
            var details = await bankClient.ShowAccountByIdAsync(account.AccountReference, accountInfoRequestId,
                correlationIdDetails, "OED", null, null, null, fromDate, toDate, detailsTimeout.Token);

            _logger.LogInformation("Retrieved account details: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} responseDetailsStatus {ResponseDetailsStatus} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, account.AccountReference, primaryOwnerNin, details.ResponseDetails?.Status, accountInfoRequestId, correlationIdDetails);

            return details;
        }

        private async Task<Transactions> ListTransactionsForAccount(Bank_v2.Bank_v2 bankClient, AccountDetails accountDetails, BankConfig bank, Guid accountInfoRequestId, DateTimeOffset? fromDate, DateTimeOffset? toDate)
        {
            var account = accountDetails.Account;

            Guid correlationIdTransactions = Guid.NewGuid();
            var primaryOwnerNin = account?.PrimaryOwner?.Identifier?.Value != null ? account.PrimaryOwner.Identifier.Value[..6] : string.Empty;

            // Start fetching transactions concurrently
            var transactionsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));

            _logger.LogInformation("Getting transactions: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, account!.AccountReference, primaryOwnerNin, accountInfoRequestId, correlationIdTransactions);

            var transactions = await bankClient.ListTransactionsAsync(account.AccountReference, accountInfoRequestId,
                correlationIdTransactions, "OED", null, null, null, fromDate, toDate, transactionsTimeout.Token);

            _logger.LogInformation("Retrieved transactions: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} transaction count {NumberOfTransactions} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, account.AccountIdentifier, primaryOwnerNin, transactions.Transactions1?.Count, accountInfoRequestId, correlationIdTransactions);

            return transactions;
        }

        private static AccountDtoV2 MapToInternalV2(
            AccountDetail account,
            ICollection<Transaction>? transactions,
            decimal availableBalance,
            decimal bookedBalance)
        {
            account.Type = account.Type;
            account.AccountIdentifier = account.AccountIdentifier;
            account.AccountReference = account.AccountReference;

            // P.t. almost passthrough mapping
            return new AccountDtoV2
            {
                AccountNumber = account.AccountIdentifier,
                AccountDetail = account,
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
                BaseUrl = bankConfig.Client.BaseAddress!.ToString(),
                DecryptionCertificate = _settings.OedDecryptCert
            };
            Transactions transactions;
            try
            {
                transactions = await bankClient.ListTransactionsAsync(accountReference, accountInfoRequestId, correlationId, "OED", null, null, null, fromDate, toDate, transactionsTimeout.Token);
            }
            catch (Exception e)
            {
                string innerExceptionMsg = string.Empty;
                if (e is ApiException k)
                {
                    innerExceptionMsg = k.InnerException?.Message ?? string.Empty;
                }
                _logger.LogError(
                    "GetTransactionsForAccount failed while processing bank {Bank} ({OrgNo}) for {Subject}, error {Error}, accountInfoRequestId: {AccountInfoRequestId}, CorrelationId: {CorrelationId}, source: {source}, innerExceptionMessage: {innerExceptionMessage})",
                     bankConfig.Name, bankConfig.OrgNo, ssn[..6], e.Message, accountInfoRequestId, correlationId, e.Source, innerExceptionMsg);
                throw;
            }

            _logger.LogInformation("Retrieved transactions: bank {BankName} accountrefence {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bankConfig.Name, accountReference, ssn[..6], accountInfoRequestId, correlationId);

            return transactions;
        }
    }

    public class BankConfig
    {
        public required HttpClient Client { get; init; }

        public required string BankAudience { get; init; }

        public required string MaskinportenEnv { get; init; }

        public string? ApiVersion { get; init; }

        public required string Name { get; init; }

        public required string OrgNo { get; init; }
    }
}
