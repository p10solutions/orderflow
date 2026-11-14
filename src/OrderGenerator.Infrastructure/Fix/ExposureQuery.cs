using OrderGenerator.Application;
using QuickFix;
using QuickFix.Fields;

namespace OrderGenerator;

public sealed partial class FixInitiator : IExposureTransport
{
    private readonly SemaphoreSlim exposureGate = new(8, 8);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExposureQuery> exposureQueries = new();

    public async Task<IReadOnlyList<ExposureTransportItem>?> QueryAsync(CancellationToken cancellationToken)
    {
        if (!Connected || session is null || !await exposureGate.WaitAsync(0, cancellationToken))
        {
            return null;
        }

        var id = Guid.NewGuid().ToString("D");
        var query = new ExposureQuery();
        exposureQueries[id] = query;
        try
        {
            var request = new QuickFix.FIX44.RequestForPositions();
            request.SetField(new PosReqID(id));
            request.SetField(new PosReqType(0));
            request.SetField(new SubscriptionRequestType('0'));
            request.SetField(new Account("ORDERFLOW"));
            request.SetField(new AccountType(1));
            request.SetField(new ClearingBusinessDate(DateTime.UtcNow.ToString("yyyyMMdd")));
            request.SetField(new TransactTime(DateTime.UtcNow));
            if (!Session.SendToTarget(request, session))
            {
                return null;
            }

            return await query.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (SessionNotFound)
        {
            return null;
        }
        finally
        {
            exposureQueries.TryRemove(id, out _);
            exposureGate.Release();
        }
    }

    private void HandlePositionReport(QuickFix.FIX44.PositionReport report)
    {
        if (!report.IsSetField(Tags.PosReqID) ||
            !exposureQueries.TryGetValue(report.GetString(Tags.PosReqID), out var query))
        {
            return;
        }

        if (new[] { Tags.Symbol, Tags.Account, Tags.AccountType, Tags.Currency, Tags.PosReqResult,
                Tags.TotalNumPosReports, Tags.NoPosAmt }.Any(tag => !report.IsSetField(tag)) ||
            report.GetInt(Tags.AccountType) != 1 || report.GetString(Tags.Account) != "ORDERFLOW" ||
            report.GetString(Tags.Currency) != "BRL" || report.GetInt(Tags.PosReqResult) != 0 ||
            report.GetInt(Tags.TotalNumPosReports) != 3 || report.GetInt(Tags.NoPosAmt) != 1)
        {
            return;
        }

        var symbol = report.GetString(Tags.Symbol);
        var amount = new QuickFix.FIX44.PositionReport.NoPosAmtGroup();
        report.GetGroup(1, amount);
        if (!amount.IsSetField(Tags.PosAmt) || !amount.IsSetField(Tags.PosAmtType))
        {
            return;
        }

        var exposure = amount.GetDecimal(Tags.PosAmt);
        if (symbol is not ("PETR4" or "VALE3" or "VIIA4") || amount.GetString(Tags.PosAmtType) != "CASH" ||
            Math.Abs(exposure) > 100_000_000m || decimal.Truncate(exposure * 100m) != exposure * 100m)
        {
            return;
        }

        query.Add(symbol, exposure);
    }

    private sealed class ExposureQuery
    {
        private readonly Dictionary<string, decimal> items = new(StringComparer.Ordinal);
        public TaskCompletionSource<IReadOnlyList<ExposureTransportItem>> Completion
        {
            get;
        } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(string symbol, decimal exposure)
        {
            lock (items)
            {
                items.TryAdd(symbol, exposure);
                if (items.Count == 3)
                {
                    Completion.TrySetResult(items.OrderBy(item => item.Key)
                        .Select(item => new ExposureTransportItem(item.Key, item.Value)).ToArray());
                }
            }
        }
    }
}
