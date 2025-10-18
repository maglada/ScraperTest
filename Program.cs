using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProductScraper;

class Program
{
    static async Task Main(string[] args)
    {
        try
        {
            // Configure scraper behavior
            var config = new ScraperConfig
            {
                Headless = true,
                SlowMo = 1000,
                EnableLogging = true,
                EnableDebugOutput = false,
                SaveDebugScreenshots = true,
                SaveErrorScreenshots = true
            };

            // Create factory with configuration
            var factory = new ScraperFactory(config);

            // Option 1: Process all files automatically
            Console.WriteLine("=== Processing all link files (Novus, ATB, etc.) ===");
            var allResults = await factory.ProcessAllFilesAsync("sites", "*Links*.txt");

            // Display summary
            Console.WriteLine($"\n=== SCRAPING COMPLETE ===");
            Console.WriteLine($"Processed {allResults.Count} files");
            
            int totalProducts = 0;
            int totalSaleItems = 0;
            
            foreach (var kvp in allResults)
            {
                var fileName = kvp.Key;
                var products = kvp.Value;
                var saleCount = products.Count(p => p.IsOnSale);
                
                totalProducts += products.Count;
                totalSaleItems += saleCount;
                
                Console.WriteLine($"\n{fileName}:");
                Console.WriteLine($"  Total products: {products.Count}");
                Console.WriteLine($"  Sale items: {saleCount}");
                Console.WriteLine($"  Regular items: {products.Count - saleCount}");
            }
            
            Console.WriteLine($"\nGRAND TOTAL:");
            Console.WriteLine($"  Total products: {totalProducts}");
            Console.WriteLine($"  Sale items: {totalSaleItems}");
            Console.WriteLine($"  Regular items: {totalProducts - totalSaleItems}");

            // Save all results to files
            SaveAllResultsToFiles(allResults);

            // Option 2: Process a specific file manually
            // var seafoodProducts = await factory.ProcessFileAsync("sites/NovusLinks_Seafood.txt");
            // Console.WriteLine($"Found {seafoodProducts.Count} seafood products");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    /// <summary>
    /// Saves scraping results to separate files per category
    /// </summary>
    static void SaveAllResultsToFiles(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Product>> results)
    {
        // Create output directory if it doesn't exist
        var outputDir = "output";
        Directory.CreateDirectory(outputDir);

        foreach (var kvp in results)
        {
            var fileName = kvp.Key;
            var products = kvp.Value;
            
            // Create output filename based on input filename
            var outputFileName = Path.Combine(outputDir, 
                Path.GetFileNameWithoutExtension(fileName) + "_products.txt");
            
            SaveProductsToFile(products, outputFileName);
        }

        // Also create a combined file with all products
        var allProducts = results.Values.SelectMany(p => p).ToList();
        SaveProductsToFile(allProducts, Path.Combine(outputDir, "all_products.txt"));
        
    }

    /// <summary>
    /// Saves products to a text file
    /// </summary>
    static void SaveProductsToFile(System.Collections.Generic.List<Product> products, string filename)
    {
        using var writer = new StreamWriter(filename);
        
        writer.WriteLine($"Total Products: {products.Count}");
        writer.WriteLine($"Generated: {DateTime.Now}");
        writer.WriteLine(new string('=', 80));
        writer.WriteLine();

        foreach (var product in products)
        {
            if (product.IsOnSale)
            {
                writer.WriteLine($"[SALE] {product.Name}");
                writer.WriteLine($"  Category: {product.Category ?? "Unknown"}");
                writer.WriteLine($"  Old Price: {product.OldPrice} ₴");
                writer.WriteLine($"  New Price: {product.Price} ₴");
                writer.WriteLine($"  Discount: {product.Discount}");
                writer.WriteLine($"  Valid Until: {product.ValidUntil}");
            }
            else
            {
                writer.WriteLine($"{product.Name}");
                writer.WriteLine($"  Category: {product.Category ?? "Unknown"}");
                writer.WriteLine($"  Price: {product.Price} ₴");
            }
            writer.WriteLine();
        }
        
        Console.WriteLine($"Products saved to {filename}");
    }
}