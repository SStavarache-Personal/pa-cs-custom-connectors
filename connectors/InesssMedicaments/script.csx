// ============================================================================
// INESSS Drug Evaluations Scraper - Custom Connector C# Script
// ============================================================================
// Scrapes tabular data from the INESSS medication evaluation page:
//   https://www.inesss.qc.ca/thematiques/medicaments/
//   medicaments-evaluation-aux-fins-dinscription.html
//
// Operations:
//   GetGeneriques      - Plan travail génériques (generic drugs)
//   GetInnovateurs     - Plan travail innovateurs (innovator drugs)
//   GetAutresTravaux   - Autres travaux (other work items)
//   GetSollicitations  - Sollicitations (manufacturer solicitations)
//   GetProduitsEvalues - Produits évalués (evaluated products, paginated)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    // Base URL for the INESSS medication evaluation page
    private const string BASE_URL =
        "https://www.inesss.qc.ca/thematiques/medicaments/medicaments-evaluation-aux-fins-dinscription.html";
    private const string SITE_ORIGIN = "https://www.inesss.qc.ca";

    // Tab panel IDs within the page HTML
    private const string PANEL_GENERIQUES = "jfmulticontent_c767-1";
    private const string PANEL_INNOVATEURS = "jfmulticontent_c767-2";
    private const string PANEL_AUTRES = "jfmulticontent_c767-3";
    private const string PANEL_SOLLICITATIONS = "jfmulticontent_c767-4";
    private const string PANEL_PRODUITS_EVALUES = "jfmulticontent_c767-5";

    // Pre-compiled regex patterns for performance
    private static readonly Regex RowRegex =
        new Regex(@"<tr[^>]*>([\s\S]*?)</tr>", RegexOptions.Compiled);
    private static readonly Regex CellRegex =
        new Regex(@"<td[^>]*?data-mobile=""([^""]*?)""[^>]*>([\s\S]*?)</td>",
            RegexOptions.Compiled);
    private static readonly Regex LinkRegex =
        new Regex(@"<a\s[^>]*?href=""([^""]*?)""[^>]*>([\s\S]*?)</a>",
            RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex =
        new Regex(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex HtmlCommentRegex =
        new Regex(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex =
        new Regex(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PaginationInfoRegex =
        new Regex(@"(\d+)\s+à\s+(\d+)\s+de\s+(\d+)\s+résultats",
            RegexOptions.Compiled);

    /// <summary>
    /// Entry point called by Power Platform runtime.
    /// </summary>
    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);

        return operationId switch
        {
            "GetGeneriques" => await HandleGetGeneriquesAsync(),
            "GetInnovateurs" => await HandleGetInnovateursAsync(),
            "GetAutresTravaux" => await HandleGetAutresTravauxAsync(),
            "GetSollicitations" => await HandleGetSollicitationsAsync(),
            "GetProduitsEvalues" => await HandleGetProduitsEvaluesAsync(),
            _ => CreateErrorResponse(
                HttpStatusCode.BadRequest,
                $"Unknown operation: {operationId}",
                "UNKNOWN_OPERATION"
            )
        };
    }

    // ========================================================================
    // OPERATION HANDLERS
    // ========================================================================

    /// <summary>
    /// Scrapes the Plan travail génériques table.
    /// Columns: Nom commercial, Dénomination commune, Nom du fabricant
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetGeneriquesAsync()
    {
        try
        {
            string html = await FetchPageAsync(BASE_URL);
            string tableHtml = ExtractTableFromPanel(html, PANEL_GENERIQUES);

            if (string.IsNullOrEmpty(tableHtml))
            {
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Could not find the Génériques table in the page.",
                    "TABLE_NOT_FOUND");
            }

            var rows = ParseTableRows(tableHtml);
            var items = new JArray();

            foreach (var row in rows)
            {
                var item = new JObject
                {
                    ["nomCommercial"] = GetCellText(row, "Nom commercial"),
                    ["denominationCommune"] = GetCellText(row, "Dénomination commune"),
                    ["nomFabricant"] = GetCellText(row, "Nom du fabricant")
                };
                items.Add(item);
            }

            var response = new JObject
            {
                ["tableName"] = "Plan travail génériques",
                ["count"] = items.Count,
                ["scrapedAt"] = DateTime.UtcNow.ToString("o"),
                ["items"] = items
            };

            return CreateJsonResponse(HttpStatusCode.OK, response);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError,
                $"Failed to scrape Génériques: {ex.Message}",
                "SCRAPE_ERROR");
        }
    }

    /// <summary>
    /// Scrapes the Plan travail innovateurs table.
    /// Columns: Nom commercial, Dénomination commune, Nom du fabricant,
    ///          Indication, Type de demande, **Statut de la demande,
    ///          Date limite pour commentaires
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetInnovateursAsync()
    {
        try
        {
            string html = await FetchPageAsync(BASE_URL);
            string tableHtml = ExtractTableFromPanel(html, PANEL_INNOVATEURS);

            if (string.IsNullOrEmpty(tableHtml))
            {
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Could not find the Innovateurs table in the page.",
                    "TABLE_NOT_FOUND");
            }

            var rows = ParseTableRows(tableHtml);
            var items = new JArray();

            foreach (var row in rows)
            {
                var item = new JObject
                {
                    ["nomCommercial"] = GetCellText(row, "Nom commercial"),
                    ["denominationCommune"] = GetCellText(row, "Dénomination commune"),
                    ["nomFabricant"] = GetCellText(row, "Nom du fabricant"),
                    ["indication"] = GetCellText(row, "Indication"),
                    ["typeDemande"] = GetCellText(row, "Type de demande"),
                    ["statutDemande"] = GetCellTextFuzzy(row, "Statut de la demande"),
                    ["dateLimiteCommentaires"] =
                        GetCellText(row, "Date limite pour commentaires")
                };
                items.Add(item);
            }

            var response = new JObject
            {
                ["tableName"] = "Plan travail innovateurs",
                ["count"] = items.Count,
                ["scrapedAt"] = DateTime.UtcNow.ToString("o"),
                ["items"] = items
            };

            return CreateJsonResponse(HttpStatusCode.OK, response);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError,
                $"Failed to scrape Innovateurs: {ex.Message}",
                "SCRAPE_ERROR");
        }
    }

    /// <summary>
    /// Scrapes the Autres travaux table.
    /// Columns: Projet, Sujet, Début des travaux, Date limite pour commentaires
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetAutresTravauxAsync()
    {
        try
        {
            string html = await FetchPageAsync(BASE_URL);
            string tableHtml = ExtractTableFromPanel(html, PANEL_AUTRES);

            if (string.IsNullOrEmpty(tableHtml))
            {
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Could not find the Autres travaux table in the page.",
                    "TABLE_NOT_FOUND");
            }

            var rows = ParseTableRows(tableHtml);
            var items = new JArray();

            foreach (var row in rows)
            {
                var item = new JObject
                {
                    ["projet"] = GetCellText(row, "Projet"),
                    ["sujet"] = GetCellText(row, "Sujet"),
                    ["debutTravaux"] = GetCellText(row, "Début des travaux"),
                    ["dateLimiteCommentaires"] =
                        GetCellText(row, "Date limite pour commentaires")
                };
                items.Add(item);
            }

            var response = new JObject
            {
                ["tableName"] = "Autres travaux",
                ["count"] = items.Count,
                ["scrapedAt"] = DateTime.UtcNow.ToString("o"),
                ["items"] = items
            };

            return CreateJsonResponse(HttpStatusCode.OK, response);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError,
                $"Failed to scrape Autres travaux: {ex.Message}",
                "SCRAPE_ERROR");
        }
    }

    /// <summary>
    /// Scrapes the Sollicitations table.
    /// Columns: Nom commercial, Dénomination commune, Nom du fabricant,
    ///          Indication, Type de demande, Statut de la demande,
    ///          Date de sollicitation
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetSollicitationsAsync()
    {
        try
        {
            string html = await FetchPageAsync(BASE_URL);
            string tableHtml = ExtractTableFromPanel(html, PANEL_SOLLICITATIONS);

            if (string.IsNullOrEmpty(tableHtml))
            {
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Could not find the Sollicitations table in the page.",
                    "TABLE_NOT_FOUND");
            }

            var rows = ParseTableRows(tableHtml);
            var items = new JArray();

            foreach (var row in rows)
            {
                var item = new JObject
                {
                    ["nomCommercial"] = GetCellText(row, "Nom commercial"),
                    ["denominationCommune"] = GetCellText(row, "Dénomination commune"),
                    ["nomFabricant"] = GetCellText(row, "Nom du fabricant"),
                    ["indication"] = GetCellText(row, "Indication"),
                    ["typeDemande"] = GetCellText(row, "Type de demande"),
                    ["statutDemande"] = GetCellTextFuzzy(row, "Statut de la demande"),
                    ["dateSollicitation"] =
                        GetCellTextFuzzy(row, "Date de sollicitation")
                };
                items.Add(item);
            }

            var response = new JObject
            {
                ["tableName"] = "Sollicitations",
                ["count"] = items.Count,
                ["scrapedAt"] = DateTime.UtcNow.ToString("o"),
                ["items"] = items
            };

            return CreateJsonResponse(HttpStatusCode.OK, response);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError,
                $"Failed to scrape Sollicitations: {ex.Message}",
                "SCRAPE_ERROR");
        }
    }

    /// <summary>
    /// Scrapes the Produits évalués table (paginated, 30 per page).
    /// Columns: Date, Nom commercial / Projet, Dénomination commune / Sujet,
    ///          Fabricant, Recommandation de l'INESSS, Décision du Ministre
    /// Links embedded in Nom commercial and Dénomination commune cells are
    /// extracted and returned as separate fields.
    /// </summary>
    private async Task<HttpResponseMessage> HandleGetProduitsEvaluesAsync()
    {
        try
        {
            // Extract page number from query string
            int page = 1;
            var query = HttpUtility.ParseQueryString(
                this.Context.Request.RequestUri.Query);
            string pageParam = query["page"];
            if (!string.IsNullOrEmpty(pageParam) &&
                int.TryParse(pageParam, out int parsedPage) &&
                parsedPage >= 1)
            {
                page = parsedPage;
            }

            // Build URL: page 1 is the base URL; page 2+ use tx_solr[page]=N
            // The INESSS site uses 0-based Solr pagination internally but
            // displays 1-based pages. tx_solr[page]=2 shows results 31-60.
            string url = page <= 1
                ? BASE_URL
                : $"{BASE_URL}?tx_solr%5Bpage%5D={page}";

            string html = await FetchPageAsync(url);
            string tableHtml = ExtractTableFromPanel(html, PANEL_PRODUITS_EVALUES);

            if (string.IsNullOrEmpty(tableHtml))
            {
                return CreateErrorResponse(HttpStatusCode.InternalServerError,
                    "Could not find the Produits évalués table in the page.",
                    "TABLE_NOT_FOUND");
            }

            // Extract total results count from footer text
            int totalResults = ExtractTotalResults(html, PANEL_PRODUITS_EVALUES);

            var rows = ParseTableRows(tableHtml);
            var items = new JArray();

            foreach (var row in rows)
            {
                // Extract cells that may contain links
                var nomInfo = GetCellWithLink(row, "Nom commercial / Projet");
                var denomInfo = GetCellWithLink(row,
                    "Dénomination commune / Sujet");

                var item = new JObject
                {
                    ["date"] = GetCellText(row, "Date"),
                    ["nomCommercialProjet"] = nomInfo.text,
                    ["nomCommercialProjetLink"] = nomInfo.link ?? "",
                    ["denominationCommuneSujet"] = denomInfo.text,
                    ["denominationCommuneSujetLink"] = denomInfo.link ?? "",
                    ["fabricant"] = GetCellText(row, "Fabricant"),
                    ["recommandationInesss"] =
                        GetCellTextFuzzy(row, "Recommandation"),
                    ["decisionMinistre"] =
                        GetCellTextFuzzy(row, "Décision du Ministre")
                };
                items.Add(item);
            }

            var response = new JObject
            {
                ["tableName"] = "Produits évalués",
                ["page"] = page,
                ["totalResults"] = totalResults,
                ["resultsPerPage"] = 30,
                ["count"] = items.Count,
                ["scrapedAt"] = DateTime.UtcNow.ToString("o"),
                ["items"] = items
            };

            return CreateJsonResponse(HttpStatusCode.OK, response);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(HttpStatusCode.InternalServerError,
                $"Failed to scrape Produits évalués: {ex.Message}",
                "SCRAPE_ERROR");
        }
    }

    // ========================================================================
    // HTML FETCHING
    // ========================================================================

    /// <summary>
    /// Fetches the HTML content of the given URL.
    /// </summary>
    private async Task<string> FetchPageAsync(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Headers.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/120.0.0.0 Safari/537.36");
        request.Headers.Add("Accept-Language", "fr-CA,fr;q=0.9,en;q=0.5");
        request.Headers.Add("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

        HttpResponseMessage response = await this.Context.SendAsync(
            request, this.CancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"HTTP {(int)response.StatusCode} fetching {url}");
        }

        return await response.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);
    }

    // ========================================================================
    // HTML PARSING - TABLE EXTRACTION
    // ========================================================================

    /// <summary>
    /// Extracts the <table>...</table> HTML within a specific tab panel.
    /// Locates the panel by its ID attribute, then finds the first table.
    /// </summary>
    private string ExtractTableFromPanel(string html, string panelId)
    {
        string marker = $"id=\"{panelId}\"";
        int panelStart = html.IndexOf(marker, StringComparison.Ordinal);
        if (panelStart < 0) return null;

        // Find the next panel or end-of-panels to bound our search
        // Panels are numbered sequentially: c767-1 through c767-5
        string nextPanelPrefix = "id=\"jfmulticontent_c767-";
        int searchFrom = panelStart + marker.Length;
        int panelEnd = html.IndexOf(nextPanelPrefix, searchFrom,
            StringComparison.Ordinal);
        if (panelEnd < 0) panelEnd = html.Length;

        // Extract the section for this panel
        string panelHtml = html.Substring(panelStart,
            panelEnd - panelStart);

        // Find the table within this panel
        int tableStart = panelHtml.IndexOf("<table",
            StringComparison.OrdinalIgnoreCase);
        if (tableStart < 0) return null;

        int tableEnd = panelHtml.IndexOf("</table>", tableStart,
            StringComparison.OrdinalIgnoreCase);
        if (tableEnd < 0) return null;

        return panelHtml.Substring(tableStart,
            tableEnd + "</table>".Length - tableStart);
    }

    /// <summary>
    /// Parses all data rows from a table's tbody.
    /// Returns a list of dictionaries mapping column name to raw cell HTML.
    /// Cells with data-mobile attributes use those as keys. When absent
    /// (e.g. the Sollicitations table), column names are extracted from
    /// thead headers and mapped by position.
    /// </summary>
    private List<Dictionary<string, string>> ParseTableRows(string tableHtml)
    {
        var results = new List<Dictionary<string, string>>();

        // Extract header names from <thead> for positional fallback
        var headerNames = new List<string>();
        int theadStart = tableHtml.IndexOf("<thead",
            StringComparison.OrdinalIgnoreCase);
        if (theadStart >= 0)
        {
            int theadEnd = tableHtml.IndexOf("</thead>", theadStart,
                StringComparison.OrdinalIgnoreCase);
            if (theadEnd >= 0)
            {
                string theadHtml = tableHtml.Substring(theadStart,
                    theadEnd - theadStart);
                var thRegex = new Regex(@"<th[^>]*>([\s\S]*?)</th>");
                foreach (Match thMatch in thRegex.Matches(theadHtml))
                {
                    string headerText = CleanCellText(thMatch.Groups[1].Value);
                    headerNames.Add(NormalizeColumnName(headerText));
                }
            }
        }

        // Extract tbody content
        int tbodyStart = tableHtml.IndexOf("<tbody",
            StringComparison.OrdinalIgnoreCase);
        if (tbodyStart < 0) return results;

        int tbodyEnd = tableHtml.IndexOf("</tbody>", tbodyStart,
            StringComparison.OrdinalIgnoreCase);
        if (tbodyEnd < 0) tbodyEnd = tableHtml.Length;

        string tbodyHtml = tableHtml.Substring(tbodyStart,
            tbodyEnd - tbodyStart);

        // Match each <tr>...</tr>
        foreach (Match rowMatch in RowRegex.Matches(tbodyHtml))
        {
            string rowHtml = rowMatch.Groups[1].Value;
            var rowData = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

            // Match each <td data-mobile="ColumnName">content</td>
            foreach (Match cellMatch in CellRegex.Matches(rowHtml))
            {
                string columnName = cellMatch.Groups[1].Value.Trim();
                string cellContent = cellMatch.Groups[2].Value;

                // Normalize the column name (remove extra spaces, asterisks)
                columnName = NormalizeColumnName(columnName);

                rowData[columnName] = cellContent;
            }

            // Fallback: cells without data-mobile — use <thead> headers
            if (rowData.Count == 0)
            {
                var simpleCellRegex = new Regex(
                    @"<td[^>]*>([\s\S]*?)</td>");
                int idx = 0;
                foreach (Match cellMatch in simpleCellRegex.Matches(rowHtml))
                {
                    string key = idx < headerNames.Count
                        ? headerNames[idx]
                        : $"col{idx}";
                    rowData[key] = cellMatch.Groups[1].Value;
                    idx++;
                }
            }

            if (rowData.Count > 0)
            {
                results.Add(rowData);
            }
        }

        return results;
    }

    // ========================================================================
    // CELL CONTENT EXTRACTION
    // ========================================================================

    /// <summary>
    /// Gets clean text from a cell, stripping HTML tags and comments.
    /// Uses exact column name match.
    /// </summary>
    private string GetCellText(Dictionary<string, string> row,
        string columnName)
    {
        string normalized = NormalizeColumnName(columnName);
        if (!row.TryGetValue(normalized, out string rawHtml))
            return "";

        return CleanCellText(rawHtml);
    }

    /// <summary>
    /// Gets clean text using a fuzzy (contains) match on column name.
    /// Useful when column names have extra characters like ** or whitespace.
    /// </summary>
    private string GetCellTextFuzzy(Dictionary<string, string> row,
        string partialColumnName)
    {
        // First try exact normalized match
        string normalized = NormalizeColumnName(partialColumnName);
        if (row.TryGetValue(normalized, out string rawHtml))
            return CleanCellText(rawHtml);

        // Fuzzy: find first key containing the partial name
        string lowerPartial = normalized.ToLowerInvariant();
        foreach (var kvp in row)
        {
            if (kvp.Key.ToLowerInvariant().Contains(lowerPartial))
                return CleanCellText(kvp.Value);
        }

        return "";
    }

    /// <summary>
    /// Extracts cell text and any embedded link from a cell.
    /// Returns (text, absoluteUrl) tuple.
    /// </summary>
    private (string text, string link) GetCellWithLink(
        Dictionary<string, string> row, string columnName)
    {
        string normalized = NormalizeColumnName(columnName);
        if (!row.TryGetValue(normalized, out string rawHtml))
        {
            // Try fuzzy match
            string lowerPartial = normalized.ToLowerInvariant();
            rawHtml = null;
            foreach (var kvp in row)
            {
                if (kvp.Key.ToLowerInvariant().Contains(lowerPartial))
                {
                    rawHtml = kvp.Value;
                    break;
                }
            }
            if (rawHtml == null) return ("", null);
        }

        // Check for an <a href="..."> link
        Match linkMatch = LinkRegex.Match(rawHtml);
        if (linkMatch.Success)
        {
            string href = HttpUtility.HtmlDecode(
                linkMatch.Groups[1].Value.Trim());
            string linkText = CleanCellText(linkMatch.Groups[2].Value);

            // Make URL absolute if relative
            if (!string.IsNullOrEmpty(href) && href.StartsWith("/"))
            {
                href = SITE_ORIGIN + href;
            }

            return (linkText, href);
        }

        // No link found, return just the cleaned text
        return (CleanCellText(rawHtml), null);
    }

    /// <summary>
    /// Cleans raw HTML cell content to plain text.
    /// Strips comments, HTML tags, decodes entities, normalizes whitespace.
    /// </summary>
    private string CleanCellText(string rawHtml)
    {
        if (string.IsNullOrEmpty(rawHtml)) return "";

        // Remove HTML comments (e.g. <!--###LIEN_FICHE###-->)
        string text = HtmlCommentRegex.Replace(rawHtml, "");

        // Remove HTML tags
        text = HtmlTagRegex.Replace(text, " ");

        // Decode HTML entities (&amp; -> &, &#039; -> ', etc.)
        text = HttpUtility.HtmlDecode(text);

        // Normalize whitespace (collapse multiple spaces/newlines)
        text = WhitespaceRegex.Replace(text, " ");

        return text.Trim();
    }

    /// <summary>
    /// Normalizes a column name by removing asterisks and extra whitespace.
    /// </summary>
    private string NormalizeColumnName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";

        // Remove leading asterisks (e.g. "**Statut de la demande")
        name = name.TrimStart('*');

        // Collapse whitespace
        name = WhitespaceRegex.Replace(name, " ");

        return name.Trim();
    }

    // ========================================================================
    // PAGINATION HELPERS
    // ========================================================================

    /// <summary>
    /// Extracts total result count from the "N à M de TOTAL résultats" text
    /// in the Produits évalués panel footer.
    /// </summary>
    private int ExtractTotalResults(string html, string panelId)
    {
        // Find the panel section
        string marker = $"id=\"{panelId}\"";
        int panelStart = html.IndexOf(marker, StringComparison.Ordinal);
        if (panelStart < 0) return 0;

        // Search for the results footer text within a reasonable range
        int searchEnd = Math.Min(panelStart + 500000, html.Length);
        string section = html.Substring(panelStart,
            searchEnd - panelStart);

        Match m = PaginationInfoRegex.Match(section);
        if (m.Success && int.TryParse(m.Groups[3].Value, out int total))
        {
            return total;
        }

        return 0;
    }

    // ========================================================================
    // COMMON HELPERS
    // ========================================================================

    /// <summary>
    /// Decodes OperationId, handling base64 encoding in some regions.
    /// </summary>
    private static string DecodeOperationId(string operationId)
    {
        try
        {
            byte[] data = Convert.FromBase64String(operationId);
            string decoded = Encoding.UTF8.GetString(data);

            // Verify the decoded string contains only printable characters.
            // Some operationIds (e.g. "GetAutresTravaux") are valid base64
            // by coincidence but decode to garbage binary data.
            foreach (char c in decoded)
            {
                if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                {
                    return operationId;
                }
            }

            return decoded;
        }
        catch (FormatException)
        {
            return operationId;
        }
    }

    /// <summary>
    /// Creates a JSON HTTP response.
    /// </summary>
    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode,
        JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = CreateJsonContent(body.ToString())
        };
    }

    /// <summary>
    /// Creates a standardized error response.
    /// </summary>
    private HttpResponseMessage CreateErrorResponse(HttpStatusCode statusCode,
        string message, string code)
    {
        var errorBody = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        return CreateJsonResponse(statusCode, errorBody);
    }
}
