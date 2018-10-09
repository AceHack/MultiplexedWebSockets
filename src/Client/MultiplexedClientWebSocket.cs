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
        private const byte _messageEnvelopeVersion = 1;
        private const int _minimumSegmentSize = 64 * 1024;
        private const int _maxMessageSize = 0xFFFF;
        private const int _minimumReceiveBufferSize = 512;
        private const int _headerLength = 20;
        private readonly ClientWebSocket _clientWebSocket;
        private readonly CancellationTokenSource _cts;
        private readonly Pipe _sendPipe;
        private readonly Pipe _receivePipe;
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ReadOnlySequence<byte>>> _inFlightRequests;
        private readonly ActionBlock<Tuple<MessageType, Guid, ReadOnlySequence<byte>, CancellationToken>> _sendBlock;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly Task _readFromSendPipeLoopTask;
        private readonly Task _writeToReceivePipeLoopTask;
        private int _disposeCount;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiplexedClientWebSocket"/> class.
        /// </summary>
        internal MultiplexedClientWebSocket()
        {
            _clientWebSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();
            _sendPipe = new Pipe(new PipeOptions(minimumSegmentSize: _minimumSegmentSize, useSynchronizationContext: false));
            _receivePipe = new Pipe(new PipeOptions(minimumSegmentSize: _minimumSegmentSize, useSynchronizationContext: false));
            _inFlightRequests = new ConcurrentDictionary<Guid, TaskCompletionSource<ReadOnlySequence<byte>>>();
            _sendBlock = new ActionBlock<Tuple<MessageType, Guid, ReadOnlySequence<byte>, CancellationToken>>(WriteToSendPipeAsync, new ExecutionDataflowBlockOptions() { BoundedCapacity = 1, EnsureOrdered = false, MaxDegreeOfParallelism = 1, SingleProducerConstrained = false });
            _memoryPool = MemoryPool<byte>.Shared;
            _readFromSendPipeLoopTask = ReadFromSendPipeLoopAsync();
            _writeToReceivePipeLoopTask = WriteToReceivePipeLoopAsync();
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
            var tuple = Tuple.Create(MessageType.Request, id, buffer, cancellationToken);
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
                await _clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token).ConfigureAwait(false);
                _clientWebSocket.Dispose();
                _sendBlock.Complete();
                _sendPipe.Writer.Complete();
                _sendPipe.Reader.Complete();
                _receivePipe.Writer.Complete();
                _receivePipe.Reader.Complete();
                await _readFromSendPipeLoopTask.ConfigureAwait(false);
                await _writeToReceivePipeLoopTask.ConfigureAwait(false);
                _cts.Cancel();
            }
        }

        private static void UpdateHeader(IMemoryOwner<byte> header, short length, MessageType messageType, Guid id)
        {
            var slice = header.Memory.Span;
            slice[0] = _messageEnvelopeVersion;
            slice = slice.Slice(1, 19);
            id.TryWriteBytes(slice);
            slice = slice.Slice(16, 3);
            BitConverter.TryWriteBytes(slice, length);
            slice[2] = (byte)messageType;
        }

        private async Task WriteToSendPipeAsync(Tuple<MessageType, Guid, ReadOnlySequence<byte>, CancellationToken> tuple)
        {
            if (!tuple.Item4.IsCancellationRequested)
            {
                using (var header = _memoryPool.Rent(_headerLength))
                {
                    UpdateHeader(header, (short)tuple.Item3.Length, tuple.Item1, tuple.Item2);
                    await _sendPipe.Writer.WriteAsync(header.Memory, _cts.Token).ConfigureAwait(false);
                }

                foreach (var segment in tuple.Item3)
                {
                    await _sendPipe.Writer.WriteAsync(segment, _cts.Token).ConfigureAwait(false);
                }
            }
        }

        private async Task ReadFromSendPipeLoopAsync()
        {
            var isComplete = false;
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    ReadResult result = await _sendPipe.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                    foreach (var segment in result.Buffer)
                    {
                        if (segment.Length > 0)
                        {
                            await _clientWebSocket.SendAsync(segment, WebSocketMessageType.Binary, false, _cts.Token).ConfigureAwait(false);
                        }
                    }

                    _sendPipe.Reader.AdvanceTo(result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                isComplete = true;
                _sendPipe.Reader.Complete(ex);
                throw;
            }
            finally
            {
                if (!isComplete)
                {
                    _sendPipe.Reader.Complete();
                }
            }
        }

        private async Task ReadFromReceivePipeLoopAsync()
        {
            var isComplete = false;
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    ReadResult result = await _receivePipe.Reader.ReadAsync(_cts.Token).ConfigureAwait(false);
                    ReadOnlySequence<byte> buffer = result.Buffer;
                    SequencePosition? position;
                    do
                    {
                        position = null;
                        if (buffer.Length >= _headerLength)
                        {
                            var messageEnvelopeVersion = buffer.Slice(0, 1).First.Slice(0, 1).Span[0];
                            if (messageEnvelopeVersion != _messageEnvelopeVersion)
                            {
                                throw new InvalidOperationException($"Invalid message envelope version {messageEnvelopeVersion}, only message envelope version {_messageEnvelopeVersion} supported");
                            }

                            var id = new Guid(buffer.Slice(1, 16).ToArray());
                            var type = (MessageType)buffer.Slice(19, 1).First.Slice(0, 1).Span[0];
                            if (type != MessageType.Response)
                            {
                                throw new InvalidOperationException($"Invalid message type {type}, message type {MessageType.Response} expected");
                            }

                            var length = BitConverter.ToInt16(buffer.Slice(17, 1).ToArray());
                            var end = length + _headerLength;
                            if (buffer.Length >= end)
                            {
                                position = buffer.GetPosition(end);
                                var data = buffer.Slice(_headerLength, position.Value);
                                ProcessData(id, data);

                                if (buffer.Length <= end)
                                {
                                    position = null;
                                }

                                buffer = buffer.Slice(end);
                            }
                        }
                    }
                    while (position != null);

                    // We sliced the buffer until no more data could be processed
                    // Tell the PipeReader how much we consumed and how much we left to process
                    _receivePipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                isComplete = true;
                _receivePipe.Reader.Complete(ex);
                throw;
            }
            finally
            {
                if (!isComplete)
                {
                    _receivePipe.Reader.Complete();
                }
            }
        }

        private async Task WriteToReceivePipeLoopAsync()
        {
            var isComplete = false;
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        // Request a minimum of _minimumReceiveBufferSize bytes from the PipeWriter
                        var memory = _receivePipe.Writer.GetMemory(_minimumReceiveBufferSize);
                        var webSocketResult = await _clientWebSocket.ReceiveAsync(memory, _cts.Token).ConfigureAwait(false);
                        if (webSocketResult.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        // Tell the PipeWriter how much was read
                        _receivePipe.Writer.Advance(webSocketResult.Count);
                    }
                    catch
                    {
                        break;
                    }

                    // Make the data available to the PipeReader
                    FlushResult flushResult = await _receivePipe.Writer.FlushAsync(_cts.Token).ConfigureAwait(false);
                    if (flushResult.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                isComplete = true;

                // Signal to the reader that we're done writing with error
                _receivePipe.Writer.Complete(ex);
                throw;
            }
            finally
            {
                if (!isComplete)
                {
                    // Signal to the reader that we're done writing
                    _receivePipe.Writer.Complete();
                }
            }
        }

        private void ProcessData(Guid id, ReadOnlySequence<byte> buffer)
        {
            if (_inFlightRequests.TryGetValue(id, out var value))
            {
                value.TrySetResult(buffer);
            }
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
