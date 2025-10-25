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
                        var beforeCount = products.Count;
                        foreach (var el in els)
                        {
                            try
                            {
                                var text = (await el.InnerTextAsync())?.Trim();
                                if (string.IsNullOrWhiteSpace(text)) continue;

 // Parse structured info from the element text
                                var prod = new Product { Category = _category };

                                // Normalize whitespace and join lines for regex
                                var normalized = Regex.Replace(text, @"\r\n|\r|\n", " ").Trim();

                                // Capture all currency-like numbers (e.g. "2.40", "3,00")
                                var moneyMatches = Regex.Matches(normalized, @"(\d+(?:[.,]\d+)?)\s*(?:грн|₴)?", RegexOptions.IgnoreCase);
                                if (moneyMatches.Count > 0)
                                {
                                    // First match -> price
                                    var pStr = moneyMatches[0].Groups[1].Value.Replace(',', '.');
                                    if (decimal.TryParse(pStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var p))
                                        prod.Price = p;
                                }

                                // If there is a second money match, treat it as old price
                                if (moneyMatches.Count > 1)
                                {
                                    var opStr = moneyMatches[1].Groups[1].Value.Replace(',', '.');
                                    if (decimal.TryParse(opStr, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var op))
                                        prod.OldPrice = op;
                                }

                                // Discount like "- 20%" or "20%"
                                var discMatch = Regex.Match(normalized, @"-?\s*(\d{1,3}%|\d+\s?%|\d+%)");
                                if (discMatch.Success) prod.Discount = discMatch.Groups[1].Value.Trim();

                                // Derive name from element lines: prefer longest line that does not contain price/грн/₴/%
                                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                                .Select(l => l.Trim())
                                                .Where(l => l.Length > 0)
                                                .ToArray();

                                string nameCandidate = lines
                                    .Where(l => !Regex.IsMatch(l, @"\d+(?:[.,]\d+)?\s*(грн|₴|%)", RegexOptions.IgnoreCase) &&
                                                !l.ToLower().EndsWith("г") && !l.ToLower().EndsWith("г."))
                                    .OrderByDescending(l => l.Length)
                                    .FirstOrDefault();

                                prod.Name = (nameCandidate ?? lines.FirstOrDefault() ?? normalized).Trim();

                                // Is on sale if old price exists and is greater than price
                                if (prod.OldPrice.HasValue && prod.OldPrice.Value > prod.Price)
                                    prod.IsOnSale = true;

                                products.Add(prod);
                            }
                            catch { }
                        
                        }
                        
                        var addedCount = products.Count - beforeCount;
                        Console.WriteLine($" Extracted {addedCount} products (Total: {products.Count})");
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
            
            Console.WriteLine($"\n{'='*60}");
            Console.WriteLine($"SCRAPING COMPLETE");
            Console.WriteLine($"Total products extracted: {products.Count}");
            Console.WriteLine($"{'='*60}");
            
            return products;
        }
    }
}