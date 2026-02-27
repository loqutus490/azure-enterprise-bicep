using Azure;
using Azure.Identity;
using Azure.AI.OpenAI;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Create credential once
var credential = new DefaultAzureCredential();

// Configure Azure OpenAI client once
var endpoint = new Uri("https://agent13-openai-dev.openai.azure.com/");
var openAiClient = new AzureOpenAIClient(endpoint, credential);

// Test endpoint
app.MapGet("/test-openai", async () =>
{
    var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

    var response = await chatClient.CompleteChatAsync(
        "Say hello from private Azure OpenAI."
    );

    return Results.Ok(response.Value.Content[0].Text);
});

app.Run();
