namespace RingCentral_amoCRM.Helpers;

// Общий ограничитель на heavy-group вызовы RingCentral (call-log, запись
// разговора) для всего процесса. До этого класса три фоновых сервиса (POLL,
// STARTUP catch-up, LATE) независимо друг от друга делали запросы к RC и
// независимо друг от друга ретраили 429 (см. AmoCrmService.RunWithRcRetryAsync) —
// но лимит heavy-group у RC один на аккаунт, а не по одному на сервис. На проде
// после разнесения стартов по времени (Startup:CatchUpDelaySeconds/
// LateAttachDelaySeconds) это не помогло: 8 отказов "rate exceeded" за 10 минут
// в первые секунды после старта, STARTUP catch-up не получил ни одной страницы
// журнала — сдвиг по времени не спасает, если сам лимит восстанавливается
// медленнее, чем разнесены старты, и/или ретраи одного сервиса продолжают жечь
// тот же бюджет, пока другой сервис уже вошёл в свой собственный ретрай-цикл.
//
// Решение: единая точка прохода для ВСЕХ heavy-group вызовов процесса —
// AmoCrmService.RunWithRcRetryAsync берёт лок здесь перед каждой попыткой.
// Два правила:
//   1. Не более одного heavy-group запроса одновременно (SemaphoreSlim(1,1)) —
//      сериализация вместо независимой конкуренции трёх сервисов.
//   2. Минимальный интервал между запросами (MinIntervalBetweenCalls) — даже
//      без 429 подряд идущие вызовы от разных сервисов не должны идти впритык.
//   3. Если RC вернул 429 с Retry-After — этот срок становится общим для ВСЕХ
//      следующих вызовов (не только того, который получил 429), пока не
//      истечёт. Раньше об этом узнавал только тот сервис, который поймал 429;
//      остальные продолжали ходить в API как ни в чём не бывало и получали
//      свои 429 тоже.
public class RcHeavyGroupRateLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    // Минимальный интервал между heavy-group вызовами даже при отсутствии 429 —
    // консервативная защита от "трёх сервисов почти одновременно", когда каждый
    // по отдельности укладывается в лимит, но все вместе — нет. Переопределяется
    // через RingCentral__HeavyGroupMinIntervalSeconds без пересборки, если 3с
    // на практике всё ещё окажется мало.
    private readonly TimeSpan _minIntervalBetweenCalls;

    public RcHeavyGroupRateLimiter(IConfiguration configuration)
    {
        _minIntervalBetweenCalls = TimeSpan.FromSeconds(
            configuration.GetValue("RingCentral:HeavyGroupMinIntervalSeconds", 3));
    }

    // Захватывает единственный слот на время одного вызова и, если нужно, ждёт
    // либо до истечения минимального интервала, либо до срока, наложенного
    // предыдущим 429 (что бы ни было позже). Вызывающий код (RunWithRcRetryAsync)
    // обязан вызвать ReleaseAsync/Dispose возвращённого IDisposable ровно один
    // раз после завершения запроса (успешного или нет).
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            TimeSpan waitFor;
            lock (_stateLock)
            {
                waitFor = _nextAllowedUtc - DateTime.UtcNow;
            }

            if (waitFor > TimeSpan.Zero)
            {
                await Task.Delay(waitFor, cancellationToken);
            }
        }
        catch
        {
            // Отмена/сбой во время ожидания — слот ещё не выдан вызывающему
            // (Releaser не создан), значит освободить семафор нужно здесь,
            // иначе он останется захваченным навсегда.
            _gate.Release();
            throw;
        }

        return new Releaser(this);
    }

    // Вызывается после 429 с известной задержкой Retry-After — отодвигает общий
    // "следующий разрешённый момент" для ВСЕХ сервисов, а не только текущего
    // вызывающего. Если уже установлен более поздний срок (другой вызов только
    // что получил свой, более долгий Retry-After) — не сокращаем его.
    public void ReportRateLimited(TimeSpan retryAfter)
    {
        var candidate = DateTime.UtcNow + retryAfter;
        lock (_stateLock)
        {
            if (candidate > _nextAllowedUtc)
            {
                _nextAllowedUtc = candidate;
            }
        }
    }

    private void Release()
    {
        lock (_stateLock)
        {
            var candidate = DateTime.UtcNow + _minIntervalBetweenCalls;
            if (candidate > _nextAllowedUtc)
            {
                _nextAllowedUtc = candidate;
            }
        }

        _gate.Release();
    }

    private sealed class Releaser : IDisposable
    {
        private readonly RcHeavyGroupRateLimiter _owner;
        private bool _disposed;

        public Releaser(RcHeavyGroupRateLimiter owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Release();
        }
    }
}
