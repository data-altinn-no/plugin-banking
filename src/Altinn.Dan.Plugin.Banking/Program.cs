using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Altinn.ApiClients.Maskinporten.Interfaces;
using Altinn.ApiClients.Maskinporten.Services;
using Altinn.Dan.Plugin.Banking.Config;
using Altinn.Dan.Plugin.Banking.Services;
using Altinn.Dan.Plugin.Banking.Services.Interfaces;
using Dan.Common.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Altinn.Dan.Plugin.Banking
{
    class Program
    {

        private static Task Main(string[] args)
        {
            var host = new HostBuilder()
                .ConfigureDanPluginDefaults()
                .ConfigureServices(services =>
                {
                    services.AddLogging();

                    // See https://docs.microsoft.com/en-us/azure/azure-monitor/app/worker-service#using-application-insights-sdk-for-worker-services
                    services.AddApplicationInsightsTelemetryWorkerService();

                    services.AddHttpClient();
                    services.AddOptions<ApplicationSettings>()
                                            .Configure<IConfiguration>((settings, configuration) => configuration.Bind(settings));

                    services.Configure<JsonSerializerOptions>(options =>
                    {
                        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                        options.Converters.Add(new JsonStringEnumConverter());
                    });

                    services.AddMemoryCache();
                    services.AddSingleton<ITokenCacheProvider, MemoryTokenCacheProvider>();
                    services.AddSingleton<IMaskinportenService, MaskinportenService>();

                    services.AddSingleton<IBankService, BankService>();
                    services.AddSingleton<IKARService, KARService>();

                })
                .Build();
            return host.RunAsync();
        }
    }
}
