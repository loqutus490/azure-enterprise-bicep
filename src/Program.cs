using Azure.AI.OpenAI;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/test-openai", async () =>
{
    var endpoint = new Uri("https://agent13-openai-dev.openai.azure.com/");
    var credential = new DefaultAzureCredential();

    var client = new AzureOpenAIClient(endpoint, credential);

    var chatClient = client.GetChatClient("gpt-4.1-mini");

    var response = await chatClient.CompleteChatAsync(
        "Say hello from private Azure OpenAI.");

    return Results.Ok(response.Value.Content[0].Text);
});

app.Run();
