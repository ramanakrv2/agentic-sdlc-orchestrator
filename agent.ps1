# Convenience wrapper: ./agent.ps1 run "Build a URL shortener..." --yes
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/Orchestrator.Cli/Orchestrator.Cli.csproj'
dotnet run --project $project --no-launch-profile -v q -- @args
exit $LASTEXITCODE
