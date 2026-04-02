using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZimraFdms.Models;

namespace ZimraFdms;

/// <summary>
/// Low-level typed HTTP client for the ZIMRA Fiscal Device Gateway API.
/// Uses per-request headers (not DefaultRequestHeaders) for thread safety.
/// </summary>
public class FdmsHttpClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly FdmsOptions _options;
    private readonly ILogger<FdmsHttpClient> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new FdmsDateTimeConverter()
        }
    };

    /// <summary>
    /// FDMS requires dates as local time without timezone offset: "yyyy-MM-ddTHH:mm:ss"
    /// </summary>
    private class FdmsDateTimeConverter : JsonConverter<DateTime>
    {
        private const string Format = "yyyy-MM-ddTHH:mm:ss";

        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var str = reader.GetString();
            return str != null ? DateTime.Parse(str) : default;
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(Format));
        }
    }

    public FdmsHttpClient(IHttpClientFactory httpFactory, IOptions<FdmsOptions> options, ILogger<FdmsHttpClient> logger)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Constructor for direct injection of pre-built HttpClient instances.
    /// Intended for multi-device scenarios where DeviceManager creates per-device
    /// HTTP clients with distinct mTLS certificates.
    /// The <paramref name="publicClient"/> is used for public (no-cert) endpoints
    /// and the <paramref name="deviceClient"/> is used for device (mTLS) endpoints.
    /// </summary>
    public FdmsHttpClient(HttpClient publicClient, HttpClient deviceClient, FdmsOptions options, ILogger<FdmsHttpClient> logger)
    {
        _httpFactory = new DirectHttpClientFactory(publicClient, deviceClient);
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Minimal IHttpClientFactory implementation that serves two pre-built HttpClient
    /// instances by name ("FdmsPublic" and "FdmsDevice"), enabling the direct-injection
    /// constructor to reuse the existing SendAsync / SubmitFileAsync logic unchanged.
    /// </summary>
    private sealed class DirectHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _publicClient;
        private readonly HttpClient _deviceClient;

        public DirectHttpClientFactory(HttpClient publicClient, HttpClient deviceClient)
        {
            _publicClient = publicClient;
            _deviceClient = deviceClient;
        }

        public HttpClient CreateClient(string name) => name == "FdmsDevice" ? _deviceClient : _publicClient;
    }

    // ═══════════════════════════════════════════════════════════════
    //  PUBLIC endpoints  (no client cert)
    // ═══════════════════════════════════════════════════════════════

    public Task<VerifyTaxpayerInformationResponse> VerifyTaxpayerInformationAsync(int deviceId, VerifyTaxpayerInformationRequest request, CancellationToken ct = default)
        => SendAsync<VerifyTaxpayerInformationResponse>(HttpMethod.Post,
            $"/Public/v1/{deviceId}/VerifyTaxpayerInformation", request, useDeviceClient: false, ct);

    public Task<RegisterDeviceResponse> RegisterDeviceAsync(int deviceId, RegisterDeviceRequest request, CancellationToken ct = default)
        => SendAsync<RegisterDeviceResponse>(HttpMethod.Post,
            $"/Public/v1/{deviceId}/RegisterDevice", request, useDeviceClient: false, ct);

    public Task<GetServerCertificateResponse> GetServerCertificateAsync(string? thumbprint = null, CancellationToken ct = default)
    {
        var url = "/Public/v1/GetServerCertificate";
        if (!string.IsNullOrEmpty(thumbprint))
            url += $"?thumbprint={Uri.EscapeDataString(thumbprint)}";
        return SendAsync<GetServerCertificateResponse>(HttpMethod.Get, url, body: null, useDeviceClient: false, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  DEVICE endpoints  (mTLS client cert required)
    // ═══════════════════════════════════════════════════════════════

    public Task<GetConfigResponse> GetConfigAsync(int deviceId, CancellationToken ct = default)
        => SendAsync<GetConfigResponse>(HttpMethod.Get, $"/Device/v1/{deviceId}/GetConfig", body: null, useDeviceClient: true, ct);

    public Task<GetStatusResponse> GetStatusAsync(int deviceId, CancellationToken ct = default)
        => SendAsync<GetStatusResponse>(HttpMethod.Get, $"/Device/v1/{deviceId}/GetStatus", body: null, useDeviceClient: true, ct);

    public Task<OpenDayResponse> OpenDayAsync(int deviceId, OpenDayRequest request, CancellationToken ct = default)
        => SendAsync<OpenDayResponse>(HttpMethod.Post, $"/Device/v1/{deviceId}/OpenDay", request, useDeviceClient: true, ct);

    public Task<SubmitReceiptResponse> SubmitReceiptAsync(int deviceId, SubmitReceiptRequest request, CancellationToken ct = default)
        => SendAsync<SubmitReceiptResponse>(HttpMethod.Post, $"/Device/v1/{deviceId}/SubmitReceipt", request, useDeviceClient: true, ct);

    public Task<CloseDayResponse> CloseDayAsync(int deviceId, CloseDayRequest request, CancellationToken ct = default)
        => SendAsync<CloseDayResponse>(HttpMethod.Post, $"/Device/v1/{deviceId}/CloseDay", request, useDeviceClient: true, ct);

    public Task<IssueCertificateResponse> IssueCertificateAsync(int deviceId, IssueCertificateRequest request, CancellationToken ct = default)
        => SendAsync<IssueCertificateResponse>(HttpMethod.Post, $"/Device/v1/{deviceId}/IssueCertificate", request, useDeviceClient: true, ct);

    public Task<PingResponse> PingAsync(int deviceId, CancellationToken ct = default)
        => SendAsync<PingResponse>(HttpMethod.Post, $"/Device/v1/{deviceId}/Ping", body: new { }, useDeviceClient: true, ct);

    public async Task<SubmitFileResponse> SubmitFileAsync(int deviceId, byte[] fileContent, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Device/v1/{deviceId}/SubmitFile");
        AddRequiredHeaders(request);
        request.Content = new ByteArrayContent(fileContent);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        var client = _httpFactory.CreateClient("FdmsDevice");
        var response = await client.SendAsync(request, ct);
        return await HandleResponseAsync<SubmitFileResponse>(response, ct);
    }

    public async Task<SubmittedFileListResponse> GetSubmittedFileListAsync(
        int deviceId, DateTime from, DateTime till,
        string? operationId = null, int offset = 0, int limit = 100,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder($"/Device/v1/{deviceId}/SubmittedFileList?");
        sb.Append($"FileUploadedFrom={from:yyyy-MM-ddTHH:mm:ss}");
        sb.Append($"&FileUploadedTill={till:yyyy-MM-ddTHH:mm:ss}");
        sb.Append($"&Offset={offset}&Limit={limit}");
        if (!string.IsNullOrEmpty(operationId))
            sb.Append($"&OperationID={Uri.EscapeDataString(operationId)}");

        return await SendAsync<SubmittedFileListResponse>(HttpMethod.Get, sb.ToString(), body: null, useDeviceClient: true, ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Core send — FIX BUG 9: per-request headers, not DefaultRequestHeaders
    // ═══════════════════════════════════════════════════════════════

    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method, string path, object? body, bool useDeviceClient, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        AddRequiredHeaders(request);

        if (body != null && method != HttpMethod.Get)
        {
            var json = JsonSerializer.Serialize(body, body.GetType(), JsonOpts);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        var clientName = useDeviceClient ? "FdmsDevice" : "FdmsPublic";
        var client = _httpFactory.CreateClient(clientName);
        var response = await client.SendAsync(request, ct);
        return await HandleResponseAsync<TResponse>(response, ct);
    }

    /// <summary>
    /// Adds DeviceModelName and DeviceModelVersion headers per-request (thread-safe).
    /// </summary>
    private void AddRequiredHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("DeviceModelName", _options.DeviceModelName);
        request.Headers.Add("DeviceModelVersion", _options.DeviceModelVersion);
    }

    private async Task<T> HandleResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
            return result ?? throw new FdmsApiException("Empty response body", new InvalidOperationException());
        }

        var rawBody = await response.Content.ReadAsStringAsync(ct);

        ApiProblemDetails? problem = null;
        try { problem = System.Text.Json.JsonSerializer.Deserialize<ApiProblemDetails>(rawBody, JsonOpts); }
        catch { /* non-JSON error body */ }

        if (problem == null)
            problem = new ApiProblemDetails { Title = rawBody, Status = (int)response.StatusCode };

        _logger.LogWarning("FDMS API error {StatusCode} {ErrorCode}: {Title}",
            (int)response.StatusCode, problem.ErrorCode, problem.Title);

        throw new FdmsApiException((int)response.StatusCode, problem);
    }
}
