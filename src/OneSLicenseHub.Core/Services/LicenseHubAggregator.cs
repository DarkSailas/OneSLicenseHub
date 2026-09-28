using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Models;

namespace OneSLicenseHub.Core.Services;

public interface ILicenseHubAggregator
{
    int PollingIntervalMinutes { get; set; }
    DateTime LastRefreshTime { get; }
    DateTime NextScheduledRefresh { get; }
    ValueTask<UnifiedLicenseOverview> GetOverviewAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}

public sealed partial class LicenseHubAggregator : BackgroundService, ILicenseHubAggregator
{
    private readonly IDigi24PlusService _digi24Service;
    private readonly IDigi14ClassicService _digi14Service;
    private readonly ISlkInspectorService _slkService;
    private readonly IGuardantInspectorService _guardantService;
    private readonly ISentinelInspectorService _sentinelService;
    private readonly HubOptions _options;
    private readonly ILogger<LicenseHubAggregator> _logger;

    private UnifiedLicenseOverview _cachedOverview = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public int PollingIntervalMinutes { get; set; }
    public DateTime LastRefreshTime { get; private set; } = DateTime.MinValue;
    public DateTime NextScheduledRefresh { get; private set; } = DateTime.MinValue;

    public LicenseHubAggregator(
        IDigi24PlusService digi24Service,
        IDigi14ClassicService digi14Service,
        ISlkInspectorService slkService,
        IGuardantInspectorService guardantService,
        ISentinelInspectorService sentinelService,
        IOptions<HubOptions> options,
        ILogger<LicenseHubAggregator> logger)
    {
        _digi24Service = digi24Service;
        _digi14Service = digi14Service;
        _slkService = slkService;
        _guardantService = guardantService;
        _sentinelService = sentinelService;
        _options = options.Value;
        _logger = logger;
        PollingIntervalMinutes = _options.PollingIntervalMinutes > 0 ? _options.PollingIntervalMinutes : 60;
    }

    public async ValueTask<UnifiedLicenseOverview> GetOverviewAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var age = DateTime.UtcNow - LastRefreshTime;
        if (!forceRefresh && _cachedOverview.Devices.Count > 0 && age.TotalSeconds < 30)
        {
            return _cachedOverview;
        }

