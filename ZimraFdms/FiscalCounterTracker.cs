using System.Globalization;
using ZimraFdms.Enums;
using ZimraFdms.Models;

namespace ZimraFdms;

/// <summary>
/// Accumulates fiscal day counters as receipts are issued.
/// Reset when a new fiscal day opens. Implements Section 6 rules.
/// Thread-safe via locking.
/// </summary>
public class FiscalCounterTracker
{
    private readonly object _lock = new();
    private readonly Dictionary<CounterKey, double> _counters = new();
    private int _receiptCounter;
    private string? _lastReceiptHashBase64;

    public int ReceiptCounter { get { lock (_lock) return _receiptCounter; } }
    public string? LastReceiptHashBase64 { get { lock (_lock) return _lastReceiptHashBase64; } }

    public void Reset()
    {
        lock (_lock)
        {
            _counters.Clear();
            _receiptCounter = 0;
            _lastReceiptHashBase64 = null;
        }
    }

    public void AccumulateReceipt(ReceiptDto receipt, string receiptHashBase64)
    {
        lock (_lock)
        {
            _receiptCounter = receipt.ReceiptCounter;
            _lastReceiptHashBase64 = receiptHashBase64;

            var cur = receipt.ReceiptCurrency.ToUpperInvariant();

            switch (receipt.ReceiptType)
            {
                case ReceiptType.FiscalInvoice:
                    foreach (var t in receipt.ReceiptTaxes)
                    {
                        Add(FiscalCounterType.SaleByTax, cur, t.TaxID, t.TaxPercent, null, t.SalesAmountWithTax);
                        Add(FiscalCounterType.SaleTaxByTax, cur, t.TaxID, t.TaxPercent, null, t.TaxAmount);
                    }
                    foreach (var p in receipt.ReceiptPayments)
                        Add(FiscalCounterType.BalanceByMoneyType, cur, null, null, p.MoneyTypeCode, p.PaymentAmount);
                    break;

                case ReceiptType.CreditNote:
                    foreach (var t in receipt.ReceiptTaxes)
                    {
                        Add(FiscalCounterType.CreditNoteByTax, cur, t.TaxID, t.TaxPercent, null, t.SalesAmountWithTax);
                        Add(FiscalCounterType.CreditNoteTaxByTax, cur, t.TaxID, t.TaxPercent, null, t.TaxAmount);
                    }
                    foreach (var p in receipt.ReceiptPayments)
                        Add(FiscalCounterType.BalanceByMoneyType, cur, null, null, p.MoneyTypeCode, p.PaymentAmount);
                    break;

                case ReceiptType.DebitNote:
                    foreach (var t in receipt.ReceiptTaxes)
                    {
                        Add(FiscalCounterType.DebitNoteByTax, cur, t.TaxID, t.TaxPercent, null, t.SalesAmountWithTax);
                        Add(FiscalCounterType.DebitNoteTaxByTax, cur, t.TaxID, t.TaxPercent, null, t.TaxAmount);
                    }
                    foreach (var p in receipt.ReceiptPayments)
                        Add(FiscalCounterType.BalanceByMoneyType, cur, null, null, p.MoneyTypeCode, p.PaymentAmount);
                    break;
            }
        }
    }

    /// <summary>Returns non-zero counters ready for closeDay submission.</summary>
    public List<FiscalDayCounterDto> GetCountersForClose()
    {
        lock (_lock)
        {
            return _counters
                .Where(kvp => Math.Abs(kvp.Value) > 0.001)
                .Select(kvp => new FiscalDayCounterDto
                {
                    FiscalCounterType = kvp.Key.Type,
                    FiscalCounterCurrency = kvp.Key.Currency,
                    FiscalCounterTaxID = kvp.Key.TaxId,
                    FiscalCounterTaxPercent = kvp.Key.TaxPercent,
                    FiscalCounterMoneyType = kvp.Key.MoneyType,
                    FiscalCounterValue = kvp.Value
                })
                .ToList();
        }
    }

    private void Add(FiscalCounterType type, string currency, int? taxId, double? taxPct, MoneyType? moneyType, double value)
    {
        var key = new CounterKey(type, currency, taxId, taxPct, moneyType);
        _counters.TryGetValue(key, out var existing);
        _counters[key] = existing + value;
    }

    /// <summary>
    /// Structured key — no string parsing needed. FIX BUG 7.
    /// </summary>
    private readonly record struct CounterKey(
        FiscalCounterType Type,
        string Currency,
        int? TaxId,
        double? TaxPercent,
        MoneyType? MoneyType);
}
