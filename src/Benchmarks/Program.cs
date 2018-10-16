using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using MultiplexedWebSockets.Server;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using Microsoft.AspNetCore.Http;

namespace MultiplexedWebSockets.Benchmarks
{
    enum Benchmark
    {
        MultiplexedWebSocket,
        HttpClient,
    }

    static class Program
    {
        const string _clientWebSocketUrl = "wss://localhost:5001/ws";
        const string _httpClientUrl = "https://localhost:5001/client";
        const int _times = 100;
        const int _batch = 10000;

        static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();
            var webHost = WebHost.CreateDefaultBuilder(args)
                .ConfigureKestrel(kestrelServerOptions =>
                {
                    kestrelServerOptions.Listen(IPAddress.Any, 5001, listenOptions =>
                    {
                        listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                        listenOptions.UseHttps();
                    });
                })
                .Configure(app =>
                {
                    app.Map("/ws", HandleMultiplexedWebSocket);
                    app.Map("/client", HandleHttpClient);
                })
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(LogLevel.Error);
                })
                .Build();

            var runTask = webHost.RunAsync(cts.Token);
            using (var clientWebSocket = new ClientWebSocket())
            using (var httpClient = new HttpClient(new WinHttpHandler(), true))
            {
                httpClient.BaseAddress = new Uri(_httpClientUrl);
                await clientWebSocket.ConnectAsync(new Uri(_clientWebSocketUrl), cts.Token).ConfigureAwait(false);
                var mx = new MultiplexedWebSocket(clientWebSocket);
                var data = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4, 5 });
                foreach (Benchmark benchmark in Enum.GetValues(typeof(Benchmark)))
                {
                    if (benchmark == Benchmark.HttpClient)
                    {
                        var response = await httpClient.SendAsync(new HttpRequestMessage() { Method = HttpMethod.Get, Version = HttpVersion.Version20 });
                        if (response.Version != HttpVersion.Version20)
                        {
                            throw new InvalidOperationException("Should be Http/2");
                        }
                    }

                    var watch = Stopwatch.StartNew();
                    for (int i = 0; i < _times; i++)
                    {
                        var tasks = new List<Task>(_batch);
                        for (int b = 0; b < _batch; b++)
                        {
                            switch (benchmark)
                            {
                                case Benchmark.MultiplexedWebSocket:
                                    tasks.Add(mx.RequestResponseAsync(data, cts.Token));
                                    break;
                                case Benchmark.HttpClient:
                                    tasks.Add(httpClient.SendAsync(new HttpRequestMessage() { Method = HttpMethod.Get, Version = HttpVersion.Version20 }));
                                    break;
                            }
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                    }

                    watch.Stop();
                    Console.WriteLine($"{benchmark}: {(_times * _batch) / watch.Elapsed.TotalSeconds:N0} per second");
                }
            }
            cts.Cancel();
            await runTask.ConfigureAwait(false);
            await webHost.StopAsync().ConfigureAwait(false);
            Console.ReadKey(true);
        }

        private static void HandleMultiplexedWebSocket(IApplicationBuilder app)
        {
            app.UseMultiplexedServerWebSocket();
        }

        private static void HandleHttpClient(IApplicationBuilder app)
        {
            app.Run(async (context) =>
            {
                await context.Response.WriteAsync("Hello");
            });
        }
    }
}
