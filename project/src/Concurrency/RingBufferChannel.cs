using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pack.Concurrency
{
public sealed class RingBufferChannel<T>
{
readonly T[] _buffer;
readonly int _capacityMask;
readonly SemaphoreSlim _items;
readonly SemaphoreSlim _spaces;
readonly object _gate = new object();
volatile bool _completed;
volatile Exception _error;
int _write;
int _read;

public RingBufferChannel(int capacity)
{
if (capacity < 2) capacity = 2;
var pow2 = 1;
while (pow2 < capacity) pow2 <<= 1;
_buffer = new T[pow2];
_capacityMask = pow2 - 1;
_items = new SemaphoreSlim(0, pow2);
_spaces = new SemaphoreSlim(pow2, pow2);
}

public int Capacity => _buffer.Length;
public bool IsCompleted => _completed;

public void Complete(Exception error = null)
{
if (_completed) return;
lock (_gate)
{
if (_completed) return;
_completed = true;
_error = error;
_items.Release(Capacity);
_spaces.Release(Capacity);
}
}

public async ValueTask<bool> WriteAsync(T item, CancellationToken ct = default)
{
if (ct.IsCancellationRequested) return false;
if (_completed) throw CreateCompletionError();
await _spaces.WaitAsync(ct).ConfigureAwait(false);
if (_completed)
{
_spaces.Release();
throw CreateCompletionError();
}
lock (_gate)
{
var w = _write & _capacityMask;
_buffer[w] = item;
_write++;
}
_items.Release();
return true;
}

public async ValueTask<(bool hasItem, T item)> ReadAsync(CancellationToken ct = default)
{
if (ct.IsCancellationRequested) return (false, default);
await _items.WaitAsync(ct).ConfigureAwait(false);
if (_completed && Count == 0)
{
_items.Release();
ThrowIfError();
return (false, default);
}
T item;
lock (_gate)
{
var r = _read & _capacityMask;
item = _buffer[r];
_buffer[r] = default;
_read++;
}
_spaces.Release();
return (true, item);
}

}