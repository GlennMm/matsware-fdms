using ZimraFdms;
using ZimraFdms.Enums;
using ZimraFdms.Models;

var builder = WebApplication.CreateBuilder(args);

// ── DI: FDMS services ──
builder.Services.AddZimraFdms(builder.Configuration.GetSection("ZimraFdms"));

// ── Swagger ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "ZIMRA FDMS Test API", Version = "v1" });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// ── Global error handler: surface FDMS error details ──
app.UseExceptionHandler(err => err.Run(async context =>
{
    var ex = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.ContentType = "application/json";

    if (ex is FdmsApiException fdmsEx)
    {
        context.Response.StatusCode = fdmsEx.HttpStatusCode > 0 ? fdmsEx.HttpStatusCode : 500;
        await context.Response.WriteAsJsonAsync(new
        {
            Error = fdmsEx.Message,
            fdmsEx.ErrorCode,
            fdmsEx.HttpStatusCode,
            ProblemDetails = fdmsEx.ProblemDetails
        });
    }
    else if (ex is InvalidOperationException invEx)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { Error = invEx.Message });
    }
    else
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { Error = ex?.Message });
    }
}));

// ── Auto-initialize on startup (if already registered) ──
using (var scope = app.Services.CreateScope())
{
    var fdms = scope.ServiceProvider.GetRequiredService<FdmsService>();
    try
    {
        await fdms.InitializeAsync();
        app.Logger.LogInformation("FDMS initialized successfully");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "FDMS initialization skipped (device may not be registered yet)");
    }
}

// ═══════════════════════════════════════════════════════════════
//  Registration endpoints (one-time)
// ═══════════════════════════════════════════════════════════════

app.MapPost("/api/fdms/verify-taxpayer", async (FdmsService fdms) =>
{
    var result = await fdms.VerifyTaxpayerAsync();
    return Results.Ok(result);
})
.WithName("VerifyTaxpayer")
.WithTags("Registration");

app.MapPost("/api/fdms/register", async (FdmsService fdms) =>
{
    var certsDir = Path.Combine(Directory.GetCurrentDirectory(), "certs");
    Directory.CreateDirectory(certsDir);

    var result = await fdms.RegisterDeviceAsync(
        privateKeyOutputPath: Path.Combine(certsDir, "device-private.pem"),
        certificateOutputPath: Path.Combine(certsDir, "device-cert.pem"));

    return Results.Ok(new
    {
        result.OperationID,
        Message = "Device registered. Certificate saved to certs/. Restart the API to load the certificate for mTLS.",
        CertificatePath = "certs/device-cert.pem",
        PrivateKeyPath = "certs/device-private.pem"
    });
})
.WithName("RegisterDevice")
.WithTags("Registration");

// ═══════════════════════════════════════════════════════════════
//  Config & Status
// ═══════════════════════════════════════════════════════════════

app.MapGet("/api/fdms/config", async (FdmsService fdms) =>
{
    var config = await fdms.GetConfigAsync();
    return Results.Ok(config);
})
.WithName("GetConfig")
.WithTags("Config");

app.MapGet("/api/fdms/status", async (FdmsService fdms) =>
{
    var status = await fdms.GetStatusAsync();
    return Results.Ok(status);
})
.WithName("GetStatus")
.WithTags("Config");

// ═══════════════════════════════════════════════════════════════
//  Fiscal Day Lifecycle
// ═══════════════════════════════════════════════════════════════

app.MapPost("/api/fdms/open-day", async (FdmsService fdms) =>
{
    var (response, isResumed) = await fdms.OpenDayAsync();
    return Results.Ok(new
    {
        response.FiscalDayNo,
        IsResumed = isResumed,
        Message = isResumed ? "Resumed existing fiscal day" : "Opened new fiscal day"
    });
})
.WithName("OpenDay")
.WithTags("Fiscal Day");

app.MapPost("/api/fdms/close-day", async (FdmsService fdms) =>
{
    var result = await fdms.CloseDayAsync();
    return Results.Ok(new
    {
        result.OperationID,
        SubmittedCount = fdms.SubmittedCount,
        FailedCount = fdms.FailedCount,
        Message = "Fiscal day closed"
    });
})
.WithName("CloseDay")
.WithTags("Fiscal Day");

// ═══════════════════════════════════════════════════════════════
//  Receipts
// ═══════════════════════════════════════════════════════════════

