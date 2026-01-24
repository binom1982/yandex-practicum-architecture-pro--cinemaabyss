using System;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    //options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

var app = builder.Build();

//if (app.Environment.IsDevelopment())
//{
    app.MapOpenApi();
    app.UseSwaggerUI(c =>
         c.SwaggerEndpoint("/openapi/v1.json", "API Telemetry Service v1")
    );
//}

var monolithUrl = Environment.GetEnvironmentVariable("MONOLITH_URL") ?? "http://localhost:9080";
var moviesServiceUrl = Environment.GetEnvironmentVariable("MOVIES_SERVICE_URL") ?? "http://localhost:9081";

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

    await RedirectRequest(context, targetUrl);
});

app.MapGet("/api/users", async (HttpContext context) =>
{
    var targetUrl = $"{monolithUrl}/api/users";
    await RedirectRequest(context, targetUrl);
});

async Task RedirectRequest(HttpContext context ,string targetUrl)
{
    try
    {
        var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUrl);

        var queryString = context.Request.QueryString;

        requestMessage.RequestUri = !queryString.HasValue
            ? new Uri(targetUrl)
            : new Uri($"{targetUrl}{queryString}");


        // Copy headers (except host)
        foreach (var header in context.Request.Headers)
        {
            if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]))
            {
                requestMessage.Content ??= new StreamContent(Stream.Null);
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, [.. header.Value]);
            }
        }

        var response = await httpClient.SendAsync(requestMessage, context.RequestAborted);

        // Copy response status
        context.Response.StatusCode = (int)response.StatusCode;

        // Copy response headers
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in response.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        // Copy response body
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

app.Run();


/*
//app.Run($"http://*:{port}");

[JsonSerializable(typeof(TelemetryRecord[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{
}*/