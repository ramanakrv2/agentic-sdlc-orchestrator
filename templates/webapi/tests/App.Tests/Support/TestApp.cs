// PLATFORM-OWNED (template-locked). Shared integration-test host for features.
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace App.Tests.Support;

/// <summary>
/// In-memory host over a throw-away SQLite file. Time is controllable through <see cref="Clock"/>.
/// Usage: <c>await using var app = new TestApp(); var client = app.CreateClient();</c>
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"app-tests-{Guid.NewGuid():N}.db");
    private readonly Dictionary<string, string?> _settings;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    public TestApp(IDictionary<string, string?>? settings = null)
    {
        _settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={_dbPath}",
            ["RateLimiting:PermitLimit"] = "100000",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };
        foreach (var (k, v) in settings ?? new Dictionary<string, string?>()) _settings[k] = v;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting (not ConfigureAppConfiguration) so values are visible while Program.cs composes services.
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock)));
    }

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch (IOException) { }
    }
}