app.MapPost("/api/fdms/receipts", async (ReceiptDto receipt, FdmsService fdms) =>
{
    var enqueued = await fdms.EnqueueReceiptAsync(receipt);
    return Results.Ok(new
    {
        ReceiptGlobalNo = enqueued.Receipt.ReceiptGlobalNo,
        ReceiptCounter = enqueued.Receipt.ReceiptCounter,
        enqueued.QrCodeUrl,
        enqueued.VerificationCode,
        Pending = fdms.PendingCount
    });
})
.WithName("SubmitReceipt")
.WithTags("Receipts");

app.MapPost("/api/fdms/receipts/sample-invoice", async (FdmsService fdms) =>
{
    var config = fdms.Config ?? await fdms.GetConfigAsync();
    // Pick the standard VAT rate (highest non-null percent)
    var stdTax = config.ApplicableTaxes
        .Where(t => t.TaxPercent.HasValue && t.TaxPercent > 0)
        .OrderByDescending(t => t.TaxPercent)
        .First();
    var taxId = stdTax.TaxID;
    var taxPct = stdTax.TaxPercent!.Value;

    // Line totals (tax-inclusive)
    double line1Total = 50.00; // 2 x $25
    double line2Total = 15.00; // 10 x $1.50
    double receiptTotal = line1Total + line2Total; // $65.00
    double taxAmount = Math.Round(receiptTotal * taxPct / (100 + taxPct), 2);

    var invoice = new ReceiptDto
    {
        ReceiptType = ReceiptType.FiscalInvoice,
        ReceiptCurrency = "USD",
        InvoiceNo = $"INV-{DateTime.Now:yyyyMMddHHmmss}",
        ReceiptDate = DateTime.Now,
        ReceiptLinesTaxInclusive = true,
        ReceiptLines = new List<ReceiptLineDto>
        {
            new()
            {
                ReceiptLineType = ReceiptLineType.Sale,
                ReceiptLineNo = 1,
                ReceiptLineHSCode = "64039190",
                ReceiptLineName = "School Shoes - Size 6",
                ReceiptLinePrice = 25.00,
                ReceiptLineQuantity = 2,
                ReceiptLineTotal = line1Total,
                TaxPercent = taxPct, TaxID = taxId
            },
            new()
            {
                ReceiptLineType = ReceiptLineType.Sale,
                ReceiptLineNo = 2,
                ReceiptLineHSCode = "48201000",
                ReceiptLineName = "Exercise Book 96-page (x10)",
                ReceiptLinePrice = 1.50,
                ReceiptLineQuantity = 10,
                ReceiptLineTotal = line2Total,
                TaxPercent = taxPct, TaxID = taxId
            }
        },
        ReceiptTaxes = new List<ReceiptTaxDto>
        {
            new()
            {
                TaxPercent = taxPct, TaxID = taxId,
                TaxAmount = taxAmount,
                SalesAmountWithTax = receiptTotal
            }
        },
        ReceiptPayments = new List<PaymentDto>
        {
            new() { MoneyTypeCode = MoneyType.Cash, PaymentAmount = receiptTotal }
        },
        ReceiptTotal = receiptTotal,
        ReceiptPrintForm = ReceiptPrintForm.Receipt48,
        BuyerData = new BuyerDto
        {
            BuyerRegisterName = "John Moyo",
            BuyerTIN = "1234567890"
        }
    };

    var enqueued = await fdms.EnqueueReceiptAsync(invoice);

    // Wait for FDMS confirmation
    SubmitReceiptResponse? fdmsResponse = null;
    try
    {
        fdmsResponse = await enqueued.SubmissionTask.WaitAsync(TimeSpan.FromSeconds(30));
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            enqueued.Receipt.ReceiptGlobalNo,
            enqueued.Receipt.ReceiptCounter,
            enqueued.QrCodeUrl,
            enqueued.VerificationCode,
            FdmsStatus = $"Submission pending/failed: {ex.Message}"
        });
    }

    return Results.Ok(new
    {
        enqueued.Receipt.ReceiptGlobalNo,
        enqueued.Receipt.ReceiptCounter,
        enqueued.QrCodeUrl,
        enqueued.VerificationCode,
        FdmsReceiptID = fdmsResponse?.ReceiptID,
        FdmsServerDate = fdmsResponse?.ServerDate,
        ValidationErrors = fdmsResponse?.ValidationErrors
    });
})
.WithName("SampleInvoice")
.WithTags("Receipts");

