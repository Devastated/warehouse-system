using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

// Сервис отчётности: не хранит свои данные, а собирает их по HTTP
// из products / inventory / movements. Это пятый микросервис системы.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5004");

string productsUrl = builder.Configuration["PRODUCTS_URL"] ?? "http://products:5001/";
string inventoryUrl = builder.Configuration["INVENTORY_URL"] ?? "http://inventory:5002/";
string movementsUrl = builder.Configuration["MOVEMENTS_URL"] ?? "http://movements:5003/";
int lowStockThreshold = int.TryParse(builder.Configuration["LOW_STOCK"], out var parsed) ? parsed : 5;

builder.Services.AddHttpClient("products", c =>
{
    c.BaseAddress = new Uri(productsUrl);
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("inventory", c =>
{
    c.BaseAddress = new Uri(inventoryUrl);
    c.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHttpClient("movements", c =>
{
    c.BaseAddress = new Uri(movementsUrl);
    c.Timeout = TimeSpan.FromSeconds(5);
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "reports",
    time = DateTime.UtcNow
}));

// JSON для веб-пульта (KPI и таблицы).
app.MapGet("/summary", async (IHttpClientFactory http) =>
{
    var snap = await LoadData(http, lowStockThreshold);
    return Results.Ok(BuildSummary(snap, lowStockThreshold));
});

// Готовый «срез» остатков: склад + товар + количество + состояние.
app.MapGet("/stock", async (IHttpClientFactory http) =>
{
    var snap = await LoadData(http, lowStockThreshold);
    return Results.Ok(snap.Stock);
});

// Текстовый отчёт — то, что система ВЫВОДИТ «человеческим языком».
// Открывается прямо в браузере: http://localhost:8080/api/reports/text
app.MapGet("/text", async (IHttpClientFactory http) =>
{
    var snap = await LoadData(http, lowStockThreshold);
    return Results.Text(BuildReport(snap, lowStockThreshold), "text/plain", Encoding.UTF8);
});

app.LogStartup("http://0.0.0.0:5004");

app.Run();

// ---------------------------------------------------------------------------
// Сборка данных из трёх сервисов. Если какой-то сервис недоступен, отчёт
// всё равно формируется из того, что удалось получить (не падает целиком).
// ---------------------------------------------------------------------------
static async Task<Snapshot> LoadData(IHttpClientFactory http, int threshold)
{
    var warnings = new List<string>();

    var products = await Get<List<ProductRow>>(http, "products", "products");
    if (products is null) { products = new(); warnings.Add("Справочник товаров недоступен"); }

    var warehouses = await Get<List<WarehouseRow>>(http, "inventory", "warehouses");
    if (warehouses is null) { warehouses = new(); warnings.Add("Сервис складов недоступен"); }

    var movements = await Get<List<MovementRow>>(http, "movements", "movements");
    if (movements is null) { movements = new(); warnings.Add("История операций недоступна"); }

    var productMap = products.ToDictionary(p => p.Id);
    var stock = new List<StockRow>();

    foreach (var warehouse in warehouses)
    {
        var response = await Get<StockResponse>(http, "inventory", $"warehouses/{warehouse.Id}/stock");
        if (response?.Items is null)
        {
            warnings.Add($"Остатки склада «{warehouse.Name}» недоступны");
            continue;
        }

        foreach (var item in response.Items)
        {
            productMap.TryGetValue(item.ProductId, out var product);
            stock.Add(new StockRow(
                warehouse.Id,
                warehouse.Name,
                warehouse.Location,
                item.ProductId,
                product?.Sku ?? $"#{item.ProductId}",
                product?.Name ?? "неизвестный товар",
                product?.Unit ?? "шт",
                item.Quantity,
                StateOf(item.Quantity, threshold)));
        }
    }

    return new Snapshot(products, warehouses, movements, stock, warnings);
}

static async Task<T?> Get<T>(IHttpClientFactory http, string client, string path) where T : class
{
    try
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var response = await http.CreateClient(client).GetAsync(path);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<T>(options)
            : null;
    }
    catch
    {
        return null;
    }
}

static string StateOf(int quantity, int threshold) =>
    quantity <= 0 ? "НЕТ" : quantity <= threshold ? "МАЛО" : "НОРМА";

static object BuildSummary(Snapshot snap, int threshold)
{
    var stock = snap.Stock.Select(s => s with { State = StateOf(s.Quantity, threshold) }).ToList();
    var low = stock.Where(s => s.State != "НОРМА")
        .OrderBy(s => s.Quantity)
        .ToList();

    return new
    {
        generatedAt = DateTime.UtcNow,
        products = snap.Products.Count,
        warehouses = snap.Warehouses.Count,
        stockLines = stock.Count,
        totalUnits = stock.Sum(s => s.Quantity),
        incoming = snap.Movements.Count(m => m.Type == "IN"),
        outgoing = snap.Movements.Count(m => m.Type == "OUT"),
        lowStockCount = low.Count,
        lowStockThreshold = threshold,
        warnings = snap.Warnings,
        stock,
        lowStock = low,
        recentMovements = snap.Movements
            .OrderByDescending(m => m.Id)
            .Take(10)
            .Select(m => new
            {
                m.Id,
                m.Type,
                m.WarehouseId,
                m.ProductId,
                m.ProductSku,
                m.ProductName,
                m.Quantity,
                m.Balance,
                m.CreatedAt
            })
    };
}

static string BuildReport(Snapshot snap, int threshold)
{
    var stock = snap.Stock.Select(s => s with { State = StateOf(s.Quantity, threshold) }).ToList();
    var low = stock.Where(s => s.State != "НОРМА").OrderBy(s => s.Quantity).ToList();
    var line = new string('=', 64);
    var sb = new StringBuilder();

    sb.AppendLine(line);
    sb.AppendLine("  ОТЧЁТ ПО СКЛАДУ  /  УЧЁТ СКЛАДСКИХ ТОВАРОВ");
    sb.AppendLine($"  сформирован: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine(line);
    sb.AppendLine();
    sb.AppendLine($"  Товаров в справочнике ......... {snap.Products.Count,5}");
    sb.AppendLine($"  Складов ....................... {snap.Warehouses.Count,5}");
    sb.AppendLine($"  Строк остатков ................ {stock.Count,5}");
    sb.AppendLine($"  Общее количество на складах ... {stock.Sum(s => s.Quantity),5} шт");
    sb.AppendLine($"  Операций прихода .............. {snap.Movements.Count(m => m.Type == "IN"),5}");
    sb.AppendLine($"  Операций расхода .............. {snap.Movements.Count(m => m.Type == "OUT"),5}");
    sb.AppendLine($"  Позиций с низким остатком ..... {low.Count,5} (порог {threshold} шт)");
    sb.AppendLine();

    sb.AppendLine("--- ОСТАТКИ ПО СКЛАДАМ -------------------------------------");
    if (stock.Count == 0)
    {
        sb.AppendLine("  Данных нет. Нажмите «Заполнить демо» в веб-пульте");
        sb.AppendLine("  или выполните docs/curl-examples.sh.");
    }
    else
    {
        foreach (var group in stock.GroupBy(s => s.WarehouseId))
        {
            var first = group.First();
            sb.AppendLine($"  {first.WarehouseName} ({first.Location})");
            foreach (var s in group.OrderBy(s => s.Sku))
            {
                sb.AppendLine($"    {s.Sku,-12} {s.Name,-28} {s.Quantity,5} {s.Unit,-3} [{s.State}]");
            }
            sb.AppendLine();
        }
    }

    sb.AppendLine("--- ПОСЛЕДНИЕ ОПЕРАЦИИ -------------------------------------");
    var recent = snap.Movements.OrderByDescending(m => m.Id).Take(10).ToList();
    if (recent.Count == 0)
    {
        sb.AppendLine("  Операций пока нет.");
    }
    else
    {
        foreach (var m in recent)
        {
            var sign = m.Type == "IN" ? "+" : "-";
            sb.AppendLine($"  #{m.Id,-4} {m.CreatedAt:yyyy-MM-dd HH:mm}  {m.Type,-3}  " +
                          $"склад {m.WarehouseId} / {m.ProductSku,-10} " +
                          $"{sign}{m.Quantity} -> остаток {m.Balance}");
        }
    }
    sb.AppendLine();

    sb.AppendLine("--- ТРЕБУЕТ ВНИМАНИЯ ---------------------------------------");
    if (low.Count == 0)
    {
        sb.AppendLine("  Критических остатков нет.");
    }
    else
    {
        foreach (var s in low)
            sb.AppendLine($"  [{s.State}] {s.WarehouseName} / {s.Sku} — {s.Quantity} {s.Unit}");
    }

    if (snap.Warnings.Count > 0)
    {
        sb.AppendLine();
        sb.AppendLine("--- ПРЕДУПРЕЖДЕНИЯ ------------------------------------------");
        foreach (var warning in snap.Warnings)
            sb.AppendLine($"  ! {warning}");
    }

    sb.AppendLine();
    sb.AppendLine(line);
    sb.AppendLine("  Конец отчёта");
    return sb.ToString();
}

// Мягкий вывод в логи контейнера: видно, что сервис стартовал.
static class StartupLogExtensions
{
    public static void LogStartup(this WebApplication app, string url)
    {
        Console.WriteLine();
        Console.WriteLine("  +----------------------------------------------------+");
        Console.WriteLine("  |  WAREHOUSE :: REPORT SERVICE                       |");
        Console.WriteLine("  |  JSON  /summary  /stock  |  TEXT  /text           |");
        Console.WriteLine($"  |  {url,-52} |");
        Console.WriteLine("  +----------------------------------------------------+");
        Console.WriteLine();
    }
}

record ProductRow(int Id, string Sku, string Name, string Unit);
record WarehouseRow(int Id, string Name, string Location);
record StockItem(int ProductId, int Quantity);
record StockResponse(int WarehouseId, List<StockItem>? Items);
record MovementRow(
    int Id,
    string Type,
    int WarehouseId,
    int ProductId,
    int Quantity,
    int Balance,
    string ProductSku,
    string ProductName,
    DateTime CreatedAt);
record StockRow(
    int WarehouseId,
    string WarehouseName,
    string Location,
    int ProductId,
    string Sku,
    string Name,
    string Unit,
    int Quantity,
    string State);
record Snapshot(
    List<ProductRow> Products,
    List<WarehouseRow> Warehouses,
    List<MovementRow> Movements,
    List<StockRow> Stock,
    List<string> Warnings);
