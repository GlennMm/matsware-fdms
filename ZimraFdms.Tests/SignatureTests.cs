// ═══════════════════════════════════════════════════════════════════════════
//  Unit Tests — validates crypto against every example in FDMS spec v7.2 §13.
//
//  Setup:
//    dotnet new xunit -n ZimraFdms.Tests
//    cd ZimraFdms.Tests
//    dotnet add reference ../ZimraFdms/ZimraFdms.csproj
//    # paste this file as SignatureTests.cs
//    dotnet test
// ═══════════════════════════════════════════════════════════════════════════

using System.Security.Cryptography;
using System.Text;
using ZimraFdms;
using ZimraFdms.Enums;
using ZimraFdms.Models;

namespace ZimraFdms.Tests;

// ─── Receipt Signature String + Hash  (Section 13.2.1) ──────────────────

public class ReceiptSignatureTests
{
    // ── FiscalInvoice Example 1 ──

    [Fact]
    public void FiscalInvoice_Ex1_Concatenation()
    {
        var receipt = MakeInvoiceEx1();
        var result = FdmsCryptoService.BuildReceiptSignatureString(321, receipt, PrevHash);

        Assert.Equal(
            "321FISCALINVOICEZWL4322019-09-19T15:43:12945000"
            + "A0250000B0.000350000C15.0015000115000D15.0030000230000"
            + PrevHash,
            result);
    }

    [Fact]
    public void FiscalInvoice_Ex1_Hash()
    {
        var concat = FdmsCryptoService.BuildReceiptSignatureString(321, MakeInvoiceEx1(), PrevHash);
        Assert.Equal("zDxEalWUpwX2BcsYxRUAEfY/13OaCrTwDt01So3a6uU=", Sha256B64(concat));
    }

    // ── FiscalInvoice Example 2 ──

    [Fact]
    public void FiscalInvoice_Ex2_Concatenation()
    {
        var receipt = MakeInvoiceEx2();
        var result = FdmsCryptoService.BuildReceiptSignatureString(322, receipt, PrevHash);

        Assert.Equal(
            "322FISCALINVOICEUSD852019-09-19T09:23:07"
            + "4035"
            + "07000.000100014.50535"
            + PrevHash,
            result);
    }

    [Fact]
    public void FiscalInvoice_Ex2_Hash()
    {
        var concat = FdmsCryptoService.BuildReceiptSignatureString(322, MakeInvoiceEx2(), PrevHash);
        Assert.Equal("2zInR7ciOQ9PbtQlKaU5XoktQ/4/y1XShfzEEoSVO7s=", Sha256B64(concat));
    }

    // ── CreditNote Example 1 ──

    [Fact]
    public void CreditNote_Ex1_Concatenation()
    {
        var receipt = MakeCreditNoteEx1();
        var result = FdmsCryptoService.BuildReceiptSignatureString(321, receipt, PrevHash);

        Assert.Equal(
            "321CREDITNOTEZWL4322020-09-19T15:43:12-945000"
            + "A0-250000B0.000-350000C15.00-15000-115000D15.00-30000-230000"
            + PrevHash,
            result);
    }

    [Fact]
    public void CreditNote_Ex1_Hash()
    {
        var concat = FdmsCryptoService.BuildReceiptSignatureString(321, MakeCreditNoteEx1(), PrevHash);
        Assert.Equal("Wu21g3N0fPIa67pnAp+FZkaEfBiv696B+4QoJCWRIcY=", Sha256B64(concat));
    }

    // ── CreditNote Example 2 ──

    [Fact]
    public void CreditNote_Ex2_Hash()
    {
        var receipt = new ReceiptDto
        {
            ReceiptType = ReceiptType.CreditNote,
            ReceiptCurrency = "USD",
            ReceiptGlobalNo = 85,
            ReceiptDate = new DateTime(2020, 9, 19, 9, 23, 7),
            ReceiptTotal = -40.35,
            ReceiptTaxes = new()
            {
                new() { TaxID = 1, TaxPercent = null, TaxAmount = 0, SalesAmountWithTax = -7 },
                new() { TaxID = 2, TaxPercent = 0, TaxAmount = 0, SalesAmountWithTax = -10 },
                new() { TaxID = 3, TaxPercent = 14.5, TaxAmount = -3, SalesAmountWithTax = -23 }
            }
        };
        var concat = FdmsCryptoService.BuildReceiptSignatureString(322, receipt, PrevHash);
        Assert.Equal("F9/QB0vhxQlEF2nk+oebwP8V+qBcNlOFvoTeE/1QxPc=", Sha256B64(concat));
    }

    // ── DebitNote Example 1 ──

