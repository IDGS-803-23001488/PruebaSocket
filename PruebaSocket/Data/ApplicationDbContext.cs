using Microsoft.EntityFrameworkCore;
using PruebaSocket.Models;

namespace PruebaSocket.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<Esp32Device> Esp32Devices => Set<Esp32Device>();

    public DbSet<Esp32Message> Esp32Messages => Set<Esp32Message>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Esp32Device>(entity =>
        {
            entity.HasKey(device => device.Id);
            entity.HasIndex(device => device.DeviceKey).IsUnique();
            entity.Property(device => device.DeviceKey).HasMaxLength(80).IsRequired();
            entity.Property(device => device.Name).HasMaxLength(120).IsRequired();
            entity.Property(device => device.Description).HasMaxLength(300);
        });

        modelBuilder.Entity<Esp32Message>(entity =>
        {
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Message).HasMaxLength(1000).IsRequired();
            entity.Property(message => message.Response).HasMaxLength(1000);
            entity.Property(message => message.ProcessingError).HasMaxLength(500);

            entity.HasOne(message => message.SourceDevice)
                .WithMany(device => device.SentMessages)
                .HasForeignKey(message => message.SourceDeviceId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(message => message.TargetDevice)
                .WithMany(device => device.ReceivedMessages)
                .HasForeignKey(message => message.TargetDeviceId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}

