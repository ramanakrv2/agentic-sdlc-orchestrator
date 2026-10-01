using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.Agents;
using Orchestrator.Core.Engine;
using Orchestrator.Core.Llm;
using Orchestrator.Core.Persistence;
using Orchestrator.Core.Policy;
using Orchestrator.Core.Projects;
using Orchestrator.Infrastructure;
using Orchestrator.Infrastructure.Llm;
using Orchestrator.Infrastructure.Persistence;
using Orchestrator.Infrastructure.Workspace;
using Serilog;
using Serilog.Formatting.Compact;

namespace Orchestrator.Cli;

/// <summary>Composition root: every abstraction is bound here, so swapping a provider is a one-line change.</summary>
public static class Composition
{
    public static IHost Build(CliArgs args)
    {
        var home = OrchestratorPaths.FindHome();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile(Path.Combine(home, "orchestrator.settings.json"), optional: true)
            .AddEnvironmentVariables("ORCH_")
            .AddInMemoryCollection(args.ConfigOverrides());

        var config = builder.Configuration;
        var services = builder.Services;

        // Logging: structured JSON files (operational) — distinct from the hash-chained audit log (governance).
        Directory.CreateDirectory(Path.Combine(home, "logs"));
        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .Enrich.FromLogContext()
            .WriteTo.File(new CompactJsonFormatter(), Path.Combine(home, "logs", "orchestrator-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error)
            .CreateLogger();
        services.AddLogging(l => l.ClearProviders().AddSerilog(dispose: true));

        services.AddSingleton(TimeProvider.System);

        // Policy-as-code.
        var policy = new PolicyEngine(PolicyDocument.FromYaml(File.ReadAllText(Path.Combine(home, "policy.yaml"))));
        services.AddSingleton(policy);
        var engineOptions = config.GetSection("Engine").Get<EngineOptions>() ?? new EngineOptions();
        engineOptions.RetryBudgetPerRun = policy.Document.Limits.RetryBudgetPerRun;
        engineOptions.MaxLoopBacks = policy.Document.Limits.MaxLoopBacks;
        services.AddSingleton(engineOptions);

        // Storage: SQLite by default; the stores depend only on the EF model, so Postgres/SQL Server is a provider swap.
        var provider = config["Storage:Provider"] ?? "Sqlite";
        var dbPath = Path.Combine(home, config["Storage:Path"] ?? ".orchestrator/orchestrator.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        services.AddDbContextFactory<OrchestratorDbContext>(o =>
        {
            if (!provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException($"Storage provider '{provider}' is not wired. Add the EF Core provider package and a Use{provider}() call here.");
            o.UseSqlite($"Data Source={dbPath}");
        });
        services.AddSingleton<DbWriteGate>();
        services.AddSingleton<IRunStore, EfRunStore>();
        services.AddSingleton<IEventStore, EfEventStore>();
        services.AddSingleton<IAuditLog, EfAuditLog>();

        // Workspace + projects.
        services.AddSingleton<IProjectRegistry>(_ => new FileProjectRegistry(Path.Combine(home, "workspace")));
        services.AddSingleton<IRollbackService, WorkspaceRollbackService>();
        services.AddSingleton<IProjectMatcher, LexicalProjectMatcher>();
        services.AddSingleton<IProfileCatalog>(_ => new YamlProfileCatalog(Path.Combine(home, "profiles")));
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // LLM: Semantic Kernel → Ollama (OpenAI-compatible), wrapped in resilience + replay.
        var llmOptions = config.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
        llmOptions.CacheDirectory = Path.Combine(home, llmOptions.CacheDirectory);
        services.AddSingleton(llmOptions);
        services.AddSingleton(new ReplayCache(llmOptions.CacheDirectory));
        services.AddSingleton<IChatModel>(sp => new SemanticKernelChatModel(llmOptions));
        services.AddSingleton<ILlmClient>(sp => new ResilientLlmClient(
            sp.GetRequiredService<IChatModel>(), sp.GetRequiredService<ReplayCache>(), llmOptions,
            sp.GetRequiredService<IEventStore>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ResilientLlmClient>>(), new HttpClient(), sp.GetServices<IRunObserver>()));

        // Human-in-the-loop.
        services.AddSingleton<IApprovalService>(new ConsoleApprovalService(args.Has("yes")));
        services.AddSingleton<IHumanInteraction>(new ConsoleHumanInteraction(args.Presets(), args.Has("yes")));
        services.AddSingleton<IRunObserver, ConsoleRunObserver>();

        services.AddSingleton<WorkflowEngine>();
        services.AddSingleton(new AgentPaths(home, Path.Combine(home, "templates", "webapi")));
        services.AddSingleton<AgentServices>();
        services.AddSingleton(new HomeDirectory(home));

        var host = builder.Build();
        using (var db = host.Services.GetRequiredService<IDbContextFactory<OrchestratorDbContext>>().CreateDbContext())
            db.Database.EnsureCreated();
        return host;
    }
}

public sealed record HomeDirectory(string Path);

/// <summary>Minimal argument parser: command, positionals, --key value, --flag, repeatable --set/--answer.</summary>
public sealed class CliArgs
{
    private static readonly HashSet<string> Flags = ["yes", "new", "verify", "help"];

    public string Command { get; }
    public List<string> Positionals { get; } = [];
    public Dictionary<string, List<string>> Options { get; } = new(StringComparer.OrdinalIgnoreCase);

    public CliArgs(string[] args)
    {
        Command = args.Length > 0 && !args[0].StartsWith("--") ? args[0].ToLowerInvariant() : "help";
        for (var i = Command == "help" && (args.Length == 0 || args[0].StartsWith("--")) ? 0 : 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                string value = "true";
                if (!Flags.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) value = args[++i];
                if (!Options.TryGetValue(key, out var list)) Options[key] = list = [];
                list.Add(value);
            }
            else Positionals.Add(a);
        }
    }

    public bool Has(string key) => Options.ContainsKey(key);
    public string? Get(string key) => Options.TryGetValue(key, out var v) ? v[^1] : null;
    public IEnumerable<string> All(string key) => Options.TryGetValue(key, out var v) ? v : [];

    public Dictionary<string, string?> ConfigOverrides()
    {
        var o = new Dictionary<string, string?>();
        if (Get("llm") is { } mode) o["Llm:Mode"] = mode;
        if (Get("model") is { } model) o["Llm:PrimaryModel"] = model;
        if (Get("fallback-model") is { } fm) o["Llm:FallbackModel"] = fm;
        if (Get("parallel") is { } p) o["Engine:MaxDegreeOfParallelism"] = p;
        if (Get("chaos") is { } chaos)
        {
            foreach (var part in chaos.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=');
                if (kv.Length != 2) continue;
                var key = kv[0].Trim().ToLowerInvariant() switch { "fault" => "FaultRate", "latency" => "LatencyRate", "latencyms" => "LatencyMs", _ => null };
                if (key is not null) o[$"Llm:Chaos:{key}"] = kv[1].Trim();
            }
        }
        return o;
    }

    /// <summary>Preset answers for human questions: --answers file.json and/or --answer id=value.</summary>
    public IReadOnlyDictionary<string, string> Presets()
    {
        var presets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Get("answers") is { } file)
        {
            var path = System.IO.Path.IsPathRooted(file) ? file : System.IO.Path.Combine(OrchestratorPaths.FindHome(), file);
            foreach (var (k, v) in Core.Common.JsonText.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))) presets[k] = v;
        }
        foreach (var a in All("answer"))
        {
            var idx = a.IndexOf('=');
            if (idx > 0) presets[a[..idx]] = a[(idx + 1)..];
        }
        return presets;
    }

    public Dictionary<string, string> RunSettings()
    {
        var s = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { Setting.Project, Setting.Deploy, Setting.Load, Setting.Availability, Setting.ReadWriteRatio, Setting.RetentionDays })
            if (Get(key) is { } v) s[key] = v;
        if (Has("new")) s[Setting.ForceNew] = "true";
        return s;
    }

    public static string Pct(double v) => (v * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";
}
