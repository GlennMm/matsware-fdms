using System.Text.Json.Serialization;
using ZimraFdms.Enums;

namespace ZimraFdms.Models;

// ═══════════════════════════════════════════════════════════════════
//  Common / Shared DTOs
// ═══════════════════════════════════════════════════════════════════

public class AddressDto
{
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Street { get; set; }
    public string? HouseNo { get; set; }
    public string? District { get; set; }
}

public class ContactsDto
{
    public string? PhoneNo { get; set; }
    public string? Email { get; set; }
}

public class SignatureDataDto
{
    /// <summary>SHA-256 hash, base64-encoded bytes.</summary>
    public string Hash { get; set; } = string.Empty;
    /// <summary>Cryptographic signature, base64-encoded bytes.</summary>
    public string Signature { get; set; } = string.Empty;
}

public class SignatureDataEx
{
    public string Hash { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
    /// <summary>SHA-1 thumbprint of FDMS certificate used for signature.</summary>
    public string CertificateThumbprint { get; set; } = string.Empty;
}

public class Tax
{
    public int TaxID { get; set; }
    public double? TaxPercent { get; set; }
    public string TaxName { get; set; } = string.Empty;
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTill { get; set; }
}

public class ApiProblemDetails
{
    public string? Type { get; set; }
    public string? Title { get; set; }
    public int? Status { get; set; }
    public string? Detail { get; set; }
    public string? Instance { get; set; }
    public string? ErrorCode { get; set; }
}

public class ValidationError
{
    public string ValidationErrorCode { get; set; } = string.Empty;
    public string ValidationErrorColor { get; set; } = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════
//  PUBLIC API — /Public/v1/...
// ═══════════════════════════════════════════════════════════════════

// POST /Public/v1/{deviceID}/VerifyTaxpayerInformation
public class VerifyTaxpayerInformationRequest
{
    public string ActivationKey { get; set; } = string.Empty;
    public string DeviceSerialNo { get; set; } = string.Empty;
}

public class VerifyTaxpayerInformationResponse
{
    public string OperationID { get; set; } = string.Empty;
    public string TaxPayerName { get; set; } = string.Empty;
    public string TaxPayerTIN { get; set; } = string.Empty;
    public string? VatNumber { get; set; }
    public string DeviceBranchName { get; set; } = string.Empty;
    public AddressDto DeviceBranchAddress { get; set; } = new();
    public ContactsDto? DeviceBranchContacts { get; set; }
}

// POST /Public/v1/{deviceID}/RegisterDevice
public class RegisterDeviceRequest
{
    public string CertificateRequest { get; set; } = string.Empty;
    public string ActivationKey { get; set; } = string.Empty;
}

public class RegisterDeviceResponse
{
    public string OperationID { get; set; } = string.Empty;
    public string Certificate { get; set; } = string.Empty;
}

// GET /Public/v1/GetServerCertificate
public class GetServerCertificateResponse
{
    public string OperationID { get; set; } = string.Empty;
    public List<string> Certificate { get; set; } = new();
    public DateTime CertificateValidTill { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
//  DEVICE API — /Device/v1/{deviceID}/...  (mTLS required)
// ═══════════════════════════════════════════════════════════════════

// GET /Device/v1/{deviceID}/GetConfig
public class GetConfigResponse
{
    public string OperationID { get; set; } = string.Empty;
    public string TaxPayerName { get; set; } = string.Empty;
    public string TaxPayerTIN { get; set; } = string.Empty;
    public string? VatNumber { get; set; }
    public string DeviceSerialNo { get; set; } = string.Empty;
    public string DeviceBranchName { get; set; } = string.Empty;
    public AddressDto DeviceBranchAddress { get; set; } = new();
    public ContactsDto? DeviceBranchContacts { get; set; }
    public DeviceOperatingMode DeviceOperatingMode { get; set; }
    public int TaxPayerDayMaxHrs { get; set; }
    public int TaxpayerDayEndNotificationHrs { get; set; }
    public List<Tax> ApplicableTaxes { get; set; } = new();
    public DateTime CertificateValidTill { get; set; }
    public string QrUrl { get; set; } = string.Empty;
}

// GET /Device/v1/{deviceID}/GetStatus
public class GetStatusResponse
{
    public string OperationID { get; set; } = string.Empty;
    public FiscalDayStatus FiscalDayStatus { get; set; }
    public FiscalDayReconciliationMode? FiscalDayReconciliationMode { get; set; }
    public SignatureDataEx? FiscalDayServerSignature { get; set; }
    public DateTime? FiscalDayClosed { get; set; }
    public FiscalDayProcessingError? FiscalDayClosingErrorCode { get; set; }
    public List<FiscalDayCounterDto>? FiscalDayCounter { get; set; }
    public List<FiscalDayDocumentQuantity>? FiscalDayDocumentQuantities { get; set; }
    public int? LastReceiptGlobalNo { get; set; }
    public int? LastFiscalDayNo { get; set; }
}

public class FiscalDayDocumentQuantity
{
    public ReceiptType ReceiptType { get; set; }
    public string ReceiptCurrency { get; set; } = string.Empty;
    public int ReceiptQuantity { get; set; }
    public double ReceiptTotalAmount { get; set; }
}

// POST /Device/v1/{deviceID}/OpenDay
public class OpenDayRequest
{
    public int? FiscalDayNo { get; set; }
    public DateTime FiscalDayOpened { get; set; }
}

public class OpenDayResponse
{
    public string OperationID { get; set; } = string.Empty;
    public int FiscalDayNo { get; set; }
}

// POST /Device/v1/{deviceID}/SubmitReceipt
public class SubmitReceiptRequest
{
    public ReceiptDto Receipt { get; set; } = new();
}

public class ReceiptDto
{
    public ReceiptType ReceiptType { get; set; }
    public string ReceiptCurrency { get; set; } = string.Empty;
    public int ReceiptCounter { get; set; }
    public int ReceiptGlobalNo { get; set; }
    public string InvoiceNo { get; set; } = string.Empty;
    public BuyerDto? BuyerData { get; set; }
    public string? ReceiptNotes { get; set; }
    public DateTime ReceiptDate { get; set; }
    public CreditDebitNoteDto? CreditDebitNote { get; set; }
    public bool ReceiptLinesTaxInclusive { get; set; }
    public List<ReceiptLineDto> ReceiptLines { get; set; } = new();
    public List<ReceiptTaxDto> ReceiptTaxes { get; set; } = new();
    public List<PaymentDto> ReceiptPayments { get; set; } = new();
    public double ReceiptTotal { get; set; }
    public ReceiptPrintForm? ReceiptPrintForm { get; set; }
    public SignatureDataDto ReceiptDeviceSignature { get; set; } = new();
    public UserInfoDto? UserInfo { get; set; }
}

public class BuyerDto
{
    public string? BuyerRegisterName { get; set; }
    public string? BuyerTradeName { get; set; }
    public string? BuyerTIN { get; set; }
    public string? VatNumber { get; set; }
    public BuyerContactsDto? BuyerContacts { get; set; }
    public BuyerAddressDto? BuyerAddress { get; set; }
}

public class BuyerAddressDto
{
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? Street { get; set; }
    public string? HouseNo { get; set; }
    public string? District { get; set; }
}

public class BuyerContactsDto
{
    public string? PhoneNo { get; set; }
    public string? Email { get; set; }
}

public class CreditDebitNoteDto
{
    public long? ReceiptID { get; set; }
    public int? DeviceID { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public int? FiscalDayNo { get; set; }
}

public class ReceiptLineDto
{
    public ReceiptLineType ReceiptLineType { get; set; }
    public int ReceiptLineNo { get; set; }
    public string? ReceiptLineHSCode { get; set; }
    public string ReceiptLineName { get; set; } = string.Empty;
    public double? ReceiptLinePrice { get; set; }
    public double ReceiptLineQuantity { get; set; }
    public double ReceiptLineTotal { get; set; }
    public string? TaxCode { get; set; }
    public double? TaxPercent { get; set; }
    public int TaxID { get; set; }
}

public class ReceiptTaxDto
{
    public string? TaxCode { get; set; }
    public double? TaxPercent { get; set; }
    public int TaxID { get; set; }
    public double TaxAmount { get; set; }
    public double SalesAmountWithTax { get; set; }
}

public class PaymentDto
{
    public MoneyType MoneyTypeCode { get; set; }
    public double PaymentAmount { get; set; }
}

public class UserInfoDto
{
    public string Username { get; set; } = string.Empty;
    public string UserFullName { get; set; } = string.Empty;
}

public class SubmitReceiptResponse
{
    public string OperationID { get; set; } = string.Empty;
    public long ReceiptID { get; set; }
    public DateTime ServerDate { get; set; }
    public SignatureDataEx ReceiptServerSignature { get; set; } = new();
    public List<ValidationError>? ValidationErrors { get; set; }
}

// POST /Device/v1/{deviceID}/CloseDay
public class CloseDayRequest
{
    public int FiscalDayNo { get; set; }
    public List<FiscalDayCounterDto> FiscalDayCounters { get; set; } = new();
    public SignatureDataDto FiscalDayDeviceSignature { get; set; } = new();
    public int ReceiptCounter { get; set; }
}

public class FiscalDayCounterDto
{
    public FiscalCounterType FiscalCounterType { get; set; }
    public string FiscalCounterCurrency { get; set; } = string.Empty;
    public double? FiscalCounterTaxPercent { get; set; }
    public int? FiscalCounterTaxID { get; set; }
    public MoneyType? FiscalCounterMoneyType { get; set; }
    public double FiscalCounterValue { get; set; }
}

public class CloseDayResponse
{
    public string OperationID { get; set; } = string.Empty;
}

// POST /Device/v1/{deviceID}/IssueCertificate
public class IssueCertificateRequest
{
    public string CertificateRequest { get; set; } = string.Empty;
}

public class IssueCertificateResponse
{
    public string OperationID { get; set; } = string.Empty;
    public string Certificate { get; set; } = string.Empty;
}

// POST /Device/v1/{deviceID}/Ping
public class PingResponse
{
    public string OperationID { get; set; } = string.Empty;
    public int ReportingFrequency { get; set; }
}

// POST /Device/v1/{deviceID}/SubmitFile  (body is base64/binary)
public class SubmitFileResponse
{
    public string OperationID { get; set; } = string.Empty;
}

// GET /Device/v1/{deviceID}/SubmittedFileList
public class SubmittedFileHeaderDto
{
    public string? FileName { get; set; }
    public DateTime? FileUploadDate { get; set; }
    public int DeviceId { get; set; }
    public int DayNo { get; set; }
    public DateTime FiscalDayOpenedAt { get; set; }
    public int FileSequence { get; set; }
    public DateTime? FileProcessingDate { get; set; }
    public FileProcessingStatus FileProcessingStatus { get; set; }
    public FileProcessingError? FileProcessingError { get; set; }
    public bool HasFooter { get; set; }
    public string? OperationId { get; set; }
    public string? IpAddress { get; set; }
    public List<InvoiceWithValidationError>? InvoiceWithValidationErrors { get; set; }
}

public class InvoiceWithValidationError
{
    public int? ReceiptCounter { get; set; }
    public int? ReceiptGlobalNo { get; set; }
    public List<ValidationError>? ValidationErrors { get; set; }
}

public class SubmittedFileListResponse
{
    public int Total { get; set; }
    public List<SubmittedFileHeaderDto>? Rows { get; set; }
}
