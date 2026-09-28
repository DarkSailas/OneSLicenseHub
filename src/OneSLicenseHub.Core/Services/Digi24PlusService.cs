using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Models;

namespace OneSLicenseHub.Core.Services;

public interface IDigi24PlusService
{
    ValueTask<DigiDeviceModel> GetStatusAsync(CancellationToken cancellationToken = default);
}

public sealed partial class Digi24PlusService(
    HttpClient httpClient,
    IOptions<HubOptions> options,
    ILogger<Digi24PlusService> logger) : IDigi24PlusService
{
    private readonly DigiDeviceConfig _config = options.Value.Digi1;
    private List<DigiPortModel>? _lastKnownPorts;

    public async ValueTask<DigiDeviceModel> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var model = new DigiDeviceModel
        {
            Id = _config.Id,
            Name = _config.Name,
            IpAddress = _config.Host,
            TotalPorts = 24,
            Model = "AnywhereUSB 24 Plus",
            Status = "Degraded"
        };

        try
        {
            var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_config.Username}:{_config.Password}"));
            var baseUri = new Uri($"https://{_config.Host}:{_config.Port}");

            // 1. Fetch config data to obtain exact port -> group -> client mappings
            var configReq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/awusb/?view=config"));
            configReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
            var configResp = await httpClient.SendAsync(configReq, cancellationToken);
            configResp.EnsureSuccessStatusCode();
            var configHtml = await configResp.Content.ReadAsStringAsync(cancellationToken);

            var (portToGroup, groupToClient, clientToIp) = ParseConfigData(configHtml);
            if (portToGroup.Count == 0)
            {
                throw new InvalidOperationException($"Digi 24 returned empty configuration (parsed 0 ports, HTML length: {configHtml.Length}).");
            }

            // 2. Fetch live status page
            var statusReq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/awusb/?view=status"));
            statusReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
            var statusResp = await httpClient.SendAsync(statusReq, cancellationToken);
            statusResp.EnsureSuccessStatusCode();
            var statusHtml = await statusResp.Content.ReadAsStringAsync(cancellationToken);

            var devicePorts = ParseStatusDevices(statusHtml);
            var activeGroups = ParseGroupsInUse(statusHtml);

            var macMatch = MacRegex().Match(statusHtml);
            if (macMatch.Success)
            {
                model.MacAddress = macMatch.Groups[1].Value.Trim();
            }

            // 3. Fetch device hardware telemetry from /status/
            try
            {
                var sysReq = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "/status/"));
                sysReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
                var sysResp = await httpClient.SendAsync(sysReq, cancellationToken);
                if (sysResp.IsSuccessStatusCode)
                {
                    var sysHtml = await sysResp.Content.ReadAsStringAsync(cancellationToken);

                    var mac24Match = Mac24StatusRegex().Match(sysHtml);
                    if (mac24Match.Success) model.MacAddress = mac24Match.Groups[1].Value.Trim();

                    var devIdMatch = DeviceId24Regex().Match(sysHtml);
                    if (devIdMatch.Success) model.DeviceGuid = devIdMatch.Groups[1].Value.Trim();

                    var snMatch = SerialNumber24Regex().Match(sysHtml);
                    if (snMatch.Success) model.SerialNumber = snMatch.Groups[1].Value.Trim();

                    var fwMatch = Firmware24Regex().Match(sysHtml);
                    if (fwMatch.Success) model.Firmware = fwMatch.Groups[1].Value.Trim();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch Digi 24 /status/ telemetry: {Message}", ex.Message);
            }

            model.Port = _config.Port;
            model.Protocol = _config.UseHttps ? "HTTPS" : "HTTP";
            if (string.IsNullOrEmpty(model.DeviceGuid) && !string.IsNullOrEmpty(model.MacAddress))
            {
                model.DeviceGuid = DigiDeviceModel.FormatDigiDeviceGuid(model.MacAddress);
            }

            // Build all 24 ports
            var ports = new List<DigiPortModel>(24);
            for (int p = 1; p <= 24; p++)
            {
                portToGroup.TryGetValue(p, out int grpNum);
                groupToClient.TryGetValue(grpNum, out string? clientHost);
                clientHost ??= string.Empty;

                activeGroups.TryGetValue(grpNum, out var activeInfo);
                string clientIp = activeInfo.Ip;
                if (string.IsNullOrEmpty(clientIp) && !string.IsNullOrEmpty(clientHost) && clientToIp.TryGetValue(clientHost, out var cfgIp))
                {
                    clientIp = cfgIp;
                }

                bool hasDevice = devicePorts.TryGetValue(p, out var dev);
                string status = "Available";
                if (hasDevice)
                {
                    status = !string.IsNullOrEmpty(activeInfo.Ip) ? "InUse" : "Connected";
                }
                else if (grpNum == 0)
                {
                    status = "Unassigned";
                }

                ports.Add(new DigiPortModel
                {
                    DeviceId = model.Id,
                    DeviceName = model.Name,
                    PortNumber = p,
                    GroupName = grpNum > 0 ? $"Group {grpNum}" : "Unassigned",
                    GroupNumber = grpNum > 0 ? grpNum : null,
                    HasDevice = hasDevice,
                    Status = status,
                    ConnectedClientHost = !string.IsNullOrEmpty(activeInfo.Client) ? activeInfo.Client : clientHost,
                    ConnectedClientIp = clientIp,
                    UsbVersion = hasDevice ? dev.UsbVersion : string.Empty,
                    HardwareManufacturer = hasDevice ? dev.Manufacturer : string.Empty,
                    HardwareProduct = hasDevice ? dev.Product : string.Empty,
                    SerialNumber = hasDevice ? dev.Serial : string.Empty
                });
            }

            _lastKnownPorts = ports;
            model.Ports = ports;
            model.Status = "Online";
            model.LastCheckTime = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to poll Digi 24 Plus ({Host}): {Message}", _config.Host, ex.Message);
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

    private static (Dictionary<int, int> portToGroup, Dictionary<int, string> groupToClient, Dictionary<string, string> clientToIp) ParseConfigData(string html)
    {
        var portToGroup = new Dictionary<int, int>();
        var groupToClient = new Dictionary<int, string>();
        var clientToIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var preMatch = PreConfigRegex().Match(html);
        if (!preMatch.Success)
        {
            return (portToGroup, groupToClient, clientToIp);
        }

        var lines = preMatch.Groups[1].Value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var kv = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                kv[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        // groups.group01.ports.0=1
        for (int g = 1; g <= 24; g++)
        {
            string gKey = g < 10 ? $"groups.group0{g}" : $"groups.group{g}";
            for (int pi = 0; pi < 24; pi++)
            {
                if (kv.TryGetValue($"{gKey}.ports.{pi}", out var pStr) && int.TryParse(pStr, out int portNum))
                {
                    portToGroup[portNum] = g;
                }
            }
        }

        // clients.0.id=app-server-02, clients.0.groups.0=group02
        for (int c = 0; c < 50; c++)
        {
            if (kv.TryGetValue($"clients.{c}.id", out var clientId))
            {
                for (int gi = 0; gi < 24; gi++)
                {
                    if (kv.TryGetValue($"clients.{c}.groups.{gi}", out var grpStr))
                    {
                        var m = GroupNumberRegex().Match(grpStr);
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int gn))
                        {
                            groupToClient[gn] = clientId;
                        }
                    }
                }
            }
        }

        return (portToGroup, groupToClient, clientToIp);
    }

    private static Dictionary<int, (string UsbVersion, string Manufacturer, string Product, string Serial)> ParseStatusDevices(string html)
    {
        var result = new Dictionary<int, (string, string, string, string)>();
        var rows = UsbDeviceRowRegex().Matches(html);
        foreach (Match row in rows)
        {
            if (int.TryParse(row.Groups[1].Value.Trim(), out int portNum))
            {
                string usbVer = row.Groups[2].Value.Trim();
                string man = row.Groups[3].Value.Trim();
                string prod = row.Groups[4].Value.Trim();
                string serial = row.Groups[5].Value.Trim();
                result[portNum] = (usbVer, man, prod, serial);
            }
        }
        return result;
    }

    private static Dictionary<int, (string Client, string Ip)> ParseGroupsInUse(string html)
    {
        var result = new Dictionary<int, (string, string)>();
        var rows = GroupInUseRowRegex().Matches(html);
        foreach (Match row in rows)
        {
            if (int.TryParse(row.Groups[1].Value.Trim(), out int groupNum))
            {
                string client = row.Groups[2].Value.Trim();
                string ip = row.Groups[3].Value.Trim();
                result[groupNum] = (client, ip);
            }
        }
        return result;
    }

    [GeneratedRegex(@"<span id='index_mac'>([^<]+)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex MacRegex();

    [GeneratedRegex(@"<pre id=""config_data""[^>]*>(.*?)</pre>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex PreConfigRegex();

    [GeneratedRegex(@"group0?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GroupNumberRegex();

    [GeneratedRegex(@"<tr>\s*<td>(\d+)</td>\s*<td>Group\s+\d+</td>\s*<td>([^<]*)</td>\s*<td>([^<]*)</td>\s*<td>([^<]*)</td>\s*<td>([^<]*)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex UsbDeviceRowRegex();

    [GeneratedRegex(@"<tr>\s*<td>Group\s+(\d+)</td>\s*<td>([^<]*)</td>\s*<td>([^<]*)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex GroupInUseRowRegex();

    [GeneratedRegex(@">Device Id</div>\s*<div[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceId24Regex();

    [GeneratedRegex(@">Serial Number</div>\s*<div[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase)]
    private static partial Regex SerialNumber24Regex();

    [GeneratedRegex(@">Firmware Version</div>\s*<div[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase)]
    private static partial Regex Firmware24Regex();

    [GeneratedRegex(@">MAC</div>\s*<div[^>]*>([^<]+)</div>", RegexOptions.IgnoreCase)]
    private static partial Regex Mac24StatusRegex();
}
