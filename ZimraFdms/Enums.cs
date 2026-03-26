using System.Text.Json.Serialization;

namespace ZimraFdms.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceOperatingMode { Online = 0, Offline = 1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiscalDayStatus { FiscalDayClosed = 0, FiscalDayOpened = 1, FiscalDayCloseInitiated = 2, FiscalDayCloseFailed = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiscalDayReconciliationMode { Auto = 0, Manual = 1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiscalCounterType { SaleByTax = 0, SaleTaxByTax = 1, CreditNoteByTax = 2, CreditNoteTaxByTax = 3, DebitNoteByTax = 4, DebitNoteTaxByTax = 5, BalanceByMoneyType = 6 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MoneyType { Cash = 0, Card = 1, MobileWallet = 2, Coupon = 3, Credit = 4, BankTransfer = 5, Other = 6 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReceiptType { FiscalInvoice = 0, CreditNote = 1, DebitNote = 2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReceiptLineType { Sale = 0, Discount = 1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReceiptPrintForm { Receipt48 = 0, InvoiceA4 = 1 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiscalDayProcessingError { BadCertificateSignature = 0, MissingReceipts = 1, ReceiptsWithValidationErrors = 2, CountersMismatch = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileProcessingStatus { FileProcessingInProgress = 0, FileProcessingIsSuccessful = 1, FileProcessingWithErrors = 2, WaitingForPreviousFile = 3 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileProcessingError { IncorrectFileFormat = 0, FileSentForClosedDay = 1, BadCertificateSignature = 2, MissingReceipts = 3, ReceiptsWithValidationErrors = 4, CountersMismatch = 5, FileExceededAllowedWaitingTime = 6 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UserStatus { Active = 0, Blocked = 1, NotConfirmed = 2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SendSecurityCodeTo { Email = 0, PhoneNumber = 1 }
