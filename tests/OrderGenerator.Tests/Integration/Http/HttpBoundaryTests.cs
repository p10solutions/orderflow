using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderGenerator;
using Xunit;

namespace OrderGenerator.Tests;

public sealed class HttpBoundaryTests
{
    private sealed class Transport : IOrderTransport
    {
        public bool Connected { get; set; } = true;

        public int Sends;

        public bool Send(string id, OrderPayload order)
        {
            Interlocked.Increment(ref Sends);

            return false;
        }
    }

    [Fact]
    public async Task R5_R10_Real_http_pending_query_diagnostic_and_late_confirmation()
    {
        var transport = new Transport();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IOrderTransport>(transport);
        builder.Services.AddOrderHttp();
        builder.Services.AddTestOrderApplication();
        await using var app = builder.Build();
        app.UseOrderHttp();
        app.MapOrderHttp();
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        var request = new OrderRequest(
            Guid.NewGuid().ToString("D"),
            "PETR4",
            "Buy",
            1,
            "1.01");
        var response = await http.PostAsJsonAsync("/orders", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("PendingConfirmation", (await response.Content.ReadFromJsonAsync<OrderResult>())!.Status);
        var registry = app.Services.GetRequiredService<TestOrderApplication>();
        registry.Diagnose(new(request.Id, "Protocol diagnostic"));
        var pending = await http.GetFromJsonAsync<OrderResult>($"/orders/{request.Id}");
        Assert.Equal("Protocol diagnostic", pending!.Diagnostic);
        Assert.Equal("PendingConfirmation", pending.Status);
        registry.Confirm(new(
            request.Id,
            "Accepted",
            "order",
            "exec"));
        response = await http.PostAsJsonAsync("/orders", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Accepted", (await response.Content.ReadFromJsonAsync<OrderResult>())!.Status);
        Assert.Equal(1, transport.Sends);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync("/orders", request with
            {
                Quantity = 2
            })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/orders/{Guid.NewGuid():D}")).StatusCode);

        foreach (var json in new[]
        {
            "{",
            "null",
            "{}",
            "{\"quantity\":\"1\"}",
            "{\"quantity\":1.1}"
        }

        )
        {
            using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var invalid = await http.PostAsync("/orders", body);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("ValidationFailed", (await invalid.Content.ReadFromJsonAsync<OrderError>())!.Code);
        }

        transport.Connected = false;
        response = await http.PostAsJsonAsync("/orders", request with
        {
            Id = Guid.NewGuid().ToString("D")
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("FixUnavailable", (await response.Content.ReadFromJsonAsync<OrderError>())!.Code);
        await app.StopAsync();
    }
}
