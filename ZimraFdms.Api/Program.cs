using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using ZimraFdms;
using ZimraFdms.Api.Data;
using ZimraFdms.Api.Services;
using ZimraFdms.Models;

var builder = WebApplication.CreateBuilder(args);

// ── DI: Database ──
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── DI: Auth ──
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/api/auth/logout";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(24 * 60);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

// ── DI: Application Services ──
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<DeviceManager>();

// ── DI: Blazor Server ──
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── Swagger ──
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "MATSwarePOS FDMS API", Version = "v1" });
});

var app = builder.Build();

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

// ── Startup: apply migrations, seed SuperAdmin, initialize active devices ──
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var authSvc = scope.ServiceProvider.GetRequiredService<AuthService>();
    await authSvc.SeedSuperAdminAsync();
}

var dm = app.Services.GetRequiredService<DeviceManager>();
await dm.InitializeActiveDevicesAsync();

// ── Middleware pipeline ──
app.UseSwagger();
app.UseSwaggerUI();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseAntiforgery();

// ═══════════════════════════════════════════════════════════════
//  Auth endpoints
// ═══════════════════════════════════════════════════════════════

app.MapGet("/api/auth/signin", async (int userId, HttpContext ctx) =>
{
    var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
    var user = await db.Users.FindAsync(userId);
    if (user == null) return Results.Redirect("/login");

    var claims = new List<Claim>
    {
        new("UserId", user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).ExcludeFromDescription();

app.MapGet("/api/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).ExcludeFromDescription();

// ═══════════════════════════════════════════════════════════════
//  Registration endpoints (one-time, no DeviceId needed)
// ═══════════════════════════════════════════════════════════════

app.MapPost("/api/fdms/verify-taxpayer", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
    var result = await fdms.VerifyTaxpayerAsync();
    return Results.Ok(result);
})
.WithName("VerifyTaxpayer")
.WithTags("Registration");

app.MapPost("/api/fdms/register", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);

    var certsDir = Path.Combine(Directory.GetCurrentDirectory(), "certs", deviceId.ToString());
    Directory.CreateDirectory(certsDir);

    var result = await fdms.RegisterDeviceAsync(
        privateKeyOutputPath: Path.Combine(certsDir, "device-private.pem"),
        certificateOutputPath: Path.Combine(certsDir, "device-cert.pem"));

    return Results.Ok(new
    {
        result.OperationID,
        Message = "Device registered. Certificate saved to certs/. Restart the API to load the certificate for mTLS.",
        CertificatePath = $"certs/{deviceId}/device-cert.pem",
        PrivateKeyPath = $"certs/{deviceId}/device-private.pem"
    });
})
.WithName("RegisterDevice")
.WithTags("Registration");

// ═══════════════════════════════════════════════════════════════
//  Config & Status
// ═══════════════════════════════════════════════════════════════

app.MapGet("/api/fdms/config", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
    return Results.Ok(await fdms.GetConfigAsync());
})
.WithName("GetConfig")
.WithTags("Config");

app.MapGet("/api/fdms/status", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
    return Results.Ok(await fdms.GetStatusAsync());
})
.WithName("GetStatus")
.WithTags("Config");

// ═══════════════════════════════════════════════════════════════
//  Fiscal Day Lifecycle
// ═══════════════════════════════════════════════════════════════

app.MapPost("/api/fdms/open-day", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
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

app.MapPost("/api/fdms/close-day", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
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

app.MapPost("/api/fdms/receipts", async (ReceiptDto receipt, HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
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

// ═══════════════════════════════════════════════════════════════
//  Monitoring & Utility
// ═══════════════════════════════════════════════════════════════

app.MapGet("/api/fdms/queue-status", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
    return Results.Ok(new
    {
        DayOpen = fdms.IsDayOpen,
        FiscalDayNo = fdms.CurrentFiscalDayNo,
        Pending = fdms.PendingCount,
        Submitted = fdms.SubmittedCount,
        Failed = fdms.FailedCount
    });
})
.WithName("QueueStatus")
.WithTags("Monitoring");

app.MapPost("/api/fdms/ping", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
    var result = await fdms.PingAsync();
    return Results.Ok(result);
})
.WithName("Ping")
.WithTags("Utility");

app.MapGet("/api/fdms/server-certificate", async (HttpContext ctx, DeviceManager dm) =>
{
    var deviceId = (int)(ctx.Items["DeviceId"] ?? throw new InvalidOperationException("X-Device-Id header required"));
    var fdms = await dm.GetOrCreateAsync(deviceId);
    var result = await fdms.GetServerCertificateAsync();
    return Results.Ok(result);
})
.WithName("GetServerCertificate")
.WithTags("Utility");

// ── Blazor Server ──
app.MapRazorComponents<ZimraFdms.Api.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
