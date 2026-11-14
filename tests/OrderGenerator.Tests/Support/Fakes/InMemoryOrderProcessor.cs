using OrderAccumulator.Application;
using OrderAccumulator.Domain;

namespace OrderGenerator.Tests;

internal sealed class InMemoryOrderProcessor(ExposureBook book) : IProcessOrderUseCase
{
    public OrderFlow.Shared.Result<ProcessOrderOutput> Execute(ProcessOrderInput input)
    {
        var order = new Order(
            input.Symbol,
            (OrderSide)input.Side,
            input.Quantity,
            input.Price);

        return OrderFlow.Shared.Result<ProcessOrderOutput>.Success(
            book.Process(
                input.Session,
                input.ClOrdId,
                order,
                input.Fingerprint).ToTestOutput());
    }
}
