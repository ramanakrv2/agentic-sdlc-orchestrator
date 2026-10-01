# Scenario 2 — BROWNFIELD: change the existing project (run scenario 1 first).
# No --project flag: the router finds the matching project from workspace/index.json + LLM re-rank, then asks to confirm.
param([string]$Llm = 'replay', [switch]$Interactive)
$requirement = 'Add link expiration to the URL shortener: when creating a short link the caller may set an optional expiry in minutes. Once a link has expired it must no longer redirect and should return 410 Gone, and the link metadata should show when it expires.'
$yes = @(); if (-not $Interactive) { $yes += '--yes' }
& "$PSScriptRoot/../agent.ps1" run $requirement --deploy local --load 1M --availability 99 --llm $Llm @yes
