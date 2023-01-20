using System;
using System.Threading.Tasks;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Microsoft.Extensions.Logging;

namespace Altinn.Dan.Plugin.Banking.Services.Interfaces;

public interface IBankService
{
    Task<BankResponse> GetTransactions(string ssn, KARResponse bankList, DateTimeOffset? fromDate, DateTimeOffset? toDate, Guid accountInfoRequestId, Guid correlationId);
}
