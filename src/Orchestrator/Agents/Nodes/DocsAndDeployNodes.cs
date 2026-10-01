using System.Text;
using System.Text.RegularExpressions;
using Orchestrator.Core.Common;
using Orchestrator.Core.Engine;

namespace Orchestrator.Agents.Nodes;

/// <summary>
/// Documentation runs in parallel with implementation. README prose comes from the LLM (fallback: deterministic template);
/// architecture/API docs and the .http file are generated deterministically from the design so they cannot drift from it.
/// </summary>
public sealed class DocsNode(AgentServices s, bool deterministicReadme = false) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var spec = AgentServices.Read<Spec>(run, ArtifactKeys.Spec);
        var design = AgentServices.Read<Design>(run, ArtifactKeys.Design);
        var nfr = AgentServices.Read<NfrArtifact>(run, ArtifactKeys.Nfr);
        var workspace = AgentServices.Read<WorkspaceArtifact>(run, ArtifactKeys.Workspace);

        if (workspace.Mode == "brownfield")
            return await MergeAsync(run, spec, design, nfr, workspace, ct);

        string readme;
        if (deterministicReadme)
        {
            readme = TemplateReadme(spec, design, nfr);
        }
        else
        {
            var context = Prompts.Section("SPECIFICATION", JsonText.Serialize(new { spec.Title, spec.Summary, spec.FunctionalRequirements, spec.Assumptions })) + "\n" +
                          Prompts.Section("DESIGN", JsonText.Serialize(new { design.Overview, design.Endpoints, design.Decisions }));
            readme = (await s.AskTextAsync(ctx, "docs", "technical writer", Prompts.Docs, context, ct, 2500)).Trim();
            if (readme.StartsWith("```")) readme = AgentServices.ExtractCode(readme);
            readme = readme.Trim() + "\n";
        }

        return NodeResult.Ok("README, docs/ARCHITECTURE.md, docs/API.md, app.http")
            .With(ArtifactKeys.File("README.md"), readme)
            .With(ArtifactKeys.File("docs/ARCHITECTURE.md"), Architecture(spec, design, nfr, run.Get(ArtifactKeys.CapacityReport)))
            .With(ArtifactKeys.File("docs/API.md"), Api(design))
            .With(ArtifactKeys.File("app.http"), Http(design))
            .Because(deterministicReadme ? "README generated from template (fallback)." : "README written by docs agent.");
    }

    /// <summary>
    /// Brownfield: documentation is cumulative. The pre-run docs (from the checkpoint, so retries see a stable base) are kept
    /// and the change is merged in: a "Changes in vN" README section, endpoint tables updated by method+route, and a new
    /// architecture change-log section whose ADRs continue the existing numbering.
    /// </summary>
    private async Task<NodeResult> MergeAsync(RunContext run, Spec spec, Design design, NfrArtifact nfr, WorkspaceArtifact workspace, CancellationToken ct)
    {
        var card = await s.Projects.GetAsync(workspace.Slug, ct);
        var version = $"v{card?.NextVersionNumber ?? 2}";
        var baseDir = Path.Combine(s.ProjectDir(run), ".versions", workspace.Checkpoint);
        string Existing(string path) => ProjectFiles.Read(baseDir, path) ?? ProjectFiles.Read(s.ProjectDir(run), path) ?? "";

        // README: keep everything, merge the API table, insert a change section before the run instructions.
        var readme = Existing("README.md");
        readme = UpsertEndpointRows(readme, design.Endpoints, withResponses: readme.Contains("| Responses |"));
        var change = new StringBuilder($"## Changes in {version} — {spec.Title}\n{spec.Summary}\n\n");
        foreach (var f in spec.FunctionalRequirements) change.AppendLine($"- **{f.Id}** {f.Description} _(acceptance: {f.Acceptance})_");
        change.AppendLine();
        var anchor = Regex.Match(readme, "^## Run(ning)? locally", RegexOptions.Multiline);
        readme = anchor.Success ? readme.Insert(anchor.Index, change.ToString()) : readme.TrimEnd() + "\n\n" + change;

        // ARCHITECTURE: append a versioned change section; ADR numbers continue.
        var architecture = Existing("docs/ARCHITECTURE.md");
        var nextAdr = Regex.Matches(architecture, "^### ADR-", RegexOptions.Multiline).Count + 1;
        var section = new StringBuilder($"\n## Change log — {version}: {spec.Title}\n\n{design.Overview}\n\n");
        var changedEntities = design.Entities.Where(e => e.Change != "unchanged").ToList();
        if (changedEntities.Count > 0)
        {
            section.AppendLine("### Data model changes");
            foreach (var e in changedEntities)
            {
                section.AppendLine($"**{e.Name}** ({e.Change})\n\n| Field | Type | Notes |\n|---|---|---|");
                foreach (var f in e.Fields) section.AppendLine($"| {f.Name} | {f.Type} | {f.Notes} |");
                section.AppendLine();
            }
        }
        section.AppendLine("### NFR traceability for this change\n| NFR | Pattern | Building block | Verified by |\n|---|---|---|---|");
        foreach (var p in design.NfrPatterns) section.AppendLine($"| {p.Nfr} | {p.Pattern} | {p.BuildingBlock} | {p.Test} |");
        section.AppendLine();
        foreach (var d in design.Decisions)
            section.AppendLine($"### ADR-{nextAdr++:000}: {d.Title} ({version})\n- **Decision:** {d.Decision}\n- **Rationale:** {d.Rationale}\n- **Alternatives considered:** {d.Alternatives ?? "n/a"}\n");
        if (design.Risks.Count > 0)
        {
            section.AppendLine("### Risks introduced");
            foreach (var r in design.Risks) section.AppendLine($"- {r}");
        }
        section.AppendLine($"\nDeployment profile **{nfr.Profile}**, capacity verdict **{nfr.Verdict}**.");
        architecture = architecture.TrimEnd() + "\n" + section;

        // API.md and app.http: merge endpoints; append requests for new routes only.
        var api = UpsertEndpointRows(Existing("docs/API.md"), design.Endpoints, withResponses: true);
        var http = Existing("app.http");
        var newEndpoints = design.Endpoints.Where(e => !http.Contains($"{e.Method.ToUpperInvariant()} {{{{base}}}}{Regex.Replace(e.Route, "\\{[^}]+\\}", "REPLACE_ME")}")).ToList();
        if (newEndpoints.Count > 0) http = http.TrimEnd() + "\n\n" + Http(new Design { Endpoints = newEndpoints }).Replace("@base = http://localhost:5080\n\n", "");

        return NodeResult.Ok($"Docs merged for {version}: README change section, {design.Decisions.Count} ADRs appended, endpoint tables updated")
            .With(ArtifactKeys.File("README.md"), readme.TrimEnd() + "\n")
            .With(ArtifactKeys.File("docs/ARCHITECTURE.md"), architecture)
            .With(ArtifactKeys.File("docs/API.md"), api.TrimEnd() + "\n")
            .With(ArtifactKeys.File("app.http"), http.TrimEnd() + "\n")
            .Because($"Brownfield docs merged into existing documentation ({version}); previous content preserved.");
    }

    /// <summary>Updates rows of a markdown endpoint table keyed by method + route; appends rows for new endpoints.</summary>
    public static string UpsertEndpointRows(string markdown, IEnumerable<EndpointDesign> endpoints, bool withResponses)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        int lastRow = -1;
        for (var i = 0; i < lines.Count; i++)
            if (Regex.IsMatch(lines[i], @"^\| (GET|POST|PUT|PATCH|DELETE) \| `")) lastRow = i;

        foreach (var e in endpoints)
        {
            var row = withResponses
                ? $"| {e.Method.ToUpperInvariant()} | `{e.Route}` | {e.Description} | {string.Join(", ", e.Responses)} |"
                : $"| {e.Method.ToUpperInvariant()} | `{e.Route}` | {e.Description} |";
            var key = $"| {e.Method.ToUpperInvariant()} | `{e.Route}` |";
            var idx = lines.FindIndex(l => l.StartsWith(key, StringComparison.Ordinal));
            if (idx >= 0) lines[idx] = row;
            else if (lastRow >= 0) lines.Insert(++lastRow, row);
        }
        return string.Join("\n", lines);
    }

    public static readonly IGate Gate = Orchestrator.Core.Engine.Gate.Sync("docs", (_, r) =>
    {
        var readme = r?.Artifacts.GetValueOrDefault(ArtifactKeys.File("README.md")) ?? "";
        var v = new List<string>();
        if (!readme.TrimStart().StartsWith('#')) v.Add("README must start with a markdown heading");
        if (readme.Length < 300) v.Add("README is too short to be useful");
        return GateResult.From(v);
    });

    public static Func<RunContext, NodeResult, CancellationToken, Task> Commit(AgentServices s) => async (run, result, ct) =>
    {
        foreach (var (key, content) in result.Artifacts.Where(a => a.Key.StartsWith(ArtifactKeys.FilePrefix)))
            await ProjectFiles.WriteAsync(s.ProjectDir(run), key[ArtifactKeys.FilePrefix.Length..], content, ct);
    };

    private static string TemplateReadme(Spec spec, Design design, NfrArtifact nfr)
    {
        var sb = new StringBuilder($"# {spec.ProjectName}\n\n{spec.Summary}\n\n## Features\n");
        foreach (var f in spec.FunctionalRequirements) sb.AppendLine($"- **{f.Id}** {f.Description}");
        sb.AppendLine("\n## API\n| Method | Route | Description |\n|---|---|---|");
        foreach (var e in design.Endpoints) sb.AppendLine($"| {e.Method} | `{e.Route}` | {e.Description} |");
        sb.AppendLine($"""

            ## Run locally
            ```
            dotnet run --project src/App        # http://localhost:5080
            dotnet test
            ```

            ## Configuration
            | Key | Default | Notes |
            |---|---|---|
            | `Database:Provider` | `Sqlite` | `Postgres` supported (set `Database:ConnectionString`) |
            | `Cache:Provider` | `Memory` | `Redis` (set `Cache:RedisConfiguration`) |
            | `RateLimiting:PermitLimit` / `WindowSeconds` | 200 / 10 | per client |

            Deployment profile: **{nfr.Profile}** — see `deploy/` and `docs/ARCHITECTURE.md`.
            """);
        return sb.ToString();
    }

    public static string Architecture(Spec spec, Design design, NfrArtifact nfr, string? capacityReport)
    {
        var sb = new StringBuilder($"# Architecture — {spec.ProjectName}\n\n{design.Overview}\n\n");
        sb.AppendLine("## Components\n- **Platform kit** (template-locked): database (EF Core, provider-switchable), cache (`ICacheService` + circuit breaker), bounded background queue, rate limiting, request timeouts, health checks, ops stats.");
        sb.AppendLine("- **Feature code** (`src/App/Features/**`): generated by the orchestrator's agents and validated by its gates.\n");
        sb.AppendLine("## Data model");
        foreach (var e in design.Entities)
        {
            sb.AppendLine($"### {e.Name} ({e.Change})\n| Field | Type | Notes |\n|---|---|---|");
            foreach (var f in e.Fields) sb.AppendLine($"| {f.Name} | {f.Type} | {f.Notes} |");
            sb.AppendLine();
        }
        sb.AppendLine("## Non-functional requirements → patterns → tests (traceability)\n| NFR | Pattern | Building block | Verified by |\n|---|---|---|---|");
        foreach (var p in design.NfrPatterns) sb.AppendLine($"| {p.Nfr} | {p.Pattern} | {p.BuildingBlock} | {p.Test} |");
        sb.AppendLine("\n## Architecture decisions");
        var i = 1;
        foreach (var d in design.Decisions)
            sb.AppendLine($"### ADR-{i++:000}: {d.Title}\n- **Decision:** {d.Decision}\n- **Rationale:** {d.Rationale}\n- **Alternatives considered:** {d.Alternatives ?? "n/a"}\n");
        if (design.Risks.Count > 0)
        {
            sb.AppendLine("## Risks");
            foreach (var r in design.Risks) sb.AppendLine($"- {r}");
        }
        sb.AppendLine($"\n## Deployment & capacity\nProfile **{nfr.Profile}**, verdict **{nfr.Verdict}**{(nfr.Overridden ? " (overridden by operator)" : "")}.\n");
        if (capacityReport is not null) sb.AppendLine(Regex.Replace(capacityReport, "^# ", "### ", RegexOptions.Multiline));
        return sb.ToString();
    }

    private static string Api(Design design)
    {
        var sb = new StringBuilder("# API\n\nBase URL (local): `http://localhost:5080`\n\n| Method | Route | Description | Responses |\n|---|---|---|---|\n");
        foreach (var e in design.Endpoints) sb.AppendLine($"| {e.Method} | `{e.Route}` | {e.Description} | {string.Join("<br>", e.Responses)} |");
        sb.AppendLine("\nPlatform endpoints: `GET /health/live`, `GET /health/ready`, `GET /ops/stats`. Errors use RFC 7807 problem details; throttled requests get `429` with `Retry-After`.");
        return sb.ToString();
    }

    private static string Http(Design design)
    {
        var sb = new StringBuilder("@base = http://localhost:5080\n\n");
        foreach (var e in design.Endpoints)
        {
            var route = Regex.Replace(e.Route, "\\{[^}]+\\}", "REPLACE_ME");
            sb.AppendLine($"### {e.Description}");
            sb.AppendLine($"{e.Method.ToUpperInvariant()} {{{{base}}}}{route}");
            if (e.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) || e.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                sb.AppendLine("Content-Type: application/json\n\n{\n  \"url\": \"https://example.com\"\n}");
            sb.AppendLine();
        }
        sb.AppendLine("### Ops\nGET {{base}}/ops/stats\n");
        return sb.ToString();
    }
}

