using System.Collections.Concurrent;
using System.Text.Json;

namespace Marian.Agent.Acp.Acp;

internal sealed class PendingRequests
{
    private long _nextId = 1;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();

    public (JsonElement id, Task<JsonElement> task) Create()
    {
        var idNum = Interlocked.Increment(ref _nextId);
        var id = JsonDocument.Parse(idNum.ToString()).RootElement.Clone();
        var key = id.ToString();

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        return (id, tcs.Task);
    }

    public bool TryResolve(JsonElement id, JsonElement? result, Exception? error)
    {
        var key = id.ToString();
        if (!_pending.TryRemove(key, out var tcs))
        {
            return false;
        }

        if (error is not null)
        {
            tcs.TrySetException(error);
            return true;
        }

        tcs.TrySetResult(result ?? default);
        return true;
    }
}
