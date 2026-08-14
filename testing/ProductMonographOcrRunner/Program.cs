using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: ProductMonographOcrRunner <pdf-path> [output-path] [start-page] [max-pages] [language-hint] [minimum-confidence]");
            return 2;
        }

        string pdfPath = Path.GetFullPath(args[0]);
        string outputPath = args.Length > 1 && args[1] != "-" ? Path.GetFullPath(args[1]) : null;
        byte[] bytes = File.ReadAllBytes(pdfPath);
        var body = new JObject
        {
            ["contentBytes"] = Convert.ToBase64String(bytes),
            ["fileName"] = Path.GetFileName(pdfPath),
            ["startPage"] = args.Length > 2 ? int.Parse(args[2]) : 1,
            ["maxPagesPerCall"] = args.Length > 3 ? int.Parse(args[3]) : 2,
            ["languageHint"] = args.Length > 4 ? args[4] : "auto",
            ["minimumConfidence"] = args.Length > 5 ? double.Parse(args[5]) : 0.86,
            ["maxOutputCharacters"] = 6 * 1024 * 1024
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/monographs/ocr")
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        var script = new Script
        {
            Context = new LocalScriptContext { OperationId = "ExtractProductMonographOcr", Request = request },
            CancellationToken = CancellationToken.None
        };

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await script.ExecuteAsync().ConfigureAwait(false);
        string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(responseJson);
            return 1;
        }

        JObject parsed = JObject.Parse(responseJson);
        string formatted = parsed.ToString(Formatting.Indented) + Environment.NewLine;
        if (outputPath == null) Console.Write(formatted);
        else File.WriteAllText(outputPath, formatted, new UTF8Encoding(false));
        Console.Error.WriteLine(new JObject
        {
            ["wallMilliseconds"] = stopwatch.ElapsedMilliseconds,
            ["connectorMilliseconds"] = parsed["elapsedMilliseconds"],
            ["pageCount"] = parsed["pageCount"],
            ["pagesReturned"] = parsed["pagesReturned"],
            ["nextStartPage"] = parsed["nextStartPage"],
            ["statuses"] = new JArray(parsed["pages"] is JArray pages ? pages.Select(page => page["ocrStatus"]) : new JToken[0])
        }.ToString(Formatting.None));
        return 0;
    }
}
