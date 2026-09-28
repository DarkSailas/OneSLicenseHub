using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneSLicenseHub.Core.Configuration;
using OneSLicenseHub.Core.Models;

namespace OneSLicenseHub.Core.Services;

public interface ISlkInspectorService
{
    ValueTask<List<KeyLicenseInfo>> InspectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Polls a single endpoint; returns <c>null</c> when the server did not answer.</summary>
    ValueTask<List<KeyLicenseInfo>?> InspectEndpointAsync(string serverEndpoint, CancellationToken cancellationToken = default);
}

public sealed partial class SlkInspectorService(
    HttpClient httpClient,
    IOptions<HubOptions> options,
    ILogger<SlkInspectorService> logger) : ISlkInspectorService
{
    private readonly HubOptions _options = options.Value;

    public async ValueTask<List<KeyLicenseInfo>> InspectAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<KeyLicenseInfo>();
        foreach (var serverEndpoint in _options.SlkServers)
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
            var uri = new Uri($"http://{serverEndpoint}/");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

            var html = await httpClient.GetStringAsync(uri, cts.Token);
            return ParseSlkHtml(html, serverEndpoint);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Failed to inspect 1C:СЛК server at {Endpoint}: {Message}", serverEndpoint, ex.Message);
            return null;
        }
    }

    public static List<KeyLicenseInfo> ParseSlkHtml(string html, string serverEndpoint)
    {
        var list = new List<KeyLicenseInfo>();

        foreach (Match sm in SerieBlockRegex().Matches(html))
        {
            string serieId = sm.Groups[1].Value.Trim();
            bool serieDisabled = sm.Groups[2].Value.Contains("disabled", StringComparison.OrdinalIgnoreCase);
            string serieTitle = WebUtility.HtmlDecode(sm.Groups[3].Value.Trim());
            string blockContent = sm.Groups[0].Value;
            var connections = ExtractConnections(blockContent);

            // One <li class="licence ..."> per key; every field is optional (expired keys have no reg. number / KPP)
            foreach (Match lm in LicenceItemRegex().Matches(blockContent))
            {
                string classes = lm.Groups[1].Value;
                string keyNo = lm.Groups[2].Value.Trim();
                string body = lm.Groups[3].Value;

                string regNo = Capture(RegNoRegex(), body);
                string keyType = Capture(KeyTypeRegex(), body);
                string inn = Digits(Capture(ItnRegex(), body));
                string kpp = Digits(Capture(IecRegex(), body));
                string status = Capture(StatusRegex(), body);

                int capacity = 1;
                var capMatch = DigitRegex().Match(keyType);
                if (capMatch.Success && int.TryParse(capMatch.Groups[1].Value, out int cap))
                {
                    capacity = cap;
                }

                bool isActive = !serieDisabled && !classes.Contains("disabled", StringComparison.OrdinalIgnoreCase);
                string organization = string.Join(" / ", new[]
                {
                    string.IsNullOrEmpty(inn) ? "" : $"ИНН: {inn}",
                    string.IsNullOrEmpty(kpp) ? "" : $"КПП: {kpp}"
                }.Where(x => x.Length > 0));

                list.Add(new KeyLicenseInfo
                {
                    Category = "1C:СЛК 3.0",
                    ProductName = serieTitle,
                    ProgramNumber = serieId,
                    DongleId = keyNo,
                    RegistrationNumber = regNo,
                    LicenseType = keyType,
                    TotalCapacity = capacity,
                    Organization = organization,
                    LicenseStatus = status,
                    IsActive = isActive,
                    Medium = classes.Contains("virtual", StringComparison.OrdinalIgnoreCase) ? "Программная" : "Аппаратная",
                    DetailsSummary = $"СЛК Серия {serieId} • Сервер: {serverEndpoint} • {status}",
                    SourceServer = serverEndpoint,
                    ActiveProcesses = isActive ? connections : []
                });
            }
        }

        return list;
    }

    private static string Capture(Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : string.Empty;
    }

    private static string Digits(string text) => new(text.Where(char.IsDigit).ToArray());

    private static List<string> ExtractConnections(string html)
    {
        var processes = new List<string>();
        var matches = ConnectionRegex().Matches(html);
        foreach (Match m in matches)
        {
            string host = m.Groups[1].Value.Trim();
            string ip = m.Groups[2].Value.Trim();
            string desc = m.Groups[3].Value.Trim();
            processes.Add($"{host} ({ip}) - {desc}");
        }
        return processes;
    }

    [GeneratedRegex(@"<li\s+id=""([A-Z0-9]+)""\s+class=""serie\s+li([^""]*)""[^>]*>.*?<h2><span class=""serie-keyid"">[^<]*</span>\s*([^<]+)</h2>(.*?)</li>\s*</ul>\s*</div>\s*</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SerieBlockRegex();

    [GeneratedRegex(@"<li class=""licen[sc]e\b([^""]*)""\s+id=""(\d+)""[^>]*>(.*?)(?=<li class=""licen[sc]e\b|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LicenceItemRegex();

    [GeneratedRegex(@"licen[sc]e-regno"">\((?:рег\.№\s*)?([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex RegNoRegex();

    [GeneratedRegex(@"licen[sc]e-keytype"">([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex KeyTypeRegex();

    [GeneratedRegex(@"licen[sc]e-itn"">([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex ItnRegex();

    [GeneratedRegex(@"licen[sc]e-iec"">([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex IecRegex();

    [GeneratedRegex(@"licen[sc]e-status[^""]*"">([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex StatusRegex();

    [GeneratedRegex(@"(\d+)\s+лиц", RegexOptions.IgnoreCase)]
    private static partial Regex DigitRegex();

    [GeneratedRegex(@"<div class=""tooltiped""><a[^>]*>\\\\([^<]+)</a>.*?\((\d+\.\d+\.\d+\.\d+)\)</div><div>([^<]+)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionRegex();
}
