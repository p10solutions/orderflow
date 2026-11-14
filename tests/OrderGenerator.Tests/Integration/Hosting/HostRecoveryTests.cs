using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using OrderAccumulator.Application;
using OrderAccumulator.Domain;
using OrderAccumulator.Infrastructure;
using OrderGenerator;
using QuickFix;
using Xunit;

namespace OrderGenerator.Tests;

[Collection("FIX socket integration")]
public sealed class HostRecoveryTests
{
    [Fact]
    public async Task R16_Real_hosts_recover_outbox_and_survive_independent_abrupt_restarts()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "OrderFlow.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var data = Path.Combine(Path.GetTempPath(), "orderflow-host-recovery", Guid.NewGuid().ToString("N"));
        var generatorData = Path.Combine(data, "generator");
        var accumulatorData = Path.Combine(data, "accumulator");
        using var generatorDatabase = new PostgresTestDatabase();
        var generatorDb = generatorDatabase.ConnectionString;
        using var accumulatorDatabase = new PostgresTestDatabase();
        var accumulatorDb = accumulatorDatabase.ConnectionString;
        var journal = new PostgresOrderIntentRepository(OrderGeneratorDatabase.Initialize(generatorDb));
        var ledger = new PostgresOrderUnitOfWorkFactory(OrderAccumulatorDatabase.Initialize(accumulatorDb));
        var session = new SessionID("FIX.4.4", "ACCUMULATOR", "GENERATOR").ToString();
        var lostResponse = Guid.NewGuid().ToString("D");
        var beforeSend = Guid.NewGuid().ToString("D");
        var original = new ProcessOrderUseCase(ledger, new ProcessOrderInputValidator()).Execute(
            (new Order(
                "VALE3",
                OrderSide.Buy,
                5,
                10.01m)).ToTestProcessInput(
                session,
                lostResponse,
                "VALE3|1|5|10.01|2")).Value.ToTestDecision();
        journal.Save(new(
            lostResponse,
            new(
                "VALE3",
                "Buy",
                5,
                10.01m),
            null,
            null));
        journal.Save(new(
            beforeSend,
            new(
                "VIIA4",
                "Sell",
                3,
                0.01m),
            null,
            null));
        var httpPort = FreePort();
        var fixPort = FreePort();

        while (fixPort == httpPort)
        {
            fixPort = FreePort();
        }

        var processes = new List<Process>();
        var output = new ConcurrentQueue<string>();
        var sessionOutput = new ConcurrentQueue<string>();
        var active = new Dictionary<Process, string>();
        var observedOrders = new Dictionary<string, string>();
        string? lastProbeError = null;
        string lastReadyStatus = "not queried";
        void RecordOutput(string name, string? line)
        {
            if (line is null)
            {
                return;
            }

            var message = name + ": " + line;
            output.Enqueue(message);

            if (line.Contains("FIX", StringComparison.OrdinalIgnoreCase) || line.Contains(
                "Recovery",
                StringComparison.OrdinalIgnoreCase) || line.Contains(
                "fail",
                StringComparison.OrdinalIgnoreCase) || line.Contains(
                "Exception",
                StringComparison.OrdinalIgnoreCase))
            {
                sessionOutput.Enqueue(message);
            }
        }

