using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Models;

namespace OneSLicenseHub.Core.Services;

public interface IGuardantInspectorService
{
    ValueTask<List<KeyLicenseInfo>> InspectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Polls a single endpoint; returns <c>null</c> when the server did not answer.</summary>
    ValueTask<List<KeyLicenseInfo>?> InspectEndpointAsync(string serverEndpoint, CancellationToken cancellationToken = default);
}

public sealed partial class GuardantInspectorService(
    HttpClient httpClient,
    IOptions<HubOptions> options,
    ILogger<GuardantInspectorService> logger) : IGuardantInspectorService
{
    private readonly HubOptions _options = options.Value;

    public async ValueTask<List<KeyLicenseInfo>> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<KeyLicenseInfo>();
        foreach (var serverEndpoint in _options.GuardantServers)
        {
            var licenses = await InspectEndpointAsync(serverEndpoint, cancellationToken);
            if (licenses is not null) results.AddRange(licenses);
        }

        return results;
    }

    public async ValueTask<List<KeyLicenseInfo>?> InspectEndpointAsync(string serverEndpoint, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = new Uri($"http://{serverEndpoint}/monitor.htm");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var html = await httpClient.GetStringAsync(uri, cts.Token);
            return ParseGuardantHtml(html, serverEndpoint);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogTrace("Guardant server at {Endpoint} unreachable or inactive: {Message}", serverEndpoint, ex.Message);
            return null;
        }
    }

    public static List<KeyLicenseInfo> ParseGuardantHtml(string html, string serverEndpoint)
    {
        var list = new List<KeyLicenseInfo>();

        // Extract program code e.g. YUVERS+ directly from server HTML
        string programName = "Guardant Net";
        var codeMatch = CodeRegex().Match(html);
        if (codeMatch.Success)
        {
            string codeVal = codeMatch.Groups[1].Value.Trim();
            programName = codeVal.Contains("YUVERS", StringComparison.OrdinalIgnoreCase)
                ? "Ювелирсофт (YUVERS+)"
                : $"Guardant ({codeVal})";
        }

        // Extract each dongle
        var dongleMatches = DongleBlockRegex().Matches(html);
        foreach (Match dm in dongleMatches)
        {
            string dongleIdHex = dm.Groups[1].Value.Trim();
            string resourceText = dm.Groups[2].Value.Trim();
            string block = dm.Groups[0].Value;

            int capacity = 0;
            var capMatch = ResourceRegex().Match(resourceText);
            if (capMatch.Success && int.TryParse(capMatch.Groups[2].Value, out int capVal))
            {
                capacity = capVal;
            }

            var clients = new List<string>();
            var clientMatches = ClientRegex().Matches(block);
            foreach (Match cm in clientMatches)
            {
                clients.Add(cm.Groups[1].Value.Trim());
            }

            list.Add(new KeyLicenseInfo
            {
                Category = "Guardant / Отраслевой",
                ProductName = programName,
                DongleId = dongleIdHex,
                LicenseType = "Сетевой ключ защиты (GLDS)",
                TotalCapacity = capacity,
                InUseCount = clients.Count,
                DetailsSummary = $"Guardant Net II • Хост: {serverEndpoint} • Лицензий: {capacity} (занято: {clients.Count})",
                ActiveProcesses = clients,
                Medium = "Аппаратная",
                SourceServer = serverEndpoint
            });
        }

        return list;
    }

    [GeneratedRegex(@"objparams\.htm\?code=\d+""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRegex();

    // A dongle block ends at the next dongle header, whatever its icon (usbdong.gif, or nousbdon.gif when all seats are taken)
    [GeneratedRegex(@"objparams\.htm\?dongle_id=\d+""[^>]*>Guardant[^<(]*\((0x[0-9A-Fa-f]+)\)</a>.*?Общий ресурс ключа\s*\(([^)]+)\)(.*?)(?=objparams\.htm\?dongle_id=\d+""[^>]*>Guardant|</body>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DongleBlockRegex();

    [GeneratedRegex(@"(\d+)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ResourceRegex();

    [GeneratedRegex(@"objparams\.htm\?lic_id=\d+""[^>]*>([^<]+)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex ClientRegex();
}
