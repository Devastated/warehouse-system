# ---------- Сборка ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Сначала только .csproj — слой restore кэшируется, пока не менялись зависимости
COPY *.csproj ./
RUN dotnet restore

# Теперь весь код и публикация
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# ---------- Запуск ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5002
ENTRYPOINT ["dotnet", "InventoryService.dll"]
