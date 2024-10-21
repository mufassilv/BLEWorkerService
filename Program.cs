using BLEWorkerService.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddSingleton<Worker>();
builder.Services.AddSingleton<WebSocketServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

builder.Services.AddCors();

var app = builder.Build();

// Configure WebSocket middleware
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
};

app.UseWebSockets(webSocketOptions);

// WebSocket endpoint
app.Use(async (context, next) =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        if (context.Request.Path == "/ws")
        {
            var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            var webSocketServer = app.Services.GetRequiredService<WebSocketServer>();
            await webSocketServer.HandleWebSocketConnection(context, webSocket);
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    else
    {
        await next();
    }
});

// CORS configuration for React app
app.UseCors(builder => builder
    .WithOrigins("http://localhost:3000")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials());

app.Run("http://localhost:5000");