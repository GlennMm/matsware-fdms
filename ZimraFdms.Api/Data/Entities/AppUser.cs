namespace ZimraFdms.Api.Data.Entities;

public class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string ApiKey { get; set; } = Guid.NewGuid().ToString("N");
    public UserRole Role { get; set; }
    public int? CreatedByUserId { get; set; }
    public AppUser? CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    public List<UserDevice> OwnedDevices { get; set; } = new();
    public List<DeviceOperator> DeviceAssignments { get; set; } = new();
}
