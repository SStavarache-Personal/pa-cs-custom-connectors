using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConnectorTestKit;

public abstract class ScriptBase
{
    public ConnectorRuntimeContext Context { get; private set; } = null!;

    public CancellationToken CancellationToken { get; private set; }

    public void Initialize(ConnectorRuntimeContext context, CancellationToken cancellationToken)
    {
        Context = context;
        CancellationToken = cancellationToken;
    }

    protected static StringContent CreateJsonContent(string content)
    {
        return new StringContent(content, Encoding.UTF8, "application/json");
    }

    public abstract Task<HttpResponseMessage> ExecuteAsync();
}