app.MapPost("/api/fdms/receipts/sample-credit-note", async (int originalReceiptGlobalNo, int originalFiscalDayNo, FdmsService fdms) =>
{
    var config = fdms.Config ?? await fdms.GetConfigAsync();
    var stdTax = config.ApplicableTaxes
        .Where(t => t.TaxPercent.HasValue && t.TaxPercent > 0)
        .OrderByDescending(t => t.TaxPercent)
        .First();
    var taxId = stdTax.TaxID;
    var taxPct = stdTax.TaxPercent!.Value;
    var deviceId = int.Parse(config.DeviceSerialNo.Length > 0 ? "21058" : "0"); // from config

    double lineTotal = -25.00;
    double taxAmount = Math.Round(lineTotal * taxPct / (100 + taxPct), 2);

    var creditNote = new ReceiptDto
    {
        ReceiptType = ReceiptType.CreditNote,
        ReceiptCurrency = "USD",
        InvoiceNo = $"CN-{DateTime.Now:yyyyMMddHHmmss}",
        ReceiptDate = DateTime.Now,
        ReceiptNotes = "Returned 1x School Shoes - wrong size",
        ReceiptLinesTaxInclusive = true,
        CreditDebitNote = new CreditDebitNoteDto
        {
            DeviceID = 21058,
            ReceiptGlobalNo = originalReceiptGlobalNo,
            FiscalDayNo = originalFiscalDayNo
        },
        ReceiptLines = new List<ReceiptLineDto>
        {
            new()
            {
                ReceiptLineType = ReceiptLineType.Sale,
                ReceiptLineNo = 1,
                ReceiptLineHSCode = "64039190",
                ReceiptLineName = "School Shoes - Size 6",
                ReceiptLinePrice = -25.00,
                ReceiptLineQuantity = 1,
                ReceiptLineTotal = lineTotal,
                TaxPercent = taxPct, TaxID = taxId
            }
        },
        ReceiptTaxes = new List<ReceiptTaxDto>
        {
            new()
            {
                TaxPercent = taxPct, TaxID = taxId,
                TaxAmount = taxAmount,
                SalesAmountWithTax = lineTotal
            }
        },
        ReceiptPayments = new List<PaymentDto>
        {
            new() { MoneyTypeCode = MoneyType.Cash, PaymentAmount = lineTotal }
        },
        ReceiptTotal = lineTotal,
        ReceiptPrintForm = ReceiptPrintForm.Receipt48
    };

    var enqueued = await fdms.EnqueueReceiptAsync(creditNote);

    SubmitReceiptResponse? fdmsResponse = null;
    try
    {
        fdmsResponse = await enqueued.SubmissionTask.WaitAsync(TimeSpan.FromSeconds(30));
    }
    catch (Exception ex)
    {
        return Results.Ok(new
        {
            enqueued.Receipt.ReceiptGlobalNo,
            enqueued.Receipt.ReceiptCounter,
            enqueued.QrCodeUrl,
            enqueued.VerificationCode,
            FdmsStatus = $"Submission pending/failed: {ex.Message}"
        });
    }

    return Results.Ok(new
    {
        enqueued.Receipt.ReceiptGlobalNo,
        enqueued.Receipt.ReceiptCounter,
        enqueued.QrCodeUrl,
        enqueued.VerificationCode,
        FdmsReceiptID = fdmsResponse?.ReceiptID,
        FdmsServerDate = fdmsResponse?.ServerDate,
        ValidationErrors = fdmsResponse?.ValidationErrors
    });
})
.WithName("SampleCreditNote")
.WithTags("Receipts");

// ═══════════════════════════════════════════════════════════════
//  Monitoring & Utility
// ═══════════════════════════════════════════════════════════════

app.MapGet("/api/fdms/queue-status", (FdmsService fdms) => Results.Ok(new
{
    DayOpen = fdms.IsDayOpen,
    FiscalDayNo = fdms.CurrentFiscalDayNo,
    Pending = fdms.PendingCount,
    Submitted = fdms.SubmittedCount,
    Failed = fdms.FailedCount
}))
.WithName("QueueStatus")
.WithTags("Monitoring");

app.MapPost("/api/fdms/ping", async (FdmsService fdms) =>
{
    var result = await fdms.PingAsync();
    return Results.Ok(result);
})
.WithName("Ping")
.WithTags("Utility");

app.MapPost("/api/fdms/reset-local-state", (FdmsService fdms) =>
{
    var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "fdms-queue.db");
    fdms.Dispose();
    if (File.Exists(dbPath)) File.Delete(dbPath);
    return Results.Ok(new { Message = "Local state cleared. Restart the API to re-initialize." });
})
.WithName("ResetLocalState")
.WithTags("Utility");

app.MapGet("/api/fdms/server-certificate", async (FdmsService fdms) =>
{
    var result = await fdms.GetServerCertificateAsync();
    return Results.Ok(result);
})
.WithName("GetServerCertificate")
.WithTags("Utility");

app.Run();
