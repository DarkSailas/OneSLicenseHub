namespace OneSLicenseHub.Core.Models;

public sealed class DigiDeviceModel
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string IpAddress { get; init; }
    public required int TotalPorts { get; init; }
    public string Model { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string DeviceGuid { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = "HTTP";
    public string Firmware { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public DateTime LastCheckTime { get; set; } = DateTime.UtcNow;
    public List<DigiPortModel> Ports { get; set; } = [];

    public static string FormatDigiDeviceGuid(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return string.Empty;
        var clean = System.Text.RegularExpressions.Regex.Replace(mac, "[^0-9A-Fa-f]", "").ToUpperInvariant();
        return clean.Length == 12 ? $"00000000-00000000-{clean[..6]}FF-FF{clean[6..]}" : string.Empty;
    }
}

public sealed class DigiPortModel
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required int PortNumber { get; init; }
    public string PortLabel => $"Port {PortNumber:D2}";
    public string GroupName { get; set; } = string.Empty;
    public int? GroupNumber { get; set; }
    public bool HasDevice { get; set; }
    public string Status { get; set; } = "Available"; // "InUse", "Connected", "Available", "Unassigned"
    public string ConnectedClientHost { get; set; } = string.Empty;
    public string ConnectedClientIp { get; set; } = string.Empty;
    public string UsbVersion { get; set; } = string.Empty;
    public string HardwareManufacturer { get; set; } = string.Empty;
    public string HardwareProduct { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public KeyLicenseInfo? LicenseInfo { get; set; }
}

public sealed class KeyLicenseInfo
{
    public required string Category { get; set; } // "1C:СЛК 3.0", "Guardant / Ювелирсофт", "Sentinel / Далион", "1C:Платформа HASP Net", "Unknown"
    public required string ProductName { get; set; }
    public string DongleId { get; set; } = string.Empty;
    public string ProgramNumber { get; set; } = string.Empty;
    public string RegistrationNumber { get; set; } = string.Empty;
    public string LicenseType { get; set; } = string.Empty;
    public int? TotalCapacity { get; set; }
    public int? InUseCount { get; set; }
    public string Organization { get; set; } = string.Empty;
    public string SupportPeriod { get; set; } = string.Empty;
    public List<string> ActiveProcesses { get; set; } = [];
    public string DetailsSummary { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string SourceServer { get; set; } = string.Empty; // License server endpoint the record was read from ("host:port")
    public string ServerName { get; set; } = string.Empty;   // Short host name of the license server
    public string ServerIp { get; set; } = string.Empty;     // IPv4 of the license server
    public string Environment { get; set; } = string.Empty;  // "PROD", "DEV", ... from InfrastructureHosts / DefaultEnvironment
    public bool IsLocalKey { get; set; } = true;             // Sentinel: key is physically attached to this server (vs. seen over network)
    public bool IsActive { get; set; } = true;               // false for disabled / expired licences
    public string LicenseStatus { get; set; } = string.Empty; // Status text reported by the licence server
    public string Medium { get; set; } = string.Empty;       // "Программная" (software key) | "Аппаратная" (hardware key)
    public List<LicensePlacement> Placements { get; set; } = []; // Digi ports the hardware key is plugged into
    public List<string> Consumers { get; set; } = [];        // Short host names of 1C servers using the licence
}

public sealed class LicensePlacement
{
    public required string DeviceId { get; init; }     // "digi-150" | "digi-151"
    public required string DeviceLabel { get; init; }  // "DIGI 01" | "DIGI 02"
    public required int PortNumber { get; init; }
    public string ConnectedHost { get; init; } = string.Empty; // Server the port is forwarded to (RealPort client)
    public string ConnectedIp { get; init; } = string.Empty;
}

public sealed class LicenseServerInfo
{
    public required string Kind { get; init; }        // "СЛК", "Guardant", "Sentinel"
    public required string Endpoint { get; init; }    // "host:port" used for polling
    public required int Port { get; init; }
    public string Host { get; set; } = string.Empty;
    public string Ip { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string Source { get; set; } = "config";    // "config" | "discovery"
    public string Status { get; set; } = "Unknown";   // "Ok" | "Empty" | "Unreachable"
    public int LicenseCount { get; set; }
    public int TotalCapacity { get; set; }
}

public sealed class UnifiedLicenseOverview
{
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public int TotalDevices { get; set; }
    public int TotalPorts { get; set; }
    public int OccupiedPorts { get; set; }
    public int InUsePorts { get; set; }
    public int FreePorts { get; set; }
    public int TotalLicensesCapacity { get; set; }
    public int TotalActiveSessions { get; set; }
    public List<DigiDeviceModel> Devices { get; set; } = [];
    public List<DigiPortModel> AllPorts { get; set; } = [];
    public List<KeyLicenseInfo> DiscoveredLicenses { get; set; } = [];
    public List<LicenseServerInfo> LicenseServers { get; set; } = [];
}