/// <summary>Deterministic deployment artifacts for the chosen profile, statically validated (no cluster needed).</summary>
public sealed class DeployNode(AgentServices s) : INodeHandler
{
    public async Task<NodeResult> ExecuteAsync(NodeContext ctx, CancellationToken ct)
    {
        var run = ctx.Run;
        var nfr = AgentServices.Read<NfrArtifact>(run, ArtifactKeys.Nfr);
        var slug = run.ProjectSlug!;
        var card = await s.Projects.GetAsync(slug, ct);
        var tag = $"v{card?.NextVersionNumber ?? 1}";
        var result = NodeResult.Ok($"Deployment artifacts for profile '{nfr.Profile}'");

        result.With(ArtifactKeys.File("Dockerfile"), Dockerfile());
        switch (nfr.Profile)
        {
            case "kubernetes":
                result.With(ArtifactKeys.File("deploy/k8s/deployment.yaml"), K8sDeployment(slug, tag));
                result.With(ArtifactKeys.File("deploy/k8s/service.yaml"), K8sService(slug));
                result.With(ArtifactKeys.File("deploy/k8s/hpa.yaml"), K8sHpa(slug));
                result.With(ArtifactKeys.File("deploy/k8s/pdb.yaml"), K8sPdb(slug));
                result.With(ArtifactKeys.File("deploy/k8s/configmap.yaml"), K8sConfig(slug));
                break;
            case "compose":
                result.With(ArtifactKeys.File("deploy/compose/docker-compose.yml"), Compose(slug, tag));
                result.With(ArtifactKeys.File("deploy/compose/nginx.conf"), Nginx());
                break;
            default:
                result.With(ArtifactKeys.File("deploy/local/README.md"), LocalReadme(slug));
                break;
        }
        return result.Because($"Generated deployment artifacts for '{nfr.Profile}' (image tag {tag}, never :latest).");
    }

