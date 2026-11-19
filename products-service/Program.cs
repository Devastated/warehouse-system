using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:5001");

// Строка подключения НЕ хардкодится: берется из environment-переменной DB.
var connStr = builder.Configuration["DB"]
    ?? "Host=db;Port=5432;Database=app;Username=postgres;Password=1234";
builder.Services.AddSingleton(new NpgsqlDataSourceBuilder(connStr).Build());

var app = builder.Build();
var ds = app.Services.GetRequiredService<NpgsqlDataSource>();

// Ждем, пока PostgreSQL поднимется, и создаем свою схему/таблицы.
await InitDb(ds);

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "products", time = DateTime.UtcNow }));

// Список всех товаров
app.MapGet("/products", async () =>
{
    var list = new List<object>();
    await using var cmd = ds.CreateCommand(
        "SELECT id, sku, name, unit, created_at FROM products.products ORDER BY id");
    await using var r = await cmd.ExecuteReaderAsync();
    while (await r.ReadAsync())
        list.Add(new
        {
            id = r.GetInt32(0),
            sku = r.GetString(1),
            name = r.GetString(2),
            unit = r.GetString(3),
            createdAt = r.GetDateTime(4)
        });
    return Results.Ok(list);
});

// Создать товар
app.MapPost("/products", async (HttpRequest req) =>
{
    var body = await req.ReadFromJsonAsync<ProductDto>();
    if (body is null || string.IsNullOrWhiteSpace(body.sku) || string.IsNullOrWhiteSpace(body.name))
        return Results.BadRequest(new { error = "sku and name are required" });

    try
    {
        await using var cmd = ds.CreateCommand(
            "INSERT INTO products.products (sku, name, unit) VALUES ($1, $2, $3) RETURNING id, created_at");
        cmd.Parameters.AddWithValue(body.sku.Trim());
        cmd.Parameters.AddWithValue(body.name.Trim());
        cmd.Parameters.AddWithValue(string.IsNullOrWhiteSpace(body.unit) ? "шт" : body.unit.Trim());
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        var id = r.GetInt32(0);
        return Results.Created($"/products/{id}", new
        {
            id,
            sku = body.sku,
            name = body.name,
            unit = body.unit,
            createdAt = r.GetDateTime(1)
        });
    }
    catch (PostgresException e) when (e.SqlState == "23505") // unique violation
    {
        return Results.Conflict(new { error = $"SKU '{body.sku}' already exists" });
    }
});

// Получить товар по id
app.MapGet("/products/{id:int}", async (int id) =>
{
    await using var cmd = ds.CreateCommand(
        "SELECT id, sku, name, unit, created_at FROM products.products WHERE id = $1");
    cmd.Parameters.AddWithValue(id);
    await using var r = await cmd.ExecuteReaderAsync();
    if (!await r.ReadAsync())
        return Results.NotFound(new { error = $"product {id} not found" });
    return Results.Ok(new
    {
        id = r.GetInt32(0),
        sku = r.GetString(1),
        name = r.GetString(2),
        unit = r.GetString(3),
        createdAt = r.GetDateTime(4)
    });
});

app.Run();

// Локальная функция: инициализация схемы с повторными попытками (ждем подъем БД).
static async Task InitDb(NpgsqlDataSource ds)
{
    const string sql = """
        CREATE SCHEMA IF NOT EXISTS products;
        CREATE TABLE IF NOT EXISTS products.products (
            id INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            sku TEXT NOT NULL UNIQUE,
            name TEXT NOT NULL,
            unit TEXT NOT NULL DEFAULT 'шт',
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

record ProductDto(string? sku, string? name, string? unit);
