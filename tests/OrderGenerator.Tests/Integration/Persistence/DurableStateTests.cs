using OrderGenerator.Infrastructure.Runtime;
using PostgresDatabase = OrderAccumulator.Infrastructure.Persistence.PostgresDatabase;
using Microsoft.Extensions.Logging.Abstractions;
using OrderAccumulator.Application;
using OrderAccumulator.Domain;
using OrderAccumulator.Infrastructure;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class DurableStateTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "orderflow-tests",
        Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, PostgresTestDatabase> databases = new();

    private string DatabasePath(string name)
    {
        if (!databases.TryGetValue(name, out var db))
        {
            databases[name] = db = new();
        }

        return db.ConnectionString;
    }

    public void Dispose()
    {
        foreach (var db in databases.Values)
        {
            db.Dispose();
        }

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void R16_Decisions_and_exact_exposure_survive_reopening_including_rejections_and_conflicts()
    {
        var store = new PostgresOrderUnitOfWorkFactory(OrderAccumulatorDatabase.Initialize(DatabasePath("accumulator")));
        var processor = new ProcessOrderUseCase(store, new ProcessOrderInputValidator());
        var order = new Order(
            "PETR4",
            OrderSide.Buy,
            99999,
            999.99m);
        var accepted = processor.Execute((order).ToTestProcessInput("session", "accepted", "original")).Value.ToTestDecision();
        var rejected = processor.Execute((order).ToTestProcessInput("session", "rejected")).Value.ToTestDecision();
        Assert.True(accepted.Accepted);
        Assert.False(rejected.Accepted);
        var reopened = new PostgresOrderUnitOfWorkFactory(
            OrderAccumulatorDatabase.Initialize(
                DatabasePath("accumulator")));
        var recovered = new ProcessOrderUseCase(reopened, new ProcessOrderInputValidator());
        Assert.Equal(
            accepted,
            recovered.Execute((order).ToTestProcessInput("session", "accepted", "original")).Value.ToTestDecision());
        Assert.Equal(
            "ClOrdID payload conflict",
            recovered.Execute(
                (order).ToTestProcessInput(
                    "session",
                    "accepted",
                    "changed")).Value.ToTestDecision().Reason);
        Assert.Equal(99_998_000.01m, reopened.Exposure("PETR4"));
        recovered.Execute((order with
        {
            Side = OrderSide.Sell
        }).ToTestProcessInput("session", "sell"));
        Assert.Equal(
            rejected,
            recovered.Execute((order).ToTestProcessInput("session", "rejected")).Value.ToTestDecision());
        Assert.Equal(0m, reopened.Exposure("PETR4"));
    }

    [Fact]
    public async Task R16_Separate_connections_serialize_concurrent_duplicates_and_limit_decisions()
    {
        var path = DatabasePath("concurrent");
        var first = new ProcessOrderUseCase(
            new PostgresOrderUnitOfWorkFactory(
                OrderAccumulatorDatabase.Initialize(path)),
            new ProcessOrderInputValidator());
        var second = new ProcessOrderUseCase(
            new PostgresOrderUnitOfWorkFactory(
                OrderAccumulatorDatabase.Initialize(
                    path)),
            new ProcessOrderInputValidator());
        var order = new Order(
            "VALE3",
            OrderSide.Buy,
            99999,
            999.99m);
        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(
                i => Task.Run(
                    () => (i % 2 == 0 ? first : second).Execute(
                        (order).ToTestProcessInput(
                            "session",
                            "same")).Value.ToTestDecision())));
        Assert.Single(decisions.Distinct());
        var competitors = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(
                i => Task.Run(
                    () => (i % 2 == 0 ? first : second).Execute(
                        (order).ToTestProcessInput(
                            "session",
                            "other-" + i)).Value.ToTestDecision())));
        Assert.All(competitors, decision => Assert.False(decision.Accepted));
        Assert.Equal(
            99_998_000.01m,
            new PostgresOrderUnitOfWorkFactory(OrderAccumulatorDatabase.Initialize(path)).Exposure("VALE3"));
    }

    [Fact]
    public void R16_Failed_decision_insert_rolls_back_exposure_update()
    {
        var store = new PostgresOrderUnitOfWorkFactory(OrderAccumulatorDatabase.Initialize(DatabasePath("rollback")));

        using (var connection = store.Database.Open())
        {
            PostgresDatabase.Execute(
                connection,
                null,
                """
                ALTER TABLE decisions ADD CONSTRAINT fail_decision CHECK (false);
                """);
        }

        var processor = new ProcessOrderUseCase(store, new ProcessOrderInputValidator());
        var order = new Order(
            "VIIA4",
            OrderSide.Buy,
            1,
            10.01m);
        Assert.Throws<Npgsql.PostgresException>(
            () => processor.Execute((order).ToTestProcessInput("session", "id")).Value.ToTestDecision());
        Assert.Equal(0m, store.Exposure("VIIA4"));

        using (var connection = store.Database.Open())
        {
            PostgresDatabase.Execute(connection, null, "ALTER TABLE decisions DROP CONSTRAINT fail_decision;");
        }

        Assert.True(processor.Execute((order).ToTestProcessInput("session", "id")).Value.ToTestDecision().Accepted);
        Assert.Equal(10.01m, store.Exposure("VIIA4"));
    }

    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;
        public List<string> Sent { get; } = [];
        public Action<string>? BeforeSend
        {
            get; init;
        }

        public bool Send(string id, OrderPayload order)
        {
            BeforeSend?.Invoke(id);
            Sent.Add(id);

            return true;
        }
    }

    [Fact]
    public async Task R16_Persist_before_send_recover_pending_and_persist_confirmation_before_return()
    {
        var path = DatabasePath("generator");
        var request = new OrderRequest(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            5,
            "10.01");
        var transport = new Transport
        {
            BeforeSend = id => Assert.Equal(
                id,
                Assert.Single(
                    new PostgresOrderIntentRepository(
                        OrderGeneratorDatabase.Initialize(
                            path)).GetAll()).Id)
        };
        var registry = new TestOrderApplication(
            transport,
            NullLogger<TestOrderApplication>.Instance,
            new PostgresOrderIntentRepository(
                OrderGeneratorDatabase.Initialize(
                    path)));
        Assert.Equal(
            SubmissionStatus.PendingConfirmation,
            (await registry.Submit(
                request,
                TimeSpan.Zero,
                CancellationToken.None)).Status);
        var recovered = new TestOrderApplication(
            transport,
            NullLogger<TestOrderApplication>.Instance,
            new PostgresOrderIntentRepository(
                OrderGeneratorDatabase.Initialize(
                    path)));
        Assert.Equal("PendingConfirmation", recovered.Get(request.Id)!.Status);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, recovered.RecoverPending(now, TimeSpan.FromSeconds(15)));
        Assert.Equal(1, recovered.RecoverPending(now.AddSeconds(16), TimeSpan.FromSeconds(15)));
        Assert.Equal(0, recovered.RecoverPending(now.AddSeconds(16), TimeSpan.FromSeconds(15)));
        var result = new OrderResult(
            request.Id,
            "Accepted",
            "order",
            "exec",
            "Accepted");
        recovered.Confirm(result);
        var terminal = new TestOrderApplication(
            transport,
            NullLogger<TestOrderApplication>.Instance,
            new PostgresOrderIntentRepository(
                OrderGeneratorDatabase.Initialize(
                    path)));
        Assert.Equal(result, terminal.Get(request.Id));
        Assert.Equal(0, terminal.RecoverPending(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.Zero));
        Assert.Equal(
            SubmissionStatus.Confirmed,
            (await terminal.Submit(request, TimeSpan.Zero, CancellationToken.None)).Status);
        Assert.Equal(
            SubmissionStatus.IdentityConflict,
            (await terminal.Submit(
                request with
                {
                    Quantity = 6
                },
                TimeSpan.Zero,
                CancellationToken.None)).Status);
        Assert.Equal(2, transport.Sent.Count);
    }

    [Fact]
    public async Task R16_Protocol_diagnostic_survives_restart_and_suspends_automatic_delivery()
    {
        var path = DatabasePath("diagnostics");
        var transport = new Transport();
        var request = new OrderRequest(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            1,
            "10");
        var registry = new TestOrderApplication(
            transport,
            NullLogger<TestOrderApplication>.Instance,
            new PostgresOrderIntentRepository(
                OrderGeneratorDatabase.Initialize(
                    path)));
        await registry.Submit(request, TimeSpan.Zero, CancellationToken.None);
        registry.Diagnose(new(request.Id, "Protocol rejection"));
        var recovered = new TestOrderApplication(
            transport,
            NullLogger<TestOrderApplication>.Instance,
            new PostgresOrderIntentRepository(
                OrderGeneratorDatabase.Initialize(
                    path)));
        Assert.Equal("Protocol rejection", recovered.Get(request.Id)!.Diagnostic);
        Assert.Equal(0, recovered.RecoverPending(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.Zero));
    }

    [Fact]
    public void R16_Same_data_directory_cannot_be_owned_by_two_hosts()
    {
        using (var first = new DataDirectoryLease(directory))
        {
            Assert.Throws<IOException>(() => new DataDirectoryLease(directory));
        }

        using var afterRestart = new DataDirectoryLease(directory);
    }

    [Fact]
    public async Task R16_Failed_intent_commit_never_sends_and_failed_confirmation_remains_pending()
    {
        var path = DatabasePath("failures");
        var journal = new PostgresOrderIntentRepository(OrderGeneratorDatabase.Initialize(path));
        var transport = new Transport();
        var registry = new TestOrderApplication(transport, NullLogger<TestOrderApplication>.Instance, journal);
        var request = new OrderRequest(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            1,
            "10");

        using (var connection = journal.Database.Open())
        {
            PostgresDatabase.Execute(connection, null, "ALTER TABLE intents ADD CONSTRAINT fail_intent CHECK (false);");
        }

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => registry.Submit(request, TimeSpan.Zero, default));
        Assert.Empty(transport.Sent);
        Assert.Null(registry.Get(request.Id));

        using (var connection = journal.Database.Open())
        {
            PostgresDatabase.Execute(connection, null, "ALTER TABLE intents DROP CONSTRAINT fail_intent;");
        }

        await registry.Submit(request, TimeSpan.Zero, default);

        using (var connection = journal.Database.Open())
        {
            PostgresDatabase.Execute(
                connection,
                null,
                "ALTER TABLE intents ADD CONSTRAINT fail_result CHECK (result IS NULL);");
        }

        var result = new OrderResult(
            request.Id,
            "Accepted",
            "order",
            "exec");
        Assert.Throws<Npgsql.PostgresException>(() => registry.Confirm(result));
        Assert.Equal("PendingConfirmation", registry.Get(request.Id)!.Status);
        Assert.Null(Assert.Single(journal.GetAll()).Result);

        using (var connection = journal.Database.Open())
        {
            PostgresDatabase.Execute(connection, null, "ALTER TABLE intents DROP CONSTRAINT fail_result;");
        }

        registry.Confirm(result);
        Assert.Equal(
            result,
            Assert.Single(
                new PostgresOrderIntentRepository(OrderGeneratorDatabase.Initialize(path)).GetAll()).Result);
    }

    [Fact]
    public void R16_Ordered_schema_upgrade_preserves_intents_and_newer_schema_is_rejected()
    {
        var path = DatabasePath("migration");
        var journal = new PostgresOrderIntentRepository(OrderGeneratorDatabase.Initialize(path));
        var intent = new StoredIntent(
            "identity",
            new(
                "PETR4",
                "Buy",
                1,
                10m),
            null,
            null);
        journal.Save(intent);
        Assert.Equal(intent, journal.Find(intent.Id));
        Assert.Null(journal.Find("unknown"));

        using (var connection = journal.Database.Open())
        {
            PostgresDatabase.Execute(
                connection,
                null,
                "DROP TABLE fix_sequences; ALTER TABLE intents DROP COLUMN last_attempt_utc; UPDATE schema_version SET version=1;");
        }

        var upgraded = new PostgresOrderIntentRepository(OrderGeneratorDatabase.Initialize(path));
        Assert.Equal(intent, Assert.Single(upgraded.GetAll()));
        new PostgresFixSequenceStore(upgraded.Database).Record(42, intent.Id);
        Assert.Equal(intent.Id, new PostgresFixSequenceStore(upgraded.Database).ReadMappings()[42]);

        using (var connection = upgraded.Database.Open())
        {
            PostgresDatabase.Execute(connection, null, "UPDATE schema_version SET version=99;");
        }

        Assert.Throws<InvalidOperationException>(
            () => new PostgresOrderIntentRepository(
                OrderGeneratorDatabase.Initialize(
                    path)));
        Assert.Equal(intent, Assert.Single(upgraded.GetAll()));
    }
}
