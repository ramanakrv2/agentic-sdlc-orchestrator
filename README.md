# Agentic SDLC Orchestrator (.NET 10) — URL Shortener case study

A governed, agentic software-delivery system: give it a requirement in plain English and it runs the SDLC —
**route → requirements → NFR/feasibility → design → plan → parallel implementation/tests/docs/deploy → validation → review → release** —
as an explicit dependency graph with gates, human approvals, retries, fallback, rollback, safe-stop, policy guardrails,
a tamper-evident audit trail and reliability metrics. The first requirement it builds is a URL shortener; later requirements
either create new projects (greenfield) or change existing ones (brownfield).

Everything is open source and runs locally: **Semantic Kernel** agents → **Ollama** (local LLM), **EF Core + SQLite** state,
**Polly** resilience, **Serilog** logging, **Spectre.Console** CLI. No paid services.

> Architecture, orchestration model and decisions: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
>
> Generated output to browse without running anything: [samples/url-shortener/](samples/url-shortener/) — the service after scenario 1 (v1) + scenario 2 (v2, link expiration), including `HISTORY.md`, `CHANGELOG.md`, `docs/`, `deploy/` and the per-run evidence packs in `.runs/`. (`.versions/` snapshots omitted.)

---

## 1. Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0.100+ (`winget install Microsoft.DotNet.SDK.10`) | everything (orchestrator, generated services, tests) |
| Ollama | latest (`winget install Ollama.Ollama`) | live LLM runs (replay mode works without it) |
| Internet (first build) | — | NuGet restore |

Ollama setup (one-time, any terminal):
```powershell
setx OLLAMA_CONTEXT_LENGTH 8192     # default context silently truncates our prompts
setx OLLAMA_KEEP_ALIVE 30m          # keep the model loaded between agent stages
# restart Ollama (tray icon → Quit, start again), then in a NEW terminal:
ollama pull qwen2.5-coder:3b        # primary model (~1.9 GB)
ollama pull qwen2.5-coder:1.5b      # fallback model (~1.0 GB)
```

### VS Code extensions
| Extension | ID | |
|---|---|---|
| C# Dev Kit | `ms-dotnettools.csdevkit` | required (build, debug, Test Explorer) |
| C# | `ms-dotnettools.csharp` | required (installed with Dev Kit) |
| YAML | `redhat.vscode-yaml` | recommended (`policy.yaml`, `profiles/*.yaml`) |
| REST Client | `humao.rest-client` | recommended (generated `app.http` files) |
| Markdown Preview Mermaid | `bierner.markdown-mermaid` | recommended (diagrams in docs) |

`.vscode/extensions.json` prompts for these automatically.

## 2. Setup & check
```powershell
dotnet build AgenticSdlc.slnx
./agent.ps1 doctor          # checks .NET, policy, template, profiles, replay cache, Ollama, models, context length
```
(`agent.ps1` is a thin wrapper around `dotnet run --project src/Orchestrator.Cli --`.)

## 3. Run the scenarios
Run from the repository root, in order (scenario 2 changes the project scenario 1 creates).

| # | Scenario | Command | What it demonstrates |
|---|---|---|---|
| 1 | **Greenfield** | `./scenarios/01-greenfield.ps1` | spec → feasibility (1M/day on local = feasible) → design/ADRs → 13-task DAG with parallel branches → build/tests/coverage gate → review → release v1 |
| 2 | **Brownfield** | `./scenarios/02-brownfield.ps1` | project routing (card index + LLM re-rank + human confirm), checkpoint, impact analysis, **schema-change approval**, 6 modify/create tasks, regression + new tests, release v2, `HISTORY.md` |
| 3 | **Ambiguous** | `./scenarios/03-ambiguous.ps1` | "Make the short links safer." → clarifying questions → answers folded into the spec → re-plan (live model; see limitations) |
| 4 | **Infeasible NFR** | `./scenarios/04-infeasible.ps1` | 100M/day @ 99.9% on a laptop → **NOT FEASIBLE** gate → choose switch to Kubernetes / lower / override (audited) / abort. In replay mode the run then safe-stops at design (no curated Kubernetes design) and rolls back — itself a demo of safe-stop; with `--llm live` it continues and generates statically-checked K8s manifests |

Scenarios 1, 2 and 4 default to `--llm replay` (curated, human-reviewed responses in `llm-cache/golden.json`) so they are fast
(~30 s, dominated by the real `dotnet build`/`dotnet test` of the generated code) and deterministic. Add `-Llm live` to use Ollama,
and `-Interactive` to answer approvals/questions yourself instead of `--yes`.

Then inspect:
```powershell
./agent.ps1 status                       # runs
./agent.ps1 status <runId>               # node states, attempts, artifacts (hash + producer)
./agent.ps1 metrics                      # success rate, retries, loop-backs, rollbacks, MTTR, e2e latency, per-stage
./agent.ps1 audit --verify               # verify the hash-chained audit trail
./agent.ps1 audit --run <runId>          # who/what/why, approvals, decisions
./agent.ps1 lineage <runId> design       # decision lineage of an artifact
./agent.ps1 projects                     # project cards (workspace/index.json)
dotnet run --project workspace/url-shortener/src/App      # the generated service on http://localhost:5080
```
Each release leaves an evidence pack in `workspace/<slug>/.runs/<runId>/` (spec, capacity report, impact, design, plan,
validation, review, NFR traceability, release notes) and a snapshot in `workspace/<slug>/.versions/vN/`.

