using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Populates a caller-supplied .docx template with JSON data entirely inside the
// custom-code sandbox. The package is edited in memory with ZipArchive and
// XDocument; nothing is written to disk and no outbound request is made.
//
// Logical modules (single file because the platform accepts one script):
//   1. ConnectorEntry      - routing, request contracts, responses
//   2. ErrorsLimits        - error codes, diagnostics, resource bounds
//   3. PackageReaderWriter - ZIP/OPC preflight, secure XML, save, post-checks
//   4. TemplateTokenizer   - run-aware paragraph text model and tag grammar
//   5. TemplateRenderer    - planner/renderer for blocks, rows, scalars
//   6. CheckboxRenderer    - w14 checkbox content controls
//   7. Inventory           - ListPlaceholders/ValidateTemplate field discovery
public class Script : ScriptBase
{
    // ===================================================================
    // 1. ConnectorEntry
    // ===================================================================

    private const string OpGenerate = "GenerateDocument";
    private const string OpValidate = "ValidateTemplate";
    private const string OpList = "ListPlaceholders";
    private const string DocxMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string DefaultFileName = "Document.docx";

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string correlationId = GetCorrelationId();
        Stopwatch clock = Stopwatch.StartNew();
        try
        {
            string operation = ResolveOperationId(Context.OperationId);
            if (operation == null)
                throw new Problem("UNKNOWN_OPERATION", "The requested operation is not implemented by this connector.");
            JObject request = await ReadRequestAsync().ConfigureAwait(false);
            if (operation == OpGenerate) return HandleGenerate(request, clock);
            if (operation == OpValidate) return HandleValidate(request, clock);
            return HandleList(request, clock);
        }
        catch (Problem problem)
        {
            return ErrorResponse(problem, correlationId);
        }
        catch (OperationCanceledException)
        {
            return ErrorResponse(new Problem("RESOURCE_LIMIT_EXCEEDED", "Processing was cancelled by the platform before it completed."), correlationId);
        }
        catch (Exception ex)
        {
            // Only the exception type is logged: messages can echo document or data content.
            try { Context.Logger.LogError("DocxTemplateGenerator internal failure {ExceptionType} {CorrelationId}", ex.GetType().Name, correlationId); }
            catch (Exception) { }
            return ErrorResponse(new Problem("RENDER_FAILED", "The document could not be generated because of an internal error. Quote the correlation ID when reporting this issue."), correlationId);
        }
    }

    // Some regions deliver the operation ID base64-encoded. Every operation ID of
    // this connector is itself valid base64, so known raw IDs must win first.
    private static string ResolveOperationId(string operationId)
    {
        if (IsKnownOperation(operationId)) return operationId;
        if (String.IsNullOrEmpty(operationId)) return null;
        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(operationId));
            return IsKnownOperation(decoded) ? decoded : null;
        }
        catch (FormatException) { return null; }
    }

    private static bool IsKnownOperation(string operationId)
    {
        return operationId == OpGenerate || operationId == OpValidate || operationId == OpList;
    }

    private string GetCorrelationId()
    {
        try
        {
            string id = Context.CorrelationId;
            if (!String.IsNullOrWhiteSpace(id)) return id;
        }
        catch (Exception) { }
        return Guid.NewGuid().ToString();
    }

    private async Task<JObject> ReadRequestAsync()
    {
        if (Context.Request.Content == null)
            throw new Problem("INVALID_REQUEST", "A JSON request body is required.");
        string body = await Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (body.Length > Limits.MaxRequestChars)
            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The request body exceeds " + Limits.MaxRequestChars / (1024 * 1024) + " MiB.");
        JToken parsed;
        try { parsed = ParseJson(body); }
        catch (JsonException) { throw new Problem("INVALID_REQUEST", "The request body must be a JSON object."); }
        JObject request = parsed as JObject;
        if (request == null) throw new Problem("INVALID_REQUEST", "The request body must be a JSON object.");
        return request;
    }

    private static JToken ParseJson(string text)
    {
        using (var reader = new JsonTextReader(new StringReader(text)))
        {
            reader.MaxDepth = Limits.MaxJsonDepth;
            reader.DateParseHandling = DateParseHandling.None;
            reader.FloatParseHandling = FloatParseHandling.Decimal;
            JToken token = JToken.ReadFrom(reader);
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.Comment)
                    throw new JsonReaderException("Unexpected content after the JSON value.");
            }
            return token;
        }
    }

    private HttpResponseMessage HandleGenerate(JObject request, Stopwatch clock)
    {
        var requestWarnings = new List<Diagnostic>();
        WarnUnknownProperties(request, requestWarnings, "templateBase64", "dataJson", "fileName", "strictMode");
        byte[] template = ReadTemplate(request);
        JObject data = ReadData(request, true);
        bool strict = ReadStrict(request);
        string fileName = SanitizeFileName(request["fileName"], requestWarnings);

        DocxPackage package = DocxPackage.Load(template, clock, CancellationToken);
        var engine = new Engine(package, data, strict, false, clock, CancellationToken);
        engine.Run();
        if (engine.Errors.Count > 0) throw Problem.FromDiagnostics(engine.Errors);
        byte[] output = package.Save(clock, CancellationToken);
        package.VerifyOutput(output, strict);

        var warnings = new JArray(requestWarnings.Concat(engine.Warnings).Select(w => w.ToJson()));
        JObject statistics = engine.Stats.ToJson(package.Stories.Count, clock.ElapsedMilliseconds);
        var response = new JObject
        {
            ["fileName"] = fileName,
            ["mimeType"] = DocxMime,
            ["fileBase64"] = Convert.ToBase64String(output),
            ["warnings"] = warnings,
            ["statistics"] = statistics
        };
        return JsonResponse(HttpStatusCode.OK, response);
    }

    // ValidateTemplate answers "is this usable?", so template, package and data
    // problems are reported as diagnostics with valid=false instead of HTTP errors.
    private HttpResponseMessage HandleValidate(JObject request, Stopwatch clock)
    {
        var errors = new DiagnosticList();
        var warnings = new DiagnosticList();
        var requestWarnings = new List<Diagnostic>();
        WarnUnknownProperties(request, requestWarnings, "templateBase64", "dataJson", "strictMode");
        foreach (Diagnostic warning in requestWarnings) warnings.Add(warning);
        bool strict = ReadStrict(request);
        bool hasData = request["dataJson"] != null && request["dataJson"].Type != JTokenType.Null;
        bool dataValidated = false;
        JObject data = null;
        Engine discover = null;
        JObject statistics = null;
        DocxPackage discoverPackage = null;

        if (hasData)
        {
            try { data = ReadData(request, true); }
            catch (Problem problem) when (problem.Diag.Code != "INVALID_REQUEST") { errors.Add(problem.Diag); }
        }
        try
        {
            byte[] template = ReadTemplate(request);
            discoverPackage = DocxPackage.Load(template, clock, CancellationToken);
            discover = new Engine(discoverPackage, null, strict, true, clock, CancellationToken);
            discover.Run();
            foreach (Diagnostic d in discover.Errors) errors.Add(d);
            foreach (Diagnostic d in discover.Warnings) warnings.Add(d);
            if (data != null && errors.Count == 0)
            {
                DocxPackage renderPackage = DocxPackage.Load(template, clock, CancellationToken);
                var render = new Engine(renderPackage, data, strict, false, clock, CancellationToken);
                render.Run();
                foreach (Diagnostic d in render.Errors) errors.Add(d);
                foreach (Diagnostic d in render.Warnings) warnings.Add(d);
                if (render.Errors.Count == 0)
                {
                    byte[] output = renderPackage.Save(clock, CancellationToken);
                    renderPackage.VerifyOutput(output, strict);
                }
                dataValidated = true;
                statistics = render.Stats.ToJson(renderPackage.Stories.Count, clock.ElapsedMilliseconds);
            }
        }
        catch (Problem problem) when (problem.Diag.Code != "INVALID_REQUEST")
        {
            errors.Add(problem.Diag);
            if (problem.Related != null) foreach (Diagnostic d in problem.Related) errors.Add(d);
        }

        var response = new JObject
        {
            ["valid"] = errors.Count == 0,
            ["dataValidated"] = dataValidated,
            ["errors"] = errors.ToJson(),
            ["warnings"] = warnings.ToJson(),
            ["fields"] = discover == null ? new JArray() : discover.FieldsToJson(),
            ["storyParts"] = discoverPackage == null ? new JArray() : discoverPackage.StoriesToJson(),
            ["elapsedMilliseconds"] = clock.ElapsedMilliseconds
        };
        if (statistics != null) response["statistics"] = statistics;
        return JsonResponse(HttpStatusCode.OK, response);
    }

    private HttpResponseMessage HandleList(JObject request, Stopwatch clock)
    {
        var requestWarnings = new List<Diagnostic>();
        WarnUnknownProperties(request, requestWarnings, "templateBase64");
        byte[] template = ReadTemplate(request);
        DocxPackage package = DocxPackage.Load(template, clock, CancellationToken);
        var engine = new Engine(package, null, true, true, clock, CancellationToken);
        engine.Run();
        var response = new JObject
        {
            ["fields"] = engine.FieldsToJson(),
            ["storyParts"] = package.StoriesToJson(),
            ["sampleDataJson"] = engine.SampleData().ToString(Newtonsoft.Json.Formatting.None),
            ["errors"] = new JArray(engine.Errors.Select(e => e.ToJson())),
            ["warnings"] = new JArray(requestWarnings.Concat(engine.Warnings).Select(w => w.ToJson())),
            ["elapsedMilliseconds"] = clock.ElapsedMilliseconds
        };
        return JsonResponse(HttpStatusCode.OK, response);
    }

    private static void WarnUnknownProperties(JObject request, List<Diagnostic> warnings, params string[] known)
    {
        foreach (JProperty property in request.Properties())
        {
            if (Array.IndexOf(known, property.Name) < 0)
                warnings.Add(new Diagnostic("UNKNOWN_REQUEST_PROPERTY", "The request property was ignored because this action does not use it.", null, property.Name.Length > 100 ? property.Name.Substring(0, 100) : property.Name, null));
        }
    }

    // Accepts a base64 string, a data URL, or a Power Automate file-content object
    // ({"$content-type": ..., "$content": "<base64>"}).
    private static byte[] ReadTemplate(JObject request)
    {
        JToken token = request["templateBase64"];
        if (token != null && token.Type == JTokenType.Object && token["$content"] != null) token = token["$content"];
        if (token == null || token.Type == JTokenType.Null)
            throw new Problem("INVALID_REQUEST", "templateBase64 is required: supply the .docx template file content as base64.");
        if (token.Type != JTokenType.String)
            throw new Problem("INVALID_REQUEST", "templateBase64 must be a base64 string.");
        string text = (string)token;
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int marker = text.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || marker > 200) throw new Problem("INVALID_BASE64", "templateBase64 is a data URL without a ';base64,' payload.");
            text = text.Substring(marker + 8);
        }
        var compact = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c != ' ' && c != '\r' && c != '\n' && c != '\t') compact.Append(c);
        }
        if (compact.Length == 0) throw new Problem("INVALID_BASE64", "templateBase64 is empty.");
        if ((long)compact.Length / 4 * 3 > Limits.MaxTemplateBytes + 3)
            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The template exceeds the " + Limits.MaxTemplateBytes / (1024 * 1024) + " MiB limit.");
        try { return Convert.FromBase64String(compact.ToString()); }
        catch (FormatException) { throw new Problem("INVALID_BASE64", "templateBase64 is not valid base64. Pass the file content, not a file path or URL."); }
    }

    private static JObject ReadData(JObject request, bool required)
    {
        JToken token = request["dataJson"];
        if (token == null || token.Type == JTokenType.Null)
        {
            if (required) throw new Problem("INVALID_REQUEST", "dataJson is required: supply a JSON object serialized as a string.");
            return null;
        }
        if (token.Type == JTokenType.Object) return (JObject)token;
        if (token.Type != JTokenType.String)
            throw new Problem("INVALID_DATA_JSON", "dataJson must be a string containing a JSON object.");
        string text = (string)token;
        if (text.Length > Limits.MaxDataJsonChars)
            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "dataJson exceeds " + Limits.MaxDataJsonChars / (1024 * 1024) + " MiB.");
        if (String.IsNullOrWhiteSpace(text))
            throw new Problem("INVALID_DATA_JSON", "dataJson is empty; supply a JSON object such as {\"CompanyName\":\"Example\"}.");
        JToken parsed;
        try { parsed = ParseJson(text); }
        catch (JsonException) { throw new Problem("INVALID_DATA_JSON", "dataJson is not valid JSON (maximum nesting depth " + Limits.MaxJsonDepth + ")."); }
        catch (OverflowException) { throw new Problem("INVALID_DATA_JSON", "dataJson contains a number that is too large; pass it as a preformatted string."); }
        JObject data = parsed as JObject;
        if (data == null) throw new Problem("INVALID_DATA_JSON", "dataJson must contain a JSON object at the top level.");
        return data;
    }

    private static bool ReadStrict(JObject request)
    {
        JToken token = request["strictMode"];
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type != JTokenType.Boolean) throw new Problem("INVALID_REQUEST", "strictMode must be true or false.");
        return (bool)token;
    }

    private static readonly string[] ReservedFileNames =
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    // Produces a plain file name that SharePoint, OneDrive and Windows accept.
    private static string SanitizeFileName(JToken token, List<Diagnostic> warnings)
    {
        if (token == null || token.Type == JTokenType.Null) return DefaultFileName;
        if (token.Type != JTokenType.String) throw new Problem("INVALID_REQUEST", "fileName must be a string.");
        string raw = (string)token;
        if (String.IsNullOrWhiteSpace(raw)) return DefaultFileName;
        string name = raw;
        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0) name = name.Substring(slash + 1);
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(c < 32 || c == 127 || "<>:\"/\\|?*".IndexOf(c) >= 0 ? '_' : c);
        name = sb.ToString().Trim();
        string[] replaceable = { ".docx", ".docm", ".dotx", ".dotm", ".doc", ".dot" };
        foreach (string extension in replaceable)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                name = name.Substring(0, name.Length - extension.Length);
                break;
            }
        }
        name = name.Trim().TrimEnd('.', ' ').TrimStart(' ');
        if (name.StartsWith("~", StringComparison.Ordinal)) name = "_" + name.Substring(1);
        if (name.Length == 0) name = "Document";
        if (ReservedFileNames.Any(r => String.Equals(r, name, StringComparison.OrdinalIgnoreCase))) name = "_" + name;
        if (name.Length > 120)
        {
            name = name.Substring(0, 120);
            if (Char.IsHighSurrogate(name[name.Length - 1])) name = name.Substring(0, name.Length - 1);
            name = name.TrimEnd('.', ' ');
        }
        string result = name + ".docx";
        if (!String.Equals(result, raw, StringComparison.Ordinal))
            warnings.Add(new Diagnostic("FILE_NAME_SANITIZED", "fileName was adjusted to a plain .docx file name that file-storage services accept.", null, "fileName", null));
        return result;
    }

    private HttpResponseMessage ErrorResponse(Problem problem, string correlationId)
    {
        JObject error = problem.Diag.ToJson();
        error["correlationId"] = correlationId;
        if (problem.Related != null && problem.Related.Count > 0)
            error["relatedErrors"] = new JArray(problem.Related.Take(Limits.MaxDiagnostics).Select(d => d.ToJson()));
        return JsonResponse((HttpStatusCode)StatusFor(problem.Diag.Code), new JObject { ["error"] = error });
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, JObject body)
    {
        return new HttpResponseMessage(status) { Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None)) };
    }

    // ===================================================================
    // 2. ErrorsLimits
    // ===================================================================

    // Application safety caps, not verified Power Platform limits. Tune after
    // measuring payload limits in the target tenant.
    private static class Limits
    {
        public const int MaxRequestChars = 16 * 1024 * 1024;
        public const int MaxTemplateBytes = 5 * 1024 * 1024;
        public const int MaxDataJsonChars = 5 * 1024 * 1024;
        public const int MaxJsonDepth = 64;
        public const int MaxZipEntries = 1000;
        public const long MaxTotalUncompressedBytes = 25L * 1024 * 1024;
        public const long MaxPartBytes = 16L * 1024 * 1024;
        public const long RatioCheckMinBytes = 1024 * 1024;
        public const int MaxCompressionRatio = 200;
        public const int MaxLoopItems = 1000;
        public const int MaxGeneratedRows = 3000;
        public const long MaxGeneratedElements = 2000000;
        public const int MaxScalarChars = 32000;
        public const int MaxOutputBytes = 20 * 1024 * 1024;
        public const int MaxDiagnostics = 100;
        public const int MaxTokenChars = 200;
        public const int MaxStoryParts = 64;
        // Stricter than the documented 120-second custom-code timeout.
        public const long ProcessingBudgetMilliseconds = 90000;
    }

    private static int StatusFor(string code)
    {
        switch (code)
        {
            case "INVALID_REQUEST":
            case "INVALID_BASE64":
            case "INVALID_DATA_JSON":
            case "UNKNOWN_OPERATION":
                return 400;
            case "RESOURCE_LIMIT_EXCEEDED":
                return 413;
            case "RENDER_FAILED":
                return 500;
            default:
                return 422;
        }
    }

    private sealed class Diagnostic
    {
        public readonly string Code;
        public readonly string Message;
        public string Part;
        public readonly string Field;
        public string Location;

        public Diagnostic(string code, string message, string part, string field, string location)
        {
            Code = code; Message = message; Part = part; Field = field; Location = location;
        }

        public string Key { get { return Code + "\u0001" + Message + "\u0001" + Part + "\u0001" + Field + "\u0001" + Location; } }

        public JObject ToJson()
        {
            var json = new JObject { ["code"] = Code, ["message"] = Message };
            if (Part != null) json["part"] = Part;
            if (Field != null) json["field"] = Field;
            if (!String.IsNullOrEmpty(Location)) json["location"] = Location;
            return json;
        }
    }

    private sealed class DiagnosticList
    {
        private readonly List<Diagnostic> _items = new List<Diagnostic>();
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
        public int Count { get { return _items.Count; } }
        public List<Diagnostic> Items { get { return _items; } }

        public bool Add(Diagnostic diagnostic)
        {
            if (_items.Count >= Limits.MaxDiagnostics || !_keys.Add(diagnostic.Key)) return false;
            _items.Add(diagnostic);
            return true;
        }

        public JArray ToJson() { return new JArray(_items.Select(d => d.ToJson())); }
    }

    private sealed class Problem : Exception
    {
        public readonly Diagnostic Diag;
        public List<Diagnostic> Related;

        public Problem(string code, string message, string part = null, string field = null, string location = null) : base(message)
        {
            Diag = new Diagnostic(code, message, part, field, location);
        }

        public static Problem FromDiagnostics(List<Diagnostic> diagnostics)
        {
            Diagnostic first = diagnostics[0];
            var problem = new Problem(first.Code, first.Message, first.Part, first.Field, first.Location);
            problem.Related = diagnostics.Skip(1).ToList();
            return problem;
        }
    }

    private static void CheckBudget(Stopwatch clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock.ElapsedMilliseconds > Limits.ProcessingBudgetMilliseconds)
            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "Processing exceeded the " + Limits.ProcessingBudgetMilliseconds / 1000 + "-second safety budget. Reduce the template size or data volume.");
    }

    // ===================================================================
    // 3. PackageReaderWriter
    // ===================================================================

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W14 = "http://schemas.microsoft.com/office/word/2010/wordml";
    private static readonly XNamespace W15 = "http://schemas.microsoft.com/office/word/2012/wordml";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private static readonly XName WP = W + "p", WR = W + "r", WT = W + "t", WTbl = W + "tbl", WTr = W + "tr", WTc = W + "tc",
        WSdt = W + "sdt", WSdtPr = W + "sdtPr", WSdtContent = W + "sdtContent", WBody = W + "body", WPPr = W + "pPr",
        WRPr = W + "rPr", WSectPr = W + "sectPr", WVal = W + "val", WId = W + "id", WTag = W + "tag", WCustomXml = W + "customXml",
        WBr = W + "br", WTab = W + "tab", WRFonts = W + "rFonts", WRStyle = W + "rStyle", WTcPr = W + "tcPr";

    private const string RelOfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    private const string RelStrictOfficeDocument = "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument";
    private const string RelHeader = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/header";
    private const string RelFooter = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer";
    private const string RelFootnotes = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/footnotes";
    private const string RelEndnotes = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/endnotes";
    private const string RelComments = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments";
    private const string MainDocumentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string MainTemplateType = "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml";

    private sealed class PackageEntry
    {
        public string Name;
        public byte[] Data;
        public bool Stored;
        public bool IsDirectory;
        public DateTimeOffset LastWrite;
        public XDocument Xml;
        public bool Modified;
    }

    private sealed class StoryPart
    {
        public string Name;
        public string Kind;
        public PackageEntry Entry;
        public readonly List<string> References = new List<string>();
    }

    private sealed class AuxiliaryPart
    {
        public string Name;
        public XDocument Xml;
    }

    private sealed class Relationship
    {
        public string Id;
        public string Type;
        public string Target;
        public bool External;
    }

    private sealed class CentralEntry
    {
        public int Flags;
        public int Method;
    }

    private sealed class DocxPackage
    {
        public readonly List<PackageEntry> Entries = new List<PackageEntry>();
        public readonly Dictionary<string, PackageEntry> ByName = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);
        public readonly List<StoryPart> Stories = new List<StoryPart>();
        public readonly List<AuxiliaryPart> Auxiliary = new List<AuxiliaryPart>();
        public string MainPartName;

        public static DocxPackage Load(byte[] bytes, Stopwatch clock, CancellationToken cancellationToken)
        {
            if (bytes.Length > Limits.MaxTemplateBytes)
                throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The template exceeds the " + Limits.MaxTemplateBytes / (1024 * 1024) + " MiB limit.");
            if (bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0)
                throw new Problem("INVALID_DOCX", "The template is a password-protected (encrypted) Word file or a legacy .doc file. Remove the password and save it as Word Document (.docx).");
            if (bytes.Length < 30 || bytes[0] != 0x50 || bytes[1] != 0x4B || bytes[2] != 0x03 || bytes[3] != 0x04)
                throw new Problem("INVALID_DOCX", "The template is not a .docx file (it is not a ZIP-based Office Open XML package).");

            List<CentralEntry> central = ReadCentralDirectory(bytes);
            var package = new DocxPackage();
            long total = 0;
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(bytes, false), ZipArchiveMode.Read, false))
                {
                    if (archive.Entries.Count != central.Count)
                        throw new Problem("INVALID_DOCX", "The template ZIP directory is inconsistent.");
                    for (int i = 0; i < archive.Entries.Count; i++)
                    {
                        CheckBudget(clock, cancellationToken);
                        ZipArchiveEntry zipEntry = archive.Entries[i];
                        CentralEntry info = central[i];
                        string name = zipEntry.FullName;
                        if (!IsSafeEntryName(name))
                            throw new Problem("UNSAFE_PACKAGE", "The template contains an unsafe ZIP entry path (absolute, parent-relative, or malformed).");
                        if (package.ByName.ContainsKey(name))
                            throw new Problem("UNSAFE_PACKAGE", "The template contains duplicate ZIP entries for the same part.");
                        if ((info.Flags & 0x41) != 0)
                            throw new Problem("UNSAFE_PACKAGE", "The template contains encrypted ZIP entries.");
                        if (info.Method != 0 && info.Method != 8)
                            throw new Problem("INVALID_DOCX", "The template uses an unsupported ZIP compression method.");
                        long declared = zipEntry.Length;
                        if (declared > Limits.MaxPartBytes)
                            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "A template part exceeds the " + Limits.MaxPartBytes / (1024 * 1024) + " MiB per-part limit.", name);
                        if (declared > Limits.RatioCheckMinBytes && declared > (long)Math.Max(1, zipEntry.CompressedLength) * Limits.MaxCompressionRatio)
                            throw new Problem("UNSAFE_PACKAGE", "A template part has a suspicious compression ratio (possible ZIP bomb).", name);
                        total += declared;
                        if (total > Limits.MaxTotalUncompressedBytes)
                            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The template expands beyond " + Limits.MaxTotalUncompressedBytes / (1024 * 1024) + " MiB.");
                        var entry = new PackageEntry
                        {
                            Name = name,
                            Stored = info.Method == 0,
                            IsDirectory = name.EndsWith("/", StringComparison.Ordinal),
                            LastWrite = zipEntry.LastWriteTime
                        };
                        entry.Data = entry.IsDirectory ? new byte[0] : ReadBounded(zipEntry, declared, name);
                        package.Entries.Add(entry);
                        package.ByName[name] = entry;
                    }
                }
            }
            catch (InvalidDataException) { throw new Problem("INVALID_DOCX", "The template ZIP package is corrupt."); }
            catch (NotSupportedException) { throw new Problem("INVALID_DOCX", "The template ZIP package uses unsupported features."); }

            package.ResolveStories(clock, cancellationToken);
            return package;
        }

        private static byte[] ReadBounded(ZipArchiveEntry entry, long declared, string name)
        {
            using (Stream stream = entry.Open())
            using (var buffer = new MemoryStream((int)Math.Min(declared, Limits.MaxPartBytes)))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > declared)
                        throw new Problem("UNSAFE_PACKAGE", "A template part expands beyond its declared size.", name);
                    buffer.Write(chunk, 0, read);
                }
                if (buffer.Length != declared)
                    throw new Problem("INVALID_DOCX", "A template part is truncated.", name);
                return buffer.ToArray();
            }
        }

        private static List<CentralEntry> ReadCentralDirectory(byte[] b)
        {
            int eocd = -1;
            for (int i = b.Length - 22; i >= Math.Max(0, b.Length - 22 - 65535); i--)
            {
                if (U32(b, i) == 0x06054b50) { eocd = i; break; }
            }
            if (eocd < 0) throw new Problem("INVALID_DOCX", "The template ZIP package is incomplete.");
            long count = U16(b, eocd + 10);
            long offset = U32(b, eocd + 16);
            if (count == 0xFFFF || offset == 0xFFFFFFFF)
            {
                int locator = eocd - 20;
                if (locator < 0 || U32(b, locator) != 0x07064b50) throw new Problem("INVALID_DOCX", "The template ZIP64 directory is malformed.");
                long record = U64(b, locator + 8);
                if (record < 0 || record + 56 > b.Length || U32(b, (int)record) != 0x06064b50) throw new Problem("INVALID_DOCX", "The template ZIP64 directory is malformed.");
                count = U64(b, (int)record + 32);
                offset = U64(b, (int)record + 48);
            }
            if (count > Limits.MaxZipEntries) throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The template contains more than " + Limits.MaxZipEntries + " ZIP entries.");
            if (offset < 0 || offset > b.Length) throw new Problem("INVALID_DOCX", "The template ZIP directory is malformed.");
            var entries = new List<CentralEntry>();
            long p = offset;
            for (long i = 0; i < count; i++)
            {
                if (p + 46 > b.Length || U32(b, (int)p) != 0x02014b50) throw new Problem("INVALID_DOCX", "The template ZIP directory is malformed.");
                int position = (int)p;
                entries.Add(new CentralEntry { Flags = U16(b, position + 8), Method = U16(b, position + 10) });
                p += 46L + U16(b, position + 28) + U16(b, position + 30) + U16(b, position + 32);
                if (p > b.Length) throw new Problem("INVALID_DOCX", "The template ZIP directory is malformed.");
            }
            return entries;
        }

        private static int U16(byte[] b, int i) { return b[i] | (b[i + 1] << 8); }
        private static long U32(byte[] b, int i) { return (long)(uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)); }
        private static long U64(byte[] b, int i)
        {
            if (i < 0 || i + 8 > b.Length) return -1;
            ulong low = (ulong)U32(b, i), high = (ulong)U32(b, i + 4);
            ulong value = low | (high << 32);
            return value > long.MaxValue ? -1 : (long)value;
        }

        private static bool IsSafeEntryName(string name)
        {
            if (String.IsNullOrEmpty(name) || name.Length > 512) return false;
            if (name[0] == '/' || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0) return false;
            foreach (char c in name) if (c < 32 || c == 127) return false;
            string[] segments = name.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                bool trailingDirectory = i == segments.Length - 1 && segment.Length == 0 && i > 0;
                if (trailingDirectory) continue;
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
            }
            return true;
        }

        public XDocument LoadXml(string name, bool required)
        {
            PackageEntry entry;
            if (!ByName.TryGetValue(name, out entry) || entry.IsDirectory)
            {
                if (required) throw new Problem("INVALID_DOCX", "The template is missing a required part.", name);
                return null;
            }
            if (entry.Xml == null) entry.Xml = ParseXml(entry.Data, name);
            return entry.Xml;
        }

        private void ResolveStories(Stopwatch clock, CancellationToken cancellationToken)
        {
            if (Entries.Any(e => e.Name.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)))
                throw new Problem("INVALID_DOCX", "Macro-enabled documents (.docm/.dotm) are not supported. Save the template as Word Document (.docx).");
            XDocument types = LoadXml("[Content_Types].xml", true);
            Dictionary<string, Relationship> rootRels = ReadRelationships("_rels/.rels", "");
            if (rootRels == null) throw new Problem("INVALID_DOCX", "The template is missing its package relationships.", "_rels/.rels");
            if (rootRels.Values.Any(r => r.Type == RelStrictOfficeDocument))
                throw new Problem("INVALID_DOCX", "Strict Open XML documents are not supported. Save the template as Word Document (.docx).");
            Relationship main = rootRels.Values.FirstOrDefault(r => r.Type == RelOfficeDocument && !r.External);
            if (main == null || main.Target == null || !ByName.ContainsKey(main.Target))
                throw new Problem("INVALID_DOCX", "The template does not contain a main Word document part.");
            MainPartName = ByName[main.Target].Name;

            XElement overrideElement = types.Root == null ? null : types.Root.Elements(ContentTypes + "Override")
                .FirstOrDefault(o => String.Equals(((string)o.Attribute("PartName") ?? "").TrimStart('/'), MainPartName, StringComparison.OrdinalIgnoreCase));
            string contentType = overrideElement == null ? null : (string)overrideElement.Attribute("ContentType");
            if (overrideElement == null && types.Root != null)
            {
                string extension = MainPartName.Substring(MainPartName.LastIndexOf('.') + 1);
                XElement defaultElement = types.Root.Elements(ContentTypes + "Default")
                    .FirstOrDefault(d => String.Equals((string)d.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase));
                contentType = defaultElement == null ? null : (string)defaultElement.Attribute("ContentType");
            }
            if (contentType != null && contentType.IndexOf("macroEnabled", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new Problem("INVALID_DOCX", "Macro-enabled documents (.docm/.dotm) are not supported. Save the template as Word Document (.docx).");
            if (contentType == MainTemplateType && overrideElement != null)
            {
                // A .dotx template is accepted, but the output must declare itself a document.
                overrideElement.SetAttributeValue("ContentType", MainDocumentType);
                ByName["[Content_Types].xml"].Modified = true;
            }
            else if (contentType != MainDocumentType)
            {
                throw new Problem("INVALID_DOCX", "The main part is not a WordprocessingML document. Only .docx (and .dotx) templates are supported.", MainPartName);
            }

            XDocument document = LoadXml(MainPartName, true);
            if (document.Root == null || document.Root.Name != W + "document" || document.Root.Element(WBody) == null)
                throw new Problem("INVALID_DOCX", "The main document part does not contain a Word document body.", MainPartName);
            var body = new StoryPart { Name = MainPartName, Kind = "body", Entry = ByName[MainPartName] };
            body.References.Add("document body");
            Stories.Add(body);

            Dictionary<string, Relationship> rels = ReadRelationships(RelationshipsPartFor(MainPartName), MainPartName) ?? new Dictionary<string, Relationship>();
            int section = 0;
            foreach (XElement sectPr in document.Root.Element(WBody).Descendants(WSectPr))
            {
                CheckBudget(clock, cancellationToken);
                section++;
                foreach (XElement reference in sectPr.Elements().Where(e => e.Name == W + "headerReference" || e.Name == W + "footerReference"))
                {
                    bool isHeader = reference.Name.LocalName == "headerReference";
                    string id = (string)reference.Attribute(R + "id");
                    string variant = (string)reference.Attribute(W + "type") ?? "default";
                    Relationship rel;
                    if (id == null || !rels.TryGetValue(id, out rel) || rel.External || rel.Type != (isHeader ? RelHeader : RelFooter) || rel.Target == null || !ByName.ContainsKey(rel.Target))
                        throw new Problem("INVALID_DOCX", "A section references a " + (isHeader ? "header" : "footer") + " part that is missing or invalid.", MainPartName, null, "section " + section);
                    string partName = ByName[rel.Target].Name;
                    StoryPart story = Stories.FirstOrDefault(s => s.Name == partName);
                    if (story == null)
                    {
                        if (Stories.Count >= Limits.MaxStoryParts)
                            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The template references more than " + Limits.MaxStoryParts + " header/footer parts.");
                        XDocument xml = LoadXml(partName, true);
                        XName expected = W + (isHeader ? "hdr" : "ftr");
                        if (xml.Root == null || xml.Root.Name != expected)
                            throw new Problem("INVALID_DOCX", "A header/footer part does not have the expected root element.", partName);
                        story = new StoryPart { Name = partName, Kind = isHeader ? "header" : "footer", Entry = ByName[partName] };
                        Stories.Add(story);
                    }
                    string label = "section " + section + " " + variant + " " + story.Kind;
                    if (!story.References.Contains(label)) story.References.Add(label);
                }
            }

            foreach (Relationship rel in rels.Values)
            {
                if (rel.External || rel.Target == null || !ByName.ContainsKey(rel.Target)) continue;
                if (rel.Type == RelFootnotes || rel.Type == RelEndnotes || rel.Type == RelComments)
                {
                    string partName = ByName[rel.Target].Name;
                    if (Auxiliary.Any(a => a.Name == partName)) continue;
                    Auxiliary.Add(new AuxiliaryPart { Name = partName, Xml = LoadXml(partName, false) });
                }
            }
        }

        private static string RelationshipsPartFor(string partName)
        {
            int slash = partName.LastIndexOf('/');
            return slash < 0 ? "_rels/" + partName + ".rels" : partName.Substring(0, slash + 1) + "_rels/" + partName.Substring(slash + 1) + ".rels";
        }

        private Dictionary<string, Relationship> ReadRelationships(string relsName, string sourcePart)
        {
            XDocument xml = LoadXml(relsName, false);
            if (xml == null) return null;
            if (xml.Root == null || xml.Root.Name != PackageRels + "Relationships")
                throw new Problem("INVALID_DOCX", "A relationships part is malformed.", relsName);
            var result = new Dictionary<string, Relationship>(StringComparer.Ordinal);
            foreach (XElement element in xml.Root.Elements(PackageRels + "Relationship"))
            {
                var rel = new Relationship
                {
                    Id = (string)element.Attribute("Id"),
                    Type = (string)element.Attribute("Type"),
                    External = String.Equals((string)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                };
                // External targets are never dereferenced.
                if (!rel.External) rel.Target = ResolveTarget(sourcePart, (string)element.Attribute("Target"));
                if (rel.Id != null && !result.ContainsKey(rel.Id)) result[rel.Id] = rel;
            }
            return result;
        }

        private static string ResolveTarget(string sourcePart, string target)
        {
            if (String.IsNullOrEmpty(target)) return null;
            int fragment = target.IndexOf('#');
            if (fragment >= 0) target = target.Substring(0, fragment);
            try { target = Uri.UnescapeDataString(target); }
            catch (Exception) { return null; }
            if (target.IndexOf('\\') >= 0 || target.IndexOf(':') >= 0) return null;
            var segments = new List<string>();
            if (!target.StartsWith("/", StringComparison.Ordinal))
            {
                int slash = sourcePart.LastIndexOf('/');
                if (slash > 0) segments.AddRange(sourcePart.Substring(0, slash).Split('/'));
            }
            foreach (string segment in target.Split('/'))
            {
                if (segment.Length == 0 || segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count == 0) return null;
                    segments.RemoveAt(segments.Count - 1);
                }
                else segments.Add(segment);
            }
            return segments.Count == 0 ? null : String.Join("/", segments);
        }

        public byte[] Save(Stopwatch clock, CancellationToken cancellationToken)
        {
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    foreach (PackageEntry entry in Entries)
                    {
                        CheckBudget(clock, cancellationToken);
                        ZipArchiveEntry zipEntry = archive.CreateEntry(entry.Name, entry.Stored ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        try { zipEntry.LastWriteTime = entry.LastWrite; }
                        catch (ArgumentOutOfRangeException) { }
                        if (entry.IsDirectory) continue;
                        byte[] data = entry.Modified ? SerializeXml(entry.Xml) : entry.Data;
                        using (Stream stream = zipEntry.Open()) stream.Write(data, 0, data.Length);
                        if (output.Length > Limits.MaxOutputBytes)
                            throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The generated document exceeds the " + Limits.MaxOutputBytes / (1024 * 1024) + " MiB output limit.");
                    }
                }
                if (output.Length > Limits.MaxOutputBytes)
                    throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The generated document exceeds the " + Limits.MaxOutputBytes / (1024 * 1024) + " MiB output limit.");
                return output.ToArray();
            }
        }

        // Re-opens the generated package and checks the invariants the renderer
        // promises. A failure here is an engine defect, never a caller error.
        public readonly HashSet<string> AllocatedSdtIds = new HashSet<string>(StringComparer.Ordinal);

        public void VerifyOutput(byte[] output, bool strict)
        {
            try
            {
                using (var archive = new ZipArchive(new MemoryStream(output, false), ZipArchiveMode.Read, false))
                {
                    if (archive.Entries.Count != Entries.Count) throw new InvalidOperationException();
                    for (int i = 0; i < Entries.Count; i++)
                    {
                        if (archive.Entries[i].FullName != Entries[i].Name) throw new InvalidOperationException();
                    }
                    var allocated = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (StoryPart story in Stories)
                    {
                        XDocument xml;
                        using (Stream stream = archive.GetEntry(story.Name).Open())
                        using (var copy = new MemoryStream())
                        {
                            stream.CopyTo(copy);
                            xml = ParseXml(copy.ToArray(), story.Name);
                        }
                        foreach (XElement table in xml.Descendants(WTbl))
                        {
                            if (!table.Descendants(WTr).Any()) throw new InvalidOperationException();
                        }
                        foreach (XElement cell in xml.Descendants(WTc))
                        {
                            XElement last = LastBlock(cell);
                            if (last == null || (last.Name != WP && !last.Descendants(WP).Any())) throw new InvalidOperationException();
                        }
                        foreach (XElement sdt in xml.Descendants(WSdt))
                        {
                            XElement sdtPr = sdt.Element(WSdtPr);
                            string id = sdtPr == null || sdtPr.Element(WId) == null ? null : (string)sdtPr.Element(WId).Attribute(WVal);
                            if (id != null && AllocatedSdtIds.Contains(id))
                            {
                                int seen;
                                allocated.TryGetValue(id, out seen);
                                allocated[id] = seen + 1;
                            }
                            XElement checkbox = sdtPr == null ? null : sdtPr.Element(W14 + "checkbox");
                            XElement tag = sdtPr == null ? null : sdtPr.Element(WTag);
                            bool bound = tag != null && !String.IsNullOrWhiteSpace((string)tag.Attribute(WVal));
                            if (strict && bound && checkbox != null && !CheckboxStateConsistent(sdt, checkbox)) throw new InvalidOperationException();
                        }
                    }
                    if (allocated.Count != AllocatedSdtIds.Count || allocated.Values.Any(v => v != 1)) throw new InvalidOperationException();
                }
            }
            catch (Problem) { throw new Problem("RENDER_FAILED", "The generated document failed its integrity check. Quote the correlation ID when reporting this issue."); }
            catch (InvalidOperationException) { throw new Problem("RENDER_FAILED", "The generated document failed its integrity check. Quote the correlation ID when reporting this issue."); }
            catch (InvalidDataException) { throw new Problem("RENDER_FAILED", "The generated document failed its integrity check. Quote the correlation ID when reporting this issue."); }
        }

        public JArray StoriesToJson()
        {
            return new JArray(Stories.Select(s => new JObject
            {
                ["part"] = s.Name,
                ["type"] = s.Kind,
                ["references"] = new JArray(s.References)
            }));
        }
    }

    private static XElement LastBlock(XElement container)
    {
        return container.Elements().LastOrDefault(e => e.Name == WP || e.Name == WTbl || e.Name == WSdt || e.Name == WCustomXml);
    }

    // DTDs and external resolution are disabled; whitespace is preserved because
    // w:t content is significant.
    private static XDocument ParseXml(byte[] data, string partName)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = Limits.MaxPartBytes,
            MaxCharactersFromEntities = 1024,
            CloseInput = true
        };
        try
        {
            using (var reader = XmlReader.Create(new MemoryStream(data, false), settings))
                return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { throw new Problem("INVALID_DOCX", "A template part is not well-formed XML.", partName); }
    }

    private static byte[] SerializeXml(XDocument document)
    {
        using (var output = new MemoryStream())
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = false,
                NewLineHandling = NewLineHandling.None,
                OmitXmlDeclaration = document.Declaration == null
            };
            using (XmlWriter writer = XmlWriter.Create(output, settings)) document.Save(writer);
            return output.ToArray();
        }
    }

    // ===================================================================
    // 4. TemplateTokenizer
    // ===================================================================

    // Stands in for tabs, breaks, fields, drawings and container boundaries in
    // the paragraph text model. It is not a legal XML character, so it can never
    // collide with document text, and tags may not contain it.
    private const char Barrier = '\u0001';

    private static readonly Regex SegmentPattern = new Regex(@"^[\p{L}\p{M}\p{N}_\- ]+$", RegexOptions.CultureInvariant);
    private static readonly Regex FlagNamePattern = new Regex(@"^(Show|Hide|Is|Has|Include|Exclude|Can|Should|Enable|Display|With|Use)[\p{Lu}\p{N}_]", RegexOptions.CultureInvariant);
    private static readonly Regex TokenLikePattern = new Regex(@"\{([^{}\u0001]{1,200})\}", RegexOptions.CultureInvariant);

    private enum TokenKind { Scalar, Open, Close, EscapeOpen, EscapeClose }

    private sealed class Token
    {
        public TokenKind Kind;
        public int Start;
        public int End;
        public string Path;
        public string[] Segments;
        public bool IsRoot;
        public bool IsControl { get { return Kind == TokenKind.Open || Kind == TokenKind.Close; } }
    }

    private sealed class Slot
    {
        public XElement T;
        public int Start;
        public int OriginalLength;
        public string Text;
        public bool Changed;
    }

    private sealed class ParagraphModel
    {
        public XElement P;
        public string Text;
        public readonly List<Slot> Slots = new List<Slot>();
        public readonly List<Token> Tokens = new List<Token>();
        public readonly List<XElement> Checkboxes = new List<XElement>();
        public readonly List<XElement> Excluded = new List<XElement>();
        public readonly StringBuilder FieldText = new StringBuilder();
        public readonly List<Diagnostic> Errors = new List<Diagnostic>();

        // A marker paragraph holds exactly one section marker and only whitespace.
        public bool IsMarkerOnly
        {
            get
            {
                if (Tokens.Count != 1 || !Tokens[0].IsControl || Checkboxes.Count > 0 || Excluded.Count > 0) return false;
                for (int i = 0; i < Text.Length; i++)
                {
                    if (i >= Tokens[0].Start && i < Tokens[0].End) continue;
                    if (!Char.IsWhiteSpace(Text[i]) || Text[i] == Barrier) return false;
                }
                return true;
            }
        }
    }

    private static bool IsCheckbox(XElement sdt)
    {
        XElement sdtPr = sdt.Element(WSdtPr);
        return sdtPr != null && sdtPr.Element(W14 + "checkbox") != null;
    }

    private static bool HasDataBinding(XElement sdt)
    {
        XElement sdtPr = sdt.Element(WSdtPr);
        return sdtPr != null && (sdtPr.Element(W + "dataBinding") != null || sdtPr.Element(W15 + "dataBinding") != null);
    }

    // Content shown as a control's placeholder text is replaced by Word as soon as
    // the user clicks it, so tags there are not rewritten either.
    private static bool IsExcludedControl(XElement sdt)
    {
        XElement sdtPr = sdt.Element(WSdtPr);
        return HasDataBinding(sdt) || (sdtPr != null && sdtPr.Element(W + "showingPlcHdr") != null);
    }

    private static readonly HashSet<string> IgnorableInline = new HashSet<string>(StringComparer.Ordinal)
    {
        "pPr", "proofErr", "bookmarkStart", "bookmarkEnd", "commentRangeStart", "commentRangeEnd", "permStart", "permEnd",
        "moveFromRangeStart", "moveFromRangeEnd", "moveToRangeStart", "moveToRangeEnd", "smartTagPr", "customXmlPr",
        "customXmlInsRangeStart", "customXmlInsRangeEnd", "customXmlDelRangeStart", "customXmlDelRangeEnd"
    };

    private static readonly HashSet<string> TransparentInline = new HashSet<string>(StringComparer.Ordinal)
    {
        "hyperlink", "smartTag", "customXml", "ins", "moveTo", "dir", "bdo"
    };

    private static ParagraphModel BuildParagraphModel(XElement paragraph)
    {
        var model = new ParagraphModel { P = paragraph };
        var text = new StringBuilder();
        int fieldDepth = 0;
        WalkInline(paragraph, model, text, ref fieldDepth);
        model.Text = text.ToString();
        Tokenize(model);
        ScanExcluded(model);
        return model;
    }

    // Builds the visible-text map. Wrappers (hyperlinks, content controls,
    // revisions) are bracketed by barriers so a tag can never straddle them.
    private static void WalkInline(XElement parent, ParagraphModel model, StringBuilder text, ref int fieldDepth)
    {
        foreach (XElement child in parent.Elements())
        {
            XName name = child.Name;
            if (name == WR)
            {
                WalkRun(child, model, text, ref fieldDepth);
            }
            else if (name.Namespace == W && IgnorableInline.Contains(name.LocalName))
            {
                continue;
            }
            else if (name == WSdt)
            {
                text.Append(Barrier);
                if (IsCheckbox(child)) model.Checkboxes.Add(child);
                else if (IsExcludedControl(child)) model.Excluded.Add(child);
                else
                {
                    XElement content = child.Element(WSdtContent);
                    if (content != null) WalkInline(content, model, text, ref fieldDepth);
                }
                text.Append(Barrier);
            }
            else if (name.Namespace == W && TransparentInline.Contains(name.LocalName))
            {
                text.Append(Barrier);
                WalkInline(child, model, text, ref fieldDepth);
                text.Append(Barrier);
            }
            else if (name == W + "del" || name == W + "moveFrom")
            {
                text.Append(Barrier);
            }
            else
            {
                // Simple fields, math, alternate content and anything unknown.
                text.Append(Barrier);
                model.Excluded.Add(child);
            }
        }
    }

    private static void WalkRun(XElement run, ParagraphModel model, StringBuilder text, ref int fieldDepth)
    {
        foreach (XElement child in run.Elements())
        {
            XName name = child.Name;
            if (name == WRPr || name == W + "lastRenderedPageBreak" || name == W + "instrText" || name == W + "delText" || name == W + "delInstrText")
                continue;
            if (name == W + "fldChar")
            {
                string type = (string)child.Attribute(W + "fldCharType");
                if (type == "begin") fieldDepth++;
                else if (type == "end") fieldDepth = Math.Max(0, fieldDepth - 1);
                text.Append(Barrier);
                continue;
            }
            if (name == WT)
            {
                if (fieldDepth > 0)
                {
                    // Field results (PAGE, DOCPROPERTY, TOC...) are never rewritten.
                    model.FieldText.Append(child.Value).Append(Barrier);
                    text.Append(Barrier);
                    continue;
                }
                model.Slots.Add(new Slot { T = child, Start = text.Length, OriginalLength = child.Value.Length, Text = child.Value });
                text.Append(child.Value);
                continue;
            }
            if (name == W + "drawing" || name == W + "pict" || name == W + "object" || name == Mc + "AlternateContent")
                model.Excluded.Add(child);
            text.Append(Barrier);
        }
    }

    private static void Tokenize(ParagraphModel model)
    {
        string s = model.Text;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{')
                {
                    int closing = s.IndexOf("}}", i + 2, StringComparison.Ordinal);
                    if (closing > i + 2)
                    {
                        string inner = s.Substring(i + 2, closing - i - 2);
                        Token ambiguous;
                        string ignored;
                        if (inner.IndexOfAny(new[] { '{', '}', Barrier }) < 0 && TryParseTagBody(inner, out ambiguous, out ignored))
                        {
                            model.Errors.Add(new Diagnostic("INVALID_PLACEHOLDER", "Double braces around a field name are ambiguous. Use single braces for a placeholder, e.g. {" + ambiguous.Path + "}. '{{' and '}}' are only for literal brace characters.", null, ambiguous.Path, null));
                            i = closing + 2;
                            continue;
                        }
                    }
                    model.Tokens.Add(new Token { Kind = TokenKind.EscapeOpen, Start = i, End = i + 2 });
                    i += 2;
                    continue;
                }
                int j = i + 1;
                while (j < s.Length && s[j] != '}' && s[j] != '{' && s[j] != Barrier && j - i <= Limits.MaxTokenChars) j++;
                if (j >= s.Length || s[j] != '}')
                {
                    model.Errors.Add(new Diagnostic("INVALID_PLACEHOLDER", "A '{' is not closed by '}'. A tag must sit in one paragraph and cannot contain tabs, line breaks, fields, or hyperlink, content-control or tracked-change boundaries. Write '{{' for a literal '{'.", null, null, null));
                    i++;
                    continue;
                }
                Token token;
                string error;
                if (TryParseTagBody(s.Substring(i + 1, j - i - 1), out token, out error))
                {
                    token.Start = i;
                    token.End = j + 1;
                    model.Tokens.Add(token);
                }
                else
                {
                    model.Errors.Add(new Diagnostic("INVALID_PLACEHOLDER", error, null, null, null));
                }
                i = j + 1;
            }
            else if (c == '}')
            {
                if (i + 1 < s.Length && s[i + 1] == '}')
                {
                    model.Tokens.Add(new Token { Kind = TokenKind.EscapeClose, Start = i, End = i + 2 });
                    i += 2;
                }
                else
                {
                    model.Errors.Add(new Diagnostic("INVALID_PLACEHOLDER", "A '}' has no matching '{'. Write '}}' for a literal '}'.", null, null, null));
                    i++;
                }
            }
            else
            {
                i++;
            }
        }
    }

    // Grammar: {Path} | {#Path} | {/Path}; Path = Segment ('.' Segment)*;
    // Segment = letters, digits, combining marks, '_', '-', inner spaces.
    // A leading "Root." addresses the top-level object.
    private static bool TryParseTagBody(string body, out Token token, out string error)
    {
        token = null;
        error = null;
        string text = body.Replace(' ', ' ').Trim();
        var kind = TokenKind.Scalar;
        if (text.StartsWith("#", StringComparison.Ordinal)) { kind = TokenKind.Open; text = text.Substring(1).Trim(); }
        else if (text.StartsWith("/", StringComparison.Ordinal)) { kind = TokenKind.Close; text = text.Substring(1).Trim(); }
        if (text.Length == 0) { error = "A tag has no field name. Use {FieldName}, {#Section} or {/Section}."; return false; }
        if (text.Length > Limits.MaxTokenChars) { error = "A tag is longer than " + Limits.MaxTokenChars + " characters."; return false; }
        string[] segments = text.Split('.').Select(segment => segment.Trim()).ToArray();
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || !SegmentPattern.IsMatch(segment))
            {
                error = "A tag contains an invalid field name. Names may use letters, digits, spaces, '_' and '-', with '.' between nested names; '#' and '/' are reserved for section markers. Write '{{' and '}}' for literal braces.";
                return false;
            }
        }
        bool isRoot = segments[0] == "Root";
        if (isRoot && segments.Length == 1) { error = "'Root' must be followed by a field name, e.g. {Root.CompanyName}."; return false; }
        token = new Token
        {
            Kind = kind,
            Path = String.Join(".", segments),
            IsRoot = isRoot,
            Segments = isRoot ? segments.Skip(1).ToArray() : segments
        };
        return true;
    }

    // Tags in places the engine does not rewrite (text boxes, field results,
    // data-bound controls) must not silently survive, so they are errors.
    private static void ScanExcluded(ParagraphModel model)
    {
        if (ContainsTokenLike(model.FieldText.ToString()))
            model.Errors.Add(new Diagnostic("UNSUPPORTED_TEMPLATE_STRUCTURE", "A tag is inside a Word field result (for example a TOC, PAGE or DOCPROPERTY field). Tags inside fields are not supported; move the tag outside the field.", null, null, null));
        foreach (XElement excluded in model.Excluded)
        {
            if (ContainsTokenLike(VisibleTextOf(excluded)))
            {
                model.Errors.Add(new Diagnostic("UNSUPPORTED_TEMPLATE_STRUCTURE", "A tag is inside a text box, shape, simple field, equation, content-control placeholder text or data-bound content control. V1 only replaces tags in ordinary paragraphs and table cells.", null, null, null));
                break;
            }
        }
    }

    private static string VisibleTextOf(XElement element)
    {
        var text = new StringBuilder();
        foreach (XElement node in element.DescendantsAndSelf())
        {
            if (node.Name == WP) text.Append(Barrier);
            else if (node.Name == WT) text.Append(node.Value);
        }
        return text.ToString();
    }

    private static bool ContainsTokenLike(string text)
    {
        if (String.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return false;
        string cleaned = text.Replace("{{", "\u0001\u0001").Replace("}}", "\u0001\u0001");
        foreach (Match match in TokenLikePattern.Matches(cleaned))
        {
            Token token;
            string error;
            if (TryParseTagBody(match.Groups[1].Value, out token, out error)) return true;
        }
        return false;
    }

    private sealed class Edit
    {
        public int Start;
        public int End;
        public string Replacement;
    }

    // Applies non-overlapping text edits. The replacement takes the formatting of
    // the run holding the tag's first character; text outside tags and all run
    // boundaries outside tags are kept.
    private static void ApplyEdits(ParagraphModel model, List<Edit> edits)
    {
        foreach (Edit edit in edits.OrderByDescending(e => e.Start))
        {
            bool first = true;
            foreach (Slot slot in model.Slots)
            {
                int slotEnd = slot.Start + slot.OriginalLength;
                if (slotEnd <= edit.Start || slot.Start >= edit.End) continue;
                if (first)
                {
                    string prefix = slot.Text.Substring(0, edit.Start - slot.Start);
                    string suffix = edit.End < slotEnd ? slot.Text.Substring(edit.End - slot.Start) : "";
                    slot.Text = prefix + edit.Replacement + suffix;
                    first = false;
                }
                else
                {
                    int removeLength = Math.Min(edit.End, slotEnd) - slot.Start;
                    slot.Text = slot.Text.Substring(removeLength);
                }
                slot.Changed = true;
            }
        }
        foreach (Slot slot in model.Slots.Where(s => s.Changed)) CommitSlot(slot);
    }

    private static void CommitSlot(Slot slot)
    {
        XElement t = slot.T;
        XElement run = t.Parent;
        if (slot.Text.Length == 0)
        {
            t.Remove();
            if (run != null && run.Parent != null && run.Elements().All(e => e.Name == WRPr)) run.Remove();
            return;
        }
        if (slot.Text.IndexOf('\n') < 0 && slot.Text.IndexOf('\t') < 0)
        {
            t.Value = slot.Text;
            t.SetAttributeValue(XNamespace.Xml + "space", "preserve");
            return;
        }
        // Multi-line values become w:br / w:tab elements inside the same run.
        var nodes = new List<XElement>();
        var current = new StringBuilder();
        foreach (char c in slot.Text)
        {
            if (c == '\n' || c == '\t')
            {
                if (current.Length > 0) nodes.Add(NewText(current.ToString()));
                current.Clear();
                nodes.Add(new XElement(c == '\n' ? WBr : WTab));
            }
            else current.Append(c);
        }
        if (current.Length > 0) nodes.Add(NewText(current.ToString()));
        t.AddAfterSelf(nodes);
        t.Remove();
    }

    private static XElement NewText(string value)
    {
        return new XElement(WT, new XAttribute(XNamespace.Xml + "space", "preserve"), value);
    }

    // ===================================================================
    // 5. TemplateRenderer (planner + renderer; discovery mode shares the path)
    // ===================================================================

    private sealed class Scope
    {
        public JToken Value;
        public string Path;
        public string Key;
        public FieldInfo Section;
    }

    private sealed class MarkerRef
    {
        public int Index;
        public int Cell;
        public ParagraphModel Model;
        public Token Token;
        public bool Alone;
    }

    private sealed class BlockPair
    {
        public MarkerRef Open;
        public MarkerRef Close;
    }

    private sealed class ContainerPlan
    {
        public List<XElement> Children;
        public readonly Dictionary<XElement, ParagraphModel> Models = new Dictionary<XElement, ParagraphModel>();
        public readonly List<BlockPair> Pairs = new List<BlockPair>();
        public readonly List<MarkerRef> Leftovers = new List<MarkerRef>();
        public readonly List<Diagnostic> Errors = new List<Diagnostic>();
        public bool Failed;
    }

    private sealed class RowPlan
    {
        public XElement Row;
        public bool Wrapped;
        public List<XElement> Cells;
        public List<MarkerRef> Leftovers = new List<MarkerRef>();
        public MarkerRef Open;
        public MarkerRef Close;
        public bool Failed;
    }

    private sealed class Statistics
    {
        public int TextFieldsReplaced;
        public int TableRowsCreated;
        public int ConditionalBlocksEvaluated;
        public int CheckboxesUpdated;

        public JObject ToJson(int storyParts, long elapsed)
        {
            return new JObject
            {
                ["textFieldsReplaced"] = TextFieldsReplaced,
                ["tableRowsCreated"] = TableRowsCreated,
                ["conditionalBlocksEvaluated"] = ConditionalBlocksEvaluated,
                ["checkboxesUpdated"] = CheckboxesUpdated,
                ["storyPartsProcessed"] = storyParts,
                ["elapsedMilliseconds"] = elapsed
            };
        }
    }

    private sealed class FieldInfo
    {
        public string Name;
        public string DataPath;
        public string Type;
        public string Scope;
        public int Occurrences;
        public int Order;
        public bool HasItemFields;
        public bool NamedLikeFlag;
        public readonly JArray Locations = new JArray();
    }

    private sealed class Engine
    {
        private readonly DocxPackage _package;
        private readonly JObject _data;
        private readonly bool _strict;
        private readonly bool _discover;
        private readonly Stopwatch _clock;
        private readonly System.Threading.CancellationToken _cancellationToken;
        private readonly DiagnosticList _errors = new DiagnosticList();
        private readonly DiagnosticList _warnings = new DiagnosticList();
        private readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        private readonly HashSet<string> _checkboxKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<int> _usedSdtIds = new HashSet<int>();
        private Dictionary<XElement, string> _locations;
        private string _part;
        private Scope _root;
        private int _scopeCounter;
        private int _generatedRows;
        private long _generatedElements;
        private int _nextSdtId = 1000000000;
        public readonly Statistics Stats = new Statistics();

        public Engine(DocxPackage package, JObject data, bool strict, bool discover, Stopwatch clock, System.Threading.CancellationToken cancellationToken)
        {
            _package = package;
            _data = data;
            _strict = strict;
            _discover = discover;
            _clock = clock;
            _cancellationToken = cancellationToken;
        }

        public List<Diagnostic> Errors { get { return _errors.Items; } }
        public List<Diagnostic> Warnings { get { return _warnings.Items; } }

        public void Run()
        {
            CollectSdtIds();
            foreach (StoryPart story in _package.Stories)
            {
                _part = story.Name;
                XDocument xml = story.Entry.Xml;
                _locations = BuildLocations(story.Kind == "body" ? xml.Root.Element(WBody) : xml.Root);
                HashSet<string> pairedBefore = PairedRangeIds(xml.Root);
                _root = new Scope { Value = _data, Path = "", Key = "root" };
                if (xml.Root.Descendants().Any(e => e.Name == W + "ins" || e.Name == W + "del" || e.Name == W + "moveFrom" || e.Name == W + "moveTo"))
                    AddWarning("TRACKED_CHANGES", "The template contains tracked changes. They are kept as revisions in generated documents; accept or reject all changes in the template first.", null, null);
                XElement container = story.Kind == "body" ? xml.Root.Element(WBody) : xml.Root;
                RenderContainer(container, _root, false);
                EnsureEndsWithParagraph(container);
                RemoveNewOrphans(xml.Root, pairedBefore);
                if (!_discover) story.Entry.Modified = true;
            }
            foreach (AuxiliaryPart aux in _package.Auxiliary)
            {
                if (aux.Xml == null || aux.Name.EndsWith("comments.xml", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (XElement paragraph in aux.Xml.Descendants(WP))
                {
                    if (ContainsTokenLike(VisibleTextOf(paragraph)))
                    {
                        AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "A tag is inside a footnote or endnote. V1 replaces tags only in the body, headers and footers.", null, null, aux.Name);
                        break;
                    }
                }
            }
        }

        // ----- diagnostics -----

        private void AddError(string code, string message, string field, XElement at, string part = null)
        {
            _errors.Add(new Diagnostic(code, message, part ?? _part, field, at == null ? null : LocationOf(at)));
        }

        private void AddWarning(string code, string message, string field, XElement at)
        {
            if (_warnings.Count == Limits.MaxDiagnostics - 1)
            {
                _warnings.Add(new Diagnostic("WARNINGS_TRUNCATED", "Further warnings were omitted.", null, null, null));
                return;
            }
            _warnings.Add(new Diagnostic(code, message, _part, field, at == null ? null : LocationOf(at)));
        }

        private string LocationOf(XElement element)
        {
            for (XElement current = element; current != null; current = current.Parent)
            {
                string location;
                if (_locations.TryGetValue(current, out location)) return location;
            }
            return null;
        }

        // Precomputed before any mutation so diagnostics refer to the template as
        // the author sees it ("table 2, row 3, cell 1, paragraph 1").
        private static Dictionary<XElement, string> BuildLocations(XElement root)
        {
            var map = new Dictionary<XElement, string>();
            int paragraph = 0, table = 0;
            Action<XElement, string> walk = null;
            walk = (container, prefix) =>
            {
                int local = 0;
                foreach (XElement element in container.Elements())
                {
                    if (element.Name == WP)
                    {
                        map[element] = prefix == null ? "paragraph " + (++paragraph) : prefix + ", paragraph " + (++local);
                    }
                    else if (element.Name == WTbl)
                    {
                        int t = ++table;
                        map[element] = "table " + t;
                        int rowIndex = 0;
                        foreach (XElement row in TableRows(element).Select(r => r.Key))
                        {
                            string rowLabel = "table " + t + ", row " + (++rowIndex);
                            map[row] = rowLabel;
                            int cellIndex = 0;
                            foreach (XElement cell in RowCells(row))
                            {
                                string cellLabel = rowLabel + ", cell " + (++cellIndex);
                                map[cell] = cellLabel;
                                walk(cell, cellLabel);
                            }
                        }
                    }
                    else if (element.Name == WSdt && element.Element(WSdtContent) != null)
                    {
                        walk(element.Element(WSdtContent), prefix);
                    }
                    else if (element.Name == WCustomXml)
                    {
                        walk(element, prefix);
                    }
                }
            };
            walk(root, null);
            return map;
        }

        // ----- structure helpers -----

        private static IEnumerable<KeyValuePair<XElement, bool>> TableRows(XElement table)
        {
            foreach (XElement element in table.Elements())
            {
                if (element.Name == WTr) yield return new KeyValuePair<XElement, bool>(element, false);
                else if (element.Name == WSdt || element.Name == WCustomXml)
                {
                    XElement content = element.Name == WSdt ? element.Element(WSdtContent) : element;
                    if (content == null) continue;
                    foreach (XElement row in content.Descendants(WTr).Where(r => r.Ancestors(WTbl).FirstOrDefault() == table))
                        yield return new KeyValuePair<XElement, bool>(row, true);
                }
            }
        }

        private static List<XElement> RowCells(XElement row)
        {
            var cells = new List<XElement>();
            foreach (XElement element in row.Elements())
            {
                if (element.Name == WTc) cells.Add(element);
                else if (element.Name == WSdt || element.Name == WCustomXml)
                {
                    XElement content = element.Name == WSdt ? element.Element(WSdtContent) : element;
                    if (content != null) cells.AddRange(content.Descendants(WTc).Where(c => c.Ancestors(WTr).FirstOrDefault() == row));
                }
            }
            return cells;
        }

        // Word requires cells, headers, footers and the body to end with a
        // paragraph; removing a trailing block must not leave a table last.
        private static void EnsureEndsWithParagraph(XElement container)
        {
            XElement last = LastBlock(container);
            if (last != null && last.Name != WTbl) return;
            XElement sectPr = container.Elements(WSectPr).LastOrDefault();
            if (sectPr != null) sectPr.AddBeforeSelf(new XElement(WP));
            else container.Add(new XElement(WP));
        }

        private void CheckBudget()
        {
            Script.CheckBudget(_clock, _cancellationToken);
        }

        // ----- containers and block conditionals -----

        private ContainerPlan Analyze(XElement container, bool isCell)
        {
            var plan = new ContainerPlan { Children = container.Elements().ToList() };
            var markers = new List<MarkerRef>();
            for (int i = 0; i < plan.Children.Count; i++)
            {
                XElement child = plan.Children[i];
                if (child.Name != WP) continue;
                ParagraphModel model = BuildParagraphModel(child);
                plan.Models[child] = model;
                if (model.Errors.Count > 0)
                {
                    foreach (Diagnostic d in model.Errors)
                        plan.Errors.Add(new Diagnostic(d.Code, d.Message, _part, d.Field, LocationOf(child)));
                    continue;
                }
                bool alone = model.IsMarkerOnly;
                foreach (Token token in model.Tokens.Where(t => t.IsControl))
                    markers.Add(new MarkerRef { Index = i, Model = model, Token = token, Alone = alone });
            }

            var stack = new Stack<MarkerRef>();
            foreach (MarkerRef marker in markers)
            {
                if (!marker.Alone)
                {
                    if (isCell) { plan.Leftovers.Add(marker); continue; }
                    plan.Errors.Add(new Diagnostic("UNSUPPORTED_TEMPLATE_STRUCTURE", "Section markers outside tables must be alone in their own paragraph. Inline conditions inside a sentence are not supported; put {#" + marker.Token.Path + "} and {/" + marker.Token.Path + "} on separate paragraphs.", _part, marker.Token.Path, LocationOf(marker.Model.P)));
                    plan.Failed = true;
                    continue;
                }
                if (marker.Token.Kind == TokenKind.Open)
                {
                    stack.Push(marker);
                }
                else if (stack.Count > 0 && stack.Peek().Token.Path == marker.Token.Path)
                {
                    plan.Pairs.Add(new BlockPair { Open = stack.Pop(), Close = marker });
                }
                else if (stack.Count == 0)
                {
                    if (isCell) plan.Leftovers.Add(marker);
                    else
                    {
                        plan.Errors.Add(new Diagnostic("UNBALANCED_MARKER", "{/" + marker.Token.Path + "} has no matching {#" + marker.Token.Path + "} earlier in the same container (body, header, footer, table cell or content control).", _part, marker.Token.Path, LocationOf(marker.Model.P)));
                        plan.Failed = true;
                    }
                }
                else
                {
                    plan.Errors.Add(new Diagnostic("UNBALANCED_MARKER", "{/" + marker.Token.Path + "} closes a section while {#" + stack.Peek().Token.Path + "} is still open. Sections must be nested, not crossed.", _part, marker.Token.Path, LocationOf(marker.Model.P)));
                    plan.Failed = true;
                }
            }
            foreach (MarkerRef open in stack.Reverse())
            {
                if (isCell) plan.Leftovers.Add(open);
                else
                {
                    plan.Errors.Add(new Diagnostic("UNBALANCED_MARKER", "{#" + open.Token.Path + "} has no matching {/" + open.Token.Path + "} later in the same container (body, header, footer, table cell or content control).", _part, open.Token.Path, LocationOf(open.Model.P)));
                    plan.Failed = true;
                }
            }
            if (isCell) plan.Leftovers.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Token.Start.CompareTo(b.Token.Start));
            return plan;
        }

        private void RenderContainer(XElement container, Scope scope, bool isCell)
        {
            CheckBudget();
            ContainerPlan plan = Analyze(container, isCell);
            foreach (Diagnostic d in plan.Errors) _errors.Add(d);
            var removed = new HashSet<XElement>();
            var conditions = new Dictionary<XElement, Token>();
            if (!plan.Failed) ApplyBlocks(plan, scope, removed, conditions);
            foreach (XElement element in removed) element.Remove();

            foreach (XElement child in plan.Children)
            {
                Token condition;
                if (conditions.TryGetValue(child, out condition)) RecordField("condition", condition, scope, child);
                if (removed.Contains(child) || child.Parent == null) continue;
                if (child.Name == WP)
                {
                    ParagraphModel model;
                    if (plan.Models.TryGetValue(child, out model) && model.Errors.Count == 0) RenderParagraph(model, scope);
                }
                else if (child.Name == WTbl)
                {
                    RenderTable(child, scope);
                }
                else if (child.Name == WSdt)
                {
                    if (IsCheckbox(child)) ProcessCheckbox(child, scope);
                    else if (IsExcludedControl(child))
                    {
                        if (ContainsTokenLike(VisibleTextOf(child)))
                            AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "A tag is inside a content control's placeholder text or a control bound to custom XML data, where Word would replace the generated value. Type the tag as the control's content or move it outside the control.", null, child);
                    }
                    else if (child.Element(WSdtContent) != null)
                    {
                        XElement content = child.Element(WSdtContent);
                        RenderContainer(content, scope, false);
                        if (!content.Elements().Any()) content.Add(new XElement(WP));
                    }
                }
                else if (child.Name == WCustomXml)
                {
                    RenderContainer(child, scope, false);
                }
                else if (child.Name != WSectPr && child.Descendants(WT).Any() && ContainsTokenLike(VisibleTextOf(child)))
                {
                    AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "A tag is inside an unsupported document element.", null, child);
                }
            }
            if (isCell) EnsureEndsWithParagraph(container);
        }

        // In discovery mode conditions are returned in "conditions" and recorded in
        // document order by the caller.
        private void ApplyBlocks(ContainerPlan plan, Scope scope, HashSet<XElement> removed, Dictionary<XElement, Token> conditions)
        {
            var insideRemoved = new bool[plan.Children.Count];
            foreach (BlockPair pair in plan.Pairs.OrderBy(p => p.Open.Index))
            {
                int open = pair.Open.Index, close = pair.Close.Index;
                if (insideRemoved[open]) continue;
                XElement marker = plan.Children[open];
                Token token = pair.Open.Token;
                bool splitsSection = false;
                for (int k = open; k <= close; k++)
                {
                    XElement child = plan.Children[k];
                    if (child.Name == WP && child.Element(WPPr) != null && child.Element(WPPr).Element(WSectPr) != null) splitsSection = true;
                }
                if (splitsSection)
                {
                    AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "The conditional block {#" + token.Path + "} contains a section break. Conditions cannot add or remove section breaks.", token.Path, marker);
                    continue;
                }

                bool keep = true;
                if (_discover)
                {
                    conditions[marker] = token;
                    if (HasRemovalUnsafeContent(plan.Children, open + 1, close - 1))
                        AddWarning("REMOVAL_MAY_FAIL", "The conditional block {#" + token.Path + "} contains footnote, endnote or comment references; generation fails if the condition is false.", token.Path, marker);
                }
                else
                {
                    JToken value = Resolve(token, scope);
                    if (value == null)
                    {
                        if (_strict)
                        {
                            AddError("MISSING_FIELD", "The condition " + token.Path + " has no value. Supply JSON true or false.", token.Path, marker);
                            continue;
                        }
                        AddWarning("MISSING_FIELD", "The condition " + token.Path + " has no value; the block was removed.", token.Path, marker);
                        keep = false;
                    }
                    else if (value.Type == JTokenType.Boolean)
                    {
                        keep = (bool)value;
                    }
                    else if (value.Type == JTokenType.Array)
                    {
                        AddError("TYPE_MISMATCH", "{#" + token.Path + "} wraps whole paragraphs, which V1 supports only for Boolean conditions. To repeat content for each array item, put {#" + token.Path + "} and {/" + token.Path + "} in different cells of one table row.", token.Path, marker);
                        continue;
                    }
                    else
                    {
                        AddError("TYPE_MISMATCH", "The condition " + token.Path + " must be JSON true or false, not " + Describe(value) + ".", token.Path, marker);
                        continue;
                    }
                    Stats.ConditionalBlocksEvaluated++;
                    if (!keep && HasRemovalUnsafeContent(plan.Children, open + 1, close - 1))
                    {
                        AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "The conditional block {#" + token.Path + "} contains footnote, endnote or comment references, which V1 cannot remove safely.", token.Path, marker);
                        continue;
                    }
                }
                if (keep)
                {
                    removed.Add(plan.Children[open]);
                    removed.Add(plan.Children[close]);
                }
                else
                {
                    for (int k = open; k <= close; k++)
                    {
                        removed.Add(plan.Children[k]);
                        insideRemoved[k] = true;
                    }
                }
            }
        }

        private static bool HasRemovalUnsafeContent(IList<XElement> children, int from, int to)
        {
            for (int k = from; k <= to; k++)
                if (ContainsRemovalUnsafe(children[k])) return true;
            return false;
        }

        private static bool ContainsRemovalUnsafe(XElement element)
        {
            return element.Descendants().Any(d => d.Name == W + "footnoteReference" || d.Name == W + "endnoteReference" || d.Name == W + "commentReference");
        }

        // ----- paragraphs and scalars -----

        private void RenderParagraph(ParagraphModel model, Scope scope)
        {
            var edits = new List<Edit>();
            bool ok = true;
            foreach (Token token in model.Tokens)
            {
                if (token.Kind == TokenKind.EscapeOpen) edits.Add(new Edit { Start = token.Start, End = token.End, Replacement = "{" });
                else if (token.Kind == TokenKind.EscapeClose) edits.Add(new Edit { Start = token.Start, End = token.End, Replacement = "}" });
                else if (token.Kind == TokenKind.Scalar)
                {
                    string value = ScalarValue(token, scope, model.P);
                    if (value == null) ok = false;
                    else edits.Add(new Edit { Start = token.Start, End = token.End, Replacement = value });
                }
            }
            if (ok && !_discover && edits.Count > 0) ApplyEdits(model, edits);
            foreach (XElement checkbox in model.Checkboxes) ProcessCheckbox(checkbox, scope);
        }

        private string ScalarValue(Token token, Scope scope, XElement at)
        {
            if (_discover)
            {
                RecordField("text", token, scope, at);
                return "";
            }
            JToken value = Resolve(token, scope);
            if (value == null)
            {
                if (_strict)
                {
                    AddError("MISSING_FIELD", "No value was supplied for {" + token.Path + "}." + ScopeHint(token, scope), token.Path, at);
                    return null;
                }
                AddWarning("MISSING_FIELD", "No value was supplied for {" + token.Path + "}; it was replaced with empty text.", token.Path, at);
                Stats.TextFieldsReplaced++;
                return "";
            }
            if (value.Type == JTokenType.Object || value.Type == JTokenType.Array)
            {
                AddError("TYPE_MISMATCH", "{" + token.Path + "} needs a text, number or Boolean value, not " + Describe(value) + ".", token.Path, at);
                return null;
            }
            string text = value.Type == JTokenType.String ? (string)value
                : value.Type == JTokenType.Boolean ? ((bool)value ? "true" : "false")
                : value.ToString(Newtonsoft.Json.Formatting.None);
            if (text.Length > Limits.MaxScalarChars)
                throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The value for {" + token.Path + "} exceeds " + Limits.MaxScalarChars + " characters.", _part, token.Path, LocationOf(at));
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\v', '\n');
            try { XmlConvert.VerifyXmlChars(text); }
            catch (XmlException)
            {
                AddError("INVALID_DATA_JSON", "The value for {" + token.Path + "} contains control characters that Word documents cannot store.", token.Path, at);
                return null;
            }
            Stats.TextFieldsReplaced++;
            return text;
        }

        private static string ScopeHint(Token token, Scope scope)
        {
            return !token.IsRoot && scope.Path.Length > 0 ? " Inside a repeated row, names resolve against the current item; use {Root." + token.Path + "} for top-level data." : "";
        }

        private static string Describe(JToken value)
        {
            switch (value.Type)
            {
                case JTokenType.Object: return "an object";
                case JTokenType.Array: return "an array";
                case JTokenType.String: return "a string";
                case JTokenType.Integer:
                case JTokenType.Float: return "a number";
                case JTokenType.Boolean: return "a Boolean";
                default: return "null";
            }
        }

        private JToken Resolve(Token token, Scope scope)
        {
            JToken current = token.IsRoot ? _root.Value : scope.Value;
            foreach (string segment in token.Segments)
            {
                JObject obj = current as JObject;
                if (obj == null) return null;
                current = obj[segment];
            }
            return current == null || current.Type == JTokenType.Null || current.Type == JTokenType.Undefined ? null : current;
        }

        private string DataPath(Token token, Scope scope)
        {
            string prefix = token.IsRoot ? "" : scope.Path;
            string path = String.Join(".", token.Segments);
            return prefix.Length == 0 ? path : prefix + "." + path;
        }

        // ----- tables and row sections -----

        private void RenderTable(XElement table, Scope scope)
        {
            CheckBudget();
            var plans = new List<RowPlan>();
            foreach (KeyValuePair<XElement, bool> row in TableRows(table).ToList())
            {
                var rowPlan = new RowPlan { Row = row.Key, Wrapped = row.Value, Cells = RowCells(row.Key) };
                for (int c = 0; c < rowPlan.Cells.Count; c++)
                {
                    foreach (MarkerRef leftover in Analyze(rowPlan.Cells[c], true).Leftovers)
                    {
                        leftover.Cell = c;
                        rowPlan.Leftovers.Add(leftover);
                    }
                }
                plans.Add(rowPlan);
            }

            for (int i = 0; i < plans.Count; i++)
            {
                RowPlan plan = plans[i];
                List<MarkerRef> l = plan.Leftovers;
                if (l.Count == 0) continue;
                string name = l[0].Token.Path;
                if (l.Count == 1 && l[0].Token.Kind == TokenKind.Open)
                {
                    RowPlan partner = plans.Skip(i + 1).FirstOrDefault(p => p.Leftovers.Count == 1 && p.Leftovers[0].Token.Kind == TokenKind.Close && p.Leftovers[0].Token.Path == name);
                    if (partner != null)
                    {
                        AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "The " + name + " section spans multiple table rows; V1 accepts a single template row. Put {#" + name + "} and {/" + name + "} in different cells of the same row.", name, plan.Row);
                        partner.Failed = true;
                    }
                    else
                    {
                        AddError("UNBALANCED_MARKER", "{#" + name + "} has no matching {/" + name + "} in the same table row.", name, plan.Row);
                    }
                    plan.Failed = true;
                }
                else if (l.Count == 2 && l[0].Token.Kind == TokenKind.Open && l[1].Token.Kind == TokenKind.Close && l[1].Token.Path == name)
                {
                    if (l[0].Cell == l[1].Cell && plan.Cells.Count > 1)
                    {
                        AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "{#" + name + "} and {/" + name + "} are in the same cell. Put them in different cells of the row (for example the first and last cells). Inline conditions inside a cell are not supported.", name, plan.Row);
                        plan.Failed = true;
                    }
                    else if (plan.Wrapped)
                    {
                        AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "The " + name + " row is wrapped in a row-level content control or custom XML element, which V1 cannot repeat or remove.", name, plan.Row);
                        plan.Failed = true;
                    }
                    else
                    {
                        plan.Open = l[0];
                        plan.Close = l[1];
                    }
                }
                else if (!plan.Failed)
                {
                    if (l.Count == 1 && l[0].Token.Kind == TokenKind.Close)
                        AddError("UNBALANCED_MARKER", "{/" + name + "} has no matching {#" + name + "} in the same table row.", name, plan.Row);
                    else if (l.Count == 2 && l[0].Token.Kind == TokenKind.Open && l[1].Token.Kind == TokenKind.Close)
                        AddError("UNBALANCED_MARKER", "A table row opens {#" + name + "} but closes {/" + l[1].Token.Path + "}.", name, plan.Row);
                    else
                        AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "A table row may carry only one row section; nested, overlapping or reversed row markers are not supported.", name, plan.Row);
                    plan.Failed = true;
                }
            }

            foreach (RowPlan plan in plans)
            {
                if (plan.Failed) continue;
                if (plan.Open == null) RenderRowCells(plan.Row, scope);
                else RenderRowSection(plan, scope);
            }

            if (!table.Descendants(WTr).Any())
            {
                if (!_discover) AddWarning("EMPTY_TABLE_REMOVED", "Every row of a table was removed, so the table was removed.", null, table);
                table.Remove();
            }
        }

        private void RenderRowCells(XElement row, Scope scope)
        {
            foreach (XElement cell in RowCells(row)) RenderContainer(cell, scope, true);
        }

        private void RenderRowSection(RowPlan plan, Scope scope)
        {
            XElement row = plan.Row;
            Token token = plan.Open.Token;
            StripRowMarkers(plan);

            if (_discover)
            {
                FieldInfo section = RecordField("tableRow", token, scope, row);
                string blocker = CloneBlocker(row);
                if (blocker != null)
                    AddWarning("ROW_NOT_REPEATABLE", "The " + token.Path + " row " + blocker + ", so it can be used only with a Boolean value, not an array.", token.Path, row);
                // Without data a row section is ambiguous. Flag-like names (ShowX,
                // IsX, HasX...) are inventoried as Boolean rows whose fields use
                // the enclosing scope; other names as arrays of items.
                section.NamedLikeFlag = FlagNamePattern.IsMatch(token.Segments[token.Segments.Length - 1]);
                if (section.NamedLikeFlag) RenderRowCells(row, scope);
                else RenderRowCells(row, new Scope { Value = null, Path = DataPath(token, scope) + "[]", Key = DataPath(token, scope) + "[]", Section = section });
                return;
            }

            JToken value = Resolve(token, scope);
            if (value == null)
            {
                if (_strict)
                {
                    AddError("MISSING_FIELD", "No value was supplied for the table row section " + token.Path + ". Supply an array (repeat the row) or true/false (keep or remove it)." + ScopeHint(token, scope), token.Path, row);
                    return;
                }
                AddWarning("MISSING_FIELD", "No value was supplied for the table row section " + token.Path + "; the row was removed.", token.Path, row);
                RemoveRow(row, token);
                return;
            }
            if (value.Type == JTokenType.Boolean)
            {
                Stats.ConditionalBlocksEvaluated++;
                if ((bool)value) RenderRowCells(row, scope);
                else RemoveRow(row, token);
                return;
            }
            if (value.Type != JTokenType.Array)
            {
                AddError("TYPE_MISMATCH", "The table row section " + token.Path + " needs an array or a Boolean, not " + Describe(value) + ".", token.Path, row);
                return;
            }

            var items = (JArray)value;
            string reason = CloneBlocker(row);
            if (reason != null && items.Count > 0)
            {
                AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "The " + token.Path + " row cannot be repeated because it " + reason + ".", token.Path, row);
                return;
            }
            if (items.Count > Limits.MaxLoopItems)
                throw new Problem("RESOURCE_LIMIT_EXCEEDED", token.Path + " has " + items.Count + " items; the limit is " + Limits.MaxLoopItems + " per repeated row.", _part, token.Path, LocationOf(row));
            _generatedRows += items.Count;
            if (_generatedRows > Limits.MaxGeneratedRows)
                throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The document would contain more than " + Limits.MaxGeneratedRows + " generated table rows.", _part, token.Path, LocationOf(row));
            _generatedElements += (long)row.DescendantsAndSelf().Count() * items.Count;
            if (_generatedElements > Limits.MaxGeneratedElements)
                throw new Problem("RESOURCE_LIMIT_EXCEEDED", "The repeated rows would make the document too large to process safely.", _part, token.Path, LocationOf(row));
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Type != JTokenType.Object)
                {
                    AddError("TYPE_MISMATCH", "Item " + (i + 1) + " of " + token.Path + " must be a JSON object whose properties fill the row, not " + Describe(items[i]) + ".", token.Path, row);
                    return;
                }
            }
            if (items.Count == 0)
            {
                RemoveRow(row, token);
                return;
            }

            string path = DataPath(token, scope) + "[]";
            for (int i = 0; i < items.Count; i++)
            {
                CheckBudget();
                var clone = new XElement(row);
                MapCloneLocations(row, clone, i + 1);
                if (i > 0) PrepareClone(clone);
                row.AddBeforeSelf(clone);
                var itemScope = new Scope { Value = items[i], Path = path, Key = path + "#" + (++_scopeCounter) };
                RenderRowCells(clone, itemScope);
            }
            row.Remove();
            Stats.TableRowsCreated += items.Count;
        }

        private void RemoveRow(XElement row, Token token)
        {
            if (ContainsRemovalUnsafe(row))
            {
                AddError("UNSUPPORTED_TEMPLATE_STRUCTURE", "The " + token.Path + " row contains footnote, endnote or comment references, which V1 cannot remove safely.", token.Path, row);
                return;
            }
            row.Remove();
        }

        private static void StripRowMarkers(RowPlan plan)
        {
            foreach (IGrouping<ParagraphModel, MarkerRef> group in new[] { plan.Open, plan.Close }.GroupBy(m => m.Model))
            {
                ParagraphModel model = group.Key;
                bool alone = model.IsMarkerOnly;
                ApplyEdits(model, group.Select(m => new Edit { Start = m.Token.Start, End = m.Token.End, Replacement = "" }).ToList());
                // A marker that had its own paragraph should not leave a blank line,
                // unless it is the cell's only paragraph.
                XElement cell = model.P.Parent;
                if (alone && cell != null && cell.Elements(WP).Count() > 1) model.P.Remove();
            }
        }

        // Returns why a row cannot be cloned safely, or null when it can.
        private static string CloneBlocker(XElement row)
        {
            foreach (XElement element in row.Descendants())
            {
                XName name = element.Name;
                if (name == W + "drawing" || name == W + "pict" || name == W + "object" || name == Mc + "AlternateContent") return "contains an image, shape, chart or embedded object";
                if (name == W + "hyperlink") return "contains a hyperlink";
                if (name == WTbl) return "contains a nested table";
                if (name == W + "footnoteReference" || name == W + "endnoteReference") return "contains a footnote or endnote";
                if (name == W + "commentReference" || name == W + "commentRangeStart") return "contains a comment";
                if (name == W + "ins" || name == W + "del" || name == W + "moveFrom" || name == W + "moveTo" ||
                    name == W + "rPrChange" || name == W + "pPrChange" || name == W + "tcPrChange" || name == W + "trPrChange" || name == W + "tblPrExChange")
                    return "contains tracked changes (accept or reject them first)";
                if (name == W + "vMerge") return "contains vertically merged cells";
                if (name == W + "altChunk" || name == W + "subDoc" || name == W + "control") return "contains embedded content";
                if (name == WSdt && HasDataBinding(element)) return "contains a data-bound content control";
            }
            return null;
        }

        private void MapCloneLocations(XElement prototype, XElement clone, int item)
        {
            using (IEnumerator<XElement> source = prototype.DescendantsAndSelf().GetEnumerator())
            using (IEnumerator<XElement> target = clone.DescendantsAndSelf().GetEnumerator())
            {
                while (source.MoveNext() && target.MoveNext())
                {
                    string location;
                    if (_locations.TryGetValue(source.Current, out location)) _locations[target.Current] = location + " (item " + item + ")";
                }
            }
        }

        // Clones after the first get fresh content-control IDs and drop IDs that
        // must stay unique (paragraph IDs, bookmarks, range permissions).
        private void PrepareClone(XElement clone)
        {
            foreach (XElement sdtPr in clone.Descendants(WSdtPr))
            {
                XElement id = sdtPr.Element(WId);
                if (id != null) id.SetAttributeValue(WVal, XmlConvert.ToString(AllocateSdtId()));
            }
            foreach (XElement element in clone.DescendantsAndSelf().Where(e => e.Name == WP || e.Name == WTr))
            {
                XAttribute paraId = element.Attribute(W14 + "paraId");
                if (paraId != null) paraId.Remove();
                XAttribute textId = element.Attribute(W14 + "textId");
                if (textId != null) textId.Remove();
            }
            foreach (XElement marker in clone.Descendants().Where(e => e.Name == W + "bookmarkStart" || e.Name == W + "bookmarkEnd" || e.Name == W + "permStart" || e.Name == W + "permEnd").ToList())
                marker.Remove();
        }

        private void CollectSdtIds()
        {
            IEnumerable<XDocument> documents = _package.Stories.Select(s => s.Entry.Xml).Concat(_package.Auxiliary.Where(a => a.Xml != null).Select(a => a.Xml));
            foreach (XDocument document in documents)
            {
                foreach (XElement sdtPr in document.Descendants(WSdtPr))
                {
                    XElement id = sdtPr.Element(WId);
                    if (id == null) continue;
                    try { _usedSdtIds.Add(XmlConvert.ToInt32((string)id.Attribute(WVal) ?? "")); }
                    catch (FormatException) { }
                    catch (OverflowException) { }
                }
            }
        }

        // Deterministic and collision-free within the package.
        private int AllocateSdtId()
        {
            while (_usedSdtIds.Contains(_nextSdtId)) _nextSdtId++;
            _usedSdtIds.Add(_nextSdtId);
            _package.AllocatedSdtIds.Add(XmlConvert.ToString(_nextSdtId));
            return _nextSdtId++;
        }

        private static readonly string[][] RangePairs =
        {
            new[] { "bookmarkStart", "bookmarkEnd" },
            new[] { "permStart", "permEnd" },
            new[] { "commentRangeStart", "commentRangeEnd" }
        };

        private static HashSet<string> PairedRangeIds(XElement root)
        {
            var paired = new HashSet<string>(StringComparer.Ordinal);
            foreach (string[] pair in RangePairs)
            {
                var starts = new HashSet<string>(root.Descendants(W + pair[0]).Select(e => (string)e.Attribute(WId)).Where(v => v != null));
                foreach (XElement end in root.Descendants(W + pair[1]))
                {
                    string id = (string)end.Attribute(WId);
                    if (id != null && starts.Contains(id)) paired.Add(pair[0] + ":" + id);
                }
            }
            return paired;
        }

        // Removes range markers whose partner was deleted with a removed block.
        private static void RemoveNewOrphans(XElement root, HashSet<string> pairedBefore)
        {
            foreach (string[] pair in RangePairs)
            {
                var starts = root.Descendants(W + pair[0]).ToList();
                var ends = root.Descendants(W + pair[1]).ToList();
                var startIds = new HashSet<string>(starts.Select(e => (string)e.Attribute(WId)).Where(v => v != null));
                var endIds = new HashSet<string>(ends.Select(e => (string)e.Attribute(WId)).Where(v => v != null));
                foreach (XElement start in starts)
                {
                    string id = (string)start.Attribute(WId);
                    if (id != null && !endIds.Contains(id) && pairedBefore.Contains(pair[0] + ":" + id)) start.Remove();
                }
                foreach (XElement end in ends)
                {
                    string id = (string)end.Attribute(WId);
                    if (id != null && !startIds.Contains(id) && pairedBefore.Contains(pair[0] + ":" + id)) end.Remove();
                }
            }
        }

        // ===================================================================
        // 6. CheckboxRenderer
        // ===================================================================

        private void ProcessCheckbox(XElement sdt, Scope scope)
        {
            XElement sdtPr = sdt.Element(WSdtPr);
            XElement checkbox = sdtPr.Element(W14 + "checkbox");
            XElement tagElement = sdtPr.Element(WTag);
            string tag = tagElement == null ? null : (string)tagElement.Attribute(WVal);
            if (String.IsNullOrWhiteSpace(tag)) return; // Unbound checkboxes stay interactive and untouched.
            tag = tag.Trim();
            if (tag.Length > 2 && tag[0] == '{' && tag[tag.Length - 1] == '}') tag = tag.Substring(1, tag.Length - 2);

            Token token;
            string error;
            if (!TryParseTagBody(tag, out token, out error) || token.Kind != TokenKind.Scalar)
            {
                AddError("UNSUPPORTED_CHECKBOX", "A checkbox content control has a Tag that is not a valid field name. Use a Boolean field name such as IsApproved, or clear the Tag to leave the checkbox unbound.", null, sdt);
                return;
            }
            CheckboxParts parts;
            if (!TryReadCheckbox(sdt, checkbox, out parts, out error))
            {
                AddError("UNSUPPORTED_CHECKBOX", "The checkbox bound to " + token.Path + " " + error, token.Path, sdt);
                return;
            }

            string key = _part + "|" + scope.Key + "|" + DataPath(token, scope);
            if (!_checkboxKeys.Add(key))
            {
                string message = "More than one checkbox in the same scope uses the Tag " + token.Path + ". Copied checkboxes keep their Tag; give each checkbox its own field.";
                if (_strict) AddError("DUPLICATE_CHECKBOX_TAG", message, token.Path, sdt);
                else AddWarning("DUPLICATE_CHECKBOX_TAG", message, token.Path, sdt);
            }

            if (_discover)
            {
                RecordField("checkbox", token, scope, sdt);
                return;
            }
            JToken value = Resolve(token, scope);
            if (value == null)
            {
                if (_strict) AddError("MISSING_FIELD", "No value was supplied for the checkbox " + token.Path + ". Supply JSON true or false." + ScopeHint(token, scope), token.Path, sdt);
                else AddWarning("MISSING_FIELD", "No value was supplied for the checkbox " + token.Path + "; it was left unchanged.", token.Path, sdt);
                return;
            }
            if (value.Type != JTokenType.Boolean)
            {
                AddError("TYPE_MISMATCH", "The checkbox " + token.Path + " needs JSON true or false, not " + Describe(value) + ".", token.Path, sdt);
                return;
            }
            bool isChecked = (bool)value;
            XElement checkedElement = checkbox.Element(W14 + "checked");
            if (checkedElement == null)
            {
                checkedElement = new XElement(W14 + "checked");
                checkbox.AddFirst(checkedElement);
            }
            checkedElement.SetAttributeValue(W14 + "val", isChecked ? "1" : "0");
            parts.Text.Value = isChecked ? parts.CheckedGlyph : parts.UncheckedGlyph;
            if (!String.Equals(parts.CheckedFont, parts.UncheckedFont, StringComparison.OrdinalIgnoreCase))
                SetRunFont(parts.Run, isChecked ? parts.CheckedFont : parts.UncheckedFont);
            Stats.CheckboxesUpdated++;
        }

        private static void SetRunFont(XElement run, string font)
        {
            XElement rPr = run.Element(WRPr);
            if (rPr == null)
            {
                rPr = new XElement(WRPr);
                run.AddFirst(rPr);
            }
            XElement rFonts = rPr.Element(WRFonts);
            if (rFonts == null)
            {
                rFonts = new XElement(WRFonts);
                XElement style = rPr.Element(WRStyle);
                if (style != null) style.AddAfterSelf(rFonts);
                else rPr.AddFirst(rFonts);
            }
            foreach (string theme in new[] { "asciiTheme", "hAnsiTheme", "eastAsiaTheme", "cstheme" })
            {
                XAttribute attribute = rFonts.Attribute(W + theme);
                if (attribute != null) attribute.Remove();
            }
            rFonts.SetAttributeValue(W + "ascii", font);
            rFonts.SetAttributeValue(W + "hAnsi", font);
            rFonts.SetAttributeValue(W + "eastAsia", font);
            rFonts.SetAttributeValue(W + "cs", font);
        }

        // ===================================================================
        // 7. Inventory
        // ===================================================================

        private FieldInfo RecordField(string type, Token token, Scope scope, XElement at)
        {
            string dataPath = DataPath(token, scope);
            string key = type + "|" + dataPath;
            FieldInfo info;
            if (!_fields.TryGetValue(key, out info))
            {
                info = new FieldInfo
                {
                    Name = token.Path,
                    DataPath = dataPath,
                    Type = type,
                    Scope = token.IsRoot || scope.Path.Length == 0 ? "" : scope.Path,
                    Order = _fields.Count
                };
                _fields[key] = info;
            }
            info.Occurrences++;
            if (info.Locations.Count < 10)
            {
                var location = new JObject { ["part"] = _part };
                string text = LocationOf(at);
                if (text != null) location["location"] = text;
                info.Locations.Add(location);
            }
            if (!token.IsRoot && scope.Section != null && type != "tableRow") scope.Section.HasItemFields = true;
            return info;
        }

        public JArray FieldsToJson()
        {
            var result = new JArray();
            foreach (FieldInfo info in _fields.Values.OrderBy(f => f.Order))
            {
                var json = new JObject
                {
                    ["name"] = info.Name,
                    ["dataPath"] = info.DataPath,
                    ["type"] = info.Type,
                    ["acceptedValues"] = new JArray(AcceptedValues(info.Type)),
                    ["scope"] = info.Scope,
                    ["occurrences"] = info.Occurrences,
                    ["locations"] = info.Locations
                };
                if (info.Type == "tableRow") json["likelyType"] = LikelyArray(info) ? "array" : "boolean";
                result.Add(json);
            }
            return result;
        }

        private static bool LikelyArray(FieldInfo info)
        {
            return !info.NamedLikeFlag && info.HasItemFields;
        }

        private static string[] AcceptedValues(string type)
        {
            switch (type)
            {
                case "text": return new[] { "string", "number", "boolean" };
                case "tableRow": return new[] { "array", "boolean" };
                default: return new[] { "boolean" };
            }
        }

        // Best-effort skeleton payload derived from the discovered fields.
        public JObject SampleData()
        {
            var root = new JObject();
            foreach (FieldInfo info in _fields.Values.OrderBy(f => f.Order))
            {
                JToken leaf;
                if (info.Type == "text") leaf = "";
                else if (info.Type == "tableRow") leaf = LikelyArray(info) ? (JToken)new JArray(new JObject()) : new JValue(true);
                else if (info.Type == "condition") leaf = new JValue(true);
                else leaf = new JValue(false);
                SetSamplePath(root, info.DataPath.Split('.'), 0, leaf);
            }
            return root;
        }

        private static void SetSamplePath(JObject target, string[] segments, int index, JToken leaf)
        {
            string segment = segments[index];
            bool isArray = segment.EndsWith("[]", StringComparison.Ordinal);
            string name = isArray ? segment.Substring(0, segment.Length - 2) : segment;
            if (index == segments.Length - 1)
            {
                if (target[name] == null) target[name] = leaf;
                return;
            }
            if (isArray)
            {
                JArray array = target[name] as JArray;
                if (array == null)
                {
                    if (target[name] != null && target[name].Type != JTokenType.Boolean) return;
                    array = new JArray(new JObject());
                    target[name] = array;
                }
                if (array.Count == 0) array.Add(new JObject());
                JObject item = array[0] as JObject;
                if (item != null) SetSamplePath(item, segments, index + 1, leaf);
                return;
            }
            JObject child = target[name] as JObject;
            if (child == null)
            {
                if (target[name] != null) return;
                child = new JObject();
                target[name] = child;
            }
            SetSamplePath(child, segments, index + 1, leaf);
        }
    }

    private sealed class CheckboxParts
    {
        public XElement Run;
        public XElement Text;
        public string CheckedGlyph;
        public string UncheckedGlyph;
        public string CheckedFont;
        public string UncheckedFont;
    }

    // Accepts only the representation Word writes: one run with one w:t whose
    // character matches the configured checked or unchecked state.
    private static bool TryReadCheckbox(XElement sdt, XElement checkbox, out CheckboxParts parts, out string error)
    {
        parts = null;
        error = null;
        if (HasDataBinding(sdt)) { error = "is bound to custom XML data, which Word would use to overwrite the generated state."; return false; }
        XElement content = sdt.Element(WSdtContent);
        if (content == null) { error = "has no content."; return false; }
        List<XElement> runs = content.Descendants(WR).ToList();
        List<XElement> texts = content.Descendants(WT).ToList();
        if (runs.Count != 1 || texts.Count != 1 || texts[0].Parent != runs[0] || runs[0].Elements().Any(e => e.Name != WRPr && e.Name != WT))
        {
            error = "does not contain the single symbol run Word creates for checkbox content controls. Delete it and insert a new Check Box Content Control.";
            return false;
        }
        int checkedCode, uncheckedCode;
        string checkedFont, uncheckedFont;
        if (!TryReadState(checkbox.Element(W14 + "checkedState"), "2612", out checkedCode, out checkedFont) ||
            !TryReadState(checkbox.Element(W14 + "uncheckedState"), "2610", out uncheckedCode, out uncheckedFont) ||
            checkedCode == uncheckedCode)
        {
            error = "has an invalid checked/unchecked symbol configuration.";
            return false;
        }
        string current = texts[0].Value;
        string checkedGlyph = Char.ConvertFromUtf32(checkedCode), uncheckedGlyph = Char.ConvertFromUtf32(uncheckedCode);
        // Symbol fonts (e.g. Wingdings) may store the glyph in the private-use area.
        string checkedPua = checkedCode < 0x100 ? ((char)(0xF000 + checkedCode)).ToString() : null;
        string uncheckedPua = uncheckedCode < 0x100 ? ((char)(0xF000 + uncheckedCode)).ToString() : null;
        bool usesPua;
        if (current == checkedGlyph || current == uncheckedGlyph) usesPua = false;
        else if ((checkedPua != null && current == checkedPua) || (uncheckedPua != null && current == uncheckedPua)) usesPua = true;
        else
        {
            error = "shows a symbol that matches neither its checked nor its unchecked state (custom font or edited symbol). Reset the symbols in the control properties.";
            return false;
        }
        if (usesPua && (checkedPua == null || uncheckedPua == null))
        {
            error = "mixes symbol-font and Unicode checkbox symbols.";
            return false;
        }
        parts = new CheckboxParts
        {
            Run = runs[0],
            Text = texts[0],
            CheckedGlyph = usesPua ? checkedPua : checkedGlyph,
            UncheckedGlyph = usesPua ? uncheckedPua : uncheckedGlyph,
            CheckedFont = checkedFont,
            UncheckedFont = uncheckedFont
        };
        return true;
    }

    private static bool TryReadState(XElement state, string defaultHex, out int code, out string font)
    {
        code = 0;
        font = state == null ? "MS Gothic" : (string)state.Attribute(W14 + "font") ?? "MS Gothic";
        string hex = state == null ? defaultHex : (string)state.Attribute(W14 + "val") ?? defaultHex;
        if (hex.Length == 0 || hex.Length > 6) return false;
        try { code = Convert.ToInt32(hex, 16); }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }
        return code >= 0x20 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF);
    }

    private static bool CheckboxStateConsistent(XElement sdt, XElement checkbox)
    {
        CheckboxParts parts;
        string error;
        if (!TryReadCheckbox(sdt, checkbox, out parts, out error)) return true; // untouched unsupported shapes are not verified
        XElement checkedElement = checkbox.Element(W14 + "checked");
        string value = checkedElement == null ? "0" : (string)checkedElement.Attribute(W14 + "val") ?? "0";
        bool isChecked = value == "1" || value == "true";
        return parts.Text.Value == (isChecked ? parts.CheckedGlyph : parts.UncheckedGlyph);
    }
}
