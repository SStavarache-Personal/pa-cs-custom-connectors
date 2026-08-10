using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: SqliteDatabaseRunner <operation-id> <request-json-path> [response-json-path]");
            return 2;
        }

        string operationId = args[0];
        string body = File.ReadAllText(Path.GetFullPath(args[1]), Encoding.UTF8);
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/sqlite")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
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
        if (args.Length > 2) File.WriteAllText(Path.GetFullPath(args[2]), responseJson + Environment.NewLine, new UTF8Encoding(false));
        else Console.Write(responseJson);
        Console.Error.WriteLine(new JObject { ["statusCode"] = (int)response.StatusCode, ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds }.ToString(Newtonsoft.Json.Formatting.None));
        return response.IsSuccessStatusCode ? 0 : 1;
    }
}