    [Fact]
    public void DebitNote_Ex1_Hash()
    {
        var receipt = new ReceiptDto
        {
            ReceiptType = ReceiptType.DebitNote,
            ReceiptCurrency = "ZWL",
            ReceiptGlobalNo = 432,
            ReceiptDate = new DateTime(2020, 9, 19, 15, 43, 12),
            ReceiptTotal = 9450,
            ReceiptTaxes = new()
            {
                new() { TaxID = 1, TaxCode = "A", TaxPercent = null, TaxAmount = 0, SalesAmountWithTax = 2500 },
                new() { TaxID = 2, TaxCode = "B", TaxPercent = 0, TaxAmount = 0, SalesAmountWithTax = 3500 },
                new() { TaxID = 3, TaxCode = "C", TaxPercent = 15, TaxAmount = 150, SalesAmountWithTax = 1150 },
                new() { TaxID = 3, TaxCode = "D", TaxPercent = 15, TaxAmount = 300, SalesAmountWithTax = 2300 }
            }
        };
        var concat = FdmsCryptoService.BuildReceiptSignatureString(321, receipt, PrevHash);
        Assert.Equal("PHcormpq5Ppb/6Quh8iOY3bDq4B4cPW5hsENb65iK/I=", Sha256B64(concat));
    }

    // ── DebitNote Example 2 ──

    [Fact]
    public void DebitNote_Ex2_Hash()
    {
        var receipt = new ReceiptDto
        {
            ReceiptType = ReceiptType.DebitNote,
            ReceiptCurrency = "USD",
            ReceiptGlobalNo = 85,
            ReceiptDate = new DateTime(2020, 9, 19, 9, 23, 7),
            ReceiptTotal = 40.35,
            ReceiptTaxes = new()
            {
                new() { TaxID = 1, TaxPercent = null, TaxAmount = 0, SalesAmountWithTax = 7 },
                new() { TaxID = 2, TaxPercent = 0, TaxAmount = 0, SalesAmountWithTax = 10 },
                new() { TaxID = 3, TaxPercent = 14.5, TaxAmount = 3, SalesAmountWithTax = 23 }
            }
        };
        var concat = FdmsCryptoService.BuildReceiptSignatureString(322, receipt, PrevHash);
        Assert.Equal("YOLYzYhCaaLN2yxrM574B83WUhxSkg52uc1hrM4g8Dw=", Sha256B64(concat));
    }

    // ── First receipt in fiscal day (no previous hash) ──

    [Fact]
    public void FirstReceipt_NoPreviousHash()
    {
        var receipt = MakeInvoiceEx1();
        var result = FdmsCryptoService.BuildReceiptSignatureString(321, receipt, null);

        // Should NOT contain the previous hash
        Assert.DoesNotContain("hNVJXP", result);
        Assert.EndsWith("230000", result); // ends at last tax field
    }

    // ── Helpers ──

    private const string PrevHash = "hNVJXP/ACOiE8McD3pKsDlqBXpuaUqQOfPnMyfZWI9k=";

    private static ReceiptDto MakeInvoiceEx1() => new()
    {
        ReceiptType = ReceiptType.FiscalInvoice,
        ReceiptCurrency = "ZWL",
        ReceiptGlobalNo = 432,
        ReceiptDate = new DateTime(2019, 9, 19, 15, 43, 12),
        ReceiptTotal = 9450.00,
        ReceiptTaxes = new()
        {
            new() { TaxID = 1, TaxCode = "A", TaxPercent = null, TaxAmount = 0, SalesAmountWithTax = 2500 },
            new() { TaxID = 2, TaxCode = "B", TaxPercent = 0, TaxAmount = 0, SalesAmountWithTax = 3500 },
            new() { TaxID = 3, TaxCode = "C", TaxPercent = 15, TaxAmount = 150, SalesAmountWithTax = 1150 },
            new() { TaxID = 3, TaxCode = "D", TaxPercent = 15, TaxAmount = 300, SalesAmountWithTax = 2300 }
        }
    };

    private static ReceiptDto MakeInvoiceEx2() => new()
    {
        ReceiptType = ReceiptType.FiscalInvoice,
        ReceiptCurrency = "USD",
        ReceiptGlobalNo = 85,
        ReceiptDate = new DateTime(2019, 9, 19, 9, 23, 7),
        ReceiptTotal = 40.35,
        ReceiptTaxes = new()
        {
            new() { TaxID = 1, TaxPercent = null, TaxAmount = 0, SalesAmountWithTax = 7 },
            new() { TaxID = 2, TaxPercent = 0, TaxAmount = 0, SalesAmountWithTax = 10 },
            new() { TaxID = 3, TaxPercent = 14.5, TaxAmount = 0.05, SalesAmountWithTax = 0.35 }
        }
    };

