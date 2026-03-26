namespace ZimraFdms;

/// <summary>
/// Configuration for the ZIMRA FDMS integration service.
/// Bind from appsettings.json section "ZimraFdms".
/// </summary>
public class FdmsOptions
{
    public const string SectionName = "ZimraFdms";

    /// <summary>
    /// Base URL for FDMS API.
    /// Testing:    https://fdmsapitest.zimra.co.zw
    /// Production: https://fdmsapi.zimra.co.zw
    /// </summary>
    public string BaseUrl { get; set; } = "https://fdmsapitest.zimra.co.zw";

    /// <summary>Device model name as registered with ZIMRA.</summary>
    public string DeviceModelName { get; set; } = string.Empty;

    /// <summary>Device model version as registered with ZIMRA.</summary>
    public string DeviceModelVersion { get; set; } = string.Empty;

    /// <summary>Device ID assigned by ZIMRA during registration portal.</summary>
    public int DeviceID { get; set; }

    /// <summary>Device serial number assigned by manufacturer (max 20 chars).</summary>
    public string DeviceSerialNo { get; set; } = string.Empty;

    /// <summary>Activation key provided by ZIMRA (8 chars, case insensitive).</summary>
    public string ActivationKey { get; set; } = string.Empty;

    /// <summary>
    /// Path to the PFX/P12 file containing the device certificate + private key.
    /// Used for mTLS client authentication on Device API endpoints.
    /// </summary>
    public string? CertificatePfxPath { get; set; }

    /// <summary>Password for the PFX/P12 file.</summary>
    public string? CertificatePfxPassword { get; set; }

    /// <summary>
    /// Path to the device private key PEM file (used for receipt/fiscal day signing).
    /// If using PFX, the private key can be extracted from the certificate instead.
    /// </summary>
    public string? PrivateKeyPemPath { get; set; }

    /// <summary>Whether to use ECC secp256r1 (true, preferred) or RSA 2048 (false).</summary>
    public bool UseEcc { get; set; } = true;

    /// <summary>HTTP request timeout in seconds. FDMS spec says 30s for sync ops.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Max retry attempts for queued receipt submissions before giving up.</summary>
    public int MaxRetryAttempts { get; set; } = 10;

    /// <summary>Base delay between retry attempts in seconds (exponential backoff).</summary>
    public int RetryBaseDelaySeconds { get; set; } = 5;

    /// <summary>Max number of receipts to buffer in the in-memory submission channel.</summary>
    public int QueueCapacity { get; set; } = 10_000;

    /// <summary>
    /// Path to the LiteDB file for persistent receipt queue.
    /// Receipts are persisted here before entering the in-memory channel,
    /// so they survive app crashes and restarts.
    /// Default: "fdms-queue.db"
    /// </summary>
    public string QueueDbPath { get; set; } = "fdms-queue.db";
}
