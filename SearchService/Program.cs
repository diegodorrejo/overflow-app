using System.Text.RegularExpressions;
using SearchService.Data;
using SearchService.Models;
using Typesense;
using Typesense.Setup;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Wolverine;
using Wolverine.RabbitMQ;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.AddServiceDefaults();

builder.Services.AddOpenTelemetry().WithTracing(providerBuilder =>
{
    providerBuilder.SetResourceBuilder(
        ResourceBuilder.CreateDefault().AddService(builder.Environment.ApplicationName)
    ).AddSource("Wolverine");
});

builder.Host.UseWolverine(opts =>
{
    opts.UseRabbitMqUsingNamedConnection("messaging")
        .AutoProvision();
    opts.ListenToRabbitQueue("questions.search", config =>
    {
        config.BindExchange("questions");
    });
});

var typesenseUri = builder.Configuration["services:typesense:typesense:0"];
if (string.IsNullOrEmpty(typesenseUri))
    throw new InvalidOperationException("Typesense URI not found in config");

var typesenseApiKey = builder.Configuration["typesense-api-key"];
if(string.IsNullOrEmpty(typesenseApiKey))
    throw new InvalidOperationException("Typesense Api Key not found in config");
    
var uri = new Uri(typesenseUri);
var protocol = uri.Scheme.Equals("typesense", StringComparison.OrdinalIgnoreCase) 
    ? "http" 
    : uri.Scheme;

builder.Services.AddTypesenseClient(config =>
{
    config.ApiKey = typesenseApiKey;
    config.Nodes = new List<Node>
    {
        new(uri.Host, uri.Port.ToString(), protocol)
    };
});

builder.Services.AddHttpClient("ITypesenseClient") 
    .AddResilienceHandler("typesense-retry-strategy", pipelineBuilder =>
    {
        pipelineBuilder.AddRetry(new HttpRetryStrategyOptions
        {
            ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                .HandleResult(res => res.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                .HandleResult(res => (int)res.StatusCode == 422),
            
            MaxRetryAttempts = 6,
            Delay = TimeSpan.FromSeconds(3),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });
    });

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapDefaultEndpoints();

app.MapGet("/search/similar-titles", async (string query, ITypesenseClient client) =>
{
    var searchParams = new SearchParameters(query, "title");

    try
    {
        var result = await client.Search<SearchQuestion>("questions", searchParams);
        return Results.Ok(result.Hits.Select(hit => hit.Document));
    }
    catch (Exception e)
    {
        return Results.Problem("Typesense search failed", e.Message);
    }
});

app.MapGet("/search", async (string query, ITypesenseClient client) =>
{
    // ... (Tu lógica de Regex y SearchParameters se mantiene igual)
    string? tag = null;
    var tagMatch = Regex.Match(query, @"\[(.*?)\]");
    if (tagMatch.Success)
    {
        tag = tagMatch.Groups[1].Value;
        query = query.Replace(tagMatch.Value, "").Trim();
    }

    var searchParams = new SearchParameters(query, "title,content");

    if (!string.IsNullOrEmpty(tag))
    {
        searchParams.FilterBy = $"tags:=[{tag}]";
    }

    try
    {
        var result = await client.Search<SearchQuestion>("questions", searchParams);
        return Results.Ok(result.Hits.Select(hit => hit.Document));
    }
    catch (Exception e)
    {
        return Results.Problem("Typesense search failed", e.Message);
    }
});

// Inicialización de datos
using (var scope = app.Services.CreateScope())
{
    var clientTs = scope.ServiceProvider.GetRequiredService<ITypesenseClient>();
    // Ahora esta llamada esperará automáticamente si Typesense devuelve "Not Ready"
    await SearchInitializer.EnsureIndexExists(clientTs);
}

app.Run();