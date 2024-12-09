using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Altinn.Dan.Plugin.Banking.Clients.V2;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Microsoft.Extensions.Logging;

namespace Altinn.Dan.Plugin.Banking.Services.Interfaces;

public interface IBankService
{
    Task<BankResponse> GetTransactions(string ssn, List<EndpointExternal> bankList, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId, bool includeTransactions = true);
    Task<Transactions> GetTransactionsForAccount(string ssn, List<EndpointExternal> filteredEndpoints, DateTime fromDate, DateTime toDate, Guid accountInfoRequestId, string accountReference);
}
