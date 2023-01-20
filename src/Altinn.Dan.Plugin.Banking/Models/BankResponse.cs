using Altinn.Dan.Plugin.Banking.Clients;
using System;
using System.Collections.Generic;

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
}
