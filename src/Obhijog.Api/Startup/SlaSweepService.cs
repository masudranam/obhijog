using Microsoft.Extensions.Options;
using Obhijog.Infrastructure.Options;
using Obhijog.Infrastructure.Sla;

namespace Obhijog.Api.Startup;

/// <summary>
/// Runs <see cref="ISlaSweeper.SweepAsync"/> every <c>Sla:SweepIntervalSeconds</c>. F11, §19.
///
/// It is a <b>caller</b> and nothing else: no ladder logic, no thresholds, no queries. The
/// manual <c>POST /admin/sla/sweep</c> endpoint calls the same method, and M10 will add a
/// third caller driven by a Service Bus message (§13.2). Anything this class knew that the
/// sweeper did not would be the one code path the tests never exercise.
///
/// Lives in the Api project because hosting is an Api concern — Infrastructure takes no
/// reference to <c>Microsoft.Extensions.Hosting</c>.
/// </summary>
public class SlaSweepService(
    IServiceScopeFactory scopeFactory,
    IOptions<SlaOptions> options,
    ILogger<SlaSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.SweepIntervalSeconds;

        // §19: zero disables it. Returning here rather than never registering the service
        // keeps the decision readable at runtime — the log line says the sweeper is off,
        // which is the question someone debugging "nothing escalated" actually has.
        if (interval <= 0)
        {
            logger.LogInformation(
                "SLA sweep is disabled: Sla:SweepIntervalSeconds is {Interval}. "
                + "POST /admin/sla/sweep still runs a pass by hand.",
                interval);

            return;
        }

        logger.LogInformation("SLA sweep starting on a {Interval}s interval.", interval);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            // A scope per pass: the sweeper and its DbContext are scoped, and a singleton
            // hosted service holding one context for the process lifetime would accumulate
            // every tracked entity it ever saw.
            using var scope = scopeFactory.CreateScope();

            try
            {
                await scope.ServiceProvider
                    .GetRequiredService<ISlaSweeper>()
                    .SweepAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The sweeper already catches per phase and per complaint (§11.6). This is
                // the outer net for anything it could not: resolving the service, opening a
                // connection. The sweep never takes the API process down.
                logger.LogError(exception, "SLA sweep pass failed. The next pass will retry.");
            }
        }
    }

    /// <summary>
    /// Shutdown arrives as a cancelled wait, which is an ordinary stop rather than a fault —
    /// letting it propagate would log an unhandled exception on every clean shutdown.
    /// </summary>
    private static async Task<bool> SafeWaitAsync(
        PeriodicTimer timer,
        CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
