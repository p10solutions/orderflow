using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using OrderGenerator;
using OrderGenerator.Application.State;
using OrderGenerator.Domain.Orders;
using OrderGenerator.Infrastructure.Persistence.Transactions;
using Xunit;
using Sql = OrderGenerator.Infrastructure.Persistence.PostgresDatabase;

namespace OrderGenerator.Tests;

public sealed class GeneratorConcurrencyTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected { get; set; } = true;
        public ConcurrentDictionary<string, int> Sends { get; } = new();

        public bool Send(string id, OrderPayload payload)
        {
            Sends.AddOrUpdate(id, 1, (_, count) => count + 1);

            return true;
        }
    }

    private static OrderRequest Request() => new(
        Guid.NewGuid().ToString("D"),
        "PETR4",
        "Buy",
        1,
        "10.00");

    private static TestOrderApplication App(
        Transport transport,
        PostgresOrderIntentRepository journal,
        int capacity = 10000) => new(
        transport,
        NullLogger<TestOrderApplication>.Instance,
        journal,
        capacity);

    private static PostgresOrderIntentRepository Journal(PostgresTestDatabase db) => new(
        OrderGeneratorDatabase.Initialize(
            db.ConnectionString));

    [Fact]
    public async Task Independent_applications_admit_one_identity_and_send_only_once()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        var transport = new Transport();
        var apps = new[]
        {
            App(transport, journal),
            App(transport, Journal(db))
        };
        var request = Request();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 16).Select(
                index => Task.Run(
                    () => apps[index % 2].Submit(
                        request,
                        TimeSpan.Zero,
                        default))));
        Assert.All(results, result => Assert.Equal(SubmissionStatus.PendingConfirmation, result.Status));
        Assert.Equal(1, journal.Count());
        Assert.Equal(1, transport.Sends[request.Id]);
        var conflict = await apps[1].Submit(request with
        {
            Quantity = 2
        }, TimeSpan.Zero, default);
        Assert.Equal(SubmissionStatus.IdentityConflict, conflict.Status);
        Assert.Equal(1, journal.Find(request.Id)!.Payload.Quantity);
    }

    [Fact]
    public async Task Capacity_is_atomic_across_independent_repository_instances()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        var transport = new Transport();
        var apps = new[]
        {
            App(transport, journal, 1),
            App(transport, Journal(db), 1)
        };
        var requests = Enumerable.Range(0, 12).Select(_ => Request()).ToArray();
        var results = await Task.WhenAll(
            requests.Select(
                (request, index) => Task.Run(
                    () => apps[index % 2].Submit(
                        request,
                        TimeSpan.Zero,
                        default))));
        Assert.Equal(1, results.Count(result => result.Status == SubmissionStatus.PendingConfirmation));
        Assert.Equal(11, results.Count(result => result.Status == SubmissionStatus.CapacityReached));
        var admitted = Assert.Single(journal.GetAll());
        Assert.Single(transport.Sends);
        transport.Connected = false;
        var retry = requests.Single(request => request.Id == admitted.Id);
        Assert.Equal(SubmissionStatus.PendingConfirmation, (await apps[0].Submit(retry, TimeSpan.Zero, default)).Status);
        Assert.Equal(1, transport.Sends[admitted.Id]);
    }

    [Fact]
    public async Task An_uncommitted_identity_does_not_block_reads_or_other_identity_updates()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        var first = Request();
        var second = Request();
        journal.Save(new(
            first.Id,
            new(
                "PETR4",
                "Buy",
                1,
                10m),
            null,
            null));
        journal.Save(new(
            second.Id,
            new(
                "PETR4",
                "Buy",
                1,
                10m),
            null,
            null));
        var app = App(new Transport(), journal);

        using (var held = new PostgresOrderIntentUnitOfWorkFactory(journal.Database).BeginIntent(first.Id))
        {
            var previous = held.Intents.Find(first.Id)!;
            held.Intents.Save(previous with
            {
                Diagnostic = "not committed"
            });
            var read = await Task.Run(() => app.Get(first.Id)).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(read!.Diagnostic);
            await Task.Run(() => app.Diagnose(new(second.Id, "other identity"))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("other identity", app.Get(second.Id)!.Diagnostic);
        }

        Assert.Null(journal.Find(first.Id)!.Diagnostic);
    }

    [Fact]
    public async Task Concurrent_recovery_workers_claim_disjoint_batches_and_preserve_retry_interval()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        var now = DateTimeOffset.UtcNow;

        for (var index = 0; index < 8; index++)
        {
            journal.Save(new(
                Guid.NewGuid().ToString("D"),
                new(
                    "PETR4",
                    "Buy",
                    1,
                    10m),
                null,
                null,
                now.AddMinutes(-1)));
        }

        var transport = new Transport();
        var apps = new[]
        {
            App(transport, journal),
            App(transport, Journal(db))
        };
        var counts = await Task.WhenAll(
            apps.Select(
                app => Task.Run(
                    () => app.RecoverPending(
                        now,
                        TimeSpan.FromSeconds(15),
                        4))));
        Assert.Equal(8, counts.Sum());
        Assert.Equal(8, transport.Sends.Count);
        Assert.All(transport.Sends.Values, count => Assert.Equal(1, count));
        var reopened = App(transport, Journal(db));
        Assert.Equal(0, reopened.RecoverPending(now.AddSeconds(1), TimeSpan.FromSeconds(15)));
        Assert.All(journal.GetAll(), intent => Assert.NotNull(intent.LastAttempt));
    }

    [Fact]
    public void Failure_in_second_recovery_update_rolls_back_entire_batch_and_sends_nothing()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        const string first = "00000000-0000-0000-0000-000000000001";
        const string blocked = "00000000-0000-0000-0000-000000000002";

        foreach (var id in new[]
        {
            first,
            blocked
        }

        )
        {
            journal.Save(new(
                id,
                new(
                    "PETR4",
                    "Buy",
                    1,
                    10m),
                null,
                null));
        }

        using (var connection = journal.Database.Open())
        {
            Sql.Execute(
                connection,
                null,
                "ALTER TABLE intents ADD CONSTRAINT fail_second_attempt CHECK (id<>'00000000-0000-0000-0000-000000000002' OR last_attempt_utc IS NULL)");
        }

        var transport = new Transport();
        Assert.Throws<Npgsql.PostgresException>(
            () => App(transport, journal).RecoverPending(
                DateTimeOffset.UtcNow,
                TimeSpan.Zero));
        Assert.Empty(transport.Sends);
        Assert.All(journal.GetAll(), intent => Assert.Null(intent.LastAttempt));
    }

    [Fact]
    public async Task Conflicting_confirmations_from_independent_connections_preserve_one_decision()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        var request = Request();
        journal.Save(new(
            request.Id,
            new(
                "PETR4",
                "Buy",
                1,
                10m),
            null,
            null));
        var apps = new[]
        {
            App(new Transport(), journal),
            App(new Transport(), Journal(db))
        };
        var accepted = new OrderResult(
            request.Id,
            "Accepted",
            "accepted-order",
            "accepted-exec");
        var rejected = new OrderResult(
            request.Id,
            "Rejected",
            "rejected-order",
            "rejected-exec",
            "Rejected");
        await Task.WhenAll(
            Enumerable.Range(0, 12).Select(
                index => Task.Run(
                    () => apps[index % 2].Confirm(
                        index % 2 == 0 ? accepted : rejected))));
        var stored = journal.Find(request.Id)!;
        Assert.Contains(stored.Result, new[] { accepted, rejected });
        Assert.Equal(OrderIntent.ContradictoryReportDiagnostic, stored.Diagnostic);
        Assert.Equal(stored.Result! with
        {
            Diagnostic = stored.Diagnostic
        }, apps[0].Get(request.Id));
    }

    [Fact]
    public async Task Failed_confirmation_commit_does_not_wake_waiter_or_publish_terminal_state()
    {
        using var db = new PostgresTestDatabase();
        var journal = Journal(db);
        var id = Guid.NewGuid().ToString("D");
        journal.Save(new(
            id,
            new(
                "PETR4",
                "Buy",
                1,
                10m),
            null,
            null));
        var notifications = new OrderConfirmationNotifier();
        var confirm = new ConfirmOrderUseCase(
            new PostgresOrderIntentUnitOfWorkFactory(journal.Database),
            notifications,
            NullLogger<ConfirmOrderUseCase>.Instance,
            new ConfirmOrderInputValidator());
        using var waiter = notifications.Subscribe(id);

        using (var connection = journal.Database.Open())
        {
            Sql.Execute(connection, null, "ALTER TABLE intents ADD CONSTRAINT fail_confirmation CHECK(result IS NULL)");
        }

        Assert.Throws<Npgsql.PostgresException>(() => confirm.Execute(new(
            id,
            "Accepted",
            "order",
            "exec")));
        Assert.False(waiter.Completion.IsCompleted);
        Assert.Null(journal.Find(id)!.Result);

        using (var connection = journal.Database.Open())
        {
            Sql.Execute(connection, null, "ALTER TABLE intents DROP CONSTRAINT fail_confirmation");
        }

        Assert.True(confirm.Execute(new(
            id,
            "Accepted",
            "order",
            "exec")).IsSuccess);
        await waiter.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Accepted", journal.Find(id)!.Result!.Status);
    }
}
