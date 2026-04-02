using Microsoft.EntityFrameworkCore;
using ZimraFdms.Api.Data;
using ZimraFdms.Api.Data.Entities;

namespace ZimraFdms.Api.Services;

public class AuthService
{
    private readonly AppDbContext _db;

    public AuthService(AppDbContext db) => _db = db;

    public async Task<AppUser?> ValidateLoginAsync(string username, string password)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == username && u.IsActive);
        if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return null;
        return user;
    }

    public async Task<AppUser> RegisterAdminAsync(string username, string email, string password)
    {
        if (await _db.Users.AnyAsync(u => u.Username == username))
            throw new InvalidOperationException("Username already taken.");
        if (await _db.Users.AnyAsync(u => u.Email == email))
            throw new InvalidOperationException("Email already registered.");

        var user = new AppUser
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = UserRole.Admin
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<AppUser> CreateOperatorAsync(string username, string email, string password, int createdByUserId)
    {
        if (await _db.Users.AnyAsync(u => u.Username == username))
            throw new InvalidOperationException("Username already taken.");

        var user = new AppUser
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = UserRole.Operator,
            CreatedByUserId = createdByUserId
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task<AppUser?> ValidateApiKeyAsync(string apiKey)
    {
        return await _db.Users.FirstOrDefaultAsync(u => u.ApiKey == apiKey && u.IsActive);
    }

    public async Task<string> RegenerateApiKeyAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId)
            ?? throw new InvalidOperationException("User not found.");
        user.ApiKey = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync();
        return user.ApiKey;
    }

    public async Task<List<UserDevice>> GetAccessibleDevicesAsync(AppUser user)
    {
        if (user.Role == UserRole.SuperAdmin)
            return await _db.Devices.Where(d => d.IsActive).ToListAsync();

        if (user.Role == UserRole.Admin)
            return await _db.Devices.Where(d => d.OwnerUserId == user.Id && d.IsActive).ToListAsync();

        // Operator: only assigned devices
        return await _db.DeviceOperators
            .Where(x => x.UserId == user.Id)
            .Select(x => x.Device)
            .Where(d => d.IsActive)
            .ToListAsync();
    }

    public async Task<bool> CanAccessDeviceAsync(AppUser user, int deviceId)
    {
        if (user.Role == UserRole.SuperAdmin) return true;
        if (user.Role == UserRole.Admin)
            return await _db.Devices.AnyAsync(d => d.Id == deviceId && d.OwnerUserId == user.Id && d.IsActive);
        return await _db.DeviceOperators.AnyAsync(x => x.UserId == user.Id && x.DeviceId == deviceId);
    }

    public async Task SeedSuperAdminAsync()
    {
        if (await _db.Users.AnyAsync(u => u.Role == UserRole.SuperAdmin))
            return;

        _db.Users.Add(new AppUser
        {
            Username = "admin",
            Email = "admin@localhost",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("ChangeMe123!"),
            Role = UserRole.SuperAdmin
        });
        await _db.SaveChangesAsync();
    }
}
