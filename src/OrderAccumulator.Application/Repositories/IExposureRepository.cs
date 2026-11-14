namespace OrderAccumulator.Application.Repositories;

public interface IExposureRepository
{
    decimal Get(string symbol);
    void Set(string symbol, decimal exposure);
}
