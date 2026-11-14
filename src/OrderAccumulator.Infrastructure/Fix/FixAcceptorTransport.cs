using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QuickFix;
using QuickFix.Store;

namespace OrderAccumulator.Infrastructure;

/// <summary>Owns the FIX engine and store; the inbound application is supplied by Entrypoints.</summary>
public sealed class FixAcceptorTransport(
    IApplication application,
    IConfiguration configuration,
    ILoggerFactory loggerFactory) : IHostedService, IDisposable
{
    private ThreadedSocketAcceptor? engine;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = CreateSettings(configuration);
        IMessageStoreFactory store = configuration["Fix:StorePath"] is null ? new MemoryStoreFactory() : new FileStoreFactory(
            settings);
        engine = new ThreadedSocketAcceptor(
            application,
            store,
            settings,
            loggerFactory);
        engine.Start();

        return Task.CompletedTask;
    }

    public static SessionSettings CreateSettings(IConfiguration configuration)
    {
        var dictionary = Path.Combine(AppContext.BaseDirectory, "FIX44.xml").Replace('\\', '/');

        return new SessionSettings(
            new StringReader(
                $"""
            [DEFAULT]
            ConnectionType=acceptor
            NonStopSession=Y
            UseDataDictionary=Y
            DataDictionary={dictionary}
            ResetOnLogon=N
            ResetOnLogout=N
            ResetOnDisconnect=N
            FileStorePath={configuration["Fix:StorePath"] ?? "unused"}
            SocketAcceptPort={configuration.GetValue("Fix:Port", 5001)}
            SocketAcceptHost={configuration["Fix:AcceptHost"] ?? "127.0.0.1"}
            [SESSION]
            BeginString=FIX.4.4
            SenderCompID=ACCUMULATOR
            TargetCompID=GENERATOR
            """));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        engine?.Stop();

        return Task.CompletedTask;
    }

    public void Dispose() => engine?.Dispose();
}
