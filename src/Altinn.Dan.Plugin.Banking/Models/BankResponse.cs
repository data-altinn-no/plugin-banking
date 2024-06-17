using System;
using System.Collections.Generic;
using Altinn.Dan.Plugin.Banking.Clients.V2;
using AccountDetail = Altinn.Dan.Plugin.Banking.Clients.AccountDetail;
using Transaction = Altinn.Dan.Plugin.Banking.Clients.Transaction;
using AccountDetailV2 = Altinn.Dan.Plugin.Banking.Clients.V2.AccountDetail;
using TransactionV2 = Altinn.Dan.Plugin.Banking.Clients.V2.Transaction;
namespace Altinn.Dan.Plugin.Banking.Models
{
    public class BankResponse
    {
        public List<BankInfo> BankAccounts { get; set; }
    }

    public class BankInfo
    {
        public string BankName { get; set; }
        public bool IsImplemented { get; set; } = true;
        public List<Account> Accounts { get; set; }
        public Exception Exception { get; set; } = null;
    }

    public class Account
    {
        public string AccountNumber { get; set; } // Seperate property by now. Copy of AccountDetail.AccountIdentifier
        public AccountDetail AccountDetail { get; set; } // Not mapped to internal by now
        public ICollection<Transaction> Transactions { get; set; } // Not mapped to internal by now
        public decimal AccountAvailableBalance { get; set; }
        public decimal AccountBookedBalance { get; set; }
    }

    public class AccountV2
    {
        public string AccountNumber { get; set; } // Seperate property by now. Copy of AccountDetail.AccountIdentifier
        public AccountDetailV2 AccountDetail { get; set; } // Not mapped to internal by now
        public ICollection<TransactionV2> Transactions { get; set; } // Not mapped to internal by now
        public decimal AccountAvailableBalance { get; set; }
        public decimal AccountBookedBalance { get; set; }
    }
}
