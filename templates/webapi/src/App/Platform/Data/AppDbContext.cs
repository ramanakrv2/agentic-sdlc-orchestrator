using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace App.Platform.Data;

// PLATFORM-OWNED (template-locked).

/// <summary>
/// Single DbContext. Features contribute entities by adding <see cref="IEntityTypeConfiguration{TEntity}"/> classes
/// (picked up automatically) and access them with <c>db.Set&lt;T&gt;()</c> inside repositories.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.Entity<SchemaInfo>(e =>
        {
            e.ToTable("__schema_info");
            e.HasKey(x => x.Id);
        });
    }
}

public sealed class SchemaInfo
{
    public int Id { get; set; }
    public string ModelHash { get; set; } = "";
}

/// <summary>Retries transient database failures (SQLite busy/locked, Postgres transient errors).</summary>
public interface IDbExecutor
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default);
    Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default);
}

public sealed class ResilientDbExecutor : IDbExecutor
{
    private readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(50),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
        })
        .Build();

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct = default) =>
        await _pipeline.ExecuteAsync(async token => await operation(token), ct);

    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken ct = default) =>
        await _pipeline.ExecuteAsync(async token => await operation(token), ct);

    public static bool IsTransient(Exception ex) => ex switch
    {
        Microsoft.Data.Sqlite.SqliteException s => s.SqliteErrorCode is 5 or 6, // SQLITE_BUSY, SQLITE_LOCKED
        DbException db => db.IsTransient,
        DbUpdateException { InnerException: { } inner } => IsTransient(inner),
        TimeoutException => true,
        _ => false,
    };
}

/// <summary>Chaos: adds latency to every DB command to demonstrate request timeouts.</summary>
public sealed class ChaosDbInterceptor(IOptions<ChaosOptions> chaos) : DbCommandInterceptor
{
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (chaos.Value.DbLatencyMs > 0) await Task.Delay(chaos.Value.DbLatencyMs, cancellationToken);
        return result;
    }
}

public sealed class DbHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Database reachable")
            : HealthCheckResult.Unhealthy("Database unreachable");
}

public static class DatabaseSetup
{
    public static IServiceCollection AddPlatformDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddSingleton<ChaosDbInterceptor>();
        services.AddSingleton<IDbExecutor, ResilientDbExecutor>();
        services.AddDbContext<AppDbContext>((sp, db) =>
        {
            switch (options.Provider.ToLowerInvariant())
            {
                case "sqlite":
                    EnsureSqliteFolder(options.ConnectionString);
                    db.UseSqlite(options.ConnectionString);
                    break;
                case "postgres":
                case "postgresql":
                    db.UseNpgsql(options.ConnectionString, npgsql => npgsql.EnableRetryOnFailure(3));
                    break;
                default:
                    throw new NotSupportedException(
                        $"Database provider '{options.Provider}' is not configured. Supported: Sqlite, Postgres. " +
                        "To add SQL Server: reference Microsoft.EntityFrameworkCore.SqlServer and add a case calling UseSqlServer().");
            }
            db.AddInterceptors(sp.GetRequiredService<ChaosDbInterceptor>());
        });
        return services;
    }

    /// <summary>
    /// Creates the schema. SQLite runs in WAL mode (concurrent readers with one writer).
    /// Development-only: when the EF model changes the database is recreated; production would use EF migrations.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Platform.Database");
        var modelHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(db.Model.ToDebugString())))[..16];

        var created = await db.Database.EnsureCreatedAsync();
        if (!created && options.ResetOnModelChange)
        {
            string? existing = null;
            try { existing = (await db.Set<SchemaInfo>().OrderBy(s => s.Id).FirstOrDefaultAsync())?.ModelHash; }
            catch (Exception) { /* table missing → treat as changed */ }

            if (existing != modelHash)
            {
                logger.LogWarning("EF model changed ({Old} → {New}); recreating development database.", existing ?? "none", modelHash);
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                created = true;
            }
        }

        if (created)
        {
            db.Set<SchemaInfo>().Add(new SchemaInfo { Id = 1, ModelHash = modelHash });
            await db.SaveChangesAsync();
        }

        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }

    private static void EnsureSqliteFolder(string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        var dir = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(dir) && builder.DataSource != ":memory:") Directory.CreateDirectory(dir);
    }
}
