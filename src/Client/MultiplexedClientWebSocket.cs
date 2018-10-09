using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace MultiplexedWebSockets
{
    /// <summary>
    /// MultiplexedClientWebSocket
    /// </summary>
    public sealed class MultiplexedClientWebSocket
    {
        private const int _minimumSegmentSize = 64 * 1024;
        private const int _maxMessageSize = 0xFFFF;
        private readonly ClientWebSocket _clientWebSocket;
        private readonly Pipe _sendPipe;
        private readonly Pipe _receivePipe;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ReadOnlySequence<byte>>> _inFlightRequests;
        private readonly ActionBlock<Tuple<MessageType, ReadOnlySequence<byte>, CancellationToken>> _sendBlock;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly Task _readFromSendPipeLoopTask;
        private int _disposeCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiplexedClientWebSocket"/> class.
        /// </summary>
        /// <param name="clientWebSocket">Standard <see cref="ClientWebSocket"/> to multiplex over</param>
        internal MultiplexedClientWebSocket(ClientWebSocket clientWebSocket)
        {
            _clientWebSocket = clientWebSocket;
            _sendPipe = new Pipe(new PipeOptions(minimumSegmentSize: _minimumSegmentSize, useSynchronizationContext: false));
            _receivePipe = new Pipe(new PipeOptions(minimumSegmentSize: _minimumSegmentSize, useSynchronizationContext: false));
            _inFlightRequests = new ConcurrentDictionary<Guid, TaskCompletionSource<ReadOnlySequence<byte>>>();
            _sendBlock = new ActionBlock<Tuple<MessageType, ReadOnlySequence<byte>, CancellationToken>>(WriteToSendPipeAsync, new ExecutionDataflowBlockOptions() { BoundedCapacity = 1, EnsureOrdered = false, MaxDegreeOfParallelism = 1, SingleProducerConstrained = false });
            _memoryPool = MemoryPool<byte>.Shared;
            _readFromSendPipeLoopTask = ReadFromSendPipeLoopAsync();
            _disposeCount = 0;
        }

        /// <summary>Sends data over the <see cref="MultiplexedClientWebSocket"></see> connection asynchronously.</summary>
        /// <param name="buffer">The buffer to be sent over the connection.</param>
        /// <param name="cancellationToken">The token that propagates the notification that operations should be canceled.</param>
        /// <returns>The task object representing the asynchronous operation.</returns>
        public async Task<ReadOnlySequence<byte>> RequestResponseAsync(ReadOnlySequence<byte> buffer, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (buffer.Length > _maxMessageSize)
            {
                throw new InvalidOperationException($"Max message size of {_maxMessageSize} exceeded");
            }

            var id = Guid.NewGuid();
            var tcs = new TaskCompletionSource<ReadOnlySequence<byte>>();
            var tuple = Tuple.Create(MessageType.Request, buffer, cancellationToken);
            _inFlightRequests[id] = tcs;
            try
            {
                cancellationToken.Register(
                () =>
                {
                    if (tcs.TrySetCanceled(cancellationToken))
                    {
                        _inFlightRequests.TryRemove(id, out tcs);
                    }
                }, false);

                if (!await _sendBlock.SendAsync(tuple, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Attempt to post to a completed block");
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _inFlightRequests.TryRemove(id, out tcs);
            }
        }

        /// <summary>
        /// DisposeAsync
        /// </summary>
        /// <returns>Task</returns>
        public async Task DisposeAsync()
        {
            if (Interlocked.Increment(ref _disposeCount) == 1)
            {
                _sendBlock.Complete();
                _sendPipe.Writer.Complete();
                _sendPipe.Reader.Complete();
                _receivePipe.Writer.Complete();
                _receivePipe.Reader.Complete();
                await _readFromSendPipeLoopTask.ConfigureAwait(false);
            }
        }

        private static void UpdateHeader(IMemoryOwner<byte> header, short length, MessageType messageType)
        {
            var span = header.Memory.Span;
            BitConverter.GetBytes(length).CopyTo(span);
            span[2] = (byte)messageType;
        }

        private async Task WriteToSendPipeAsync(Tuple<MessageType, ReadOnlySequence<byte>, CancellationToken> tuple)
        {
            if (!tuple.Item3.IsCancellationRequested)
            {
                using (var header = _memoryPool.Rent(3))
                {
                    UpdateHeader(header, (short)tuple.Item2.Length, tuple.Item1);
                    await _sendPipe.Writer.WriteAsync(header.Memory).ConfigureAwait(false);
                }

                foreach (var segment in tuple.Item2)
                {
                    await _sendPipe.Writer.WriteAsync(segment).ConfigureAwait(false);
                }
            }
        }

        private async Task ReadFromSendPipeLoopAsync()
        {
            while (true)
            {
                ReadResult result = await _sendPipe.Reader.ReadAsync().ConfigureAwait(false);
                foreach (var segment in result.Buffer)
                {
                    if (segment.Length > 0)
                    {
                        await _clientWebSocket.SendAsync(segment, WebSocketMessageType.Binary, false, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                _sendPipe.Reader.AdvanceTo(result.Buffer.End);
                if (result.IsCompleted)
                {
                    break;
                }
            }

            _sendPipe.Reader.Complete();
        }

        private void ThrowIfDisposed()
        {
            if (_disposeCount > 0)
            {
                throw new ObjectDisposedException(typeof(MultiplexedClientWebSocket).Name);
            }
        }
    }
}
