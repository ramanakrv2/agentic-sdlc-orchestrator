using System.Globalization;
using System.Text;
using Orchestrator.Cli;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;
// Prompts must be byte-identical on every machine (replay keys are prompt hashes): never format with the local culture.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var cli = new CliArgs(args);

if (cli.Command is "help" or "--help" || cli.Has("help"))
{
    Commands.PrintHelp();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C = safe stop (persist + rollback + resumable). Second Ctrl+C = hard exit.
    if (cts.IsCancellationRequested) return;
    e.Cancel = true;
    AnsiConsole.MarkupLine("[yellow]Safe-stop requested — finishing in-flight work and persisting state…[/]");
    cts.Cancel();
};

try
{
    using var host = Composition.Build(cli);
    var commands = new Commands(host.Services, cli);
    return cli.Command switch
    {
        "run" => await commands.RunAsync(cts.Token),
        "resume" => await commands.ResumeAsync(cts.Token),
        "revise" => await commands.ReviseAsync(cts.Token),
        "status" => await commands.StatusAsync(cts.Token),
        "metrics" => await commands.MetricsAsync(cts.Token),
        "audit" => await commands.AuditAsync(cts.Token),
        "lineage" => await commands.LineageAsync(cts.Token),
        "projects" => await commands.ProjectsAsync(cts.Token),
        "doctor" => await commands.DoctorAsync(cts.Token),
        "fixtures" => await commands.FixturesAsync(cts.Token),
        _ => Commands.Unknown(cli.Command),
    };
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
    return 1;
}
finally
{
    Serilog.Log.CloseAndFlush();
}
