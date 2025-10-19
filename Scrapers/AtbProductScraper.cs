using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace ProductScraper
{
    public class AtbProductScraper : IProductScraper
    {
        private readonly ScraperConfig _config;
        private readonly string _category;

        public AtbProductScraper(ScraperConfig config, string category)
        {
            _config = config ?? new ScraperConfig();
            _category = string.IsNullOrWhiteSpace(category) ? "ATB" : category;
        }

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

            using var playwright = await Playwright.CreateAsync();
            
            await using var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _config.Headless,
                SlowMo = _config.SlowMo,
                Args = new[] { "--disable-blink-features=AutomationControlled" }
            });

            Console.WriteLine($"Launching Firefox headless? {_config.Headless}");
            
            var cookiePath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scrapers", "cookies_playwright.json"));
            Console.WriteLine($"Looking for cookies at: {cookiePath}");
            
            var cookiesExist = File.Exists(cookiePath);
            Console.WriteLine($"Cookies file exists: {cookiesExist}");

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                StorageStatePath = cookiesExist ? cookiePath : null,
                IgnoreHTTPSErrors = true,
                JavaScriptEnabled = true,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:115.0) Gecko/20100101 Firefox/115.0",
                Locale = "uk-UA",
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 }, // More common resolution
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

            // Track if we've already solved CAPTCHA this session
            bool captchaSolvedThisSession = false;

            for (int i = 0; i < catalogUrls.Count; i++)
            {
                var url = catalogUrls[i];
                if (string.IsNullOrWhiteSpace(url)) continue;

                try
                {
                    if (_config.EnableLogging) Console.WriteLine($"\n[{i+1}/{catalogUrls.Count}] ATB: navigating to {url}");

                    // Longer delays
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

                    // Wait for potential CAPTCHA to load
                    await Task.Delay(3000);

                    // Check multiple CAPTCHA indicators
                    var captchaSelectors = new[]
                    {
                        "iframe[src*='turnstile']",
                        "iframe[src*='challenges.cloudflare']",
                        "[id*='cf-turnstile']",
                        "[class*='cf-turnstile']",
                        "#challenge-form"
                    };

                    bool hasCaptcha = false;
                    foreach (var selector in captchaSelectors)
                    {
                        var element = await page.QuerySelectorAsync(selector);
                        if (element != null)
                        {
                            hasCaptcha = true;
                            if (_config.EnableLogging) Console.WriteLine($"Detected CAPTCHA element: {selector}");
                            break;
                        }
                    }

                    // Also check for "Checking your browser" text
                    var pageText = await page.Locator("body").InnerTextAsync();
                    if (pageText.Contains("Checking your browser") || pageText.Contains("Just a moment"))
                    {
                        hasCaptcha = true;
                        if (_config.EnableLogging) Console.WriteLine("Detected Cloudflare challenge text");
                    }
                    
                    if (response?.Status == 403 || hasCaptcha)
                    {
                        Console.WriteLine($"\n⚠⚠⚠ CLOUDFLARE CHALLENGE DETECTED ⚠⚠⚠");
                        
                        // Save debug info
                        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                        var html = await page.ContentAsync();
                        var debugFile = $"blocked_page_{timestamp}.html";
                        File.WriteAllText(debugFile, html);
                        Console.WriteLine($"Saved HTML to: {debugFile}");

                        if (_config.SaveErrorScreenshots)
                        {
                            Directory.CreateDirectory("output");
                            var screenshotPath = Path.Combine("output", $"atb_captcha_{timestamp}.png");
                            await page.ScreenshotAsync(new PageScreenshotOptions { Path = screenshotPath, FullPage = true });
                            Console.WriteLine($"Saved screenshot: {screenshotPath}");
                        }
                        
                        // Manual solve option
                        if (GetConfigAllowsHumanSolve(_config) && !captchaSolvedThisSession)
                        {
                            Console.WriteLine("\n" + new string('=', 60));
                            Console.WriteLine("MANUAL CAPTCHA SOLVING REQUIRED");
                            Console.WriteLine(new string('=', 60));
                            Console.WriteLine("Instructions:");
                            Console.WriteLine("1. Look at the browser window that opened");
                            Console.WriteLine("2. Solve the Cloudflare Turnstile challenge");
                            Console.WriteLine("3. Wait for the page to fully load");
                            Console.WriteLine("4. Press ENTER here to continue");
                            Console.WriteLine(new string('=', 60));
                            Console.ReadLine();
                            
                            // Wait a bit more after user presses Enter
                            Console.WriteLine("Checking if challenge is solved...");
                            await Task.Delay(2000);

                            // Check if we're past the challenge
                            var stillHasCaptcha = false;
                            foreach (var selector in captchaSelectors)
                            {
                                var element = await page.QuerySelectorAsync(selector);
                                if (element != null)
                                {
                                    stillHasCaptcha = true;
                                    break;
                                }
                            }

                            // Also check URL - if it changed, we might be past it
                            var currentUrl = page.Url;
                            var responseStatus = response?.Status ?? 0;

                            if (!stillHasCaptcha && currentUrl.Contains("atbmarket.com") && !pageText.Contains("Checking your browser"))
                            {
                                Console.WriteLine("✓✓✓ CAPTCHA SOLVED! ✓✓✓");
                                captchaSolvedThisSession = true;
                                
                                // Save cookies IMMEDIATELY
                                await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = cookiePath });
                                Console.WriteLine($"✓ Cookies saved to: {cookiePath}");
                                
                                // Also save to backup location
                                var backupPath = cookiePath.Replace(".json", $"_backup_{timestamp}.json");
                                await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = backupPath });
                                Console.WriteLine($"✓ Backup cookies saved to: {backupPath}");
                                
                                // Wait a bit before continuing
                                await Task.Delay(3000);
                            }
                            else
                            {
                                Console.WriteLine("⚠ Challenge still present or page didn't load properly.");
                                Console.WriteLine($"Current URL: {currentUrl}");
                                Console.WriteLine("Options:");
                                Console.WriteLine("1. Press ENTER to retry this page");
                                Console.WriteLine("2. Press CTRL+C to abort");
                                Console.ReadLine();
                                
                                // Retry the current URL
                                i--;
                                continue;
                            }
                        }
                        else if (captchaSolvedThisSession)
                        {
                            // We solved it before, but got blocked again
                            Console.WriteLine("⚠ Got blocked again even after solving CAPTCHA once.");
                            Console.WriteLine("This means:");
                            Console.WriteLine("- ATB has very aggressive rate limiting");
                            Console.WriteLine("- You need to wait much longer between requests");
                            Console.WriteLine("- Consider using proxies");
                            Console.WriteLine("\nStopping to avoid further blocks...");
                            break;
                        }
                        else
                        {
                            Console.WriteLine("⚠ HumanSolveCaptcha is disabled. Stopping.");
                            Console.WriteLine("Set HumanSolveCaptcha=true in config to manually solve.");
                            break;
                        }
                    }

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
                        Console.WriteLine($"⚠ No product elements found on {url}");
                        
                        // Debug: show what we got
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

                                products.Add(new Product
                                {
                                    Name = text,
                                    Category = _category
                                });
                            }
                            catch { }
                        }
                        
                        var addedCount = products.Count - beforeCount;
                        Console.WriteLine($"✓ Extracted {addedCount} products (Total: {products.Count})");
                        
                        // Save cookies after successful scrape
                        if (addedCount > 0)
                        {
                            await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = cookiePath });
                            if (_config.EnableLogging) Console.WriteLine("✓ Cookies updated");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ ERROR for {url}: {ex.Message}");
                    if (_config.SaveErrorScreenshots)
                    {
                        Directory.CreateDirectory("output");
                        var path = Path.Combine("output", $"atb_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                        try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }); } catch { }
                    }
                }

                // Much longer delay between pages
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