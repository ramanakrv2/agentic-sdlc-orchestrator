# Scenario 1 — GREENFIELD: a new URL shortener from a plain-English requirement.
# Deterministic by default (--llm replay uses curated responses in llm-cache/golden.json). Use -Llm live for Ollama, -Interactive to approve yourself.
param([string]$Llm = 'replay', [switch]$Interactive)
$requirement = 'Build a URL shortener service. Users can create a short link for a long URL, optionally choosing a custom alias. Visiting the short link redirects to the original URL. Provide click analytics per link: total clicks, clicks per day and top referrers. It must stay fast and reliable under load.'
$yes = @(); if (-not $Interactive) { $yes += '--yes' }
& "$PSScriptRoot/../agent.ps1" run $requirement --new --deploy local --load 1M --availability 99 --llm $Llm @yes
