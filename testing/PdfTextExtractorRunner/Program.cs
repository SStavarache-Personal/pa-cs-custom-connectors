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
            Console.Error.WriteLine("Usage: PdfTextExtractorRunner <pdf-path> [include-artifacts] [output-path] [start-page] [end-page] [max-output-characters] [operation-id] [file-name]");
            return 2;
        }

        string pdfPath = Path.GetFullPath(args[0]);
        bool includeArtifacts = args.Length > 1 && bool.Parse(args[1]);
        string outputPath = args.Length > 2 && args[2] != "-" ? Path.GetFullPath(args[2]) : null;
        string operationId = args.Length > 6 ? args[6] : "ExtractPdfText";
        string fileName = args.Length > 7 ? args[7] : Path.GetFileName(pdfPath);
        byte[] bytes = File.ReadAllBytes(pdfPath);
        var body = new JObject
        {
            ["contentBytes"] = Convert.ToBase64String(bytes),
            ["includeArtifacts"] = includeArtifacts,
            ["includePageBreaks"] = true,
            ["fileName"] = fileName,
            ["maxOutputCharacters"] = 6 * 1024 * 1024
        };
        if (args.Length > 3) body["startPage"] = int.Parse(args[3]);
        if (args.Length > 4 && !string.Equals(args[4], "null", StringComparison.OrdinalIgnoreCase)) body["endPage"] = int.Parse(args[4]);
        if (args.Length > 5) body["maxOutputCharacters"] = int.Parse(args[5]);
        bool isPageChunks = string.Equals(operationId, "ExtractPdfPageChunks", StringComparison.Ordinal) ||
            string.Equals(operationId, "ExtractPdfMarkdownPageChunks", StringComparison.Ordinal);
        string relativePath = string.Equals(operationId, "ExtractPdfMarkdownPageChunks", StringComparison.Ordinal)
            ? "/pdf/extract-markdown-page-chunks"
            : string.Equals(operationId, "ExtractPdfMarkdown", StringComparison.Ordinal)
                ? "/pdf/extract-markdown"
                : isPageChunks ? "/pdf/extract-page-chunks" : "/pdf/extract";
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com" + relativePath)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        var script = new Script
        {
            Context = new LocalScriptContext { OperationId = operationId, Request = request },
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

        if (isPageChunks)
        {
            JArray chunks = JArray.Parse(responseJson);
            if (outputPath == null) Console.Write(chunks.ToString(Formatting.Indented));
            else File.WriteAllText(outputPath, chunks.ToString(Formatting.Indented) + Environment.NewLine, new UTF8Encoding(false));
            Console.Error.WriteLine(new JObject
            {
                ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds,
                ["pageCount"] = chunks.Count == 0 ? 0 : chunks[0]["metadata"]["pageCount"],
                ["pageChunks"] = chunks.Count,
                ["characterCount"] = chunks.Sum(item => (int)item["characterCount"]),
                ["hasTextLayer"] = chunks.Any(item => (bool)item["hasTextLayer"]),
                ["tableCount"] = chunks.Sum(item => (int?)item["tableCount"] ?? 0),
                ["usedTaggedStructure"] = chunks.Any(item => (bool?)item["usedTaggedStructure"] ?? false),
                ["truncated"] = chunks.Any(item => (bool)item["truncated"])
            }.ToString(Formatting.None));
        }
        else
        {
            JObject parsed = JObject.Parse(responseJson);
            string text = (string)parsed["text"] ?? string.Empty;
            if (outputPath == null) Console.Write(text);
            else File.WriteAllText(outputPath, text, new UTF8Encoding(false));
            Console.Error.WriteLine(new JObject
            {
                ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds,
                ["pageCount"] = parsed["pageCount"],
                ["pagesExtracted"] = parsed["pagesExtracted"],
                ["characterCount"] = parsed["characterCount"],
                ["hasTextLayer"] = parsed["hasTextLayer"],
                ["tableCount"] = parsed["tableCount"],
                ["usedTaggedStructure"] = parsed["usedTaggedStructure"],
                ["truncated"] = parsed["truncated"],
                ["warnings"] = parsed["warnings"]
            }.ToString(Formatting.None));
        }
        return 0;
    }
}
