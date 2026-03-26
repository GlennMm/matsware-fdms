using System.Security.Cryptography;
using System.Text;
using LiteDB;
using Microsoft.Extensions.Logging;
using ZimraFdms.Enums;
using ZimraFdms.Models;

namespace ZimraFdms;

public enum ReceiptQueueStatus { Pending = 0, Submitting = 1, Submitted = 2, Failed = 3 }

public class PersistedReceipt
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public int FiscalDayNo { get; set; }
    public ReceiptQueueStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTime EnqueuedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public long? FdmsReceiptId { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FdmsErrorCode { get; set; }
    public List<string>? ValidationErrors { get; set; }
    public ReceiptDto Receipt { get; set; } = null!;
    public string QrCodeUrl { get; set; } = string.Empty;
    public string VerificationCode { get; set; } = string.Empty;
}

public class PersistedFiscalDayState
{
    public int Id { get; set; }
    public int FiscalDayNo { get; set; }
    public DateTime FiscalDayOpenedAt { get; set; }
    public bool IsOpen { get; set; }
    public int NextReceiptCounter { get; set; }
    public int NextReceiptGlobalNo { get; set; }
    public string? LastReceiptHashBase64 { get; set; }
}

public class PersistedDeviceConfig
{
    public int Id { get; set; }
    public DateTime FetchedAt { get; set; }
    public GetConfigResponse Config { get; set; } = null!;
}

/// <summary>
/// Device identity with encrypted private key. FIX BUG 1.
/// The private key PEM is encrypted with AES-256-GCM using a key derived
/// from the device serial number + activation key via PBKDF2.
/// This isn't military-grade (the derivation inputs are in the same DB),
/// but it prevents casual extraction by anyone who just opens the .db file.
/// </summary>
public class PersistedDeviceIdentity
{
    public int Id { get; set; }
    public int DeviceId { get; set; }
    public string DeviceSerialNo { get; set; } = string.Empty;
    public string ActivationKey { get; set; } = string.Empty;
    public string DeviceModelName { get; set; } = string.Empty;
    public string DeviceModelVersion { get; set; } = string.Empty;
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>AES-256-GCM encrypted private key (base64). NOT plain text.</summary>
    public string EncryptedPrivateKey { get; set; } = string.Empty;

    /// <summary>Nonce used for AES-GCM encryption (base64).</summary>
    public string EncryptionNonce { get; set; } = string.Empty;

    /// <summary>Auth tag from AES-GCM (base64).</summary>
    public string EncryptionTag { get; set; } = string.Empty;

    public DateTime CertificateIssuedAt { get; set; }
    public DateTime? CertificateValidTill { get; set; }
    public DateTime RegisteredAt { get; set; }
}

