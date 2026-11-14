using OrderAccumulator.Domain;

namespace OrderAccumulator.Application.UseCases.ProcessOrder;

public interface IProcessOrderUseCase
{
    Result<ProcessOrderOutput> Execute(ProcessOrderInput input);
}