Other useful commands:
```powershell
./agent.ps1 run "Build a todo API with due dates" --deploy local --load 100k --llm live      # any new requirement
./agent.ps1 run "..." --chaos fault=0.4,latency=0.2 --llm live      # inject LLM faults → retries, circuit breaker, model fallback
./agent.ps1 resume <runId>                                          # continue a safe-stopped run (Ctrl+C also safe-stops)
./agent.ps1 revise <runId> --node nfr --set deploy=kubernetes       # change an upstream input → re-plan downstream on resume
```

## 4. Workspace layout (one folder per base requirement)
```
workspace/
  index.json                 derived index of all project cards (routing reads only this)
  <slug>/
    project.json             card: summary, aliases, capabilities, entities, endpoints (from code), module map, versions, source hash
    HISTORY.md               human-readable version history
    CHANGELOG.md
    src/App, tests/App.Tests generated .NET 10 service (platform kit + feature code)
    deploy/                  profile-specific deployment artifacts
    .versions/v1, v2 …       snapshots (rollback targets); pre-<runId> checkpoints
    .runs/<runId>/           evidence pack per run
```

## 5. Testing approach
| Suite | Command | Covers |
|---|---|---|
| Orchestrator (81 tests) | `dotnet test --project tests/Orchestrator.Tests` | DAG validation; ordering, parallelism + join; entry/exit gates; retries with feedback; fallback; retry budget; approvals (approve/reject/revise); forbidden actions; validator loop-back; dynamic expansion; re-planning; resume; Ctrl+C safe-stop; audit chain tamper detection; metrics math; policy/conventions; feasibility; routing; Polly retry/circuit breaker/model fallback/replay; EF stores; snapshots/rollback; plan/code/deploy gates |
| Generated service | runs inside the **validation gate** of every run (`dotnet build` + `dotnet test` + coverlet coverage ≥ 70%) | template platform tests (health, rate limit, cache circuit breaker, queue load shedding) + agent-written unit/integration tests (WebApplicationFactory, FakeTimeProvider, concurrency) |

Libraries: xUnit v3 (Microsoft Testing Platform), Shouldly, NSubstitute, Microsoft.Extensions.TimeProvider.Testing, coverlet.MTP —
FluentAssertions v8 and Moq avoided (licensing), Testcontainers avoided (needs Docker).
Tip: if `dotnet test` output is piped (e.g. `| Select-String`) the MTP runner may exit with code 35; redirect to a file instead.

## 6. Packages (all open source)
| Package | License | Purpose |
|---|---|---|
| Microsoft.SemanticKernel | MIT | agent LLM calls (OpenAI connector → Ollama's OpenAI-compatible API) |
| Polly.Core | BSD-3 | timeout, retry, circuit breaker, chaos (orchestrator LLM calls + generated service) |
| Microsoft.EntityFrameworkCore.Sqlite | MIT | orchestrator state/events/audit; generated service persistence |
| Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL | generated service: `Database:Provider=Postgres` |
| Microsoft.Extensions.Caching.StackExchangeRedis | MIT | generated service: `Cache:Provider=Redis` |
| Serilog (+ Hosting, Settings.Configuration, Sinks.Console/File, Formatting.Compact, AspNetCore) | Apache-2.0 | structured logging |
| Spectre.Console | MIT | CLI, approval prompts |
| YamlDotNet | MIT | policy and deployment profiles |
| Microsoft.Extensions.Hosting | MIT | composition root / configuration |
| xunit.v3, Shouldly, NSubstitute, coverlet.MTP, Microsoft.AspNetCore.Mvc.Testing, Microsoft.Extensions.TimeProvider.Testing | Apache-2.0 / BSD / MIT | tests |

## 7. Limitations & trade-offs (honest list)
- **Local 3B model quality.** `qwen2.5-coder:3b` on CPU writes plausible specs but frequently non-compiling multi-file C#, and is slow
  (design ≈ 8 min, each code file several minutes). The gates catch this (see `docs/evidence-live-run-qwen3b.log`: the plan gate rejected
  the model's plan and the retry with feedback fixed it). For a dependable demo the scenarios use **curated replay entries**: responses to the
  exact prompts, authored with AI assistance and human-reviewed, stored per prompt hash. Replay is a fallback link in the chain, not a bypass —
  the same gates (policy, build, tests, coverage, review, approvals) run on replayed output. Point `Llm:PrimaryModel` at a bigger model
  (or any OpenAI-compatible endpoint) for better live results.
- **Scenario 3 has no curated responses** (its answers drive the spec, so many prompt variants exist); it needs Ollama and its outcome depends on the model.
- **Replay keys are prompt hashes**: changing a prompt template, the platform doc or a profile invalidates affected entries (they fall back to live).
- **Deployment artifacts** for kubernetes are generated and statically validated, not deployed (no Docker/cluster required).
- **Development database** of generated services is recreated when the EF model changes; production would use EF migrations (called out as a release risk).
- **Approvals are CLI prompts**; no web UI. `--yes` auto-approves and is recorded as such in the audit log.
- **Two deployment profiles** (local, kubernetes); more are added as `profiles/*.yaml` (capacity envelope + strategies).
- **Single orchestrator process.** State is durable (resume works across restarts) but there is no distributed worker pool; `IWorkQueue`-style
  distribution is a documented extension point, not implemented.
- **Load-test tooling** (gateway + load generator) was descoped for time; scaling is demonstrated through design (stateless instances,
  cache-aside, write-behind, rate limiting), tests (parallel creates, queue shedding, cache breaker) and the capacity analysis.