    public static readonly IGate Gate = Orchestrator.Core.Engine.Gate.Sync("deployment-static-checks", (_, r) =>
    {
        var v = new List<string>();
        foreach (var (key, content) in r?.Artifacts ?? [])
        {
            if (!key.Contains("deploy/k8s/") && !key.Contains("docker-compose")) continue;
            if (Regex.IsMatch(content, @"image:\s*\S+:latest\b") || Regex.IsMatch(content, @"image:\s*[^\s:]+\s*$", RegexOptions.Multiline))
                v.Add($"{key}: images must use an immutable tag (no :latest)");
            if (Regex.IsMatch(content, @"(?i)(password|secret)\s*[:=]\s*[""']?[A-Za-z0-9]{6,}") && !content.Contains("secretKeyRef"))
                v.Add($"{key}: secret literal found; use a Secret reference");
            if (key.EndsWith("deployment.yaml"))
            {
                foreach (var required in new[] { "livenessProbe", "readinessProbe", "resources:", "limits:", "requests:" })
                    if (!content.Contains(required)) v.Add($"{key}: missing {required}");
            }
            if (key.EndsWith("hpa.yaml"))
            {
                var min = int.Parse(Regex.Match(content, @"minReplicas:\s*(\d+)").Groups[1].Value is { Length: > 0 } m ? m : "0");
                var max = int.Parse(Regex.Match(content, @"maxReplicas:\s*(\d+)").Groups[1].Value is { Length: > 0 } x ? x : "0");
                if (min < 2) v.Add($"{key}: minReplicas must be >= 2 for availability");
                if (max < min) v.Add($"{key}: maxReplicas must be >= minReplicas");
            }
        }
        return GateResult.From(v);
    });

