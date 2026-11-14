using Microsoft.Extensions.DependencyInjection;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.State;
using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Application;

public static class Registration
{
    public static IServiceCollection AddOrderUseCases(
        this IServiceCollection services,
        int capacity = OrderIntentOptions.DefaultCapacity)
    {
        services.AddSingleton(new OrderIntentOptions(capacity));
        services.AddSingleton<IValidator<GetExposuresInput>, GetExposuresInputValidator>();
        services.AddSingleton<IGetExposuresUseCase, GetExposuresUseCase>();
        services.AddSingleton<OrderConfirmationNotifier>();
        services.AddSingleton<IValidator<SubmitOrderInput>, SubmitOrderInputValidator>();
        services.AddSingleton<ISubmitOrderUseCase, SubmitOrderUseCase>();
        services.AddSingleton<IValidator<GetOrderInput>, GetOrderInputValidator>();
        services.AddSingleton<IGetOrderUseCase, GetOrderUseCase>();
        services.AddSingleton<IValidator<ConfirmOrderInput>, ConfirmOrderInputValidator>();
        services.AddSingleton<IConfirmOrderUseCase, ConfirmOrderUseCase>();
        services.AddSingleton<IValidator<RecordProtocolDiagnosticInput>, RecordProtocolDiagnosticInputValidator>();
        services.AddSingleton<IRecordProtocolDiagnosticUseCase, RecordProtocolDiagnosticUseCase>();
        services.AddSingleton<IValidator<GetKnownOrdersInput>, GetKnownOrdersInputValidator>();
        services.AddSingleton<IGetKnownOrdersUseCase, GetKnownOrdersUseCase>();
        services.AddSingleton<IValidator<RecoverPendingOrdersInput>, RecoverPendingOrdersInputValidator>();
        services.AddSingleton<IRecoverPendingOrdersUseCase, RecoverPendingOrdersUseCase>();

        return services;
    }
}
