namespace ZimraFdms.Api.Data.Entities;

public class DeviceOperator
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public int DeviceId { get; set; }
    public UserDevice Device { get; set; } = null!;
}
