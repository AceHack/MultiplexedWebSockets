using System;
using System.Buffers;

namespace MultiplexedWebSockets
{
    /// <summary>
    /// LinkedSegment
    /// </summary>
    /// <typeparam name="T">Type</typeparam>
    internal sealed class LinkedSegment<T> : ReadOnlySequenceSegment<T>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LinkedSegment{T}"/> class.
        /// </summary>
        /// <param name="memory">memory</param>
        public LinkedSegment(ReadOnlyMemory<T> memory) => Memory = memory;

        /// <summary>
        /// Add
        /// </summary>
        /// <param name="memory">memory</param>
        /// <returns>LinkedSegment</returns>
        public LinkedSegment<T> Add(ReadOnlyMemory<T> memory)
        {
            var segment = new LinkedSegment<T>(memory);
            segment.RunningIndex = RunningIndex + Memory.Length;
            Next = segment;
            return segment;
        }
    }
}
