# 1. Capa de ejecución (Runtime)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# 2. Capa de compilación (SDK)
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copiar archivos de proyecto y restaurar dependencias
COPY ["PruebaSocket/PruebaSocket.csproj", "PruebaSocket/"]
RUN dotnet restore "PruebaSocket/PruebaSocket.csproj"

# Copiar todo lo demás y compilar
COPY . .
WORKDIR "/src/PruebaSocket"
RUN dotnet build "PruebaSocket.csproj" -c Release -o /app/build

# 3. Capa de publicación
FROM build AS publish
RUN dotnet publish "PruebaSocket.csproj" -c Release -o /app/publish /p:UseAppHost=false

# 4. Configurar el contenedor final
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PruebaSocket.dll"]