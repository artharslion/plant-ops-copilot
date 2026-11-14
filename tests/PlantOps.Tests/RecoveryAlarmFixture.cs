using Microsoft.Extensions.DependencyInjection;
using PlantOps.Application.Alarms;
using PlantOps.Application.Telemetry;

namespace PlantOps.Tests;

internal static class RecoveryAlarmFixture
{
    internal static async Task<(AlarmTests.Fixture Fixture, AlarmDto Alarm, DateTimeOffset? RecoveryAt, DateTimeOffset HighAt)>
        Create(AssetManagementTests.Session session, AlarmTests.Clock clock, bool qualifiesForRecovery)
    {
        var fixture = await AlarmTests.Configure(session, new RuleInput("Recovery fixture", ClearBelow: 6, ClearForMs: 3000));
        await using var scope = session.Factory.Services.CreateAsyncScope();
        var ingestor = scope.ServiceProvider.GetRequiredService<IMeasurementIngestor>();
        async Task Observe(double value)
        {
            clock.Now = clock.Now.AddSeconds(1);
            await ingestor.IngestAsync([
                SamplePolicy.Normalize(fixture.Config, value, "Good", clock.Now, null, clock.Now, Guid.NewGuid().ToString("N"))]);
        }

        var triggerWindowStart = clock.Now;
        clock.Now = triggerWindowStart.AddSeconds(-2);
        await Observe(5);
        clock.Now = triggerWindowStart;
        var alarm = await AlarmTests.Raise(session, fixture, clock);
        var lowSamples = qualifiesForRecovery ? 4 : 1;
        for (var i = 0; i < lowSamples; i++)
        {
            await Observe(5);
        }

        DateTimeOffset? recoveryAt = qualifiesForRecovery ? clock.Now : null;
        await Observe(14);
        var highAt = clock.Now;
        clock.Now = clock.Now.AddSeconds(1);
        return (fixture, alarm, recoveryAt, highAt);
    }
}
