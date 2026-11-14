using OrderGenerator;
using OrderGenerator.Application;
using OrderGenerator.Application.State;
using OrderGenerator.Application.Repositories;
using OrderGenerator.Application.Transactions;
using OrderGenerator.Infrastructure.Persistence.Transactions;
using OrderGenerator.Infrastructure.Persistence.Repositories;
using OrderGenerator.Infrastructure.Persistence;
using OrderGenerator.Infrastructure.Persistence.Fix;
using OrderGenerator.Infrastructure.Fix;
using OrderGenerator.Api;
using OrderGenerator.Worker;
using OrderGenerator.Infrastructure.Runtime;

var builder = WebApplication.CreateBuilder(args);
using var data = new DataDirectoryLease(
    builder.Configuration["Storage:Directory"] ?? Path.Combine(
        AppContext.BaseDirectory,
        "data"));
builder.Configuration["Fix:StorePath"] = Path.Combine(data.DirectoryPath, "fix");
var database = OrderGeneratorDatabase.Initialize(
    builder.Configuration.GetConnectionString("Orders") ?? throw new InvalidOperationException("ConnectionStrings:Orders is required"));
using var databaseLifetime = database.AcquireHostLease();
builder.Services.AddSingleton(database);
builder.Services.AddSingleton(databaseLifetime);
builder.Services.AddSingleton<IOrderIntentRepository, PostgresOrderIntentRepository>();
builder.Services.AddSingleton<IOrderIntentUnitOfWorkFactory, PostgresOrderIntentUnitOfWorkFactory>();
builder.Services.AddSingleton<IFixSequenceStore, PostgresFixSequenceStore>();
builder.Services.AddOrderHttp();
builder.Services.AddOrderUseCases(
    builder.Configuration.GetValue(
        "Storage:MaxIntents",
        OrderIntentOptions.DefaultCapacity));
builder.Services.AddOrderProcessingWorker();
var app = builder.Build();
app.UseOrderHttp();
app.MapOrderHttp();
app.MapOrderHealth(() => app.Services.GetRequiredService<IOrderTransport>().Connected && database.IsAvailable());
app.Run();
