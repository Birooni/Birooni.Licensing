namespace Birooni.Licensing.Api.DTOs.Responses;

public class AdminStatsResponse
{
    public int TotalLicenses { get; set; }
    public int ActiveLicenses { get; set; }
    public int TrialLicenses { get; set; }
    public int TotalActivations { get; set; }
    public int ActiveDevices { get; set; }
    public int TotalReleases { get; set; }
    public int ValidationsLast24Hours { get; set; }
}
