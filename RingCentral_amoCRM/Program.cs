using RingCentral;
using RingCentral_amoCRM.Helpers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();

var cid = builder.Configuration.GetSection("Credentials")["CID"];
var cs = builder.Configuration.GetSection("Credentials")["CS"];
var jwt = builder.Configuration.GetSection("Credentials")["JWT"];
var url = builder.Configuration.GetSection("Credentials")["URL"];

builder.Services.AddSingleton(_ =>
{
    var rc = new RestClient(cid, cs, url);
    return rc;
});


builder.Services.AddHttpClient("AmoCrmClient", client =>
    {
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    })
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        // Используем HttpClientHandler для лучшей совместимости с amoCRM
        var handler = new HttpClientHandler
        {
            // Явно устанавливаем только TLS 1.2 (наиболее совместимый вариант)
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12,
            
            // Включаем автоматическую декомпрессию
            AutomaticDecompression = 
                System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            
            // Настройки пула соединений
            MaxConnectionsPerServer = 10,
            
            // Дополнительные настройки
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseDefaultCredentials = false,
            
            // Проверка сертификата (для диагностики)
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (errors == System.Net.Security.SslPolicyErrors.None)
                    return true;
                
                // Логируем проблемы с сертификатом
                Console.WriteLine($"SSL Certificate validation warning: {errors}");
                if (cert != null)
                    Console.WriteLine($"Certificate Subject: {cert.Subject}");
                
                // Для разработки: разрешаем подключение даже с ошибками
                // В продакшене лучше возвращать false
                return true;
            }
        };
        return handler;
    });

builder.Services.AddSingleton<AmoCrmService>();
builder.Services.AddSingleton<CallProcessingGuard>();
builder.Services.AddScoped<SubscriptionService>();
builder.Services.AddHostedService<SubscriptionHostedService>();
builder.Services.AddHostedService<CallLogPollingService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UsePathBase("/rc2amocrm");

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "RingCentral Listener V1");
});

app.UseCors(c =>
{
    c.AllowAnyHeader();
    c.AllowAnyMethod();
    c.AllowAnyOrigin();
});

app.Use(async (context, next) =>
{
    context.Request.EnableBuffering();
    await next();
});

using (var scope = app.Services.CreateScope())
{
    var svc = scope.ServiceProvider.GetRequiredService<AmoCrmService>();
    await svc.InitializeAsync();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();