// File: src/OnlineStoreSystem.CatalogService/Controllers/ProductsController.cs
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OnlineStoreSystem.CatalogService.Data;
using OnlineStoreSystem.CatalogService.Data.Entities;
using OnlineStoreSystem.CatalogService.Mapping;
using OnlineStoreSystem.Shared.Contracts.Catalog;
using OnlineStoreSystem.Shared.Contracts.Common;
using OnlineStoreSystem.Shared.Problems;

namespace OnlineStoreSystem.CatalogService.Controllers;

/// <summary>
/// Products CRUD - implements <c>contracts/catalog-v1.yaml</c> (URI-versioned: <c>/api/v1/products</c>).
/// Every 400 / 404 / 409 response is an RFC 7807 problem document.
/// </summary>
[ApiController]
[Route("api/v1/products")]
public sealed class ProductsController(
    CatalogDbContext db,
    TimeProvider clock,
    ILogger<ProductsController> logger) : ControllerBase
{
    private const int MaxPageSize = 100;
    private const int MaxIdsPerLookup = 100;

    /// <summary>Lists products (paged). <c>ids</c> turns the call into a batch lookup used by OrderService.</summary>
    [HttpGet(Name = "ListProducts")]
    [ProducesResponseType<PagedResult<ProductDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<ActionResult<PagedResult<ProductDto>>> GetProducts(
        [FromQuery] string? search,
        [FromQuery] string? category,
        [FromQuery(Name = "ids")] int[]? ids,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var errors = new Dictionary<string, string[]>();
        if (page < 1)
        {
            errors["page"] = ["page must be greater than or equal to 1."];
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
        }

        if (ids is { Length: > MaxIdsPerLookup })
        {
            errors["ids"] = [$"At most {MaxIdsPerLookup} ids can be looked up at once."];
        }

        if (errors.Count > 0)
        {
            return this.ValidationProblemFor(errors);
        }

        var query = db.Products.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (ids is { Length: > 0 })
        {
            var distinctIds = ids.Distinct().ToArray();
            query = query.Where(p => distinctIds.Contains(p.ProductId));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p =>
                p.Name.Contains(term) ||
                p.Sku.Contains(term) ||
                (p.Description != null && p.Description.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            var categoryName = category.Trim();
            query = query.Where(p => p.Category == categoryName);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(p => p.ProductId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ProductMappings.Projection)
            .ToListAsync(cancellationToken);

        logger.LogInformation("Returned {Count} of {Total} products (page {Page}, ids filter: {IdsFilter})",
            items.Count, totalCount, page, ids is { Length: > 0 } ? string.Join(',', ids) : "none");

        return Ok(new PagedResult<ProductDto>(items, page, pageSize, totalCount));
    }

    /// <summary>Gets one product.</summary>
    [HttpGet("{productId:int}", Name = "GetProductById")]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<ProductDto>> GetProductById(int productId, CancellationToken cancellationToken)
    {
        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.ProductId == productId)
            .Select(ProductMappings.Projection)
            .SingleOrDefaultAsync(cancellationToken);

        return product is null ? this.NotFoundProblem("Product", productId) : Ok(product);
    }

    /// <summary>Creates a product.</summary>
    [HttpPost(Name = "CreateProduct")]
    [Consumes("application/json")]
    [ProducesResponseType<ProductDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<ProductDto>> CreateProduct(
        [FromBody] CreateProductRequest request,
        CancellationToken cancellationToken)
    {
        var sku = request.Sku.Trim();
        if (await db.Products.AnyAsync(p => p.Sku == sku, cancellationToken))
        {
            return this.ConflictProblem("Duplicate SKU", $"A product with SKU '{sku}' already exists.");
        }

        var product = new Product();
        product.Apply(request, clock.GetUtcNow().UtcDateTime);
        db.Products.Add(product);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation)
        {
            return this.ConflictProblem("Duplicate SKU", $"A product with SKU '{sku}' already exists.");
        }

        logger.LogInformation("Created product {ProductId} ({Sku})", product.ProductId, product.Sku);
        return CreatedAtRoute("GetProductById", new { productId = product.ProductId }, product.ToDto());
    }

    /// <summary>Replaces a product (full update).</summary>
    [HttpPut("{productId:int}", Name = "UpdateProduct")]
    [Consumes("application/json")]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<ActionResult<ProductDto>> UpdateProduct(
        int productId,
        [FromBody] UpdateProductRequest request,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.ProductId == productId, cancellationToken);
        if (product is null)
        {
            return this.NotFoundProblem("Product", productId);
        }

        var sku = request.Sku.Trim();
        if (await db.Products.AnyAsync(p => p.Sku == sku && p.ProductId != productId, cancellationToken))
        {
            return this.ConflictProblem("Duplicate SKU", $"Another product already uses SKU '{sku}'.");
        }

        product.Apply(request, clock.GetUtcNow().UtcDateTime);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueConstraintViolation)
        {
            return this.ConflictProblem("Duplicate SKU", $"Another product already uses SKU '{sku}'.");
        }

        logger.LogInformation("Updated product {ProductId} ({Sku})", product.ProductId, product.Sku);
        return Ok(product.ToDto());
    }

    /// <summary>Deletes a product.</summary>
    [HttpDelete("{productId:int}", Name = "DeleteProduct")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> DeleteProduct(int productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.ProductId == productId, cancellationToken);
        if (product is null)
        {
            return this.NotFoundProblem("Product", productId);
        }

        db.Products.Remove(product);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted product {ProductId} ({Sku})", productId, product.Sku);
        return NoContent();
    }
}
