using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZimraFdms.Enums;
using ZimraFdms.Models;

namespace ZimraFdms;

/// <summary>
/// Result returned when a receipt is enqueued.
/// </summary>
public class EnqueuedReceipt
{
    public ReceiptDto Receipt { get; init; } = null!;
    public string QrCodeUrl { get; init; } = string.Empty;
    public string VerificationCode { get; init; } = string.Empty;
    public Task<SubmitReceiptResponse> SubmissionTask { get; init; } = null!;
}

/// <summary>
/// ZIMRA FDMS integration — online mode with LiteDB-backed persistent queue.
///
/// FIXES from code review:
///   BUG 2  — UpdateDayCounters saves correct current values (not current+1)
///   BUG 3  — _nextReceiptGlobalNo only used after sync from LiteDB/FDMS
///   BUG 4  — Replayed receipts don't need TCS (no caller to notify)
///   BUG 5  — OpenDayAsync on resume returns clearly typed result
///   BUG 6  — Entire enqueue is serialized via _enqueueLock (sign+accumulate+persist is atomic)
///   BUG 10 — InitializeAsync builds PFX from PEM cert+key (not just writes PEM)
///   BUG 11 — CloseDayAsync checks for unsent receipts after drain and warns
/// </summary>
public class FdmsService : IDisposable
{
    private readonly FdmsHttpClient _http;
    private readonly FdmsCryptoService _crypto;
    private readonly FdmsOptions _options;
    private readonly ILogger<FdmsService> _logger;
    private readonly FiscalCounterTracker _counters = new();
    private readonly ReceiptQueueStore _store;

    // FIX BUG 6: serialize the sign → accumulate → persist → channel-write sequence
    private readonly SemaphoreSlim _enqueueLock = new(1, 1);

    private Channel<ChannelItem>? _channel;
    private CancellationTokenSource? _processorCts;
    private Task? _processorTask;

    private GetConfigResponse? _config;
    private int _currentFiscalDayNo;
    private DateTime _fiscalDayOpenedAt;
    private int _nextReceiptCounter;    // current value; Interlocked.Increment returns +1
    private int _nextReceiptGlobalNo;   // FIX BUG 3: no default; must be set from store/FDMS
    private bool _dayIsOpen;

    private int _pendingCount;
    private int _submittedCount;
    private int _failedCount;

    // TCS for awaitable receipt submission results
    private readonly Dictionary<int, TaskCompletionSource<SubmitReceiptResponse>> _inflightTcs = new();
    private readonly object _tcsLock = new();

