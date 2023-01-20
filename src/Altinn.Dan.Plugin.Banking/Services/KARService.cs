using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.Dan.Plugin.Banking.Clients;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Altinn.Dan.Plugin.Banking.Services;

// ReSharper disable once InconsistentNaming
public class KARService : IKARService
{
    private const int KarRequestTimeoutSecs = 30;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMaskinportenService _maskinportenService;
    private readonly ApplicationSettings _settings;
    private readonly ILogger<KARService> _logger;

    public KARService(IHttpClientFactory httpClientFactory, IMaskinportenService maskinportenService, IOptions<ApplicationSettings> applicationSettings, ILogger<KARService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _maskinportenService = maskinportenService;
        _settings = applicationSettings.Value;
        _logger = logger;
    }

    public async Task<KARResponse> GetBanksForCustomer(string ssn, DateTimeOffset fromDate, DateTimeOffset toDate, Guid accountInfoRequestId, Guid correlationId)
    {
/*
        return new KARResponse()
        {
            Banks = new List<CustomerRelation>()
            {
                new()
                {
                    ActiveAccount = true,
                    BankName = "SBANKEN ASA",
                    OrganizationID = "915287700"
                },
                new()
                {
                    ActiveAccount = true,
                    BankName = "SPAREBANK 1 SMN",
                    OrganizationID = "937901003"
                }
            }
        };
*/
        var kar = new KAR(_httpClientFactory.CreateClient("KAR"))
        {
            BaseUrl = _settings.KarUrl
        };

        var token = await _maskinportenService.GetToken(_settings.Certificate, _settings.MaskinportenEnvironment,
            _settings.ClientId, "bits:kundeforhold", null);

        var karTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(KarRequestTimeoutSecs));

        return await kar.Get(ssn, token.AccessToken, fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"),
            accountInfoRequestId, correlationId, karTimeout.Token);
    }
}
