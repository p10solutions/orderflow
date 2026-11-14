using Microsoft.Extensions.Hosting;
using OrderGenerator.Application.UseCases;

namespace OrderGenerator.Worker.Fix;

/// <summary>Inbound adapter: connects semantic FIX notifications to application use cases.</summary>
public sealed class FixReportConsumer(
    FixInitiator initiator,
    IGetKnownOrdersUseCase known,
    IConfirmOrderUseCase confirm,
    IRecordProtocolDiagnosticUseCase diagnose) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        initiator.RestoreCorrelation(known.Execute(new()).Value.ToCorrelation());
        initiator.ReportReceived += OnReport;
        initiator.DiagnosticReceived += OnDiagnostic;

        return Task.CompletedTask;
    }

    private void OnReport(OrderResult report) => confirm.Execute(report.ToConfirmOrderInput());

    private void OnDiagnostic(ProtocolDiagnostic diagnostic) => diagnose.Execute(new(diagnostic.Id, diagnostic.Message));

    public Task StopAsync(CancellationToken cancellationToken)
    {
        initiator.ReportReceived -= OnReport;
        initiator.DiagnosticReceived -= OnDiagnostic;

        return Task.CompletedTask;
    }
}
