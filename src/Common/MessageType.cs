using System;
using System.Collections.Generic;
using System.Text;

namespace MultiplexedWebSockets
{
    /// <summary>
    /// MessageType
    /// </summary>
    public enum MessageType : byte
    {
        /// <summary>
        /// None
        /// </summary>
        None = 0,

        /// <summary>
        /// Request
        /// </summary>
        Request = 1,

        /// <summary>
        /// Response
        /// </summary>
        Response = 2,
    }
}