    private static ReceiptDto MakeCreditNoteEx1() => new()
    {
        ReceiptType = ReceiptType.CreditNote,
        ReceiptCurrency = "ZWL",
        ReceiptGlobalNo = 432,
        ReceiptDate = new DateTime(2020, 9, 19, 15, 43, 12),
        ReceiptTotal = -9450.00,
        ReceiptTaxes = new()
        {
            new() { TaxID = 1, TaxCode = "A", TaxPercent = null, TaxAmount = 0, SalesAmountWithTax = -2500 },
            new() { TaxID = 2, TaxCode = "B", TaxPercent = 0, TaxAmount = 0, SalesAmountWithTax = -3500 },
            new() { TaxID = 3, TaxCode = "C", TaxPercent = 15, TaxAmount = -150, SalesAmountWithTax = -1150 },
            new() { TaxID = 3, TaxCode = "D", TaxPercent = 15, TaxAmount = -300, SalesAmountWithTax = -2300 }
        }
    };

    private static string Sha256B64(string input)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}

// ─── Fiscal Day Signature  (Section 13.3.1) ─────────────────────────────

public class FiscalDaySignatureTests
{
    [Fact]
    public void FiscalDay_Concatenation()
    {
        var result = FdmsCryptoService.BuildFiscalDaySignatureString(321, 84, new DateTime(2019, 9, 23), SpecCounters());

        Assert.Equal(
            "321842019-09-23"
            + "SALEBYTAXZWL2300000"
            + "SALEBYTAXZWL0.001200000"
            + "SALEBYTAXUSD14.502500"
            + "SALEBYTAXZWL15.001200"
            + "SALETAXBYTAXUSD15.00250"
            + "SALETAXBYTAXZWL15.00230000"
            + "BALANCEBYMONEYTYPEUSDCASH3700"
            + "BALANCEBYMONEYTYPEZWLCARD1500000"
            + "BALANCEBYMONEYTYPEZWLCASH2000000",
            result);
    }

    [Fact]
    public void FiscalDay_Hash()
    {
        var concat = FdmsCryptoService.BuildFiscalDaySignatureString(321, 84, new DateTime(2019, 9, 23), SpecCounters());
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(concat)));
        Assert.Equal("OdT8lLI0JXhXl1XQgr64Zb1ltFDksFXThVxqM6O8xZE=", hash);
    }

    [Fact]
    public void ZeroCounters_Excluded()
    {
        var counters = new List<FiscalDayCounterDto>
        {
            new() { FiscalCounterType = FiscalCounterType.SaleByTax, FiscalCounterCurrency = "USD", FiscalCounterTaxPercent = 15, FiscalCounterValue = 100 },
            new() { FiscalCounterType = FiscalCounterType.SaleTaxByTax, FiscalCounterCurrency = "USD", FiscalCounterTaxPercent = 15, FiscalCounterValue = 0 },
        };
        var result = FdmsCryptoService.BuildFiscalDaySignatureString(1, 1, new DateTime(2024, 1, 1), counters);
        Assert.Contains("SALEBYTAX", result);
        Assert.DoesNotContain("SALETAXBYTAX", result);
    }

    private static List<FiscalDayCounterDto> SpecCounters() => new()
    {
        new() { FiscalCounterType = FiscalCounterType.SaleByTax, FiscalCounterCurrency = "ZWL", FiscalCounterTaxPercent = null, FiscalCounterValue = 23000 },
        new() { FiscalCounterType = FiscalCounterType.SaleByTax, FiscalCounterCurrency = "ZWL", FiscalCounterTaxPercent = 0, FiscalCounterValue = 12000 },
        new() { FiscalCounterType = FiscalCounterType.SaleByTax, FiscalCounterCurrency = "USD", FiscalCounterTaxPercent = 14.5, FiscalCounterValue = 25 },
        new() { FiscalCounterType = FiscalCounterType.SaleByTax, FiscalCounterCurrency = "ZWL", FiscalCounterTaxPercent = 15, FiscalCounterValue = 12 },
        new() { FiscalCounterType = FiscalCounterType.SaleTaxByTax, FiscalCounterCurrency = "USD", FiscalCounterTaxPercent = 15, FiscalCounterValue = 2.50 },
        new() { FiscalCounterType = FiscalCounterType.SaleTaxByTax, FiscalCounterCurrency = "ZWL", FiscalCounterTaxPercent = 15, FiscalCounterValue = 2300 },
        new() { FiscalCounterType = FiscalCounterType.BalanceByMoneyType, FiscalCounterCurrency = "ZWL", FiscalCounterMoneyType = MoneyType.Card, FiscalCounterValue = 15000 },
        new() { FiscalCounterType = FiscalCounterType.BalanceByMoneyType, FiscalCounterCurrency = "USD", FiscalCounterMoneyType = MoneyType.Cash, FiscalCounterValue = 37 },
        new() { FiscalCounterType = FiscalCounterType.BalanceByMoneyType, FiscalCounterCurrency = "ZWL", FiscalCounterMoneyType = MoneyType.Cash, FiscalCounterValue = 20000 },
    };
}

