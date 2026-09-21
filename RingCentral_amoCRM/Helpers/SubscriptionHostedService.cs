public class SubscriptionHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SubscriptionHostedService> _logger;
    private DateTime _lastSubscriptionRenewal = DateTime.MinValue;
    private readonly TimeSpan _subscriptionRenewalInterval = TimeSpan.FromDays(5);

    public SubscriptionHostedService(
        IServiceProvider serviceProvider, 
        ILogger<SubscriptionHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Небольшая задержка перед стартом
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        _logger.LogInformation("🚀 WebHook Hosted Service starting subscription management...");

        // Основной цикл работы
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Проверяем, нужно ли обновить подписки (при старте или каждые 5 дней)
                var now = DateTime.UtcNow;
                if (now - _lastSubscriptionRenewal >= _subscriptionRenewalInterval)
                {
                    _logger.LogInformation("Creating/renewing subscriptions...");
                    
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var subscriptionService = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                        
                        try
                        {
                            await subscriptionService.CreateSmsSubscriptionAsync();
                            _lastSubscriptionRenewal = now;
                            _logger.LogInformation("✅ Subscriptions renewed successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "❌ Error while creating/renewing subscriptions.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error during background service iteration.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("SubscriptionHostedService is stopping.");
    }
}