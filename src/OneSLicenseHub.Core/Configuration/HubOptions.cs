namespace OneSLicenseHub.Core.Configuration;

public sealed class HubOptions
{
    public DigiDeviceConfig Digi1 { get; set; } = new()
    {
        Id = "digi-150",
        Name = "Digi AnywhereUSB 24 Plus",
        Host = "192.168.1.150",
        Port = 443,
        UseHttps = true,
        Username = "admin",
        Password = ""
    };

    public DigiDeviceConfig Digi2 { get; set; } = new()
    {
        Id = "digi-151",
        Name = "Digi AnywhereUSB/14",
        Host = "192.168.1.151",
        Port = 80,
        UseHttps = false,
        Username = "root",
        Password = ""
    };

    public List<string> SlkServers { get; set; } =
    [
        "192.168.1.20:9099",
        "192.168.1.21:9099"
    ];

    public List<string> GuardantServers { get; set; } =
    [
        "192.168.1.30:3185",
        "192.168.1.31:3185"
    ];

    public List<string> SentinelServers { get; set; } =
    [
        "192.168.1.40:1947"
    ];

    public int PollingIntervalMinutes { get; set; } = 60; // Default: 1 hour
    public int RequestTimeoutSeconds { get; set; } = 6;

    public Dictionary<string, string> KnownHosts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Servers scanned for license services (SLK :9099, Guardant :3185, Sentinel :1947) on every poll.</summary>
    public List<InfrastructureHost> InfrastructureHosts { get; set; } = [];

    /// <summary>Environment label for license servers that are not listed in <see cref="InfrastructureHosts"/>.</summary>
    public string DefaultEnvironment { get; set; } = string.Empty;

    public int DiscoveryTimeoutMilliseconds { get; set; } = 1500;
    public Dictionary<string, PortOverrideConfig> PortOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InfrastructureHost
{
    public string Host { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
}

public sealed class PortOverrideConfig
{
    public string? ProductName { get; set; }
    public string? Category { get; set; }
    public string? LicenseType { get; set; }
    public int? TotalCapacity { get; set; }
}

public sealed class DigiDeviceConfig
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; }
    public bool UseHttps { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