        // Refresh runs on the service lifetime token: an aborted browser request must not cancel
        // the poll half-way and publish an empty snapshot. The caller only stops waiting.
        await RefreshDataAsync(_stoppingToken).WaitAsync(cancellationToken);
        return _cachedOverview;
    }

    private CancellationToken _stoppingToken = CancellationToken.None;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _logger.LogInformation("LicenseHub background polling service started (Interval: {Minutes} min)", PollingIntervalMinutes);

        // Initial fetch
        try
        {
            await RefreshDataAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Initial license aggregation failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetNextPollDelay();
                NextScheduledRefresh = DateTime.UtcNow.Add(delay);
                await Task.Delay(delay, stoppingToken);
                await RefreshDataAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background license polling error");
            }
        }
    }

    private async Task RefreshDataAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            _logger.LogInformation("Refreshing Digi devices and license servers telemetry...");

            // Fetch Digi telemetry in parallel (with retries: AnywhereUSB/14 web server intermittently drops connections)
            var digi24Task = PollDigiWithRetryAsync(ct => _digi24Service.GetStatusAsync(ct), cancellationToken);
            var digi14Task = PollDigiWithRetryAsync(ct => _digi14Service.GetStatusAsync(ct), cancellationToken);

            // License servers: configured endpoints + auto-discovered on InfrastructureHosts, polled in parallel
            var licenseServers = await BuildLicenseServerListAsync(cancellationToken);
            var licensesTask = InspectLicenseServersAsync(licenseServers, cancellationToken);

            await Task.WhenAll(digi24Task, digi14Task, licensesTask);

            var digi24 = await digi24Task;
            var digi14 = await digi14Task;
            var allLicenses = await licensesTask;

            // Enrich client hostnames & IP addresses from KnownHosts, active processes, and forward DNS
            await EnrichClientInfoAsync(digi24.Ports, digi14.Ports, allLicenses, cancellationToken);

            // Correlate Digi ports with discovered licenses
            CorrelatePortsWithLicenses(digi24.Ports, allLicenses);
            CorrelatePortsWithLicenses(digi14.Ports, allLicenses);

            var allPorts = new List<DigiPortModel>(digi24.Ports.Count + digi14.Ports.Count);
            allPorts.AddRange(digi24.Ports);
            allPorts.AddRange(digi14.Ports);

            // Tag Location on all discovered catalog licenses
            foreach (var lic in allLicenses)
            {
                lic.Consumers = ExtractConsumerHosts(lic);

                // A software licence is activated on its server and never sits in a USB port
                bool isSoftware = lic.Medium.Equals("Программная", StringComparison.OrdinalIgnoreCase);
                var matching = isSoftware ? new List<DigiPortModel>() : allPorts
                    .Where(p => p.HasDevice && p.LicenseInfo != null && (
                        (!string.IsNullOrEmpty(lic.DongleId) && p.LicenseInfo.DongleId.Equals(lic.DongleId, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(lic.RegistrationNumber) && p.LicenseInfo.RegistrationNumber.Equals(lic.RegistrationNumber, StringComparison.OrdinalIgnoreCase))
                    ))
                    .ToList();

                if (matching.Count > 0)
                {
                    lic.Location = string.Join(", ", matching.Select(p =>
                        p.DeviceId == "digi-150" ? $"DIGI 01: P{p.PortNumber:D2}" : $"DIGI 02: P{p.PortNumber:D2}"));
                    lic.Placements = matching.Select(ToPlacement).ToList();
                    if (string.IsNullOrEmpty(lic.Medium)) lic.Medium = "Аппаратная";
                }
                else
                {
                    string srvName = lic.ServerName;
                    string srvAddr = lic.ServerIp;
                    if (string.IsNullOrEmpty(srvName) && string.IsNullOrEmpty(srvAddr))
                    {
                        string srvEndpoint = lic.SourceServer;
                        if (string.IsNullOrEmpty(srvEndpoint) && !string.IsNullOrEmpty(lic.DetailsSummary))
                        {
                            var mHost = HostSummaryRegex().Match(lic.DetailsSummary);
                            if (mHost.Success) srvEndpoint = mHost.Groups[1].Value.Trim();
                        }

                        if (string.IsNullOrEmpty(srvEndpoint)) continue;

                        srvAddr = srvEndpoint.Split(':')[0];
                        srvName = await ResolveServerNameAsync(srvAddr, cancellationToken);
                    }

                    if (string.IsNullOrEmpty(srvAddr)) srvAddr = srvName;
                    if (string.IsNullOrEmpty(srvName)) srvName = srvAddr;

                    // License server itself may hold its dongle through a Digi port
                    var srvPort = allPorts.FirstOrDefault(p => p.HasDevice && (
                        p.ConnectedClientIp.Equals(srvAddr, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(p.ConnectedClientHost) && (
                            p.ConnectedClientHost.Equals(srvAddr, StringComparison.OrdinalIgnoreCase) ||
                            p.ConnectedClientHost.Equals(srvName, StringComparison.OrdinalIgnoreCase)))));

                    lic.Location = srvPort != null
                        ? (srvPort.DeviceId == "digi-150" ? $"DIGI 01: P{srvPort.PortNumber:D2}" : $"DIGI 02: P{srvPort.PortNumber:D2}")
                        : srvName.Equals(srvAddr, StringComparison.OrdinalIgnoreCase) ? srvAddr : $"{srvName} ({srvAddr})";
                }
            }

            int occupied = allPorts.Count(p => p.HasDevice || p.Status == "InUse");
            int inUse = allPorts.Count(p => p.Status == "InUse");
            int free = allPorts.Count - occupied;
            int totalCap = allLicenses.Sum(l => l.TotalCapacity ?? 0);
            int totalSessions = allLicenses.Sum(l => l.InUseCount ?? l.ActiveProcesses.Count);

            var overview = new UnifiedLicenseOverview
            {
                GeneratedAt = DateTime.UtcNow,
                TotalDevices = 2,
                TotalPorts = allPorts.Count,
                OccupiedPorts = occupied,
                InUsePorts = inUse,
                FreePorts = free,
                TotalLicensesCapacity = totalCap,
                TotalActiveSessions = totalSessions,
                Devices = [digi24, digi14],
                AllPorts = allPorts,
                DiscoveredLicenses = allLicenses,
                LicenseServers = licenseServers
            };

            Interlocked.Exchange(ref _cachedOverview, overview);
            LastRefreshTime = DateTime.UtcNow;
            NextScheduledRefresh = LastRefreshTime.Add(GetNextPollDelay());

            _logger.LogInformation(
                "LicenseHub refresh complete: {TotalPorts} ports ({Occupied} occupied, {InUse} in use), {LicCount} licenses discovered ({Capacity} seats). Next run at: {NextRun}",
                allPorts.Count, occupied, inUse, allLicenses.Count, totalCap, NextScheduledRefresh.ToLocalTime().ToString("HH:mm:ss"));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static readonly (string Kind, int Port)[] LicenseServiceKinds = [("СЛК", 9099), ("Guardant", 3185), ("Sentinel", 1947)];

    /// <summary>Configured license servers plus services found by probing <see cref="HubOptions.InfrastructureHosts"/>.</summary>
    private async Task<List<LicenseServerInfo>> BuildLicenseServerListAsync(CancellationToken cancellationToken)
    {
        var hostEnv = _options.InfrastructureHosts
            .Where(h => !string.IsNullOrWhiteSpace(h.Host))
            .GroupBy(h => h.Host.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => (g.First().Environment ?? string.Empty).Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);

        var servers = new List<LicenseServerInfo>();
        void AddConfigured(IEnumerable<string> endpoints, string kind, int defaultPort)
        {
            foreach (var e in endpoints.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()))
            {
                int port = int.TryParse(e.Split(':').ElementAtOrDefault(1), out int p) ? p : defaultPort;
                servers.Add(new LicenseServerInfo { Kind = kind, Endpoint = e.Contains(':') ? e : $"{e}:{port}", Port = port, Source = "config" });
            }
        }

        AddConfigured(_options.SlkServers, "СЛК", 9099);
        AddConfigured(_options.GuardantServers, "Guardant", 3185);
        AddConfigured(_options.SentinelServers, "Sentinel", 1947);

        // Auto-discovery: plain TCP probe of well-known license ports on every infrastructure host
        var probes = hostEnv.Keys.SelectMany(h => LicenseServiceKinds.Select(k => (Host: h, k.Kind, k.Port))).ToList();
        var probeResults = await Task.WhenAll(probes.Select(async p => (Probe: p, Open: await IsPortOpenAsync(p.Host, p.Port, cancellationToken))));
        foreach (var (probe, _) in probeResults.Where(r => r.Open))
        {
            servers.Add(new LicenseServerInfo { Kind = probe.Kind, Endpoint = $"{probe.Host}:{probe.Port}", Port = probe.Port, Source = "discovery" });
        }

        await Task.WhenAll(servers.Select(async s =>
        {
            string addr = s.Endpoint.Split(':')[0];
            if (IPAddress.TryParse(addr, out _))
            {
                s.Ip = addr;
                s.Host = await ResolveServerNameAsync(addr, cancellationToken);
            }
            else
            {
                s.Host = addr.ToLowerInvariant();
                s.Ip = await ResolveHostIpAsync(addr, cancellationToken);
            }

            s.Environment = hostEnv.TryGetValue(s.Host, out var env) && !string.IsNullOrEmpty(env)
                ? env
                : _options.DefaultEnvironment.Trim().ToUpperInvariant();
        }));

        // Same service reachable both from config (by IP) and discovery (by name): keep the configured entry
        return servers
            .GroupBy(s => $"{s.Kind}|{(string.IsNullOrEmpty(s.Ip) ? s.Host : s.Ip)}|{s.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(s => s.Kind).ThenBy(s => s.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new System.Net.Sockets.TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Math.Max(200, _options.DiscoveryTimeoutMilliseconds));
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<List<KeyLicenseInfo>> InspectLicenseServersAsync(List<LicenseServerInfo> servers, CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(servers.Select(async s => (Server: s, Licenses: await (s.Kind switch
        {
            "СЛК" => _slkService.InspectEndpointAsync(s.Endpoint, cancellationToken),
            "Guardant" => _guardantService.InspectEndpointAsync(s.Endpoint, cancellationToken),
            _ => _sentinelService.InspectEndpointAsync(s.Endpoint, cancellationToken)
        }))));

        var all = new List<KeyLicenseInfo>();
        foreach (var (server, licenses) in results)
        {
            if (licenses is null)
            {
                server.Status = "Unreachable";
                continue;
            }

            foreach (var lic in licenses)
            {
                lic.ServerName = server.Host;
                lic.ServerIp = server.Ip;
                lic.Environment = server.Environment;
            }
            all.AddRange(licenses);
        }

        // A Sentinel key is listed by every ACC that sees it over the network: keep the server it is plugged into
        var deduped = all
            .GroupBy(l => l.Category.StartsWith("Sentinel", StringComparison.OrdinalIgnoreCase) ? $"S|{l.DongleId}|{l.ProgramNumber}"
                        : l.Category.Contains("Guardant", StringComparison.OrdinalIgnoreCase) ? $"G|{l.DongleId}"
                        : $"K|{l.ProgramNumber}|{l.DongleId}|{l.RegistrationNumber}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(l => l.IsLocalKey).First())
            .ToList();

        foreach (var server in servers.Where(s => s.Status != "Unreachable"))
        {
            var own = deduped.Where(l => l.SourceServer.Equals(server.Endpoint, StringComparison.OrdinalIgnoreCase)).ToList();
            server.LicenseCount = own.Count;
            server.TotalCapacity = own.Sum(l => l.TotalCapacity ?? 0);
            server.Status = own.Count > 0 ? "Ok" : "Empty";
        }

        return deduped;
    }

    private const int DigiPollAttempts = 3;
    private static readonly TimeSpan DigiRetryDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DegradedRetryInterval = TimeSpan.FromMinutes(2);

    private async Task<DigiDeviceModel> PollDigiWithRetryAsync(Func<CancellationToken, ValueTask<DigiDeviceModel>> poll, CancellationToken cancellationToken)
    {
        // Each attempt issues up to 3 sequential HTTP requests to the hub
        var attemptTimeout = TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds) * 3);
        DigiDeviceModel result = null!;

        for (int attempt = 1; attempt <= DigiPollAttempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(attemptTimeout);

            result = await poll(cts.Token);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Status == "Online") return result;

            if (attempt < DigiPollAttempts)
            {
                _logger.LogWarning("{Device} poll attempt {Attempt}/{Max} returned {Status}, retrying in {Delay}s",
                    result.Name, attempt, DigiPollAttempts, result.Status, DigiRetryDelay.TotalSeconds);
                await Task.Delay(DigiRetryDelay, cancellationToken);
            }
        }

        // Keep last known ports from the previous snapshot instead of wiping the table
        if (result.Ports.Count == 0)
        {
            var previous = _cachedOverview.Devices.FirstOrDefault(d => d.Id == result.Id);
            if (previous is { Ports.Count: > 0 })
            {
                result.Ports = previous.Ports;
                result.Status = "Degraded";
            }
        }

        return result;
    }

    private TimeSpan GetNextPollDelay()
    {
        var interval = TimeSpan.FromMinutes(PollingIntervalMinutes);
        bool anyDeviceDown = _cachedOverview.Devices.Any(d => d.Status != "Online");
        return anyDeviceDown && interval > DegradedRetryInterval ? DegradedRetryInterval : interval;
    }

    private void CorrelatePortsWithLicenses(List<DigiPortModel> ports, List<KeyLicenseInfo> licenses)
    {
        var guardantPool = licenses
            .Where(l => l.Category.Contains("Guardant", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(l => l.DongleId)
            .OrderByDescending(l => l.TotalCapacity ?? 0)
            .ToList();

        var slkPool = licenses
            .Where(l => l.Category.Contains("СЛК", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int guardantAssignedIndex = 0;

        foreach (var port in ports)
        {
            string overrideKey = $"{port.DeviceId}:{port.PortNumber}";
            if (_options.PortOverrides.TryGetValue(overrideKey, out var ovr))
            {
                port.LicenseInfo = new KeyLicenseInfo
                {
                    Category = ovr.Category ?? "1C:Платформа HASP",
                    ProductName = ovr.ProductName ?? "1С:Предприятие 8 (Клиентская лицензия)",
                    LicenseType = ovr.LicenseType ?? "Сетевой ключ NetHASP",
                    TotalCapacity = ovr.TotalCapacity,
                    DetailsSummary = $"Назначено вручную для {port.PortLabel}"
                };
                continue;
            }

            // If physically no device in port
            if (!port.HasDevice)
            {
                port.LicenseInfo = null;
                port.Status = "Available";
                continue;
            }

            string host = port.ConnectedClientHost.ToLowerInvariant();
            string hwProduct = port.HardwareProduct.ToLowerInvariant();
            string hwMan = port.HardwareManufacturer.ToLowerInvariant();
            string desc = port.Description.ToLowerInvariant();

            // 1. Dalion matching
            // 1. Dalion matching
            if (host.Contains("dalion") || desc.Contains("dalion") || hwProduct.Contains("dalion") || (port.DeviceId == "digi-151" && port.PortNumber == 11))
            {
                var match = licenses.FirstOrDefault(l => l.Category.Contains("Далион", StringComparison.OrdinalIgnoreCase) || l.ProductName.Contains("Далион", StringComparison.OrdinalIgnoreCase));
                port.LicenseInfo = match != null ? new KeyLicenseInfo
                {
                    Category = match.Category,
                    ProductName = match.ProductName,
                    DongleId = match.DongleId,
                    RegistrationNumber = match.RegistrationNumber,
                    LicenseType = match.LicenseType,
                    TotalCapacity = match.TotalCapacity ?? 1,
                    InUseCount = match.InUseCount,
                    ActiveProcesses = match.ActiveProcesses,
                    DetailsSummary = match.DetailsSummary
                } : new KeyLicenseInfo
                {
                    Category = "Sentinel / Далион",
                    ProductName = "Далион: Тренд / Управление магазином",
                    LicenseType = "Сетевой сервер лицензий Далион (Feature 99)",
                    TotalCapacity = 1,
                    DetailsSummary = $"Привязан к {port.ConnectedClientHost} (Group {port.GroupNumber})"
                };
            }
            // 2. Guardant / Ювелирсофт (YUVERS+)
            else if (hwMan.Contains("aktiv") || hwProduct.Contains("guardant") || host.Contains("key05") || (port.DeviceId == "digi-151" && port.PortNumber is 5 or 10))
            {
                KeyLicenseInfo? assignedLic = null;

                if (port.DeviceId == "digi-150" && (port.PortNumber == 10 || port.PortNumber == 15))
                {
                    assignedLic = guardantPool.FirstOrDefault(l => l.DongleId.Equals("0x1A2B3C01", StringComparison.OrdinalIgnoreCase)) ?? guardantPool.FirstOrDefault(l => l.TotalCapacity == 110);
                }
                else if (port.DeviceId == "digi-150" && (port.PortNumber == 11 || port.PortNumber == 16))
                {
                    assignedLic = guardantPool.FirstOrDefault(l => l.DongleId.Equals("0x1A2B3C02", StringComparison.OrdinalIgnoreCase)) ?? guardantPool.FirstOrDefault(l => l.TotalCapacity == 31);
                }
                else if (port.DeviceId == "digi-150" && (port.PortNumber == 14 || port.PortNumber == 18))
                {
                    assignedLic = guardantPool.FirstOrDefault(l => l.DongleId.Equals("0x1A2B3C03", StringComparison.OrdinalIgnoreCase)) ?? guardantPool.FirstOrDefault(l => l.TotalCapacity == 10);
                }
                else if (port.DeviceId == "digi-150" && port.PortNumber == 17)
                {
                    assignedLic = guardantPool.FirstOrDefault(l => l.DongleId.Equals("0x1A2B3C04", StringComparison.OrdinalIgnoreCase)) ?? guardantPool.FirstOrDefault(l => l.TotalCapacity == 11);
                }
                else if (port.DeviceId == "digi-151" && port.PortNumber == 5)
                {
                    assignedLic = guardantPool.FirstOrDefault(l => l.DongleId.Equals("0x1A2B3C05", StringComparison.OrdinalIgnoreCase)) ?? guardantPool.FirstOrDefault(l => l.TotalCapacity == 16);
                }
                else if (port.DeviceId == "digi-151" && port.PortNumber == 10)
                {
                    assignedLic = guardantPool.FirstOrDefault(l => l.DongleId.Equals("0x1A2B3C04", StringComparison.OrdinalIgnoreCase)) ?? guardantPool.FirstOrDefault(l => l.TotalCapacity == 11);
                }
                else if (guardantAssignedIndex < guardantPool.Count)
                {
                    assignedLic = guardantPool[guardantAssignedIndex % guardantPool.Count];
                    guardantAssignedIndex++;
                }

                if (assignedLic != null)
                {
                    port.LicenseInfo = new KeyLicenseInfo
                    {
                        Category = assignedLic.Category,
                        ProductName = assignedLic.ProductName,
                        DongleId = assignedLic.DongleId,
                        RegistrationNumber = assignedLic.RegistrationNumber,
                        LicenseType = assignedLic.LicenseType,
                        TotalCapacity = assignedLic.TotalCapacity,
                        InUseCount = assignedLic.InUseCount,
                        ActiveProcesses = assignedLic.ActiveProcesses,
                        DetailsSummary = assignedLic.DetailsSummary
                    };
                }
                else
                {
                    port.LicenseInfo = new KeyLicenseInfo
                    {
                        Category = "Guardant / Отраслевой",
                        ProductName = "Ювелирсофт (YUVERS+)",
                        LicenseType = "Нет данных от службы Guardant",
                        DetailsSummary = $"Ключ Guardant в порту, служба GLDS не сообщила о нём • Подключен к {port.ConnectedClientHost} (Group {port.GroupNumber})"
                    };
                }
            }
            // 3. 1C:СЛК 3.0 (Катран)
            else if (host.Contains("key08") || host.Contains("key09") || (port.DeviceId == "digi-151" && port.PortNumber == 7))
            {
                var slkMatch = slkPool.FirstOrDefault();
                port.LicenseInfo = slkMatch != null ? new KeyLicenseInfo
                {
                    Category = slkMatch.Category,
                    ProductName = slkMatch.ProductName,
                    DongleId = slkMatch.DongleId,
                    RegistrationNumber = slkMatch.RegistrationNumber,
                    LicenseType = slkMatch.LicenseType,
                    TotalCapacity = slkMatch.TotalCapacity ?? 11,
                    InUseCount = slkMatch.InUseCount,
                    ActiveProcesses = slkMatch.ActiveProcesses,
                    DetailsSummary = slkMatch.DetailsSummary
                } : new KeyLicenseInfo
                {
                    Category = "1C:СЛК 3.0",
                    ProductName = "1С:СЛК 3.0",
                    LicenseType = "Нет данных от службы СЛК",
                    DetailsSummary = $"СЛК сервер на {port.ConnectedClientHost}"
                };
            }
            // 4. HASP / Sentinel 1C Platform (Server 64-bit or Multi-user client)
            else if (hwProduct.Contains("hasp") || hwProduct.Contains("sentinel") || hwMan.Contains("aks") || hwMan.Contains("safenet") || host.Contains("key") || host.Contains("app") || !string.IsNullOrEmpty(host))
            {
                string prodName = "1С:Предприятие 8 (Клиентская лицензия NetHASP)";
                string licType = "Сетевой многопользовательский (Client Net)";
                int? capacity = 50;

                // Server 64-bit dongles on application servers
                if (hwProduct.Contains("3.25") || (hwProduct.Contains("sentinel") && host.Contains("sql")) || (port.DeviceId == "digi-150" && port.PortNumber is 1 or 2 or 3 or 4 or 6 or 13) || (port.DeviceId == "digi-151" && port.PortNumber == 13))
                {
                    prodName = "1С:Предприятие 8 (Сервер 64-bit / x86-64)";
                    licType = "Серверная лицензия (Enterprise x64)";
                    capacity = 1;
                }
                else if (hwProduct.Contains("2.17") || port.DeviceId == "digi-151")
                {
                    prodName = "1С:Предприятие 8 (Клиентская лицензия NetHASP)";
                    licType = "Сетевой многопользовательский пул NetHASP LM";

                    // Calibrated seat capacities per production host
                    if (host.Contains("app01") || (port.DeviceId == "digi-151" && port.PortNumber == 4)) capacity = 300;
                    else if (host.Contains("key02") || host.Contains("app07") || (port.DeviceId == "digi-151" && port.PortNumber is 3 or 8)) capacity = 100;
                    else if (host.Contains("key03") || (port.DeviceId == "digi-150" && port.PortNumber == 5) || (port.DeviceId == "digi-151" && port.PortNumber == 12)) capacity = 100;
                    else if (host.Contains("app-dev") || (port.DeviceId == "digi-151" && port.PortNumber == 2)) capacity = 20;
                    else capacity = 50;
                }

                port.LicenseInfo = new KeyLicenseInfo
                {
                    Category = "1C:Платформа HASP",
                    ProductName = prodName,
                    LicenseType = licType,
                    TotalCapacity = capacity,
                    DetailsSummary = $"Хост: {port.ConnectedClientHost} • Аппаратный ключ {port.HardwareProduct}"
                };
            }

            // Deduce and enrich HardwareProduct & HardwareManufacturer for AnywhereUSB/14 where raw descriptors are generic
            if (port.HasDevice && port.LicenseInfo != null)
            {
                bool isGenericHw = string.IsNullOrWhiteSpace(port.HardwareProduct) ||
                                   port.HardwareProduct.Equals("USB Key", StringComparison.OrdinalIgnoreCase) ||
                                   port.HardwareProduct.Equals("dalion", StringComparison.OrdinalIgnoreCase);

                if (isGenericHw)
                {
                    var cat = port.LicenseInfo.Category ?? string.Empty;
                    var prod = port.LicenseInfo.ProductName ?? string.Empty;

                    if (cat.Contains("Guardant", StringComparison.OrdinalIgnoreCase) || prod.Contains("Ювелирсофт", StringComparison.OrdinalIgnoreCase))
                    {
                        port.HardwareProduct = "Guardant Stealth II";
                        port.HardwareManufacturer = "Aktiv Co.";
                    }
                    else if (cat.Contains("СЛК", StringComparison.OrdinalIgnoreCase) || prod.Contains("СЛК", StringComparison.OrdinalIgnoreCase))
                    {
                        port.HardwareProduct = "Katran СЛК Key";
                        port.HardwareManufacturer = "Katran";
                    }
                    else if (cat.Contains("Sentinel", StringComparison.OrdinalIgnoreCase) || cat.Contains("Далион", StringComparison.OrdinalIgnoreCase) || prod.Contains("Далион", StringComparison.OrdinalIgnoreCase))
                    {
                        port.HardwareProduct = "Sentinel HL";
                        port.HardwareManufacturer = "SafeNet Inc.";
                    }
                    else if (cat.Contains("HASP", StringComparison.OrdinalIgnoreCase))
                    {
                        if (prod.Contains("Сервер", StringComparison.OrdinalIgnoreCase))
                        {
                            port.HardwareProduct = "HASP HL 3.25";
                            port.HardwareManufacturer = "AKS";
                        }
                        else
                        {
                            port.HardwareProduct = "HASP 2.17";
                            port.HardwareManufacturer = "AKS";
                        }
                    }
                }
            }

            if (port.LicenseInfo != null)
            {
                port.LicenseInfo.Location = port.DeviceId == "digi-150"
                    ? $"DIGI 01: P{port.PortNumber:D2}"
                    : $"DIGI 02: P{port.PortNumber:D2}";
                // Whatever licence is attributed to a physical port, the thing in the port is a hardware key
                port.LicenseInfo.Medium = "Аппаратная";
                port.LicenseInfo.Placements = [ToPlacement(port)];
                if (port.LicenseInfo.Consumers.Count == 0 && !string.IsNullOrWhiteSpace(port.ConnectedClientHost))
                {
                    port.LicenseInfo.Consumers = [ShortHost(port.ConnectedClientHost)];
                }
            }
        }
    }

    private static LicensePlacement ToPlacement(DigiPortModel port) => new()
    {
        DeviceId = port.DeviceId,
        DeviceLabel = port.DeviceId == "digi-150" ? "DIGI 01" : "DIGI 02",
        PortNumber = port.PortNumber,
        ConnectedHost = ShortHost(port.ConnectedClientHost),
        ConnectedIp = port.ConnectedClientIp
    };

    private static string ShortHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        string h = host.Trim().TrimStart('\\');
        if (IPAddress.TryParse(h, out _)) return h;
        int dot = h.IndexOf('.');
        return (dot > 0 ? h[..dot] : h).ToLowerInvariant();
    }

    /// <summary>1C servers using a licence: SLK "HOST (ip) - ...", Guardant "rphost.exe (host.domain)".</summary>
    private static List<string> ExtractConsumerHosts(KeyLicenseInfo lic)
    {
        var hosts = new List<string>();
        foreach (var proc in lic.ActiveProcesses)
        {
            var m = HostProcessRegex().Match(proc.TrimStart('\\'));
            string host = m.Success ? m.Groups[1].Value : string.Empty;
            if (string.IsNullOrEmpty(host))
            {
                var paren = ParenHostRegex().Match(proc);
                if (paren.Success) host = paren.Groups[1].Value;
            }

            host = ShortHost(host);
            if (!string.IsNullOrEmpty(host) && !hosts.Contains(host, StringComparer.OrdinalIgnoreCase)) hosts.Add(host);
        }
        return hosts;
    }

    private static readonly ConcurrentDictionary<string, string> ForwardDnsCache = new(StringComparer.OrdinalIgnoreCase);

    private async Task EnrichClientInfoAsync(List<DigiPortModel> ports1, List<DigiPortModel> ports2, List<KeyLicenseInfo> licenses, CancellationToken cancellationToken)
    {
        var ipToHost = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hostToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Seed from KnownHosts config
        foreach (var (ip, host) in _options.KnownHosts)
        {
            if (!string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(host))
            {
                string cleanIp = ip.Trim();
                string cleanHost = host.Trim().ToLowerInvariant();
                ipToHost[cleanIp] = cleanHost;
                hostToIp[cleanHost] = cleanIp;
            }
        }

        // 2. Dynamically harvest from license active processes (e.g. "APP-DEV-01 (192.168.1.50) - ...")
        foreach (var lic in licenses)
        {
            foreach (var proc in lic.ActiveProcesses)
            {
                var match = HostProcessRegex().Match(proc);
                if (match.Success)
                {
                    string h = match.Groups[1].Value.Trim().TrimStart('\\').ToLowerInvariant();
                    string ip = match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(h) && !string.IsNullOrEmpty(ip))
                    {
                        ipToHost.TryAdd(ip, h);
                        hostToIp.TryAdd(h, ip);
                    }
                }
            }
        }

        var all = ports1.Concat(ports2).ToList();

        // 3. Harvest pairs from ports that already have both host and IP
        foreach (var p in all)
        {
            if (!string.IsNullOrWhiteSpace(p.ConnectedClientIp) && !string.IsNullOrWhiteSpace(p.ConnectedClientHost))
            {
                string ip = p.ConnectedClientIp.Trim();
                string h = p.ConnectedClientHost.Trim().ToLowerInvariant();
                if (!IPAddress.TryParse(h, out _))
                {
                    ipToHost.TryAdd(ip, h);
                    hostToIp.TryAdd(h, ip);
                }
            }
        }

        // 4. Resolve hostnames for ports having only IP
        foreach (var port in all)
        {
            if (!string.IsNullOrWhiteSpace(port.ConnectedClientIp))
            {
                string ip = port.ConnectedClientIp.Trim();
                if (string.IsNullOrWhiteSpace(port.ConnectedClientHost) ||
                    port.ConnectedClientHost.Equals(ip, StringComparison.OrdinalIgnoreCase) ||
                    IPAddress.TryParse(port.ConnectedClientHost, out _))
                {
                    if (ipToHost.TryGetValue(ip, out var resolvedHost))
                    {
                        port.ConnectedClientHost = resolvedHost;
                    }
                }
            }
        }

        // 5. Resolve IP addresses for ports having host but missing IP (via cross-mapping & forward DNS)
        foreach (var port in all)
        {
            if (!string.IsNullOrWhiteSpace(port.ConnectedClientHost) && string.IsNullOrWhiteSpace(port.ConnectedClientIp))
            {
                string host = port.ConnectedClientHost.Trim().ToLowerInvariant();
                if (hostToIp.TryGetValue(host, out var foundIp))
                {
                    port.ConnectedClientIp = foundIp;
                }
                else if (!IPAddress.TryParse(host, out _))
                {
                    string resolvedIp = await ResolveHostIpAsync(host, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(resolvedIp))
                    {
                        port.ConnectedClientIp = resolvedIp;
                        hostToIp[host] = resolvedIp;
                        ipToHost.TryAdd(resolvedIp, host);
                    }
                }
            }
        }
    }

    private static async ValueTask<string> ResolveHostIpAsync(string host, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        if (ForwardDnsCache.TryGetValue(host, out var cached)) return cached;

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip != null)
            {
                string ipStr = ip.ToString();
                ForwardDnsCache[host] = ipStr;
                return ipStr;
            }
        }
        catch
        {
            // Forward DNS lookup failure
        }

        return string.Empty;
    }

    private static readonly ConcurrentDictionary<string, string> ReverseDnsCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a license server address to its short host name (KnownHosts → PTR); returns the address itself on failure.</summary>
    private async ValueTask<string> ResolveServerNameAsync(string address, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        if (!IPAddress.TryParse(address, out _)) return address.ToLowerInvariant();
        if (_options.KnownHosts.TryGetValue(address, out var known) && !string.IsNullOrWhiteSpace(known)) return known.Trim().ToLowerInvariant();
        if (ReverseDnsCache.TryGetValue(address, out var cached)) return cached;

        try
        {
            var entry = await Dns.GetHostEntryAsync(address, cancellationToken).WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            string name = entry.HostName;
            int dot = name.IndexOf('.');
            string shortName = dot > 0 ? name[..dot] : name;
            if (!string.IsNullOrWhiteSpace(shortName) && !IPAddress.TryParse(shortName, out _))
            {
                // Only successful lookups are cached: a cold-start timeout must not pin the raw IP forever
                string resolved = shortName.ToLowerInvariant();
                ReverseDnsCache[address] = resolved;
                return resolved;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Reverse DNS missing or timed out; retried on the next poll
        }

        return address;
    }

    [GeneratedRegex(@"\(([A-Za-z0-9][A-Za-z0-9\.\-_]*)\)")]
    private static partial Regex ParenHostRegex();

    [GeneratedRegex(@"^([A-Za-z0-9\-_]+)\s*\(([\d\.]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex HostProcessRegex();

    [GeneratedRegex(@"(?:Хост|Сервер|СЛК-сервер):\s*([a-zA-Z0-9\.\-_:]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HostSummaryRegex();
}
