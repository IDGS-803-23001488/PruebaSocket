# PruebaSocket

API ASP.NET Core para comunicar ESP32 mediante WebSockets y registrar la actividad en MySQL.

## Configuracion

El proyecto carga variables desde `.env` en desarrollo local. Usa `.env.copy` como plantilla.

Variables principales:

```env
APP_RUNTIME_MODE=LocalDebug

MYSQL_CONNECTION_STRING=
MYSQL_HOST=localhost
MYSQL_PORT=3306
MYSQL_DATABASE=prueba_socket
MYSQL_USER=root
MYSQL_PASSWORD=
MYSQL_SERVER_VERSION=8.0.11-TiDB-v8.5.3-serverless
```

`APP_RUNTIME_MODE=LocalDebug` evita la redireccion HTTPS para facilitar conexiones desde ESP32 en local. Para Render o produccion, usa otro valor, por ejemplo `RenderServer`, y define las variables desde el panel de Render.

Si `MYSQL_CONNECTION_STRING` tiene valor, la app usa esa cadena completa. Si esta vacia, arma la conexion con `MYSQL_HOST`, `MYSQL_PORT`, `MYSQL_DATABASE`, `MYSQL_USER` y `MYSQL_PASSWORD`.

## Base de datos

La base de datos usa MySQL con Entity Framework Core.

Modelos principales:

- `Esp32Device`: catalogo de dispositivos ESP32. Guarda `DeviceKey`, nombre, descripcion, estado y ultima conexion.
- `Esp32Message`: bitacora de mensajes. Guarda ESP32 origen, ESP32 destino opcional, mensaje, respuesta, si se pudo procesar y error si ocurrio.

Para crear o actualizar la base:

```powershell
dotnet tool restore
dotnet tool run dotnet-ef database update --project .\PruebaSocket\PruebaSocket.csproj --startup-project .\PruebaSocket\PruebaSocket.csproj
```

## Ejecucion local

```powershell
dotnet run --project .\PruebaSocket\PruebaSocket.csproj
```

Por defecto la app escucha en el puerto definido por `PORT`; si no existe, usa `8080`.

## WebSockets

Cada ESP32 debe conectarse indicando su identificador:

```text
ws://localhost:8080/ws?deviceKey=esp32-a
```

Para enviar lo que mande un ESP32 hacia otro ESP32 conectado:

```text
ws://localhost:8080/ws?deviceKey=esp32-a&targetDeviceKey=esp32-b
```

Cuando `esp32-a` envie un mensaje, el servidor intentara reenviarlo a `esp32-b` y guardara el resultado en la tabla de mensajes.

## Endpoints

- `GET /`: pantalla web para ver mensajes ESP32 en tiempo real.
- `GET /devices`: lista los ESP32 registrados.
- `POST /devices`: registra manualmente un ESP32.
- `GET /messages`: lista los ultimos 100 mensajes registrados.
- `GET /message-events`: stream SSE usado por la pantalla web.
- `GET /encender`: envia `LED_ON` a todos los sockets abiertos y registra el intento.

Ejemplo para registrar un ESP32:

```http
POST http://localhost:8080/devices
Content-Type: application/json

{
  "deviceKey": "esp32-a",
  "name": "ESP32 Sala",
  "description": "Dispositivo de pruebas"
}
```

## Render y produccion

El archivo `.env` no se sube a Git porque contiene secretos locales. El archivo que si se versiona es `.env.copy`, y sirve como plantilla para saber que variables configurar.

En Render, define las variables de entorno equivalentes al `.env` desde el panel del servicio. Render tambien puede definir `PORT`; la app lo respeta automaticamente.

Valores sugeridos:

```env
APP_RUNTIME_MODE=RenderServer
MYSQL_CONNECTION_STRING=Server=host;Port=3306;Database=db;User=user;Password=password;
MYSQL_SERVER_VERSION=8.0.11-TiDB-v8.5.3-serverless
```

Cuando el repositorio se conecte a Render con auto deploy habilitado, cada push a la rama configurada publicara una nueva version. Para cambiar valores de produccion no edites `.env`; modifica las Environment Variables en Render y redeploya el servicio si Render no lo reinicia automaticamente.