        string Diagnostics(string stage) => $"Host recovery failed during: {stage}\n" + $"Readiness: {lastReadyStatus}; last probe error: {lastProbeError ?? "none"}\n" + "Hosts: " + string.Join(
            ", ",
            active.Select(
                item => $"{item.Value} PID={item.Key.Id} " + (item.Key.HasExited ? $"exited code={item.Key.ExitCode}" : "running"))) + "\n" + "Orders: " + string.Join(
            "; ",
            observedOrders.Select(
                item => $"{item.Key}: {item.Value}")) + "\n" + "FIX/recovery events:\n" + string.Join(
            '\n',
            sessionOutput.TakeLast(60)) + "\nRecent host output:\n" + string.Join(
            '\n',
            output.TakeLast(60));
        Process Start(string name, string storage)
        {
            var configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
            var dll = Path.Combine(
                root!.FullName,
                "src",
                name + ".Host",
                "bin",
                configuration,
                "net10.0",
                name + ".dll");
            var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_EXE") ?? "dotnet")
            {
                WorkingDirectory = root.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.ArgumentList.Add(dll);
            info.Environment["Storage__Directory"] = storage;
            info.Environment["ConnectionStrings__Orders"] = name == "OrderGenerator" ? generatorDb : accumulatorDb;
            info.Environment["Fix__Port"] = fixPort.ToString();
            info.Environment["Fix__Host"] = "127.0.0.1";
            info.Environment["Fix__AcceptHost"] = "127.0.0.1";
            info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{httpPort}";
            var process = new Process
            {
                StartInfo = info
            };
            process.OutputDataReceived += (_, e) => RecordOutput(name, e.Data);
            process.ErrorDataReceived += (_, e) => RecordOutput(name, e.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            processes.Add(process);
            active.Add(process, name);

            return process;
        }

        async Task Kill(Process process)
        {
            active.Remove(process);

            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{httpPort}"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        async Task Wait(string stage, Func<Task<bool>> predicate)
        {
            var elapsed = Stopwatch.StartNew();
            lastProbeError = null;

            while (elapsed.Elapsed < TimeSpan.FromSeconds(40))
            {
                if (active.Keys.Any(process => process.HasExited))
                {
                    Assert.Fail(Diagnostics(stage));
                }

                try
                {
                    if (await predicate())
                    {
                        return;
                    }
                }
                catch (HttpRequestException error)
                {
                    lastProbeError = error.Message;
                }
                catch (TaskCanceledException error)
                {
                    lastProbeError = error.Message;
                }

                await Task.Delay(100);
            }

            Assert.Fail(Diagnostics(stage));
        }

        async Task<bool> Ready(bool expected)
        {
            using var response = await client.GetAsync("/health/ready");
            lastReadyStatus = ((int)response.StatusCode).ToString();

            return expected ? response.IsSuccessStatusCode : response.StatusCode == HttpStatusCode.ServiceUnavailable;
        }

        async Task<OrderResult?> Result(string id)
        {
            using var response = await client.GetAsync("/orders/" + id);
            var result = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<OrderResult>() : null;
            observedOrders[id] = $"HTTP {(int)response.StatusCode}; status={result?.Status}; diagnostic={result?.Diagnostic}";

            return result;
        }

        try
        {
            var accumulator = Start("OrderAccumulator", accumulatorData);
            var generator = Start("OrderGenerator", generatorData);
            await Wait("initial FIX logon and HTTP readiness", () => Ready(true));
            await Wait(
                "recovery of seeded pending orders",
                async () => (await Result(lostResponse))?.Status == "Accepted" && (await Result(
                    beforeSend))?.Status == "Accepted");
            Assert.Equal(original.ExecId, (await Result(lostResponse))!.ExecId);
            Assert.Equal(50.05m, ledger.Exposure("VALE3"));
            Assert.Equal(-0.03m, ledger.Exposure("VIIA4"));
            var request = new OrderRequest(
                Guid.NewGuid().ToString("D"),
                "PETR4",
                "Buy",
                99999,
                "999.99");
            using var submitted = await client.PostAsJsonAsync("/orders", request);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var accepted = await submitted.Content.ReadFromJsonAsync<OrderResult>();
            Assert.Equal("Accepted", accepted!.Status);
            await Kill(generator);
            generator = Start("OrderGenerator", generatorData);
            await Wait("Generator restart and FIX reconnection", () => Ready(true));
            Assert.Equal(accepted, await Result(request.Id));
            using var retry = await client.PostAsJsonAsync("/orders", request);
            Assert.Equal(accepted, await retry.Content.ReadFromJsonAsync<OrderResult>());
            await Kill(accumulator);
            // Observe the old connection closing before accepting readiness from the new session.
            // Otherwise the previous connected flag can satisfy readiness before reconnection.
            await Wait("Generator observes Accumulator disconnect", () => Ready(false));
            accumulator = Start("OrderAccumulator", accumulatorData);
            await Wait("Accumulator restart and FIX reconnection", () => Ready(true));
            // The new order must observe the exposure persisted by the previous process.
            var overLimit = request with
            {
                Id = Guid.NewGuid().ToString("D"),
                Quantity = 3
            };
            using var rejectedResponse = await client.PostAsJsonAsync("/orders", overLimit);
            var rejectionBody = await rejectedResponse.Content.ReadAsStringAsync();
            Assert.True(
                rejectedResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted,
                $"POST after Accumulator restart was not admitted: HTTP {(int)rejectedResponse.StatusCode} {rejectionBody}\n" + Diagnostics("order admission after Accumulator restart"));
            var rejectionSubmission = await rejectedResponse.Content.ReadFromJsonAsync<OrderResult>();
            Assert.NotNull(rejectionSubmission);
            Assert.Equal(overLimit.Id, rejectionSubmission.Id);
            Assert.Equal(
                rejectedResponse.StatusCode == HttpStatusCode.OK ? "Rejected" : "PendingConfirmation",
                rejectionSubmission.Status);
            await Wait(
                "risk rejection after Accumulator restart",
                async () => (await Result(overLimit.Id))?.Status == "Rejected");
            Assert.Equal(99_998_000.01m, ledger.Exposure("PETR4"));
            Assert.Equal(original.ExecId, (await Result(lostResponse))!.ExecId);
        }
        finally
        {
            foreach (var process in processes)
            {
                await Kill(process);
                process.Dispose();
            }

            Directory.Delete(data, recursive: true);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }
}
