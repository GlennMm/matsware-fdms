using Microsoft.EntityFrameworkCore;
using ZimraFdms.Api.Data.Entities;

namespace ZimraFdms.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserDevice> Devices => Set<UserDevice>();
    public DbSet<DeviceOperator> DeviceOperators => Set<DeviceOperator>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.HasIndex(u => u.ApiKey).IsUnique();
            e.HasOne(u => u.CreatedBy)
                .WithMany()
                .HasForeignKey(u => u.CreatedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<UserDevice>(e =>
        {
            e.HasOne(d => d.Owner)
                .WithMany(u => u.OwnedDevices)
                .HasForeignKey(d => d.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceOperator>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
            e.HasOne(x => x.User)
                .WithMany(u => u.DeviceAssignments)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Device)
                .WithMany(d => d.Operators)
                .HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