    private static string Dockerfile() => """
        FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
        WORKDIR /src
        COPY . .
        RUN dotnet publish src/App/App.csproj -c Release -o /app

        FROM mcr.microsoft.com/dotnet/aspnet:10.0
        WORKDIR /app
        COPY --from=build /app .
        USER $APP_UID
        ENV ASPNETCORE_URLS=http://+:8080
        EXPOSE 8080
        ENTRYPOINT ["dotnet", "App.dll"]
        """ + "\n";

    private static string K8sDeployment(string slug, string tag) => $$"""
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: {{slug}}
          labels: { app: {{slug}} }
        spec:
          replicas: 3
          selector:
            matchLabels: { app: {{slug}} }
          strategy:
            type: RollingUpdate
            rollingUpdate: { maxUnavailable: 0, maxSurge: 1 }
          template:
            metadata:
              labels: { app: {{slug}} }
            spec:
              containers:
                - name: api
                  image: registry.local/{{slug}}:{{tag}}
                  ports: [{ containerPort: 8080 }]
                  envFrom: [{ configMapRef: { name: {{slug}}-config } }]
                  env:
                    - name: Database__ConnectionString
                      valueFrom: { secretKeyRef: { name: {{slug}}-db, key: connectionString } }
                    - name: InstanceId
                      valueFrom: { fieldRef: { fieldPath: metadata.name } }
                  resources:
                    requests: { cpu: 250m, memory: 256Mi }
                    limits: { cpu: "1", memory: 512Mi }
                  readinessProbe:
                    httpGet: { path: /health/ready, port: 8080 }
                    periodSeconds: 5
                  livenessProbe:
                    httpGet: { path: /health/live, port: 8080 }
                    periodSeconds: 10
        """ + "\n";

