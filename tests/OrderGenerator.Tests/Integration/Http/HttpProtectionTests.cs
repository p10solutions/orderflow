using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class HttpProtectionTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected => true;

        public int Sends;
        public Action<string>? Sent;

        public bool Send(string id, OrderPayload order)
        {
            Interlocked.Increment(ref Sends);
            Sent?.Invoke(id);

            return true;
        }
    }

    private static (WebApplication App, Transport Transport) CreateHost()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var transport = new Transport();
        builder.Services.AddSingleton<IOrderTransport>(transport);
        builder.Services.AddOrderHttp();
        builder.Services.AddTestOrderApplication();
        var app = builder.Build();
        app.UseOrderHttp();
        app.MapOrderHttp();
        var registry = app.Services.GetRequiredService<TestOrderApplication>();
        transport.Sent = id => registry.Confirm(new(
            id,
            "Accepted",
            "order",
            "exec"));

        return (app, transport);
    }

    private static string Body(int bytes)
    {
        var body = JsonSerializer.Serialize(
            new
            {
                id = Guid.NewGuid().ToString("D"),
                symbol = "PETR4",
                side = "Buy",
                quantity = 1,
                price = "1.00",
                padding = ""
            });

        return body.Replace(
            "\"padding\":\"\"",
            "\"padding\":\"" + new string(
                'x',
                bytes - Encoding.UTF8.GetByteCount(
                    body)) + "\"");
    }

    private sealed class ChunkedBody(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(
            Encoding.UTF8.GetBytes(
                body)).AsTask();
    }

    [Theory]
    [InlineData(false, "/orders")]
    [InlineData(true, "/orders")]
    [InlineData(true, "/orders/")]
    public async Task R9_Body_budget_is_enforced_before_registration_with_or_without_content_length(
        bool chunked,
        string path)
    {
        var (host, transport) = CreateHost();
        await using var app = host;
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };

        foreach (var size in new[]
        {
            OrderHttpPolicy.MaxOrderBodyBytes,
            OrderHttpPolicy.MaxOrderBodyBytes + 1
        }

        )
        {
            var json = Body(size);
            using HttpContent content = chunked ? new ChunkedBody(json) : new StringContent(json);
            content.Headers.ContentType = new("application/json");
            using var response = await http.PostAsync(path, content);
            Assert.Equal(
                size == OrderHttpPolicy.MaxOrderBodyBytes ? HttpStatusCode.OK : HttpStatusCode.RequestEntityTooLarge,
                response.StatusCode);

            if (size > OrderHttpPolicy.MaxOrderBodyBytes)
            {
                Assert.Equal("RequestBodyTooLarge", (await response.Content.ReadFromJsonAsync<OrderError>())!.Code);
                var id = JsonDocument.Parse(json).RootElement.GetProperty("id").GetString()!;
                Assert.Null(app.Services.GetRequiredService<TestOrderApplication>().Get(id));
            }
        }

        Assert.Equal(1, transport.Sends);
        await app.StopAsync();
    }

    [Theory]
    [InlineData("localhost", null, 200)]
    [InlineData("127.0.0.1", null, 200)]
    [InlineData("[::1]", null, 200)]
    [InlineData("untrusted.example", null, 400)]
    [InlineData("localhost", "http://localhost", 200)]
    [InlineData("localhost", "https://localhost", 403)]
    [InlineData("localhost", "http://localhost:5173", 403)]
    [InlineData("localhost", "https://untrusted.example", 403)]
    [InlineData("localhost", "null", 403)]
    public async Task R9_Host_and_origin_are_checked_before_send(
        string authority,
        string? origin,
        int status)
    {
        var (host, transport) = CreateHost();
        await using var app = host;
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = new StringContent(Body(256), Encoding.UTF8, "application/json")
        };
        request.Headers.Host = authority;

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        using var response = await http.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, transport.Sends);
        await app.StopAsync();
    }

    [Fact]
    public async Task R9_Post_admission_preserves_queries_and_recovers_after_requests_complete()
    {
        var (host, transport) = CreateHost();
        await using var app = host;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saturated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        // Installed after the production limiter to hold admitted requests deterministically.
        app.Use(
            async (context, next) =>
        {
            if (context.Request.Method == "POST")
            {
                if (Interlocked.Increment(ref active) == OrderHttpPolicy.ConcurrentPosts)
                {
                    saturated.TrySetResult();
                }

                await release.Task.WaitAsync(context.RequestAborted);
            }

            await next(context);
        });
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single()),
            Timeout = TimeSpan.FromSeconds(15)
        };
        var requests = Enumerable.Range(0, OrderHttpPolicy.ConcurrentPosts).Select(
            _ => http.PostAsync(
                "/orders",
                new StringContent(
                    Body(256),
                    Encoding.UTF8,
                    "application/json"))).ToArray();

        try
        {
            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var refusedBody = Body(256);
            using var refused = await http.PostAsync(
                "/orders",
                new StringContent(
                    refusedBody,
                    Encoding.UTF8,
                    "application/json"));
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.Equal("HttpCapacityReached", (await refused.Content.ReadFromJsonAsync<OrderError>())!.Code);
            Assert.Null(
                app.Services.GetRequiredService<TestOrderApplication>().Get(
                    JsonDocument.Parse(refusedBody).RootElement.GetProperty("id").GetString()!));
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/status")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/orders/{Guid.NewGuid():D}")).StatusCode);
            Assert.Equal(0, transport.Sends);
        }
        finally
        {
            release.TrySetResult();
        }

        foreach (var response in await Task.WhenAll(requests))
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        using var recovered = await http.PostAsync(
            "/orders",
            new StringContent(
                Body(256),
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(OrderHttpPolicy.ConcurrentPosts + 1, transport.Sends);
        await app.StopAsync();
    }
}
