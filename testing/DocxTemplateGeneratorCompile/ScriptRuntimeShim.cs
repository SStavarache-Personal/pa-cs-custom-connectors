using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

// Mirrors the supporting classes published in "Use custom code in a custom
// connector" (learn.microsoft.com/connectors/custom-connectors/write-code).
public abstract class ScriptBase
{
    public IScriptContext Context { get; }

    public CancellationToken CancellationToken { get; }

    public static StringContent CreateJsonContent(string serializedJson)
    {
        return new StringContent(serializedJson, Encoding.UTF8, "application/json");
    }

    public abstract Task<HttpResponseMessage> ExecuteAsync();
}

public interface IScriptContext
{
    string CorrelationId { get; }

    string OperationId { get; }

    HttpRequestMessage Request { get; }

    ILogger Logger { get; }

    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
