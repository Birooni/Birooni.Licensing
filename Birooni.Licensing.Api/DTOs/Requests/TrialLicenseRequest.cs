using System.ComponentModel.DataAnnotations;

namespace Birooni.Licensing.Api.DTOs.Requests;

public class TrialLicenseRequest
{
    [Required(ErrorMessage = "CustomerName is required")]
    [MaxLength(200)]
    public string CustomerName { get; set; } = string.Empty;

    [Required(ErrorMessage = "CustomerEmail is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    [MaxLength(200)]
    public string CustomerEmail { get; set; } = string.Empty;

    [Required(ErrorMessage = "DeviceId is required")]
    [MaxLength(200)]
    public string DeviceId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string DeviceName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Product is required")]
    [MaxLength(100)]
    public string Product { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? PluginVersion { get; set; }
}
