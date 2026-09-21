using System.Collections.Concurrent;

namespace RingCentral_amoCRM.Helpers;

// Общая защита от гонки между CallLogPollingService и LateAttachService:
// оба могут одновременно решить, что у звонка ещё нет заметки, и оба
// попытаются её создать. AcquireAsync сериализует блок "проверка +
// создание" по конкретному call ID; IsProcessed/MarkProcessed — быстрый
// потокобезопасный pre-check в памяти (источник истины при рестарте —
// amoCRM через NoteExistsAsync, не этот набор).
public class CallProcessingGuard
{
    private readonly ConcurrentDictionary<string, byte> _processedCallIds = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private DateTime _lastCleanup = DateTime.UtcNow;

    public bool IsProcessed(string callId) =>
        !string.IsNullOrEmpty(callId) && _processedCallIds.ContainsKey(callId);

    public void MarkProcessed(string callId)
    {
        if (!string.IsNullOrEmpty(callId))
        {
            _processedCallIds[callId] = 0;
        }
    }

    public async Task<IDisposable> AcquireAsync(string callId)
    {
        var sem = _locks.GetOrAdd(callId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync();
        return new Releaser(sem);
    }

    public void ClearOlderThanIfDue(TimeSpan age)
    {
        if ((DateTime.UtcNow - _lastCleanup) < age)
        {
            return;
        }

        _processedCallIds.Clear();
        _locks.Clear();
        _lastCleanup = DateTime.UtcNow;
    }

    public int ProcessedCount => _processedCallIds.Count;

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private bool _disposed;

        public Releaser(SemaphoreSlim sem) => _sem = sem;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sem.Release();
        }
    }
}
