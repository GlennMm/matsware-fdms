using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZimraFdms.Api.Data;
using ZimraFdms.Api.Data.Entities;

namespace ZimraFdms.Api.Services;

public class DeviceManager : IDisposable
{
    private readonly ConcurrentDictionary<int, FdmsService> _services = new();
    private readonly IServiceProvider _sp;
    private readonly ILoggerFactory _loggerFactory;

    public DeviceManager(IServiceProvider sp, ILoggerFactory loggerFactory)
    {
        _sp = sp;
        _loggerFactory = loggerFactory;
    }

    public async Task<FdmsService> GetOrCreateAsync(int userDeviceId)
    {
        if (_services.TryGetValue(userDeviceId, out var existing))
            return existing;

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.Devices.FindAsync(userDeviceId)
            ?? throw new InvalidOperationException($"Device {userDeviceId} not found.");

        return _services.GetOrAdd(userDeviceId, _ => BuildFdmsService(device));
    }

    public void Remove(int userDeviceId)
    {
        if (_services.TryRemove(userDeviceId, out var svc))
            svc.Dispose();
    }

    public async Task InitializeActiveDevicesAsync()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var activeDevices = await db.Devices
            .Where(d => d.IsActive && d.CertificatePfxPath != null)
            .ToListAsync();

        var logger = _loggerFactory.CreateLogger<DeviceManager>();

        foreach (var device in activeDevices)
        {
            try
            {
                var svc = _services.GetOrAdd(device.Id, _ => BuildFdmsService(device));
                await svc.InitializeAsync();
            }
            catch
            {
                // InitializeAsync may fail if PFX wasn't in HttpClient yet
                // (it restores PFX from LiteDB during init). Rebuild and retry.
                try
                {
                    if (_services.TryRemove(device.Id, out var old)) old.Dispose();
                    var svc = _services.GetOrAdd(device.Id, _ => BuildFdmsService(device));
                    await svc.InitializeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to initialize device {DeviceId} ({Label})", device.DeviceID, device.DeviceLabel);
                }
            }
        }
    }

    private FdmsService BuildFdmsService(UserDevice device)
    {
        var options = new FdmsOptions
        {
            BaseUrl = device.BaseUrl,
            DeviceModelName = device.DeviceModelName,
            DeviceModelVersion = device.DeviceModelVersion,
            DeviceID = device.DeviceID,
            DeviceSerialNo = device.DeviceSerialNo,
            ActivationKey = device.ActivationKey,
            CertificatePfxPath = device.CertificatePfxPath,
            CertificatePfxPassword = device.ActivationKey,
            PrivateKeyPemPath = device.PrivateKeyPemPath,
            UseEcc = device.UseEcc,
            QueueDbPath = device.QueueDbPath
        };

        var publicClient = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual
        };
        if (!string.IsNullOrEmpty(options.CertificatePfxPath) && File.Exists(options.CertificatePfxPath))
        {
            handler.ClientCertificates.Add(
                X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePfxPath, options.CertificatePfxPassword));
        }
        var deviceClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };

        var crypto = new FdmsCryptoService(options);
        var store = new ReceiptQueueStore(options, _loggerFactory.CreateLogger<ReceiptQueueStore>());
        var httpClient = new FdmsHttpClient(publicClient, deviceClient, options, _loggerFactory.CreateLogger<FdmsHttpClient>());
        var svc = new FdmsService(httpClient, crypto, store,
            Options.Create(options), _loggerFactory.CreateLogger<FdmsService>());

        return svc;
    }

    public void Dispose()
    {
        foreach (var svc in _services.Values)
            svc.Dispose();
        _services.Clear();
    }
}
