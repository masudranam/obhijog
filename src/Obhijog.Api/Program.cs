using System.Text;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Obhijog.Api.Auth;
using Obhijog.Api.Endpoints;
using Obhijog.Api.Errors;
using Obhijog.Api.HealthChecks;
using Obhijog.Api.Startup;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;
using Obhijog.Infrastructure.Persistence.Seeding;

var builder = WebApplication.CreateBuilder(args);

// Required configuration is bound, validated and checked at startup rather than on first
// use. ValidateOnStart is what makes fail-fast structural instead of something each key
// has to remember — SPEC.md §19 and .claude/rules/backend-dotnet.md.
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => Encoding.UTF8.GetByteCount(o.SigningKey) >= JwtOptions.MinimumSigningKeyBytes,
        $"Jwt:SigningKey must be at least {JwtOptions.MinimumSigningKeyBytes} bytes. "
        + "It has no default and never falls back to a built-in value. See SPEC.md §10.2.")
    .ValidateOnStart();

builder.Services.AddOptions<StorageOptions>()
    .Bind(builder.Configuration.GetSection(StorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// IsNullOrWhiteSpace, not a null check: an empty value in appsettings.json binds to ""
// rather than null, which would sail past ?? and fail later as an opaque connect error.
var postgres = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgres))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required and has no default. See SPEC.md §19.");
}

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? ["http://localhost:4200"];

// snake_case globally, so no hand-written SQL, psql session or index filter has to
// double-quote an identifier. SPEC.md D11.
builder.Services.AddDbContext<ObhijogDbContext>(options =>
    options.UseNpgsql(postgres).UseSnakeCaseNamingConvention());

builder.Services.AddIdentityCore<User>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 10;
    })
    .AddRoles<Role>()
    .AddEntityFrameworkStores<ObhijogDbContext>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? new JwtOptions();

        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwt.SigningKey)),

            // The default five-minute grace makes a 15-minute access token a 20-minute one
            // and hides expiry bugs in testing.
            ClockSkew = TimeSpan.Zero,

            // Must match what TokenService emits (§10.2), or every role policy silently
            // denies: the principal would carry a "role" claim the framework never reads.
            RoleClaimType = TokenService.RoleClaim,
            NameClaimType = "name",
        };
    });

builder.Services.AddAuthorizationBuilder().AddObhijogPolicies();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<TokenService>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DatabaseSeeder>();

builder.Services.AddSingleton(serviceProvider =>
    new BlobServiceClient(serviceProvider.GetRequiredService<IOptions<StorageOptions>>()
        .Value.ConnectionString));

builder.Services.AddSingleton(serviceProvider =>
    serviceProvider.GetRequiredService<BlobServiceClient>()
        .GetBlobContainerClient(serviceProvider.GetRequiredService<IOptions<StorageOptions>>()
            .Value.Container));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ObhijogDbContext>("postgres", tags: [HealthEndpoints.ReadyTag])
    .AddCheck<BlobContainerHealthCheck>("blob", tags: [HealthEndpoints.ReadyTag]);

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

// One handler owns every exception-to-status mapping (§16.4). No endpoint returns
// BadRequest from its own try/catch.
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? context.HttpContext.TraceIdentifier);

var app = builder.Build();

// "dotnet run --project src/Obhijog.Api -- --seed" runs the seeder and exits. Seeding is a
// deliberate act, never a startup side effect: SPEC.md §8.11 requires it to run *after*
// migrations, and an app that seeded on boot would race a rolling deployment.
if (args.Contains("--seed", StringComparer.OrdinalIgnoreCase))
{
    using var seedScope = app.Services.CreateScope();
    await seedScope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
    return;
}

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthEndpoints();
var api = app.MapGroup("/api/v1");
api.MapAuthEndpoints();
api.MapReferenceEndpoints();

// Before serving. In Azure the container comes from Bicep and this is a no-op; locally
// nothing else creates it, and /health/ready is 503 until it exists.
await BlobContainerInitializer.EnsureContainerAsync(
    app.Services.GetRequiredService<BlobContainerClient>());

app.Run();
