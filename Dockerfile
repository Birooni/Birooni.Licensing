# Multi-stage Dockerfile optimized for ASP.NET Core 8 Web API on Render
# Stage 1: Build & Publish
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files and restore dependencies (layer caching)
COPY ["Birooni.Licensing.Api/Birooni.Licensing.Api.csproj", "Birooni.Licensing.Api/"]
COPY ["Birooni.Client/Birooni.Client.csproj", "Birooni.Client/"]
RUN dotnet restore "Birooni.Licensing.Api/Birooni.Licensing.Api.csproj"

# Copy source code and build
COPY . .
WORKDIR "/src/Birooni.Licensing.Api"
RUN dotnet build "Birooni.Licensing.Api.csproj" -c Release -o /app/build
RUN dotnet publish "Birooni.Licensing.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Production Runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Expose default HTTP port
EXPOSE 8080

# Environment defaults for production on Render
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Copy published binaries
COPY --from=build /app/publish .

# Render dynamically passes the $PORT environment variable (defaults to 8080 if unset)
ENTRYPOINT ["sh", "-c", "dotnet Birooni.Licensing.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
