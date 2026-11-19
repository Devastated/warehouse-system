using System.Net;
using System.Net.Http.Json;
using Npgsql;
using NpgsqlTypes;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5003");

// Строка подключения из environment-переменной DB (не хардкод).
var connStr = builder.Configuration["DB"]
    ?? "Host=db;Port=5432;Database=app;Username=postgres;Password=1234";
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connStr).Build());

// Адреса соседних сервисов — из environment-переменных (не хардкод).
var productsUrl = builder.Configuration["PRODUCTS_URL"] ?? "http://products:5001/";
var inventoryUrl = builder.Configuration["INVENTORY_URL"] ?? "http://inventory:5002/";
builder.Services.AddHttpClient("products", c =>
{
    c.BaseAddress = new Uri(productsUrl);
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("inventory", c =>
{
    c.BaseAddress = new Uri(inventoryUrl);
    c.Timeout = TimeSpan.FromSeconds(10);
});

var app = builder.Build();
var ds = app.Services.GetRequiredService<NpgsqlDataSource>();

await InitDb(ds);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "movements", time = DateTime.UtcNow }));

// Сценарий прихода/расхода: валидирует товар, меняет остаток, пишет историю.
app.MapPost("/movements", async (HttpRequest req, IHttpClientFactory http) =>
{
    var body = await req.ReadFromJsonAsync<MovementDto>();
    if (body is null || body.warehouseId <= 0 || body.productId <= 0 || body.quantity <= 0)
        return Results.BadRequest(new { error = "warehouseId>0, productId>0 and quantity>0 are required" });

    var type = (body.type ?? "").Trim().ToUpperInvariant();
    if (type is not ("IN" or "OUT"))
        return Results.BadRequest(new { error = "type must be 'IN' (приход) or 'OUT' (расход)" });

    // Шаг 1. Проверяем, что товар существует — HTTP-запрос в Products Service.
    var products = http.CreateClient("products");
    using var productResp = await products.GetAsync($"products/{body.productId}");
    if (productResp.StatusCode == HttpStatusCode.NotFound)
        return Results.BadRequest(new { error = $"product {body.productId} does not exist" });
    productResp.EnsureSuccessStatusCode();
    var product = (await productResp.Content.ReadFromJsonAsync<ProductInfo>())!;

    // Шаг 2. Применяем изменение остатка — HTTP-запрос в Inventory Service.
    var delta = type == "IN" ? body.quantity : -body.quantity;
    var inventory = http.CreateClient("inventory");
    using var adjustResp = await inventory.PostAsJsonAsync("inventory/adjust",
        new { warehouseId = body.warehouseId, productId = body.productId, delta });
    if (!adjustResp.IsSuccessStatusCode)
    {
        var detail = await adjustResp.Content.ReadAsStringAsync();
        return Results.BadRequest(new
        {
            error = $"inventory service rejected operation (например, нехватка остатка): {detail}"
        });
    }
    var stock = (await adjustResp.Content.ReadFromJsonAsync<StockInfo>())!;

    // Шаг 3. Сохраняем операцию в собственной таблице (снимок остатка после операции).
    await using var cmd = ds.CreateCommand("""
        INSERT INTO movements.movements
            (type, warehouse_id, product_id, quantity, balance, product_sku, product_name)
        VALUES ($1, $2, $3, $4, $5, $6, $7)
        RETURNING id, created_at
        """);
    cmd.Parameters.AddWithValue(type);
    cmd.Parameters.AddWithValue(body.warehouseId);
    cmd.Parameters.AddWithValue(body.productId);
    cmd.Parameters.AddWithValue(body.quantity);
    cmd.Parameters.AddWithValue(stock.quantity);
    cmd.Parameters.AddWithValue(product.sku);
    cmd.Parameters.AddWithValue(product.name);
    await using var r = await cmd.ExecuteReaderAsync();
    await r.ReadAsync();
    var id = r.GetInt32(0);

    return Results.Created($"/movements/{id}", new
    {
        id,
        type,
        warehouseId = body.warehouseId,
        productId = body.productId,
        productSku = product.sku,
        productName = product.name,
        quantity = body.quantity,
        balance = stock.quantity,
        createdAt = r.GetDateTime(1)
    });
});

// История операций с фильтрами (оба необязательны).
app.MapGet("/movements", async (int? warehouseId, int? productId) =>
{
    var list = new List<object>();
    await using var cmd = ds.CreateCommand("""
        SELECT id, type, warehouse_id, product_id, quantity, balance, product_sku, product_name, created_at
        FROM movements.movements
        WHERE warehouse_id = COALESCE(@warehouseId, warehouse_id)
          AND product_id = COALESCE(@productId, product_id)
        ORDER BY id
        """);
    cmd.Parameters.Add(new NpgsqlParameter("warehouseId", NpgsqlDbType.Integer)
        { Value = (object?)warehouseId ?? DBNull.Value });
    cmd.Parameters.Add(new NpgsqlParameter("productId", NpgsqlDbType.Integer)
        { Value = (object?)productId ?? DBNull.Value });
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        list.Add(new
        {
            id = r.GetInt32(0),
            type = r.GetString(1),
            warehouseId = r.GetInt32(2),
            productId = r.GetInt32(3),
            quantity = r.GetInt32(4),
            balance = r.GetInt32(5),
            productSku = r.GetString(6),
            productName = r.GetString(7),
            createdAt = r.GetDateTime(8)
        });
    return Results.Ok(list);
});

// Одна операция по id.
app.MapGet("/movements/{id:int}", async (int id) =>
{
    await using var cmd = ds.CreateCommand("""
        SELECT id, type, warehouse_id, product_id, quantity, balance, product_sku, product_name, created_at
        FROM movements.movements WHERE id = $1
        """);
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync())
        return Results.NotFound(new { error = $"movement {id} not found" });
    return Results.Ok(new
    {
        id = r.GetInt32(0),
        type = r.GetString(1),
        warehouseId = r.GetInt32(2),
        productId = r.GetInt32(3),
        quantity = r.GetInt32(4),
        balance = r.GetInt32(5),
        productSku = r.GetString(6),
        productName = r.GetString(7),
        createdAt = r.GetDateTime(8)
    });
});

app.Run();

// Инициализация схемы с повторными попытками (ждем подъем PostgreSQL).
static async Task InitDb(NpgsqlDataSource ds)
{
    const string sql = """
        CREATE SCHEMA IF NOT EXISTS movements;
        CREATE TABLE IF NOT EXISTS movements.movements (
            id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            type TEXT NOT NULL CHECK (type IN ('IN','OUT')),
            warehouse_id INT NOT NULL,
            product_id INT NOT NULL,
            quantity INT NOT NULL CHECK (quantity > 0),
            balance INT NOT NULL,
            product_sku TEXT NOT NULL,
            product_name TEXT NOT NULL,
            created_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        """;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var cmd = ds.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
            return;
        }
        catch (Exception) when (attempt < 30)
        {
            await Task.Delay(2000);
        }
    }
}

record MovementDto(string? type, int warehouseId, int productId, int quantity);
record ProductInfo(int id, string sku, string name, string unit, DateTime createdAt);
record StockInfo(int warehouseId, int productId, int quantity);
