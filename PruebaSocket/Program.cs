using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PruebaSocket.Data;
using PruebaSocket.Models;

LoadEnvFile();

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var connectionString = BuildMySqlConnectionString();
var mysqlVersion = ParseMySqlVersion(Environment.GetEnvironmentVariable("MYSQL_SERVER_VERSION") ?? "8.0.36");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, new MySqlServerVersion(mysqlVersion)));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultFiles();
app.UseStaticFiles();

var runtimeMode = Environment.GetEnvironmentVariable("APP_RUNTIME_MODE") ?? "LocalDebug";
if (!string.Equals(runtimeMode, "LocalDebug", StringComparison.OrdinalIgnoreCase))
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromMinutes(2)
});

var sockets = new ConcurrentDictionary<string, WebSocket>(StringComparer.OrdinalIgnoreCase);
var messageStreams = new ConcurrentDictionary<Guid, Channel<MessageEvent>>();
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var deviceKey = context.Request.Query["deviceKey"].ToString();
    var targetDeviceKey = context.Request.Query["targetDeviceKey"].ToString();

    if (string.IsNullOrWhiteSpace(deviceKey))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("Falta el parametro deviceKey.");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    sockets[deviceKey] = socket;

    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var sourceDevice = await RegisterOrUpdateDeviceAsync(db, deviceKey, context.RequestAborted);

    Console.WriteLine($"ESP32 conectado: {deviceKey}");

    try
    {
        await ReceiveMessagesAsync(socket, sourceDevice.Id, targetDeviceKey, sockets, messageStreams, app.Services, context.RequestAborted);
    }
    finally
    {
        sockets.TryRemove(deviceKey, out _);
        Console.WriteLine($"ESP32 desconectado: {deviceKey}");
    }
});

app.MapGet("/devices", async (ApplicationDbContext db) =>
    await db.Esp32Devices
        .OrderBy(device => device.Name)
        .Select(device => new
        {
            device.Id,
            device.DeviceKey,
            device.Name,
            device.Description,
            device.IsActive,
            device.LastSeenAtUtc
        })
        .ToListAsync());

app.MapPost("/devices", async (CreateDeviceRequest request, ApplicationDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(request.DeviceKey) || string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest("DeviceKey y Name son obligatorios.");
    }

    var exists = await db.Esp32Devices.AnyAsync(device => device.DeviceKey == request.DeviceKey);
    if (exists)
    {
        return Results.Conflict("Ya existe un ESP32 con ese DeviceKey.");
    }

    var device = new Esp32Device
    {
        DeviceKey = request.DeviceKey.Trim(),
        Name = request.Name.Trim(),
        Description = request.Description?.Trim()
    };

    db.Esp32Devices.Add(device);
    await db.SaveChangesAsync();

    return Results.Created($"/devices/{device.Id}", device);
});

app.MapGet("/messages", async (ApplicationDbContext db) =>
    await db.Esp32Messages
        .Include(message => message.SourceDevice)
        .Include(message => message.TargetDevice)
        .OrderByDescending(message => message.CreatedAtUtc)
        .Take(100)
        .Select(message => new MessageEvent(
            message.Id,
            message.SourceDevice.DeviceKey,
            message.TargetDevice == null ? null : message.TargetDevice.DeviceKey,
            message.Message,
            message.Response,
            message.WasProcessed,
            message.ProcessingError,
            message.CreatedAtUtc))
        .ToListAsync());

