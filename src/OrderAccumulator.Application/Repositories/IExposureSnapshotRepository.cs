namespace OrderAccumulator.Application.Repositories;

public sealed record ExposureSnapshotItem(string Symbol, decimal Exposure);

public interface IExposureSnapshotRepository
{
    IReadOnlyList<ExposureSnapshotItem> Read();
}