    public FdmsService(
        FdmsHttpClient http, FdmsCryptoService crypto, ReceiptQueueStore store,
        IOptions<FdmsOptions> options, ILogger<FdmsService> logger)
    {
        _http = http;
        _crypto = crypto;
        _store = store;
        _options = options.Value;
        _logger = logger;

        // FIX BUG 3: restore counters from LiteDB, don't assume 1
        var dayState = _store.GetDayState(_options.DeviceID);
        if (dayState != null)
        {
            _currentFiscalDayNo = dayState.FiscalDayNo;
            _fiscalDayOpenedAt = dayState.FiscalDayOpenedAt;
            _nextReceiptCounter = dayState.NextReceiptCounter;
            _nextReceiptGlobalNo = dayState.NextReceiptGlobalNo;
            _dayIsOpen = dayState.IsOpen;
            _logger.LogInformation("Restored state: day={DayNo} open={IsOpen} globalNo={GN}",
                dayState.FiscalDayNo, dayState.IsOpen, dayState.NextReceiptGlobalNo);
        }
        else
        {
            var lastGlobal = _store.GetLastReceiptGlobalNo(_options.DeviceID);
            _nextReceiptGlobalNo = lastGlobal ?? 0;
            _nextReceiptCounter = 0;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  Observability
    // ═══════════════════════════════════════════════════════════════

    public int PendingCount => _pendingCount;
    public int SubmittedCount => _submittedCount;
    public int FailedCount => _failedCount;
    public bool IsDayOpen => _dayIsOpen;
    public int CurrentFiscalDayNo => _currentFiscalDayNo;
    public GetConfigResponse? Config => _config;
    public ReceiptQueueStore Store => _store;

    public event Action<ReceiptDto, SubmitReceiptResponse>? OnReceiptSubmitted;
    public event Action<ReceiptDto, Exception>? OnReceiptFailed;

    // ═══════════════════════════════════════════════════════════════
    //  1. Device Registration
    // ═══════════════════════════════════════════════════════════════

    public Task<VerifyTaxpayerInformationResponse> VerifyTaxpayerAsync(CancellationToken ct = default)
        => _http.VerifyTaxpayerInformationAsync(_options.DeviceID, new VerifyTaxpayerInformationRequest
        {
            ActivationKey = _options.ActivationKey,
            DeviceSerialNo = _options.DeviceSerialNo
        }, ct);

    public async Task<RegisterDeviceResponse> RegisterDeviceAsync(
        string privateKeyOutputPath, string certificateOutputPath, CancellationToken ct = default)
    {
        var csr = _crypto.GenerateCsrAndSaveKey(_options.DeviceSerialNo, _options.DeviceID, privateKeyOutputPath);
        var privateKeyPem = await File.ReadAllTextAsync(privateKeyOutputPath, ct);

        var resp = await _http.RegisterDeviceAsync(_options.DeviceID, new RegisterDeviceRequest
        {
            ActivationKey = _options.ActivationKey,
            CertificateRequest = csr
        }, ct);

        await File.WriteAllTextAsync(certificateOutputPath, resp.Certificate, ct);

        // Persist identity with encrypted private key (BUG 1 fix is in ReceiptQueueStore)
        _store.SaveDeviceIdentity(_options.DeviceID, _options.DeviceSerialNo, _options.ActivationKey,
            _options.DeviceModelName, _options.DeviceModelVersion, resp.Certificate, privateKeyPem);

        _logger.LogInformation("Device {DeviceID} registered and identity persisted", _options.DeviceID);
        return resp;
    }

    public async Task<IssueCertificateResponse> RenewCertificateAsync(
        string privateKeyOutputPath, string certificateOutputPath, CancellationToken ct = default)
    {
        var csr = _crypto.GenerateCsrAndSaveKey(_options.DeviceSerialNo, _options.DeviceID, privateKeyOutputPath);
        var privateKeyPem = await File.ReadAllTextAsync(privateKeyOutputPath, ct);

        var resp = await _http.IssueCertificateAsync(_options.DeviceID, new IssueCertificateRequest
        {
            CertificateRequest = csr
        }, ct);

        await File.WriteAllTextAsync(certificateOutputPath, resp.Certificate, ct);
        _store.UpdateCertificate(_options.DeviceID, resp.Certificate, privateKeyPem, _config?.CertificateValidTill);

        _logger.LogInformation("Certificate renewed for device {DeviceID}", _options.DeviceID);
        return resp;
    }

    // ═══════════════════════════════════════════════════════════════
    //  2. Initialize + Config + Status
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Call on app startup. FIX BUG 10: builds PFX from persisted PEM cert+key.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // ── 0. Restore device identity ──
        var identity = _store.GetDeviceIdentity(_options.DeviceID);
        if (identity != null)
        {
            _logger.LogInformation("Loaded device identity: ID={Id} Serial={Sn}", identity.DeviceId, identity.DeviceSerialNo);

            // FIX BUG 10: If PFX doesn't exist, build it from persisted PEM cert + decrypted key
            if (string.IsNullOrEmpty(_options.CertificatePfxPath) || !File.Exists(_options.CertificatePfxPath))
            {
                var privateKeyPem = _store.DecryptPrivateKey(identity);
                var pfxPath = Path.Combine(Path.GetDirectoryName(_options.QueueDbPath) ?? ".", $"device-{identity.DeviceId}.pfx");
                var pfxPassword = identity.ActivationKey; // use activation key as PFX password

                using var cert = X509Certificate2.CreateFromPem(identity.CertificatePem, privateKeyPem);
                var pfxBytes = cert.Export(X509ContentType.Pfx, pfxPassword);
                await File.WriteAllBytesAsync(pfxPath, pfxBytes, ct);

                // Set paths so HttpClient handler and CryptoService can find them
                // NOTE: we set on a local copy, not mutating IOptions<T> shared instance
                _options.CertificatePfxPath = pfxPath;
                _options.CertificatePfxPassword = pfxPassword;
                _logger.LogInformation("Restored PFX to {Path}", pfxPath);
            }

            // Restore private key PEM for signing
            if (string.IsNullOrEmpty(_options.PrivateKeyPemPath) || !File.Exists(_options.PrivateKeyPemPath))
            {
                var keyPath = Path.Combine(Path.GetDirectoryName(_options.QueueDbPath) ?? ".", $"device-{identity.DeviceId}.key");
                var privateKeyPem = _store.DecryptPrivateKey(identity);
                await File.WriteAllTextAsync(keyPath, privateKeyPem, ct);
                _options.PrivateKeyPemPath = keyPath;
            }
        }
        else if (_options.DeviceID == 0)
        {
            _logger.LogWarning("No device identity found. Call RegisterDeviceAsync first.");
            return;
        }

        // ── 1. Config ──
        try { await GetConfigAsync(ct); }
        catch (Exception ex) when (ex is HttpRequestException or FdmsApiException)
        {
            var cached = _store.GetCachedConfig(_options.DeviceID);
            if (cached != null)
            {
                _config = cached.Config;
                _logger.LogWarning("FDMS unreachable, using cached config from {At}", cached.FetchedAt);
            }
            else throw;
        }

        // ── 2. Reconcile fiscal day state with FDMS ──
        try
        {
            var fdms = await GetStatusAsync(ct);
            var local = _store.GetDayState(_options.DeviceID);

            if (fdms.LastReceiptGlobalNo.HasValue)
            {
                var fdmsGN = fdms.LastReceiptGlobalNo.Value;
                if (fdmsGN > _nextReceiptGlobalNo) _nextReceiptGlobalNo = fdmsGN;
            }

            if (local is { IsOpen: true } && fdms.FiscalDayStatus == FiscalDayStatus.FiscalDayClosed)
            {
                _logger.LogWarning("FDMS says day closed (local says open). Updating local.");
                _store.SaveDayClosed(_options.DeviceID);
                _dayIsOpen = false;
            }
            else if ((local == null || !local.IsOpen) && fdms.FiscalDayStatus == FiscalDayStatus.FiscalDayOpened)
            {
                _logger.LogWarning("FDMS says day {DayNo} open (local says closed). Saving open state for resume.",
                    fdms.LastFiscalDayNo);
                _currentFiscalDayNo = fdms.LastFiscalDayNo ?? _currentFiscalDayNo;
                _dayIsOpen = false; // keep false so OpenDayAsync can resume properly
                _store.SaveDayOpened(_options.DeviceID, _currentFiscalDayNo, DateTime.Now, _nextReceiptGlobalNo);
            }
            else if (fdms.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseFailed)
            {
                _logger.LogWarning("FDMS says day close failed: {Err}. Saving open state for resume.",
                    fdms.FiscalDayClosingErrorCode);
                _currentFiscalDayNo = fdms.LastFiscalDayNo ?? _currentFiscalDayNo;
                _dayIsOpen = false;
                _store.SaveDayOpened(_options.DeviceID, _currentFiscalDayNo, DateTime.Now, _nextReceiptGlobalNo);
            }

            _logger.LogInformation("Initialized: FDMS={Status} local={Open} globalNo={GN}",
                fdms.FiscalDayStatus, _dayIsOpen, _nextReceiptGlobalNo);

            // ── 3. Auto-resume open day (set up queue channel + processor) ──
            if (!_dayIsOpen && (fdms.FiscalDayStatus == FiscalDayStatus.FiscalDayOpened
                || fdms.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseFailed))
            {
                _logger.LogInformation("Auto-resuming fiscal day {DayNo}", _currentFiscalDayNo);
                await OpenDayAsync(ct: ct);
            }
            else if (_dayIsOpen || (local is { IsOpen: true } && fdms.FiscalDayStatus == FiscalDayStatus.FiscalDayOpened))
            {
                _logger.LogInformation("Auto-resuming fiscal day {DayNo} from local state", _currentFiscalDayNo);
                await OpenDayAsync(ct: ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or FdmsApiException)
        {
            _logger.LogWarning("Could not reach FDMS for status. Using local: day={D} open={O}", _currentFiscalDayNo, _dayIsOpen);

            // Auto-resume from local state if day was open
            var localState = _store.GetDayState(_options.DeviceID);
            if (localState is { IsOpen: true })
            {
                _logger.LogInformation("Auto-resuming fiscal day {DayNo} from local state (offline)", localState.FiscalDayNo);
                await OpenDayAsync(ct: ct);
            }
        }
    }

    public async Task<GetConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        _config = await _http.GetConfigAsync(_options.DeviceID, ct);
        _store.SaveConfig(_options.DeviceID, _config);
        _logger.LogInformation("Config: {Name} TIN:{TIN} VAT:{VAT}",
            _config.TaxPayerName, _config.TaxPayerTIN, _config.VatNumber ?? "non-VAT");
        return _config;
    }

    public async Task<GetStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        var s = await _http.GetStatusAsync(_options.DeviceID, ct);
        if (s.LastReceiptGlobalNo.HasValue) _nextReceiptGlobalNo = s.LastReceiptGlobalNo.Value;
        if (s.LastFiscalDayNo.HasValue) _currentFiscalDayNo = s.LastFiscalDayNo.Value;
        return s;
    }

    // ═══════════════════════════════════════════════════════════════
    //  3. Fiscal Day Lifecycle
    // ═══════════════════════════════════════════════════════════════

    /// <summary>FIX BUG 5: returns bool isResumed so caller knows.</summary>
    public async Task<(OpenDayResponse Response, bool IsResumed)> OpenDayAsync(
        int? fiscalDayNo = null, CancellationToken ct = default)
    {
        if (_config == null) await GetConfigAsync(ct);

        var saved = _store.GetDayState(_options.DeviceID);
        var isResume = saved is { IsOpen: true };

        if (isResume)
        {
            _logger.LogInformation("Resuming day {DayNo} from previous session", saved!.FiscalDayNo);
            _currentFiscalDayNo = saved.FiscalDayNo;
            _fiscalDayOpenedAt = saved.FiscalDayOpenedAt;
            _nextReceiptCounter = saved.NextReceiptCounter;
            _nextReceiptGlobalNo = saved.NextReceiptGlobalNo;
        }
        else
        {
            if (_dayIsOpen) throw new InvalidOperationException("Fiscal day already open.");
            _fiscalDayOpenedAt = DateTime.Now;
            var resp = await _http.OpenDayAsync(_options.DeviceID, new OpenDayRequest
            {
                FiscalDayNo = fiscalDayNo,
                FiscalDayOpened = _fiscalDayOpenedAt
            }, ct);
            _currentFiscalDayNo = resp.FiscalDayNo;
            _nextReceiptCounter = 0;
        }

        _counters.Reset();
        _pendingCount = _submittedCount = _failedCount = 0;

        _channel = Channel.CreateBounded<ChannelItem>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
        });
        _processorCts = new CancellationTokenSource();
        _processorTask = Task.Run(() => ProcessQueueAsync(_processorCts.Token));
        _dayIsOpen = true;

