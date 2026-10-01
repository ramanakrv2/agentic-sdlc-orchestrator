// Composition root. PLATFORM-OWNED (template-locked): agents add behaviour in Features/, never here.
using App.Features;
using App.Platform;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatform();                                   // logging, database, cache, queue, resilience, rate limiting, health, ops
builder.Services.AddFeatures(builder.Configuration);     // feature services (Features/FeatureRegistration.cs)

var app = builder.Build();

app.UsePlatform();
app.MapFeatures();

await app.InitializePlatformAsync();
app.Run();

/// <summary>Exposed for WebApplicationFactory integration tests.</summary>
public partial class Program;
