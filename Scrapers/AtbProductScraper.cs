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

            using var playwright = await Playwright.CreateAsync();
            
            // Use Firefox instead of Chromium - harder to detect
            await using var browser = await playwright.Firefox.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = _config.Headless,
                SlowMo = _config.SlowMo
            });

            Console.WriteLine($"Launching Firefox headless? {_config.Headless}");
            
            var cookiePath = "Scrapers/cookies_playwright.json";
            Console.WriteLine($"Looking for cookies at: {Path.GetFullPath(cookiePath)}");
            
            // Check if cookies file exists
            var cookiesExist = File.Exists(cookiePath);
            Console.WriteLine($"Cookies file exists: {cookiesExist}");

            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                StorageStatePath = cookiesExist ? cookiePath : null,
                IgnoreHTTPSErrors = true,
                JavaScriptEnabled = true,
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:109.0) Gecko/20100101 Firefox/115.0",
                Locale = "uk-UA",
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                ExtraHTTPHeaders = new Dictionary<string, string>
                {
                    ["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8",
                    ["Accept-Language"] = "uk-UA,uk;q=0.9,en-US;q=0.8,en;q=0.7",
                    ["Accept-Encoding"] = "gzip, deflate, br",
                    ["DNT"] = "1",
                    ["Connection"] = "keep-alive",
                    ["Upgrade-Insecure-Requests"] = "1",
                    ["Sec-Fetch-Dest"] = "document",
                    ["Sec-Fetch-Mode"] = "navigate",
                    ["Sec-Fetch-Site"] = "none",
                    ["Sec-Fetch-User"] = "?1",
                    ["Cache-Control"] = "max-age=0"
                }
            });

            var page = await context.NewPageAsync();

            if (_config.EnableLogging)
            {
                page.Console += (_, msg) => Console.WriteLine($"BROWSER: {msg.Text}");
                page.PageError += (_, err) => Console.WriteLine($"PAGE ERROR: {err}");
            }

            // More aggressive stealth
            await page.AddInitScriptAsync(@"
                // Remove webdriver property
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                
                // Add chrome object for Firefox
                window.chrome = { runtime: {} };
                
                // Override permissions
                const originalQuery = window.navigator.permissions.query;
                window.navigator.permissions.query = (parameters) => (
                    parameters.name === 'notifications' ?
                        Promise.resolve({ state: Notification.permission }) :
                        originalQuery(parameters)
                );
                
                // Add realistic plugins
                Object.defineProperty(navigator, 'plugins', {
                    get: () => [
                        { name: 'Chrome PDF Plugin', filename: 'internal-pdf-viewer', description: 'Portable Document Format' },
                        { name: 'Chrome PDF Viewer', filename: 'mhjfbmdgcfjbbpaeojofohoefgiehjai', description: '' },
                        { name: 'Native Client', filename: 'internal-nacl-plugin', description: '' }
                    ]
                });
                
                // Languages
                Object.defineProperty(navigator, 'languages', {
                    get: () => ['uk-UA', 'uk', 'en-US', 'en']
                });
                
                // Platform
                Object.defineProperty(navigator, 'platform', {
                    get: () => 'Win32'
                });
                
                // Vendor
                Object.defineProperty(navigator, 'vendor', {
                    get: () => 'Google Inc.'
                });
            ");

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

                    // Add random delay before each request (more human-like)
                    var randomDelay = new Random().Next(2000, 5000);
                    await Task.Delay(randomDelay);

                    page.SetDefaultNavigationTimeout(60000);
                    
                    var response = await page.GotoAsync(url, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.Load, // Wait for full load instead of just DOM
                        Timeout = 60000
                    });
                    
                    Console.WriteLine($"Response status: {response?.Status}");

                    // Wait a bit for any lazy-loaded content
                    await Task.Delay(3000);

                    // Check for captcha
                    var hasCaptchaFrame = await page.QuerySelectorAsync("iframe[src*='turnstile'], iframe[src*='challenges.cloudflare'], [id*='cf-turnstile'], [class*='captcha']") != null;
                    
                    if ((response != null && response.Status == 403) || hasCaptchaFrame)
                    {
                        Console.WriteLine($"ATB: captcha/challenge detected on {url} (status {response?.Status}).");
                        
                        // Save HTML for inspection
                        var html = await page.ContentAsync();
                        File.WriteAllText($"blocked_page_{DateTime.UtcNow:yyyyMMddHHmmss}.html", html);
                        Console.WriteLine($"Saved blocked page HTML for inspection");

                        if (_config.SaveErrorScreenshots)
                        {
                            Directory.CreateDirectory("output");
                            var path = Path.Combine("output", $"atb_blocked_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                            await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true });
                            Console.WriteLine($"Saved screenshot: {path}");
                        }
                        
                        // If manual solving is enabled, wait for user
                        if (GetConfigAllowsHumanSolve(_config))
                        {
                            Console.WriteLine("\n===========================================");
                            Console.WriteLine("CAPTCHA DETECTED - MANUAL INTERVENTION NEEDED");
                            Console.WriteLine("===========================================");
                            Console.WriteLine("The browser should be visible (set Headless=false)");
                            Console.WriteLine("Please solve the CAPTCHA in the browser, then press Enter to continue...");
                            Console.ReadLine();
                            
                            // Check if we're past the captcha
                            var stillBlocked = await page.QuerySelectorAsync("iframe[src*='turnstile'], iframe[src*='challenges.cloudflare']");
                            if (stillBlocked == null)
                            {
                                Console.WriteLine("✓ CAPTCHA appears to be solved! Continuing...");
                                
                                // Save cookies after successful solve
                                await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = cookiePath });
                                Console.WriteLine($"✓ Saved fresh cookies to {cookiePath}");
                            }
                            else
                            {
                                Console.WriteLine("⚠ CAPTCHA still present. Skipping this URL.");
                                continue;
                            }
                        }
                        else
                        {
                            Console.WriteLine("Skipping this URL due to access restrictions.");
                            continue;
                        }
                    }

                    // Try to find products
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
                        if (_config.EnableLogging) 
                        {
                            Console.WriteLine($"ATB: no product elements found on {url}");
                            var bodyText = await page.Locator("body").InnerTextAsync();
                            Console.WriteLine($"Page body preview: {bodyText.Substring(0, Math.Min(200, bodyText.Length))}...");
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
                        if (_config.EnableLogging) Console.WriteLine($"✓ Extracted {addedCount} products from this page");
                        
                        // Save cookies after successful scrape
                        if (addedCount > 0)
                        {
                            await context.StorageStateAsync(new BrowserContextStorageStateOptions { Path = cookiePath });
                            if (_config.EnableLogging) Console.WriteLine("✓ Saved fresh cookies");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ATB ERROR for {url}: {ex.Message}");
                    if (_config.SaveErrorScreenshots)
                    {
                        Directory.CreateDirectory("output");
                        var path = Path.Combine("output", $"atb_error_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
                        try { await page.ScreenshotAsync(new PageScreenshotOptions { Path = path, FullPage = true }); } catch { }
                    }
                }

                // Longer delay between pages
                if (_config.EnableLogging) Console.WriteLine($"Waiting 5-8 seconds before next page...");
                await Task.Delay(new Random().Next(5000, 8000));
            }

            await context.CloseAsync();
            return products;
        }
    }
}