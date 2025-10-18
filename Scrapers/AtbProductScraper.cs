using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace ProductScraper
{
    // /*
    //  Small ATB scraper (documentation):
    //  - Extracts raw inner text from product cards on ATB catalog pages.
    //  - Keeps extraction minimal: raw text is stored in Product.Name so parsing can be added later.
    //  - Detects Cloudflare / Turnstile challenges and can wait for a manual solve when
    //    ScraperConfig.HumanSolveCaptcha == true (run with Headless = false to interact).
    //  - Enable detailed output via ScraperConfig.EnableLogging and save screenshots with SaveErrorScreenshots.
    //  - Avoids aggressive headers and networkidle waits to reduce Cloudflare triggers.
    // */
    public class AtbProductScraper : IProductScraper
    {
        private readonly ScraperConfig _config;
        private readonly string _category;

        public AtbProductScraper(ScraperConfig config, string category)
        {
            _config = config ?? new ScraperConfig();
            _category = string.IsNullOrWhiteSpace(category) ? "ATB" : category;
        }

        // Safely detect whether the ScraperConfig type exposes a HumanSolveCaptcha boolean
        // property; use reflection so this code compiles even if that property doesn't exist.
        private static bool GetConfigAllowsHumanSolve(ScraperConfig config)
        {
            if (config == null) return false;
            try
            {
                var prop = config.GetType().GetProperty("HumanSolveCaptcha");
                if (prop != null && prop.PropertyType == typeof(bool))
                {
                    var val = prop.GetValue(config);
                    return val is bool b && b;
                }
            }
            catch { }
            return false;
        }

        public async Task<List<Product>> ScrapeAsync(List<string> catalogUrls)
        {
            var products = new List<Product>();

            var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/116.0.0.0 Safari/537.36";

            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = false,
                SlowMo = _config.SlowMo,
                Args = new[]
                {
                    "--disable-blink-features=AutomationControlled",
                    "--no-sandbox",
                    "--disable-setuid-sandbox"
                }
            });

            Console.WriteLine($"Launching Chromium headless? {_config.Headless}");
            var cookiePath = Path.Combine(AppContext.BaseDirectory, "Scrapers", "cookies.json");
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                StorageStatePath = cookiePath,
                IgnoreHTTPSErrors = true,
                JavaScriptEnabled = true,
                UserAgent = userAgent,
                Locale = "uk-UA",
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
                    ["Accept-Language"] = "uk-UA,uk;q=0.9,en-US;q=0.8,en;q=0.7",
                    ["Referer"] = "https://www.atbmarket.com/"
                }
            });

            var page = await context.NewPageAsync();

            if (_config.EnableLogging)
            {
                page.Console += (_, msg) => Console.WriteLine($"BROWSER: {msg.Text}");
                page.PageError += (_, err) => Console.WriteLine($"PAGE ERROR: {err}");
            }

            // Stealth: minimize detectable automation signals
            await page.AddInitScriptAsync(@"Object.defineProperty(navigator, 'webdriver', {get: () => false});
window.chrome = window.chrome || { runtime: {} };
Object.defineProperty(navigator, 'languages', {get: () => ['uk-UA','uk','en-US','en']});
Object.defineProperty(navigator, 'plugins', {get: () => [1,2,3,4,5]});");

            // selectors to try (extend when you know ATB markup variants)
            var selectors = new[]
            {
                ".product-tile[data-testid*='product']",
                ".product-card",
                "[class*='ProductTile']",
                "[class*='catalog-item__bottom']",
                "[data-test*='product']",
                ".product, .product-item, .card, [class*='product']"
            };

            foreach (var url in catalogUrls)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;

                try
                {
                    if (_config.EnableLogging) Console.WriteLine($"ATB: navigating to {url}");

                    page.SetDefaultNavigationTimeout(45000);
                    page.Response += (_, response) =>
                    {
                        if (response.Status == 403)
                        {
                            Console.WriteLine($"ATB: access denied(403) on {url}.");
                        }
                    };
                    var response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 45000
                    });
                    Console.WriteLine($"Response status: {response?.Status}");

                    var html = await page.ContentAsync();
                    File.WriteAllText("cf_dump.html", html);
                    // detect Cloudflare / Turnstile / general captcha frames or 403
                    var hasCaptchaFrame = await page.QuerySelectorAsync("iframe[src*='turnstile'], iframe[src*='challenges.cloudflare'], [id*='cf-turnstile'], [class*='captcha']") != null;
                    if ((response != null && response.Status == 403) || hasCaptchaFrame)
                    {
                        Console.WriteLine($"ATB: captcha/challenge detected on {url} (status {response?.Status}).");

                        if (GetConfigAllowsHumanSolve(_config))
                        {
                            Console.WriteLine("Please solve the challenge in the opened browser window, then press Enter to continue...");
                            if (_config.Headless)
                                Console.WriteLine("Note: set Headless = false in ScraperConfig to see the browser UI.");

                            var solved = false;
                            var start = DateTime.UtcNow;
                            while (!solved && DateTime.UtcNow - start < TimeSpan.FromMinutes(5))
                            {
                                await Task.Delay(1500);
                                var still = await page.QuerySelectorAsync("iframe[src*='turnstile'], iframe[src*='challenges.cloudflare'], [id*='cf-turnstile'], [class*='captcha']");
                                if (still == null)
                                {
                                    solved = true;
                                    break;
                                }

                                if (Console.KeyAvailable)
                                {
                                    var key = Console.ReadKey(intercept: true);
                                    if (key.Key == ConsoleKey.Enter)
                                    {
                                        solved = true;
                                        break;
                                    }
                                }
                            }

                            if (!solved)
                                Console.WriteLine("Timed out waiting for manual CAPTCHA solve; continuing to next URL.");
                            else
                                Console.WriteLine("Continuing after manual solve.");
                        }
                        else
                        {
                            if (_config.SaveErrorScreenshots)
                            {
                                Directory.CreateDirectory("output");
                                var path = Path.Combine("output", $"atb_403_or_captcha_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                                await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
                                Console.WriteLine($"Saved screenshot: {path}");
                            }
                            continue;
                        }
                    }

                    // wait briefly for product-like elements (try selectors one by one)
                    IElementHandle[] els = Array.Empty<IElementHandle>();
                    foreach (var sel in selectors)
                    {
                        try
                        {
                            var maybe = await page.QuerySelectorAllAsync(sel);
                            if (maybe != null && maybe.Count > 0)
                            {
                                els = maybe.ToArray();
                                break;
                            }

                            var waited = await page.WaitForSelectorAsync(sel, new PageWaitForSelectorOptions { Timeout = 8000 });
                            if (waited != null)
                            {
                                var found = await page.QuerySelectorAllAsync(sel);
                                if (found != null && found.Count > 0)
                                {
                                    els = found.ToArray();
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            // ignore selector failures and try next
                        }
                    }

                    // fallback: wait briefly for any product-like element if none found
                    if (els.Length == 0)
                    {
                        try
                        {
                            await page.WaitForTimeoutAsync(800); // small pause for lazy-loaded content
                            var fallback = await page.QuerySelectorAllAsync("[class*='card'], [class*='product']");
                            if (fallback != null && fallback.Count > 0) els = fallback.ToArray();
                        }
                        catch { }
                    }

                    if (els.Length == 0)
                    {
                        if (_config.EnableLogging) Console.WriteLine($"ATB: no product elements found on {url} (body length { (await page.ContentAsync()).Length })");
                    }
                    else
                    {
                        foreach (var el in els)
                        {
                            try
                            {
                                var text = (await el.InnerTextAsync())?.Trim();
                                if (string.IsNullOrWhiteSpace(text)) continue;

                                products.Add(new Product
                                {
                                    Name = text,
                                    Category = _category
                                });
                            }
                            catch { /* ignore element-level errors */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ATB ERROR for {url}: {ex.Message}");
                    if (_config.SaveErrorScreenshots)
                    {
                        Directory.CreateDirectory("output");
                        var path = Path.Combine("output", $"atb_error_{DateTime.UtcNow:yyyyMMdd_HHmms}.png");
                        try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }); Console.WriteLine($"Saved screenshot: {path}"); } catch { }
                    }
                }
            }

            await context.CloseAsync();
            return products;
        }
    }
}