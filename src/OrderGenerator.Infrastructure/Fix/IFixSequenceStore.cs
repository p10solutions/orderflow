namespace OrderGenerator.Infrastructure.Fix;

public interface IFixSequenceStore
{
    IReadOnlyDictionary<ulong, string> ReadMappings();
    void Record(ulong sequence, string clOrdId);
}
