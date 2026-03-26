using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZimraFdms;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddZimraFdms(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfigurationSection configSection)
    {
        services.Configure<FdmsOptions>(configSection);

        // Public HTTP client — no client certificate
        services.AddHttpClient("FdmsPublic", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<FdmsOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        });

        // Device HTTP client — mTLS with device certificate
        services.AddHttpClient("FdmsDevice", (sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<FdmsOptions>>().Value;
            client.BaseAddress = new Uri(opts.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(opts.TimeoutSeconds);
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<FdmsOptions>>().Value;
            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual
            };

            // Try PFX first, then try PEM cert (built by InitializeAsync from LiteDB)
            if (!string.IsNullOrEmpty(opts.CertificatePfxPath) && File.Exists(opts.CertificatePfxPath))
            {
                handler.ClientCertificates.Add(
                    new X509Certificate2(opts.CertificatePfxPath, opts.CertificatePfxPassword));
            }

            return handler;
        });

        services.AddSingleton<FdmsCryptoService>(sp =>
            new FdmsCryptoService(sp.GetRequiredService<IOptions<FdmsOptions>>().Value));

        services.AddSingleton<ReceiptQueueStore>(sp =>
            new ReceiptQueueStore(
                sp.GetRequiredService<IOptions<FdmsOptions>>().Value,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ReceiptQueueStore>()));

        services.AddSingleton<FdmsHttpClient>();
        services.AddSingleton<FdmsService>();

        return services;
    }
}
