using System.ComponentModel.DataAnnotations;

namespace Birooni.Licensing.Api.DTOs.Requests;

public class CreateCustomLicenseRequest
{
    public string? CustomKey { get; set; } // If null, auto-generates format BIRU-XXXX-XXXX-XXXX

    [Required]
    [MaxLength(100)]
    public string Product { get; set; } = "BiruBox"; // BiruBox, BirooniSuite, etc.

    [Required]
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    [Required]
    [EmailAddress]
    [MaxLength(200)]
    public string CustomerEmail { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Company { get; set; }

    [Required]
    [MaxLength(50)]
    public string LicenseType { get; set; } = "Commercial"; // Commercial, Trial, Educational, NFR

    [Required]
    [MaxLength(50)]
    public string LicenseMode { get; set; } = "Individual"; // Individual, SiteLicense, Floating

    [MaxLength(100)]
    public string? AllowedDomain { get; set; } // e.g. "@gensler.com"

    [Range(1, 1000)]
    public int MaxActivations { get; set; } = 2; // Default 2 PC seats per individual user

    [Range(1, 1000)]
    public int? ConcurrentSeats { get; set; } // For Floating licenses

    public int? DurationDays { get; set; } // e.g. 365 for 1-year sub, null for perpetual
}
