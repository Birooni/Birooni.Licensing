using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Birooni.Client.Hardware;

public static class HardwareFingerprintCollector
{
    /// <summary>
    /// Computes a deterministic, composite hardware fingerprint hash (HW-...)
    /// based on Motherboard Serial, CPU Processor ID, and Windows Machine GUID.
    /// Incorporates resilient hardware fallbacks if WMI or registry permissions are restricted.
    /// </summary>
    public static string GetDeviceFingerprint()
    {
        var motherboardSerial = GetMotherboardSerial();
        var cpuProcessorId = GetCpuProcessorId();
        var machineGuid = GetWindowsMachineGuid();

        var composite = $"{motherboardSerial.Trim()}|{cpuProcessorId.Trim()}|{machineGuid.Trim()}";
#if NET8_0_OR_GREATER
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(composite));
        var hex = Convert.ToHexString(hashBytes);
#else
        byte[] hashBytes;
        using (var sha = SHA256.Create())
        {
            hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(composite));
        }
        var hex = BitConverter.ToString(hashBytes).Replace("-", "");
#endif

        return $"HW-{hex.Substring(0, Math.Min(16, hex.Length))}";
    }

    private static string GetMotherboardSerial()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var obj in searcher.Get())
                {
                    var serial = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(serial) &&
                        !serial.Equals("None", StringComparison.OrdinalIgnoreCase) &&
                        !serial.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                        !serial.Equals("Default string", StringComparison.OrdinalIgnoreCase))
                    {
                        return serial;
                    }
                }
            }
            catch
            {
                // Fall through to fallback
            }
        }

        return GetFallbackMacAddress();
    }

    private static string GetCpuProcessorId()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var searcher = new System.Management.ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
                foreach (var obj in searcher.Get())
                {
                    var procId = obj["ProcessorId"]?.ToString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(procId))
                    {
                        return procId;
                    }
                }
            }
            catch
            {
                // Fall through to fallback
            }
        }

        var procCount = Environment.ProcessorCount.ToString();
        var procIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "GENERIC_CPU";
        return $"{procIdentifier}_{procCount}";
    }

    private static string GetWindowsMachineGuid()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

                var guid = key?.GetValue("MachineGuid")?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    return guid;
                }
            }
            catch
            {
                // Fall through to fallback
            }
        }

        return $"FALLBACK_HOST_{Environment.MachineName}";
    }

    private static string GetFallbackMacAddress()
    {
        try
        {
            var firstMac = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                              nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(nic => nic.GetPhysicalAddress().ToString())
                .FirstOrDefault(mac => !string.IsNullOrEmpty(mac));

            if (!string.IsNullOrEmpty(firstMac))
            {
                return firstMac;
            }
        }
        catch
        {
            // Ignore
        }

        return Environment.MachineName;
    }
}
