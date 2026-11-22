using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Playwright;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class DemoTools
{
    [McpServerTool(Name = "add_numbers")]
    [Description("Add two numbers accurately. Only call when the user explicitly asks for addition and supplies two numbers. Never use for greetings or general questions.")]
    public static string AddNumbers(
        [Description("The first number.")] decimal a,
        [Description("The second number.")] decimal b)
        => (a + b).ToString(CultureInfo.InvariantCulture);

    [McpServerTool(Name = "get_store_hours")]
    [Description("Look up the opening hours of the fictional Learning Bookshop. Only call when the user asks about this bookshop's opening hours. Never use for greetings or general questions.")]
    public static string GetStoreHours()
        => "DEMO DATA: Learning Bookshop opens Monday-Friday 10:15-18:45, "
            + "Saturday 11:00-16:30, and is closed Sunday. These are fictional hours, not live business data.";

    [McpServerTool(Name = "search_web")]
    [Description("Search the web using Bing. Use when the user asks to search the web or needs factual information you are unsure about. Returns search result titles, snippets and links, not full pages.")]
    public static async Task<string> SearchWeb(
        [Description("A short search phrase describing the topic.")] string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200)
            throw new McpException("Search query must contain 1 to 200 characters.");

        using var playwright = await Playwright.CreateAsync().WaitAsync(cancellationToken);
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }).WaitAsync(cancellationToken);
        }
        catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException(
                "Playwright Chromium is not installed. Run the browser installation command in the README.", ex);
        }

        await using (browser)
        {
            var page = await browser.NewPageAsync().WaitAsync(cancellationToken);
            IResponse? response;
            try
            {
                response = await page.GotoAsync(
                    "https://www.bing.com/search?q=" + Uri.EscapeDataString(query.Trim()) + "&setlang=en",
                    new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 }).WaitAsync(cancellationToken);
            }
            catch (PlaywrightException ex)
            {
                Console.Error.WriteLine($"Bing search navigation failed: {ex.Message}");
                throw new McpException("Could not load Bing search. Check your internet connection and try again.", ex);
            }

            if (response is null || !response.Ok)
                throw new McpException($"Bing search failed (HTTP {response?.Status.ToString() ?? "no response"}). It may be blocking automated requests.");

            var results = await page.Locator("#b_results li.b_algo").EvaluateAllAsync<JsonElement>(
                """
                items => items.map(item => {
                    const link = item.querySelector('h2 a[href]');
                    return {
                        title: link?.textContent.trim().slice(0, 200) ?? '',
                        url: link?.href ?? '',
                        snippet: (item.querySelector('.b_caption p, p')?.textContent ?? '').trim().slice(0, 500)
                    };
                }).filter(result => result.title && /^https?:\/\//i.test(result.url)).slice(0, 5)
                """).WaitAsync(cancellationToken);
            if (results.GetArrayLength() == 0)
                throw new McpException("Bing returned no readable search results. There may be no matches, the page may have changed, or Bing may be blocking the search.");
            return results.GetRawText();
        }
    }
}
