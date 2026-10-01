# Scenario 3 — AMBIGUOUS: "Make the short links safer."
# The analyst must surface clarifying questions; answers (interactive, or the presets below) are folded back into the spec
# before design. No curated replay entries exist for this scenario: it runs on the live model (Ollama).
param([switch]$Interactive)
$preset = @()
if (-not $Interactive) {
    $preset += @('--yes',
        '--answer', 'route=url-shortener',
        '--answer', 'clarify:A1=Block links that point to private/internal networks and localhost',
        '--answer', 'clarify:A2=Apply to new links only',
        '--answer', 'clarify:A3=Reject with 400 and a clear reason')
}
& "$PSScriptRoot/../agent.ps1" run 'Make the short links safer.' --deploy local --load 1M --availability 99 --llm auto @preset
