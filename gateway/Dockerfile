# ---------- Сборка ----------
# Контекст сборки — корень проекта (см. docker-compose.yml), потому что
# вместе со шлюзом в образ попадает статический веб-пульт из web/.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY gateway/*.csproj ./gateway/
RUN dotnet restore gateway/Warehouse.Gateway.csproj

COPY gateway/ ./gateway/
COPY web/ ./web/
RUN dotnet publish gateway/Warehouse.Gateway.csproj -c Release -o /app --no-restore

# ---------- Запуск ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# Статические файлы веб-пульта они не входят в publish-output проекта,
# поэтому копируем их отдельно в стандартную папку wwwroot.
COPY --from=build /src/web /app/wwwroot
EXPOSE 8080
ENTRYPOINT ["dotnet", "Warehouse.Gateway.dll"]
