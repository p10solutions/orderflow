using OrderAccumulator.Application.UseCases.GetExposures;
using QuickFix.Fields;

namespace OrderAccumulator.Worker.Fix;

internal static class ExposureReportMapping
{
    public static QuickFix.FIX44.PositionReport ToPositionReport(this GetExposuresItem item, string requestId)
    {
        var report = new QuickFix.FIX44.PositionReport();
        report.SetField(new PosMaintRptID(Guid.NewGuid().ToString("D")));
        report.SetField(new PosReqID(requestId));
        report.SetField(new TotalNumPosReports(3));
        report.SetField(new PosReqResult(0));
        report.SetField(new ClearingBusinessDate(DateTime.UtcNow.ToString("yyyyMMdd")));
        report.SetField(new Account("ORDERFLOW"));
        report.SetField(new AccountType(1));
        report.SetField(new Currency("BRL"));
        report.SetField(new Symbol(item.Symbol));
        report.SetField(new SettlPrice(0m));
        report.SetField(new PriorSettlPrice(0m));
        report.SetField(new SettlPriceType(1));
        var quantity = new QuickFix.FIX44.PositionReport.NoPositionsGroup();
        quantity.SetField(new PosType("TOT"));
        quantity.SetField(new LongQty(0m));
        quantity.SetField(new ShortQty(0m));
        report.AddGroup(quantity);
        var amount = new QuickFix.FIX44.PositionReport.NoPosAmtGroup();
        amount.SetField(new PosAmtType("CASH"));
        amount.SetField(new PosAmt(item.Exposure));
        report.AddGroup(amount);

        return report;
    }
}