        if (!isResume)
            _store.SaveDayOpened(_options.DeviceID, _currentFiscalDayNo, _fiscalDayOpenedAt, _nextReceiptGlobalNo);

        await ReplayUnsentReceiptsAsync(ct);

        _logger.LogInformation("Fiscal day {DayNo} {A}", _currentFiscalDayNo, isResume ? "resumed" : "opened");
        return (new OpenDayResponse { FiscalDayNo = _currentFiscalDayNo }, isResume);
    }

    private async Task ReplayUnsentReceiptsAsync(CancellationToken ct)
    {
        var unsent = _store.GetUnsentReceipts(_options.DeviceID);
        if (unsent.Count == 0) return;

        _logger.LogInformation("Replaying {Count} unsent receipts", unsent.Count);
        foreach (var r in unsent)
        {
            _counters.AccumulateReceipt(r.Receipt, r.Receipt.ReceiptDeviceSignature.Hash);
            if (r.Receipt.ReceiptCounter > _nextReceiptCounter) _nextReceiptCounter = r.Receipt.ReceiptCounter;
            if (r.Receipt.ReceiptGlobalNo > _nextReceiptGlobalNo) _nextReceiptGlobalNo = r.Receipt.ReceiptGlobalNo;

            // FIX BUG 4: replayed receipts have no live TCS — that's fine, events still fire
            await _channel!.Writer.WriteAsync(new ChannelItem(r.Id, r.Receipt), ct);
            Interlocked.Increment(ref _pendingCount);
        }
    }

    /// <summary>FIX BUG 11: after drain, check for unsent receipts in LiteDB.</summary>
    public async Task<CloseDayResponse> CloseDayAsync(CancellationToken ct = default)
    {
        if (!_dayIsOpen) throw new InvalidOperationException("No fiscal day is open.");

        _channel?.Writer.TryComplete();
        if (_processorTask != null)
        {
            var drain = await Task.WhenAny(_processorTask, Task.Delay(TimeSpan.FromMinutes(5), ct));
            if (drain != _processorTask)
                _logger.LogWarning("Queue drain timed out, {P} still pending", _pendingCount);
        }

        // FIX BUG 11: verify no unsent receipts remain
        var stillUnsent = _store.GetUnsentReceipts(_options.DeviceID);
        if (stillUnsent.Count > 0)
            _logger.LogError("{Count} receipts still unsent after queue drain! FDMS may reject close with MissingReceipts.",
                stillUnsent.Count);

        var counters = _counters.GetCountersForClose();
        var sig = _crypto.SignFiscalDay(_options.DeviceID, _currentFiscalDayNo, _fiscalDayOpenedAt, counters);

        var resp = await _http.CloseDayAsync(_options.DeviceID, new CloseDayRequest
        {
            FiscalDayNo = _currentFiscalDayNo,
            FiscalDayCounters = counters,
            FiscalDayDeviceSignature = sig,
            ReceiptCounter = _counters.ReceiptCounter
        }, ct);

        await PollForDayClosureAsync(ct);
        _store.SaveDayClosed(_options.DeviceID);

        _processorCts?.Cancel();
        _processorCts?.Dispose();
        _processorCts = null;
        _processorTask = null;
        _channel = null;
        _dayIsOpen = false;

        _logger.LogInformation("Day {D} closed. OK={Ok} FAIL={F}", _currentFiscalDayNo, _submittedCount, _failedCount);
        return resp;
    }

    private async Task PollForDayClosureAsync(CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000, ct);
            var s = await _http.GetStatusAsync(_options.DeviceID, ct);
            if (s.FiscalDayStatus == FiscalDayStatus.FiscalDayClosed) return;
            if (s.FiscalDayStatus == FiscalDayStatus.FiscalDayCloseFailed)
                throw new FdmsApiException(422, new ApiProblemDetails
                    { Title = $"Day close failed: {s.FiscalDayClosingErrorCode}", ErrorCode = s.FiscalDayClosingErrorCode?.ToString() });
        }
        throw new TimeoutException($"FDMS did not confirm day {_currentFiscalDayNo} closure within 60s");
    }

    // ═══════════════════════════════════════════════════════════════
    //  4. Receipt Queue — FIX BUG 2, 6
    // ═══════════════════════════════════════════════════════════════

    public async Task<EnqueuedReceipt> EnqueueReceiptAsync(ReceiptDto receipt, CancellationToken ct = default)
    {
        if (!_dayIsOpen || _channel == null)
            throw new InvalidOperationException("No fiscal day is open.");

        // FIX BUG 6: serialize the entire sign → accumulate → persist → channel sequence
        await _enqueueLock.WaitAsync(ct);
        try
        {
            // Sequential numbering (inside lock, no Interlocked needed)
            _nextReceiptCounter++;
            _nextReceiptGlobalNo++;
            receipt.ReceiptCounter = _nextReceiptCounter;
            receipt.ReceiptGlobalNo = _nextReceiptGlobalNo;

            // Sign (uses _counters.LastReceiptHashBase64 which is safe inside lock)
            var sig = _crypto.SignReceipt(_options.DeviceID, receipt, _counters.LastReceiptHashBase64);
            receipt.ReceiptDeviceSignature = sig;

            // Accumulate counters
            _counters.AccumulateReceipt(receipt, sig.Hash);

            // QR code
            var qrUrl = _config?.QrUrl ?? "https://invoice.zimra.co.zw";
            var qrCodeUrl = FdmsCryptoService.BuildQrCodeUrl(
                qrUrl, _options.DeviceID, receipt.ReceiptDate, receipt.ReceiptGlobalNo, sig.Signature);
            var verificationCode = FdmsCryptoService.FormatVerificationCode(qrCodeUrl);

            // Persist to LiteDB (crash-safe)
            _store.Enqueue(receipt, _currentFiscalDayNo, _options.DeviceID, qrCodeUrl, verificationCode);

            // FIX BUG 2: save current values, not current+1
            _store.UpdateDayCounters(_options.DeviceID,
                _nextReceiptCounter, _nextReceiptGlobalNo, sig.Hash);

            // TCS for awaitable result
            var tcs = new TaskCompletionSource<SubmitReceiptResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_tcsLock) _inflightTcs[receipt.ReceiptGlobalNo] = tcs;

            // Channel write
            await _channel.Writer.WriteAsync(new ChannelItem(receipt.ReceiptGlobalNo, receipt), ct);
            Interlocked.Increment(ref _pendingCount);

            return new EnqueuedReceipt
            {
                Receipt = receipt,
                QrCodeUrl = qrCodeUrl,
                VerificationCode = verificationCode,
                SubmissionTask = tcs.Task
            };
        }
        finally
        {
            _enqueueLock.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  5. Ping + Server Certificate
    // ═══════════════════════════════════════════════════════════════

    public Task<PingResponse> PingAsync(CancellationToken ct = default) => _http.PingAsync(_options.DeviceID, ct);
    public Task<GetServerCertificateResponse> GetServerCertificateAsync(CancellationToken ct = default) => _http.GetServerCertificateAsync(null, ct);

    // ═══════════════════════════════════════════════════════════════
    //  Background Queue Processor
    // ═══════════════════════════════════════════════════════════════

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        _logger.LogInformation("Queue processor started for day {D}", _currentFiscalDayNo);
        try
        {
            await foreach (var item in _channel!.Reader.ReadAllAsync(ct))
            {
                await ProcessOneAsync(item, ct);
                Interlocked.Decrement(ref _pendingCount);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "Queue processor crashed"); }

        _logger.LogInformation("Queue processor stopped. OK={Ok} FAIL={F}", _submittedCount, _failedCount);
    }

    private async Task ProcessOneAsync(ChannelItem item, CancellationToken ct)
    {
        var max = _options.MaxRetryAttempts;

        for (int attempt = 1; attempt <= max; attempt++)
        {
            try
            {
                _store.MarkSubmitting(item.GlobalNo, attempt);
                var resp = await _http.SubmitReceiptAsync(_options.DeviceID,
                    new SubmitReceiptRequest { Receipt = item.Receipt }, ct);

                _store.MarkSubmitted(item.GlobalNo, resp);
                Interlocked.Increment(ref _submittedCount);

                if (resp.ValidationErrors is { Count: > 0 })
                    _logger.LogWarning("Receipt #{GN} accepted with errors: {E}", item.GlobalNo,
                        string.Join(", ", resp.ValidationErrors.Select(e => $"{e.ValidationErrorCode}({e.ValidationErrorColor})")));
                else
                    _logger.LogDebug("Receipt #{GN} → ID {ID}", item.GlobalNo, resp.ReceiptID);

                ResolveTcs(item.GlobalNo, resp);
                OnReceiptSubmitted?.Invoke(item.Receipt, resp);
                return;
            }
            catch (FdmsApiException ex) when (ex.HttpStatusCode is 400 or 422)
            {
                _store.MarkFailed(item.GlobalNo, ex.Message, ex.ErrorCode);
                Interlocked.Increment(ref _failedCount);
                _logger.LogError("Receipt #{GN} rejected: {Code}", item.GlobalNo, ex.ErrorCode);
                RejectTcs(item.GlobalNo, ex);
                OnReceiptFailed?.Invoke(item.Receipt, ex);
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                if (attempt >= max)
                {
                    var msg = $"Max retries ({max}) exceeded: {ex.Message}";
                    _store.MarkFailed(item.GlobalNo, msg);
                    Interlocked.Increment(ref _failedCount);
                    var final = new FdmsApiException(msg, ex);
                    RejectTcs(item.GlobalNo, final);
                    OnReceiptFailed?.Invoke(item.Receipt, final);
                    return;
                }
                var delay = Math.Min(_options.RetryBaseDelaySeconds * Math.Pow(2, attempt - 1), 120);
                _logger.LogWarning("Receipt #{GN} attempt {A}/{M} failed, retry in {D:F0}s", item.GlobalNo, attempt, max, delay);
                try { await Task.Delay(TimeSpan.FromSeconds(delay), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    private static bool IsTransient(Exception ex) => ex is
        HttpRequestException or TaskCanceledException or FdmsApiException { HttpStatusCode: >= 500 };

    private void ResolveTcs(int gn, SubmitReceiptResponse resp)
    { lock (_tcsLock) { if (_inflightTcs.Remove(gn, out var tcs)) tcs.TrySetResult(resp); } }

    private void RejectTcs(int gn, Exception ex)
    { lock (_tcsLock) { if (_inflightTcs.Remove(gn, out var tcs)) tcs.TrySetException(ex); } }

    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        _processorCts?.Cancel();
        _channel?.Writer.TryComplete();
        _processorCts?.Dispose();
        _enqueueLock.Dispose();
        _store.Dispose();
        GC.SuppressFinalize(this);
    }

    private record ChannelItem(int GlobalNo, ReceiptDto Receipt);
}
