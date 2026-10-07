// File: web/OnlineStoreSystem.Storefront/Controllers/HomeController.cs
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using OnlineStoreSystem.Shared.Correlation;
using OnlineStoreSystem.Storefront.Clients;
using OnlineStoreSystem.Storefront.Models;

namespace OnlineStoreSystem.Storefront.Controllers;

public sealed class HomeController(
    StorefrontApiClient api,
    ILogger<HomeController> logger) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var productsTask = api.GetProductsAsync(null, null, 1, 4, cancellationToken);
        var stockTask = api.GetStockLevelsAsync(cancellationToken);
        await Task.WhenAll(productsTask, stockTask);

        var products = await productsTask;
        var stock = await stockTask;

        if (!products.IsSuccess)
        {
            logger.LogWarning("Home page rendered without products: {Error}", products.ErrorMessage);
            return View(new HomeViewModel { ErrorMessage = products.ErrorMessage });
        }

        return View(new HomeViewModel
        {
            Featured = products.Value!.Items
                .Select(p => new ProductTileViewModel(p, stock.GetValueOrDefault(p.ProductId)))
                .ToList()
        });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error(string? message = null) => View(new ErrorViewModel
    {
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
        CorrelationId = HttpContext.CorrelationId,
        Message = message
    });
}
