using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Birooni.Licensing.Api.Models;

[Table("licenses")]
public class License
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    [Column("license_key")]
    public string LicenseKey { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    [Column("product")]
    public string Product { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    [Column("customer_name")]
    public string CustomerName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(200)]
    [Column("customer_email")]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    [Column("license_type")]
    public string LicenseType { get; set; } = "Standard";

    [Required]
    [MaxLength(50)]
    [Column("license_mode")]
    public string LicenseMode { get; set; } = "Individual"; // Individual, SiteLicense, Floating

    [MaxLength(200)]
    [Column("company")]
    public string? Company { get; set; }

    [MaxLength(100)]
    [Column("allowed_domain")]
    public string? AllowedDomain { get; set; } // e.g. "@firm.com" for SiteLicense

    [Column("max_activations")]
    public int MaxActivations { get; set; } = 1;

    [Column("concurrent_seats")]
    public int? ConcurrentSeats { get; set; } // For Floating licenses

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation property
    public virtual ICollection<Activation> Activations { get; set; } = new List<Activation>();
}
