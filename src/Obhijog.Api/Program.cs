using Azure.Storage.Blobs;
using Microsoft.EntityFrameworkCore;
using Obhijog.Api.Endpoints;
using Obhijog.Api.HealthChecks;
using Obhijog.Api.Startup;
using Obhijog.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Required configuration has no fallback. A development default that reaches a deployed
// environment is the same bug as a hard-coded secret, so startup fails loudly instead.
// SPEC.md §19; .claude/rules/backend-dotnet.md.
// IsNullOrWhiteSpace, not a null check: an empty value in appsettings.json binds to ""
// rather than null, which would sail past ?? and fail later as an opaque connect error.
var postgres = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgres))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required and has no default. See SPEC.md §19.");
}

var storageConnection = builder.Configuration["Storage:ConnectionString"];
if (string.IsNullOrWhiteSpace(storageConnection))
{
    throw new InvalidOperationException(
        "Storage:ConnectionString is required and has no default. See SPEC.md §19.");
}

var storageContainer = builder.Configuration["Storage:Container"] ?? "complaint-attachments";

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:4200"];

// snake_case globally, so no hand-written SQL, psql session or index filter has to
// double-quote an identifier. SPEC.md D11.
builder.Services.AddDbContext<ObhijogDbContext>(options =>
    options.UseNpgsql(postgres).UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(_ => new BlobServiceClient(storageConnection));
builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<BlobServiceClient>()
        .GetBlobContainerClient(storageContainer));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ObhijogDbContext>("postgres", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<BlobContainerHealthCheck>("blob", tags: [HealthEndpoints.ReadyTag]);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.MapHealthEndpoints();

// Before serving. In Azure the container comes from Bicep and this is a no-op; locally
// nothing else creates it, and /health/ready is 503 until it exists.
await BlobContainerInitializer.EnsureContainerAsync(
    app.Services.GetRequiredService<BlobContainerClient>());

app.Run();
