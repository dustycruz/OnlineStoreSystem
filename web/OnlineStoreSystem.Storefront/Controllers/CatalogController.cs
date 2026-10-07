// File: web/OnlineStoreSystem.Storefront/Controllers/CatalogController.cs
using Microsoft.AspNetCore.Mvc;
using OnlineStoreSystem.Storefront.Clients;
using OnlineStoreSystem.Storefront.Models;

namespace OnlineStoreSystem.Storefront.Controllers;

/// <summary>Browse products (CatalogService) with live availability (InventoryService).</summary>
public sealed class CatalogController(StorefrontApiClient api) : Controller
{
    private const int PageSize = 12;

    public async Task<IActionResult> Index(string? search, string? category, int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var productsTask = api.GetProductsAsync(search, category, page, PageSize, cancellationToken);
        var stockTask = api.GetStockLevelsAsync(cancellationToken);
        await Task.WhenAll(productsTask, stockTask);

        var products = await productsTask;
        var stock = await stockTask;

        if (!products.IsSuccess)
        {
            return View(new CatalogIndexViewModel
            {
                Search = search,
                Category = category,
                ErrorMessage = products.ErrorMessage
            });
        }

        var pageResult = products.Value!;
        return View(new CatalogIndexViewModel
        {
            Products = pageResult.Items
                .Select(p => new ProductTileViewModel(p, stock.GetValueOrDefault(p.ProductId)))
                .ToList(),
            Search = search,
            Category = category,
            Page = pageResult.Page,
            TotalPages = pageResult.TotalPages,
            TotalCount = pageResult.TotalCount
        });
    }

    public async Task<IActionResult> Details(int id, CancellationToken cancellationToken)
    {
        var productTask = api.GetProductAsync(id, cancellationToken);
        var stockTask = api.GetStockLevelsAsync(cancellationToken);
        await Task.WhenAll(productTask, stockTask);

        var product = await productTask;
        if (product.StatusCode == StatusCodes.Status404NotFound)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("NotFound", $"Product {id} does not exist.");
        }

        if (!product.IsSuccess)
        {
            return RedirectToAction("Error", "Home", new { message = product.ErrorMessage });
        }

        var stock = await stockTask;
        return View(new ProductTileViewModel(product.Value!, stock.GetValueOrDefault(id)));
    }
}
