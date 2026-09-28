using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Models;

namespace OneSLicenseHub.Core.Services;

public interface IDigi14ClassicService
{
    ValueTask<DigiDeviceModel> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed partial class Digi14ClassicService(
    HttpClient httpClient,
    IOptions<HubOptions> options,
    ILogger<Digi14ClassicService> logger) : IDigi14ClassicService
{
    private readonly HubOptions _options = options.Value;
    private readonly DigiDeviceConfig _config = options.Value.Digi2;

    public async ValueTask<DigiDeviceModel> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var model = new DigiDeviceModel
        {
            Id = _config.Id,
            Name = _config.Name,
            IpAddress = _config.Host,
            TotalPorts = 14,
            Model = "AnywhereUSB/14",
            Status = "Degraded"
        };

        try
        {
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.Username}:{_config.Password}"));
            var baseUri = new Uri($"http://{_config.Host}:{_config.Port}");

            // 1. Fetch RealPort USB configuration (port -> group + description)
            var configReq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/config/applications/realport_usb_config.htm"));
            configReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
            var configResp = await httpClient.SendAsync(configReq, cancellationToken);
            configResp.EnsureSuccessStatusCode();
            var configHtml = await configResp.Content.ReadAsStringAsync(cancellationToken);

            var (portToGroup, portDescriptions) = ParseRealportUsbConfig(configHtml);
            if (portToGroup.Count == 0)
            {
                throw new InvalidOperationException($"Digi 14 returned HTML without any USB port mappings (parsed 0 ports, HTML length: {configHtml.Length}).");
            }

            // 2. Fetch Active System Connections
            var connReq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/management/system/connections_mgmt.htm"));
            connReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
            var connResp = await httpClient.SendAsync(connReq, cancellationToken);
            connResp.EnsureSuccessStatusCode();
            var connHtml = await connResp.Content.ReadAsStringAsync(cancellationToken);

            var groupToConnection = ParseActiveConnections(connHtml);

            // 3. Fetch System info for MAC, Product ID & firmware
            try
            {
                var sysReq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/admin/sysinfo/general_stats.htm"));
                sysReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
                var sysResp = await httpClient.SendAsync(sysReq, cancellationToken);
                if (sysResp.IsSuccessStatusCode)
                {
                    var sysHtml = await sysResp.Content.ReadAsStringAsync(cancellationToken);

                    var macMatch = Mac14Regex().Match(sysHtml);
                    if (macMatch.Success) model.MacAddress = macMatch.Groups[1].Value.Trim();

                    var pidMatch = ProductId14Regex().Match(sysHtml);
                    if (pidMatch.Success) model.ProductId = pidMatch.Groups[1].Value.Trim();

                    var fwMatch = Firmware14Regex().Match(sysHtml);
                    if (fwMatch.Success) model.Firmware = fwMatch.Groups[1].Value.Trim();
                }
            }
            catch
            {
                // Non-fatal
            }

            model.Port = _config.Port;
            model.Protocol = "HTTP";
            model.DeviceGuid = DigiDeviceModel.FormatDigiDeviceGuid(model.MacAddress);

            // Build all 14 ports
            var ports = new List<DigiPortModel>(14);
            for (int p = 1; p <= 14; p++)
            {
                portToGroup.TryGetValue(p, out int grpNum);
                portDescriptions.TryGetValue(p, out string? desc);
                desc ??= string.Empty;

                groupToConnection.TryGetValue(grpNum, out var conn);

                string clientIp = conn.Ip ?? string.Empty;
                string clientHost = conn.Host ?? string.Empty;

                // If host is empty or equal to IP, resolve host via KnownHosts or DNS PTR
                if (!string.IsNullOrEmpty(clientIp) && (string.IsNullOrEmpty(clientHost) || clientHost == clientIp))
                {
                    clientHost = await ResolveClientHostAsync(clientIp, cancellationToken);
                }

                bool isInUse = !string.IsNullOrEmpty(clientIp);
                string status = isInUse ? "InUse" : (grpNum > 0 ? "Available" : "Unassigned");

                ports.Add(new DigiPortModel
                {
                    DeviceId = model.Id,
                    DeviceName = model.Name,
                    PortNumber = p,
                    GroupName = grpNum > 0 ? $"Group {grpNum}" : "Unassigned",
                    GroupNumber = grpNum > 0 ? grpNum : null,
                    HasDevice = isInUse || !string.IsNullOrEmpty(desc),
                    Status = status,
                    ConnectedClientHost = clientHost,
                    ConnectedClientIp = clientIp,
                    UsbVersion = "2.0",
                    HardwareManufacturer = !string.IsNullOrEmpty(desc) && desc.Contains("dalion", StringComparison.OrdinalIgnoreCase) ? "SafeNet / Dalion" : string.Empty,
                    HardwareProduct = !string.IsNullOrEmpty(desc) ? desc : (isInUse ? "USB Key" : string.Empty),
                    Description = desc
                });
            }

            _lastKnownPorts = ports;
            model.Ports = ports;
            model.Status = "Online";
            model.LastCheckTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to poll Digi AnywhereUSB/14 ({Host}): {Message}", _config.Host, ex.Message);
            if (_lastKnownPorts != null && _lastKnownPorts.Count > 0)
            {
                model.Ports = _lastKnownPorts;
                model.Status = "Degraded";
            }
            else
            {
                model.Status = "Offline";
            }
        }

