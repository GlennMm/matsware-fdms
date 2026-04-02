namespace ZimraFdms.Api.Data.Entities;

public class UserDevice
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    public AppUser Owner { get; set; } = null!;
    public string DeviceLabel { get; set; } = string.Empty;
    public int DeviceID { get; set; }
    public string DeviceSerialNo { get; set; } = string.Empty;
    public string ActivationKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://fdmsapitest.zimra.co.zw";
    public bool UseEcc { get; set; } = true;
    public string DeviceModelName { get; set; } = "Server";
    public string DeviceModelVersion { get; set; } = "v1";
    public string? CertificatePfxPath { get; set; }
    public string? PrivateKeyPemPath { get; set; }
    public string QueueDbPath { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<DeviceOperator> Operators { get; set; } = new();
}
