using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.ApiClients.Maskinporten.Models;
using Altinn.Dan.Plugin.Banking.Clients.V2;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Services;
using FakeItEasy;
using Jose;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Altinn.Dan.Plugin.Banking.Test.Services
{
    [TestClass]
    public class BankServiceTests
    {
        private readonly X509Certificate2 _certificate;
        private readonly IOptions<ApplicationSettings> _fakeOptions;
        private readonly FakeableHttpMessageHandler _handler;
        private readonly HttpClient _client;
        private readonly ILoggerFactory _fakeLogger;
        private readonly IMaskinportenService _fakeMpService;

        public BankServiceTests()
        {
            _fakeLogger = A.Fake<ILoggerFactory>();
            _fakeMpService = A.Fake<IMaskinportenService>();
            _fakeOptions = A.Fake<IOptions<ApplicationSettings>>();
            _handler = A.Fake<FakeableHttpMessageHandler>();
            _client = new HttpClient(_handler)
            {
                BaseAddress = new Uri("http://test.com")
            };

            A.CallTo(() => _fakeMpService.GetToken(A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<string>._, A<bool>._)).Returns(new TokenResponse { AccessToken = "321" });
            _certificate = CertificateGenerator.GenerateSelfSignedCertificate();
            A.CallTo(() => _fakeOptions.Value).Returns(new ApplicationSettings { Jwk = "321", ClientId = "54345", BankScope = "somescope", OedDecryptCert = _certificate });
        }

        [TestMethod]
        public async Task GetAccounts_Ok_Accounts()
        {
            // Arrange
            var nin = "12345678901";
            var fromDate = DateTime.Now.AddMonths(-1);
            var toDate = DateTime.Now;
            var bankName = "bank1";
            var orgNumber = "789";
            var bankList = GetDefaultBankConfig(bankName, orgNumber);

            var accounts = GetDefaultAccounts(bankName, orgNumber)!;
            var account1Details = GetAccountDetails(accounts.Accounts1!.ElementAt(0))!;
            var account2Details = GetAccountDetails(accounts.Accounts1!.ElementAt(1))!;
            FakeGetAccounts(accounts);
            FakeGetAccountDetails(account1Details);
            FakeGetAccountDetails(account2Details);

            var bankService = new BankService(_fakeLogger, _fakeMpService, _fakeOptions);

            // Act
            var result = await bankService.GetAccounts(nin, bankList, fromDate, toDate, Guid.NewGuid(), false);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.BankAccounts.Count);
            Assert.IsFalse(result.BankAccounts.Single().HasErrors);
            Assert.AreEqual(2, result.BankAccounts.Single().Accounts.Count);
        }

        [TestMethod]
        public async Task GetAccounts_InternalServerError_BankHasErrors()
        {
            // Arrange
            var nin = "12345678901";
            var fromDate = DateTime.Now.AddMonths(-1);
            var toDate = DateTime.Now;
            var bankName = "bank1";
            var orgNumber = "789";
            var bankList = GetDefaultBankConfig(bankName, orgNumber);

            var accounts = GetDefaultAccounts(bankName, orgNumber);
            FakeGetAccounts(accounts, HttpStatusCode.InternalServerError);

            var bankService = new BankService(_fakeLogger, _fakeMpService, _fakeOptions);

            // Act
            var result = await bankService.GetAccounts(nin, bankList, fromDate, toDate, Guid.NewGuid(), false);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.BankAccounts.Count);
            Assert.IsTrue(result.BankAccounts.Single().HasErrors);
            Assert.AreEqual(0, result.BankAccounts.Single().Accounts.Count);
            Assert.AreEqual(bankName, result.BankAccounts.Single().BankName);
        }

        [TestMethod]
        public async Task GetAccounts_GetAccountDetailsInternalServerError_AccountHasErrors()
        {
            // Arrange
            var nin = "12345678901";
            var fromDate = DateTime.Now.AddMonths(-1);
            var toDate = DateTime.Now;
            var bankName = "bank1";
            var orgNumber = "789";
            var bankList = GetDefaultBankConfig(bankName, orgNumber);

            var accounts = GetDefaultAccounts(bankName, orgNumber);
            accounts.Accounts1!.Add(GetAccount(bankName, orgNumber, "3"));
            var account1Details = GetAccountDetails(accounts.Accounts1.ElementAt(0))!;
            var account2Details = GetAccountDetails(accounts.Accounts1.ElementAt(1))!;
            var account3Details = GetAccountDetails(accounts.Accounts1.ElementAt(2))!;
            FakeGetAccounts(accounts);
            FakeGetAccountDetails(account1Details, HttpStatusCode.InternalServerError);
            FakeGetAccountDetails(account2Details);
            FakeGetAccountDetails(account3Details, HttpStatusCode.InternalServerError);

            var bankService = new BankService(_fakeLogger, _fakeMpService, _fakeOptions);

            // Act
            var result = await bankService.GetAccounts(nin, bankList, fromDate, toDate, Guid.NewGuid(), false);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.BankAccounts.Count);
            Assert.IsFalse(result.BankAccounts.Single().HasErrors);
            Assert.AreEqual(3, result.BankAccounts.Single().Accounts.Count);
            Assert.IsTrue(result.BankAccounts.Single().Accounts.Any(x => x.HasErrors));
        }

        [TestMethod]
        public async Task GetAccounts_TypeFromAccountList_AccountHasType()
        {
            // Arrange
            var nin = "12345678901";
            var fromDate = DateTime.Now.AddMonths(-1);
            var toDate = DateTime.Now;
            var bankName = "bank1";
            var orgNumber = "789";
            var bankList = GetDefaultBankConfig(bankName, orgNumber);

            var accounts = GetDefaultAccounts(bankName, orgNumber);
            var account1Details = GetAccountDetails(accounts.Accounts1!.ElementAt(0))!;
            var account2Details = GetAccountDetails(accounts.Accounts1!.ElementAt(1))!;
            FakeGetAccounts(accounts);
            FakeGetAccountDetails(account1Details);
            FakeGetAccountDetails(account2Details);

            var bankService = new BankService(_fakeLogger, _fakeMpService, _fakeOptions);

            // Act
            var result = await bankService.GetAccounts(nin, bankList, fromDate, toDate, Guid.NewGuid(), false);

            // Assert
            Assert.IsNotNull(result);
            Assert.AreEqual(1, result.BankAccounts.Count);
            Assert.IsFalse(result.BankAccounts.Single().HasErrors);
            Assert.AreEqual(2, result.BankAccounts.Single().Accounts.Count);
            Assert.IsFalse(result.BankAccounts.Single().Accounts.Any(x => x.HasErrors));
            Assert.IsTrue(result.BankAccounts.Single().Accounts.All(x => x.AccountDetail.Type == accounts.Accounts1!.ElementAt(0).Type));
        }

        private Dictionary<string, BankConfig> GetDefaultBankConfig(string bankName, string orgNumber)
        {
            return new Dictionary<string, BankConfig>
            {
                {
                    orgNumber,
                    new BankConfig
                    {
                        OrgNo = orgNumber,
                        Name = bankName,
                        Client = _client,
                        MaskinportenEnv = "test1",
                        BankAudience = "someaudience"
                    }
                }
            };
        }

        private void FakeGetAccountDetails(AccountDetails accountDetails, HttpStatusCode httpStatusCode = HttpStatusCode.OK)
        {
            A.CallTo(() => _handler.FakeSendAsync(A<HttpRequestMessage>.That.Matches(x => x.RequestUri != null && x.RequestUri.AbsoluteUri.Contains($"accounts/{accountDetails.Account!.AccountReference}")), A<CancellationToken>._))
                .Returns(Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = httpStatusCode,
                    Content = new StringContent(JWT.Encode(JsonConvert.SerializeObject(accountDetails), _certificate.GetRSAPublicKey(), JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
                }));
        }

        private void FakeGetAccounts(Accounts accounts, HttpStatusCode httpStatusCode = HttpStatusCode.OK)
        {
            A.CallTo(() => _handler.FakeSendAsync(A<HttpRequestMessage>._, A<CancellationToken>._))
                .Returns(Task.FromResult(new HttpResponseMessage
                {
                    StatusCode = httpStatusCode,
                    Content = new StringContent(JWT.Encode(JsonConvert.SerializeObject(accounts), _certificate.GetRSAPublicKey(), JweAlgorithm.RSA_OAEP_256, JweEncryption.A128CBC_HS256), Encoding.UTF8, new MediaTypeHeaderValue("application/json"))
                }))
                .NumberOfTimes(1);
        }

        private static AccountDetails? GetAccountDetails(Account account)
        {
            return new AccountDetails
            {
                ResponseDetails = new ResponseDetails
                {
                    Message = "OK",
                    Status = ResponseDetailsStatus.Complete,
                },
                Account = new AccountDetail
                {
                    AccountIdentifier = account.AccountIdentifier,
                    AccountReference = account.AccountReference,
                    PrimaryOwner = account.PrimaryOwner,
                    Status = account.Status,
                    Servicer = account.Servicer!,
                    Balances = new List<Balance>
                    {
                        new Balance
                        {
                            CreditDebitIndicator = CreditOrDebit.Credit,
                            CreditLineAmount = 100,
                            CreditLineIncluded = true,
                            Amount = 100,
                            Currency = "NOK",
                            Type = BalanceType.AvailableBalance
                        },
                        new Balance
                        {
                            CreditDebitIndicator = CreditOrDebit.Credit,
                            CreditLineAmount = 100,
                            CreditLineIncluded = true,
                            Amount = 100,
                            Currency = "NOK",
                            Type = BalanceType.AvailableBalance
                        }
                    }
                },
            };
        }

        private static Accounts GetDefaultAccounts(string bankName, string orgNumber)
        {
            return new Accounts
            {
                ResponseDetails = new ResponseDetails
                {
                    Message = "OK",
                    Status = ResponseDetailsStatus.Complete
                },
                Accounts1 =
                [
                    GetAccount(bankName, orgNumber, "1"),
                    GetAccount(bankName, orgNumber, "2"),
                ]
            };
        }

        private static Account GetAccount(string bankName, string orgNumber, string accRef)
        {
            return new Account
            {
                AccountReference = accRef,
                AccountIdentifier = Guid.NewGuid().ToString(),
                Type = AccountType.LoanAccount,
                Status = AccountStatus.Enabled,
                Servicer = new FinancialInstitution
                {
                    Identifier = new Identifier
                    {
                        Type = IdentifierType.NationalIdentityNumber,
                        Value = orgNumber
                    },
                    Name = bankName
                },
                PrimaryOwner = new AccountRole
                {
                    Identifier = new Identifier
                    {
                        Type = IdentifierType.NationalIdentityNumber,
                        Value = "12345678910"
                    }
                }
            };
        }
    }
}
