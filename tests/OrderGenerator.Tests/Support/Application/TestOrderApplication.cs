using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderGenerator;
using OrderGenerator.Application;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.State;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Tests;

/// <summary>Regression fixture only. Production adapters depend on individual use cases.</summary>
internal sealed class TestOrderApplication
{
    private readonly ISubmitOrderUseCase submit;
    private readonly IGetOrderUseCase get;
    private readonly IConfirmOrderUseCase confirm;
    private readonly IRecordProtocolDiagnosticUseCase diagnose;
    private readonly IGetKnownOrdersUseCase known;
    private readonly IRecoverPendingOrdersUseCase recover;
    public const int Capacity = OrderIntentOptions.DefaultCapacity;

    public TestOrderApplication(
        IOrderTransport transport,
        ILogger<TestOrderApplication> logger,
        IOrderIntentRepository? repository = null,
        int capacity = Capacity)
    {
        repository ??= new InMemoryOrderIntentRepository();
        var transactions = TestOrderIntentUnitOfWorkFactory.For(repository);
        var notifications = new OrderConfirmationNotifier();
        get = new GetOrderUseCase(repository, new GetOrderInputValidator());
        submit = new SubmitOrderUseCase(
            transactions,
            new OrderIntentOptions(capacity),
            notifications,
            transport,
            get,
            new ForwardLogger<SubmitOrderUseCase>(
                logger),
            new SubmitOrderInputValidator());
        confirm = new ConfirmOrderUseCase(
            transactions,
            notifications,
            new ForwardLogger<ConfirmOrderUseCase>(logger),
            new ConfirmOrderInputValidator());
        diagnose = new RecordProtocolDiagnosticUseCase(
            transactions,
            new ForwardLogger<RecordProtocolDiagnosticUseCase>(
                logger),
            new RecordProtocolDiagnosticInputValidator());
        known = new GetKnownOrdersUseCase(repository, new GetKnownOrdersInputValidator());
        recover = new RecoverPendingOrdersUseCase(
            transactions,
            transport,
            new ForwardLogger<RecoverPendingOrdersUseCase>(
                logger),
            new RecoverPendingOrdersInputValidator());
    }

    private TestOrderApplication(IServiceProvider services)
    {
        submit = services.GetRequiredService<ISubmitOrderUseCase>();
        get = services.GetRequiredService<IGetOrderUseCase>();
        confirm = services.GetRequiredService<IConfirmOrderUseCase>();
        diagnose = services.GetRequiredService<IRecordProtocolDiagnosticUseCase>();
        known = services.GetRequiredService<IGetKnownOrdersUseCase>();
        recover = services.GetRequiredService<IRecoverPendingOrdersUseCase>();
    }

    internal static TestOrderApplication FromServices(IServiceProvider services) => new(services);

    public static string? Validate(OrderRequest request, out OrderPayload payload)
    {
        var result = new SubmitOrderInputValidator().Validate(request.ToSubmitOrderInput(TimeSpan.Zero));
        payload = new(
            request.Symbol,
            request.Side,
            request.Quantity,
            result.IsValid
            ? decimal.Parse(
                    request.Price,
                    System.Globalization.CultureInfo.InvariantCulture)
            : 0m);

        return result.IsValid ? null : result.Errors[0].ErrorMessage;
    }

    public async Task<Submission> Submit(
        OrderRequest request,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var result = await submit.ExecuteAsync(request.ToSubmitOrderInput(timeout), ct);

        if (result.IsSuccess)
        {
            return new(
                result.Value.Status == "PendingConfirmation"
                ? SubmissionStatus.PendingConfirmation
                : SubmissionStatus.Confirmed,
                result.Value.ToTestOrderResult());
        }

        var error = result.Error;
        var status = error.Code switch
        {
            "ValidationFailed" => SubmissionStatus.ValidationFailed,
            "IdentityConflict" => SubmissionStatus.IdentityConflict,
            "FixUnavailable" => SubmissionStatus.TransportUnavailable,
            "RegistryCapacityReached" => SubmissionStatus.CapacityReached,
            _ => throw new InvalidOperationException(error.Code)
        };

        return new(status, new OrderError(error.Message, error.Code, error.Fields));
    }

    public OrderResult? Get(string id) => get.Execute(new(id)) is { IsSuccess: true } result ? result.Value.ToTestOrderResult() : null;

    public void Confirm(OrderResult result) => confirm.Execute(result.ToConfirmOrderInput());

    public void Diagnose(ProtocolDiagnostic diagnostic) => diagnose.Execute(new(diagnostic.Id, diagnostic.Message));

    public IReadOnlyDictionary<string, OrderPayload> KnownOrders() => known.Execute(new()).Value.ToCorrelation();

    public int RecoverPending(
        DateTimeOffset now,
        TimeSpan interval,
        int batchSize = 64) => recover.Execute(
        new(
            now,
            interval,
            batchSize)).Value.AttemptedCount;

    private sealed class ForwardLogger<T>(ILogger logger) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => logger.BeginScope(state);

        public bool IsEnabled(LogLevel level) => logger.IsEnabled(level);

        public void Log<TState>(
            LogLevel level,
            EventId id,
            TState state,
            Exception? error,
            Func<TState, Exception?, string> formatter) => logger.Log(
            level,
            id,
            state,
            error,
            formatter);
    }
}

internal static class TestOrderApplicationRegistration
{
    public static void AddTestOrderApplication(this IServiceCollection services)
    {
        services.AddSingleton<IOrderIntentRepository, InMemoryOrderIntentRepository>();
        services.AddSingleton<IOrderIntentUnitOfWorkFactory, TestOrderIntentUnitOfWorkFactory>();
        services.AddOrderUseCases();
        services.AddSingleton<TestOrderApplication>(TestOrderApplication.FromServices);
    }
}
