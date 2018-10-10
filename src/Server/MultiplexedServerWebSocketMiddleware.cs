using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace MultiplexedWebSockets.Server
{
    /// <summary>
    /// MultiplexedServerWebSocketMiddleware
    /// </summary>
    public sealed class MultiplexedServerWebSocketMiddleware
    {
        private readonly RequestDelegate _next;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiplexedServerWebSocketMiddleware"/> class.
        /// </summary>
        /// <param name="next">next</param>
        public MultiplexedServerWebSocketMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        /// <summary>
        /// InvokeAsync
        /// </summary>
        /// <param name="context">context</param>
        /// <returns>Task</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                var multiplexedWebSocket = new MultiplexedWebSocket(webSocket);
                await multiplexedWebSocket.Completion.ConfigureAwait(false);
            }
            else
            {
                await _next(context).ConfigureAwait(false);
            }
        }
    }
}
