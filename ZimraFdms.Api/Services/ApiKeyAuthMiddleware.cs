using ZimraFdms.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ZimraFdms.Api.Services;

public class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/") || path.StartsWith("/api/auth/"))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) || string.IsNullOrEmpty(apiKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { Error = "Missing X-Api-Key header" });
            return;
        }

        var db = context.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.ApiKey == apiKey.ToString() && u.IsActive);
        if (user == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { Error = "Invalid API key" });
            return;
        }

        context.Items["AuthUser"] = user;

        if (context.Request.Headers.TryGetValue("X-Device-Id", out var deviceIdStr)
            && int.TryParse(deviceIdStr, out var deviceId))
        {
            var authService = context.RequestServices.GetRequiredService<AuthService>();
            if (!await authService.CanAccessDeviceAsync(user, deviceId))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { Error = "No access to this device" });
                return;
            }
            context.Items["DeviceId"] = deviceId;
        }

        await _next(context);
    }
}
