using Microsoft.AspNetCore.Http.HttpResults;
using OneSLicenseHub.Core;
using OneSLicenseHub.Core.Models;
using OneSLicenseHub.Core.Services;
using Serilog;

var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
};
var builder = WebApplication.CreateBuilder(options);
builder.WebHost.UseUrls("http://*:5075");

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File("logs/licenses-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

builder.Host.UseSerilog();
builder.Host.UseWindowsService();

// Add Core Services
builder.Services.AddLicenseHubCore(builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseDefaultFiles();
// Revalidate UI assets on every load (ETag), so an update never mixes new HTML with cached CSS/JS
var noCacheStaticFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
};
app.UseStaticFiles(noCacheStaticFiles);

// Minimal API endpoints
var api = app.MapGroup("/api");

api.MapGet("/overview", async ValueTask<Ok<UnifiedLicenseOverview>> (
    ILicenseHubAggregator aggregator,
    CancellationToken ct) =>
{
    var overview = await aggregator.GetOverviewAsync(forceRefresh: false, ct);
    return TypedResults.Ok(overview);
})
.WithName("GetOverview");

api.MapPost("/overview/refresh", async ValueTask<Ok<UnifiedLicenseOverview>> (
    ILicenseHubAggregator aggregator,
    CancellationToken ct) =>
{
    var overview = await aggregator.GetOverviewAsync(forceRefresh: true, ct);
    return TypedResults.Ok(overview);
})
.WithName("RefreshOverview");

api.MapGet("/devices/{id}", async ValueTask<Results<Ok<DigiDeviceModel>, NotFound>> (
    string id,
    ILicenseHubAggregator aggregator,
    CancellationToken ct) =>
{
    var overview = await aggregator.GetOverviewAsync(forceRefresh: false, ct);
    var device = overview.Devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    return device is not null ? TypedResults.Ok(device) : TypedResults.NotFound();
})
.WithName("GetDeviceById");

api.MapGet("/settings", (ILicenseHubAggregator aggregator) =>
{
    return Results.Ok(new
    {
        pollingIntervalMinutes = aggregator.PollingIntervalMinutes,
        lastRefreshTime = aggregator.LastRefreshTime,
        nextScheduledRefresh = aggregator.NextScheduledRefresh
    });
})
.WithName("GetSettings");

api.MapPost("/settings", (UpdateSettingsRequest request, ILicenseHubAggregator aggregator) =>
{
    if (request.PollingIntervalMinutes is >= 1 and <= 1440)
    {
        aggregator.PollingIntervalMinutes = request.PollingIntervalMinutes.Value;
        Log.Information("Polling interval updated to {Interval} minutes via API", aggregator.PollingIntervalMinutes);
    }

    return Results.Ok(new
    {
        pollingIntervalMinutes = aggregator.PollingIntervalMinutes,
        lastRefreshTime = aggregator.LastRefreshTime,
        nextScheduledRefresh = aggregator.NextScheduledRefresh
    });
})
.WithName("UpdateSettings");

app.MapGet("/health", () => Results.Ok(new
{
    status = "Healthy",
    timestamp = DateTime.UtcNow,
    runtime = Environment.Version.ToString()
}));

app.MapFallbackToFile("index.html", noCacheStaticFiles);

try
{
    Log.Information("Starting OneSLicenseHub.Web server...");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

public record UpdateSettingsRequest(int? PollingIntervalMinutes);
