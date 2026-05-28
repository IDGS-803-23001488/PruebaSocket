using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

List<WebSocket> sockets = new();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var socket = await context.WebSockets.AcceptWebSocketAsync();

        sockets.Add(socket);

        Console.WriteLine("ESP32 conectado");

        while (socket.State == WebSocketState.Open)
        {
            await Task.Delay(1000);
        }
    }
});

app.MapGet("/encender", async () =>
{
    var mensaje = Encoding.UTF8.GetBytes("LED_ON");

    foreach (var socket in sockets)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.SendAsync(
                mensaje,
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }
    }

    return "Comando enviado";
});

app.Run();
