using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Models;

namespace OneSLicenseHub.Core.Services;

public interface ISentinelInspectorService
{
    ValueTask<List<KeyLicenseInfo>> InspectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Polls a single endpoint; returns <c>null</c> when the server did not answer.</summary>
    ValueTask<List<KeyLicenseInfo>?> InspectEndpointAsync(string serverEndpoint, CancellationToken cancellationToken = default);
}

public sealed partial class SentinelInspectorService(
    HttpClient httpClient,
    IOptions<HubOptions> options,
    ILogger<SentinelInspectorService> logger) : ISentinelInspectorService
{
    private readonly HubOptions _options = options.Value;

    public async ValueTask<List<KeyLicenseInfo>> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<KeyLicenseInfo>();
        foreach (var serverEndpoint in _options.SentinelServers)
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
            var uri = new Uri($"http://{serverEndpoint}/_int_/tab_feat.html");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var html = await httpClient.GetStringAsync(uri, cts.Token);
            return ParseSentinelFeatures(html, serverEndpoint);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogTrace("Sentinel server at {Endpoint} unreachable: {Message}", serverEndpoint, ex.Message);
            return null;
        }
    }

    public static List<KeyLicenseInfo> ParseSentinelFeatures(string html, string serverEndpoint)
    {
        var list = new List<KeyLicenseInfo>();

        // Each feature is a flat JSON object; parse objects one by one so fields never bleed between features
        foreach (Match block in JsonObjectRegex().Matches(html))
        {
            string obj = block.Value;
            string vendor = Field(obj, "ven");
            string haspId = Field(obj, "haspid");
            string fid = Field(obj, "fid");
            string featureName = Field(obj, "fn");
            string typ = Field(obj, "typ");
            string isLocal = Field(obj, "isloc");

            if (string.IsNullOrEmpty(haspId) || string.IsNullOrEmpty(fid)) continue;
            if (string.IsNullOrEmpty(featureName) && fid == "0") continue;

            bool isDalion = featureName.Equals("Format", StringComparison.OrdinalIgnoreCase) ||
                            vendor.Equals("DWRDA", StringComparison.OrdinalIgnoreCase);

            list.Add(new KeyLicenseInfo
            {
                Category = isDalion ? "Sentinel / Далион" : "Sentinel HASP",
                ProductName = isDalion
                    ? "Далион: Тренд / Управление магазином (Format)"
                    : !string.IsNullOrEmpty(featureName) ? featureName : $"HASP Feature {fid}",
                DongleId = haspId,
                ProgramNumber = fid,
                LicenseType = typ,
                IsLocalKey = isLocal != "0",
                // HASP-SL-* = software licence (SL), HASP-HL / HASP4 = hardware dongle
                Medium = typ.Contains("-SL", StringComparison.OrdinalIgnoreCase) || typ.StartsWith("SL", StringComparison.OrdinalIgnoreCase)
                    ? "Программная"
                    : "Аппаратная",
                DetailsSummary = $"Sentinel HASP ({vendor}) • Фича {fid}: {featureName} • Сервер: {serverEndpoint}",
                SourceServer = serverEndpoint
            });
        }

        return list;
    }

    private static string Field(string jsonObject, string name)
    {
        var m = Regex.Match(jsonObject, $@"""{Regex.Escape(name)}""\s*:\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value.Trim() : string.Empty;
    }

    [GeneratedRegex(@"\{[^{}]*""haspid""[^{}]*\}", RegexOptions.Singleline)]
    private static partial Regex JsonObjectRegex();
}
