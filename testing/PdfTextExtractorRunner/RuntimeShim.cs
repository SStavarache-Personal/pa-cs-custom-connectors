using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public abstract class ScriptBase
{
    public IScriptContext Context { get; set; }
    public CancellationToken CancellationToken { get; set; }

    public static StringContent CreateJsonContent(string serializedJson)
    {
        return new StringContent(serializedJson, Encoding.UTF8, "application/json");
    }

    public abstract Task<HttpResponseMessage> ExecuteAsync();
}

public interface IScriptContext
{
    string OperationId { get; }
    HttpRequestMessage Request { get; }
}

public sealed class LocalScriptContext : IScriptContext
{
    public string OperationId { get; set; }
    public HttpRequestMessage Request { get; set; }
}
