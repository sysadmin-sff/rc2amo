using Microsoft.AspNetCore.Mvc;
using RingCentral_amoCRM.Helpers;

[ApiController] 
[Route("oauth")] 
public class AmoCrmController : ControllerBase
{
    private readonly AmoCrmService _amoCrmService;
    private readonly ILogger<AmoCrmController> _logger;
    
    public AmoCrmController(AmoCrmService amoCrmService, ILogger<AmoCrmController> logger)
    {
        _amoCrmService = amoCrmService;
        _logger = logger;
    }
    
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            _logger.LogError("amoCRM не предоставил код авторизации.");
            if (HttpContext.Request.Query.ContainsKey("error"))
            {
                return BadRequest($"Авторизация не удалась. Причина: {HttpContext.Request.Query["error"]}");
            }
            return BadRequest("Код авторизации не найден.");
        }

        try
        {
            _logger.LogInformation($"Получен код авторизации. Выполняется обмен на токены...");
            
            // ВЫЗОВ НОВОГО МЕТОДА: Обмен кода на Refresh Token и сохранение его
            await _amoCrmService.ExchangeCodeForTokensAsync(code); 
            
            return Ok("✅ Интеграция amoCRM успешно авторизована и токены сохранены! Можете вернуться к приложению.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Критическая ошибка при обмене кода авторизации на токены.");
            return StatusCode(500, "Ошибка сервера при обмене токенов.");
        }
    }
}