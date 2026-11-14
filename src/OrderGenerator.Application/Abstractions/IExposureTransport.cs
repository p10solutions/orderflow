namespace OrderGenerator.Application;

public sealed record ExposureTransportItem(string Symbol, decimal Exposure);

public interface IExposureTransport
{
    Task<IReadOnlyList<ExposureTransportItem>?> QueryAsync(CancellationToken cancellationToken);
}