public class ReceiptQueueStore : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<PersistedReceipt> _receipts;
    private readonly ILiteCollection<PersistedFiscalDayState> _dayState;
    private readonly ILiteCollection<PersistedDeviceConfig> _deviceConfig;
    private readonly ILiteCollection<PersistedDeviceIdentity> _deviceIdentity;
    private readonly ILogger<ReceiptQueueStore> _logger;

    public ReceiptQueueStore(FdmsOptions options, ILogger<ReceiptQueueStore> logger)
    {
        _logger = logger;
        var dbPath = options.QueueDbPath ?? "fdms-queue.db";

        _db = new LiteDatabase($"Filename={dbPath};Connection=shared");
        _receipts = _db.GetCollection<PersistedReceipt>("receipt_queue");
        _dayState = _db.GetCollection<PersistedFiscalDayState>("fiscal_day_state");
        _deviceConfig = _db.GetCollection<PersistedDeviceConfig>("device_config");
        _deviceIdentity = _db.GetCollection<PersistedDeviceIdentity>("device_identity");

        _receipts.EnsureIndex(x => x.Status);
        _receipts.EnsureIndex(x => x.FiscalDayNo);
        _receipts.EnsureIndex(x => x.DeviceId);
    }

    // ─── Device Identity (BUG 1 FIX: encrypted private key) ────

    public void SaveDeviceIdentity(int deviceId, string serialNo, string activationKey,
        string modelName, string modelVersion, string certificatePem, string privateKeyPem)
    {
        var (encrypted, nonce, tag) = EncryptPrivateKey(privateKeyPem, serialNo, activationKey);

        _deviceIdentity.Upsert(new PersistedDeviceIdentity
        {
            Id = deviceId,
            DeviceId = deviceId,
            DeviceSerialNo = serialNo,
            ActivationKey = activationKey,
            DeviceModelName = modelName,
            DeviceModelVersion = modelVersion,
            CertificatePem = certificatePem,
            EncryptedPrivateKey = encrypted,
            EncryptionNonce = nonce,
            EncryptionTag = tag,
            CertificateIssuedAt = DateTime.UtcNow,
            RegisteredAt = DateTime.UtcNow
        });
        _logger.LogInformation("Device identity saved (private key encrypted) for device {DeviceId}", deviceId);
    }

    public PersistedDeviceIdentity? GetDeviceIdentity(int deviceId) => _deviceIdentity.FindById(deviceId);

    /// <summary>Decrypts and returns the private key PEM.</summary>
    public string DecryptPrivateKey(PersistedDeviceIdentity identity)
    {
        return DecryptPrivateKeyInternal(
            identity.EncryptedPrivateKey, identity.EncryptionNonce, identity.EncryptionTag,
            identity.DeviceSerialNo, identity.ActivationKey);
    }

    public void UpdateCertificate(int deviceId, string certificatePem, string privateKeyPem, DateTime? validTill)
    {
        var identity = _deviceIdentity.FindById(deviceId);
        if (identity == null) return;

        var (encrypted, nonce, tag) = EncryptPrivateKey(privateKeyPem, identity.DeviceSerialNo, identity.ActivationKey);
        identity.CertificatePem = certificatePem;
        identity.EncryptedPrivateKey = encrypted;
        identity.EncryptionNonce = nonce;
        identity.EncryptionTag = tag;
        identity.CertificateIssuedAt = DateTime.UtcNow;
        identity.CertificateValidTill = validTill;
        _deviceIdentity.Update(identity);
        _logger.LogInformation("Certificate updated for device {DeviceId}", deviceId);
    }

    // ─── Encryption helpers ─────────────────────────────────────

    private static byte[] DeriveKey(string serial, string activationKey)
    {
        var salt = Encoding.UTF8.GetBytes($"ZimraFdms-{serial}");
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(activationKey), salt, 100_000, HashAlgorithmName.SHA256, 32);
    }

    private static (string encrypted, string nonce, string tag) EncryptPrivateKey(
        string privateKeyPem, string serial, string activationKey)
    {
        var key = DeriveKey(serial, activationKey);
        var plaintext = Encoding.UTF8.GetBytes(privateKeyPem);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return (Convert.ToBase64String(ciphertext), Convert.ToBase64String(nonce), Convert.ToBase64String(tag));
    }

    private static string DecryptPrivateKeyInternal(
        string encryptedB64, string nonceB64, string tagB64, string serial, string activationKey)
    {
        var key = DeriveKey(serial, activationKey);
        var ciphertext = Convert.FromBase64String(encryptedB64);
        var nonce = Convert.FromBase64String(nonceB64);
        var tag = Convert.FromBase64String(tagB64);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    // ─── Device Config ──────────────────────────────────────────

    public void SaveConfig(int deviceId, GetConfigResponse config)
    {
        _deviceConfig.Upsert(new PersistedDeviceConfig { Id = deviceId, FetchedAt = DateTime.UtcNow, Config = config });
    }

    public PersistedDeviceConfig? GetCachedConfig(int deviceId) => _deviceConfig.FindById(deviceId);

    // ─── Fiscal Day State ───────────────────────────────────────

    public void SaveDayOpened(int deviceId, int fiscalDayNo, DateTime openedAt, int nextReceiptGlobalNo)
    {
        _dayState.Upsert(new PersistedFiscalDayState
        {
            Id = deviceId, FiscalDayNo = fiscalDayNo, FiscalDayOpenedAt = openedAt,
            IsOpen = true, NextReceiptCounter = 1, NextReceiptGlobalNo = nextReceiptGlobalNo
        });
    }

    public void UpdateDayCounters(int deviceId, int nextReceiptCounter, int nextReceiptGlobalNo, string? lastReceiptHash)
    {
        var state = _dayState.FindById(deviceId);
        if (state == null) return;
        state.NextReceiptCounter = nextReceiptCounter;
        state.NextReceiptGlobalNo = nextReceiptGlobalNo;
        state.LastReceiptHashBase64 = lastReceiptHash;
        _dayState.Update(state);
    }

    public void SaveDayClosed(int deviceId)
    {
        var state = _dayState.FindById(deviceId);
        if (state == null) return;
        state.IsOpen = false;
        _dayState.Update(state);
    }

    public PersistedFiscalDayState? GetDayState(int deviceId) => _dayState.FindById(deviceId);

    // ─── Receipt Queue ──────────────────────────────────────────

    public void Enqueue(ReceiptDto receipt, int fiscalDayNo, int deviceId, string qrCodeUrl, string verificationCode)
    {
        _receipts.Insert(new PersistedReceipt
        {
            Id = receipt.ReceiptGlobalNo, DeviceId = deviceId, FiscalDayNo = fiscalDayNo,
            Status = ReceiptQueueStatus.Pending, EnqueuedAt = DateTime.UtcNow,
            Receipt = receipt, QrCodeUrl = qrCodeUrl, VerificationCode = verificationCode
        });
    }

    public void MarkSubmitting(int receiptGlobalNo, int attempt)
    {
        var r = _receipts.FindById(receiptGlobalNo);
        if (r == null) return;
        r.Status = ReceiptQueueStatus.Submitting;
        r.Attempts = attempt;
        r.LastAttemptAt = DateTime.UtcNow;
        _receipts.Update(r);
    }

    public void MarkSubmitted(int receiptGlobalNo, SubmitReceiptResponse response)
    {
        var r = _receipts.FindById(receiptGlobalNo);
        if (r == null) return;
        r.Status = ReceiptQueueStatus.Submitted;
        r.SubmittedAt = DateTime.UtcNow;
        r.FdmsReceiptId = response.ReceiptID;
        if (response.ValidationErrors is { Count: > 0 })
            r.ValidationErrors = response.ValidationErrors.Select(e => $"{e.ValidationErrorCode}({e.ValidationErrorColor})").ToList();
        _receipts.Update(r);
    }

    public void MarkFailed(int receiptGlobalNo, string errorMessage, string? fdmsErrorCode = null)
    {
        var r = _receipts.FindById(receiptGlobalNo);
        if (r == null) return;
        r.Status = ReceiptQueueStatus.Failed;
        r.ErrorMessage = errorMessage;
        r.FdmsErrorCode = fdmsErrorCode;
        _receipts.Update(r);
    }

    public List<PersistedReceipt> GetUnsentReceipts(int deviceId)
        => _receipts.Find(r => r.DeviceId == deviceId && (r.Status == ReceiptQueueStatus.Pending || r.Status == ReceiptQueueStatus.Submitting))
            .OrderBy(r => r.Id).ToList();

    public List<PersistedReceipt> GetReceiptsForDay(int deviceId, int fiscalDayNo)
        => _receipts.Find(r => r.DeviceId == deviceId && r.FiscalDayNo == fiscalDayNo).OrderBy(r => r.Id).ToList();

    public Dictionary<ReceiptQueueStatus, int> GetStatusCounts(int deviceId, int fiscalDayNo)
        => _receipts.Find(r => r.DeviceId == deviceId && r.FiscalDayNo == fiscalDayNo)
            .GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());

    public List<PersistedReceipt> GetFailedReceipts(int deviceId)
        => _receipts.Find(r => r.DeviceId == deviceId && r.Status == ReceiptQueueStatus.Failed).OrderBy(r => r.Id).ToList();

    public int? GetLastReceiptGlobalNo(int deviceId)
        => _receipts.Find(r => r.DeviceId == deviceId).OrderByDescending(r => r.Id).FirstOrDefault()?.Id;

    public int PurgeOldReceipts(int olderThanDays = 90)
    {
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        return _receipts.DeleteMany(r => r.Status == ReceiptQueueStatus.Submitted && r.SubmittedAt < cutoff);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
