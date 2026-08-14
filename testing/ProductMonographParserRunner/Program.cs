using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
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
            Console.Error.WriteLine("Usage: ProductMonographParserRunner <page-json-path> [output-path|-] [source-url]");
            return 2;
        }

        string inputPath = Path.GetFullPath(args[0]);
        string outputPath = args.Length > 1 && args[1] != "-" ? Path.GetFullPath(args[1]) : null;
        string sourceUrl = args.Length > 2 ? args[2] : null;

        JToken input;
        try
        {
            input = JToken.Parse(File.ReadAllText(inputPath));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not read page JSON: " + ex.Message);
            return 2;
        }

        JArray pages = input as JArray ?? input["pages"] as JArray;
        if (pages == null)
        {
            Console.Error.WriteLine("Input must be a page array or an object containing pages.");
            return 2;
        }

        var body = new JObject { ["pages"] = pages };
        if (!string.IsNullOrWhiteSpace(sourceUrl)) body["sourceUrl"] = sourceUrl;

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/monographs/parse")
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        var script = new Script
        {
            Context = new LocalScriptContext { OperationId = "ParseProductMonograph", Request = request },
            CancellationToken = CancellationToken.None
        };

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response = await script.ExecuteAsync().ConfigureAwait(false);
        string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        stopwatch.Stop();

        if (outputPath == null)
        {
            Console.Write(responseJson);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, JToken.Parse(responseJson).ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));
        }

        JObject parsed = null;
        try { parsed = JObject.Parse(responseJson); }
        catch { }

        var summary = new JObject
        {
            ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds,
            ["statusCode"] = (int)response.StatusCode,
            ["documentStatus"] = parsed == null ? null : parsed["documentStatus"],
            ["language"] = parsed == null ? null : parsed["language"]?["value"],
            ["controlNumber"] = parsed == null ? null : parsed["controlNumber"]?["value"],
            ["templateFamily"] = parsed == null ? null : parsed["template"]?["family"],
            ["initialAuthorization"] = parsed == null ? null : parsed["dates"]?["initialAuthorization"]?["isoValue"],
            ["revision"] = parsed == null ? null : parsed["dates"]?["revision"]?["isoValue"]
        };

        if (parsed != null && parsed["sections"] is JObject sections)
        {
            summary["sectionHashes"] = new JObject
            {
                ["indications"] = HashSection(sections["indications"]),
                ["pediatricsPrimary"] = HashSection(sections["pediatrics"]?["primary"]),
                ["geriatricsPrimary"] = HashSection(sections["geriatrics"]?["primary"]),
                ["contraindications"] = HashSection(sections["contraindications"])
            };
        }

        Console.Error.WriteLine(summary.ToString(Formatting.None));
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static JToken HashSection(JToken section)
    {
        if (section == null || (string)section["status"] == "notFound") return JValue.CreateNull();
        string markdown = (string)section["rawMarkdown"] ?? string.Empty;
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(markdown));
            return new JValue(BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant());
        }
    }
}
