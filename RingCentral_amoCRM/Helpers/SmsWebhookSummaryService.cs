// Почасовая сводка по WEBHOOK-событиям message-store (не-instant фильтр,
// добавлен для исходящих SMS — см. SubscriptionService; тот же фильтр
// расширен под голосовую почту, см. RecordVoicemailWebhookSummary). На проде
// ожидается заметный объём: 167 доп. исходящих SMS за 3 дня плюс read/delete/
// status-change события на те же сообщения — логировать по строке на каждое
// было бы шумно (для голосовых по каждому новому всё равно пишется отдельная
// строка VM id=... — см. ProcessVoicemailAsync, здесь только счётчики
// newCount/updatedCount самого вебхука). AmoCrmService копит счётчики
// (RecordSmsWebhookSummary/RecordVoicemailWebhookSummary), этот сервис раз в
// час забирает и логирует их одной строкой.
//
// Отдельный таймер, а не переиспользование цикла SubscriptionHostedService:
// у той службы своя ответственность (обновление подписки раз в 5 дней) и свой
// цикл ожидания — совмещать с ней сводку по вебхукам смешало бы два независимых
// повода для пробуждения.
public class SmsWebhookSummaryService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SmsWebhookSummaryService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public SmsWebhookSummaryService(IServiceProvider serviceProvider, ILogger<SmsWebhookSummaryService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var amoService = scope.ServiceProvider.GetRequiredService<AmoCrmService>();
                var (newCount, updatedCount, otherCount) = amoService.FlushSmsWebhookSummary();
                var (vmNewCount, vmUpdatedCount) = amoService.FlushVoicemailWebhookSummary();

                if (newCount == 0 && updatedCount == 0 && otherCount == 0 && vmNewCount == 0 && vmUpdatedCount == 0)
                {
                    _logger.LogInformation("WEBHOOK SUMMARY period=1h newSms=0 updatedSms=0 newVm=0 updatedVm=0 other=0 (no message-store events)");
                    continue;
                }

                _logger.LogInformation(
                    "WEBHOOK SUMMARY period=1h newSms={NewCount} updatedSms={UpdatedCount} newVm={VmNewCount} updatedVm={VmUpdatedCount} other={OtherCount}",
                    newCount, updatedCount, vmNewCount, vmUpdatedCount, otherCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SmsWebhookSummaryService: error while flushing summary");
            }
        }

        _logger.LogInformation("SmsWebhookSummaryService is stopping.");
    }
}