app.MapGet("/message-events", async context =>
{
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Connection = "keep-alive";
    context.Response.ContentType = "text/event-stream";

    var clientId = Guid.NewGuid();
    var channel = Channel.CreateUnbounded<MessageEvent>();
    messageStreams[clientId] = channel;

    try
    {
        await context.Response.WriteAsync("event: connected\ndata: {}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);

        await foreach (var message in channel.Reader.ReadAllAsync(context.RequestAborted))
        {
            var payload = JsonSerializer.Serialize(message, jsonOptions);
            await context.Response.WriteAsync($"event: message\ndata: {payload}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
    finally
    {
        messageStreams.TryRemove(clientId, out _);
    }
});

app.MapGet("/encender", async (ApplicationDbContext db) =>
{
    var mensaje = Encoding.UTF8.GetBytes("LED_ON");
    var enviados = 0;

    foreach (var socket in sockets.Values)
    {
        if (socket.State != WebSocketState.Open)
        {
            continue;
        }

        await socket.SendAsync(mensaje, WebSocketMessageType.Text, true, CancellationToken.None);
        enviados++;
    }

    var log = new Esp32Message
    {
        SourceDeviceId = await GetOrCreateServerDeviceIdAsync(db),
        Message = "LED_ON",
        Response = $"Comando enviado a {enviados} socket(s).",
        WasProcessed = enviados > 0
    };

    db.Esp32Messages.Add(log);
    await db.SaveChangesAsync();
    PublishMessage(messageStreams, new MessageEvent(log.Id, "server", null, log.Message, log.Response, log.WasProcessed, log.ProcessingError, log.CreatedAtUtc));

    return $"Comando enviado a {enviados} socket(s).";
});

app.MapControllers();

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

static async Task ReceiveMessagesAsync(
    WebSocket socket,
    int sourceDeviceId,
    string targetDeviceKey,
    ConcurrentDictionary<string, WebSocket> sockets,
    ConcurrentDictionary<Guid, Channel<MessageEvent>> messageStreams,
    IServiceProvider services,
    CancellationToken cancellationToken)
{
    var buffer = new byte[4096];

    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
    {
        var result = await socket.ReceiveAsync(buffer, cancellationToken);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Conexion cerrada", cancellationToken);
            break;
        }

        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
        await ProcessEsp32MessageAsync(sourceDeviceId, targetDeviceKey, message, sockets, messageStreams, services, cancellationToken);
    }
}

static async Task ProcessEsp32MessageAsync(
    int sourceDeviceId,
    string targetDeviceKey,
    string message,
    ConcurrentDictionary<string, WebSocket> sockets,
    ConcurrentDictionary<Guid, Channel<MessageEvent>> messageStreams,
    IServiceProvider services,
    CancellationToken cancellationToken)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    Esp32Device? targetDevice = null;
    WebSocket? targetSocket = null;

    if (!string.IsNullOrWhiteSpace(targetDeviceKey))
    {
        targetDevice = await db.Esp32Devices.FirstOrDefaultAsync(device => device.DeviceKey == targetDeviceKey, cancellationToken);
        sockets.TryGetValue(targetDeviceKey, out targetSocket);
    }

    var log = new Esp32Message
    {
        SourceDeviceId = sourceDeviceId,
        TargetDeviceId = targetDevice?.Id,
        Message = message
    };

    try
    {
        if (targetSocket is not { State: WebSocketState.Open })
        {
            log.Response = string.IsNullOrWhiteSpace(targetDeviceKey)
                ? "Mensaje recibido, sin ESP32 destino configurado."
                : $"Mensaje recibido, pero {targetDeviceKey} no esta conectado.";
            log.WasProcessed = false;
        }
        else
        {
            var payload = Encoding.UTF8.GetBytes(message);
            await targetSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            log.Response = $"Mensaje reenviado a {targetDeviceKey}.";
            log.WasProcessed = true;
        }
    }
    catch (Exception ex)
    {
        log.Response = "No se pudo procesar el mensaje.";
        log.ProcessingError = ex.Message;
        log.WasProcessed = false;
    }

    db.Esp32Messages.Add(log);
    await db.SaveChangesAsync(cancellationToken);

    var sourceDeviceKey = await db.Esp32Devices
        .Where(device => device.Id == sourceDeviceId)
        .Select(device => device.DeviceKey)
        .FirstAsync(cancellationToken);

    PublishMessage(messageStreams, new MessageEvent(
        log.Id,
        sourceDeviceKey,
        targetDevice?.DeviceKey,
        log.Message,
        log.Response,
        log.WasProcessed,
        log.ProcessingError,
        log.CreatedAtUtc));
}

static void PublishMessage(
    ConcurrentDictionary<Guid, Channel<MessageEvent>> messageStreams,
    MessageEvent message)
{
    foreach (var stream in messageStreams.Values)
    {
        stream.Writer.TryWrite(message);
    }
}

static async Task<Esp32Device> RegisterOrUpdateDeviceAsync(
    ApplicationDbContext db,
    string deviceKey,
    CancellationToken cancellationToken)
{
    var device = await db.Esp32Devices.FirstOrDefaultAsync(item => item.DeviceKey == deviceKey, cancellationToken);

    if (device is null)
    {
        device = new Esp32Device
        {
            DeviceKey = deviceKey.Trim(),
            Name = deviceKey.Trim()
        };
        db.Esp32Devices.Add(device);
    }

    device.LastSeenAtUtc = DateTime.UtcNow;
    await db.SaveChangesAsync(cancellationToken);

    return device;
}

static async Task<int> GetOrCreateServerDeviceIdAsync(ApplicationDbContext db)
{
    const string serverKey = "server";
    var device = await db.Esp32Devices.FirstOrDefaultAsync(item => item.DeviceKey == serverKey);

    if (device is not null)
    {
        return device.Id;
    }

    device = new Esp32Device
    {
        DeviceKey = serverKey,
        Name = "Servidor"
    };

    db.Esp32Devices.Add(device);
    await db.SaveChangesAsync();

    return device.Id;
}

static string BuildMySqlConnectionString()
{
    var connectionString = Environment.GetEnvironmentVariable("MYSQL_CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(connectionString))
    {
        return connectionString;
    }

    var host = Environment.GetEnvironmentVariable("MYSQL_HOST") ?? "localhost";
    var port = Environment.GetEnvironmentVariable("MYSQL_PORT") ?? "3306";
    var database = Environment.GetEnvironmentVariable("MYSQL_DATABASE") ?? "prueba_socket";
    var user = Environment.GetEnvironmentVariable("MYSQL_USER") ?? "root";
    var password = Environment.GetEnvironmentVariable("MYSQL_PASSWORD") ?? string.Empty;

    return $"Server={host};Port={port};Database={database};User={user};Password={password};";
}

static Version ParseMySqlVersion(string versionValue)
{
    var numericPart = new string(versionValue
        .TakeWhile(character => char.IsDigit(character) || character == '.')
        .ToArray());

    return Version.TryParse(numericPart, out var version)
        ? version
        : new Version(8, 0, 36);
}

static void LoadEnvFile()
{
    var startDirectories = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    };

    foreach (var startDirectory in startDirectories)
    {
        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var envPath = Path.Combine(directory.FullName, ".env");
            if (File.Exists(envPath))
            {
                foreach (var line in File.ReadAllLines(envPath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                    {
                        continue;
                    }

                    var separatorIndex = trimmed.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = trimmed[..separatorIndex].Trim();
                    var value = trimmed[(separatorIndex + 1)..].Trim().Trim('"');

                    if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }

                return;
            }

            directory = directory.Parent;
        }
    }
}

public sealed record CreateDeviceRequest(string DeviceKey, string Name, string? Description);

public sealed record MessageEvent(
    long Id,
    string SourceDevice,
    string? TargetDevice,
    string Message,
    string? Response,
    bool WasProcessed,
    string? ProcessingError,
    DateTime CreatedAtUtc);
