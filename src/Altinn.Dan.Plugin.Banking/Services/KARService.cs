using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.Dan.Plugin.Banking.Clients;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Models;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace Altinn.Dan.Plugin.Banking.Services;

// ReSharper disable once InconsistentNaming
public class KARService : IKARService
{
    private const int KarRequestTimeoutSecs = 30;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMaskinportenService _maskinportenService;
    private readonly ApplicationSettings _settings;

    public KARService(IHttpClientFactory httpClientFactory, IMaskinportenService maskinportenService, IOptions<ApplicationSettings> applicationSettings)
    {
        _httpClientFactory = httpClientFactory;
        _maskinportenService = maskinportenService;
        _settings = applicationSettings.Value;
    }

    public async Task<KARResponse> GetBanksForCustomer(string ssn, DateTimeOffset fromDate, DateTimeOffset toDate, Guid accountInfoRequestId, Guid correlationId, bool skipKAR)
    {
        if (skipKAR)
        {
            return await GetAllImplementedBanks();
        }

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

    private static readonly KARResponse ImplementedBanksCache = new() { Banks = new List<CustomerRelation>() };
    private async Task<KARResponse> GetAllImplementedBanks()
    {
        if (ImplementedBanksCache.Banks.Count > 0) return ImplementedBanksCache;

        var banks = _settings.ImplementedBanks.Split(',');
        var erClient = _httpClientFactory.CreateClient("er");
        var customerRelationTasks = banks.Select(bank => Task.Run(async () =>
        {
            var response = await erClient.GetFromJsonAsync<ER>($"https://data.brreg.no/enhetsregisteret/api/enheter/{bank}");
            return new CustomerRelation { ActiveAccount = true, BankName = response.navn, OrganizationID = response.organisasjonsNummer };
        }))
        .ToList();

        await Task.WhenAll(customerRelationTasks);
        foreach (var customerRelationTask in customerRelationTasks)
        {
            ImplementedBanksCache.Banks.Add(customerRelationTask.Result);
        }

        return ImplementedBanksCache;
    }
}

internal class ER {
    public string organisasjonsNummer { get; set; }
    public string navn { get; set; }
}
