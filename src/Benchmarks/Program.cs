using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using MultiplexedWebSockets.Server;

namespace MultiplexedWebSockets.Benchmarks
{
    static class Program
    {
        const string _serverUrl = "http://*:5000";
        const string _clientUrl = "ws://localhost:5000";
        const int _times = 100;
        const int _batch = 10000;

        static async Task Main(string[] args)
        {
            var cts = new CancellationTokenSource();
            var webHost = WebHost.CreateDefaultBuilder(args)
                .UseUrls(_serverUrl)
                .Configure(app =>
                {
                    app.UseMultiplexedServerWebSocket();
                })
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.SetMinimumLevel(LogLevel.Error);
                })
                .Build();

            var runTask = webHost.RunAsync(cts.Token);
            var client = new ClientWebSocket();
            await client.ConnectAsync(new Uri(_clientUrl), cts.Token).ConfigureAwait(false);
            var mx = new MultiplexedWebSocket(client);
            var data = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3, 4, 5 });
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < _times; i++)
            {
                var tasks = new List<Task>(_batch);
                for (int b = 0; b < _batch; b++)
                {
                    tasks.Add(mx.RequestResponseAsync(data, cts.Token));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            watch.Stop();
            Console.WriteLine((_times * _batch) / watch.Elapsed.TotalSeconds);
            cts.Cancel();
            await runTask.ConfigureAwait(false);
            await webHost.StopAsync().ConfigureAwait(false);
            Console.ReadKey(true);
        }
    }
}
