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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AccountDtoV2 = Altinn.Dan.Plugin.Banking.Models.AccountV2;
using Bank_v2 = Altinn.Dan.Plugin.Banking.Clients.V2;

namespace Altinn.Dan.Plugin.Banking.Services;
public partial class BankService(
    ILoggerFactory loggerFactory,
    IMaskinportenService maskinportenService,
    IOptions<ApplicationSettings> applicationSettings)
    : IBankService
{
    private readonly ILogger<BankService> _logger = loggerFactory.CreateLogger<BankService>();
    private readonly ApplicationSettings _settings = applicationSettings.Value;
    private const int TransactionRequestTimeoutSecs = 30;
    private const int AccountDetailsRequestTimeoutSecs = 30;
    private const int AccountListRequestTimeoutSecs = 30;

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
                    var bankMetadataParams = new BankMetadataParams(bank.Value, accountInfoRequestId, fromDate, toDate, includeTransactions);
                    bankInfo = await InvokeBank(ssn, bankMetadataParams);
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

    private async Task<BankInfo> InvokeBank(string ssn, BankMetadataParams bankMetadata)
    {
        var bank = bankMetadata.Bank;
        var accountInfoRequestId = bankMetadata.AccountInfoRequestId;
        var fromDate = bankMetadata.FromDate;
        var toDate = bankMetadata.ToDate;

        var token = await maskinportenService.GetToken(_settings.Jwk, bank.MaskinportenEnv, _settings.ClientId, _settings.BankScope, bank.BankAudience);
        bank.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var bankClient = new Bank_v2.Bank_v2(bank.Client, _settings)
        {
            BaseUrl = bank.Client.BaseAddress!.ToString(),
            DecryptionCertificate = _settings.OedDecryptCert
        };
        var accounts = await GetAllAccounts(bankClient, ssn, bankMetadata);
        return await GetAccountDetailsV2(bankClient, accounts, bankMetadata);
    }

    private async Task<BankInfo> GetAccountDetailsV2(Bank_v2.Bank_v2 bankClient, Accounts accounts, BankMetadataParams bankMetadataParams)
    {
        var bankInfo = new BankInfo() { Accounts = [] };

        if (accounts.Accounts1 == null ||
            accounts.Accounts1.Count == 0)
        {
            return bankInfo;
        }

        Task<AccountDetails>[] accountsDetailsTasks = accounts.Accounts1
            .Select(account => GetAccountById(bankClient, account, bankMetadataParams))
            .ToArray();

        var results = Task.WhenAll(accountsDetailsTasks);
        try
        {
            AccountDetails[] accountsDetails = await results;
            var mappedAccounts = await MapAccountsToInternal(accountsDetails, bankClient, accounts, bankMetadataParams);
            bankInfo.Accounts.AddRange(mappedAccounts);
        }
        catch (Exception e)
        {
            if (e is ApiException k)
            {
                var successfulTasks = accountsDetailsTasks.Where(x => x.IsCompletedSuccessfully).ToArray();
                var successfulAccounts = await Task.WhenAll(successfulTasks);
                var faultedAccounts = accounts.Accounts1.Where(x => successfulAccounts.All(y => y.Account!.AccountIdentifier != x.AccountIdentifier)).ToList();

                foreach (var faultedAccount in faultedAccounts)
                {
                    faultedAccount.LogGetAccountByIdError(_logger, k, bankMetadataParams.Bank, bankMetadataParams.AccountInfoRequestId);
                    bankInfo.Accounts.Add(faultedAccount.ToDefaultDto());
                }

                var mappedAccounts = await MapAccountsToInternal(successfulAccounts, bankClient, accounts, bankMetadataParams);
                bankInfo.Accounts.AddRange(mappedAccounts);
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

        return bankInfo;
    }

    private async Task<List<AccountDtoV2>> MapAccountsToInternal(AccountDetails[] accountsDetails, Bank_v2.Bank_v2 bankClient, Accounts accounts, BankMetadataParams bankMetadataParams)
    {
        var mappedAccounts = new List<AccountDtoV2>();
        foreach (var accountDetails in accountsDetails)
        {
            if (accountDetails?.Account == null) continue;

            var transactions = new Transactions();
            if (bankMetadataParams.IncludeTransactions)
            {
                transactions = await ListTransactionsForAccount(bankClient, accountDetails, bankMetadataParams);
            }

            var account = accounts.Accounts1!.FirstOrDefault(x => x.AccountReference == accountDetails.Account!.AccountReference);
            var internalAccount = MapToInternalV2(accountDetails.Account!, account?.Type, transactions?.Transactions1);
            if (internalAccount.AccountDetail == null) continue;

            mappedAccounts.Add(internalAccount);
        }

        return mappedAccounts;
    }

    private async Task<Accounts> GetAllAccounts(
        Bank_v2.Bank_v2 bankClient,
        string ssn,
        BankMetadataParams bankMetadataParams)
    {
        var bank = bankMetadataParams.Bank;
        var accountInfoRequestId = bankMetadataParams.AccountInfoRequestId;
        var fromDate = bankMetadataParams.FromDate;
        var toDate = bankMetadataParams.ToDate;
        var correlationId = Guid.NewGuid();

        _logger.LogInformation("Preparing request to {BankName}, url {BankAudience}, version {BankApiVersion}, accountinforequestid {AccountInfoRequestId}, correlationid {CorrelationId}, fromdate {FromDate}, todate {ToDate}",
                        bank.Name, bank.BankAudience, bank.ApiVersion, accountInfoRequestId, correlationId, fromDate, toDate);

        var accountListTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountListRequestTimeoutSecs));
        var accounts = await bankClient.ListAccountsAsync(
            accountInfoRequestId,
            correlationId,
            PluginConstants.LegalMandate,
            ssn,
            true,
            null,
            null,
            null,
            fromDate,
            toDate,
            accountListTimeout.Token);

        _logger.LogInformation("Found {NumberOfAccounts} accounts for {DeceasedNin} in bank {OrganisationNumber} with accountinforequestid {AccountInfoRequestId} and correlationid {CorrelationId}",
            accounts.Accounts1?.Count, ssn[..6], bank.OrgNo, accountInfoRequestId, correlationId);

        return accounts;
    }

    private async Task<AccountDetails> GetAccountById(Bank_v2.Bank_v2 bankClient, Bank_v2.Account account, BankMetadataParams bankMetadataParams)
    {
        var bank = bankMetadataParams.Bank;
        var accountInfoRequestId = bankMetadataParams.AccountInfoRequestId;

        Guid correlationIdDetails = Guid.NewGuid();

        var primaryOwnerNin = account.PrimaryOwner?.Identifier?.Value != null ? account.PrimaryOwner.Identifier.Value[..6] : string.Empty;
        _logger.LogInformation("Getting account details: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                    bank.Name, account.AccountReference, primaryOwnerNin, accountInfoRequestId, correlationIdDetails);

        _logger.LogInformation("Fra: {FromDate} Til: {EndDate}", bankMetadataParams.FromDate, bankMetadataParams.ToDate);
        if (DataNotDeliveredRegex().Match(account.AccountReference).Success)
        {
            _logger.LogInformation("Data not delivered for account: bank {BankName} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
                bank.Name, primaryOwnerNin, accountInfoRequestId, correlationIdDetails);
            return new AccountDetails
            {
                ResponseDetails = new ResponseDetails
                {
                    Message = "Data not delivered for account",
                    Status = ResponseDetailsStatus.Partial
                },
                Account = null
            };
        }

        var detailsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(AccountDetailsRequestTimeoutSecs));
        var details = await bankClient.ShowAccountByIdAsync(account.AccountReference, accountInfoRequestId,
            correlationIdDetails, PluginConstants.LegalMandate, null, null, null, bankMetadataParams.FromDate, bankMetadataParams.ToDate, detailsTimeout.Token);

        _logger.LogInformation("Retrieved account details: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} responseDetailsStatus {ResponseDetailsStatus} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
            bank.Name, account.AccountReference, primaryOwnerNin, details.ResponseDetails?.Status, accountInfoRequestId, correlationIdDetails);

        return details;
    }

    private async Task<Transactions> ListTransactionsForAccount(Bank_v2.Bank_v2 bankClient, AccountDetails accountDetails, BankMetadataParams bankMetadataParams)
    {
        var bank = bankMetadataParams.Bank;
        var accountInfoRequestId = bankMetadataParams.AccountInfoRequestId;
        var account = accountDetails.Account;

        Guid correlationIdTransactions = Guid.NewGuid();
        var primaryOwnerNin = account?.PrimaryOwner?.Identifier?.Value != null ? account.PrimaryOwner.Identifier.Value[..6] : string.Empty;

        // Start fetching transactions concurrently
        var transactionsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));

        _logger.LogInformation("Getting transactions: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
            bank.Name, account!.AccountReference, primaryOwnerNin, accountInfoRequestId, correlationIdTransactions);

        var transactions = await bankClient.ListTransactionsAsync(account.AccountReference, accountInfoRequestId,
            correlationIdTransactions, PluginConstants.LegalMandate, null, null, null, bankMetadataParams.FromDate, bankMetadataParams.ToDate, transactionsTimeout.Token);

        _logger.LogInformation("Retrieved transactions: bank {BankName} accountreference {AccountReference} dob {DateOfBirth} transaction count {NumberOfTransactions} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
            bank.Name, account.AccountIdentifier, primaryOwnerNin, transactions.Transactions1?.Count, accountInfoRequestId, correlationIdTransactions);

        return transactions;
    }

    private static AccountDtoV2 MapToInternalV2(
        AccountDetail account,
        AccountType? type,
        ICollection<Transaction>? transactions)
    {
        account.Type = type;
        account.AccountIdentifier = account.AccountIdentifier;
        account.AccountReference = account.AccountReference;

        var balances = account?.Balances;
        var availableBalance = CalculateAvailableBalance(balances);
        var bookedBalance = CalculateBookedBalance(balances);

        // P.t. almost passthrough mapping
        return new AccountDtoV2
        {
            AccountNumber = account!.AccountIdentifier,
            AccountDetail = account,
            Transactions = transactions,
            AccountAvailableBalance = availableBalance,
            AccountBookedBalance = bookedBalance
        };
    }

    private static decimal CalculateBookedBalance(ICollection<Balance>? balances)
    {
        var bookedCredit = balances?.FirstOrDefault(b =>
                        b?.Type == BalanceType.BookedBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)
                    ?.Amount ?? 0;
        var bookedDebit = balances?.FirstOrDefault(b =>
                b?.Type == BalanceType.BookedBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)
            ?.Amount ?? 0;

        return bookedCredit - bookedDebit;
    }

    private static decimal CalculateAvailableBalance(ICollection<Balance>? balances)
    {
        var availableCredit = balances?.FirstOrDefault(b =>
                        b?.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Credit)
                    ?.Amount ?? 0;
        var availableDebit = balances?.FirstOrDefault(b =>
                b?.Type == BalanceType.AvailableBalance && b.CreditDebitIndicator == CreditOrDebit.Debit)
            ?.Amount ?? 0;

        return availableCredit - availableDebit;
    }

    public async Task<Transactions> GetTransactionsForAccount(string ssn, BankConfig bankConfig, DateTime fromDate, DateTime toDate, Guid accountInfoRequestId, string accountReference)
    {
        var correlationId = Guid.NewGuid();
        var transactionsTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(TransactionRequestTimeoutSecs));

        _logger.LogInformation("Getting transactions: bank {BankName} accountrefence {AccountReference} dob {DateOfBirth} accountinforequestid {AccountInfoRequestId} correlationid {CorrelationId}",
            bankConfig.Name, accountReference, ssn[..6], accountInfoRequestId, correlationId);

        var token = await maskinportenService.GetToken(_settings.Jwk, bankConfig.MaskinportenEnv, _settings.ClientId, _settings.BankScope, bankConfig.BankAudience);

        bankConfig.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        var bankClient = new Bank_v2.Bank_v2(bankConfig.Client, _settings)
        {
            BaseUrl = bankConfig.Client.BaseAddress!.ToString(),
            DecryptionCertificate = _settings.OedDecryptCert
        };
        Transactions transactions;
        try
        {
            transactions = await bankClient.ListTransactionsAsync(accountReference, accountInfoRequestId, correlationId, PluginConstants.LegalMandate, null, null, null, fromDate, toDate, transactionsTimeout.Token);
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

    [GeneratedRegex(@"(?i)\bdataNotDelivered\b", RegexOptions.None, "en-GB")]
    private static partial Regex DataNotDeliveredRegex();
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

public readonly record struct BankMetadataParams(
    BankConfig Bank,
    Guid AccountInfoRequestId,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate,
    bool IncludeTransactions = true);
