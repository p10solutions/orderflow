namespace OrderGenerator;

public interface IOrderTransport
{
    bool Connected
    {
        get;
    }

    bool Send(string id, OrderPayload order);
}
