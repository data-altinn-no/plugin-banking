using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.Dan.Plugin.Banking.Clients;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Account = Altinn.Dan.Plugin.Banking.Clients.Account;
using AccountDto = Altinn.Dan.Plugin.Banking.Models.Account;

namespace Altinn.Dan.Plugin.Banking.Services
{
    public class BankService : IBankService
    {
        private readonly IMaskinportenService _maskinportenService;
        private readonly ILogger<BankService> _logger;
        private readonly ApplicationSettings _settings;

        private const int TransactionRequestTimeoutSecs = 10;
        private const int AccountDetailsRequestTimeoutSecs = 10;
        private const int AccountListRequestTimeoutSecs = 10;

        private static Dictionary<string, BankConfig> _bankConfigs;
        private static readonly object LockObject = new();

        public BankService(ILoggerFactory loggerFactory, IMaskinportenService maskinportenService, IOptions<ApplicationSettings> applicationSettings)
        {
            _maskinportenService = maskinportenService;
            _logger = loggerFactory.CreateLogger<BankService>();
            _settings = applicationSettings.Value;
        }

        public async Task<BankResponse> GetTransactions(string ssn, KARResponse bankList, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId, Guid correlationId)
        {
            Configure(_settings.Endpoints);

            BankResponse bankResponse = new BankResponse { BankAccounts = new List<BankInfo>() };
            var bankTasks = new List<Task<BankInfo>>();

            foreach (var bank in bankList.Banks)
            {
                bankTasks.Add(Task.Run(async () =>
                {
                    string orgnr = bank.OrganizationID;
                    string name = bank.BankName;
                    BankInfo bankInfo;
                    try
                    {
                        bankInfo = await InvokeBank(ssn, orgnr, fromDate, toDate, accountInfoRequestId, correlationId);
                    }
                    catch (Exception e)
                    {
                        bankInfo = new BankInfo { Exception = e, Accounts = new List<AccountDto>() };
                        _logger.LogError(
                            "Banktransaksjoner failed while processing bank {Bank} ({OrgNo}) for {Subject}, error {Error} (accountInfoRequestId: {AccountInfoRequestId}, correlationID: {CorrelationId})",
                             name, orgnr, ssn[..6], e.Message, accountInfoRequestId, correlationId);
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
                return new BankInfo { Accounts = new List<AccountDto>(), IsImplemented = false };

            BankConfig bankConfig = _bankConfigs[orgnr];
            var token = await _maskinportenService.GetToken(_settings.Certificate, _settings.MaskinportenEnvironment,
                _settings.ClientId, "bits:kontoinformasjon.oed", bankConfig.BankAudience);

            bankConfig.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

            var bankClient = new Bank(bankConfig.Client)
            {
                BaseUrl = bankConfig.Client.BaseAddress?.ToString(),
                DecryptionCertificate = _settings.OedDecryptCert
            };
            var accountListTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountListRequestTimeoutSecs));
            var accounts = await bankClient.ListAccountsAsync(accountInfoRequestId, correlationId, "OED", ssn, null, null, null,
                fromDate, toDate, accountListTimeout.Token);

            return await GetAccountDetails(bankClient, accounts, accountInfoRequestId, correlationId);
        }

        private async Task<BankInfo> GetAccountDetails(Bank bankClient, Accounts accounts, Guid accountInfoRequestId, Guid correlationId)
        {

            BankInfo bankInfo = new BankInfo() { Accounts = new List<AccountDto>() };
            var accountDetailsTasks = new List<Task<AccountDto>>();
            foreach (Account account in accounts.Accounts1)
            {
                accountDetailsTasks.Add(Task.Run(async () =>
                {
                    // Start fetching transactions concurrently
                    var transactionsTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));
                    var transactionsTask = bankClient.ListTransactionsAsync(account.AccountReference, accountInfoRequestId,
                        correlationId, "OED", null, null, DateTime.Now.AddMonths(-3), DateTime.Now,
                        transactionsTimeout.Token);

                    var detailsTimeout =
                        new CancellationTokenSource(TimeSpan.FromSeconds(AccountDetailsRequestTimeoutSecs));
                    var details = await bankClient.ShowAccountByIdAsync(account.AccountReference, accountInfoRequestId,
                        correlationId, "OED", null, null, null, null, detailsTimeout.Token);

                    if (details.Account == null)
                    {
                        // Some test accounts come up with an empty response from the bank here (just '{ "responseStatus": "complete" }'.
                        // We skip those by returning an empty AccountDto.
                        return new AccountDto();
                    }

                    var availableCredit = details.Account.Balances.FirstOrDefault(b =>
                            b.Type == BalanceType.AvailableBalance &&
                            b.CreditDebitIndicator == CreditOrDebit.Credit)
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

                    await transactionsTask;

                    return MapToInternal(
                        details.Account,
                        transactionsTask.Result.Transactions1,
                        availableCredit - availableDebit,
                        bookedCredit - bookedDebit);
                }));
            }

            await Task.WhenAll(accountDetailsTasks);

            foreach (var accountDetailsTask in accountDetailsTasks.Where(accountDetailsTask => accountDetailsTask.Result.AccountDetail != null))
            {
                bankInfo.Accounts.Add(accountDetailsTask.Result);
            }

            return bankInfo;
        }

        private AccountDto MapToInternal(
            AccountDetail detail,
            ICollection<Transaction> transactions,
            decimal availableBalance,
            decimal bookedBalance)
        {
            // P.t. almost passthrough mapping
            return new AccountDto
            {
                AccountNumber = detail.AccountIdentifier,
                AccountDetail = detail,
                Transactions = transactions,
                AccountAvailableBalance = availableBalance,
                AccountBookedBalance = bookedBalance
            };
        }

        private static void Configure(KontoOpplysninger banks)
        {
            if (_bankConfigs != null) return;
            lock (LockObject)
            {
                if (_bankConfigs != null) return;
                _bankConfigs = new Dictionary<string, BankConfig>();

                foreach (var bank in banks.endpoints)
                {
                    _bankConfigs.Add(bank.orgNo, new BankConfig()
                    {
                        BankAudience = bank.url,
                        Client = new HttpClient { BaseAddress = new Uri(bank.url) }
                    });
                }
            }
        }
    }

    internal class BankConfig
    {
        public HttpClient Client { get; init; }
        public string BankAudience { get; init; }
    }
}
