# Scenario 4 — FEASIBILITY GATE: 100M requests/day with 99.9% availability on a laptop.
# The deterministic capacity analyzer blocks it; you choose: switch to Kubernetes / lower load / override (audited) / abort.
$requirement = 'Build a URL shortener service. Users can create a short link for a long URL, optionally choosing a custom alias. Visiting the short link redirects to the original URL. Provide click analytics per link: total clicks, clicks per day and top referrers. It must stay fast and reliable under load.'
& "$PSScriptRoot/../agent.ps1" run $requirement --new --deploy local --load 100M --availability 99.9 --llm replay @args
