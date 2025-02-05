FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8082

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["RateLimiters.Endpoints/RateLimiters.Endpoints.csproj", "RateLimiters.Endpoints/"]
COPY ["RateLimiters/RateLimiters.csproj", "RateLimiters/"]
RUN dotnet restore "RateLimiters.Endpoints/RateLimiters.Endpoints.csproj"
COPY . .
WORKDIR "/src/RateLimiters.Endpoints"
RUN dotnet build "RateLimiters.Endpoints.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "RateLimiters.Endpoints.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RateLimiters.Endpoints.dll"]
