using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderAccumulator;
using OrderAccumulator.Domain;
using OrderGenerator;
using QuickFix;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class SessionLifetimeTests
{
    [Fact]
    public void R2_Actual_host_settings_preserve_session_across_utc_midnight()
    {
        var config = new ConfigurationBuilder().Build();
        using var initiator = new FixInitiator(config, NullLogger<FixInitiator>.Instance, NullLoggerFactory.Instance);
        using var acceptor = new FixAcceptor(
            new InMemoryOrderProcessor(new ExposureBook()),
            config,
            NullLogger<FixAcceptor>.Instance,
            NullLoggerFactory.Instance);
        var before = new DateTime(
            2026,
            11,
            9,
            23,
            59,
            59,
            DateTimeKind.Utc);

        foreach (var settings in new[]
        {
            initiator.CreateSettings(),
            acceptor.CreateSettings()
        }

        )
        {
            var dictionary = settings.Get(settings.GetSessions().Single());
            var schedule = new SessionSchedule(dictionary);
            Assert.True(schedule.NonStopSession);
            Assert.False(schedule.IsNewSession(before, before.AddSeconds(2)));
            Assert.False(schedule.IsNewSession(before, before.AddDays(7)));
            Assert.True(schedule.IsSessionTime(before.AddSeconds(2)));
            Assert.False(dictionary.GetBool("ResetOnLogon"));
            Assert.False(dictionary.GetBool("ResetOnLogout"));
            Assert.False(dictionary.GetBool("ResetOnDisconnect"));
        }
    }
}
