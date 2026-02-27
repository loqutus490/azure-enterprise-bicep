using Azure.AI.OpenAI;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/test-openai", async () =>
{
    var endpoint = new Uri("https://agent13-openai-dev.openai.azure.com/");
    var credential = new DefaultAzureCredential();

    var openAiClient = new AzureOpenAIClient(endpoint, credential);

    var chatClient = openAiClient.GetChatClient("gpt-4.1-mini");

    var response = await chatClient.CompleteChatAsync(
        new ChatMessage[]
        {
            new UserChatMessage("Say hello from private Azure OpenAI.")
        });

    return Results.Ok(response.Value.Content[0].Text);
});

app.Run();

