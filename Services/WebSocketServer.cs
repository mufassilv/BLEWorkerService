using System.Text.Json;
using Microsoft.Extensions.Logging;
using BLEWorkerService.Models;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace BLEWorkerService.Services
{
    public class WebSocketServer
    {
        private readonly ILogger<WebSocketServer> _logger;
        private readonly Worker _worker;
        private readonly HashSet<WebSocket> _sockets = new HashSet<WebSocket>();
        private const int BufferSize = 4096;

        public WebSocketServer(ILogger<WebSocketServer> logger, Worker worker)
        {
            _logger = logger;
            _worker = worker;
        }

        public async Task HandleWebSocketConnection(HttpContext context, WebSocket webSocket)
        {
            try
            {
                _logger.LogInformation("New WebSocket connection attempt from {RemoteIpAddress}", context.Connection.RemoteIpAddress);

                var buffer = new byte[1024 * 4];
                WebSocketReceiveResult result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                while (!result.CloseStatus.HasValue)
                {
                    _logger.LogInformation("Received WebSocket message. MessageType: {MessageType}, EndOfMessage: {EndOfMessage}, Count: {Count}",
                                            result.MessageType, result.EndOfMessage, result.Count);

                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogInformation("Received message: {Message}", message);

                    // Process the message here

                    // Echo the message back (for testing purposes)
                    await webSocket.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);

                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }

                await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
                _logger.LogInformation("WebSocket connection closed. Status: {CloseStatus}, Description: {CloseStatusDescription}",
                                        result.CloseStatus, result.CloseStatusDescription);
            }
            catch (WebSocketException ex)
            {
                _logger.LogError($"WebSocket error: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Unexpected error: {ex.Message}");
            }
            finally
            {
                _logger.LogInformation("WebSocket connection closed");
            }
        }

        public async Task AcceptSocketAsync(WebSocket socket)
        {
            _sockets.Add(socket);
            _logger.LogInformation("New WebSocket connection accepted");

            try
            {
                await HandleClientMessages(socket);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error handling client messages: {ex.Message}");
            }
            finally
            {
                _sockets.Remove(socket);
                _logger.LogInformation("WebSocket connection closed");
            }
        }

        private async Task HandleClientMessages(WebSocket socket)
        {
            var buffer = new byte[BufferSize];

            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    break;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                _logger.LogInformation($"Received message: {message}");

                try
                {
                    var messageObj = JsonSerializer.Deserialize<WebSocketMessage>(message);

                    switch (messageObj.Type)
                    {
                        case "activateService":
                            _worker.Activate();
                            break;
                        case "deactivateService":
                            _worker.Deactivate();
                            break;
                        case "scanDevices":
                            var devices = await _worker.ScanDevicesAsync();
                            await SendScannedDeviceList(devices);
                            _logger.LogInformation("Starting device scan...");
                            break;
                        case "connectDevice":
                            if (messageObj.Data != null)
                            {
                                await _worker.ConnectToDeviceAsync(messageObj.Data);
                            }
                            break;
                        case "disconnectDevice":
                            _worker.Disconnect();
                            await SendDeviceStatus("Device disconnected");
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError($"Error deserializing message: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing message: {ex.Message}");
                }
            }
        }

        public async Task SendMessageAsync(string type, string data)
        {
            var message = new WebSocketMessage { Type = type, Data = data };
            var jsonMessage = JsonSerializer.Serialize(message);
            var messageBuffer = Encoding.UTF8.GetBytes(jsonMessage);

            var deadSockets = new List<WebSocket>();

            foreach (var socket in _sockets)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.SendAsync(
                            new ArraySegment<byte>(messageBuffer),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None
                        );
                    }
                    else
                    {
                        deadSockets.Add(socket);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error sending message: {ex.Message}");
                    deadSockets.Add(socket);
                }
            }

            foreach (var socket in deadSockets)
            {
                _sockets.Remove(socket);
            }
        }

        public async Task SendScannedDeviceList(List<string> deviceNames)
        {
            await SendMessageAsync("deviceList", JsonSerializer.Serialize(deviceNames));
        }

        public async Task SendDeviceStatus(string message)
        {
            await SendMessageAsync("deviceStatus", message);
        }

        public async Task SendStrokeData(StrokeData strokeData)
        {
            await SendMessageAsync("strokeData", JsonSerializer.Serialize(strokeData));
        }
    }
}