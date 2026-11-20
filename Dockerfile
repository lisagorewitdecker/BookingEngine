# Build stage
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src

COPY ["BookingEngine.slnx", "BookingEngine.slnx"]
COPY ["src/BookingEngine.Api/BookingEngine.Api.csproj", "src/BookingEngine.Api/"]
COPY ["src/BookingEngine.ServiceDefaults/BookingEngine.ServiceDefaults.csproj", "src/BookingEngine.ServiceDefaults/"]
COPY ["src/BookingEngine.Worker/BookingEngine.Worker.csproj", "src/BookingEngine.Worker/"]
COPY ["src/BookingEngine.AppHost/BookingEngine.AppHost.csproj", "src/BookingEngine.AppHost/"]

RUN dotnet restore "src/BookingEngine.Api/BookingEngine.Api.csproj"

COPY . .
WORKDIR "/src/src/BookingEngine.Api"
RUN dotnet build "BookingEngine.Api.csproj" -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish "BookingEngine.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y \
    curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "BookingEngine.Api.dll"]
