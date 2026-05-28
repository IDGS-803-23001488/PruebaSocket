using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Agregar servicios esenciales al contenedor (Controladores y OpenAPI si los usas)
builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

// Configuración del pipeline de solicitudes HTTP
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Nota: A veces UseHttpsRedirection causa problemas con ESP32 si no maneja certificados SSL internamente.
// Si tu ESP32 tiene problemas para conectar, puedes comentar la línea de abajo.
app.UseHttpsRedirection();

app.UseAuthorization();

// --- 1. CONFIGURACIÓN DE WEBSOCKETS ---
var webSocketOptions = new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2) // Mantiene viva la conexión con tu ESP32
};
app.UseWebSockets(webSocketOptions);

// --- 2. TU LÓGICA DE WEBSOCKETS Y ENDPOINTS ---
List<WebSocket> sockets = new();

// Endpoint para que se conecte el ESP32
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
    else
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
    }
});

// Endpoint HTTP para encender el LED desde el navegador o Postman
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

// Mapear los controladores por si tienes rutas en la carpeta Controllers
app.MapControllers();

// --- 3. CONFIGURACIÓN DEL PUERTO PARA RENDER ---
// Lee el puerto dinámico asignado por Render en producción; usa el 8080 si estás en local.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");
