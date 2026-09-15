using System.Text;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Obhijog.Api.Auth;
using Obhijog.Api.Endpoints;
using Obhijog.Api.Errors;
using Obhijog.Api.HealthChecks;
using Obhijog.Api.Startup;
using Obhijog.Infrastructure.Attachments;
using Obhijog.Infrastructure.Auth;
using Obhijog.Infrastructure.Complaints;
using Obhijog.Infrastructure.Dashboard;
using Obhijog.Infrastructure.Export;
using Obhijog.Infrastructure.Identity;
using Obhijog.Infrastructure.Notifications;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;
using Obhijog.Infrastructure.Persistence.Seeding;
using Obhijog.Infrastructure.Reference;
using Obhijog.Infrastructure.Sla;

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

builder.Services.AddOptions<AttachmentOptions>()
    .Bind(builder.Configuration.GetSection(AttachmentOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => o.AllowedContentTypeSet.Count > 0,
        "Attachments:AllowedContentTypes must name at least one type. It is an allow-list, "
        + "so an empty value rejects every upload (SPEC.md §19).")
    .ValidateOnStart();

// The ladder's rungs are configuration, not constants, so they are validated where every
// other option is. EscalationLevel2Percent > 100 and WarningThresholdPercent < 100 are
// enforced by the [Range] attributes; this checks the one relationship between them that
// an attribute cannot see — the warning must come strictly before the breach, or the two
// rungs fire together and the ladder has one step (SPEC.md §11.2).
builder.Services.AddOptions<SlaOptions>()
    .Bind(builder.Configuration.GetSection(SlaOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        o => o.WarningThresholdPercent < 100 && o.EscalationLevel2Percent > 100,
        "Sla:WarningThresholdPercent must be below 100 and Sla:EscalationLevel2Percent above "
        + "it — the ladder of SPEC.md §11.2 is warn, then breach, then escalate.")
    .ValidateOnStart();

builder.Services.AddOptions<NotificationOptions>()
    .Bind(builder.Configuration.GetSection(NotificationOptions.SectionName))
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
    .AddJwtBearer(options => options.MapInboundClaims = false);

// Configured from the validated JwtOptions rather than a second GetSection(...).Get<T>():
// that overload returns null for a missing section and the ?? fallback it invites is a
// built-in default signing key, which is the thing §10.2 forbids. Going through IOptions
// means the ValidateOnStart above has already run.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
        bearer.TokenValidationParameters = TokenService.CreateValidationParameters(jwt.Value));

// The multipart limit tracks Attachments:MaxSizeBytes rather than being a second number
// to keep in step. Without it a caller could stream an arbitrarily large body that the
// framework buffers before the service ever gets to return its 413.
builder.Services.AddOptions<FormOptions>()
    .Configure<IOptions<AttachmentOptions>>((form, attachments) =>
        form.MultipartBodyLengthLimit = attachments.Value.MaxSizeBytes + 64 * 1024);

builder.Services.AddAuthorizationBuilder().AddObhijogPolicies();

// Enums cross the wire as strings, never as their ordinals. The database stores them as
// varchar (§8.12) and the Angular models mirror them as string unions
// (.claude/rules/frontend-angular.md), so an integer here would be the one representation
// nothing else uses — and reordering an enum member would silently change the API.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<ReferenceService>();
builder.Services.AddScoped<ComplaintService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<ComplaintTransitionService>();
builder.Services.AddScoped<CommentService>();
builder.Services.AddScoped<IAttachmentStore, BlobAttachmentStore>();
builder.Services.AddScoped<SlaService>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<ComplaintExportService>();
builder.Services.AddScoped<ISlaSweeper, SlaSweeper>();

// The MVP channel of F12. Swapping in email is a different registration here and no other
// change anywhere — the sweeper writes the row and never formats a message for a channel.
builder.Services.AddScoped<INotificationSender, LogNotificationSender>();

// Registered unconditionally; Sla:SweepIntervalSeconds = 0 makes it return immediately with
// a log line saying so. Skipping the registration instead would make a disabled sweeper
// indistinguishable at runtime from one that was never wired up (§19).
builder.Services.AddHostedService<SlaSweepService>();

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
api.MapComplaintEndpoints().MapExportEndpoint();
api.MapAttachmentEndpoints();
api.MapSlaEndpoints();
api.MapDashboardEndpoints();

// Before serving. In Azure the container comes from Bicep and this is a no-op; locally
// nothing else creates it, and /health/ready is 503 until it exists.
await BlobContainerInitializer.EnsureContainerAsync(
    app.Services.GetRequiredService<BlobContainerClient>());

app.Run();
