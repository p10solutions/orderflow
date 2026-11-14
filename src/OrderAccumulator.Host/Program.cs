using OrderAccumulator.Application.UseCases.GetExposures;
using OrderAccumulator.Application.Repositories;
using OrderAccumulator.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderAccumulator.Worker;
using OrderAccumulator.Application;
using OrderAccumulator.Application.Transactions;
using OrderAccumulator.Application.UseCases.ProcessOrder;
using OrderAccumulator.Infrastructure.Persistence.Transactions;
using OrderAccumulator.Infrastructure.Persistence;
using OrderAccumulator.Infrastructure;
using OrderAccumulator.Infrastructure.Runtime;

var builder = Host.CreateApplicationBuilder(args);
using var data = new DataDirectoryLease(
    builder.Configuration["Storage:Directory"] ?? Path.Combine(
        AppContext.BaseDirectory,
        "data"));
builder.Configuration["Fix:StorePath"] = Path.Combine(data.DirectoryPath, "fix");
var database = OrderAccumulatorDatabase.Initialize(
    builder.Configuration.GetConnectionString("Orders") ?? throw new InvalidOperationException("ConnectionStrings:Orders is required"));
using var databaseLifetime = database.AcquireHostLease();
builder.Services.AddSingleton(database);
builder.Services.AddSingleton<IExposureSnapshotRepository, PostgresExposureSnapshotRepository>();
builder.Services.AddSingleton<FluentValidation.IValidator<GetExposuresInput>, GetExposuresInputValidator>();
builder.Services.AddSingleton<IGetExposuresUseCase, GetExposuresUseCase>();
builder.Services.AddSingleton<IOrderUnitOfWorkFactory, PostgresOrderUnitOfWorkFactory>();
builder.Services.AddSingleton<FluentValidation.IValidator<ProcessOrderInput>, ProcessOrderInputValidator>();
builder.Services.AddSingleton<IProcessOrderUseCase, ProcessOrderUseCase>();
builder.Services.AddOrderProcessingWorker();
await builder.Build().RunAsync();
