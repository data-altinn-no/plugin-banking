using Altinn.Dan.Plugin.Banking.Clients.V2;
using Altinn.Dan.Plugin.Banking.Services;
using Microsoft.Extensions.Logging;
using System;

namespace Altinn.Dan.Plugin.Banking.Extensions
{
    public static class AccountExtensions
    {
        public static void LogGetAccountByIdError(
            this Account account,
            ILogger logger,
            Exception e,
            BankConfig bank,
            Guid accountInfoRequestId)
        {
            string correlationId = null, innerExceptionMsg = null;
            if (e is ApiException k)
            {
                correlationId = k.CorrelationId;
                innerExceptionMsg = k.InnerException?.Message;
            }

            logger.LogError("GetAccountById failed while processing account {Account} for {Bank} ({OrgNo}) for {Subject}, error {Error}, accountInfoRequestId: {AccountInfoRequestId}, CorrelationId: {CorrelationId}, source: {source}, innerExceptionMessage: {innerExceptionMessage}",
              account.AccountReference,
              bank.Name,
              bank.OrgNo,
              account?.PrimaryOwner?.Identifier?.Value[..6],
              e.Message,
              accountInfoRequestId,
              correlationId,
              e.Source,
              innerExceptionMsg);
        }
    }
}