        return model;
    }

    private List<DigiPortModel>? _lastKnownPorts;

    private static (Dictionary<int, int> portToGroup, Dictionary<int, string> portDescriptions) ParseRealportUsbConfig(string html)
    {
        var portToGroup = new Dictionary<int, int>();
        var portDescriptions = new Dictionary<int, string>();

        var portMatches = PortBlockRegex().Matches(html);
        foreach (Match pm in portMatches)
        {
            if (int.TryParse(pm.Groups[1].Value, out int portNum))
            {
                string block = pm.Groups[0].Value;
                var selMatch = SelectedGroupRegex().Match(block);
                if (selMatch.Success && int.TryParse(selMatch.Groups[1].Value, out int grpNum))
                {
                    portToGroup[portNum] = grpNum;
                }

                var descMatch = DescRegex().Match(block);
                if (descMatch.Success)
                {
                    string desc = descMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(desc))
                    {
                        portDescriptions[portNum] = desc;
                    }
                }
            }
        }

        return (portToGroup, portDescriptions);
    }

    private static Dictionary<int, (string Ip, string Host)> ParseActiveConnections(string html)
    {
        var result = new Dictionary<int, (string, string)>();
        var matches = ConnectionRowRegex().Matches(html);
        foreach (Match m in matches)
        {
            string ip = m.Groups[1].Value.Trim();
            if (int.TryParse(m.Groups[2].Value.Trim(), out int grpNum))
            {
                result[grpNum] = (ip, string.Empty);
            }
        }
        return result;
    }

    private static readonly ConcurrentDictionary<string, string> HostCache = new(StringComparer.OrdinalIgnoreCase);

    private async ValueTask<string> ResolveClientHostAsync(string ip, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ip)) return string.Empty;

        // 1. Configured KnownHosts mapping
        if (_options.KnownHosts.TryGetValue(ip, out var knownHost) && !string.IsNullOrWhiteSpace(knownHost))
        {
            return knownHost.ToLowerInvariant();
        }

        // 2. Cache check
        if (HostCache.TryGetValue(ip, out var cached))
        {
            return cached;
        }

        // 3. Fully async DNS PTR lookup with fast 500ms timeout
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(500));

            var hostEntry = await Dns.GetHostEntryAsync(ip, timeoutCts.Token);
            string name = hostEntry.HostName;
            int dot = name.IndexOf('.');
            string shortName = dot > 0 ? name[..dot] : name;
            if (!string.IsNullOrWhiteSpace(shortName))
            {
                string res = shortName.ToLowerInvariant();
                HostCache[ip] = res;
                return res;
            }
        }
        catch
        {
            // Reverse DNS missing in network or timed out
        }

        HostCache[ip] = ip;
        return ip;
    }

    [GeneratedRegex(@"USB Port (\d+).*?(?=USB Port \d+|</table>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PortBlockRegex();

    [GeneratedRegex(@"<OPTION VALUE=[^>]*SELECTED>Group\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SelectedGroupRegex();

    [GeneratedRegex(@"NAME=""description""[^>]*VALUE=""([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DescRegex();

    [GeneratedRegex(@"<td align=left>\s*([\d\.]+)\s*\(Group\s+(\d+)\)</td>", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionRowRegex();

    [GeneratedRegex(@"Ethernet&nbsp;MAC Address:.*?<td class=""field-value"">\s*([0-9A-Fa-f:]+)\s*</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Mac14Regex();

    [GeneratedRegex(@"Product ID:.*?<td class=""field-value"">\s*([^\s<]+)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ProductId14Regex();

    [GeneratedRegex(@"Firmware Version:.*?<td class=""field-value"">\s*([0-9\.]+)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Firmware14Regex();
}
