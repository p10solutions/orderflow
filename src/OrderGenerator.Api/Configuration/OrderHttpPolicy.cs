namespace OrderGenerator.Api;

public static class OrderHttpPolicy
{
    public const string SubmissionPath = "/orders";
    public const string RateLimitPolicy = "orders";
    public const int MaxOrderBodyBytes = 4096;
    public const int ConcurrentPosts = 64;
    public static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(3);
}
