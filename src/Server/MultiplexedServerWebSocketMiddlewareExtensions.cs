using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.AspNetCore.Builder;

namespace MultiplexedWebSockets.Server
{
    /// <summary>
    /// MultiplexedServerWebSocketMiddlewareExtensions
    /// </summary>
    public static class MultiplexedServerWebSocketMiddlewareExtensions
    {
        /// <summary>
        /// UseMultiplexedServerWebSocket
        /// </summary>
        /// <param name="builder">builder</param>
        /// <returns>IApplicationBuilder</returns>
        public static IApplicationBuilder UseMultiplexedServerWebSocket(this IApplicationBuilder builder)
        {
            return builder.UseWebSockets().UseMiddleware<MultiplexedServerWebSocketMiddleware>();
        }

        /// <summary>
        /// UseMultiplexedServerWebSocket
        /// </summary>
        /// <param name="builder">builder</param>
        /// <param name="options">options</param>
        /// <returns>IApplicationBuilder</returns>
        public static IApplicationBuilder UseMultiplexedServerWebSocket(this IApplicationBuilder builder, WebSocketOptions options)
        {
            return builder.UseWebSockets(options).UseMiddleware<MultiplexedServerWebSocketMiddleware>();
        }
    }
}
