using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.ApiClients.Maskinporten.Services;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Services;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Dan.Common.Extensions;
using Dan.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureDanPluginDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient();
        services.Configure<ApplicationSettings>(context.Configuration);

        services.AddMemoryCache();
        services.AddSingleton<ITokenCacheProvider, MemoryTokenCacheProvider>();
        services.AddSingleton<IMaskinportenService, MaskinportenService>();
        services.AddSingleton<IMemoryCacheProvider, MemoryCacheProvider>();
        services.AddTransient<IBankService, BankService>();
        services.AddTransient<IKARService, KARService>();
        services.AddTransient<IDanPluginClientService, DanPluginClientService>();

    })
    .Build();

await host.RunAsync();
