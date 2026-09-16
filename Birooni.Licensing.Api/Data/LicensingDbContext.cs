using Birooni.Licensing.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Birooni.Licensing.Api.Data;

public class LicensingDbContext : DbContext
{
    public LicensingDbContext(DbContextOptions<LicensingDbContext> options) : base(options)
    {
    }

    public DbSet<License> Licenses => Set<License>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<ValidationLog> ValidationLogs => Set<ValidationLog>();
    public DbSet<ProductRelease> ProductReleases => Set<ProductRelease>();
    public DbSet<FloatingSession> FloatingSessions => Set<FloatingSession>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // License configuration
        modelBuilder.Entity<License>(entity =>
        {
            entity.ToTable("licenses");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.LicenseKey).HasColumnName("license_key").IsRequired().HasMaxLength(100);
            entity.Property(e => e.Product).HasColumnName("product").IsRequired().HasMaxLength(100);
            entity.Property(e => e.CustomerName).HasColumnName("customer_name").IsRequired().HasMaxLength(200);
            entity.Property(e => e.CustomerEmail).HasColumnName("customer_email").IsRequired().HasMaxLength(200);
            entity.Property(e => e.LicenseType).HasColumnName("license_type").IsRequired().HasMaxLength(50);
            entity.Property(e => e.LicenseMode).HasColumnName("license_mode").IsRequired().HasMaxLength(50).HasDefaultValue("Individual");
            entity.Property(e => e.Company).HasColumnName("company").HasMaxLength(200);
            entity.Property(e => e.AllowedDomain).HasColumnName("allowed_domain").HasMaxLength(100);
            entity.Property(e => e.MaxActivations).HasColumnName("max_activations").HasDefaultValue(1);
            entity.Property(e => e.ConcurrentSeats).HasColumnName("concurrent_seats");
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasIndex(e => e.LicenseKey).IsUnique();
            entity.HasIndex(e => e.AllowedDomain);
            entity.HasIndex(e => e.LicenseMode);
        });

        // Activation configuration
        modelBuilder.Entity<Activation>(entity =>
        {
            entity.ToTable("activations");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.LicenseId).HasColumnName("license_id").IsRequired();
            entity.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired().HasMaxLength(200);
            entity.Property(e => e.DeviceName).HasColumnName("device_name").HasMaxLength(200);
            entity.Property(e => e.PluginVersion).HasColumnName("plugin_version").HasMaxLength(50);
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);
            entity.Property(e => e.ActivatedAt).HasColumnName("activated_at");
            entity.Property(e => e.LastValidatedAt).HasColumnName("last_validated_at");

            entity.HasOne(e => e.License)
                .WithMany(l => l.Activations)
                .HasForeignKey(e => e.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.LicenseId);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => new { e.LicenseId, e.DeviceId });
        });

        // ValidationLog configuration
        modelBuilder.Entity<ValidationLog>(entity =>
        {
            entity.ToTable("validation_logs");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(e => e.LicenseId).HasColumnName("license_id");
            entity.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).HasColumnName("status").IsRequired().HasMaxLength(100);
            entity.Property(e => e.IpAddress).HasColumnName("ip_address").HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasColumnName("created_at");

            entity.HasOne(e => e.License)
                .WithMany()
                .HasForeignKey(e => e.LicenseId)
                .OnDelete(DeleteBehavior.SetNull);

            entity.HasIndex(e => e.LicenseId);
            entity.HasIndex(e => e.DeviceId);
            entity.HasIndex(e => e.CreatedAt);
        });

        // ProductRelease configuration
        modelBuilder.Entity<ProductRelease>(entity =>
        {
            entity.ToTable("product_releases");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.Product).HasColumnName("product").IsRequired().HasMaxLength(100);
            entity.Property(e => e.Version).HasColumnName("version").IsRequired().HasMaxLength(50);
            entity.Property(e => e.RevitVersion).HasColumnName("revit_version").IsRequired().HasMaxLength(50);
            entity.Property(e => e.DownloadUrl).HasColumnName("download_url").IsRequired().HasMaxLength(500);
            entity.Property(e => e.ReleaseNotes).HasColumnName("release_notes");
            entity.Property(e => e.IsMandatory).HasColumnName("is_mandatory").HasDefaultValue(false);
            entity.Property(e => e.ChecksumSha256).HasColumnName("checksum_sha256").HasMaxLength(128);
            entity.Property(e => e.ReleasedAt).HasColumnName("released_at");

            entity.HasIndex(e => new { e.Product, e.RevitVersion });
            entity.HasIndex(e => e.ReleasedAt);
        });

        // FloatingSession configuration
        modelBuilder.Entity<FloatingSession>(entity =>
        {
            entity.ToTable("floating_sessions");

            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.LicenseId).HasColumnName("license_id").IsRequired();
            entity.Property(e => e.DeviceId).HasColumnName("device_id").IsRequired().HasMaxLength(200);
            entity.Property(e => e.UserName).HasColumnName("user_name").HasMaxLength(200);
            entity.Property(e => e.PluginVersion).HasColumnName("plugin_version").HasMaxLength(50);
            entity.Property(e => e.StartedAt).HasColumnName("started_at");
            entity.Property(e => e.LastHeartbeatAt).HasColumnName("last_heartbeat_at");
            entity.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            entity.Property(e => e.IsActive).HasColumnName("is_active").HasDefaultValue(true);

            entity.HasOne(e => e.License)
                .WithMany()
                .HasForeignKey(e => e.LicenseId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.LicenseId);
            entity.HasIndex(e => new { e.LicenseId, e.DeviceId });
            entity.HasIndex(e => e.ExpiresAt);
        });
    }
}
