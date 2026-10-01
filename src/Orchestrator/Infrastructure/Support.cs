using System.Diagnostics;
using System.Text;
using Orchestrator.Core.Capacity;

namespace Orchestrator.Infrastructure;

/// <summary>Locates the repository home (folder containing policy.yaml) so the CLI works from any sub-directory.</summary>
public static class OrchestratorPaths
{
    public static string FindHome(string? start = null)
    {
        var dir = new DirectoryInfo(start ?? Directory.GetCurrentDirectory());
        for (var d = dir; d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "policy.yaml"))) return d.FullName;
        // Fall back to the binary location (dotnet run from the project folder).
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "policy.yaml"))) return d.FullName;
        return dir.FullName;
    }
}

public interface IProfileCatalog
{
    IReadOnlyList<DeploymentProfile> All { get; }
    DeploymentProfile Get(string id);
}

public sealed class YamlProfileCatalog : IProfileCatalog
{
    public YamlProfileCatalog(string directory)
    {
        All = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.yaml").Select(f => DeploymentProfile.FromYaml(File.ReadAllText(f))).OrderBy(p => p.Rank).ToList()
            : [];
    }

    public IReadOnlyList<DeploymentProfile> All { get; }

    public DeploymentProfile Get(string id) =>
        All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"Unknown deployment profile '{id}'. Known: {string.Join(", ", All.Select(p => p.Id))}");
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public string Combined => StdOut + Environment.NewLine + StdErr;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), true);
        }
    }
}
