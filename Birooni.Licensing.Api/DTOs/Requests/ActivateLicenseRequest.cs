using System.ComponentModel.DataAnnotations;

namespace Birooni.Licensing.Api.DTOs.Requests;

public class ActivateLicenseRequest
{
    [Required(ErrorMessage = "LicenseKey is required")]
    public string LicenseKey { get; set; } = string.Empty;

    [Required(ErrorMessage = "DeviceId is required")]
    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;
}
