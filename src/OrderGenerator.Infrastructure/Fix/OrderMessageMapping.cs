using QuickFix;
using QuickFix.Fields;

namespace OrderGenerator.Infrastructure.Fix;

internal static class OrderMessageMapping
{
    internal static QuickFix.FIX44.NewOrderSingle ToNewOrderSingle(this OrderPayload order, string id)
    {
        var message = new QuickFix.FIX44.NewOrderSingle(
            new ClOrdID(id),
            new Symbol(order.Symbol),
            new Side(
                order.Side == "Buy" ? Side.BUY : Side.SELL),
            new TransactTime(
                DateTime.UtcNow),
            new OrdType(
                OrdType.LIMIT));
        message.Set(new OrderQty(order.Quantity));
        message.Set(new Price(order.Price));

        return message;
    }

    internal static SentOrderSnapshot ToSentOrderSnapshot(this Message message) => new(
        message.GetString(Tags.Symbol),
        message.GetChar(
            Tags.Side),
        message.GetDecimal(
            Tags.OrderQty),
        message.IsSetField(
            Tags.Price) ? message.GetDecimal(
            Tags.Price) : 0m);

    internal static SentOrderSnapshot ToSentOrderSnapshot(this OrderPayload order) => new(
        order.Symbol,
        order.Side == "Buy" ? Side.BUY : Side.SELL,
        order.Quantity,
        order.Price);
}
