using System.Text.Json;
using System.Text.Json.Serialization;

using Confluent.Kafka;

using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateSlimBuilder(args);

//builder.Services.ConfigureHttpJsonOptions(options =>
//{
//    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
//});

// Add services
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

// Kafka configuration
var kafkaBootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BROKERS") ?? "kafka:9092";

// Register Kafka producer and consumer as singletons
builder.Services.AddSingleton(sp =>
{
    var config = new ProducerConfig
    {
        BootstrapServers = kafkaBootstrapServers,
        ClientId = "events-service-producer"
    };
    return new ProducerBuilder<string, string>(config).Build();
});

builder.Services.AddSingleton(sp =>
{
    var config = new ConsumerConfig
    {
        BootstrapServers = kafkaBootstrapServers,
        GroupId = "events-service-group",
        AutoOffsetReset = AutoOffsetReset.Earliest,
        EnableAutoCommit = true
    };
    return config;
});

// Background service to consume messages
builder.Services.AddHostedService<KafkaConsumerService>();

var app = builder.Build();

//if (app.Environment.IsDevelopment())
//{
app.MapOpenApi();
app.UseSwaggerUI(c =>
     c.SwaggerEndpoint("/openapi/v1.json", "API Telemetry Service v1")
);
//}


// Events Endpoints

app.MapPost("/api/events/movie", async (
    [FromBody] MovieEventDto movieEvent,
    [FromServices] IProducer<string, string> producer) =>
{
    var eventId = Guid.NewGuid().ToString();
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    var payload = new
    {
        id = eventId,
        type = "movie",
        timestamp = timestamp,
        payload = movieEvent
    };

    var json = JsonSerializer.Serialize(payload);
    var topic = "movie-events";
    var message = new Message<string, string> { Key = eventId, Value = json };

    try
    {
        var deliveryResult = await producer.ProduceAsync(topic, message);
        var response = new EventResponseDto
        (
            Status: "success",
            Partition: deliveryResult.Partition.Value,
            Offset: deliveryResult.Offset.Value,
            Event: payload
        );
        return Results.Created($"/api/events/{eventId}", response);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to produce movie event: {ex}");
        return Results.StatusCode(500);
    }
})
.WithName("createMovieEvent")
.WithTags("events");

app.MapPost("/api/events/user", async (
    [FromBody] UserEventDto userEvent,
    [FromServices] IProducer<string, string> producer) =>
{
    var eventId = Guid.NewGuid().ToString();
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    if (string.IsNullOrEmpty(userEvent.Timestamp))
    {
        //userEvent.Timestamp = timestamp;
    }

    var payload = new
    {
        id = eventId,
        type = "user",
        timestamp =timestamp,
        payload = userEvent
    };

    var json = JsonSerializer.Serialize(payload);
    var topic = "user-events";
    var message = new Message<string, string> { Key = eventId, Value = json };

    try
    {
        var deliveryResult = await producer.ProduceAsync(topic, message);
        var response = new EventResponseDto
        (
            Status: "success",
            Partition: deliveryResult.Partition.Value,
            Offset: deliveryResult.Offset.Value,
            Event: payload
        );
        return Results.Created($"/api/events/{eventId}", response);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to produce user event: {ex}");
        return Results.StatusCode(500);
    }
})
.WithName("createUserEvent")
.WithTags("events");

app.MapPost("/api/events/payment", async (
    [FromBody] PaymentEventDto paymentEvent,
    [FromServices] IProducer<string, string> producer) =>
{
    var eventId = Guid.NewGuid().ToString();
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    
    if (string.IsNullOrEmpty(paymentEvent.Timestamp))
    {
        //paymentEvent.Timestamp = timestamp;
    }

    var payload = new
    {
        id = eventId,
        type = "payment",
        timestamp,
        payload = paymentEvent
    };

    var json = JsonSerializer.Serialize(payload);
    var topic = "payment-events";
    var message = new Message<string, string> { Key = eventId, Value = json };

    try
    {
        var deliveryResult = await producer.ProduceAsync(topic, message);
        var response = new EventResponseDto
        (
            Status: "success",
            Partition: deliveryResult.Partition.Value,
            Offset: deliveryResult.Offset.Value,
            Event: payload
        );
        return Results.Created($"/api/events/{eventId}", response);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to produce payment event: {ex}");
        return Results.StatusCode(500);
    }
})
.WithName("createPaymentEvent")
.WithTags("events");

// Health check
app.MapGet("/api/events/health", () => Results.Ok(new StatusResultDto(true)))
    .WithName("getEventsServiceHealth")
    .WithTags("health1");

app.Run();

// --- DTOs ---

record MovieEventDto(
    int MovieId,
    string Title,
    string Action,
    int? UserId = null,
    double? Rating = null,
    string[]? Genres = null,
    string? Description = null);

record UserEventDto(
    int UserId,
    string Action,
    string Timestamp,
    string? Username = null,
    string? Email = null);

record PaymentEventDto(
    int PaymentId,
    int UserId,
    double Amount,
    string Status,
    string Timestamp,
    string? MethodType = null);

record EventResponseDto(
    string Status,
    int Partition,
    long Offset,
    object Event);

public record StatusResultDto(bool Success);

//[JsonSerializable(typeof(MovieEventDto))]
//[JsonSerializable(typeof(UserEventDto))]
//[JsonSerializable(typeof(PaymentEventDto))]
//[JsonSerializable(typeof(EventResponseDto))]
//[JsonSerializable(typeof(StatusResultDto))]
//internal partial class AppJsonSerializerContext : JsonSerializerContext
//{
//}

// --- Kafka Consumer Background Service ---

public class KafkaConsumerService : BackgroundService
{
    private readonly ConsumerConfig _config;
    private readonly ILogger<KafkaConsumerService> _logger;

    public KafkaConsumerService(ConsumerConfig config, ILogger<KafkaConsumerService> logger)
    {
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var topics = new[] { "movie-events", "user-events", "payment-events" };

        using var consumer = new ConsumerBuilder<string, string>(_config).Build();
        consumer.Subscribe(topics);

        _logger.LogInformation("Kafka consumer started. Listening to topics: {Topics}", string.Join(", ", topics));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(stoppingToken);
                if (result?.Message != null)
                {
                    _logger.LogInformation("Consumed event from topic '{Topic}' [Partition={Partition}, Offset={Offset}]: {Value}",
                        result.Topic,
                        result.Partition.Value,
                        result.Offset.Value,
                        result.Message.Value);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consuming Kafka message");
            }
        }

        consumer.Close();
    }
}