using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ZimraFdms.Enums;
using ZimraFdms.Models;

namespace ZimraFdms;

/// <summary>
/// All ZIMRA FDMS cryptographic operations per spec Sections 11–13.
/// </summary>
public class FdmsCryptoService
{
    private readonly FdmsOptions _options;

    public FdmsCryptoService(FdmsOptions options) => _options = options;

    // ═══════════════════════════════════════════════════════════════
    //  Receipt Device Signature  (Section 13.2.1)
    // ═══════════════════════════════════════════════════════════════

    public SignatureDataDto SignReceipt(int deviceId, ReceiptDto receipt, string? previousReceiptHashBase64)
    {
        var data = BuildReceiptSignatureString(deviceId, receipt, previousReceiptHashBase64);
        return Sign(data);
    }

    public static string BuildReceiptSignatureString(int deviceId, ReceiptDto receipt, string? prevHash)
    {
        var sb = new StringBuilder(512);

        sb.Append(deviceId);
        sb.Append(receipt.ReceiptType.ToString().ToUpperInvariant());
        sb.Append(receipt.ReceiptCurrency.ToUpperInvariant());
        sb.Append(receipt.ReceiptGlobalNo);
        sb.Append(receipt.ReceiptDate.ToString("yyyy-MM-ddTHH:mm:ss"));
        sb.Append(ToCents(receipt.ReceiptTotal));

        foreach (var t in receipt.ReceiptTaxes
            .OrderBy(t => t.TaxID)
            .ThenBy(t => t.TaxCode ?? string.Empty, StringComparer.Ordinal))
        {
            sb.Append(t.TaxCode ?? string.Empty);
            sb.Append(FormatTaxPercent(t.TaxPercent));
            sb.Append(ToCents(t.TaxAmount));
            sb.Append(ToCents(t.SalesAmountWithTax));
        }

        if (!string.IsNullOrEmpty(prevHash))
            sb.Append(prevHash);

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Fiscal Day Device Signature  (Section 13.3.1)
    // ═══════════════════════════════════════════════════════════════

    public SignatureDataDto SignFiscalDay(int deviceId, int fiscalDayNo, DateTime fiscalDayDate, List<FiscalDayCounterDto> counters)
    {
        var data = BuildFiscalDaySignatureString(deviceId, fiscalDayNo, fiscalDayDate, counters);
        return Sign(data);
    }

    public static string BuildFiscalDaySignatureString(
        int deviceId, int fiscalDayNo, DateTime fiscalDayDate, List<FiscalDayCounterDto> counters)
    {
        var sb = new StringBuilder(1024);

        sb.Append(deviceId);
        sb.Append(fiscalDayNo);
        sb.Append(fiscalDayDate.ToString("yyyy-MM-dd"));

        foreach (var c in counters
            .Where(c => c.FiscalCounterValue != 0)
            .OrderBy(c => (int)c.FiscalCounterType)
            .ThenBy(c => c.FiscalCounterCurrency, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.FiscalCounterTaxID ?? 0)
            .ThenBy(c => (int?)c.FiscalCounterMoneyType ?? 0))
        {
            sb.Append(c.FiscalCounterType.ToString().ToUpperInvariant());
            sb.Append(c.FiscalCounterCurrency.ToUpperInvariant());

            if (c.FiscalCounterType == FiscalCounterType.BalanceByMoneyType)
                sb.Append(c.FiscalCounterMoneyType?.ToString().ToUpperInvariant() ?? string.Empty);
            else
                sb.Append(FormatTaxPercent(c.FiscalCounterTaxPercent));

            sb.Append(ToCents(c.FiscalCounterValue));
        }

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Core signing — FIX BUG 8: throw if no key configured
    // ═══════════════════════════════════════════════════════════════

    public SignatureDataDto Sign(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        var hash = SHA256.HashData(bytes);

        byte[] sig = _options.UseEcc ? SignEcc(bytes) : SignRsa(bytes);

        return new SignatureDataDto
        {
            Hash = Convert.ToBase64String(hash),
            Signature = Convert.ToBase64String(sig)
        };
    }

    private byte[] SignEcc(byte[] data)
    {
        using var ecdsa = ECDsa.Create();

        if (!string.IsNullOrEmpty(_options.PrivateKeyPemPath) && File.Exists(_options.PrivateKeyPemPath))
            ecdsa.ImportFromPem(File.ReadAllText(_options.PrivateKeyPemPath));
        else if (!string.IsNullOrEmpty(_options.CertificatePfxPath) && File.Exists(_options.CertificatePfxPath))
        {
            using var cert = new X509Certificate2(_options.CertificatePfxPath, _options.CertificatePfxPassword);
            using var key = cert.GetECDsaPrivateKey() ?? throw new InvalidOperationException("PFX contains no ECDSA private key");
            return key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        else
            throw new InvalidOperationException(
                "No signing key available. Set PrivateKeyPemPath or CertificatePfxPath, or call RegisterDeviceAsync first.");

        return ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private byte[] SignRsa(byte[] data)
    {
        using var rsa = RSA.Create();

        if (!string.IsNullOrEmpty(_options.PrivateKeyPemPath) && File.Exists(_options.PrivateKeyPemPath))
            rsa.ImportFromPem(File.ReadAllText(_options.PrivateKeyPemPath));
        else if (!string.IsNullOrEmpty(_options.CertificatePfxPath) && File.Exists(_options.CertificatePfxPath))
        {
            using var cert = new X509Certificate2(_options.CertificatePfxPath, _options.CertificatePfxPassword);
            using var key = cert.GetRSAPrivateKey() ?? throw new InvalidOperationException("PFX contains no RSA private key");
            return key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        else
            throw new InvalidOperationException(
                "No signing key available. Set PrivateKeyPemPath or CertificatePfxPath, or call RegisterDeviceAsync first.");

        return rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  QR Code  (Section 11)
    // ═══════════════════════════════════════════════════════════════

    public static string BuildQrCodeUrl(string qrUrl, int deviceId, DateTime receiptDate, int receiptGlobalNo, string signatureBase64)
    {
        var sigBytes = Convert.FromBase64String(signatureBase64);
        var sigHex = Convert.ToHexString(sigBytes);
        var md5Hex = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sigHex)));
        var qrData = md5Hex[..16].ToUpperInvariant();

        return $"{qrUrl.TrimEnd('/')}/{deviceId:D10}{receiptDate:ddMMyyyy}{receiptGlobalNo:D10}{qrData}";
    }

    public static string FormatVerificationCode(string qrCodeUrl)
    {
        var d = qrCodeUrl[^16..];
        return $"{d[..4]}-{d[4..8]}-{d[8..12]}-{d[12..16]}";
    }

    // ═══════════════════════════════════════════════════════════════
    //  CSR Generation  (Section 12)
    // ═══════════════════════════════════════════════════════════════

    public string GenerateCsrAndSaveKey(string deviceSerialNo, int deviceId, string privateKeyOutputPath)
    {
        var cn = $"ZIMRA-{deviceSerialNo}-{deviceId:D10}";
        var dn = new X500DistinguishedName($"CN={cn}, O=Zimbabwe Revenue Authority, S=Zimbabwe, C=ZW");

        byte[] csrDer;

        if (_options.UseEcc)
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(privateKeyOutputPath, ecdsa.ExportECPrivateKeyPem());
            var req = new CertificateRequest(dn, ecdsa, HashAlgorithmName.SHA256);
            csrDer = req.CreateSigningRequest();
        }
        else
        {
            using var rsa = RSA.Create(2048);
            File.WriteAllText(privateKeyOutputPath, rsa.ExportRSAPrivateKeyPem());
            var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            csrDer = req.CreateSigningRequest();
        }

        var pem = new StringBuilder();
        pem.AppendLine("-----BEGIN CERTIFICATE REQUEST-----");
        pem.AppendLine(Convert.ToBase64String(csrDer, Base64FormattingOptions.InsertLineBreaks));
        pem.AppendLine("-----END CERTIFICATE REQUEST-----");
        return pem.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════

    private static long ToCents(double amount) => (long)Math.Round(amount * 100, MidpointRounding.AwayFromZero);

    private static string FormatTaxPercent(double? pct) =>
        pct.HasValue ? pct.Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
}
