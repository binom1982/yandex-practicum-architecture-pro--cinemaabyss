using System;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateSlimBuilder(args);

// Регистрация всех стандартных inline-ограничений, включая regex (нужен для {**slug})
builder.Services.AddRouting(options =>
{
    // Это включит все стандартные ограничения: int, guid, bool, regex, minlength и т.д.
    // В том числе — поддержку catch-all (**)
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    //options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
//builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpClient();

var app = builder.Build();

//if (app.Environment.IsDevelopment())
//{
    app.MapOpenApi();
    //app.UseSwagger();
    app.UseSwaggerUI(c =>
         c.SwaggerEndpoint("/openapi/v1.json", "API Proxy Service v1")
    );
//}

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
var monolithUrl = Environment.GetEnvironmentVariable("MONOLITH_URL") ?? "http://localhost:9080";
var moviesServiceUrl = Environment.GetEnvironmentVariable("MOVIES_SERVICE_URL") ?? "http://localhost:9081";
var eventsServiceUrl = Environment.GetEnvironmentVariable("EVENTS_SERVICE_URL") ?? "http://localhost:9082";

Console.WriteLine($"PORT: {port}");
Console.WriteLine($"MONOLITH_URL: {monolithUrl}");
Console.WriteLine($"MOVIES_SERVICE_URL: {moviesServiceUrl}");
Console.WriteLine($"EVENTS_SERVICE_URL: {eventsServiceUrl}");

// Постепенная миграция
var isGradualMigration = Environment.GetEnvironmentVariable("GRADUAL_MIGRATION")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

/*
    0 - всегда монолит
    50 - 50/50
    100" - всегда микросервисы
*/
var migrationPercentValue = Environment.GetEnvironmentVariable("MOVIES_MIGRATION_PERCENT") ?? "0";
var migrationPercent = int.TryParse(migrationPercentValue, out var percent) && percent is >= 0 and <= 100 ? percent : 0;

var httpClient = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient();

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    Console.WriteLine($"{nameof(path)}: {path}");
    Console.WriteLine($"QueryString: {context.Request.QueryString}");

    if (
        path.StartsWithSegments("/api/users")
        || path.StartsWithSegments("/api/subscriptions")
        || path.StartsWithSegments("/api/payments")
    )
    {
        // Безопасное формирование URL с правильной обработкой query string
        var uriBuilder = new UriBuilder(monolithUrl)
        {
            Path = path.ToString().TrimStart('/').Trim('/') // Убираем лишние слеши
        };

        // Добавляем query string БЕЗ дублирования '?'
        if (context.Request.QueryString.HasValue &&
            !string.IsNullOrEmpty(context.Request.QueryString.Value))
        {
            // Убираем начальный '?' из QueryString.Value
            uriBuilder.Query = context.Request.QueryString.Value.TrimStart('?');
        }

        var targetUrl = uriBuilder.ToString();
        //Console.WriteLine($"{nameof(targetUrl)}: {targetUrl}");

        Console.WriteLine($"➡️ Proxying {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
        Console.WriteLine($"➡️ Target URL: {targetUrl}");
        await RedirectRequest(context, targetUrl);
        return; // не вызываем next()
    }

    await next(); // если не нашли — продолжаем конвейер
});


app.MapGet("/api/movies", async (HttpContext context) =>
{
    var useMoviesService = false;

    if (isGradualMigration)
    {
        // Generate a random number between 0 and 99
        var random = RandomNumberGenerator.GetInt32(100);
        useMoviesService = random < migrationPercent;
    }

    var targetUrl = useMoviesService 
        ? $"{moviesServiceUrl}/api/movies"
        : $"{monolithUrl}/api/movies";
    Console.WriteLine($"{nameof(targetUrl)}: {targetUrl}");
    await RedirectRequest(context, targetUrl);
});

//app.MapGet("/api/users", async (HttpContext context) =>
//{
//    var targetUrl = $"{monolithUrl}/api/users";
//    await RedirectRequest(context, targetUrl);
//});

//app.MapGet("/api/users/{**slug}", async (string slug, HttpContext context) =>
//{
//    var targetUrl = $"{monolithUrl}/api/users/{slug}";
//    await RedirectRequest(context, targetUrl);
//}).ExcludeFromDescription();

//app.MapGet("/api/events/{**slug}", async (string slug, HttpContext context) =>
//{
//    var targetUrl = $"{eventsServiceUrl}/api/events/{slug}";
//    await RedirectRequest(context, targetUrl);
//}).ExcludeFromDescription();

async Task RedirectRequest(HttpContext context, string targetUrl)
{
    try
    {
        var method = context.Request.Method;
        var requestMessage = new HttpRequestMessage(new HttpMethod(method), targetUrl);

        // Копируем заголовки (кроме Host)
        foreach (var header in context.Request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]))
            {
                requestMessage.Content ??= new StreamContent(Stream.Null);
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
            }
        }

        // Копируем тело запроса
        if (context.Request.ContentLength > 0 ||
            context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            var streamContent = new StreamContent(context.Request.Body);
            if (context.Request.Headers.TryGetValue("Content-Type", out var contentType))
            {
                streamContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            }
            requestMessage.Content = streamContent;
        }

        var response = await httpClient.SendAsync(requestMessage, context.RequestAborted);

        context.Response.StatusCode = (int)response.StatusCode;

        // Копируем заголовки ответа
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in response.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await response.Content.CopyToAsync(context.Response.Body);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Proxy error: {ex}");
        context.Response.StatusCode = (int)HttpStatusCode.BadGateway;
        await context.Response.WriteAsync("Gateway error");
    }
}

// Optional: Add a health endpoint
app.MapGet("/health", () => "OK");


//app.Run();
app.Run($"http://+:{port}");


/*
//app.Run($"http://*:{port}");

[JsonSerializable(typeof(TelemetryRecord[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}*/