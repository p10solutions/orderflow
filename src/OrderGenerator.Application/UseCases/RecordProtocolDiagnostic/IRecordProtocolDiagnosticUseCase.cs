namespace OrderGenerator.Application.UseCases;

public interface IRecordProtocolDiagnosticUseCase
{
    Result<RecordProtocolDiagnosticOutput> Execute(RecordProtocolDiagnosticInput input);
}
