using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
ConfigureObservability(builder);
ConfigureAuthentication(builder);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient<IMercadoPagoGateway, MercadoPagoGateway>();
builder.Services.AddSingleton<IBillingStore, PostgresBillingStore>();
builder.Services.AddSingleton<IEventBus, RabbitMqEventBus>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();

static void ConfigureObservability(WebApplicationBuilder builder)
{
    var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    if (string.IsNullOrWhiteSpace(endpoint)) return;
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("garage-management-billing-service"))
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter(options => options.Endpoint = new Uri(endpoint)))
        .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter(options => options.Endpoint = new Uri(endpoint)));
}

static void ConfigureAuthentication(WebApplicationBuilder builder)
{
    var enabled = builder.Configuration.GetValue<bool>("ExternalJwt:Enabled");
    var authentication = builder.Services.AddAuthentication();
    builder.Services.AddAuthorization(options =>
    {
        if (enabled)
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
    });
    if (!enabled) return;
    var publicKey = builder.Configuration["ExternalJwt:PublicKeyPem"]
        ?? throw new InvalidOperationException("ExternalJwt:PublicKeyPem não foi configurada.");
    using var rsa = RSA.Create();
    rsa.ImportFromPem(publicKey);
    authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["ExternalJwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = builder.Configuration["ExternalJwt:Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
}

app.MapPost("/quotes", async (
    CreateQuoteRequest request,
    IBillingStore store,
    CancellationToken cancellationToken) =>
{
    if (request.ServiceOrderId == Guid.Empty || request.Amount <= 0)
        return Results.BadRequest(new { error = "serviceOrderId e amount válido são obrigatórios." });
    var quote = await store.CreateQuoteAsync(request, cancellationToken);
    return Results.Created($"/quotes/{quote.QuoteId}", quote);
}).WithOpenApi();

app.MapPost("/catalog/services", async (CatalogServiceRequest request, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    var service = new CatalogServiceResponse(Guid.NewGuid(), request.Description, request.Price, request.EstimatedTime);
    await store.SaveCatalogServiceAsync(service, cancellationToken);
    return Results.Created($"/catalog/services/{service.ServiceId}", service);
}).WithOpenApi();

app.MapPost("/catalog/products", async (CatalogProductRequest request, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    var product = new CatalogProductResponse(Guid.NewGuid(), request.Description, request.Price);
    await store.SaveCatalogProductAsync(product, request.InitialQuantity, cancellationToken);
    return Results.Created($"/catalog/products/{product.ProductId}", product);
}).WithOpenApi();

app.MapPost("/inventory/{productId:guid}/reserve", async (Guid productId, InventoryQuantityRequest request, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    await store.ReserveStockAsync(productId, request.Quantity, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapGet("/catalog/services", async (PostgresBillingStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListCatalogServicesAsync(cancellationToken))).WithOpenApi();
app.MapGet("/catalog/services/{id:guid}", async (Guid id, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    var item = await store.GetCatalogServiceAsync(id, cancellationToken);
    return item is null ? Results.NotFound() : Results.Ok(item);
}).WithOpenApi();
app.MapPut("/catalog/services/{id:guid}", async (Guid id, CatalogServiceRequest request, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    await store.UpdateCatalogServiceAsync(id, request, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();
app.MapDelete("/catalog/services/{id:guid}", async (Guid id, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    await store.DeleteCatalogServiceAsync(id, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();
app.MapGet("/catalog/products", async (PostgresBillingStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListCatalogProductsAsync(cancellationToken))).WithOpenApi();
app.MapGet("/catalog/products/{id:guid}", async (Guid id, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    var item = await store.GetCatalogProductAsync(id, cancellationToken);
    return item is null ? Results.NotFound() : Results.Ok(item);
}).WithOpenApi();
app.MapPut("/catalog/products/{id:guid}", async (Guid id, CatalogProductUpdateRequest request, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    await store.UpdateCatalogProductAsync(id, request, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();
app.MapDelete("/catalog/products/{id:guid}", async (Guid id, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    await store.DeleteCatalogProductAsync(id, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();
app.MapGet("/inventory/{productId:guid}", async (Guid productId, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    var item = await store.GetInventoryAsync(productId, cancellationToken);
    return item is null ? Results.NotFound() : Results.Ok(item);
}).WithOpenApi();
app.MapPut("/inventory/{productId:guid}", async (Guid productId, InventoryUpdateRequest request, PostgresBillingStore store, CancellationToken cancellationToken) =>
{
    await store.UpdateInventoryAsync(productId, request.Quantity, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapPost("/payments", async (
    PaymentRequest request,
    IMercadoPagoGateway gateway,
    IBillingStore store,
    IEventBus bus,
    CancellationToken cancellationToken) =>
{
    var result = await gateway.CreatePaymentAsync(request, cancellationToken);
    await store.SetPaymentStatusAsync(request.ServiceOrderId, result.Status, cancellationToken);
    await bus.PublishAsync(new BillingEvent(
        result.Status == "approved" ? "PaymentApproved" : "PaymentRejected",
        Guid.NewGuid(), request.CorrelationId, request.ServiceOrderId), cancellationToken);
    return result.Status == "approved" ? Results.Ok(result) : Results.UnprocessableEntity(result);
}).WithOpenApi();

app.MapPost("/quotes/{serviceOrderId:guid}/approve", async (
    Guid serviceOrderId,
    ApproveQuoteRequest request,
    IBillingStore store,
    IEventBus bus,
    CancellationToken cancellationToken) =>
{
    await store.ApproveQuoteAsync(serviceOrderId, cancellationToken);
    await bus.PublishAsync(new BillingEvent("QuoteApproved", Guid.NewGuid(), request.CorrelationId, serviceOrderId), cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapPost("/quotes/{serviceOrderId:guid}/reject", async (
    Guid serviceOrderId,
    RejectQuoteRequest request,
    IBillingStore store,
    IEventBus bus,
    CancellationToken cancellationToken) =>
{
    await store.RejectQuoteAsync(serviceOrderId, request.Reason, cancellationToken);
    await bus.PublishAsync(new BillingEvent("QuoteRejected", Guid.NewGuid(), request.CorrelationId, serviceOrderId), cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.Run();

public sealed record CreateQuoteRequest(
    Guid ServiceOrderId,
    decimal Amount,
    Guid CorrelationId,
    string? EstimateNumber = null,
    Guid? CustomerId = null,
    Guid? VehicleId = null,
    IReadOnlyList<QuoteServiceItemRequest>? ServiceItems = null,
    IReadOnlyList<QuoteProductItemRequest>? PartItems = null);
public sealed record QuoteServiceItemRequest(Guid ServiceId, int Quantity);
public sealed record QuoteProductItemRequest(Guid ProductId, int Quantity);
public sealed record CatalogServiceRequest(string Description, decimal Price, double EstimatedTime);
public sealed record CatalogServiceResponse(Guid ServiceId, string Description, decimal Price, double EstimatedTime);
public sealed record CatalogProductRequest(string Description, decimal Price, int InitialQuantity);
public sealed record CatalogProductResponse(Guid ProductId, string Description, decimal Price);
public sealed record CatalogProductUpdateRequest(string Description, decimal Price);
public sealed record InventoryResponse(Guid ProductId, int Quantity, int ReservedQuantity);
public sealed record InventoryUpdateRequest(int Quantity);
public sealed record InventoryQuantityRequest(int Quantity);
public sealed record QuoteResponse(Guid QuoteId, Guid ServiceOrderId, decimal Amount, string Status);
public sealed record PaymentRequest(Guid ServiceOrderId, decimal Amount, string Token, Guid CorrelationId);
public sealed record PaymentResponse(Guid ServiceOrderId, string Status, string? MercadoPagoId);
public sealed record ApproveQuoteRequest(Guid CorrelationId);
public sealed record RejectQuoteRequest(string Reason, Guid CorrelationId);
public sealed record BillingEvent(string EventType, Guid EventId, Guid CorrelationId, Guid ServiceOrderId);

public interface IBillingStore
{
    Task SaveAsync(QuoteResponse quote, Guid correlationId, CancellationToken cancellationToken);
    Task<QuoteResponse> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken cancellationToken);
    Task ApproveQuoteAsync(Guid serviceOrderId, CancellationToken cancellationToken);
    Task RejectQuoteAsync(Guid serviceOrderId, string reason, CancellationToken cancellationToken);
    Task SaveCatalogServiceAsync(CatalogServiceResponse service, CancellationToken cancellationToken);
    Task SaveCatalogProductAsync(CatalogProductResponse product, int initialQuantity, CancellationToken cancellationToken);
    Task ReserveStockAsync(Guid productId, int quantity, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogServiceResponse>> ListCatalogServicesAsync(CancellationToken cancellationToken);
    Task<CatalogServiceResponse?> GetCatalogServiceAsync(Guid id, CancellationToken cancellationToken);
    Task UpdateCatalogServiceAsync(Guid id, CatalogServiceRequest request, CancellationToken cancellationToken);
    Task DeleteCatalogServiceAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<CatalogProductResponse>> ListCatalogProductsAsync(CancellationToken cancellationToken);
    Task<CatalogProductResponse?> GetCatalogProductAsync(Guid id, CancellationToken cancellationToken);
    Task UpdateCatalogProductAsync(Guid id, CatalogProductUpdateRequest request, CancellationToken cancellationToken);
    Task DeleteCatalogProductAsync(Guid id, CancellationToken cancellationToken);
    Task<InventoryResponse?> GetInventoryAsync(Guid productId, CancellationToken cancellationToken);
    Task UpdateInventoryAsync(Guid productId, int quantity, CancellationToken cancellationToken);
    Task SetPaymentStatusAsync(Guid serviceOrderId, string status, CancellationToken cancellationToken);
}

public sealed class PostgresBillingStore(IConfiguration configuration) : IBillingStore
{
    private readonly string _connectionString = configuration.GetConnectionString("Billing")
        ?? throw new InvalidOperationException("ConnectionStrings:Billing não foi configurada.");

    public async Task SaveAsync(QuoteResponse quote, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS quotes (
                id uuid PRIMARY KEY, service_order_id uuid NOT NULL, amount numeric(18,2) NOT NULL,
                status varchar(40) NOT NULL, correlation_id uuid NOT NULL
            );
            CREATE TABLE IF NOT EXISTS catalog_services (
                id uuid PRIMARY KEY, description varchar(300) NOT NULL, price numeric(18,2) NOT NULL,
                estimated_time double precision NOT NULL
            );
            CREATE TABLE IF NOT EXISTS catalog_products (
                id uuid PRIMARY KEY, description varchar(300) NOT NULL, price numeric(18,2) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS inventory (
                product_id uuid PRIMARY KEY REFERENCES catalog_products(id),
                quantity int NOT NULL, reserved_quantity int NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS quote_items (
                id uuid PRIMARY KEY, quote_id uuid NOT NULL,
                item_type varchar(20) NOT NULL, item_id uuid NOT NULL, quantity int NOT NULL,
                unit_price numeric(18,2) NOT NULL, total_price numeric(18,2) NOT NULL
            );
            INSERT INTO quotes VALUES (@id, @order, @amount, @status, @correlation)
            ON CONFLICT (id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("id", quote.QuoteId);
        command.Parameters.AddWithValue("order", quote.ServiceOrderId);
        command.Parameters.AddWithValue("amount", quote.Amount);
        command.Parameters.AddWithValue("status", quote.Status);
        command.Parameters.AddWithValue("correlation", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<QuoteResponse> CreateQuoteAsync(CreateQuoteRequest request, CancellationToken cancellationToken)
    {
        var quoteId = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS quotes (
                id uuid PRIMARY KEY, service_order_id uuid NOT NULL, amount numeric(18,2) NOT NULL,
                status varchar(40) NOT NULL, correlation_id uuid NOT NULL
            );
            CREATE TABLE IF NOT EXISTS catalog_services (
                id uuid PRIMARY KEY, description varchar(300) NOT NULL, price numeric(18,2) NOT NULL, estimated_time double precision NOT NULL
            );
            CREATE TABLE IF NOT EXISTS catalog_products (
                id uuid PRIMARY KEY, description varchar(300) NOT NULL, price numeric(18,2) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS inventory (
                product_id uuid PRIMARY KEY REFERENCES catalog_products(id), quantity int NOT NULL, reserved_quantity int NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS quote_items (
                id uuid PRIMARY KEY, quote_id uuid NOT NULL, item_type varchar(20) NOT NULL,
                item_id uuid NOT NULL, quantity int NOT NULL, unit_price numeric(18,2) NOT NULL, total_price numeric(18,2) NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var total = 0m;
        var serviceItems = request.ServiceItems ?? Array.Empty<QuoteServiceItemRequest>();
        foreach (var item in serviceItems)
        {
            if (item.Quantity <= 0) throw new InvalidOperationException("Quantidade de serviço inválida.");
            await using var itemCommand = connection.CreateCommand();
            itemCommand.Transaction = transaction;
            itemCommand.CommandText = "SELECT price FROM catalog_services WHERE id=@id";
            itemCommand.Parameters.AddWithValue("id", item.ServiceId);
            var value = await itemCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Serviço não encontrado.");
            var unitPrice = Convert.ToDecimal(value);
            total += unitPrice * item.Quantity;
            await InsertQuoteItemAsync(connection, transaction, quoteId, "service", item.ServiceId, item.Quantity, unitPrice, cancellationToken);
        }
        foreach (var item in request.PartItems ?? Array.Empty<QuoteProductItemRequest>())
        {
            if (item.Quantity <= 0) throw new InvalidOperationException("Quantidade de peça inválida.");
            await using var itemCommand = connection.CreateCommand();
            itemCommand.Transaction = transaction;
            itemCommand.CommandText = "SELECT p.price, i.quantity - i.reserved_quantity FROM catalog_products p JOIN inventory i ON i.product_id=p.id WHERE p.id=@id";
            itemCommand.Parameters.AddWithValue("id", item.ProductId);
            await using var reader = await itemCommand.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Produto não encontrado ou sem estoque.");
            var unitPrice = reader.GetDecimal(0);
            var available = reader.GetInt32(1);
            if (available < item.Quantity) throw new InvalidOperationException("Estoque insuficiente.");
            total += unitPrice * item.Quantity;
            await reader.DisposeAsync();
            await InsertQuoteItemAsync(connection, transaction, quoteId, "product", item.ProductId, item.Quantity, unitPrice, cancellationToken);
            await using var reserve = connection.CreateCommand();
            reserve.Transaction = transaction;
            reserve.CommandText = "UPDATE inventory SET reserved_quantity=reserved_quantity+@quantity WHERE product_id=@id";
            reserve.Parameters.AddWithValue("quantity", item.Quantity);
            reserve.Parameters.AddWithValue("id", item.ProductId);
            await reserve.ExecuteNonQueryAsync(cancellationToken);
        }
        if (total == 0) total = request.Amount;
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO quotes(id,service_order_id,amount,status,correlation_id) VALUES (@id,@order,@amount,'PendingApproval',@correlation)";
        insert.Parameters.AddWithValue("id", quoteId);
        insert.Parameters.AddWithValue("order", request.ServiceOrderId);
        insert.Parameters.AddWithValue("amount", total);
        insert.Parameters.AddWithValue("correlation", request.CorrelationId);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new QuoteResponse(quoteId, request.ServiceOrderId, total, "PendingApproval");
    }

    private static async Task InsertQuoteItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid quoteId, string type, Guid itemId, int quantity, decimal unitPrice, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO quote_items(id,quote_id,item_type,item_id,quantity,unit_price,total_price) VALUES (@id,@quote,@type,@item,@quantity,@unit,@total)";
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("quote", quoteId);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("unit", unitPrice);
        command.Parameters.AddWithValue("total", unitPrice * quantity);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ApproveQuoteAsync(Guid serviceOrderId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE quotes SET status='approved' WHERE service_order_id=@order AND status='PendingApproval'";
        command.Parameters.AddWithValue("order", serviceOrderId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Orçamento não encontrado ou já processado.");
    }

    public async Task RejectQuoteAsync(Guid serviceOrderId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Motivo da reprovação é obrigatório.", nameof(reason));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var status = connection.CreateCommand();
        status.Transaction = transaction;
        status.CommandText = "UPDATE quotes SET status='rejected' WHERE service_order_id=@order AND status='PendingApproval'";
        status.Parameters.AddWithValue("order", serviceOrderId);
        if (await status.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Orçamento não encontrado ou já processado.");
        await using var release = connection.CreateCommand();
        release.Transaction = transaction;
        release.CommandText = """
            UPDATE inventory i SET reserved_quantity = i.reserved_quantity - q.quantity
            FROM quote_items q
            WHERE q.quote_id IN (SELECT id FROM quotes WHERE service_order_id=@order)
              AND q.item_type='product' AND i.product_id=q.item_id
            """;
        release.Parameters.AddWithValue("order", serviceOrderId);
        await release.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetPaymentStatusAsync(Guid serviceOrderId, string status, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE quotes SET status = @status WHERE service_order_id = @order";
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("order", serviceOrderId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCatalogServiceAsync(CatalogServiceResponse service, CancellationToken cancellationToken)
    {
        if (service.Price < 0 || service.EstimatedTime < 0) throw new ArgumentException("Preço e tempo não podem ser negativos.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO catalog_services(id,description,price,estimated_time) VALUES (@id,@description,@price,@time)";
        command.Parameters.AddWithValue("id", service.ServiceId);
        command.Parameters.AddWithValue("description", service.Description);
        command.Parameters.AddWithValue("price", service.Price);
        command.Parameters.AddWithValue("time", service.EstimatedTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCatalogProductAsync(CatalogProductResponse product, int initialQuantity, CancellationToken cancellationToken)
    {
        if (product.Price < 0 || initialQuantity < 0) throw new ArgumentException("Preço e estoque inválidos.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var productCommand = connection.CreateCommand();
        productCommand.Transaction = transaction;
        productCommand.CommandText = "INSERT INTO catalog_products(id,description,price) VALUES (@id,@description,@price)";
        productCommand.Parameters.AddWithValue("id", product.ProductId);
        productCommand.Parameters.AddWithValue("description", product.Description);
        productCommand.Parameters.AddWithValue("price", product.Price);
        await productCommand.ExecuteNonQueryAsync(cancellationToken);
        await using var inventory = connection.CreateCommand();
        inventory.Transaction = transaction;
        inventory.CommandText = "INSERT INTO inventory(product_id,quantity,reserved_quantity) VALUES (@id,@quantity,0)";
        inventory.Parameters.AddWithValue("id", product.ProductId);
        inventory.Parameters.AddWithValue("quantity", initialQuantity);
        await inventory.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReserveStockAsync(Guid productId, int quantity, CancellationToken cancellationToken)
    {
        if (quantity <= 0) throw new ArgumentException("Quantidade deve ser maior que zero.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE inventory SET reserved_quantity=reserved_quantity+@quantity WHERE product_id=@id AND quantity-reserved_quantity>=@quantity";
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("id", productId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            throw new InvalidOperationException("Produto não encontrado ou estoque insuficiente.");
    }

    public async Task<IReadOnlyList<CatalogServiceResponse>> ListCatalogServicesAsync(CancellationToken cancellationToken)
    {
        var result = new List<CatalogServiceResponse>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,description,price,estimated_time FROM catalog_services ORDER BY description";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new CatalogServiceResponse(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDouble(3)));
        return result;
    }

    public async Task<CatalogServiceResponse?> GetCatalogServiceAsync(Guid id, CancellationToken cancellationToken)
    {
        var items = await ListCatalogServicesAsync(cancellationToken);
        return items.FirstOrDefault(item => item.ServiceId == id);
    }

    public async Task UpdateCatalogServiceAsync(Guid id, CatalogServiceRequest request, CancellationToken cancellationToken)
    {
        if (request.Price <= 0 || request.EstimatedTime <= 0) throw new ArgumentException("Preço e tempo devem ser maiores que zero.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE catalog_services SET description=@description,price=@price,estimated_time=@time WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("description", request.Description);
        command.Parameters.AddWithValue("price", request.Price);
        command.Parameters.AddWithValue("time", request.EstimatedTime);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Serviço não encontrado.");
    }

    public async Task DeleteCatalogServiceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM catalog_services WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Serviço não encontrado.");
    }

    public async Task<IReadOnlyList<CatalogProductResponse>> ListCatalogProductsAsync(CancellationToken cancellationToken)
    {
        var result = new List<CatalogProductResponse>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,description,price FROM catalog_products ORDER BY description";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new CatalogProductResponse(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2)));
        return result;
    }

    public async Task<CatalogProductResponse?> GetCatalogProductAsync(Guid id, CancellationToken cancellationToken)
    {
        var items = await ListCatalogProductsAsync(cancellationToken);
        return items.FirstOrDefault(item => item.ProductId == id);
    }

    public async Task UpdateCatalogProductAsync(Guid id, CatalogProductUpdateRequest request, CancellationToken cancellationToken)
    {
        if (request.Price <= 0) throw new ArgumentException("Preço deve ser maior que zero.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE catalog_products SET description=@description,price=@price WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("description", request.Description);
        command.Parameters.AddWithValue("price", request.Price);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Produto não encontrado.");
    }

    public async Task DeleteCatalogProductAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM catalog_products WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Produto não encontrado.");
    }

    public async Task<InventoryResponse?> GetInventoryAsync(Guid productId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT product_id,quantity,reserved_quantity FROM inventory WHERE product_id=@id";
        command.Parameters.AddWithValue("id", productId);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new InventoryResponse(reader.GetGuid(0), reader.GetInt32(1), reader.GetInt32(2)) : null;
    }

    public async Task UpdateInventoryAsync(Guid productId, int quantity, CancellationToken cancellationToken)
    {
        if (quantity < 0) throw new ArgumentException("Quantidade não pode ser negativa.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureCatalogAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE inventory SET quantity=@quantity WHERE product_id=@id";
        command.Parameters.AddWithValue("id", productId);
        command.Parameters.AddWithValue("quantity", quantity);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Estoque não encontrado.");
    }

    private static async Task EnsureCatalogAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS catalog_services (id uuid PRIMARY KEY, description varchar(300) NOT NULL, price numeric(18,2) NOT NULL, estimated_time double precision NOT NULL);
            CREATE TABLE IF NOT EXISTS catalog_products (id uuid PRIMARY KEY, description varchar(300) NOT NULL, price numeric(18,2) NOT NULL);
            CREATE TABLE IF NOT EXISTS inventory (product_id uuid PRIMARY KEY REFERENCES catalog_products(id), quantity int NOT NULL, reserved_quantity int NOT NULL DEFAULT 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public interface IMercadoPagoGateway
{
    Task<PaymentResponse> CreatePaymentAsync(PaymentRequest request, CancellationToken cancellationToken);
}

public interface IEventBus
{
    Task PublishAsync(BillingEvent message, CancellationToken cancellationToken);
}

public sealed class RabbitMqEventBus : IEventBus, IDisposable
{
    private readonly IConnection connection;
    private readonly IModel channel;

    public RabbitMqEventBus(IConfiguration configuration)
    {
        var factory = new ConnectionFactory { Uri = new Uri(configuration["RabbitMq:Uri"] ?? "amqp://guest:guest@localhost:5672/") };
        connection = factory.CreateConnection();
        channel = connection.CreateModel();
        channel.ExchangeDeclare("garage.events", ExchangeType.Topic, durable: true);
    }

    public Task PublishAsync(BillingEvent message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        channel.BasicPublish("garage.events", message.EventType, null, body);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        channel.Dispose();
        connection.Dispose();
    }
}

public sealed class MercadoPagoGateway(HttpClient client, IConfiguration configuration) : IMercadoPagoGateway
{
    public async Task<PaymentResponse> CreatePaymentAsync(PaymentRequest request, CancellationToken cancellationToken)
    {
        var token = configuration["MERCADOPAGO_ACCESS_TOKEN"]
            ?? throw new InvalidOperationException("MERCADOPAGO_ACCESS_TOKEN não foi configurado.");
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadopago.com/v1/payments");
        message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        message.Content = JsonContent.Create(new { transaction_amount = request.Amount, token = request.Token, payment_method_id = "visa", installments = 1 });
        using var response = await client.SendAsync(message, cancellationToken);
        var status = response.IsSuccessStatusCode ? "approved" : "rejected";
        return new PaymentResponse(request.ServiceOrderId, status, response.IsSuccessStatusCode ? response.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null : null);
    }
}
