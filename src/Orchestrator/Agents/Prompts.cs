namespace Orchestrator.Agents;

/// <summary>
/// Prompt templates. Kept deterministic (no timestamps/run ids/absolute paths) so that the replay cache key
/// — a hash of the prompt — is stable across machines and runs.
/// </summary>
public static class Prompts
{
    public const string JsonOnly = "Respond with a single JSON object only. No prose, no markdown fences.";

    public static string System(string role) =>
        $"You are the {role} in an automated, governed software delivery pipeline for .NET 10 web APIs. " +
        "Be precise and conservative. Never invent requirements the user did not ask for; record them as assumptions instead.";

    // ------------------------------------------------------------------ router
    public const string Router = """
        Decide whether the REQUEST changes one of the EXISTING PROJECTS or asks for a brand-new project.
        Return JSON: {"decision":"existing|new|unclear","project":"<slug or null>","confidence":0.0-1.0,"rationale":"<one sentence>"}
        """;

    // ------------------------------------------------------------------ analyst
    public const string Analyst = """
        Normalise the REQUIREMENT into an engineering specification.
        Rules:
        - functionalRequirements: concrete, testable behaviours with acceptance criteria.
        - ambiguities: ONLY questions an engineer cannot reasonably default (max 3). Each has 2-4 concrete options and a recommended default. Use [] if none.
        - assumptions: decisions you made on the user's behalf.
        - nfr: copy load/availability figures ONLY if the requirement states them, else null.
        - For a change to an EXISTING PROJECT describe only the change; existing behaviour stays unless the request says otherwise.
        Return JSON:
        {"title":"","projectName":"<short product name>","summary":"","aliases":["other names users may use"],"capabilities":["short capability phrases of the whole product"],
         "functionalRequirements":[{"id":"FR-1","description":"","acceptance":""}],
         "nfr":{"requestsPerDay":null,"availability":null,"notes":""},
         "ambiguities":[{"id":"A1","question":"","options":["",""],"default":""}],
         "clarifications":[],"assumptions":[],"outOfScope":[]}
        """;

    public const string AnalystRefine = """
        Update the DRAFT SPECIFICATION using the ANSWERS to its ambiguities.
        Turn each answer into functional requirements and/or assumptions, put each question/answer pair in "clarifications",
        and set "ambiguities" to []. Keep every other field unless an answer changes it. Return the full specification JSON.
        """;

    // ------------------------------------------------------------------ impact
    public const string Impact = """
        Analyse the impact of the SPECIFICATION on the EXISTING CODE (brownfield change).
        Identify which files must change, whether the data model changes, which request hot paths are touched, and risks.
        Return JSON: {"summary":"","files":[{"path":"","change":"","risk":"low|medium|high"}],"dataModelChange":false,"hotPaths":[""],"risks":[""]}
        """;

    // ------------------------------------------------------------------ architect
    public const string Architect = """
        Produce the technical design for the SPECIFICATION on top of the PLATFORM KIT.
        Rules:
        - Use only platform building blocks for cross-cutting concerns (cache, queue, db executor, TimeProvider).
        - nfrPatterns: map EVERY non-functional concern (load, latency, availability, abuse, privacy, data growth) to a pattern from the PATTERN CATALOG,
          the platform building block that implements it, and the test that proves it.
        - decisions: record significant choices with rationale and rejected alternatives.
        - entities[].change: new | modified | unchanged. schemaChange=true if any persisted entity is new or modified in an EXISTING project.
        Return JSON:
        {"overview":"","entities":[{"name":"","change":"new","fields":[{"name":"","type":"","notes":""}]}],
         "endpoints":[{"method":"GET","route":"","description":"","responses":["200 ..."]}],
         "nfrPatterns":[{"nfr":"","pattern":"","buildingBlock":"","test":""}],
         "decisions":[{"title":"","decision":"","rationale":"","alternatives":""}],
         "schemaChange":false,"risks":[""]}
        """;

    // ------------------------------------------------------------------ planner
    public const string Planner = """
        Decompose the DESIGN into file-level tasks.
        Rules:
        - One task per file. kind = code | test. action = create | modify (modify only for files listed in EXISTING FILES).
        - Code paths: src/App/Features/<Feature>/<File>.cs ; test paths: tests/App.Tests/Features/<Feature>/<File>.cs
        - src/App/Features/FeatureRegistration.cs must be modified whenever services or endpoints are added or changed.
        - dependsOn lists task ids whose files this file needs to compile against (types it uses). No cycles.
        - Keep it small: group closely related types (e.g. entity + its EF configuration) in one file.
        Return JSON: {"tasks":[{"id":"T1","kind":"code","path":"","action":"create","description":"what the file contains: types and members","dependsOn":[]}]}
        """;

    // ------------------------------------------------------------------ coder
    public const string Coder = """
        Write the complete content of the TARGET FILE for this task.
        Rules:
        - C# 13 / .NET 10, nullable enabled, file-scoped namespace App.Features.<Feature> (tests: App.Tests.Features.<Feature>).
        - Use only types from the platform kit, the DEPENDENCY FILES, the BCL, ASP.NET Core and EF Core.
        - Follow every PLATFORM KIT convention (no .Result/.Wait(), no DateTime.Now, endpoints never touch AppDbContext, inject TimeProvider).
        - Tests: xUnit v3 + Shouldly, use TestApp from App.Tests.Support and TestContext.Current.CancellationToken.
        Return ONLY the full file in a single ```csharp code block.
        """;

    // ------------------------------------------------------------------ docs
    public const string Docs = """
        Write README.md for the service described by the SPECIFICATION and DESIGN.
        Sections: overview, features, API (table of endpoints), running locally (`dotnet run --project src/App`, base URL http://localhost:5080),
        configuration (Database:Provider Sqlite|Postgres, Cache:Provider Memory|Redis, RateLimiting), testing (`dotnet test`), design notes.
        Return ONLY markdown.
        """;

    // ------------------------------------------------------------------ reviewer
    public const string Reviewer = """
        Review the CHANGED FILES against the SPECIFICATION. Look for: unmet requirements, security issues (injection, open redirect, secrets),
        concurrency/scalability problems, missing validation, and weak or tautological tests.
        severity: high (must fix before release) | medium | low. verdict: approve | changes.
        Return JSON: {"verdict":"approve","summary":"","findings":[{"severity":"low","file":"","issue":"","suggestion":""}]}
        """;

    public static string WithFeedback(string prompt, IReadOnlyList<string> feedback)
    {
        if (feedback.Count == 0) return prompt;
        return prompt + "\n\nPREVIOUS ATTEMPT WAS REJECTED. Fix ALL of these issues:\n" +
               string.Join("\n", feedback.Take(25).Select(f => "- " + (f.Length > 600 ? f[..600] + "…" : f)));
    }

    public static string Section(string title, string body) => $"### {title}\n{body.Trim()}\n";
}
