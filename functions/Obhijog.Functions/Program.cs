using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Obhijog.Infrastructure.Messaging;
using Obhijog.Infrastructure.Notifications;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Persistence;

// The Service Bus consumer of SPEC.md §14 F16.
//
// It shares Obhijog.Infrastructure with the API, so it shares the DbContext, the entity
// configurations, and — the part that matters — the partial unique index that makes a
// redelivered message harmless (§11.3 defence 4). A consumer with its own hand-rolled
// persistence would be a second definition of that guarantee, and the second one would be
// the one that was wrong.
//
// It does NOT run migrations. Schema changes belong to one owner and that owner is the API
// (§8, CLAUDE.md non-negotiable 8); a Function racing a rolling API deployment to apply the
// same migration is a deadlock on the history table, not a convenience.

// CreateBuilder already applies the worker defaults in Worker SDK 2.x; there is deliberately
// no ConfigureFunctionsWebApplication call, because that pulls in the ASP.NET Core HTTP
// integration and this worker has no HTTP trigger. One Service Bus trigger, no port.
var builder = FunctionsApplication.CreateBuilder(args);

// Indexed rather than GetConnectionString(), which needs a using this worker otherwise has
// no reason to carry. Fails fast and names the key: a Function that starts without a
// database and finds out on the first message dead-letters real breaches while looking
// healthy in the portal.
var postgres = builder.Configuration["ConnectionStrings:Postgres"];

if (string.IsNullOrWhiteSpace(postgres))
{
    throw new InvalidOperationException(
        "ConnectionStrings:Postgres is required and has no default. See SPEC.md §19.");
}

builder.Services.AddDbContext<ObhijogDbContext>(options =>
    options.UseNpgsql(postgres).UseSnakeCaseNamingConvention());

builder.Services.AddOptions<NotificationOptions>()
    .Bind(builder.Configuration.GetSection(NotificationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// The same clock discipline as the API: injected, never DateTimeOffset.UtcNow
// (CLAUDE.md non-negotiable 5).
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<INotificationSender, LogNotificationSender>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddScoped<SlaBreachNotificationHandler>();

builder.Build().Run();
