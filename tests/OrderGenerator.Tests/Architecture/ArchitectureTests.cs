using OrderAccumulator.Application;
using OrderAccumulator.Domain;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Http_request_contract_belongs_to_api_and_does_not_leak_into_application_input()
    {
        Assert.Same(typeof(OrderEndpoints).Assembly, typeof(OrderRequest).Assembly);
        Assert.DoesNotContain(
            typeof(ISubmitOrderUseCase).Assembly.GetTypes(),
            type => type.Name == nameof(OrderRequest));
        Assert.DoesNotContain(
            typeof(SubmitOrderInput).GetProperties(),
            property => property.PropertyType.Assembly == typeof(OrderRequest).Assembly);
    }

    [Fact]
    public void Use_case_contracts_have_specific_input_and_result_output()
    {
        var contracts = typeof(ISubmitOrderUseCase).Assembly.GetTypes().Where(
            type => type.IsInterface && type.Name.EndsWith("UseCase")).Append(
            typeof(IProcessOrderUseCase));

        foreach (var contract in contracts)
        {
            var operation = contract.Name[1..^"UseCase".Length];
            var method = Assert.Single(contract.GetMethods());
            var input = Assert.Single(
                method.GetParameters(),
                parameter => parameter.ParameterType != typeof(CancellationToken));
            Assert.Equal(operation + "Input", input.ParameterType.Name);
            var result = method.ReturnType;

            if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>))
            {
                result = result.GetGenericArguments()[0];
            }

            Assert.Equal(typeof(OrderFlow.Shared.Result<>), result.GetGenericTypeDefinition());
            Assert.Equal(operation + "Output", Assert.Single(result.GetGenericArguments()).Name);
        }
    }

    [Fact]
    public void Use_case_data_contracts_do_not_wrap_domain_transport_or_other_operation_models()
    {
        var interfaces = typeof(ISubmitOrderUseCase).Assembly.GetTypes().Where(
            type => type.IsInterface && type.Name.EndsWith("UseCase")).Append(
            typeof(IProcessOrderUseCase));

        foreach (var contract in interfaces)
        {
            var operation = contract.Name[1..^"UseCase".Length];
            var method = Assert.Single(contract.GetMethods());
            var input = Assert.Single(
                method.GetParameters(),
                parameter => parameter.ParameterType != typeof(CancellationToken));
            var result = method.ReturnType;

            if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>))
            {
                result = result.GetGenericArguments()[0];
            }

            AssertContractData(
                input.ParameterType,
                operation,
                contract.Assembly,
                []);
            AssertContractData(
                result.GetGenericArguments()[0],
                operation,
                contract.Assembly,
                []);
        }
    }

    private static void AssertContractData(
        Type type,
        string operation,
        System.Reflection.Assembly owner,
        HashSet<Type> visited)
    {
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(TimeSpan) || type == typeof(DateTimeOffset) || type == typeof(Guid))
        {
            return;
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            AssertContractData(
                underlying,
                operation,
                owner,
                visited);

            return;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            AssertContractData(
                type.GetGenericArguments()[0],
                operation,
                owner,
                visited);

            return;
        }

        Assert.True(
            type.Assembly == owner && type.Name.StartsWith(operation, StringComparison.Ordinal),
            $"{operation} exposes a coupled type: {type.FullName}");

        if (!visited.Add(type))
        {
            return;
        }

        foreach (var property in type.GetProperties())
        {
            AssertContractData(
                property.PropertyType,
                operation,
                owner,
                visited);
        }
    }

    [Fact]
    public void Generator_consumers_depend_on_use_case_interfaces()
    {
        var consumers = new[]
        {
            typeof(OrderEndpoints),
            typeof(FixReportConsumer),
            typeof(OrderRecoveryWorker),
            typeof(SubmitOrderUseCase)
        };
        var dependencies = consumers.SelectMany(
            type => type.GetConstructors().SelectMany(ctor => ctor.GetParameters()).Concat(
                type.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly).SelectMany(
                    method => method.GetParameters()))).Select(
            parameter => parameter.ParameterType).Where(
            type => type.Namespace == typeof(ISubmitOrderUseCase).Namespace && type.Name.EndsWith("UseCase")).ToArray();
        Assert.NotEmpty(dependencies);
        Assert.All(
            dependencies,
            type => Assert.True(
                type.IsInterface,
                $"Consumer depends on concrete use case {type.Name}"));
    }

    [Fact]
    public void Domain_and_application_do_not_reference_transport_database_or_hosts()
    {
        foreach (var assembly in new[]
        {
            typeof(Order).Assembly,
            typeof(OrderPayload).Assembly,
            typeof(ProcessOrderUseCase).Assembly,
            typeof(SubmitOrderUseCase).Assembly
        }

        )
        {
            var references = assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray();
            Assert.DoesNotContain(
                references,
                name => name.StartsWith("QuickFix", StringComparison.OrdinalIgnoreCase) || name.Contains("Sqlite") || name.Contains("Npgsql") || name.Contains("Infrastructure") || name.StartsWith("Microsoft.AspNetCore") || name is "OrderGenerator" or "OrderAccumulator");
        }

        Assert.DoesNotContain(
            typeof(SubmitOrderUseCase).Assembly.GetReferencedAssemblies(),
            name => name.Name!.StartsWith("OrderAccumulator"));
    }

    [Fact]
    public void Inbound_workers_and_consumers_belong_to_entrypoints_and_infrastructure_has_no_reverse_dependency()
    {
        Assert.Equal("OrderGenerator.Api", typeof(OrderEndpoints).Assembly.GetName().Name);
        Assert.Equal("OrderGenerator.Worker", typeof(OrderRecoveryWorker).Assembly.GetName().Name);
        Assert.Same(typeof(OrderRecoveryWorker).Assembly, typeof(FixReportConsumer).Assembly);
        Assert.Equal("OrderAccumulator.Worker", typeof(FixAcceptor).Assembly.GetName().Name);

        foreach (var assembly in new[]
        {
            typeof(FixInitiator).Assembly,
            typeof(OrderAccumulator.Infrastructure.FixAcceptorTransport).Assembly
        }

        )
        {
            Assert.DoesNotContain(
                assembly.GetReferencedAssemblies(),
                reference => reference.Name is "OrderGenerator" or "OrderAccumulator" or "OrderGenerator.Api" or "OrderGenerator.Worker" or "OrderAccumulator.Worker" || reference.Name!.Contains("Entrypoints"));
        }
    }

    [Fact]
    public void Api_and_worker_adapters_do_not_reference_each_other_and_api_has_no_infrastructure()
    {
        Assert.DoesNotContain(
            typeof(OrderEndpoints).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.Contains("Worker") || reference.Name.Contains("Infrastructure") || reference.Name is "OrderGenerator" or "OrderAccumulator");
        Assert.DoesNotContain(
            typeof(OrderRecoveryWorker).Assembly.GetReferencedAssemblies(),
            reference => reference.Name!.Contains(".Api") || reference.Name is "OrderGenerator" or "OrderAccumulator");
    }
}
