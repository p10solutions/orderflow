using QuickFix;
using QuickFix.Fields;
using QuickFix.FIX44;

namespace OrderGenerator.Infrastructure.Fix;

internal static class ExecutionReportDecoder
{
    public static ReportDecodingResult Decode(
        string id,
        ExecutionReport report,
        SentOrderSnapshot expected)
    {
        try
        {
            var status = report.ExecType.Value switch
            {
                ExecType.NEW => "Accepted",
                ExecType.REJECTED => "Rejected",
                _ => null
            };

            if (status is null)
            {
                return Inconsistent();
            }

            var accepted = status == "Accepted";

            if (!MatchesOrder(report, expected))
            {
                return Inconsistent();
            }

            if (!MatchesExecutionState(report, expected, accepted))
            {
                return Inconsistent();
            }

            if (!HasDecisionIdentity(report))
            {
                return Inconsistent();
            }

            if (!HasRejectionReason(report, accepted))
            {
                return Inconsistent();
            }

            return new ReportDecodingResult.Valid(
                new(
                    id,
                    status,
                    report.OrderID.Value,
                    report.ExecID.Value,
                    report.IsSetField(
                        Tags.Text) ? report.Text.Value : null));
        }
        catch (FieldNotFoundException)
        {
            return new ReportDecodingResult.Invalid("Incomplete FIX report; awaiting valid confirmation");
        }
    }

    private static ReportDecodingResult Inconsistent() => new ReportDecodingResult.Invalid("Inconsistent FIX report; awaiting valid confirmation");

    private static bool MatchesOrder(ExecutionReport report, SentOrderSnapshot expected) => report.Symbol.Value == expected.Symbol && report.Side.Value == expected.Side && report.OrderQty.Value == expected.Quantity && report.Price.Value == expected.Price;

    private static bool MatchesExecutionState(
        ExecutionReport report,
        SentOrderSnapshot expected,
        bool accepted) => report.OrdStatus.Value == (accepted ? OrdStatus.NEW : OrdStatus.REJECTED) && report.CumQty.Value == 0m && report.AvgPx.Value == 0m && report.LeavesQty.Value == (accepted ? expected.Quantity : 0m);

    private static bool HasDecisionIdentity(ExecutionReport report) => !string.IsNullOrWhiteSpace(report.OrderID.Value) && !string.IsNullOrWhiteSpace(
        report.ExecID.Value);

    private static bool HasRejectionReason(ExecutionReport report, bool accepted) => accepted || report.OrdRejReason.Value == 99 && !string.IsNullOrWhiteSpace(
        report.Text.Value);
}
