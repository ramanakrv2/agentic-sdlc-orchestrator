# Platform kit (template-locked)

Every generated service starts from this template. Agents write **feature code only** under
`src/App/Features/**` and tests under `tests/App.Tests/Features/**`. Everything under `src/App/Platform/**`,
`Program.cs`, `appsettings.json`, the `.csproj` files and `tests/App.Tests/{Platform,Support}/**` is locked by policy.

## Wiring
- `Features/FeatureRegistration.cs` — `AddFeatures(IServiceCollection, IConfiguration)` registers feature services,
  `MapFeatures(IEndpointRouteBuilder)` maps endpoints. This is the only file that connects features to the app.
- Namespaces: `App.Features.<Feature>`; tests: `App.Tests.Features.<Feature>`.

## Building blocks
| Need | Use | Notes |
|---|---|---|
| Persistence | `AppDbContext` (`App.Platform.Data`) | Add an `IEntityTypeConfiguration<T>` per entity (auto-discovered). Access via `db.Set<T>()` **inside repositories only**. |
| Repository | Interface + EF implementation in the feature folder | Endpoints/services depend on the interface, never on `AppDbContext`. |
| Transient DB faults | `IDbExecutor.ExecuteAsync(...)` | Retries SQLite busy/locked and Postgres transient errors. |
| Caching (cache-aside) | `ICacheService` (`App.Platform.Caching`) | `GetOrCreateAsync(key, factory, ttl)`, `SetAsync`, `RemoveAsync`. Circuit breaker built in; failures degrade to the source. Memory or Redis via config. |
| Off-request-path work | `IBackgroundQueue<T>` + `IBatchHandler<T>` (`App.Platform.Queue`) | Register with `services.AddBackgroundQueue<T, THandler>(o => { o.Capacity = ...; o.BatchSize = ...; })`. `TryEnqueue` never blocks; returns false (and counts) when full. |
| Time | Inject `TimeProvider` | Never `DateTime.Now`/`UtcNow` (tests use `FakeTimeProvider`). |
| Errors | Return `Results.Problem(...)` / `Results.ValidationProblem(...)` | ProblemDetails is configured. |
| Rate limiting, request timeouts, health (`/health/live`, `/health/ready`), `/ops/stats`, `X-Instance-Id` | Already applied globally | Nothing to do in features. |

## Conventions enforced at the validation gate
- No `.Result` / `.Wait()` (sync-over-async).
- No mutable `static` fields in features (instances must be stateless → horizontally scalable).
- Endpoint files must not reference `AppDbContext`.
- No `DateTime.Now` / `DateTime.UtcNow` in features (use `TimeProvider`).
- No secrets in code; packages limited to the policy allowlist.

## Testing
- `tests/App.Tests/Support/TestApp.cs`: `await using var app = new TestApp(); var client = app.CreateClient();`
  Override settings with `new TestApp(new Dictionary<string,string?>{ ["Key"] = "value" })`; control time with `app.Clock.Advance(...)`.
- Use xUnit v3 (`[Fact]`, `[Theory]`), Shouldly assertions, `TestContext.Current.CancellationToken`.
