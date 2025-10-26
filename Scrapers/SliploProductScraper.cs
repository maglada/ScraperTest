using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;
using System.Linq;
using System.Text.RegularExpressions;
using System.Globalization;

namespace ProductScraper
{
    public class SilpoProductScraper : IProductScraper
    {
        private readonly ScraperConfig _config;
        private readonly string _category;

        public SilpoProductScraper(ScraperConfig config, string category)
        {
            _config = config ?? new ScraperConfig();
            _category = string.IsNullOrWhiteSpace(category) ? "Silpo" : category;
        }


        public async Task<List<Product>> ScrapeAsync(List<string> catalogUrls)
        {
            var products = new List<Product>();

            using var playwright = await Playwright.CreateAsync();
            
            await using var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _config.Headless,
                SlowMo = _config.SlowMo
            });

            Console.WriteLine($"Launching Firefox headless? {_config.Headless}");

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = true,
                JavaScriptEnabled = true,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:115.0) Gecko/20100101 Firefox/115.0",
                Locale = "uk-UA",
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept-Language"] = "uk-UA,uk;q=0.9",
                    ["Referer"] = "https://www.atbmarket.com/"
                }
            });

            var page = await context.NewPageAsync();

            if (_config.EnableLogging)
            {
                page.Console += (_, msg) => Console.WriteLine($"BROWSER: {msg.Text}");
                page.PageError += (_, err) => Console.WriteLine($"PAGE ERROR: {err}");
            }

            await page.AddInitScriptAsync(@"
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                delete navigator.__proto__.webdriver;
            ");

            var selectors = new[]
            {
                "[class*='catalog-item__bottom']",
                ".product-tile[data-testid*='product']",
                ".product-card",
                "[class*='ProductTile']"
            };

            for (int i = 0; i < catalogUrls.Count; i++)
            {
                var url = catalogUrls[i];
                if (string.IsNullOrWhiteSpace(url)) continue;

                try
                {
                    if (_config.EnableLogging) Console.WriteLine($"\n[{i+1}/{catalogUrls.Count}] ATB: navigating to {url}");

                    var delayMs = new Random().Next(5000, 10000);
                    if (_config.EnableLogging) Console.WriteLine($"Pre-request delay: {delayMs}ms");
                    await Task.Delay(delayMs);

                    page.SetDefaultNavigationTimeout(90000);
                    
                    var response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 90000
                    });
                    
                    Console.WriteLine($"Response status: {response?.Status}");

                    // Find products
                    IElementHandle[] els = Array.Empty<IElementHandle>();
                    foreach (var sel in selectors)
                    {
                        try
                        {
                            var maybe = await page.QuerySelectorAllAsync(sel);
                            if (maybe != null && maybe.Count > 0)
                            {
                                els = maybe.ToArray();
                                if (_config.EnableLogging) Console.WriteLine($"Found {els.Length} elements with selector: {sel}");
                                break;
                            }
                        }
                        catch { }
                    }

                    if (els.Length == 0)
                    {
                        Console.WriteLine($"No product elements found on {url}");
                        
                        if (_config.EnableLogging)
                        {
                            var bodyText = await page.Locator("body").InnerTextAsync();
                            Console.WriteLine($"Page preview: {bodyText.Substring(0, Math.Min(200, bodyText.Length))}...");
                        }
                    }
                    else
                    {
                        int beforeCount = products.Count;
                        foreach (var el in els)
                        {
                            try
                            {
                                var prod = new Product { Category = _category };

                                // Get product name
                                var nameEl = await el.QuerySelectorAsync(".product-card__title");
                                prod.Name = (await nameEl?.InnerTextAsync())?.Trim() ?? "";

                                // Get the price container
                                var priceContainer = await el.QuerySelectorAsync(".product-card-price");
                                
                                if (priceContainer != null)
                                {
                                    // Get current price (retail price)
                                    var priceEl = await priceContainer.QuerySelectorAsync(".product-card-price__displayPrice");
                                    if (priceEl != null)
                                    {
                                        var priceText = (await priceEl.InnerTextAsync())?.Trim() ?? "";
                                        var price = Regex.Match(priceText, @"(\d+(?:[.,]\d+)?)");
                                        if (price.Success && decimal.TryParse(price.Groups[1].Value.Replace(',', '.'),
                                            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var p))
                                        {
                                            prod.Price = p;
                                        }
                                    }

                                    // Check for bulk price
                                    var bulkPriceEl = await priceContainer.QuerySelectorAsync(".product-card-offer__price");
                                    if (bulkPriceEl != null)
                                    {
                                        var bulkPriceText = (await bulkPriceEl.InnerTextAsync())?.Trim() ?? "";
                                        var bulkPrice = Regex.Match(bulkPriceText, @"(\d+(?:[.,]\d+)?)");
                                        if (bulkPrice.Success && decimal.TryParse(bulkPrice.Groups[1].Value.Replace(',', '.'),
                                            NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var bp))
                                        {
                                            prod.BulkPrice = bp;
                                            prod.IsBulk = true;
                                            
                                            // Get bulk quantity requirement (optional)
                                            var bulkValueEl = await priceContainer.QuerySelectorAsync(".product-card-offer__value");
                                            if (bulkValueEl != null && _config.EnableLogging)
                                            {
                                                var bulkValueText = (await bulkValueEl.InnerTextAsync())?.Trim() ?? "";
                                                Console.WriteLine($"  Bulk offer: {bulkValueText} at {bp} грн");
                                            }
                                        }
                                    }
                                }

                                // Get old price
                                var oldPriceEl = await el.QuerySelectorAsync(".product-card-price__displayOldPrice");
                                if (oldPriceEl != null)
                                {
                                    var oldPriceText = (await oldPriceEl.InnerTextAsync())?.Trim() ?? "";
                                    var oldPrice = Regex.Match(oldPriceText, @"(\d+(?:[.,]\d+)?)");
                                    if (oldPrice.Success && decimal.TryParse(oldPrice.Groups[1].Value.Replace(',', '.'),
                                        NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var op))
                                    {
                                        prod.OldPrice = op;
                                        prod.IsOnSale = op > prod.Price;
                                    }
                                }

                                // Get discount
                                var discountEl = await el.QuerySelectorAsync(".product-card-price__sale");
                                if (discountEl != null)
                                {
                                    prod.Discount = (await discountEl.InnerTextAsync())?.Trim() ?? "";
                                }

                                // Get weight/amount info
                                var weightEl = await el.QuerySelectorAsync(".ft-typo-14-semibold span");
                                if (weightEl != null)
                                {
                                    var weightText = (await weightEl.InnerTextAsync())?.Trim() ?? "";
                                    // Store weight info in Name for now, could add a separate Weight property to Product class if needed
                                    prod.Name = $"{prod.Name} ({weightText})";
                                }

                                if (!string.IsNullOrWhiteSpace(prod.Name))
                                {
                                    products.Add(prod);
                                    
                                    if (_config.EnableLogging && prod.IsBulk)
                                    {
                                        Console.WriteLine($"  ✓ Found bulk product: {prod.Name} - Retail: {prod.Price} грн, Bulk: {prod.BulkPrice} грн");
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                if (_config.EnableLogging) Console.WriteLine($"Error parsing product: {ex.Message}");
                            }
                        }

                        int addedCount = products.Count - beforeCount;
                        int bulkCount = products.Skip(beforeCount).Count(p => p.IsBulk);
                        Console.WriteLine($" Extracted {addedCount} products ({bulkCount} with bulk pricing) (Total: {products.Count})");
                    }
                    
                }
                catch (Exception ex)
                {
                    Console.WriteLine($" ERROR for {url}: {ex.Message}");
                    if (_config.SaveErrorScreenshots)
                    {
                        Directory.CreateDirectory("output");
                        var path = Path.Combine("output", $"atb_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                        try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }); } catch { }
                    }
                }

                if (i < catalogUrls.Count - 1)
                {
                    var waitTime = new Random().Next(15000, 25000);
                    if (_config.EnableLogging) Console.WriteLine($"Inter-page delay: {waitTime}ms ({waitTime/1000}s)");
                    await Task.Delay(waitTime);
                }
            }

            await context.CloseAsync();
            
            int totalBulkProducts = products.Count(p => p.IsBulk);
            
            Console.WriteLine($"\n{new string('=', 60)}");
            Console.WriteLine($"SCRAPING COMPLETE");
            Console.WriteLine($"Total products extracted: {products.Count}");
            Console.WriteLine($"Products with bulk pricing: {totalBulkProducts}");
            Console.WriteLine($"{new string('=', 60)}");
            
            return products;
        }
    }
}