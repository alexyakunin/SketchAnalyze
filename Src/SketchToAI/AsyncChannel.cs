using System;
using System.Threading;
using System.Threading.Tasks;

namespace SketchToAI
{
    public interface IAsyncChannel<T>
    {
        bool IsEmpty { get; }
        bool IsFull { get; }
        bool IsPutCompleted { get; }
        bool CompletePut();
        ValueTask PutAsync(T item, CancellationToken cancellationToken = default);
        ValueTask<(T Item, bool IsDequeued)> PullAsync(CancellationToken cancellationToken = default);
        ValueTask PutAsync(ReadOnlyMemory<T> source, CancellationToken cancellationToken = default);
        ValueTask<int> PullAsync(Memory<T> source, CancellationToken cancellationToken = default);
    }
    
    public sealed class AsyncChannel<T> : IAsyncChannel<T>
    {
        private readonly T[] _buffer;
        private int _readPosition;
        private int _writePosition;
        private TaskCompletionSource<bool> _pullHappened = new TaskCompletionSource<bool>();
        private TaskCompletionSource<bool> _putHappened = new TaskCompletionSource<bool>();

        public int Size { get; }
        public int FreeCount => Size - Count;
        public int Count {
            get {
                lock (Lock) {
                    return CountNoLock;
                }
            }
        }
        public bool IsEmpty => Count == 0;
        public bool IsFull => Count == Size;
        public bool IsPutCompleted { get; private set; }
        
        private int CountNoLock {
            get {
                var diff = _writePosition - _readPosition;
                return diff >= 0 ? diff : diff + _buffer.Length;
            }
        }

        private int FreeCountNoLock => Size - CountNoLock;
        private object Lock => _buffer;
        
        public AsyncChannel(int size)
        {
            if (size <= 0)
                throw Errors.QueueSizeMustBeGreaterThanZero(nameof(size));
            Size = size;
            _buffer = new T[size + 1]; // To make sure that "buffer is full" != "buffer is empty"
        }

        public AsyncChannel(T[] buffer) // Might be used w/ System.Buffers
        {
            if (buffer.Length <= 1)
                throw Errors.BufferLengthMustBeGreaterThanOne(nameof(buffer));
            Size = buffer.Length - 1; // To make sure that "buffer is full" != "buffer is empty"
            _buffer = buffer;  
        }

        public bool CompletePut()
        {
            if (IsPutCompleted)
                return false;
            lock (Lock) {
                if (IsPutCompleted)
                    return false;
                IsPutCompleted = true;
                _putHappened?.TrySetResult(true);
                return true;
            }
        }

        public async ValueTask PutAsync(T item, CancellationToken cancellationToken = default)
        {
            while (true) {
                if (FreeCount == 0)
                    await WaitForDequeueAsync(cancellationToken).ConfigureAwait(false);
                lock (Lock) {
                    if (IsPutCompleted)
                        throw Errors.EnqueueCompleted();
                    if (FreeCountNoLock == 0)
                        continue;
                    _buffer[_writePosition] = item;
                    _writePosition = (_writePosition + 1) % _buffer.Length;
                    _putHappened?.TrySetResult(true);
                    return;
                }
            }
        }

        public async ValueTask PutAsync(ReadOnlyMemory<T> source, CancellationToken cancellationToken = default)
        {
            while (source.Length > 0) {
                if (FreeCount == 0)
                    await WaitForDequeueAsync(cancellationToken).ConfigureAwait(false);
                lock (Lock) {
                    if (IsPutCompleted)
                        throw Errors.EnqueueCompleted();
                    var availableLength = FreeCountNoLock;
                    if (availableLength == 0)
                        continue;
                    var chunkLength = Math.Min(source.Length, availableLength);
                    var chunk1Length = Math.Min(chunkLength, _buffer.Length - _writePosition);
                    var chunk2Length = chunkLength - chunk1Length;
                    if (chunk1Length > 0) {
                        source.Span.Slice(0, chunk1Length).CopyTo(_buffer.AsSpan(_writePosition, chunk1Length));
                        source = source.Slice(chunk1Length);
                    }
                    if (chunk2Length > 0) {
                        source.Span.Slice(0, chunk2Length).CopyTo(_buffer.AsSpan(0, chunk2Length));
                        source = source.Slice(chunk2Length);
                    }
                    _writePosition = (_writePosition + chunkLength) % _buffer.Length;
                    _putHappened?.TrySetResult(true);
                }
            }
        }