    private static string K8sService(string slug) => $$"""
        apiVersion: v1
        kind: Service
        metadata: { name: {{slug}} }
        spec:
          selector: { app: {{slug}} }
          ports: [{ port: 80, targetPort: 8080 }]
        """ + "\n";

    private static string K8sHpa(string slug) => $$"""
        apiVersion: autoscaling/v2
        kind: HorizontalPodAutoscaler
        metadata: { name: {{slug}} }
        spec:
          scaleTargetRef: { apiVersion: apps/v1, kind: Deployment, name: {{slug}} }
          minReplicas: 3
          maxReplicas: 20
          metrics:
            - type: Resource
              resource: { name: cpu, target: { type: Utilization, averageUtilization: 70 } }
        """ + "\n";

    private static string K8sPdb(string slug) => $$"""
        apiVersion: policy/v1
        kind: PodDisruptionBudget
        metadata: { name: {{slug}} }
        spec:
          minAvailable: 2
          selector:
            matchLabels: { app: {{slug}} }
        """ + "\n";

    private static string K8sConfig(string slug) => $$"""
        apiVersion: v1
        kind: ConfigMap
        metadata: { name: {{slug}}-config }
        data:
          Database__Provider: Postgres
          Cache__Provider: Redis
          Cache__RedisConfiguration: redis:6379
          RateLimiting__PermitLimit: "500"
        """ + "\n";

    private static string Compose(string slug, string tag) => $$"""
        services:
          api:
            image: {{slug}}:{{tag}}
            build: ../..
            environment:
              Database__Provider: Postgres
              Database__ConnectionString: Host=db;Database=app;Username=app;Password=${DB_PASSWORD}
              Cache__Provider: Redis
              Cache__RedisConfiguration: redis:6379
            deploy: { replicas: 3 }
            depends_on: [db, redis]
          db:
            image: postgres:17.2
            environment: { POSTGRES_DB: app, POSTGRES_USER: app, POSTGRES_PASSWORD: "${DB_PASSWORD}" }
            volumes: [dbdata:/var/lib/postgresql/data]
          redis:
            image: redis:7.4
          gateway:
            image: nginx:1.27
            ports: ["8080:80"]
            volumes: [./nginx.conf:/etc/nginx/nginx.conf:ro]
            depends_on: [api]
        volumes:
          dbdata: {}
        """ + "\n";

    private static string Nginx() => """
        events {}
        http {
          upstream api { server api:8080; }
          server {
            listen 80;
            location / { proxy_pass http://api; proxy_set_header X-Forwarded-For $remote_addr; }
          }
        }
        """ + "\n";

    private static string LocalReadme(string slug) => $"""
        # Local deployment — {slug}

        Single machine, SQLite (WAL) + in-memory cache.

        Limits: SQLite has a single writer; each instance has its own in-memory cache (use Cache:Provider=Redis to share).
        """ + "\n";
}