// ─── QR Code (Section 11) ───────────────────────────────────────────────

public class QrCodeTests
{
    [Fact]
    public void QrUrl_Structure()
    {
        var url = FdmsCryptoService.BuildQrCodeUrl(
            "https://invoice.zimra.co.zw", 321,
            new DateTime(2023, 4, 3), 1112223331,
            Convert.ToBase64String(new byte[64]));

        Assert.StartsWith("https://invoice.zimra.co.zw/", url);
        // deviceID(10) + date(8) + globalNo(10) + qrData(16) = 44 chars after /
        var path = url.Split('/').Last();
        Assert.Equal(44, path.Length);
    }

    [Fact]
    public void VerificationCode_Format()
    {
        var url = "https://invoice.zimra.co.zw/00000003210304202311122233314C8BE27663330417";
        Assert.Equal("4C8B-E276-6333-0417", FdmsCryptoService.FormatVerificationCode(url));
    }
}

// ─── Fiscal Counter Tracker ─────────────────────────────────────────────

public class FiscalCounterTrackerTests
{
    [Fact]
    public void Invoice_ProducesCorrectCounters()
    {
        var tracker = new FiscalCounterTracker();
        tracker.AccumulateReceipt(new ReceiptDto
        {
            ReceiptType = ReceiptType.FiscalInvoice,
            ReceiptCurrency = "USD",
            ReceiptCounter = 1,
            ReceiptTaxes = new() { new() { TaxID = 1, TaxPercent = 15, TaxAmount = 13.04, SalesAmountWithTax = 100 } },
            ReceiptPayments = new() { new() { MoneyTypeCode = MoneyType.Cash, PaymentAmount = 100 } }
        }, "hash1");

        var c = tracker.GetCountersForClose();
        Assert.Contains(c, x => x.FiscalCounterType == FiscalCounterType.SaleByTax && x.FiscalCounterValue == 100);
        Assert.Contains(c, x => x.FiscalCounterType == FiscalCounterType.SaleTaxByTax && Math.Abs(x.FiscalCounterValue - 13.04) < 0.01);
        Assert.Contains(c, x => x.FiscalCounterType == FiscalCounterType.BalanceByMoneyType && x.FiscalCounterValue == 100);
    }

    [Fact]
    public void CreditNote_DecreasesBalance()
    {
        var tracker = new FiscalCounterTracker();
        tracker.AccumulateReceipt(new ReceiptDto
        {
            ReceiptType = ReceiptType.FiscalInvoice, ReceiptCurrency = "USD", ReceiptCounter = 1,
            ReceiptTaxes = new() { new() { TaxID = 1, TaxPercent = 15, TaxAmount = 13, SalesAmountWithTax = 100 } },
            ReceiptPayments = new() { new() { MoneyTypeCode = MoneyType.Cash, PaymentAmount = 100 } }
        }, "h1");
        tracker.AccumulateReceipt(new ReceiptDto
        {
            ReceiptType = ReceiptType.CreditNote, ReceiptCurrency = "USD", ReceiptCounter = 2,
            ReceiptTaxes = new() { new() { TaxID = 1, TaxPercent = 15, TaxAmount = -6.5, SalesAmountWithTax = -50 } },
            ReceiptPayments = new() { new() { MoneyTypeCode = MoneyType.Cash, PaymentAmount = -50 } }
        }, "h2");

        var balance = tracker.GetCountersForClose().First(c => c.FiscalCounterType == FiscalCounterType.BalanceByMoneyType);
        Assert.Equal(50, balance.FiscalCounterValue);
    }

    [Fact]
    public void Reset_ClearsAll()
    {
        var tracker = new FiscalCounterTracker();
        tracker.AccumulateReceipt(new ReceiptDto
        {
            ReceiptType = ReceiptType.FiscalInvoice, ReceiptCurrency = "USD", ReceiptCounter = 5,
            ReceiptTaxes = new() { new() { TaxID = 1, TaxPercent = 15, TaxAmount = 10, SalesAmountWithTax = 100 } },
            ReceiptPayments = new() { new() { MoneyTypeCode = MoneyType.Cash, PaymentAmount = 100 } }
        }, "h");

        tracker.Reset();
        Assert.Empty(tracker.GetCountersForClose());
        Assert.Equal(0, tracker.ReceiptCounter);
        Assert.Null(tracker.LastReceiptHashBase64);
    }
}
