using System;
using System.Threading.Tasks;
using Altinn.Dan.Plugin.Banking.Models;

namespace Altinn.Dan.Plugin.Banking.Services.Interfaces;

// ReSharper disable once InconsistentNaming
public interface IKARService
{
    Task<KARResponse> GetBanksForCustomer(string ssn, DateTimeOffset fromDate, DateTimeOffset toDate, Guid accountInfoRequestId, Guid correlationId, bool skipKAR);
}