        public async ValueTask<(T Item, bool IsDequeued)> PullAsync(CancellationToken cancellationToken = default)
        {
            while (true) {
                if (IsEmpty)
                    await WaitForEnqueueAsync(cancellationToken).ConfigureAwait(false);
                lock (Lock) {
                    if (CountNoLock == 0) {
                        if (IsPutCompleted)
                            return (default!, false);
                        continue;
                    }
                    var item = _buffer[_readPosition];
                    _readPosition = (_readPosition + 1) % _buffer.Length;
                    _pullHappened?.TrySetResult(true);
                    return (item, true);
                }
            }
        }

        public async ValueTask<int> PullAsync(Memory<T> target, CancellationToken cancellationToken = default)
        {
            if (target.Length <= 0)
                throw Errors.BufferLengthMustBeGreaterThanZero(nameof(target));
            var readLength = 0;
            while (target.Length > 0) {
                if (IsEmpty) {
                    if (readLength > 0)
                        // Don't await for enqueue if there is already something to return
                        return readLength;
                    await WaitForEnqueueAsync(cancellationToken).ConfigureAwait(false);
                }
                lock (Lock) {
                    var availableLength = CountNoLock;
                    if (availableLength == 0) {
                        if (IsPutCompleted)
                            return readLength;
                        continue;
                    }
                    var chunkLength = Math.Min(target.Length, availableLength);
                    var chunk1Length = Math.Min(chunkLength, _buffer.Length - readLength);
                    var chunk2Length = chunkLength - chunk1Length;
                    if (chunk1Length > 0) {
                        _buffer.AsSpan(_readPosition, chunkLength).CopyTo(target.Span);
                        target = target.Slice(chunkLength);
                    }
                    if (chunk2Length > 0) {
                        _buffer.AsSpan(_readPosition, chunkLength).CopyTo(target.Span);
                        target = target.Slice(chunkLength);
                    }
                    readLength += chunkLength;
                    _readPosition = (_readPosition + chunkLength) % _buffer.Length;
                    _pullHappened?.TrySetResult(true);
                }
            }
            return readLength;
        }

        private async Task WaitForDequeueAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs; 
            lock (Lock) {
                cancellationToken.ThrowIfCancellationRequested();
                tcs = _pullHappened;
                if (tcs.Task.IsCompleted && FreeCountNoLock > 0)
                    return;
                tcs = new TaskCompletionSource<bool>();
                _pullHappened = tcs;
            }

            var cancelDelay = new CancellationTokenSource();
            var cancelAny = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, cancelDelay.Token);
            try {
                var delayTask = Task.Delay(Timeout.Infinite, cancelAny.Token);
                await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
            }
            finally {
                cancelDelay.Cancel(); // To make sure the delay task is gone
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        private async Task WaitForEnqueueAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool> tcs; 
            lock (Lock) {
                cancellationToken.ThrowIfCancellationRequested();
                tcs = _putHappened;
                if (tcs.Task.IsCompleted && (CountNoLock > 0 || IsPutCompleted))
                    return;
                tcs = new TaskCompletionSource<bool>();
                _putHappened = tcs;
            }

            var cancelDelay = new CancellationTokenSource();
            var cancelAny = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, cancelDelay.Token);
            try {
                var delayTask = Task.Delay(Timeout.Infinite, cancelAny.Token);
                await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
            }
            finally {
                cancelDelay.Cancel(); // To make sure the delay task is gone
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
