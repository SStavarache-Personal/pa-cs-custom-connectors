using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const int AbsoluteMaxInputBytes = 64 * 1024 * 1024;
    private const int DefaultMaxOutputCharacters = 2 * 1024 * 1024;
    private const int AbsoluteMaxOutputCharacters = 6 * 1024 * 1024;
    private const int MaximumPageCount = 1000;
    private const int MaximumDecodedStreamBytes = 64 * 1024 * 1024;
    private const long MaximumTotalDecodedStreamBytes = 192L * 1024 * 1024;
    private const int SoftDeadlineMilliseconds = 100000;
    private const int MaximumOcrPagesPerCall = 25;
    private const int MaximumTextFragmentsPerPage = 250000;
    private const int MaximumStructureElements = 500000;
    private const int MaximumStructureDepth = 128;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);
        if (string.Equals(operationId, "ExtractProductMonographOcr", StringComparison.Ordinal))
        {
            return await HandleExtractOcrAsync().ConfigureAwait(false);
        }
        else
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "UNKNOWN_OPERATION",
                "Unknown operation: " + operationId);
        }
    }

    private async Task<HttpResponseMessage> HandleExtractOcrAsync()
    {
        Stopwatch operationStopwatch = Stopwatch.StartNew();
        JObject request;
        try
        {
            if (this.Context.Request.Content == null)
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_JSON", "The request body must be a JSON object.");
            }

            string body = await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
            request = JObject.Parse(body);
        }
        catch (JsonException)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_JSON", "The request body must be a JSON object.");
        }

        string contentBytes = (string)request["contentBytes"];
        if (string.IsNullOrWhiteSpace(contentBytes))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "MISSING_CONTENT", "The contentBytes field is required.");
        }

        byte[] pdfBytes;
        try
        {
            pdfBytes = Convert.FromBase64String(RemoveDataUrlPrefix(contentBytes));
        }
        catch (FormatException)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_BASE64", "The contentBytes field is not valid base64.");
        }

        if (pdfBytes.Length > AbsoluteMaxInputBytes)
        {
            return CreateErrorResponse(
                HttpStatusCode.RequestEntityTooLarge,
                "PDF_TOO_LARGE",
                "The decoded PDF is " + pdfBytes.Length + " bytes; the connector limit is " + AbsoluteMaxInputBytes + " bytes.");
        }

        if (!LooksLikePdf(pdfBytes))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_PDF", "The decoded content does not contain a PDF header.");
        }

        ExtractionOptions options = new ExtractionOptions
        {
            StartPage = GetBoundedInteger(request, "startPage", 1, 1, MaximumPageCount),
            EndPage = null,
            IncludeArtifacts = false,
            IncludePageBreaks = false,
            ReturnPageChunks = true,
            RichMarkdown = true,
            FileName = GetOptionalString(request, "fileName", 1024),
            MaxPagesPerCall = GetBoundedInteger(request, "maxPagesPerCall", 25, 1, MaximumOcrPagesPerCall),
            LanguageHint = NormalizeLanguageHint(GetOptionalString(request, "languageHint", 8)),
            MinimumConfidence = GetBoundedDouble(request, "minimumConfidence", 0.86, 0.50, 0.99),
            SoftDeadlineMilliseconds = SoftDeadlineMilliseconds,
            Stopwatch = operationStopwatch,
            MaxOutputCharacters = GetBoundedInteger(
                request,
                "maxOutputCharacters",
                DefaultMaxOutputCharacters,
                1000,
                AbsoluteMaxOutputCharacters)
        };

        try
        {
            var extractor = new PdfTextDocument(pdfBytes, options, this.CancellationToken);
            return CreateJsonResponse(HttpStatusCode.OK, extractor.ExtractOcr());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PdfExtractionException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCode.InternalServerError,
                "PDF_OCR_FAILED",
                "The PDF could not be processed: " + ex.Message);
        }
    }

    private static string DecodeOperationId(string operationId)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return string.Empty;
        }

        try
        {
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(operationId));
            return string.Equals(decoded, "ExtractProductMonographOcr", StringComparison.Ordinal)
                ? decoded
                : operationId;
        }
        catch (FormatException)
        {
            return operationId;
        }
    }

    private static string RemoveDataUrlPrefix(string value)
    {
        string trimmed = value.Trim();
        int comma = trimmed.IndexOf(',');
        if (comma > 0 && trimmed.Substring(0, comma).IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return trimmed.Substring(comma + 1);
        }

        return trimmed;
    }

    private static bool LooksLikePdf(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length - 4, 1024);
        for (int i = 0; i < limit; i++)
        {
            if (bytes[i] == (byte)'%' && bytes[i + 1] == (byte)'P' && bytes[i + 2] == (byte)'D' &&
                bytes[i + 3] == (byte)'F' && bytes[i + 4] == (byte)'-')
            {
                return true;
            }
        }

        return false;
    }

    private static int GetBoundedInteger(JObject body, string name, int defaultValue, int minimum, int maximum)
    {
        JToken token = body[name];
        int value;
        if (token == null || token.Type == JTokenType.Null || !int.TryParse(token.ToString(), out value))
        {
            value = defaultValue;
        }

        if (value < minimum) return minimum;
        if (value > maximum) return maximum;
        return value;
    }

    private static double GetBoundedDouble(JObject body, string name, double defaultValue, double minimum, double maximum)
    {
        JToken token = body[name];
        double value;
        if (token == null || token.Type == JTokenType.Null || !double.TryParse(token.ToString(), out value)) value = defaultValue;
        if (value < minimum) return minimum;
        if (value > maximum) return maximum;
        return value;
    }

    private static string NormalizeLanguageHint(string value)
    {
        if (string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)) return "en";
        if (string.Equals(value, "fr", StringComparison.OrdinalIgnoreCase)) return "fr";
        return "auto";
    }

    private static string GetOptionalString(JObject body, string name, int maximumLength)
    {
        JToken token = body[name];
        if (token == null || token.Type == JTokenType.Null) return null;
        string value = token.ToString().Trim();
        if (value.Length == 0) return null;
        return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
    }

    private HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JToken body)
    {
        return new HttpResponseMessage(statusCode) { Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None)) };
    }

    private HttpResponseMessage CreateErrorResponse(HttpStatusCode statusCode, string code, string message)
    {
        return CreateJsonResponse(statusCode, new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        });
    }

    private sealed class ExtractionOptions
    {
        public int StartPage;
        public int? EndPage;
        public bool IncludeArtifacts;
        public bool IncludePageBreaks;
        public bool ReturnPageChunks;
        public bool RichMarkdown;
        public string FileName;
        public int MaxOutputCharacters;
        public int MaxPagesPerCall;
        public string LanguageHint;
        public double MinimumConfidence;
        public int SoftDeadlineMilliseconds;
        public Stopwatch Stopwatch;
    }

    private sealed class ExtractionResult
    {
        public string Text;
        public int PageCount;
        public int PagesExtracted;
        public int StartPage;
        public int EndPage;
        public bool HasTextLayer;
        public bool Truncated;
        public int TableCount;
        public bool UsedTaggedStructure;
        public JArray PageChunks = new JArray();
        public List<string> Warnings = new List<string>();
    }

    private sealed class PdfExtractionException : Exception
    {
        public PdfExtractionException(string code, string message) : base(message)
        {
            this.Code = code;
        }

        public string Code { get; private set; }
    }

    // The PDF object model is deliberately small: it covers the types needed for
    // page trees, resources, content streams, fonts, xref streams, and object streams.
    private abstract class PdfValue { }

    private sealed class PdfNull : PdfValue
    {
        public static readonly PdfNull Value = new PdfNull();
        private PdfNull() { }
    }

    private sealed class PdfBoolean : PdfValue
    {
        public PdfBoolean(bool value) { this.Value = value; }
        public bool Value;
    }

    private sealed class PdfNumber : PdfValue
    {
        public PdfNumber(double value, bool integer) { this.Value = value; this.IsInteger = integer; }
        public double Value;
        public bool IsInteger;
    }

    private sealed class PdfName : PdfValue
    {
        public PdfName(string value) { this.Value = value; }
        public string Value;
    }

    private sealed class PdfString : PdfValue
    {
        public PdfString(byte[] bytes) { this.Bytes = bytes; }
        public byte[] Bytes;
    }

    private sealed class PdfKeyword : PdfValue
    {
        public PdfKeyword(string value) { this.Value = value; }
        public string Value;
    }

    private sealed class PdfReference : PdfValue
    {
        public PdfReference(int objectNumber, int generation)
        {
            this.ObjectNumber = objectNumber;
            this.Generation = generation;
        }

        public int ObjectNumber;
        public int Generation;
    }

    private sealed class PdfArray : PdfValue
    {
        public readonly List<PdfValue> Items = new List<PdfValue>();
    }

    private sealed class PdfDictionary : PdfValue
    {
        public readonly Dictionary<string, PdfValue> Items = new Dictionary<string, PdfValue>(StringComparer.Ordinal);

        public PdfValue Get(string name)
        {
            PdfValue value;
            return this.Items.TryGetValue(name, out value) ? value : null;
        }
    }

    private sealed class PdfStream : PdfValue
    {
        public PdfStream(PdfDictionary dictionary, byte[] data)
        {
            this.Dictionary = dictionary;
            this.Data = data;
        }

        public PdfDictionary Dictionary;
        public byte[] Data;
        public byte[] DecodedData;
    }

    private sealed class XrefEntry
    {
        public int Type;
        public long Offset;
        public int Generation;
        public int ObjectStreamNumber;
        public int ObjectStreamIndex;
    }

    private sealed class PdfParser
    {
        private readonly byte[] _data;
        private readonly int _end;

        public PdfParser(byte[] data) : this(data, 0, data.Length) { }

        public PdfParser(byte[] data, int start, int length)
        {
            this._data = data;
            this.Position = start;
            this._end = Math.Min(data.Length, start + length);
        }

        public int Position { get; set; }

        public bool AtEnd
        {
            get { return this.Position >= this._end; }
        }

        public byte[] Data
        {
            get { return this._data; }
        }

        public void SkipWhitespaceAndComments()
        {
            while (this.Position < this._end)
            {
                byte current = this._data[this.Position];
                if (IsWhitespace(current))
                {
                    this.Position++;
                    continue;
                }

                if (current == (byte)'%')
                {
                    while (this.Position < this._end && this._data[this.Position] != 10 && this._data[this.Position] != 13)
                    {
                        this.Position++;
                    }
                    continue;
                }

                break;
            }
        }

        public bool StartsWith(string value)
        {
            this.SkipWhitespaceAndComments();
            return StartsWithAt(this.Position, value);
        }

        public bool StartsWithAt(int position, string value)
        {
            if (position < 0 || position + value.Length > this._end) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (this._data[position + i] != (byte)value[i]) return false;
            }
            return true;
        }

        public string ReadRawToken()
        {
            this.SkipWhitespaceAndComments();
            if (this.Position >= this._end) return null;

            int start = this.Position;
            byte current = this._data[this.Position];
            if (IsDelimiter(current))
            {
                if ((current == (byte)'<' || current == (byte)'>') && this.Position + 1 < this._end &&
                    this._data[this.Position + 1] == current)
                {
                    this.Position += 2;
                }
                else
                {
                    this.Position++;
                }
            }
            else
            {
                while (this.Position < this._end && !IsWhitespace(this._data[this.Position]) && !IsDelimiter(this._data[this.Position]))
                {
                    this.Position++;
                }
            }

            return Encoding.ASCII.GetString(this._data, start, this.Position - start);
        }

        public PdfValue ReadValue(bool allowReferences)
        {
            this.SkipWhitespaceAndComments();
            if (this.Position >= this._end) return null;

            byte current = this._data[this.Position];
            if (current == (byte)'/') return ReadName();
            if (current == (byte)'(') return ReadLiteralString();
            if (current == (byte)'[') return ReadArray(allowReferences);
            if (current == (byte)'<')
            {
                if (this.Position + 1 < this._end && this._data[this.Position + 1] == (byte)'<')
                {
                    return ReadDictionary(allowReferences);
                }
                return ReadHexString();
            }

            if (IsNumberStart(current))
            {
                int firstStart = this.Position;
                string firstToken = this.ReadRawToken();
                double firstNumber;
                if (!TryParsePdfNumber(firstToken, out firstNumber))
                {
                    return new PdfKeyword(firstToken);
                }

                bool firstInteger = IsIntegerToken(firstToken);
                int afterFirst = this.Position;
                if (allowReferences && firstInteger)
                {
                    this.SkipWhitespaceAndComments();
                    int secondStart = this.Position;
                    string secondToken = this.ReadRawToken();
                    double secondNumber;
                    if (secondToken != null && IsIntegerToken(secondToken) && TryParsePdfNumber(secondToken, out secondNumber))
                    {
                        this.SkipWhitespaceAndComments();
                        string thirdToken = this.ReadRawToken();
                        if (string.Equals(thirdToken, "R", StringComparison.Ordinal))
                        {
                            return new PdfReference((int)firstNumber, (int)secondNumber);
                        }
                    }

                    this.Position = afterFirst;
                }

                return new PdfNumber(firstNumber, firstInteger);
            }

            string token = this.ReadRawToken();
            if (string.Equals(token, "true", StringComparison.Ordinal)) return new PdfBoolean(true);
            if (string.Equals(token, "false", StringComparison.Ordinal)) return new PdfBoolean(false);
            if (string.Equals(token, "null", StringComparison.Ordinal)) return PdfNull.Value;
            return new PdfKeyword(token);
        }

        private PdfName ReadName()
        {
            this.Position++;
            var bytes = new List<byte>();
            while (this.Position < this._end)
            {
                byte current = this._data[this.Position];
                if (IsWhitespace(current) || IsDelimiter(current)) break;
                if (current == (byte)'#' && this.Position + 2 < this._end)
                {
                    int high = HexValue(this._data[this.Position + 1]);
                    int low = HexValue(this._data[this.Position + 2]);
                    if (high >= 0 && low >= 0)
                    {
                        bytes.Add((byte)((high << 4) | low));
                        this.Position += 3;
                        continue;
                    }
                }
                bytes.Add(current);
                this.Position++;
            }

            return new PdfName(Encoding.UTF8.GetString(bytes.ToArray()));
        }

        private PdfString ReadLiteralString()
        {
            this.Position++;
            int depth = 1;
            var bytes = new List<byte>();
            while (this.Position < this._end && depth > 0)
            {
                byte current = this._data[this.Position++];
                if (current == (byte)'\\')
                {
                    if (this.Position >= this._end) break;
                    byte escaped = this._data[this.Position++];
                    if (escaped == (byte)'n') bytes.Add(10);
                    else if (escaped == (byte)'r') bytes.Add(13);
                    else if (escaped == (byte)'t') bytes.Add(9);
                    else if (escaped == (byte)'b') bytes.Add(8);
                    else if (escaped == (byte)'f') bytes.Add(12);
                    else if (escaped == 10) { }
                    else if (escaped == 13)
                    {
                        if (this.Position < this._end && this._data[this.Position] == 10) this.Position++;
                    }
                    else if (escaped >= (byte)'0' && escaped <= (byte)'7')
                    {
                        int octal = escaped - (byte)'0';
                        int count = 1;
                        while (count < 3 && this.Position < this._end && this._data[this.Position] >= (byte)'0' && this._data[this.Position] <= (byte)'7')
                        {
                            octal = (octal << 3) + this._data[this.Position] - (byte)'0';
                            this.Position++;
                            count++;
                        }
                        bytes.Add((byte)(octal & 0xff));
                    }
                    else bytes.Add(escaped);
                    continue;
                }

                if (current == (byte)'(')
                {
                    depth++;
                    bytes.Add(current);
                }
                else if (current == (byte)')')
                {
                    depth--;
                    if (depth > 0) bytes.Add(current);
                }
                else bytes.Add(current);
            }

            return new PdfString(bytes.ToArray());
        }

        private PdfString ReadHexString()
        {
            this.Position++;
            var bytes = new List<byte>();
            int high = -1;
            while (this.Position < this._end)
            {
                byte current = this._data[this.Position++];
                if (current == (byte)'>') break;
                if (IsWhitespace(current)) continue;
                int value = HexValue(current);
                if (value < 0) continue;
                if (high < 0) high = value;
                else
                {
                    bytes.Add((byte)((high << 4) | value));
                    high = -1;
                }
            }
            if (high >= 0) bytes.Add((byte)(high << 4));
            return new PdfString(bytes.ToArray());
        }

        private PdfArray ReadArray(bool allowReferences)
        {
            this.Position++;
            var result = new PdfArray();
            while (this.Position < this._end)
            {
                this.SkipWhitespaceAndComments();
                if (this.Position < this._end && this._data[this.Position] == (byte)']')
                {
                    this.Position++;
                    break;
                }
                PdfValue value = this.ReadValue(allowReferences);
                if (value == null) break;
                result.Items.Add(value);
            }
            return result;
        }

        private PdfDictionary ReadDictionary(bool allowReferences)
        {
            this.Position += 2;
            var result = new PdfDictionary();
            while (this.Position < this._end)
            {
                this.SkipWhitespaceAndComments();
                if (this.Position + 1 < this._end && this._data[this.Position] == (byte)'>' && this._data[this.Position + 1] == (byte)'>')
                {
                    this.Position += 2;
                    break;
                }

                PdfName key = this.ReadValue(false) as PdfName;
                if (key == null)
                {
                    this.ReadRawToken();
                    continue;
                }
                PdfValue value = this.ReadValue(allowReferences);
                result.Items[key.Value] = value ?? PdfNull.Value;
            }
            return result;
        }

        public void SkipInlineImage()
        {
            // Parse the inline-image dictionary until ID, then look for a delimiter-bounded EI.
            while (!this.AtEnd)
            {
                string token = this.ReadRawToken();
                if (string.Equals(token, "ID", StringComparison.Ordinal)) break;
            }
            if (this.Position < this._end && IsWhitespace(this._data[this.Position])) this.Position++;
            for (int i = this.Position + 1; i + 2 < this._end; i++)
            {
                if (this._data[i] == (byte)'E' && this._data[i + 1] == (byte)'I' &&
                    IsWhitespace(this._data[i - 1]) && IsWhitespace(this._data[i + 2]))
                {
                    this.Position = i + 2;
                    return;
                }
            }
            this.Position = this._end;
        }

        public int FindBytes(string value, int start)
        {
            if (string.IsNullOrEmpty(value)) return -1;
            for (int i = Math.Max(0, start); i + value.Length <= this._end; i++)
            {
                if (StartsWithAt(i, value)) return i;
            }
            return -1;
        }

        private static bool TryParsePdfNumber(string value, out double number)
        {
            number = 0;
            if (string.IsNullOrEmpty(value)) return false;
            int position = 0;
            bool negative = false;
            if (value[position] == '+' || value[position] == '-')
            {
                negative = value[position] == '-';
                position++;
            }
            bool any = false;
            while (position < value.Length && value[position] >= '0' && value[position] <= '9')
            {
                any = true;
                number = number * 10 + value[position] - '0';
                position++;
            }
            if (position < value.Length && value[position] == '.')
            {
                position++;
                double factor = 0.1;
                while (position < value.Length && value[position] >= '0' && value[position] <= '9')
                {
                    any = true;
                    number += (value[position] - '0') * factor;
                    factor *= 0.1;
                    position++;
                }
            }
            if (!any || position != value.Length) return false;
            if (negative) number = -number;
            return true;
        }

        private static bool IsIntegerToken(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            int start = value[0] == '+' || value[0] == '-' ? 1 : 0;
            if (start == value.Length) return false;
            for (int i = start; i < value.Length; i++)
            {
                if (value[i] < '0' || value[i] > '9') return false;
            }
            return true;
        }

        private static bool IsNumberStart(byte value)
        {
            return (value >= (byte)'0' && value <= (byte)'9') || value == (byte)'+' || value == (byte)'-' || value == (byte)'.';
        }

        private static bool IsDelimiter(byte value)
        {
            return value == (byte)'(' || value == (byte)')' || value == (byte)'<' || value == (byte)'>' ||
                value == (byte)'[' || value == (byte)']' || value == (byte)'{' || value == (byte)'}' ||
                value == (byte)'/' || value == (byte)'%';
        }

        private static bool IsWhitespace(byte value)
        {
            return value == 0 || value == 9 || value == 10 || value == 12 || value == 13 || value == 32;
        }

        private static int HexValue(byte value)
        {
            if (value >= (byte)'0' && value <= (byte)'9') return value - (byte)'0';
            if (value >= (byte)'A' && value <= (byte)'F') return value - (byte)'A' + 10;
            if (value >= (byte)'a' && value <= (byte)'f') return value - (byte)'a' + 10;
            return -1;
        }
    }

    private sealed class PdfPage
    {
        public int ObjectNumber;
        public PdfDictionary Dictionary;
        public PdfDictionary Resources;
        public double Width = 612;
        public double Height = 792;
        public int Rotation;
    }

    private sealed class SemanticInfo
    {
        public int StructureOrder;
        public int BlockId;
        public string BlockRole;
        public int TableId;
        public int RowId;
        public int CellId;
        public string CellRole;
        public int ListId;
        public int ListItemId;
        public bool IsListLabel;
    }

    private sealed class SemanticCell
    {
        public int Id;
        public int Order;
        public int PageObjectNumber;
        public string Role;
        public int ColumnSpan = 1;
        public int RowSpan = 1;
    }

    private sealed class SemanticRow
    {
        public int Id;
        public int Order;
        public int PageObjectNumber;
        public readonly List<SemanticCell> Cells = new List<SemanticCell>();
    }

    private sealed class SemanticTable
    {
        public int Id;
        public int Order;
        public readonly List<SemanticRow> Rows = new List<SemanticRow>();
    }

    private sealed class SemanticTraversalState
    {
        public int PageObjectNumber;
        public int BlockId;
        public string BlockRole;
        public int TableId;
        public int RowId;
        public int CellId;
        public string CellRole;
        public int ListId;
        public int ListItemId;
        public bool IsListLabel;

        public SemanticTraversalState Clone()
        {
            return (SemanticTraversalState)this.MemberwiseClone();
        }
    }

    private sealed class PageContentResult
    {
        public string Text;
        public int TableCount;
        public bool UsedTaggedStructure;
    }

    private sealed class PdfTextDocument
    {
        private readonly byte[] _data;
        private readonly ExtractionOptions _options;
        private readonly System.Threading.CancellationToken _cancellationToken;
        private readonly Dictionary<int, XrefEntry> _xref = new Dictionary<int, XrefEntry>();
        private readonly Dictionary<int, PdfValue> _objectCache = new Dictionary<int, PdfValue>();
        private readonly Dictionary<int, Dictionary<int, PdfValue>> _objectStreamCache = new Dictionary<int, Dictionary<int, PdfValue>>();
        private readonly HashSet<long> _visitedXrefOffsets = new HashSet<long>();
        private readonly List<string> _warnings = new List<string>();
        private readonly Dictionary<long, SemanticInfo> _semanticByPageAndMcid = new Dictionary<long, SemanticInfo>();
        private readonly Dictionary<int, SemanticTable> _semanticTables = new Dictionary<int, SemanticTable>();
        private readonly Dictionary<string, string> _semanticRoleMap = new Dictionary<string, string>(StringComparer.Ordinal);
        private long _totalDecodedStreamBytes;
        private int _semanticOrder;
        private int _semanticSyntheticId = 1000000000;
        private int _semanticElementCount;
        private PdfDictionary _trailer;

        public PdfTextDocument(byte[] data, ExtractionOptions options, System.Threading.CancellationToken cancellationToken)
        {
            this._data = data;
            this._options = options;
            this._cancellationToken = cancellationToken;
        }

        public SemanticInfo GetSemanticInfo(int pageObjectNumber, int mcid)
        {
            SemanticInfo info;
            return this._semanticByPageAndMcid.TryGetValue(SemanticKey(pageObjectNumber, mcid), out info) ? info : null;
        }

        public List<SemanticTable> GetSemanticTablesForPage(int pageObjectNumber)
        {
            return this._semanticTables.Values
                .Where(table => table.Rows.Any(row => row.PageObjectNumber == pageObjectNumber ||
                    row.Cells.Any(cell => cell.PageObjectNumber == pageObjectNumber)))
                .OrderBy(table => table.Order)
                .ToList();
        }

        private void BuildSemanticStructure(PdfDictionary catalog)
        {
            this._semanticByPageAndMcid.Clear();
            this._semanticTables.Clear();
            this._semanticRoleMap.Clear();
            this._semanticOrder = 0;
            this._semanticElementCount = 0;

            PdfDictionary structureRoot = AsDictionary(Resolve(catalog.Get("StructTreeRoot")));
            if (structureRoot == null) return;
            PdfDictionary roleMap = AsDictionary(Resolve(structureRoot.Get("RoleMap")));
            if (roleMap != null)
            {
                foreach (KeyValuePair<string, PdfValue> item in roleMap.Items)
                {
                    string mapped = GetName(Resolve(item.Value));
                    if (!string.IsNullOrEmpty(mapped)) this._semanticRoleMap[item.Key] = mapped;
                }
            }

            WalkStructure(
                structureRoot.Get("K"),
                new SemanticTraversalState(),
                new HashSet<int>(),
                0);
        }

        private void WalkStructure(
            PdfValue value,
            SemanticTraversalState inherited,
            HashSet<int> visited,
            int depth)
        {
            if (value == null) return;
            if (depth > MaximumStructureDepth)
            {
                throw new PdfExtractionException("PDF_RESOURCE_LIMIT", "The tagged structure tree exceeded the processing depth limit.");
            }
            PdfArray array = Resolve(value) as PdfArray;
            if (array != null)
            {
                foreach (PdfValue item in array.Items) WalkStructure(item, inherited, visited, depth + 1);
                return;
            }

            PdfNumber mcidNumber = Resolve(value) as PdfNumber;
            if (mcidNumber != null)
            {
                RegisterSemanticMcid(inherited.PageObjectNumber, (int)mcidNumber.Value, inherited);
                return;
            }

            PdfReference reference = value as PdfReference;
            if (reference != null && !visited.Add(reference.ObjectNumber)) return;
            PdfDictionary element = AsDictionary(Resolve(value));
            if (element == null) return;
            if (++this._semanticElementCount > MaximumStructureElements)
            {
                throw new PdfExtractionException("PDF_RESOURCE_LIMIT", "The tagged structure tree exceeded the element processing limit.");
            }

            var state = inherited.Clone();
            PdfReference pageReference = element.Get("Pg") as PdfReference;
            if (pageReference != null) state.PageObjectNumber = pageReference.ObjectNumber;

            string type = GetName(Resolve(element.Get("Type")));
            string role = GetName(Resolve(element.Get("S")));
            string mappedRole;
            if (!string.IsNullOrEmpty(role) && this._semanticRoleMap.TryGetValue(role, out mappedRole)) role = mappedRole;

            if ((string.Equals(type, "MCR", StringComparison.Ordinal) || string.IsNullOrEmpty(role)) && element.Get("MCID") != null)
            {
                int mcid = GetInt(Resolve(element.Get("MCID")), -1);
                if (mcid >= 0) RegisterSemanticMcid(state.PageObjectNumber, mcid, state);
                return;
            }

            int id = reference == null ? this._semanticSyntheticId++ : reference.ObjectNumber;
            if (IsBlockRole(role))
            {
                state.BlockId = id;
                state.BlockRole = role;
            }
            if (string.Equals(role, "L", StringComparison.Ordinal)) state.ListId = id;
            else if (string.Equals(role, "LI", StringComparison.Ordinal)) state.ListItemId = id;
            else if (string.Equals(role, "Lbl", StringComparison.Ordinal)) state.IsListLabel = true;

            if (string.Equals(role, "Table", StringComparison.Ordinal))
            {
                state.TableId = id;
                state.RowId = 0;
                state.CellId = 0;
                state.CellRole = null;
                if (!this._semanticTables.ContainsKey(id))
                {
                    this._semanticTables[id] = new SemanticTable { Id = id, Order = this._semanticOrder };
                }
            }
            else if (string.Equals(role, "TR", StringComparison.Ordinal) && state.TableId != 0)
            {
                state.RowId = id;
                state.CellId = 0;
                state.CellRole = null;
                SemanticTable table;
                if (this._semanticTables.TryGetValue(state.TableId, out table) && !table.Rows.Any(row => row.Id == id))
                {
                    table.Rows.Add(new SemanticRow
                    {
                        Id = id,
                        Order = this._semanticOrder,
                        PageObjectNumber = state.PageObjectNumber
                    });
                }
            }
            else if ((string.Equals(role, "TH", StringComparison.Ordinal) || string.Equals(role, "TD", StringComparison.Ordinal)) &&
                state.TableId != 0 && state.RowId != 0)
            {
                state.CellId = id;
                state.CellRole = role;
                SemanticTable table;
                SemanticRow row;
                if (this._semanticTables.TryGetValue(state.TableId, out table) &&
                    (row = table.Rows.FirstOrDefault(candidate => candidate.Id == state.RowId)) != null &&
                    !row.Cells.Any(cell => cell.Id == id))
                {
                    row.Cells.Add(new SemanticCell
                    {
                        Id = id,
                        Order = this._semanticOrder,
                        PageObjectNumber = state.PageObjectNumber,
                        Role = role,
                        ColumnSpan = Math.Max(1, FindStructureAttribute(element.Get("A"), "ColSpan", 1, 0)),
                        RowSpan = Math.Max(1, FindStructureAttribute(element.Get("A"), "RowSpan", 1, 0))
                    });
                }
            }

            WalkStructure(element.Get("K"), state, visited, depth + 1);
        }

        private int FindStructureAttribute(PdfValue value, string name, int defaultValue, int depth)
        {
            if (depth > 16) return defaultValue;
            PdfValue resolved = Resolve(value);
            PdfNumber number = resolved as PdfNumber;
            if (number != null) return (int)number.Value;
            PdfArray array = resolved as PdfArray;
            if (array != null)
            {
                foreach (PdfValue item in array.Items)
                {
                    int found = FindStructureAttribute(item, name, -1, depth + 1);
                    if (found >= 0) return found;
                }
                return defaultValue;
            }
            PdfDictionary dictionary = AsDictionary(resolved);
            if (dictionary == null) return defaultValue;
            PdfNumber attribute = Resolve(dictionary.Get(name)) as PdfNumber;
            return attribute == null ? defaultValue : (int)attribute.Value;
        }

        private void RegisterSemanticMcid(int pageObjectNumber, int mcid, SemanticTraversalState state)
        {
            if (pageObjectNumber <= 0 || mcid < 0) return;
            this._semanticByPageAndMcid[SemanticKey(pageObjectNumber, mcid)] = new SemanticInfo
            {
                StructureOrder = this._semanticOrder++,
                BlockId = state.BlockId,
                BlockRole = state.BlockRole,
                TableId = state.TableId,
                RowId = state.RowId,
                CellId = state.CellId,
                CellRole = state.CellRole,
                ListId = state.ListId,
                ListItemId = state.ListItemId,
                IsListLabel = state.IsListLabel
            };
        }

        private static bool IsBlockRole(string role)
        {
            if (string.IsNullOrEmpty(role)) return false;
            return role == "P" || role == "Caption" || role == "TOCI" || role == "Quote" || role == "Code" ||
                role == "LBody" || role == "Lbl" ||
                (role.Length == 2 && role[0] == 'H' && role[1] >= '1' && role[1] <= '6');
        }

        private static long SemanticKey(int pageObjectNumber, int mcid)
        {
            return ((long)pageObjectNumber << 32) ^ (uint)mcid;
        }

        public JObject ExtractOcr()
        {
            this._cancellationToken.ThrowIfCancellationRequested();
            long startXref = FindStartXref(this._data);
            if (startXref >= 0)
            {
                try
                {
                    LoadXrefAt(startXref, true);
                }
                catch (Exception ex)
                {
                    this._warnings.Add("Xref recovery was required: " + ex.Message);
                    this._xref.Clear();
                    ScanIndirectObjects();
                }
            }
            else
            {
                this._warnings.Add("No startxref marker was found; indirect objects were recovered by scanning.");
                ScanIndirectObjects();
            }

            if (this._trailer == null) FindTrailerByScanning();
            if (this._trailer == null) throw new PdfExtractionException("PDF_STRUCTURE_INVALID", "The PDF trailer could not be located.");
            if (this._trailer.Get("Encrypt") != null) throw new PdfExtractionException("ENCRYPTED_PDF_UNSUPPORTED", "Encrypted or password-protected PDFs are not supported.");

            PdfDictionary catalog = AsDictionary(Resolve(this._trailer.Get("Root")));
            if (catalog == null) throw new PdfExtractionException("PDF_STRUCTURE_INVALID", "The document catalog could not be resolved.");
            var pages = new List<PdfPage>();
            CollectPages(catalog.Get("Pages"), null, null, 0, pages, new HashSet<int>());
            if (pages.Count == 0) throw new PdfExtractionException("PDF_STRUCTURE_INVALID", "The PDF does not contain any resolvable pages.");
            if (pages.Count > MaximumPageCount) throw new PdfExtractionException("TOO_MANY_PAGES", "The PDF contains more than " + MaximumPageCount + " pages.");

            BuildSemanticStructure(catalog);
            int startPage = Math.Min(Math.Max(1, this._options.StartPage), pages.Count);
            int pageIndex = startPage - 1;
            int outputCharacters = 0;
            int ocrPagesProcessed = 0;
            bool deadlineReached = false;
            var chunks = new JArray();
            JObject documentMetadata = CreateDocumentMetadata(catalog, pages.Count);

            while (pageIndex < pages.Count)
            {
                this._cancellationToken.ThrowIfCancellationRequested();
                if (DeadlineReached())
                {
                    deadlineReached = true;
                    break;
                }

                PdfPage page = pages[pageIndex];
                var pageWarnings = new List<string>(this._warnings);
                var interpreter = new ContentInterpreter(this, page, false, this._cancellationToken);
                PageContentResult textResult = interpreter.Extract(true);
                string text = (textResult.Text ?? string.Empty).Trim();
                bool hasAnyTextLayer = text.Length > 0;
                bool usableTextLayer = IsUsableText(text);
                string extractionMethod = "textLayer";
                string ocrStatus = "notRequired";
                double confidence = usableTextLayer ? 1.0 : 0.0;
                JObject sourceImage = null;

                if (!usableTextLayer)
                {
                    if (ocrPagesProcessed >= this._options.MaxPagesPerCall)
                    {
                        break;
                    }

                    ocrPagesProcessed++;
                    RasterDecodeResult raster = DecodeDominantPageImage(page);
                    sourceImage = raster.ToJson();
                    if (raster.Image == null)
                    {
                        text = string.Empty;
                        extractionMethod = "none";
                        ocrStatus = raster.Status;
                        if (!string.IsNullOrEmpty(raster.Warning)) pageWarnings.Add(raster.Warning);
                    }
                    else if (DeadlineReached())
                    {
                        text = string.Empty;
                        extractionMethod = "ocr";
                        ocrStatus = "deadlineExceeded";
                        deadlineReached = true;
                        pageWarnings.Add("The OCR soft deadline was reached before recognition could start.");
                    }
                    else
                    {
                        OcrRecognitionResult recognized = OcrEngine.Recognize(
                            raster.Image,
                            this._options.LanguageHint,
                            this._options.MinimumConfidence,
                            this._options.Stopwatch,
                            this._options.SoftDeadlineMilliseconds,
                            this._cancellationToken);
                        extractionMethod = "ocr";
                        ocrStatus = recognized.Status;
                        confidence = recognized.Confidence;
                        text = recognized.Status == "recognized" ? recognized.Text.Trim() : string.Empty;
                        foreach (string warning in recognized.Warnings) pageWarnings.Add(warning);
                        if (recognized.Status == "deadlineExceeded") deadlineReached = true;
                    }
                }

                int remaining = Math.Max(0, this._options.MaxOutputCharacters - outputCharacters);
                bool truncated = text.Length > remaining;
                if (truncated)
                {
                    text = text.Substring(0, remaining);
                    pageWarnings.Add("Output was truncated at maxOutputCharacters.");
                }
                outputCharacters += text.Length;

                JObject metadata = (JObject)documentMetadata.DeepClone();
                metadata["pageNumber"] = pageIndex + 1;
                chunks.Add(new JObject
                {
                    ["metadata"] = metadata,
                    ["page"] = new JObject
                    {
                        ["width"] = page.Width,
                        ["height"] = page.Height,
                        ["rotation"] = page.Rotation
                    },
                    ["contentType"] = "text/markdown",
                    ["text"] = text,
                    ["characterCount"] = text.Length,
                    ["tableCount"] = usableTextLayer ? textResult.TableCount : 0,
                    ["usedTaggedStructure"] = usableTextLayer && textResult.UsedTaggedStructure,
                    ["hasTextLayer"] = hasAnyTextLayer,
                    ["truncated"] = truncated,
                    ["extractionMethod"] = extractionMethod,
                    ["ocrStatus"] = ocrStatus,
                    ["confidence"] = Math.Round(confidence, 6),
                    ["sourceImage"] = sourceImage,
                    ["warnings"] = new JArray(pageWarnings.Distinct())
                });

                pageIndex++;
                if (truncated || deadlineReached) break;
            }

            int? nextPage = pageIndex < pages.Count ? (int?)(pageIndex + 1) : null;
            int? processedStartPage = chunks.Count > 0 ? (int?)startPage : null;
            int? processedEndPage = chunks.Count > 0 ? (int?)pageIndex : null;
            return new JObject
            {
                ["pageCount"] = pages.Count,
                ["sourcePageCount"] = pages.Count,
                ["startPage"] = startPage,
                ["pagesReturned"] = chunks.Count,
                ["processedRange"] = new JObject
                {
                    ["startPage"] = processedStartPage.HasValue ? new JValue(processedStartPage.Value) : JValue.CreateNull(),
                    ["endPage"] = processedEndPage.HasValue ? new JValue(processedEndPage.Value) : JValue.CreateNull(),
                    ["pagesReturned"] = chunks.Count,
                    ["ocrPagesProcessed"] = ocrPagesProcessed
                },
                ["completed"] = !nextPage.HasValue,
                ["nextPage"] = nextPage.HasValue ? new JValue(nextPage.Value) : JValue.CreateNull(),
                ["nextStartPage"] = nextPage.HasValue ? new JValue(nextPage.Value) : JValue.CreateNull(),
                ["hasMorePages"] = nextPage.HasValue,
                ["deadlineReached"] = deadlineReached,
                ["elapsedMilliseconds"] = this._options.Stopwatch.ElapsedMilliseconds,
                ["pages"] = chunks,
                ["warnings"] = new JArray(this._warnings.Distinct())
            };
        }

        private bool DeadlineReached()
        {
            return this._options.Stopwatch != null && this._options.Stopwatch.ElapsedMilliseconds >= this._options.SoftDeadlineMilliseconds;
        }

        private static bool IsUsableText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            int useful = 0;
            int replacements = 0;
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c)) useful++;
                if (c == '\uFFFD') replacements++;
            }
            return useful >= 12 && replacements * 20 < Math.Max(1, value.Length);
        }

        private RasterDecodeResult DecodeDominantPageImage(PdfPage page)
        {
            var candidates = new List<ImageCandidate>();
            CollectImageCandidates(page.Resources, candidates, new HashSet<PdfStream>(), 0);
            if (candidates.Count == 0)
            {
                return RasterDecodeResult.Failure("noImage", "No image XObject was available for OCR.");
            }

            candidates = candidates.OrderByDescending(item => item.Area).ToList();
            ImageCandidate dominant = candidates[0];
            long totalArea = candidates.Sum(item => item.Area);
            if (dominant.Width < 200 || dominant.Height < 200 || (candidates.Count > 1 && dominant.Area * 100 < totalArea * 65))
            {
                return RasterDecodeResult.Failure("unsupportedPageComposition", "The page does not contain one dominant full-page raster image.", dominant);
            }

            double pageAspect = page.Width / Math.Max(1.0, page.Height);
            double imageAspect = dominant.Width / Math.Max(1.0, (double)dominant.Height);
            double aspectRatio = Math.Max(pageAspect, imageAspect) / Math.Max(0.0001, Math.Min(pageAspect, imageAspect));
            if (aspectRatio > 1.45)
            {
                return RasterDecodeResult.Failure("unsupportedPageComposition", "The dominant image aspect ratio does not match the PDF page.", dominant);
            }

            try
            {
                GrayImage image = DecodeImage(dominant);
                if (image == null) return RasterDecodeResult.Failure("unsupportedImageFilter", "The dominant image uses an unsupported filter, color space, or bit depth.", dominant);
                image = image.Rotate(page.Rotation);
                double dpiX = dominant.Width * 72.0 / Math.Max(1.0, page.Width);
                double dpiY = dominant.Height * 72.0 / Math.Max(1.0, page.Height);
                dominant.Dpi = (dpiX + dpiY) / 2.0;
                return RasterDecodeResult.Success(image, dominant);
            }
            catch (PlatformNotSupportedException ex)
            {
                return RasterDecodeResult.Failure("codecUnavailable", "The System.Drawing image codec is unavailable in this runtime: " + ex.Message, dominant);
            }
            catch (Exception ex)
            {
                return RasterDecodeResult.Failure("imageDecodeFailed", "The dominant image could not be decoded: " + ex.Message, dominant);
            }
        }

        private void CollectImageCandidates(PdfDictionary resources, List<ImageCandidate> result, HashSet<PdfStream> visited, int depth)
        {
            if (resources == null || depth > 8) return;
            PdfDictionary xobjects = AsDictionary(Resolve(resources.Get("XObject")));
            if (xobjects == null) return;
            foreach (KeyValuePair<string, PdfValue> pair in xobjects.Items)
            {
                PdfStream stream = Resolve(pair.Value) as PdfStream;
                if (stream == null || !visited.Add(stream)) continue;
                string subtype = GetName(Resolve(stream.Dictionary.Get("Subtype")));
                if (subtype == "Image")
                {
                    int width = GetInt(Resolve(stream.Dictionary.Get("Width")), 0);
                    int height = GetInt(Resolve(stream.Dictionary.Get("Height")), 0);
                    if (width > 0 && height > 0)
                    {
                        result.Add(new ImageCandidate
                        {
                            Stream = stream,
                            Width = width,
                            Height = height,
                            Filter = string.Join("+", GetFilterNames(stream))
                        });
                    }
                }
                else if (subtype == "Form")
                {
                    PdfDictionary formResources = AsDictionary(Resolve(stream.Dictionary.Get("Resources"))) ?? resources;
                    CollectImageCandidates(formResources, result, visited, depth + 1);
                }
            }
        }

        private List<string> GetFilterNames(PdfStream stream)
        {
            var result = new List<string>();
            PdfValue value = Resolve(stream.Dictionary.Get("Filter"));
            PdfName single = value as PdfName;
            PdfArray many = value as PdfArray;
            if (single != null) result.Add(single.Value);
            else if (many != null)
            {
                foreach (PdfValue item in many.Items)
                {
                    PdfName name = Resolve(item) as PdfName;
                    if (name != null) result.Add(name.Value);
                }
            }
            return result;
        }

        private GrayImage DecodeImage(ImageCandidate candidate)
        {
            PdfStream stream = candidate.Stream;
            List<string> filters = GetFilterNames(stream);
            if (filters.Any(filter => filter == "JBIG2Decode" || filter == "JPXDecode")) return null;
            if (filters.Any(filter => filter == "CCITTFaxDecode" || filter == "CCF"))
            {
                return DecodeCcittImage(stream, candidate.Width, candidate.Height, filters);
            }
            if (filters.Any(filter => filter == "DCTDecode" || filter == "DCT"))
            {
                byte[] encoded = DecodeImagePrefix(stream.Data, filters, "DCTDecode", "DCT");
                return DecodeDrawingImage(encoded);
            }

            byte[] samples = DecodeStream(stream);
            return DecodeSampleImage(stream.Dictionary, samples, candidate.Width, candidate.Height);
        }

        private static byte[] DecodeImagePrefix(byte[] data, List<string> filters, string stopLong, string stopShort)
        {
            byte[] current = data;
            foreach (string filter in filters)
            {
                if (filter == stopLong || filter == stopShort) break;
                if (filter == "ASCIIHexDecode" || filter == "AHx") current = DecodeAsciiHex(current);
                else if (filter == "ASCII85Decode" || filter == "A85") current = DecodeAscii85(current);
                else if (filter == "RunLengthDecode" || filter == "RL") current = DecodeRunLength(current, MaximumDecodedStreamBytes);
                else throw new InvalidDataException("Unsupported image prefix filter: " + filter + ".");
            }
            return current;
        }

        private GrayImage DecodeCcittImage(PdfStream stream, int width, int height, List<string> filters)
        {
            byte[] encoded = DecodeImagePrefix(stream.Data, filters, "CCITTFaxDecode", "CCF");
            PdfValue parmsValue = Resolve(stream.Dictionary.Get("DecodeParms"));
            PdfArray parmsArray = parmsValue as PdfArray;
            int filterIndex = filters.FindIndex(item => item == "CCITTFaxDecode" || item == "CCF");
            PdfDictionary parms = null;
            if (parmsArray != null && filterIndex >= 0 && filterIndex < parmsArray.Items.Count)
            {
                parms = AsDictionary(Resolve(parmsArray.Items[filterIndex]));
            }
            else
            {
                parms = AsDictionary(parmsValue);
            }
            int k = parms == null ? 0 : GetInt(Resolve(parms.Get("K")), 0);
            bool blackIs1 = parms != null && IsTrue(Resolve(parms.Get("BlackIs1")));
            byte[] tiff = BuildCcittTiff(encoded, width, height, k, blackIs1);
            return DecodeDrawingImage(tiff);
        }

        private static bool IsTrue(PdfValue value)
        {
            PdfBoolean boolean = value as PdfBoolean;
            return boolean != null && boolean.Value;
        }

        private static byte[] BuildCcittTiff(byte[] compressed, int width, int height, int k, bool blackIs1)
        {
            ushort entryCount = (ushort)(k >= 0 ? 12 : 11);
            uint rationalOffset = (uint)(8 + 2 + entryCount * 12 + 4);
            uint stripOffset = rationalOffset + 16;
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.ASCII, true))
            {
                writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write((uint)8);
                writer.Write(entryCount);
                WriteTiffEntry(writer, 256, 4, 1, (uint)width);
                WriteTiffEntry(writer, 257, 4, 1, (uint)height);
                WriteTiffEntry(writer, 258, 3, 1, 1);
                WriteTiffEntry(writer, 259, 3, 1, (uint)(k < 0 ? 4 : 3));
                WriteTiffEntry(writer, 262, 3, 1, (uint)(blackIs1 ? 0 : 1));
                WriteTiffEntry(writer, 266, 3, 1, 1);
                WriteTiffEntry(writer, 273, 4, 1, stripOffset);
                WriteTiffEntry(writer, 278, 4, 1, (uint)height);
                WriteTiffEntry(writer, 279, 4, 1, (uint)compressed.Length);
                if (k >= 0) WriteTiffEntry(writer, 292, 4, 1, (uint)(k > 0 ? 1 : 0));
                WriteTiffEntry(writer, 282, 5, 1, rationalOffset);
                WriteTiffEntry(writer, 283, 5, 1, rationalOffset + 8);
                writer.Write((uint)0);
                writer.Write((uint)300); writer.Write((uint)1);
                writer.Write((uint)300); writer.Write((uint)1);
                writer.Write(compressed);
                writer.Flush();
                return output.ToArray();
            }
        }

        private static void WriteTiffEntry(BinaryWriter writer, ushort tag, ushort type, uint count, uint value)
        {
            writer.Write(tag); writer.Write(type); writer.Write(count); writer.Write(value);
        }

        private static GrayImage DecodeDrawingImage(byte[] encoded)
        {
            using (var input = new MemoryStream(encoded, false))
            using (Image image = Image.FromStream(input, false, true))
            using (var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawImage(image, 0, 0, bitmap.Width, bitmap.Height);
                }
                using (var bmp = new MemoryStream())
                {
                    bitmap.Save(bmp, ImageFormat.Bmp);
                    return GrayImage.FromBmp(bmp.ToArray());
                }
            }
        }

        private GrayImage DecodeSampleImage(PdfDictionary dictionary, byte[] samples, int width, int height)
        {
            int bits = GetInt(Resolve(dictionary.Get("BitsPerComponent")), 8);
            PdfValue colorValue = Resolve(dictionary.Get("ColorSpace"));
            string color = GetName(colorValue);
            PdfArray colorArray = colorValue as PdfArray;
            if (color == null && colorArray != null && colorArray.Items.Count > 0) color = GetName(Resolve(colorArray.Items[0]));
            int components = color == "DeviceRGB" || color == "RGB" ? 3 : color == "DeviceGray" || color == "G" || color == null ? 1 : 0;
            if (components == 0 || (bits != 1 && bits != 8)) return null;
            int rowBytes = (width * components * bits + 7) / 8;
            if (samples.Length < rowBytes * height) return null;
            var pixels = new byte[width * height];
            bool invert = HasInvertedDecode(dictionary);
            for (int y = 0; y < height; y++)
            {
                int row = y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    int gray;
                    if (bits == 1)
                    {
                        int bit = (samples[row + (x >> 3)] >> (7 - (x & 7))) & 1;
                        gray = bit == 0 ? 0 : 255;
                    }
                    else if (components == 1)
                    {
                        gray = samples[row + x];
                    }
                    else
                    {
                        int offset = row + x * 3;
                        gray = (samples[offset] * 299 + samples[offset + 1] * 587 + samples[offset + 2] * 114) / 1000;
                    }
                    pixels[y * width + x] = (byte)(invert ? 255 - gray : gray);
                }
            }
            return new GrayImage(width, height, pixels);
        }

        private bool HasInvertedDecode(PdfDictionary dictionary)
        {
            PdfArray decode = Resolve(dictionary.Get("Decode")) as PdfArray;
            return decode != null && decode.Items.Count >= 2 && GetNumber(Resolve(decode.Items[0]), 0) > GetNumber(Resolve(decode.Items[1]), 1);
        }

        public ExtractionResult Extract()
        {
            this._cancellationToken.ThrowIfCancellationRequested();
            long startXref = FindStartXref(this._data);
            if (startXref >= 0)
            {
                try
                {
                    LoadXrefAt(startXref, true);
                }
                catch (Exception ex)
                {
                    this._warnings.Add("Xref recovery was required: " + ex.Message);
                    this._xref.Clear();
                    ScanIndirectObjects();
                }
            }
            else
            {
                this._warnings.Add("No startxref marker was found; indirect objects were recovered by scanning.");
                ScanIndirectObjects();
            }

            if (this._trailer == null)
            {
                FindTrailerByScanning();
            }
            if (this._trailer == null)
            {
                throw new PdfExtractionException("PDF_STRUCTURE_INVALID", "The PDF trailer could not be located.");
            }
            if (this._trailer.Get("Encrypt") != null)
            {
                throw new PdfExtractionException("ENCRYPTED_PDF_UNSUPPORTED", "Encrypted or password-protected PDFs are not supported.");
            }

            PdfDictionary catalog = AsDictionary(Resolve(this._trailer.Get("Root")));
            if (catalog == null)
            {
                throw new PdfExtractionException("PDF_STRUCTURE_INVALID", "The document catalog could not be resolved.");
            }

            var pages = new List<PdfPage>();
            CollectPages(catalog.Get("Pages"), null, null, 0, pages, new HashSet<int>());
            if (pages.Count == 0)
            {
                throw new PdfExtractionException("PDF_STRUCTURE_INVALID", "The PDF does not contain any resolvable pages.");
            }
            if (pages.Count > MaximumPageCount)
            {
                throw new PdfExtractionException("TOO_MANY_PAGES", "The PDF contains more than " + MaximumPageCount + " pages.");
            }
            if (this._options.RichMarkdown) BuildSemanticStructure(catalog);

            int startPage = Math.Min(Math.Max(1, this._options.StartPage), pages.Count);
            int endPage = this._options.EndPage.HasValue ? Math.Min(Math.Max(startPage, this._options.EndPage.Value), pages.Count) : pages.Count;
            var combined = new StringBuilder(Math.Min(this._options.MaxOutputCharacters, 128 * 1024));
            var pageChunks = new JArray();
            JObject documentMetadata = this._options.ReturnPageChunks ? CreateDocumentMetadata(catalog, pages.Count) : null;
            int pagesExtracted = 0;
            int chunkCharacters = 0;
            bool hasText = false;
            bool truncated = false;
            int tableCount = 0;
            bool usedTaggedStructure = false;

            for (int pageIndex = startPage - 1; pageIndex < endPage; pageIndex++)
            {
                this._cancellationToken.ThrowIfCancellationRequested();
                var interpreter = new ContentInterpreter(this, pages[pageIndex], this._options.IncludeArtifacts, this._cancellationToken);
                PageContentResult pageResult = interpreter.Extract(this._options.RichMarkdown);
                string pageText = pageResult.Text;
                tableCount += pageResult.TableCount;
                usedTaggedStructure = usedTaggedStructure || pageResult.UsedTaggedStructure;
                if (!string.IsNullOrWhiteSpace(pageText)) hasText = true;

                if (this._options.ReturnPageChunks)
                {
                    string markdown = pageText.Trim();
                    int remaining = Math.Max(0, this._options.MaxOutputCharacters - chunkCharacters);
                    bool pageTruncated = markdown.Length > remaining;
                    if (pageTruncated) markdown = markdown.Substring(0, remaining);
                    chunkCharacters += markdown.Length;
                    if (!pageTruncated && chunkCharacters >= this._options.MaxOutputCharacters && pageIndex + 1 < endPage)
                    {
                        pageTruncated = true;
                    }

                    var pageWarnings = new List<string>(this._warnings);
                    if (string.IsNullOrWhiteSpace(pageText))
                    {
                        pageWarnings.Add("No extractable text layer was found on this page. This connector reads existing text layers; it does not perform OCR on page images.");
                    }
                    if (pageTruncated)
                    {
                        pageWarnings.Add("Output was truncated at maxOutputCharacters.");
                    }

                    JObject metadata = (JObject)documentMetadata.DeepClone();
                    metadata["pageNumber"] = pageIndex + 1;
                    var pageChunk = new JObject
                    {
                        ["metadata"] = metadata,
                        ["page"] = new JObject
                        {
                            ["width"] = pages[pageIndex].Width,
                            ["height"] = pages[pageIndex].Height,
                            ["rotation"] = pages[pageIndex].Rotation
                        },
                        ["contentType"] = "text/markdown",
                        ["text"] = markdown,
                        ["characterCount"] = markdown.Length,
                        ["hasTextLayer"] = !string.IsNullOrWhiteSpace(pageText),
                        ["truncated"] = pageTruncated,
                        ["warnings"] = new JArray(pageWarnings)
                    };
                    if (this._options.RichMarkdown)
                    {
                        pageChunk["tableCount"] = pageResult.TableCount;
                        pageChunk["usedTaggedStructure"] = pageResult.UsedTaggedStructure;
                    }
                    pageChunks.Add(pageChunk);
                    pagesExtracted++;
                    if (pageTruncated)
                    {
                        truncated = true;
                        break;
                    }
                    continue;
                }

                if (pagesExtracted > 0 && this._options.IncludePageBreaks)
                {
                    AppendWithLimit(combined, "\n\n--- Page " + (pageIndex + 1) + " ---\n\n", this._options.MaxOutputCharacters, ref truncated);
                }
                else if (pagesExtracted > 0)
                {
                    AppendWithLimit(combined, "\n\n", this._options.MaxOutputCharacters, ref truncated);
                }
                AppendWithLimit(combined, pageText, this._options.MaxOutputCharacters, ref truncated);
                pagesExtracted++;
                if (truncated) break;
            }

            if (!hasText)
            {
                this._warnings.Add("No extractable text layer was found in the selected pages. This connector reads existing text layers; it does not perform OCR on page images.");
            }
            if (truncated)
            {
                this._warnings.Add("Output was truncated at maxOutputCharacters.");
            }

            return new ExtractionResult
            {
                Text = combined.ToString().Trim(),
                PageCount = pages.Count,
                PagesExtracted = pagesExtracted,
                StartPage = startPage,
                EndPage = startPage + pagesExtracted - 1,
                HasTextLayer = hasText,
                Truncated = truncated,
                TableCount = tableCount,
                UsedTaggedStructure = usedTaggedStructure,
                PageChunks = pageChunks,
                Warnings = this._warnings
            };
        }

        private JObject CreateDocumentMetadata(PdfDictionary catalog, int pageCount)
        {
            PdfDictionary info = AsDictionary(Resolve(this._trailer.Get("Info"))) ??
                AsDictionary(Resolve(catalog.Get("Info")));
            string version = FindPdfVersion(this._data);
            return new JObject
            {
                ["format"] = string.IsNullOrEmpty(version) ? "PDF" : "PDF " + version,
                ["pdfVersion"] = string.IsNullOrEmpty(version) ? JValue.CreateNull() : new JValue(version),
                ["fileName"] = string.IsNullOrEmpty(this._options.FileName) ? JValue.CreateNull() : new JValue(this._options.FileName),
                ["fileSizeBytes"] = this._data.Length,
                ["title"] = MetadataToken(info, "Title"),
                ["author"] = MetadataToken(info, "Author"),
                ["subject"] = MetadataToken(info, "Subject"),
                ["keywords"] = MetadataToken(info, "Keywords"),
                ["creator"] = MetadataToken(info, "Creator"),
                ["producer"] = MetadataToken(info, "Producer"),
                ["creationDate"] = MetadataToken(info, "CreationDate"),
                ["modificationDate"] = MetadataToken(info, "ModDate"),
                ["trapped"] = MetadataToken(info, "Trapped"),
                ["pageCount"] = pageCount
            };
        }

        private JToken MetadataToken(PdfDictionary info, string name)
        {
            if (info == null) return JValue.CreateNull();
            PdfValue value = Resolve(info.Get(name));
            PdfString text = value as PdfString;
            if (text != null) return new JValue(DecodeMetadataBytes(text.Bytes));
            PdfName pdfName = value as PdfName;
            if (pdfName != null) return new JValue(pdfName.Value);
            PdfKeyword keyword = value as PdfKeyword;
            return keyword == null ? JValue.CreateNull() : new JValue(keyword.Value);
        }

        private static string FindPdfVersion(byte[] data)
        {
            int limit = Math.Min(data.Length - 8, 1024);
            for (int i = 0; i <= limit; i++)
            {
                if (!MatchesAscii(data, i, "%PDF-")) continue;
                int start = i + 5;
                int end = start;
                while (end < data.Length && end - start < 16 && !IsPdfWhitespace(data[end])) end++;
                return Encoding.ASCII.GetString(data, start, end - start);
            }
            return null;
        }

        private static string DecodeMetadataBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
            {
                var bigEndian = new StringBuilder((bytes.Length - 2) / 2);
                for (int i = 2; i + 1 < bytes.Length; i += 2)
                {
                    bigEndian.Append((char)((bytes[i] << 8) | bytes[i + 1]));
                }
                return bigEndian.ToString();
            }
            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            var result = new StringBuilder(bytes.Length);
            for (int i = 0; i < bytes.Length; i++) result.Append(DecodePdfDocumentCharacter(bytes[i]));
            return result.ToString();
        }

        private static char DecodePdfDocumentCharacter(byte value)
        {
            switch (value)
            {
                case 0x80: return '\u2022';
                case 0x81: return '\u2020';
                case 0x82: return '\u2021';
                case 0x83: return '\u2026';
                case 0x84: return '\u2014';
                case 0x85: return '\u2013';
                case 0x88: return '\u2039';
                case 0x89: return '\u203a';
                case 0x8a: return '\u2212';
                case 0x8b: return '\u2030';
                case 0x8d: return '\u201c';
                case 0x8e: return '\u201d';
                case 0x8f: return '\u2018';
                case 0x90: return '\u2019';
                case 0x92: return '\u2122';
                case 0x93: return '\ufb01';
                case 0x94: return '\ufb02';
                case 0x96: return '\u0152';
                case 0x9c: return '\u0153';
                default: return (char)value;
            }
        }

        private static void AppendWithLimit(StringBuilder builder, string value, int limit, ref bool truncated)
        {
            if (truncated || string.IsNullOrEmpty(value)) return;
            int remaining = limit - builder.Length;
            if (remaining <= 0)
            {
                truncated = true;
                return;
            }
            if (value.Length <= remaining) builder.Append(value);
            else
            {
                builder.Append(value, 0, remaining);
                truncated = true;
            }
        }

        private static long FindStartXref(byte[] data)
        {
            string marker = "startxref";
            int minimum = Math.Max(0, data.Length - 1024 * 1024);
            for (int i = data.Length - marker.Length; i >= minimum; i--)
            {
                bool match = true;
                for (int j = 0; j < marker.Length; j++)
                {
                    if (data[i + j] != (byte)marker[j]) { match = false; break; }
                }
                if (!match) continue;

                int position = i + marker.Length;
                while (position < data.Length && IsPdfWhitespace(data[position])) position++;
                long offset = 0;
                bool any = false;
                while (position < data.Length && data[position] >= (byte)'0' && data[position] <= (byte)'9')
                {
                    any = true;
                    offset = offset * 10 + data[position] - (byte)'0';
                    position++;
                }
                if (any) return offset;
            }
            return -1;
        }

        private void LoadXrefAt(long offset, bool newest)
        {
            if (offset < 0 || offset >= this._data.Length || this._visitedXrefOffsets.Contains(offset)) return;
            this._visitedXrefOffsets.Add(offset);
            this._cancellationToken.ThrowIfCancellationRequested();

            var parser = new PdfParser(this._data);
            parser.Position = (int)offset;
            parser.SkipWhitespaceAndComments();
            PdfDictionary sectionTrailer;
            if (parser.StartsWith("xref"))
            {
                sectionTrailer = ParseClassicXref(parser);
            }
            else
            {
                sectionTrailer = ParseXrefStream((int)offset);
            }

            if (sectionTrailer == null) throw new InvalidDataException("Invalid xref section at offset " + offset + ".");
            if (newest || this._trailer == null) this._trailer = MergeTrailer(this._trailer, sectionTrailer);

            long xrefStreamOffset = GetLong(sectionTrailer.Get("XRefStm"), -1);
            if (xrefStreamOffset >= 0 && !this._visitedXrefOffsets.Contains(xrefStreamOffset))
            {
                LoadXrefAt(xrefStreamOffset, false);
            }
            long previous = GetLong(sectionTrailer.Get("Prev"), -1);
            if (previous >= 0) LoadXrefAt(previous, false);
        }

        private PdfDictionary ParseClassicXref(PdfParser parser)
        {
            string header = parser.ReadRawToken();
            if (!string.Equals(header, "xref", StringComparison.Ordinal)) return null;

            while (!parser.AtEnd)
            {
                parser.SkipWhitespaceAndComments();
                if (parser.StartsWith("trailer"))
                {
                    parser.ReadRawToken();
                    return parser.ReadValue(true) as PdfDictionary;
                }

                string startToken = parser.ReadRawToken();
                string countToken = parser.ReadRawToken();
                int startObject;
                int count;
                if (!TryParsePositiveInteger(startToken, out startObject) || !TryParsePositiveInteger(countToken, out count))
                {
                    throw new InvalidDataException("Invalid classic xref subsection.");
                }
                if (count > 10000000) throw new InvalidDataException("Unreasonable xref subsection size.");

                for (int i = 0; i < count; i++)
                {
                    string offsetToken = parser.ReadRawToken();
                    string generationToken = parser.ReadRawToken();
                    string state = parser.ReadRawToken();
                    long objectOffset;
                    int generation;
                    if (!long.TryParse(offsetToken, out objectOffset) || !int.TryParse(generationToken, out generation)) continue;
                    int objectNumber = startObject + i;
                    if (state == "n" && !this._xref.ContainsKey(objectNumber))
                    {
                        this._xref[objectNumber] = new XrefEntry { Type = 1, Offset = objectOffset, Generation = generation };
                    }
                }
            }
            return null;
        }

        private PdfDictionary ParseXrefStream(int offset)
        {
            PdfValue value = ParseIndirectAt(offset, false);
            PdfStream stream = value as PdfStream;
            if (stream == null) throw new InvalidDataException("The xref offset does not point to an xref stream.");
            PdfDictionary dictionary = stream.Dictionary;
            byte[] data = DecodeStream(stream);
            PdfArray widths = Resolve(dictionary.Get("W")) as PdfArray;
            if (widths == null || widths.Items.Count < 3) throw new InvalidDataException("The xref stream has no valid W array.");
            int w0 = GetInt(widths.Items[0], 0);
            int w1 = GetInt(widths.Items[1], 0);
            int w2 = GetInt(widths.Items[2], 0);
            PdfArray index = Resolve(dictionary.Get("Index")) as PdfArray;
            var sections = new List<int>();
            if (index != null)
            {
                for (int i = 0; i < index.Items.Count; i++) sections.Add(GetInt(index.Items[i], 0));
            }
            else
            {
                sections.Add(0);
                sections.Add(GetInt(dictionary.Get("Size"), 0));
            }

            int position = 0;
            for (int section = 0; section + 1 < sections.Count; section += 2)
            {
                int startObject = sections[section];
                int count = sections[section + 1];
                for (int i = 0; i < count; i++)
                {
                    long type = ReadBigEndianField(data, ref position, w0);
                    long field1 = ReadBigEndianField(data, ref position, w1);
                    long field2 = ReadBigEndianField(data, ref position, w2);
                    if (w0 == 0) type = 1;
                    int objectNumber = startObject + i;
                    if (this._xref.ContainsKey(objectNumber)) continue;
                    if (type == 1)
                    {
                        this._xref[objectNumber] = new XrefEntry { Type = 1, Offset = field1, Generation = (int)field2 };
                    }
                    else if (type == 2)
                    {
                        this._xref[objectNumber] = new XrefEntry
                        {
                            Type = 2,
                            ObjectStreamNumber = (int)field1,
                            ObjectStreamIndex = (int)field2
                        };
                    }
                }
            }
            return dictionary;
        }

        private static long ReadBigEndianField(byte[] data, ref int position, int width)
        {
            long value = 0;
            for (int i = 0; i < width; i++)
            {
                if (position >= data.Length) throw new InvalidDataException("Unexpected end of xref stream.");
                value = (value << 8) | data[position++];
            }
            return value;
        }

        private static PdfDictionary MergeTrailer(PdfDictionary preferred, PdfDictionary fallback)
        {
            if (preferred == null) return fallback;
            foreach (KeyValuePair<string, PdfValue> item in fallback.Items)
            {
                if (!preferred.Items.ContainsKey(item.Key)) preferred.Items[item.Key] = item.Value;
            }
            return preferred;
        }

        private void ScanIndirectObjects()
        {
            for (int i = 0; i < this._data.Length - 8; i++)
            {
                if (i > 0 && this._data[i - 1] != 10 && this._data[i - 1] != 13) continue;
                int position = i;
                int objectNumber;
                int generation;
                if (!ReadAsciiInteger(this._data, ref position, out objectNumber)) continue;
                while (position < this._data.Length && IsPdfWhitespace(this._data[position])) position++;
                if (!ReadAsciiInteger(this._data, ref position, out generation)) continue;
                while (position < this._data.Length && IsPdfWhitespace(this._data[position])) position++;
                if (!MatchesAscii(this._data, position, "obj")) continue;
                this._xref[objectNumber] = new XrefEntry { Type = 1, Offset = i, Generation = generation };
            }
        }

        private void FindTrailerByScanning()
        {
            for (int i = this._data.Length - 7; i >= 0; i--)
            {
                if (!MatchesAscii(this._data, i, "trailer")) continue;
                var parser = new PdfParser(this._data);
                parser.Position = i + 7;
                PdfDictionary dictionary = parser.ReadValue(true) as PdfDictionary;
                if (dictionary != null)
                {
                    this._trailer = dictionary;
                    return;
                }
            }
        }

        public PdfValue Resolve(PdfValue value)
        {
            PdfReference reference = value as PdfReference;
            return reference == null ? value : LoadObject(reference.ObjectNumber);
        }

        private PdfValue LoadObject(int objectNumber)
        {
            PdfValue cached;
            if (this._objectCache.TryGetValue(objectNumber, out cached)) return cached;
            XrefEntry entry;
            if (!this._xref.TryGetValue(objectNumber, out entry)) return null;

            PdfValue value;
            if (entry.Type == 2)
            {
                value = LoadCompressedObject(objectNumber, entry);
            }
            else
            {
                value = ParseIndirectAt((int)entry.Offset, true);
            }
            if (value != null) this._objectCache[objectNumber] = value;
            return value;
        }

        private PdfValue ParseIndirectAt(int offset, bool resolveLength)
        {
            if (offset < 0 || offset >= this._data.Length) return null;
            var parser = new PdfParser(this._data);
            parser.Position = offset;
            string objectToken = parser.ReadRawToken();
            string generationToken = parser.ReadRawToken();
            string objKeyword = parser.ReadRawToken();
            int ignored;
            if (!int.TryParse(objectToken, out ignored) || !int.TryParse(generationToken, out ignored) || objKeyword != "obj")
            {
                return null;
            }

            PdfValue value = parser.ReadValue(true);
            PdfDictionary dictionary = value as PdfDictionary;
            if (dictionary == null) return value;
            int afterValue = parser.Position;
            string next = parser.ReadRawToken();
            if (!string.Equals(next, "stream", StringComparison.Ordinal))
            {
                parser.Position = afterValue;
                return value;
            }

            int streamStart = parser.Position;
            if (streamStart < this._data.Length && this._data[streamStart] == 13) streamStart++;
            if (streamStart < this._data.Length && this._data[streamStart] == 10) streamStart++;
            int length = -1;
            PdfValue lengthValue = dictionary.Get("Length");
            PdfNumber directLength = lengthValue as PdfNumber;
            if (directLength != null) length = (int)directLength.Value;
            else if (resolveLength && lengthValue is PdfReference)
            {
                length = GetInt(Resolve(lengthValue), -1);
            }

            int streamEnd = -1;
            if (length >= 0 && streamStart + length <= this._data.Length)
            {
                streamEnd = streamStart + length;
            }
            else
            {
                streamEnd = parser.FindBytes("endstream", streamStart);
                if (streamEnd < 0) throw new InvalidDataException("A PDF stream has no endstream marker.");
                while (streamEnd > streamStart && (this._data[streamEnd - 1] == 10 || this._data[streamEnd - 1] == 13)) streamEnd--;
            }

            byte[] streamData = new byte[Math.Max(0, streamEnd - streamStart)];
            Buffer.BlockCopy(this._data, streamStart, streamData, 0, streamData.Length);
            return new PdfStream(dictionary, streamData);
        }

        private PdfValue LoadCompressedObject(int requestedObjectNumber, XrefEntry requestedEntry)
        {
            Dictionary<int, PdfValue> objects;
            if (!this._objectStreamCache.TryGetValue(requestedEntry.ObjectStreamNumber, out objects))
            {
                PdfStream stream = LoadObject(requestedEntry.ObjectStreamNumber) as PdfStream;
                if (stream == null) return null;
                byte[] decoded = DecodeStream(stream);
                int count = GetInt(stream.Dictionary.Get("N"), 0);
                int first = GetInt(stream.Dictionary.Get("First"), 0);
                if (count <= 0 || first < 0 || first > decoded.Length) return null;

                var headerParser = new PdfParser(decoded, 0, first);
                var numbers = new List<int>();
                for (int i = 0; i < count * 2; i++)
                {
                    string token = headerParser.ReadRawToken();
                    int number;
                    if (!int.TryParse(token, out number)) break;
                    numbers.Add(number);
                }
                objects = new Dictionary<int, PdfValue>();
                for (int i = 0; i + 1 < numbers.Count; i += 2)
                {
                    int objectNumber = numbers[i];
                    int objectOffset = first + numbers[i + 1];
                    int nextOffset = i + 3 < numbers.Count ? first + numbers[i + 3] : decoded.Length;
                    if (objectOffset < first || objectOffset >= decoded.Length || nextOffset < objectOffset) continue;
                    var objectParser = new PdfParser(decoded, objectOffset, nextOffset - objectOffset);
                    objects[objectNumber] = objectParser.ReadValue(true);
                }
                this._objectStreamCache[requestedEntry.ObjectStreamNumber] = objects;
            }

            PdfValue value;
            return objects.TryGetValue(requestedObjectNumber, out value) ? value : null;
        }

        public byte[] DecodeStream(PdfStream stream)
        {
            if (stream.DecodedData != null) return stream.DecodedData;
            byte[] data = stream.Data;
            PdfValue filterValue = Resolve(stream.Dictionary.Get("Filter"));
            if (filterValue == null)
            {
                TrackDecodedStream(data);
                stream.DecodedData = data;
                return data;
            }

            var filters = new List<string>();
            PdfName single = filterValue as PdfName;
            PdfArray many = filterValue as PdfArray;
            if (single != null) filters.Add(single.Value);
            else if (many != null)
            {
                foreach (PdfValue item in many.Items)
                {
                    PdfName name = Resolve(item) as PdfName;
                    if (name != null) filters.Add(name.Value);
                }
            }

            PdfValue decodeParmsValue = Resolve(stream.Dictionary.Get("DecodeParms"));
            PdfArray decodeParmsArray = decodeParmsValue as PdfArray;
            for (int i = 0; i < filters.Count; i++)
            {
                string filter = filters[i];
                if (filter == "FlateDecode" || filter == "Fl")
                {
                    data = Inflate(data, MaximumDecodedStreamBytes);
                    PdfDictionary parameters = decodeParmsArray != null && i < decodeParmsArray.Items.Count
                        ? AsDictionary(Resolve(decodeParmsArray.Items[i]))
                        : AsDictionary(decodeParmsValue);
                    data = ApplyPredictor(data, parameters);
                }
                else if (filter == "ASCIIHexDecode" || filter == "AHx") data = DecodeAsciiHex(data);
                else if (filter == "ASCII85Decode" || filter == "A85") data = DecodeAscii85(data);
                else if (filter == "RunLengthDecode" || filter == "RL") data = DecodeRunLength(data, MaximumDecodedStreamBytes);
                else
                {
                    throw new InvalidDataException("Unsupported PDF stream filter: " + filter + ".");
                }
                EnsureDecodedStreamSize(data.Length);
            }

            TrackDecodedStream(data);
            stream.DecodedData = data;
            return data;
        }

        private void TrackDecodedStream(byte[] data)
        {
            EnsureDecodedStreamSize(data == null ? 0 : data.Length);
            long length = data == null ? 0 : data.Length;
            if (this._totalDecodedStreamBytes > MaximumTotalDecodedStreamBytes - length)
            {
                throw new PdfExtractionException(
                    "PDF_RESOURCE_LIMIT",
                    "Decoded PDF streams exceeded the " + MaximumTotalDecodedStreamBytes + " byte processing budget.");
            }
            this._totalDecodedStreamBytes += length;
        }

        private static void EnsureDecodedStreamSize(int length)
        {
            if (length > MaximumDecodedStreamBytes)
            {
                throw new PdfExtractionException(
                    "PDF_RESOURCE_LIMIT",
                    "A decoded PDF stream exceeded the " + MaximumDecodedStreamBytes + " byte processing limit.");
            }
        }

        private static byte[] Inflate(byte[] input, int maximumBytes)
        {
            Exception firstFailure = null;
            try
            {
                return InflateSlice(input, 0, input.Length, maximumBytes);
            }
            catch (PdfExtractionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstFailure = ex;
            }

            // Some .NET Standard hosts expect raw DEFLATE rather than the zlib wrapper used by PDF.
            if (input.Length > 6)
            {
                try
                {
                    return InflateSlice(input, 2, input.Length - 6, maximumBytes);
                }
                catch (PdfExtractionException)
                {
                    throw;
                }
                catch (Exception) { }
            }
            throw new InvalidDataException("A FlateDecode stream could not be decompressed.", firstFailure);
        }

        private static byte[] InflateSlice(byte[] input, int offset, int count, int maximumBytes)
        {
            using (var source = new MemoryStream(input, offset, count, false))
            using (var deflate = new DeflateStream(source, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int read;
                while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length > maximumBytes - read)
                    {
                        throw new PdfExtractionException(
                            "PDF_RESOURCE_LIMIT",
                            "A decoded PDF stream exceeded the " + maximumBytes + " byte processing limit.");
                    }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }

        private static byte[] ApplyPredictor(byte[] data, PdfDictionary parameters)
        {
            if (parameters == null) return data;
            int predictor = GetInt(parameters.Get("Predictor"), 1);
            if (predictor <= 1) return data;
            int colors = Math.Max(1, GetInt(parameters.Get("Colors"), 1));
            int bits = Math.Max(1, GetInt(parameters.Get("BitsPerComponent"), 8));
            int columns = Math.Max(1, GetInt(parameters.Get("Columns"), 1));
            long pixelBits = (long)colors * bits;
            long rowBits = pixelBits * columns;
            if (rowBits <= 0 || rowBits > (long)MaximumDecodedStreamBytes * 8)
            {
                throw new PdfExtractionException("PDF_RESOURCE_LIMIT", "PDF predictor dimensions exceed the stream processing limit.");
            }
            int bytesPerPixel = Math.Max(1, (int)((pixelBits + 7) / 8));
            int rowBytes = (int)((rowBits + 7) / 8);

            if (predictor == 2)
            {
                byte[] result = (byte[])data.Clone();
                for (int row = 0; row < result.Length; row += rowBytes)
                {
                    int end = Math.Min(result.Length, row + rowBytes);
                    for (int i = row + bytesPerPixel; i < end; i++) result[i] = (byte)(result[i] + result[i - bytesPerPixel]);
                }
                return result;
            }
            if (predictor < 10 || predictor > 15) return data;

            int encodedRow = rowBytes + 1;
            if (encodedRow <= 1 || data.Length < encodedRow) return data;
            int rowCount = data.Length / encodedRow;
            byte[] decoded = new byte[rowCount * rowBytes];
            byte[] previous = new byte[rowBytes];
            for (int row = 0; row < rowCount; row++)
            {
                int source = row * encodedRow;
                int target = row * rowBytes;
                int filter = data[source];
                for (int column = 0; column < rowBytes; column++)
                {
                    int raw = data[source + 1 + column];
                    int left = column >= bytesPerPixel ? decoded[target + column - bytesPerPixel] : 0;
                    int up = previous[column];
                    int upLeft = column >= bytesPerPixel ? previous[column - bytesPerPixel] : 0;
                    int value;
                    if (filter == 0) value = raw;
                    else if (filter == 1) value = raw + left;
                    else if (filter == 2) value = raw + up;
                    else if (filter == 3) value = raw + ((left + up) >> 1);
                    else if (filter == 4) value = raw + Paeth(left, up, upLeft);
                    else value = raw;
                    decoded[target + column] = (byte)value;
                }
                Buffer.BlockCopy(decoded, target, previous, 0, rowBytes);
            }
            return decoded;
        }

        private static int Paeth(int left, int up, int upLeft)
        {
            int prediction = left + up - upLeft;
            int leftDistance = Math.Abs(prediction - left);
            int upDistance = Math.Abs(prediction - up);
            int diagonalDistance = Math.Abs(prediction - upLeft);
            if (leftDistance <= upDistance && leftDistance <= diagonalDistance) return left;
            return upDistance <= diagonalDistance ? up : upLeft;
        }

        private static byte[] DecodeAsciiHex(byte[] data)
        {
            var result = new List<byte>();
            int high = -1;
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == (byte)'>') break;
                int value = PdfParserHexValue(data[i]);
                if (value < 0) continue;
                if (high < 0) high = value;
                else
                {
                    result.Add((byte)((high << 4) | value));
                    high = -1;
                }
            }
            if (high >= 0) result.Add((byte)(high << 4));
            return result.ToArray();
        }

        private static byte[] DecodeAscii85(byte[] data)
        {
            var result = new List<byte>();
            uint tuple = 0;
            int count = 0;
            for (int i = 0; i < data.Length; i++)
            {
                byte current = data[i];
                if (IsPdfWhitespace(current)) continue;
                if (current == (byte)'~') break;
                if (current == (byte)'z' && count == 0)
                {
                    result.Add(0); result.Add(0); result.Add(0); result.Add(0);
                    continue;
                }
                if (current < (byte)'!' || current > (byte)'u') continue;
                tuple = tuple * 85 + (uint)(current - (byte)'!');
                count++;
                if (count == 5)
                {
                    result.Add((byte)(tuple >> 24));
                    result.Add((byte)(tuple >> 16));
                    result.Add((byte)(tuple >> 8));
                    result.Add((byte)tuple);
                    tuple = 0;
                    count = 0;
                }
            }
            if (count > 1)
            {
                int original = count;
                while (count < 5) { tuple = tuple * 85 + 84; count++; }
                for (int i = 0; i < original - 1; i++) result.Add((byte)(tuple >> (24 - i * 8)));
            }
            return result.ToArray();
        }

        private static byte[] DecodeRunLength(byte[] data, int maximumBytes)
        {
            var result = new List<byte>();
            int position = 0;
            while (position < data.Length)
            {
                int length = data[position++];
                if (length == 128) break;
                if (length <= 127)
                {
                    int count = Math.Min(length + 1, data.Length - position);
                    if (result.Count > maximumBytes - count)
                    {
                        throw new PdfExtractionException("PDF_RESOURCE_LIMIT", "A RunLengthDecode stream exceeded the processing limit.");
                    }
                    for (int i = 0; i < count; i++) result.Add(data[position++]);
                }
                else if (position < data.Length)
                {
                    byte value = data[position++];
                    int count = 257 - length;
                    if (result.Count > maximumBytes - count)
                    {
                        throw new PdfExtractionException("PDF_RESOURCE_LIMIT", "A RunLengthDecode stream exceeded the processing limit.");
                    }
                    for (int i = 0; i < count; i++) result.Add(value);
                }
            }
            return result.ToArray();
        }

        private void CollectPages(
            PdfValue nodeValue,
            PdfDictionary inheritedResources,
            PdfArray inheritedMediaBox,
            int inheritedRotation,
            List<PdfPage> pages,
            HashSet<int> visited)
        {
            if (nodeValue == null || pages.Count >= MaximumPageCount) return;
            PdfReference reference = nodeValue as PdfReference;
            if (reference != null && !visited.Add(reference.ObjectNumber)) return;
            PdfDictionary node = AsDictionary(Resolve(nodeValue));
            if (node == null) return;

            PdfDictionary resources = AsDictionary(Resolve(node.Get("Resources"))) ?? inheritedResources;
            PdfArray mediaBox = Resolve(node.Get("MediaBox")) as PdfArray ?? inheritedMediaBox;
            int rotation = node.Get("Rotate") != null ? GetInt(Resolve(node.Get("Rotate")), inheritedRotation) : inheritedRotation;
            string type = GetName(node.Get("Type"));
            PdfArray kids = Resolve(node.Get("Kids")) as PdfArray;
            if (type == "Page" || kids == null)
            {
                double width = 612;
                double height = 792;
                if (mediaBox != null && mediaBox.Items.Count >= 4)
                {
                    width = Math.Abs(GetNumber(mediaBox.Items[2], 612) - GetNumber(mediaBox.Items[0], 0));
                    height = Math.Abs(GetNumber(mediaBox.Items[3], 792) - GetNumber(mediaBox.Items[1], 0));
                }
                pages.Add(new PdfPage
                {
                    ObjectNumber = reference == null ? 0 : reference.ObjectNumber,
                    Dictionary = node,
                    Resources = resources ?? new PdfDictionary(),
                    Width = width,
                    Height = height,
                    Rotation = rotation
                });
                return;
            }

            foreach (PdfValue kid in kids.Items)
            {
                CollectPages(kid, resources, mediaBox, rotation, pages, visited);
            }
        }

        public static PdfDictionary AsDictionary(PdfValue value)
        {
            PdfStream stream = value as PdfStream;
            return stream != null ? stream.Dictionary : value as PdfDictionary;
        }

        public static int GetInt(PdfValue value, int defaultValue)
        {
            PdfNumber number = value as PdfNumber;
            return number == null ? defaultValue : (int)number.Value;
        }

        public static long GetLong(PdfValue value, long defaultValue)
        {
            PdfNumber number = value as PdfNumber;
            return number == null ? defaultValue : (long)number.Value;
        }

        public static double GetNumber(PdfValue value, double defaultValue)
        {
            PdfNumber number = value as PdfNumber;
            return number == null ? defaultValue : number.Value;
        }

        public static string GetName(PdfValue value)
        {
            PdfName name = value as PdfName;
            return name == null ? null : name.Value;
        }

        private static bool TryParsePositiveInteger(string value, out int result)
        {
            return int.TryParse(value, out result) && result >= 0;
        }

        private static bool ReadAsciiInteger(byte[] data, ref int position, out int result)
        {
            result = 0;
            int start = position;
            while (position < data.Length && data[position] >= (byte)'0' && data[position] <= (byte)'9')
            {
                result = result * 10 + data[position] - (byte)'0';
                position++;
            }
            return position > start;
        }

        private static bool MatchesAscii(byte[] data, int position, string value)
        {
            if (position < 0 || position + value.Length > data.Length) return false;
            for (int i = 0; i < value.Length; i++) if (data[position + i] != (byte)value[i]) return false;
            return true;
        }

        private static bool IsPdfWhitespace(byte value)
        {
            return value == 0 || value == 9 || value == 10 || value == 12 || value == 13 || value == 32;
        }

        private static int PdfParserHexValue(byte value)
        {
            if (value >= (byte)'0' && value <= (byte)'9') return value - (byte)'0';
            if (value >= (byte)'A' && value <= (byte)'F') return value - (byte)'A' + 10;
            if (value >= (byte)'a' && value <= (byte)'f') return value - (byte)'a' + 10;
            return -1;
        }
    }

    private sealed class ImageCandidate
    {
        public PdfStream Stream;
        public int Width;
        public int Height;
        public string Filter;
        public double Dpi;
        public long Area { get { return (long)this.Width * this.Height; } }
    }

    private sealed class RasterDecodeResult
    {
        public GrayImage Image;
        public string Status;
        public string Warning;
        public ImageCandidate Candidate;

        public static RasterDecodeResult Success(GrayImage image, ImageCandidate candidate)
        {
            return new RasterDecodeResult { Image = image, Status = "decoded", Candidate = candidate };
        }

        public static RasterDecodeResult Failure(string status, string warning, ImageCandidate candidate = null)
        {
            return new RasterDecodeResult { Status = status, Warning = warning, Candidate = candidate };
        }

        public JObject ToJson()
        {
            if (this.Candidate == null) return null;
            return new JObject
            {
                ["filter"] = string.IsNullOrEmpty(this.Candidate.Filter) ? "none" : this.Candidate.Filter,
                ["width"] = this.Candidate.Width,
                ["height"] = this.Candidate.Height,
                ["dpi"] = Math.Round(this.Candidate.Dpi, 2)
            };
        }
    }

    private sealed class GrayImage
    {
        public GrayImage(int width, int height, byte[] pixels)
        {
            this.Width = width;
            this.Height = height;
            this.Pixels = pixels;
        }

        public int Width;
        public int Height;
        public byte[] Pixels;

        public static GrayImage FromBmp(byte[] data)
        {
            if (data == null || data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M') throw new InvalidDataException("The drawing codec did not return a BMP image.");
            int offset = ReadInt32(data, 10);
            int width = ReadInt32(data, 18);
            int signedHeight = ReadInt32(data, 22);
            int height = Math.Abs(signedHeight);
            int bits = ReadUInt16(data, 28);
            int compression = ReadInt32(data, 30);
            if (width <= 0 || height <= 0 || (bits != 24 && bits != 32) || compression != 0) throw new InvalidDataException("The drawing codec returned an unsupported BMP layout.");
            int bytesPerPixel = bits / 8;
            int stride = ((width * bytesPerPixel + 3) / 4) * 4;
            if ((long)offset + (long)stride * height > data.Length) throw new InvalidDataException("The drawing codec returned a truncated BMP image.");
            var pixels = new byte[width * height];
            bool topDown = signedHeight < 0;
            for (int y = 0; y < height; y++)
            {
                int sourceY = topDown ? y : height - 1 - y;
                int row = offset + sourceY * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x * bytesPerPixel;
                    pixels[y * width + x] = (byte)((data[p + 2] * 299 + data[p + 1] * 587 + data[p] * 114) / 1000);
                }
            }
            return new GrayImage(width, height, pixels);
        }

        public static GrayImage FromBitmap(Bitmap bitmap)
        {
            using (var output = new MemoryStream())
            {
                bitmap.Save(output, ImageFormat.Bmp);
                return FromBmp(output.ToArray());
            }
        }

        public GrayImage Rotate(int degrees)
        {
            int rotation = ((degrees % 360) + 360) % 360;
            if (rotation == 0) return this;
            if (rotation == 180)
            {
                var output = new byte[this.Pixels.Length];
                for (int i = 0; i < this.Pixels.Length; i++) output[this.Pixels.Length - 1 - i] = this.Pixels[i];
                return new GrayImage(this.Width, this.Height, output);
            }
            if (rotation == 90 || rotation == 270)
            {
                int newWidth = this.Height;
                int newHeight = this.Width;
                var output = new byte[newWidth * newHeight];
                for (int y = 0; y < this.Height; y++)
                {
                    for (int x = 0; x < this.Width; x++)
                    {
                        int targetX = rotation == 90 ? this.Height - 1 - y : y;
                        int targetY = rotation == 90 ? x : this.Width - 1 - x;
                        output[targetY * newWidth + targetX] = this.Pixels[y * this.Width + x];
                    }
                }
                return new GrayImage(newWidth, newHeight, output);
            }
            return this;
        }

        private static int ReadInt32(byte[] data, int offset)
        {
            return data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24;
        }

        private static int ReadUInt16(byte[] data, int offset)
        {
            return data[offset] | data[offset + 1] << 8;
        }
    }

    private sealed class OcrRecognitionResult
    {
        public string Status;
        public string Text;
        public double Confidence;
        public readonly List<string> Warnings = new List<string>();
    }

    private sealed class OcrGlyph
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int LineHeight;
        public ulong[] Bits;
        public int Foreground;
        public int Holes;
        public char Character;
        public double Score;
    }

    private sealed class OcrTemplate
    {
        public char Character;
        public ulong[] Bits;
        public int Foreground;
        public int Holes;
        public double Aspect;
    }

    private sealed class OcrComponent
    {
        public int MinX;
        public int MinY;
        public int MaxX;
        public int MaxY;
        public int Pixels;

        public int Width { get { return this.MaxX - this.MinX + 1; } }
        public int Height { get { return this.MaxY - this.MinY + 1; } }

        public void Merge(OcrComponent other)
        {
            this.MinX = Math.Min(this.MinX, other.MinX);
            this.MinY = Math.Min(this.MinY, other.MinY);
            this.MaxX = Math.Max(this.MaxX, other.MaxX);
            this.MaxY = Math.Max(this.MaxY, other.MaxY);
            this.Pixels += other.Pixels;
        }
    }

    private static class OcrEngine
    {
        private const int NormalizedWidth = 16;
        private const int NormalizedHeight = 24;
        private const string EmbeddedPrototypeModel =
            "nL19sCTVdeCZVVld+WSlMgt9Jk0qqwhveLXhmVXSfGWLJOu1kPhsqVEjIZBkUQiEsYxRtpHobKl4rywajHDTwrJsowX3Gs1o2Old72DHLoNDvZoKTW/DMiwj" +
            "x+wGigmhqHDYRP9BdGTEKhz1R0Wx5+N+Zr3X3VZfWzSv+8c5ee+559577r3n7rtm/65eFL83dcbtJxzHmXp1P4sz+L+4xJIYhX8Cfwh/XIdTz3FS53j7+0DN" +
            "vDqskjzO8zhLsFSq4H8py+EPqqQOZ8Ag9XR7Eym3CrI4znwoAZZKFfg3/CkqUgUzV1JHSdY8zBP8rzYY4nyWl8yVrOPt/41koYbjBDTJi7wYWwV/mI8T0tCV" +
            "1EnxXfM+/IUCfo1XCv2H5n39XSfaP8DfOFOUFuf8a5wrAn/PP6ywBl1J/TuiQFq/KpqMyVVF3Z+q7zqEvwmn/VlWpmmZQRFtxjWTBVEQB34cxN7crR1JHYF/" +
            "TpiqKmByKECJ1gIqD+IkiYMcqZas+QeR8mZ5maVCjg8lwGJJ87u5J6k32l8hqZNwNhyBjlkaIZlCkS0e+bEfxN25YFjDb8oaTNA6oKR+KmSRVOTJMhJdh8fb" +
            "f6raC60wzuiLBAXWW8UVUbnZXk+3v0W2MQdZwg7LZmE7BFmq5p9uf5vtsD8+B4XWoe3wPwo7nPcXQzS6VYuChi/mQ9RQt/L/rb4LqY1VKxxvjMfFAikl6w3q" +
            "KVIayoqpTrBnUt1kaJym7TL1fWmHgspzsy9TP16hxu0/5Dbu11eXygp1TXQzLGAbcRA84y3IpvC7vot4fzqcDeuqLCsic7ZgskRwJ3EMdrgI5562wzHZ/Gw4" +
            "281MPIpHfulLaySrApuKc2+hqBPtDWWHoCPYYS4tOA3SgG0erN4PToSaYg2nsgax9hptDB4xyzOuD22Hf2nVYGJYobTEpLJrMXWebH9DyMrIs5FetjToA2S/" +
            "hs0/zx6ApGXcqil/F/eUmHsb/Lw0fO/j7ZKY1I8ilIRlxXrpp3EWRak/85hCi8KvYvtZtXgubFvQW4i6g2W5tQcdPfYj8BHUQt2Ui2ivyI/iOPJrb0a28Vz7" +
            "cdIwS6JYWKs5okgLhgIfkOjvelHWhfS8+Wr/4l84fsnv+s/iu9jet/DyiqQ2E9/1ovguMJ0kT4qk2OIX/gl0oEB/198KvwEjEYwdTXlSu3FRJdpvnGj/rrTe" +
            "GLxvmpWRtl7tecn3xq62+U0eifqzYZnpsQF6ZqU8NvXJODjhzQX1HMmahKMshUE+Qk9NtiBajUvUBSroxp4cU15pPyA1REEsDfytX6mRnWT5gY+U0vCopPo1" +
            "jCvwq8or6me5KDAOJUEC49fCnbcaIyz2Zfjr1Rj+h0ZLRfGvMAEPoL/ru+w3rp5l4G2qCiVBicWokJBM8JFBHOrx65X2n2i/ARpW2Zg0BB3h7ybc8AmOlbHy" +
            "Npc4F7e/gL9xp+7MfdF9xn3MnWxfPPD2Q6T2tb9GHgBsyl26v3BPAr99WcBYgNSV7U9bsiZnLVNvLqjPC6p25+6Js2vozvpMjWlcnngwQ4Sv/bH3jDfZtkzD" +
            "+eZ8E6nj7YMWdWx7xmMGqUPth3i+QdQx7+hZqKm32FyQrKfbjxFVQ1l6v/Beoz/dujDD7TXC32RO4fRnH5zFg9gJRAkdjwtI4hp0YRTuM3W/oCbD2QfreJQ4" +
            "WJAJmRMaErUYcnu9r307U8mkP/1gmQwCQ1ZoSpq0Jq2Zy7KuNDSE/hyvx05sSJM6Cg1nIcu6r3UzUvB3oK+ENQwozrkKUbdQT5kIqjwv6qutT1myzpciWYGW" +
            "NTgP6qXWAaQSpz/pg+foz/vzJOXa364A9cvW4zzfEMyiP+5nfedsBaj/0ipJ1kTKguHn3LLeRysptAyc3cyh7edFVvQKZ/sC1PdbnzDqcN5NRbs6ui+2prI4" +
            "MyhcG59nDYGCUSwsvZ5lsWgRzMywCOqv2TZiLasnpSl5gnJmjqZuV7KACiuDkpa+Su2jkQi92tJ97axejfvJQngbnGMviFqeg1puYkHq4+07gZq3lq3XdF1t" +
            "W5bDJcn6cvtLKAuo5XlQUsNftvahx4ZanbdOng9HFvUb7d9GHwX+eu7+/BzfBcVjWVGbegr8V+rWz88prRbUVe3PoSz4yby1aJ06BzUPuTZuo/bCsWHhvgl6" +
            "nr1o6s94JUXtVZ+jLPpLoeFhVRvLc0qT7XVV+xFBLf4Z1G+092MdRk4y7Y9ix6ci/UrX6U4sj422yNQXTSoxvdEkbIxEivpl65NM+TB3QK/WdbxGcVW/xvbq" +
            "83fdI2QBFZaBGk1CQ5Lq1bLmf4NGWB5TZn3pnSahLua4V4vR4TYaK53cKcBH9UtmwMsJgotgZmBPsuY32W+QN0xZM7OIEXlKMxLdXo+zLKJKJUXpZ0iS1C7w" +
            "G18nv7EIYRKIs0cc9XhsRh9FPrCEWRosUVJkJDVBD+ARRZzDo2XAIyT6ptLJBrFFHaLRfObBep5jB5nDJVbSgBs56TquNhfkbZA6SNSiL6nIpDwljailoMbt" +
            "P8fvAirHJcI4GcdjqJtc6gg1h9QoEwxTvK4EKtzoF8MCFhxx0YMxEMd/llM7Fayfk0oySJ2g+Ab2S5RGUYC8l4uZA7YSjG/pCNcSsi6Y+g5RUscCqCgnuxJU" +
            "DfoBZck63r4beyWu6GE9X5ZpSc2DtdEFa29NqL2yNO4F8bK/7DO1w72NAwkOWi9AAAqKpTHVi6KFYJB6g/syzsx3zwAZsSwwBuzL2KdG0Fo9WOKa1Nvtz2hZ" +
            "sCoqy0FJ7QX9FHXEcbV0YFVsUfxd6OnGfbBBbRkxzb2UJeKPzNqoRKwSrQNal6mcKdVi8CPoW4p6mkYiaq2kkzmZY9oh+BC0w5Jk+Y5JfU201kbhayZnO4Qe" +
            "CRaFssZQpaaGzyoNl8O8WBs7aIc4G+mjz5gRNXcWm3Yr/y8ka95He8k3IqTGJlW3gHGWm7YdflvaYbgEm/Jzh0uBFoz+ogZZKN6k3qBYJfcwjH2tSaZgrwar" +
            "AxgPxwaD1Os8x6ZV22y3UzrSNjK0eq4NbC9oLaOVX6dxmS1jtimocpVCxrRDYb0ka2BTAXprpCqQZVvvSGt4aGRSCVEetxdoONTUWHg2kD8McvhjppS/Ud5G" +
            "2KH0NrjyxVpfDpMNoJirZA/jL5tTE2rqAK3oYc7WT4q2aYcZeQG0qRbrmCubQuohso1lv2CqopJRmyUTSbVMS9wFIxHOUhZenHRiy+YjB0fpLn4X9uaUuqn0" +
            "2I+3f5+oRd/PLYqtXlEZ16KQdQetfGE25HUCkMZjgxxVkKK5dSocgpR1R/tenouG3aQbd5uUx6NDRr0yMDR8VGoI9t6RFs91Ieq9XGmvxykGuwiZ8rl3jSUF" +
            "825F6X6JGmIMdg69q5NAKTrYH8UKkWRpyxjqUe+59h+Thnk/HoI0vRoZko/yZoJiD6X78qe4V/Zn2aBMsZJHjvb1XB/oR7G1lJ8/I2x+BjafZqm0DPajwoui" +
            "rCVaaiht/pX2zSLag/sOqdlTIh5XmPMtWa+IXgnjiUlJT0p+lD2Ar3oYftfXBVUP0yqteqzjmEycfDZavKhDJWvg3i3GFPyyUT6olBUmDnspkBVB/18Y33WG" +
            "Z3oYxQICpYl+kqsxlj4S1hqG3zhD0VSiinIsqLFJoW0QpfzGpVvPo6CVMHoJ3jeWY1M368ZBsgjnLlIc1Ve7KVXEX5XADK0P9jFmT7JW+VVQxfmiP/eQOkSy" +
            "YB5VyAit8DPeLAQqE/6n6uDGX573a5epR3n8GhqzL/qmWTgdCi8CDFJJDqtpjzV8Wox6MBuSs6jCCWuvZg3HTAXjYJwUC4rcXgrj1//I88M+zqNwFsVjEGiC" +
            "VOFsOBvBRlAERdHHnQeuwxO0TqFahNEr4flQjr2rDkEa/h7k+XmQkyRXUn+s51FDDAdHOY+TRBVMgX55MmT9kHq9vU9FOEdpWg54XuNPPZCHbRdhwRisjG8y" +
            "9SUV750hJcaTGa4fhL/qQOsn4ZJamKk3eF1J8yicewkqBlkh1H/EfhhlHTNkvUGrbKKuxr2bVFAzb4IU2VMHI77Bj0NNjWmWMkOLKnw9esXQb0KoC/yJ9CRV" +
            "UiyFRR2nUQ9rcAmeXvVhqK1ZnyQJT9LN0W8sRB0+TT2FbcMYGwKyDf6J8AgBSnIlNRHj10ahx5RJX1hULi3RyRLoX1LW8fZzUkPod1Eh5zXYxrMh2BN58O4G" +
            "+d1Qyjreft6gYrA6ooZI1U2qr7/rWdYQfhrLWVRRk4Y1Wi/a/Lg9DpBT3/UGR79J2gJmekxNNEV2mJCkuSfb6zhZFMe+B8YMZca2YYyCYFPeoiXt8C5th5uq" +
            "lRNqZWG76LuXlh2e4FU2SKuHdWbOh2C8Y/sVY0WAXEtS94s5G1APqdkXrjM9ouSIS1a/UDX/TeEPcYzV8xrQkG1DzI+6BVqUbq8nhd/Avix8JtQijIJ9sl5R" +
            "Equ9DtAKEam4MOZDGbQZ2lSmZnFlu0JLZH/4JO22L9DX9I2VQw624ZEXFdbbJjuUGj5OvZLmUVHHnEUF0GKJI2u17GLJkmThMVXJ+YY5j8onffL0JlV1M/iy" +
            "EKk72r8j5xvmPMp3cH6CfkqOKDSqLMWY8jDNKhdIxebsaxJSKydEVSiHqL7U8IiaR8HMRs2ihIaF9PTdMZRCUz8Q81705V01t5n2he8dYz9pIzOGmh8uQ9bw" +
            "2/RdMNPrw1wPCs2isKe4ICthT9/NqSRsU5fCPOpPSMNn+r6eRw1Bv3DGYwr2ZNIuKI4NF2pM+byYN9R5Wvb0LIW9b2zUIdjvQo0pv2POUjSVwCqqb4zlVO9L" +
            "Qb0i52wezIiyXqbnQxh1cdSqth21cVxpSeq32G/g7Mucs+VECVltnjko6gTHo1jDsZpHYaQJ5q+iDlUNLtV3fcuiUjn7Gk65FslHAVMQ5UkN7xd1WBZpLmTR" +
            "fAg0DJ1CyQLqaCg1lPMoqPlCycpRkmZ8LMkzot4d5zLwbFwbvFszedfUn/ojfwCl50e+71NUiGKKk+F0U1I73AfE/FDE8QKMnY/CNIygrIU0r6YY33Q4HUpK" +
            "rhDF/D2cBiMoadALosCH0g2AEvtGmnpbUg5HDpGbERfx4RxFTQxZr4vdW5Y1DVjWgGQxI6j+bHNmfNch47umIX9VGWb0VX6Iay7+LhgHDA2/btUGcqNwIGrD" +
            "DxeyNuhciKYelVQfd3CwjPqDftqPoPh90pD2bGjMUd/1ZXlEBb1nf3LR5MJphC0W+ZH+Lg/mpujD1XfdJ2VlvOPmXDT54CSe4cG3JEqUhhb1tlinKGmFcxVJ" +
            "iwdxGrN1YIyZpWnqHlOWpEAWHtqI4gVrCBSOpvq7vmTb4btmZIepT4epfLHa9UVR31VKWRg17MM3fXDKX0WyxApP+mRlhxxLoYUtWiJIG4E0luWjtC1kvcE7" +
            "4FLDcCp0PDu1w33c1pDaeZbUdBgoS1hDqEexzyappwzbmBCFLTPvV/0Mxk8VhxJ7elLDx5UkxdAhRz50IJjEljVwHzY0lFxNR3rwAMFiS0rOiETUpXCunLxn" +
            "esE0mkXpWrQW+4LyxT6Eon5fU2OiLp1eNbsQqCiKY19oKPeO1Xfd0ZR15WT3dPcoKoGL13x/KW2qq2W9LebzZIc8ex1OL5qCLLQOEIbUUNrvtnboYxvrVl5u" +
            "bt3KD1ntNU2mCdY89q0o8bep+VPyu5QVoqxyO4vymXpT+kP2UMDMfDqYty2D1In23YbNs09cN7xvh7yvqvuAqTOyNny5awOennxp2fSjgkHqOZrp4Y6PsPl4" +
            "GgufgfUed7fol5fBqHdPQ8MJ+ezSGh9WNXzE6F1kv1D3I/CkqfKiqp+onnKm/UcNinplvwQq3pZ6rv2vecdniAVGm2JWjIoUSgzFL1SvVPvfLOsPmv0fbCOl" +
            "Em9rGydkK5c0il/hfAS86G7wvWDxPahHPzb6l+hhRntlIgqNHhFr309law2N3TpH1vwXbH/9EWc3ylqno1LQvYTdi/7VldSXG16eNMxGGR7PY2NcpU60HzY9" +
            "wAbW4+TqaT7KcXQw6kPsEcrvmjBVCWoTuKsn+TSfFRnU4mKFYerrUpbo/6YsHIkWWB9Bk3pIUmNJTYkqc9SQZA1tWZc7+3h108eCQzF2vY7zFsyfO3JdSWs2" +
            "jLQGwZLiDpfDZB33K/G01HwIDZvSIpLWErEZB6h8PJCXcDzqcuc2qvk5jAbwET3d1Reub0S+aMYcB3FM66/LxekjjKLRiUqxtPFxbWXJ6mQUjxKyxu2nhIbA" +
            "oTkIZtFfGzoyjjv2Od4j4lFI/RshCyg8n0fSkIpVFEBQG8VQyjpEsphaDMENDlDWW61F6MsoAMW+cG1DsSX6rhNi1JsP9bf5XIdDMe8d48w3yOPhIuQo1uXg" +
            "5zlGNN3E+d9gMHAG8G8/bdXmeh6mOGY8Cqk7DGpEVAqr7jrUMSwHbB9jBzIOcDmMX3sFNd0cjZjqQal1DEDIesaQ9TafgmNpwwH+og2fuZcZkQqwJ2FNTI3J" +
            "5tEKY2MQWHpLaVGpiEFk7Tzp81oPqSeV9eotAKDwRJ5hG0gtFXWIV9lY605syfL1er5kWYGinqaVlKSUFcJaXUnicT4rVBzgcud4+38SNo86jkWAeEGxH21R" +
            "3YKi2KGm/nfDoqS5oiyT4riSjLNdLuJRzOgNDqDg39c2hB1iPKpYGho+TbEvLUlQHAnblNLaG2YsBTX8srAMPFkkQ4W1Nw+NngyV1DbiUZeL84dkhcMZ7dNi" +
            "mSPFYnnGE7dpVSmpEzKaCtKmJA1NofbqME0Mr5Gb8Si03s/xuhJk6U0RoWEkrdfxjylJbFF/pj2AGui5vfyxjhBhhENG6PC7nrDsUFHYl3WkMk+G0gqReoR9" +
            "L/oMx/SGIhImZQEJdigio0gdbtih4aMyY08RKBkZRd+L816M83Udsyy9I6GOOGD8AKMORykScLnzuPK9oJOpI55bMeJlGI/yswXFoy4X+3rwd8KOY5YF93+W" +
            "lMrTy7zHcbnzcPv3RB2akkBWX0SxqQ51FGshNHxctVdDwz5HKUQPoziRrI3H28d0/+o3vqtQhIxH0V4Ravgt1cqLvvldvoxGrcSjLofZ158afdlsL5prkY4d" +
            "kuMXsepfj1MMtt6sN3vKra9BmXtRYu4At7Go9jrR/qq0+c16mDqpOIYxD+faS2UdKBSPErKeo56CRI/8O0vDUzsNSXE77oayV77SvpXjUaCjlIT03HOMXgmy" +
            "4k7iB9LmT4j4xoxGIsmltLcloz1O3qF4j+4pJ8Q8ir5rsxRMxt9VyHaGsbJAf7105Xd9nb+L6sL6LiXJydvQVt0+1KD6rkp9l5aF49d8qDmMLPrJMVEbV8A8" +
            "6hHaJar7WR7hJI1GoqPumbBIglyd8aflPd13cJEa8z0poCq8BhHxJvFrMBIVSZKLG3UZ3dqDP+ZbdFdAX/4DuhtI68FIHqh6DWThRTEpiagYb/qxLHEeO6yH" +
            "JZ6Zj3gEOxMuh3xDza98On2fZ1Uub1ddAZ7th2rfAY/b8+efwFMFuXkbrsrnibwndYW45Ydxkrogig6L4G6yvrmXj+FXPi/kPSnU8AdSw3wMeuRr+GVADelk" +
            "Ft02YSX0zTuk/lrsOsyZinCzHU9Y8E4kUgXeSqDbXDOlYSVvPNHxKN4kfqa19IKAbmLQbQz8pe/rIfUYU5uzqi4rQR1zlx6db5MlCMJY3K1C6kRbRLGG0wem" +
            "JW4hYtd4Buwwx/UGzfF8WnnEQUa3U5B6Q9w0cfqzq2akIc6xf+6NQ7ykJwpMvmLvhLqvhxb1iDi9VV+QqWMRZ8J/gvaCdqb9Br7BRLfohEUdpzNmfKq6Fhvm" +
            "cevn4EMLujHFt03zKsvmQ7PmsVdOhvWwbmVqRvRWnyyqMm4IWhb1NK1hibq0IklIncH2yhKTqqCVh9qi/pZrEDiY1fM8xTsG1MYGWFNFBQxqMTQ1PN5+iWTN" +
            "iFrwwohO3yw39E2f8cZiEylpG2/QHIBPHwHXQtt9xoPaGG4UqCPeU6GbZkoSU88yxdIuQFnPhD9HDcHm6aYUXpAptCRuL4qmbkw2p9fN1AEiqvlc3RwVN3Zy" +
            "cfcOv4uoTZIlqdbPwW8kcaALz73EbR2s+W8IWTNXnnWJnbdAlvA1NCIDl+gbNGi9Yl25AbOv/1ZKgy8L8f6uvFFEt/xCuU5BDb8n/cZwnsojGEe9M/2NnC2K" +
            "bKoCv2G113PSbwznGR/2SJxfQItBKysKanGDajFk6km2Q9ytTDNpT+hrsiSz76mSHbqSohvWuFuZsRUGTOWFuluIt/bAHxre5nG+Dzusd2dpNJDrAPC+4KW0" +
            "LL7tl6VljDoihf0LvypL84E+BItWL3uK7GVZVg+ZephuFGJ8J+tlvUivb/pFTLUudUyjLItYR6QmBhU3KSEN6x7vjkvq8fZx/q5hleZYxOFD9L4JeV/uY+h/" +
            "sRbld50Q31WlY6BkAIm8b+PGdDWei+96jmZf4LGzeUacHlPoEncu7oPhb+pC1vxz7b9iDa+e490voaEYU3I1Eo2Zkn1Z3tcDz3FgNCoH7EVj51WweqN30YXI" +
            "zJPWe0LEvia4vhmVI3k0CqUZo3mexHEyVzb/HJ1Mc2BEGQ3SQTZg23jVOxMa9/Wor2AwUfr5V/TocMCU9ao3LlTvojBpjLcXlYZPMEUa1qNqkInV3rKfJOad" +
            "vTyYG9/1jKYOADXiDTquxaQQt+hw8tHXffkVcd7GKUDDO+tBOahEi+FOupQVFzHIGiu/8Qp5AJL1TaBAQ0UNTVn4j7GQdSXMo3Bfj0/FdWktIm4GdI3bMMat" +
            "m5Ko43xqcZNWAZk4FSjOA6rT/S08nYZl5rCsp9ufEFSH5tFOJE72iti6vPnFskaKwtUNxtK4TdUg2zVufsk7Pk4tqONkhxQjHAa4PoiNG2P6hg99U21QX5fU" +
            "Jt2KTsS571Dd8VFyNPV0+4CmUMdcnD7sintwdDNoRmdG8dSopA6aVK4oeduBz8K3aosaU2SJI5KqvcR5e1lzPNZI/8XfRXd8NqmV427SVu2k27VUB/cCpeGn" +
            "zPaK6fRgV0lqzYwjuL6iToiIGXIBrmE4Ti9qQetnU8fbnxS1YZyMlK2M9mRYE1shU3cIarGpKHG+X1rhais/TRqSrNI4zx6YVqi/r1TU7baGmZDVVTfAWrKV" +
            "S0PDTdXKQPJZcb65EK7axlxRj6xSvMcgb5ng7Z4G9YY4DyAocfJYnoJFiu4ECYtyFPX7JpU78vTsKtXS1Lj9WbaoTau1dB9x9OzKtMORsF5qL91WXHuinfQB" +
            "bVnznxT2ZNwGMOywVhblW3a4T9mhMG3dU4Ql1mLDOjbs8DbhbZZDoXy8tV+Td/yYulN5G46oibtSofKiLdPfMPUI7acYFpWrm61d406r8DjS9z5CO3QWJU87" +
            "B46+w9XSVoXUbe3rhZ/vpuA1IhGA6dJ9EdJu3eHDzBzBiAT1eT062JSy3JGID7LHQeqO9rVSVg+oNTGe8D0Y1Y9HIqYgZT1Mpwjwuyg7QiROwPuCVB6bdcQP" +
            "Zw3vszTEmAnLI9sVPhSttxSHjNk2VE8hMsDzWGr8knc5pZ+XvpdPcHEro7RupKajmlJcRRxS39B9eYgzfmVVniN0nLp6dKiEhnzkAW+F+6nf60S0UcQ10dL1" +
            "F4kqkjb/aWnzmzgyExXQSXF1i0uPDZJ6jtoLGT/tpJ3MWaP/JJ1LnzhTZRX8sZI6094jNcQ9GKmIq0cVSck+yRp+saFhV8w25P20mSNbSo9EZwSFtTEfRhlO" +
            "6GSfnAhvU8uDVkrWK4piWX7UEb5D95BSbJzmK7LE7IYl8T04bl3hofjgudza5JsmmMMo40wkUGAlE+GKH1YGBcz+cR0wovVUNqf5fAZ+4y/ELSRK+EI31PK4" +
            "wpO6YdbX+Wcw98xcnJHOwB8+TFSV4CSaSxxVXbz5n8EM0ee1SorRr3GiqafkaexcMJjZh2TNgVLZpLI4Xwwldbz9N0JDzh5FGTriRTLHnFd4GpwjPuMi19l+" +
            "kHpZ3JMa9ylRVI5/vsC5ex9mo7BWIQ5jAZQJhimV3QK5XEubk475UOSTwXU9aChlnWj/r+I8m0kt4jmdMwdqg6mCKCnrdZ1XBNYPKZVROovwpG4aYEKWALPQ" +
            "RLE/F1mxmNrQ53tL8UtQGbSGyP8F9jkPa09SclzGc+YjRY2yGd1zlRnAfIweBlrW23Kdwvf1KMcV/s8shhGZpPliJXVCzOa55o+qjGTyFkIWYUYa8ZNUrGEz" +
            "u73+jcjNMh+K/FFgcRivolbOOU6EdkiZlkJpUU/KTFCFssOsDoSsXGeGMtvraWnzQ0mhNKyvOhyDLLliDirM66Tt8P8SsmjfLNe5mPBO71hEfIoxzASH5nf9" +
            "V4PCyFU+phxTlE9KRLLGkpLf9Ub7X4nzopx1CusCs9KgLDy/z/l48JvmKrKE1P9sUaaGRJH14jctDOo4nzDBuOghWMfiZc5yVs5yzGdQkUWJOHY87suVL9rh" +
            "Q/KWBNoGWi/aUzwDKgtiH9ahURAluNdj2MYJiqXwfb06K9nq0zKfJVO39nIMh0Yy8rVQsuTtRZE/SlA1UGhluCSnlVJuU7wPy3VBefE41p1j9glpT2hR+dhu" +
            "r78VPRlrUPo+rEW0MU1tbJjt9STNlmfunD0bZW8Cq6IT4/ATnd8phTZTsp5s/w/KeqmWU4zR5kiF4yLQOZ4sO3ycoj0zj3OzyUxRMeUExJ+Zmat8+ilTzxj5" +
            "o3QeJ/xz/JmdQ2pcMHWHys+G4x3nigKLiHFPNcPUdpnOI+VH7HEyGM2fMDSUBa03y81cUtweM/Vd/4e68STzMlGuqIR/Zmagkn4Uqb9TVCOPW1/+jBj6ObYZ" +
            "a/icGL84C5TICZhQFsVcy+IeJDV8rj3l27Cib/GfoiZoYVo+eX9jdDgoPTbm4KM8a5jeCSOFVRDHMgpLJ2DEeYBMnSOi+7CV8NgA1sks5HqlI4/QFmOjf71C" +
            "cQDsKSRESAKhZBtWNijfDzhug9TD8h5HVlZllWGB/yupPnwzIp34wVz1yu8LD4Dx1Ap/UQIppDBDZILBoRh3R5JgYXzXD3SkfVxX8P/jOocC34WtIX4Z0Siu" +
            "+R8wcwiIak7CoC7yGbVxnCcigxRmn8qVBzgj7tFP8Z5UhmQpJIHXoBxlMWWd0tEox9kN86iHVR4zYbtqN2S+OXtAnvlwsjVoPI60IfV90ZczzsLJFlTCTGE8" +
            "36y/qe/iYlSP62O3c0j4jaofyzaVfbMab8y+qdb5WQe3gCjShtT3xa3sZr7PuFpIWUSZssbC5nkehZFdGeEdmxrSPqyMce4Gf/hv6bYO3r7Pk4JnUtyXNuuH" +
            "5H1wIPrge8Xpnt1gG98T983REkTOz5xivMhdrc74g8VDOyvqGUWZmUJx31BJ05TLFN8noixmmZFpDUf04exqGY/phF0vcJethSOpI4oShivKfEj6JZI6alBy" +
            "1QbjV16nmc7eWWZ5Vcx2y2BDJwjcV925ojb1+KUoKrgXchWv1ztIeb9wtYZfk3lTyTo4JyHuks0P1peKBSjFIh6DFpY1f1zlaJ0L65DZN7GNJ3qVD2P5XLWX" +
            "zsOZc3/nMaTyq8VBkHZA50GYQCsvFPUdMZ8fSy9BVMzUA+qUTnHEoMQI687pbBPvDpN3xj25b9aHxFlGPHGBlKqN/6iyJdCunMy9uYHrOLANzoIwFJSQ9Qbt" +
            "tktp40J4iirfoF3D3azdxGCYOibnUUQlYhU0ZlmCOtKgOHKLmV0pm16JeQKhgG3Vw9lVZoTqlGWH35F2OGSnS3aBXgMsY5IL+w2dcOkt3aUja36DM62B59XZ" +
            "YHGXEqgh2SFHqYJvAbXhmO1Fu2ZXg8XLjIQsS9YG2f2bhqzjtGrjGgT3qnL+JuBr5gdnB+TNaZ/qo1at/JyqwQ2oQZkhOBkjJbwN1GJM1Fz05SfZ97Id5jwj" +
            "YpsCq0IvdaUI2cQ+eABpvc/LsyLgs9mP8rzfp33baqM+OLuUT3+h35CyeF8PM0jGnBVT5fyMS2jlg/L0IfheWLUfoZMpSH2P5huYVUTlPxa7efhlenTAiD+f" +
            "1NvtPExnDzC/KOb8pL4s9+TKDHqmPn+I9/K4PnbDqMcjEQ3zIt8yre14N/QqPTZ0El+cnEENZYaQPKYRUax08SQFfNemyGdAZ5YCceIGqf8gvov31uTqmD39" +
            "fFOdqsITN4n8Lt7Xm3k4NqCD5zEYJ0e4vz4bqrM9id+PVXs9R+tlGr8Kzm+TCD+PkupNQeF5m/4z6rvkmQo6BZNFItcq51gVtUHxum6APnvR8L0w5icZnoKp" +
            "lNVXqg7xdCpQYPNKw69Q/6KsmChnJEpZVcqaQF7H73S7yke9Qh6bNMSDE5QDORZnB6pCywKme9TTGn5bUHVf5KzOcs5zmYuxCDwA6veot2zJXnlG5GaZUvZO" +
            "3jzFX9V4PqZ6h37sB34YqJ7M3/Vd9hs5TvFyc9SDmicvhTWfdHHcUxqeEXcDYaaX1KwYshntu3Ir48WnwPd+LurdcfY4P3KH8qaJM2lh5GodyqA76OKSpQ57" +
            "8Vq0Bj21GKu/RtR1BsWRyXV31B0ETEV4o2CFOm3IImktljbyQFoAFCxb1iKgNmxqfQtqdA7qfZ1rDUro5w68gYc3VOZ91DDOTYapvQ1qhnKQClNNbdrUb3Zu" +
            "MDVkWR5xGFsKt5b1m51PNmRNmQpTk2rIOu3m5g/4vsY7JlDW19YjvNPVi3rQ75p1+NFV6l1I4Q0QooBZpcz2EvsU7wLOn8ZIpVEvilZq/h2d9VVZ4eRdeJMO" +
            "NRzg3SyUZdnGyP1E06K8WXcEBSxqje4miFV0d2RStzSomTfzBOVjlEquvk3qsFEbbE9Q81IWcRHeuFihLJtvifayKCGrNHvKXQ17wmytnKo68znexJmi7f71" +
            "ewbFTB2WIaXujgUF88agMqmXVJYVwWGmOE+kn5cUneWyqd/egqpXqMSStcOVWcLInyfOB6Bc6lw6uWSaTlOYQW9ibrZsFI38UTCS1C7385LiGdNFzkWTi6YX" +
            "QonwtAqew8nSiPLYa1kDLYv3vXYiNUEKRCCVArNmWQdSd9hUIqjd02xGFI0alDVfttge5wn3poY/xBaGv4D/cbD2Od8kSYORWRtPaA3ZDsl28UgfpfGP+PwD" +
            "fJVF/aG7r2m9JC0li4qw7vucd9y0w+fcvateA2SlaFExaNiXqxBT1s7OJZYXZY/ouOzne1DQz5t9Eqm9nStsCjj2vL1uD3wvUGtrUdLwbB/u/MuGx15n7wuj" +
            "CjIkC/xGk7q8IUuODUyhlwdZKxpeY1pvi3vLwOt5vQA1XEt8GImaGu61PDZRYPN/6aVEzcMIqXz1uz62hZ9HL0/jULIWr+VbUSt+XowN4OfBr63RSdMmddq9" +
            "yvwB7m2+C3yoP/HXwWPXfcxPsAYlGdutnNlUSN5a3MEHKsJWbtbhaddsZXFKwV+HMiCqh1TUiQJLlt+52h4dQtYQvfwgwnEoAv38tKnhepPCO6CoYTyIQUOo" +
            "w1UNd3auN3+gb45KKo+gDouVOrzW/i7OLBCsBwOiYG4TYz6PpDHqfWIbapSkQNHcptFiHwV/KG5lOkuYMaNnoZLBGBSCe5cRxTJLdYYrpP5IUuESKfELxzsM" +
            "+yqq1Pn0Puq83a6ULFho6F8xydJUmuVLT1IDd1NThqwsAVmJQWVZoTXc5X5bUaBhLgpLyrNKlLHOR8bU943vUtNXkMQHFcWvBnWX+5iq0aU3N7kQ/g0FUfxy" +
            "3pdfxdSTBrVsUlJaxtlLJPWEHomcuj/NpuW0HOHeSB8Xe1x50HyxlsTUl0xq9xT3YGCgq/tlriu9SR12b9VUyLJIWjQLYbmC23UpDntZsHRNypJ1NTIgLR3F" +
            "QCW0yUeDZWbVxnFa62E9BEPzlPIyDBJzryKgnE5MflTs3i5F1kRF4W2voT6bCv+ku1WSOiHOY8PaBfPH8No8JWko39ytyIFT1DHZVkPSkSla5TaoQrbZR53X" +
            "2/9eUniSUr1iQJkm8SQsx1bpNqG2+R3u/2nYId484D1mzk9JZ2F5X2+4NHrlG3R6VtZHUoi4dSFkyf2NQtcF98ofaSscEkevLdDfwn9jinNVGRrK3VvK+luS" +
            "EQ6yFDyAFyUReV66/6vy1DC1qSyjHuKmQzVC00Nvg6sUmvWuUG+LfQdaZ8OcKS2zEZlQjDf9Yo550OkHm3pIa0jZdKRvwRs7MpKDDyiZFN80URYlXqsKqObh" +
            "33UbG3WPrfwD2VpsC9oO+XyvsEO7vU5JO0SrS7TVoyUEeaD32lKS70nqmKYKiwoDa1+P7NCTvfILhrfJoIXEL3+O/5ZRNBFmo+ADQrMvf11TIfn2iEqA7aV8" +
            "L7SfST3kfs6QBeNOBv/lDGThShR/D7NrkpbZ1MiQFSUsSFA5/O1S6GhRL7gTg6py4djRiw6F34Vfc8OHMvWwTeVbUTrPIlPPul+16pBHFBpTEjWmZDB2WbJO" +
            "ut/aanSIbS8/L2zqsPtF9W/z/igflaPRCB1uXIcwN8+kq49i04sedg8Y1Gw4Kvk8Bo7/tdFeLzba67OaCmHxUxKXljhfi7C/0Z18WEaYsh5yv2zKysTJj7SM" +
            "cVRJUxGjRg2t9jpoUbMKS53VOd4jq2i3JMsWjfY6bXwX+o7ZId6rAw79SMWlymzqWfd3TVlX45hXl6Uc9XjLc6W9TrtfMWSxjiipTJjjRq765lh5jcyn159t" +
            "lvxu1SBpPG7BV9Wz9Wo0G077TO1w98v5NXwHtoyZejYQB+kyJxvUgkHqjfYn5I7PoRLfCxrIY3pYfEFF6+Wo3j0JJWXcoydZUWnKkhfp84asHSoOQBTG9Vee" +
            "yAAKvsqkdrl3Kmo2rHDfQd0AkVcezZpgaqAsCm/fmRRKkVxFsiZK1h7l2aZE4W2T2NYOKfyqUMva4ao1UX/Ka/5BrA5y6uL39AmTa5yRa6zaaOe8Su10wdRm" +
            "MD2feVrWHh0J7E8PgH2MokFsPVCAv497lTodhdRdhiw6IQXWERntLNp6rQpN6nWRHRfqfbM+EDuNRw1US9fpzGgvlddxiLdbt6cw6i5zVV2j87T36806jRu6" +
            "aWusU7O93m7LdeUEpOE9E5v0hc1XTq3ymKGGpf6uTbyts/o4S+6MB6ihab0P6hrEs5WpTKlb6MvSznyA+wJa1tvt3zOp4TzL1aHnRF3MnuO+gCHrbRGfZx1B" +
            "1u5cnKE0qcVum9qhY5Vgh7VKWO8bNsh1Mlev8iGl1pVDoCgtcaQuFEgqIKo2PMDHTErd2PMtKm7IGrgf1+01LAeVI+/EaWlCQ682NNyv+/LmfJSvx42+FZDf" +
            "mI/qTW0bI2PFMUWuyte1dbBlAFNBaxl+412ujIrg7mM1itb91d4F/pD8RqipfYaG1SjekoqRMjTc735EazisywjDDUZrwe/hJ9F6XZoaHnZvNKmrof8P7L7i" +
            "w4IqGtUPmdTt7pVGbZRpNPAbbcWySkvWQ25hyALK+jL63Xq8HsFwPR2aGn7Gqnnwh+v5umXz6xmUmVXzLxjtNRvO03yQD3DkU753PYepS705G5pj5UNGJBDq" +
            "vsK6x2evtMfGf6+gDifWd91mavjNCv7L8bo1Dq3TOHTIrnkdIwL7PZCtR+t272Ivn8Wmx37B3aOpzdk6UtYYO/AHuC84C83veta91BwdRum6bYn0e0sSUifd" +
            "zNQwTQcro0MvXqFeMPcdhvWogm836zDBNGOpPr8pqU8b38VUbtbhIElpvzO0Nfy4+V0HSqgPeXSA6n0Q9/Je1dDwtLHvQFkT0gytQ48svaQXR1W/NqiPWR5A" +
            "rB14bTISBX+PPzPmvUj9lkHFuaLMgj/LTeou5aMwFhAFsNZI1TpDrjYweG7ENz4GFnWvIevFEP6zmREnE6GULHoxNGU9Ya0dclwvZCoKpVYOebK0auNHOg8n" +
            "aJj35VFCUejF1jzOQ60fUi8ZM3Nc36jVCWsnZGUNWS9Z896luabRBanQpOxYZY2rh5TXDyM6R5umvay3ZtYgU3staqYoWkJQXAmMY4W60VoFTLMZroroVzoi" +
            "aVDvQHk29Ul77aCokilcA6xQO9QMdtlaht24i2dRy+7IKHSLp4sxo5amfle1Fr0nwZTmKDszlETHHD5mvDKgZCE3MijMbhPbsgZqr03IyuHvrMriPLyK2uUe" +
            "1rI47sDnmzP9Um6A0SklifvXdw07xFhIMrReAsc4E8U2zJ6yR8VF8bsoWpEbr3qzPMqGs3TMXvmkKQujZkVins+VMa3Q1FDv0OEdFVgY44FxsZNHyQI5mWQi" +
            "T2FI6svKBonC/iEpXCGllEcjsWtjj/JRNcnqpSKmMYpScTobZHXAoszv2qNWUkxlytfQCSvcVgUqCG1ql7YN2cqpZYVoh7lpT9xeX7Haq9sn65DWyLmS8i7G" +
            "BQ3qOqWhYfNMlIJLRebplkl9zdAwiINs9XVjjuSask675s7I0vX5pg/tasoT7lgjTQ/weZPy4kS/i6zfQ47jpgcwZS1kRCo1fHzDyzP1WcsfUsQwtWRhIs8V" +
            "6vctDatCx5NkrAcjTU0NH2hS+bmpk8Zu+9LN+luPKU0/73fO6eeBsuMbH3N2dgozKhJyD5OFa3DNN+sCqY3OleYPYIUAkxv07gMsGUcF4yb1YWuvrTZkZTJy" +
            "GEX+wrOpW6wdVRyJBjgCCQ1xVEZq2aB2dj5t6VfndSYDWTQy4EGroKnhzs5tVm1oSsSUgMpXKL9zR4MqK4MqMz7W1RjNb+ncalGzfJQprkqlhtZ3fVxlaMSs" +
            "e1TfKi+WzpGVrkMZLdTrWh+H8UtG5zDaT1tJtCKlaK88EEj+cWlQb4jIEhPwaxtZmBJVUwM9wg7FvuNASlOyBiStWg4ltcu9f2sNt9WPqbFB0ZM8lBZ+3ldP" +
            "O2Duiga1x/2mouZIDTDnBFLz0KTmDWqiqIUlS0kjamFRTxinxWB1O5qKe6mzUD+LhJ3MjEh/3Dr1AdSd0xFnT6wVVdKcyKZ+aKwQ6+FsMBuMhCzZWhkdkDJn" +
            "ekh93JBFFHEoS1IwLY9t6mKVk9DMLhg7dmZD9VN6fRWp726RNTEWmQNXGaau3EbW1tK0rH9v5NMz8lyKbH4rPyFqX/tn6l72cogPvHDZ/ies4V9tLYsyAOp/" +
            "t2Xd1j5lyNIPeTX/vbBkvS6ixDJf5UD1ykg/1MJZaDzpPZD6HZ1PbzhySp1PL9REzPuZoaRklmbOpqdz3GXUV3R2U3pBTayLkPqyRcmH/JDS7dWkxrTXJvKK" +
            "GBEUcdPdiqrIl82Q+uHW1MpPTOoROpu6xd9YkSQ59my3ac+WynzGuCUKns2nrVH8dxjLbH9YGd5GeDY8wuqD9wUOfi+829zwvZerk4Qga0BFHNuee5l88mUd" +
            "PelyqHvl5WqHzqI6guqYflRTh429G/KIwh9WEfm2qMLYPBTbH75gzkXZHyK1VkFt4P8SY3lDpB5yf8empKw1rA2gWBb5bE09a+wTGRquGVTDX/N36VNVc8pM" +
            "jJ4NbNGHEYwSBohi+FH8rk+a/nAdCvretdEae1/goV7nw+XQ/q4bm7LYT6/h2VmRmwImSVHPlPWsMYMFWULD0RpSKSY1QFmwELH9/AvGPuycPDYUp16D4pWd" +
            "slMhk+o36CR1l0mNkKoFRc+00Yi2aHzXs0ZkCag7mSpJw9IHaTiWp82R6KSaA9B3MefM1mZUG8A5JUzi5paG1zrH6fw85sShkMG6Gutgda9/zwVWMi5T8oUX" +
            "7KUZzu70nKhvzFPAxvCNaynrhNhPQQaDFDCPUZTNZfguqpD1hsixsNykiZqhY1PDRUvLMmZfoCPNEtjmzbkDFVPDHXpFD9R8RMWhApT4HRWTGqidEWBY1ujc" +
            "sgaGhovzpp5wd6/MbTjHs5yncP0tHbOVnzBOcZtzm9KY3axSPzRi5vVwquY2pZqN4k5J7mw0qHzLuY1J4eUFW9bFlKnmfOcb/MADUge3nNtsPbuR1JW/oqw/" +
            "33K+sdXshgtS++gGzep8o9jyZ/zAA2r4xHayVmY3WtZtlJlhe1lNSYXoy8Mt5zapMw/NuY2vn+Mg6jNbzm2QQk7WXKIys7AHuPkccxvfem5eUree99xGU/vo" +
            "1tjqTIbPfa3OOAKiPk55AreY/WzDSOobgrJ3USm652xVkDoj6hA9G80Q5Ni4lsEoK+YooixVHZ4Rt9QbvmYN5gwRzRwiw3O0NHW9oGilui58bw8L+F78p2oN" +
            "k/qc9NjroqA2PSxzymFpaKh81H7zpBPu3kov2pvT7Ab+KbzowtUeYL9aIbJHVFQkqEgwraVBXa52EJbnqA2Tul2/XaXrkWZfxvyLiqnhC+5l5nxD58DCmYOx" +
            "DrD94QvGTRNB4eOhaynNozLVv2x/+KzhseU8KhWyQNpaJqRtNKiP2tSIHyo9O/WCcQdhzrMvmH/VvTqimcNataXHtncQ5pv1ALNPlmKeQrMbujhqUyet+SHM" +
            "iEbG3Ab+t2zYu5R1kylrSJIG8Ld5Pt/JttDwOmfk3qz3zLyMzkOqHd8NzvdJmTUyvfd4HYyV+j7RNCzzLPPXhG/ZCDZ8yoeEoUFzv/I6mPfqvRu89xhFcWTs" +
            "O27gSUz4YVRHU8+k9huyKB+UTWEWj4jyx1oaftGQxdnJJQEFXyWhja2pZ2p42vguegcwAWlrwkdvxEVMQVnOiqupl1wzzjazcqEHGwlnZQBZs75N3bUNhbLw" +
            "TCxTdd9uLyO23J9VeFZOnajYoMwbURczcfu1a9bGZ8zdW5gkZnLPl2sjovzHfu3Z7WVYb3+6W58IEi0dibyzxjmi62BuY549KPHuUE+dFCm6cdcPfN/XOSqY" +
            "2uV+yjjBlYk304UV0r4DnljIRuZJDKRuM0+LiXxqgtqwqKGm9qjawPMbmXnKQVH+iqy7jFYmaQOjJjZEjuYyK21qh/tn1tmexUCcL9sUpUzKvByXdCKor1v5" +
            "D83zG5j7OxU74MDQHdxqgTfWjZMO+F2HzTMwQzx7JBmgMOeHkDTpm9TjFjUeiLzVQr94RT+u+ZsVNd3U2SJ9bGO0QbSNKI7NEwFIfUFZIVADnbW0O2QOsyUh" +
            "VXumhp/SFr9Z8rMnLGvYHSpZkT5FxNTnjJqXmZOpvZDCTOFQgLI0HJl+I4TayOKGbQRgUeNqPjR9lOVt6ByRaRs+WSGdIrL8xl61hsXcB9i3/AblY8zGOEXE" +
            "1B3aGw6zkk/bKM82Ymlkh6Hp2XZbni2K/J62e5GhOYuyWcMf7rF8VIQ7ZnpeJzI7Rw0/f9K93PC9aRT1/J5xFgtzrNHRh+lZZNV9pPTpo2BMGd2yLLdl2ffa" +
            "YASLwNv0RDtvxCtjl6RutGqjEhT7XpHhaYX6cGefpSHKSnpqVCE/n1n+mqn9W1DiVIoYHYAa2tQLRsx8MkwHkenbxvz2DNRiv27UoXnreTIsB9JL+bq9wOZt" +
            "6qR5h66/ciKI2suPsnjm2rLMm3czvFs6MM7o5TQOwaBbN6jrGhpWeMlMyspBQ8rZaGu4075vjnln6NQtMZvsM2wGKb9j3lAjf0OyeFzGPWJOMWh/l2/ttcF6" +
            "qjTOEdHMASn7HNH1jVMfGxvqvkOE9531r40Nm7rZouItqbhBvaPzq8h6X+dzJrWZxxEVvq3PJY83rHv0SP2eTeWUVDOG9XWsSt6kftOStdzIcnGPAzSUv8vy" +
            "5YZN7e18qaGhTLdGN0GorGp4zL7L6dSHRr0BHZGow95aT1wLKvo29Z+sW7RA5XjfnlYp9AJz1Im6hdX/kfq7hqz5mMPtyBHlr3VsSUxdtTXlU1YGkrZKyZ0R" +
            "eozTKMtNzHvaLJLaodaVxjkKKLzHYP9M3ua+3nlbvkTpNM560A5l82da1hF5PdU6fyHzbNs/ldRIUY5jZsFLKmTsn+jaGLh/ImVVZhGSKrtI6i7jHFFT0vay" +
            "zDXRYN04cZDSa4zkQ7l0fU09YaxT+ExZRmY+H4qXKfFlTpj7drtmKx82TqaBHPrFpxtwjU0c/er6pqzDhoZ8Ei3bggqQCjS1w71ny1YWtrFNK3fVOqVpPav2" +
            "pC2qq07pNM7mlE27MG2j6Q9lPfvRoh9H+t+aXvQ+24uqLI0yawSXJmWe0onz7bxo0aDMUzpFrlwnaaiLLWvDOvWBOmp/qD3iRsMfbnTuaVLiXjG+Cyp/36Ru" +
            "6fyOxWg/bfrsJnWwc3tTlrxjPdRXoFc1/OiWno3qUDTY2orvfd6+pa79YVTTfW7hsZOmhv/SomAx2kt7vQiLcf0zsKmnO5faVEVxPJIGo4O/taznO7fYGj5U" +
            "0pu8OBKBhugAYPHw45Xv+oJNbYrUfbG4p05JJ5cN6sfWaI61wUe9xKgnZDXr8O+tuQ3J4gOEsZQUBRuNMeUG5/utz9GZnnk4bZ274N9j6ouCmrnnYjDbGVNP" +
            "tm7jU0eUi/2sDOZqd5n669Z9dAuuDjlnx9kLvboH1EutBx1+pXRGbzmcpXiUlVlQ31PUuYukftnCt6twh4HecThHwb/H1GOCqs/J1Ioa07g8xehFKF5nEa8Y" +
            "TFyj9sQrCAvaA0NKvm0q3oLQr6bwmyEtzuHP1FJQh+g9jhm+vq1eqp6o3P1TRTA1d/B8BFJ3EWVpqKTNGtSixdR9rev4DCy08qR1juLWdKcIqXsVdT62wdRX" +
            "W7cw5Z2PzWPWPbbe29l6z8Pmpy16lZUs6jvCojCCcdZW9vhvMfXHmgrPahkG9V9ahw3qXL1EU981KW/7YlIX0x26yXAy1O8EqdeM5BtD6j2Oubsgi9pHr11M" +
            "h7P+xLRbV/f6mXqhhHamW0hd2b6RqGlfvhEi3gnRr34YL9fw/ghS+0zKMxgl0aBaTD3mfJTz2px3QeoPfiXqWec2lYno/Arb4X4+hemel/W60vc+KKjzK0z9" +
            "1a8k669at3JPOa/+JXvKL1v88nXt1e55FE960e8RhaPKuQtm62TqMFPn5bHnymM/Jf38eYwO0mP/Bu2bz4YT+eqUN1FvkkxNnyheUVl4TH1FUoqbGiPZTLxO" +
            "JN6vEtRVtDdas6ymrSuvK88uzB3281fRzfHa6JWNccGRr3Hp8yI3OI9T9k6MKU6V1xDfLsYsUUi/ufDzJ8RbSDg+0LeZHsesdWTEqcAbnIcpyyW+emv+baMm" +
            "WCK/oNTi04Q3OM9xhuFNljPxttJSv7zEsm50bm/dRBHAWThxz1XAkzopUd9s5YKS/mm7ghy/GXSjc7B1JUVtsdbPxaCnKwV1PcmaeufWcEIvsCB1jPoyjoHn" +
            "+iYsc3ol50bwaQ9QnHfWP9vIIFtvTi9l3ej8W5ofnte4jLNKemkIqTvPezSftaSGH2pThHPoDEUQuivfM3IaMwn09WM69XGjc2P7Bjk6dK3RwfZOor8UdPbg" +
            "RudddOoDxrz+yqhnSeFzQhWdgbkR+vI1PFb25Tt8jtE+hv+knfFMUEnrwzJ/2dlLGwpZMVJ7Wh8XmcjOo1CPQeriVnp+sqhmQRZpmLcuPV8KZSkNbzovDdEX" +
            "wVjQ4p7ybbJi0ZO3L0TN8X1jknUr2SFnPTtXwbd9WNatrTv+eRTJ+qvWf4+UPIrTUa1j92RhI9Lmf9qiXRgdkldvNVqzFkWxzf+Q20tLUXIm+m+L+Q3aYSSo" +
            "q5gKjHbZ2hbBfiuhYfLPaOWJssOkdfn5UdTSknJ+JTvc2dpzfjZPNSSpX5JtiDHvrOWwe9h9tTUWfflWGped4FzFD454C3dB3xXR/RQczc+uYcfzPd9dOAuH" +
            "qXuImvbPaoPIINVi6nF+J2tz0j97ebT/aPhoyPcKkXqKR+XNc1L9U5SrHamHKVs4nmY7KxNiOSUyNSP15+K07bn1OyWyVqOGn6Y7404z3Ukg36KT68yfQHmz" +
            "VZCsM21xZ2RT/M3QCZsWMXEPtx5tLdU5mBvFe23OpiVn1UO532r9pHXKyQX1ijgtBjom6r1AOSIZ62ewpxbbE1I6RytxUsfQsXzaKfcUaLihNNR3l6DFhtoa" +
            "TF84BepNqD1NvVudZlGyAoNja4dyqnXGom4zqaShoaAepbz/krrJecKI3G4UeJsn6kbtp1pPtX7WmruZR5v0cZRtGNlWb3J+ZORY2ChyjMW1IxcYWEO+CCUK" +
            "kcpzm/qhPg8AVIx7Ne2oRVQLGDfyoiCO84aslwyKdIxJR1dp6JGG+UZla6iz/i6LHONx3czNWi+2SEMvCzN8ay5fNqjKpPLcz7u5ewLWxVhOeHmY00siNnXa" +
            "OJ1Ou2aWtEzIyorl2Kas2/cbUscXWUepYYO6xspKWh+iBMSdtP08zo7d1IUa7UbdIjbjh0h9zKayEZ61RwpGfNC0uxW1t5M39udGa6MuUs+3UpDVc9E6Nlao" +
            "a2wqHa2V3dJFCjXskU1tJDbF78NiSmZ8Pz7wAveoe7T1mnMG+vsvWkfdwIWfhfAnMWXbK5l6vc2Vw28JBeFR7zX3TOsM3qZylq3X3KPeUUmpDNQ3qZde8V07" +
            "oAL4W+5rigK5KAt/RTb1DSmLdETqDPXAZYsoT1CZSe1Q+UWDMWjeh0JaIoN3Dkg//GkCfzqW1C73WblTMS7yIin6P+aXN7h4Pw6LfkGPWOuMxjeBt3lEyqI3" +
            "m4M+SsIaQUlLWRuJLWuP+8d6p61IkiQ8Fh4jf06SvGNhEib8tnelZe1S5yoHuPuFaaa7L7rzFpYXW0+5T7m+i9tY5v7XTcY5Itppw2h5ABRzwDyDlOd3g8Ck" +
            "9qg9eqAwpXXA/R/XMc+0jkBBquN1u3rXDKkvKgoTpkGzeE954DNgbbxACiVBm3UDk9qhsgtSJoWw6z0KPv1NsI0C/v+fnFOtR92u1+WcDUYrf2mFerP1T+eg" +
            "uras4FGS9aCg3pSUR7f5K02pHTplh6/RqFOQBZ8huwdbxFautLcxz29s5H4HGgZrwXmrdaJFbRX44JJtL7rTOjlTFPh+fOw+0/o51OEzbuzGHvwkLiq7L/uW" +
            "B4gj5a/Bzz+Ffp7HlMj2836nsUNnyDrBsvC4SGZruGGd0sEvk14UrIp9b5JZ/hqpW+wTC9LPg12Ql/fwsAiMXmObetqqDSUL/XVLjClBljSpnR3ziNC8okTu" +
            "4AdrBzyiGPXW/CJvetGPNv288KK1+zz40B4dntvIbMrOMQ5dbC3tgKzW89BTohbJwqN6cbM2rDGlGkUDGFFSirc8L0cHf3V0MM/2zA/RCWfQsCYNoeZxXA42" +
            "Vr7rFpuKSl9SUINYwKg3iuZ3fcqiaoPKXKayFVkHOzfZe22HZjBall5Jo14qKUvWXhhTPilzW7TMe1uN+/DWjfi94AGkZ8MZarZVGTVvnO+FMeVaIWsMf2N7" +
            "WeaN+L3g5z+l8m8stpYlpWXy9t1e8L2f01k7WtW2jHm3Hal7LGqLsnIjfi/43rvPi5o3qHPIWl+9EY8aXq/OA848e8dA3GLAkyDWDcu9xviF0cCpsT8hb49R" +
            "1p/RwqL2qLvtFP0y3j8XDFAlZaywqb06g5GnI1ZGnBfvJw50ayF1Md1DjM+ryDvge50raf0V/zM4pPbRPCo5j6Jvju91xu1/jV7wPIo8WYTUbb+SrKfbuBdQ" +
            "nEeRkpD6ZYtWo41IodFa6rYV37fCvQCs+Tt5x6exUyt3vEr9tKG4i4fU+yjL5aQhaWQQkom45gX1BYPScXzNVOp5+1xRY9q9Dc6r6Jr/MkVFgvPlRM0PVCbD" +
            "pRNtV0Z0FznV3kavl5et7Kw+am74w3ers6nb+l7lfU3qpnPL4hteBvWCcWdkG2+Dd9St+8tIffbcVOOW+l7nIePW8zb+cOWW+l7nWTPv3LayqsqWdaZ9o8ge" +
            "xV7Q9FHC26xDAd9WWe21X/nD1XMDbMmjdfRT9aamzojxC7xhQ1Kt9q+otQY6r8Be53J1Ypz2Hi39+DZtSYTtRfebt9RDcxeaC6wGeEwZ2tTdZiYdj3biTMbh" +
            "m9C2n7/diDnMvanewVP7a1DzmDPA8thmK9ehsV+o5NXrNfj5qtDUJ5xjjbNYSRXhAcT3fui997733ndf/+6X3/3yBY9dcuyQ+XeQuq5B4SvKRL3/3nffS9SR" +
            "Fer5Rp4loKL0vVgGvdG71989vWB6weELj1Y29XfWDBYpfPguM/W74NhD9pmlTzj/qXE+qhjTo8s7791570WnL3wZyqvRsfEqdXuDyuNxMt553863Lnrrwlex" +
            "ZKvU/9cZNal8nI+L+z5y30fe2v0qla2ou1apAqm3gHprG+oPdtgn0xwnq9KofO/N8c3R9b92/Tuuf8fL/i/iVeqqFarMkLpt5/U7gXkXUEmT+v6Oy1cpkFXG" +
            "6ztJ1trW1O4GVQkKJL0TZG1J/ciwXj7dufO2d1+/ay+WL33sS9f+5KM/uQbLt/aY1GnXWMQ5/EZmUr1//P7xFQ9e8eA9WB44deDUgUcP2NTdNlXuxLO8915x" +
            "3xX33QO/rv3SqbtO3fXonY/eaVNfaVD01n0lqTfv2op6X+egZbvyFW7S7+A9D7wJ5RToaNfGNZ2HLUq99PrgxoP3P3jm4GsPUGlQv9l5aEXWFVg27nnw/oNn" +
            "Dp7ZktrbecSWVfGbsvhLyzq6oqF5GpMyl2Ufyj70L+79b+67Yu+uj73n5IVQomsjmyoba1jOQnpvfm9x3/uhvOfuC//hwjejN9eaGprrL34h40P33vsv7suh" +
            "6nfd955rLzh1wanoVEPWd6wVIsnCx2U/dO9l0NAk6+SFq9QTRuRWWBMIQUMiy7jrFLSvedJZUvcZFNb8FQb1JtnFKvWHJlUSdx9yVyD0pWvRnlakIVU2ZBnU" +
            "PfdsQ/194zZBUMbRe6PL3nvZ++9+993v/ocLTl5wuHesavbKv7feeSQK3Ohll33tirt33b3rHy45ecnhdCvK9hv4HEcvSy8bXDbaNdo1vWR6ySQ9kq1Sq2PK" +
            "ZSDr7svuBmknUdYlq7KqHXsa3gbv9Fy282s7774IrelkdLLxqhlT1zaoJAcq+drOr2lqo0l9YsfeJlXl+WXF1z7ytY/cvfsfdp/MTmZHV2Qd2PHpJjXGhLhj" +
            "GB3uu4pGhy3GlPfuuKxhvSlMcT/03g9FMILtvP7Cl9fW1z6ZrH7XlQ1ZaYRngj+0895fu/cdp8HLf8z/4gr13+24xJaFl7JwLGdJ75iurft7/dXaaMoCHenp" +
            "ceAi9POvbqHhtTvyBsWPlQtZMWnYb1LHd9ywOqbk4LJxNP/gyx98OX51C+rAjvUtKCg7Ryjrwpf9l/0vFk3qT3dc36TGIAs1TF7eRtYnYfy60WhfzHJU9U73" +
            "Xu7B7OQ9a1iSNXzBLi56NvV50yrSLK3S0+nL6cuXPHbp2qVrV64VUPDttcykXnJvsK2px9JOS2kfQFl+XEQmddqiYD6UVkLHxy4ghqhohbrZlsXUBacvePk9" +
            "jyHTJ1lRk7rPosbZOF2kb13y1iWvXnrkyiNX+kO/wBt79ne9w7r/RRqmc6yPS16+9DFVG1GD+s3OZ+xZFOg3h/KzC372nqc+EPWhJLH1VUgd63y4ccJ/1Bv1" +
            "ftp78h1Yeu9c+7W17lo36TSpbIWa9ereTy8E6p1PvjN651qwSj3f2dWwwhEy0U/XQNI71t655m4l6/nOR5q228PMET+NUBZQ4Zq3Su1Q8Xm8c5yMitHG6MHR" +
            "m6NTIxgV7ure1f3t7v3dje4Yirp3g5R0QHgrrCDqn6AwBdxXoTBVaWqgssgGQtYVIOseYAR1D5T7V6lqRdaDKEtSv70VNXK/o+2pLKBslMvyDJYDMDs5GGDZ" +
            "pLvMla6NJ9w/1ZZRbUBZQjlz6LVDR795dPPopiA2zLcXP2nldjZkHUBJKOsoSxpDMWT9UN01Q1lS2plDIO2bIA0ZQZmynjDWeiWtcH86+Fn61CWPXXJk95Hd" +
            "/lX+Tj/xk46xI8V+47PGfAjThP2Mys/TIxceudC/yA+xBMbeEmu4z5yxDTBD2s/A27waEXVh953dEPej2oHtbcw9Dk6a/rMSpF3y1CVHLgFZF/l9P+kmtoY7" +
            "jFZOqJUf1HZ4J7WytMRK7i590nixEd+Sx3p/sPyn8s3yVIlz+Ecf6B6Eskl36seaelPswyZK0j2mxbPN3ycsSlFdlfebZRUg60GUheuFB7CArAel1WvPdrvt" +
            "2dJx+lb6anoE6+JS/woolMekSG1/eMdW1CWvXnLk0iOSAs72bKddc3DKs6x3r/DX5OXfTyNKtBbZY4rfMT12UeW9ce+t3qu9Vy848h7/Pf4H/ISyhTco+zxA" +
            "QRqixyYNhb8mDa3vsm/EF2NzbHjsA2LMi5q+17dW2SALNFz03rrg1fccec+RD6A1baXhh60duo2xMTZcuTaEUuDbpkVmy9ppedGqqtMavfwFT7Z673TQiwZr" +
            "eNfMt6m9VswBxn/0u2s/pbHhKaa6Ppi8TX3YujVWkiSQ1XvyQvLySCHX8Nj2/ldZzXBEueD5C5+/sPdOKL+G+1hrnbxBbdgvvYKOPDb87MKn3vkUjg0hjCmd" +
            "1THl2iYV1RfWIOvJi568KApZw6JBHezkjZGo7tG3tZ58R/TOCGUBlzTq8OlOsaph76cXPHnhk/hdLCuwa34f+Pn9Rl4G3HH1syBVGd5T8RBzFhvveOwDL/pp" +
            "M5sDUfQiNBW/9DkLhPX2xz7nsHH7fuqWQRTRe9UqLzz+Pk6jqAymrkl9zshTUYf8Lrl5L9XHR6mj2njxCjW8y9CwSmLoS+qNAXpnADSEGa2t4Y+Mt+GQyjHb" +
            "TxVXmP8lKSmjS5avUC+5ZjyqDjN6RRJXijJqQfktYvP9FKTutyiQltl3nWG1k1WJTY2Mee8kHOHSoYzpVXL1IgTmnPDjbt0yv8t4mzusM7yJHFMuHPnKKd57" +
            "jkPNIPVD4+7tNCwjfsscW1nk/MfcEUFEJ3RN6tMmhZmFYMHBLcVUANQJz5b1hPFdMxfaC9qYpKhXNbCVKysLBFK65vH2SUzvY+p7zmS7+cLKArHPfnfJRYvC" +
            "F8bNW9UBPY+Ou35me93caC+2Q8WUrKHdXk8YGeTwNhnedLZvweP9Z/tFnn3WybRZiPeIwe6qmK0Qc/2AZeBPZ2e1qHl/nMdoR1T3wIL9jnO8UWZTB5oUv+5q" +
            "UkWTGpkW5dWwlIKFL2W1EXYILY2ZhXKrV46Ms3OYF6eiVxrApqD22HqpXyYLI+/UPpgfGi9DebO+JWukZPlzb9Yyqc8bsmb9EimUNCJLxCfmScO5JesJw9vg" +
            "LaO88NmixOspaFF5gXeU7Pb6gumj+lUei54i/GEZp5gjbGq18uNWVremPWmLmjWoW7ewwkAy29jhC8aJoJkXZeR5R5qC2gTfWzY8tv16eO3FEb7GYbzkQe9x" +
            "2Jlq9jknjTcKp27m+9y/ZL/EESX1o8yfurasGyw7hF5JrymLnoyy0hgoW8Od1g1rHIvo8VVR73jkDOZx8dSzR72d1owIKEyYkhl+Hn6Q5bMG5VvrZfbzSaXe" +
            "ky4D8vNVYn+X37nbonJJiYJv16yORKddc5Yy8Urw2ZxrCkuQipdhglnLpswzMNL7EjHiF1fwbePYyvaD7WW8a+aNIhxThBzRl/HKftZtyrKyToXgMGksF70/" +
            "5XEoavj50+6tje+qaIT11es/MOtNQMMGZd7Zn3rgAImKjTeD4qT5Xafdz1gawgiGaQHLRL4ZngVxnGRe87u+aFNoQOR7lSyg5lZmvJud19scx8aXifDFIFXo" +
            "bd7aNXOSyvydSN2vqMykyjTBu9BbZZ++2XlD5jL1cFqBL/EKSRlSZu7pscqAerPzdlvupyxD8SYMvy+Cv5K5p3fPF46WpU+0oo7V2Hh7Jq8KM7uzqeEu9ZIy" +
            "UblREnwLfJVBao/Odc8vItPLv/S/SBm5YG1qomQtjLeThSx3a1kj0x/2p+l0NB3NuGSjbOaNtsg/fbOVPQZmB7vxrbFZOcItkgLba6vs0zfDfOM6k8pAWjkl" +
            "bhSNwOOqlzIsWeZsGagcKZY2ykZJraixRY3pZDW94cUvjrNnku+Oe1tl/UXqj53G++Zs7XlAr8Ivt6QO0W1TfB/eemGc3xjvbyfraX49ATUcivfF9Gvq3tJd" +
            "ZZA63v5LR778Re/P5/wevXjzy13NSsz9S73A3l/2i6IYF7zfxlTYzCHN1In2XzDlKllC2nK4lTTZK/9c2K6gxrwjmAyJ8lYZ1vBWeS6tj29sD5TfmIcya3Uz" +
            "ZzVS90pqONsciXeJoGSYfXq+Zc5q1PALgsJXtSXTS6NsHi5C8zWOwKK+pKlcUvhK06K/CLfKWL1iUWM1RlbBmGtjOzv8M9laQ4wsCq4KNrjmt6YeaT8iLaqw" +
            "13hgh9va/CPtb2vrzQ0qP5v1nhFedOlV/LJWRu8z4a/c9KFm5l+kNoUdCr+m3+/KtTe0M10jJV4Z8NI8Nf08eHpzdDAzXSN1v+nlzdcnyTosDVva2+gXygwt" +
            "0YvmxstGVh5ppMYGRd53TKVBLSyPfbuxyoZ6TISnz8T44K3WBVIPqUg7aThUtYh1ONxew+uNE0GjmF6UHFEOrwhH5kyd0tuwfK+epcBcv2+8QQmTAbMOTd9r" +
            "veIh3ryUT4aBLE9n1rapT5knnYAq5VNj+Lq8x9o1qRfs81EZvkc/q+oMCqx29SlCeySyXvHoC0ZQRms1qGeN6DfIuhrfyqzLsiL96NUF25okNTKp4ewQUjVQ" +
            "+EaGPlVpUp9yvkPe5myRKMwfi/lmHNfx6Fo2UH/TvrsZi0qbsaha3MwHKmHqX3Henm0iURgBwNUGMS49TC2ou1XcANdeftmMX2HcS1B9reHBs8aiOKpEGnoT" +
            "jwejTzn/T/vQtrEojkRRbh5gnFBceQTq/xU3nlYiUaWORCkq0dRER6LyZiSqQRW6vT7fiESZEQBYAcS5N3f57N4U7wOL7/qcHYui1bIvI1GwQoG1PJ8xbMms" +
            "up+CcflzZ49F+bGQRZwnqbuN1VBkx6KybhwoCrgdkvpHOS5zLCprxqJ8XGF74sHtTGr4j2J04FiUb8WiumSJC4yjIJNo6r+K1xO2jEXRaj5LiMKnxxXVU2ui" +
            "1VhUl1fLMeVaDkzqH9uPGLGoPG/GonKOD6F+hVNJ6tfUfH6baFSOr0PPQnoSfKw1nGwTi2JbJArzwSNVaeqxs8SikKJ4WUPWVLxsuF0sKsgwFjX3yGt4PFFB" +
            "qtwuFlV2aW6Ksagm9R/EuGzHogIZi6LVch6gv+Hn2CX15a1iUSMdi/JJQ/JQimqpu2bbRqNwLerZdvghfRN5i2hUnObog0OSkmnf21N+fsto1Aj9G3kBjyxR" +
            "UaNto1FdirUJ3+FJH4rUQMX0Zh5GejiuJOqP+hfuA9BXxdo2Bmo3UMSiMisWheNDVIZNKlNzgNrzfT8SL9jKghmaA7ufIPUZI8K5Eo2isQgjxKYkpK7X7dWM" +
            "RVGaQaj5cMr9y6LKbWJRMhpFvQv97tjU8MvbR6OUzyYfb1FfOWs0CqRtQc3be5VFkc0PVPRQeNLYz2BcdtT4yu31WbXrwN5XxpVwTUojczD3zFGZqU/o+FAc" +
            "0XVTI66EOVqD2qU5gCeXHEjdquND+Ok0MquXjfEiXDDfgrrXikXhGKtjUZTnmjXEUdnQ8H4rFsUjs1p15BZVbCWrxiTpmYhFlSKChZE5b4ojbN+UdcDsy8rz" +
            "SlkxjZY2td8ZGdFvesU+pkc7oNNgJpbIo0JPcizV8Uak7jZm5nGgEqWaVIJ3/fUdy/3OXUZ+AJLlMwPFw3u+kcgrYN713w9zbGMV4GYhTdOw+C96eGuf7+tl" +
            "8Tg3NXzCXAV4eULvFOf46DhoyHf8kqwYW3dA9zs/MtcpLt3q5xIsvO1u+u93XrLv7OPdUpmkNiBZ+JPGfVOk7m1SzCUm1ZT1hBnV73OMCFYd2SirPRU2h//K" +
            "MrOp3zKp3SJCBMs9QaFbXKEOG/N5ikeVKh4Vz0IxeaBN1WVqUnfaGpYqHpXMQukUU2yvTFPH298RcZugjxngRcQHfkexHrx7j/foE/EnpaT+QkV76GZzLl6h" +
            "TxQVCiqXmaT3w0zv2zJug/feM/V6fUxRAHnPP5avkTP1hpgRIXU0FHrgCxd0B1/d8+efV/K7drjf07cXOdqWUMH4kCdu+of2Tf/9zi73B8oK8dvoVj8Wiiot" +
            "3R97Rdi86b/feZtiX5pK+kkMxhsnCUejjnnHPHFnX93a3+/scZ/SGiIlfh1jDZHBu/7WTf/9zutiBouRpZV4FGffwArEvNXwW019Y7t4FFFxGFO1g84G9YaY" +
            "fVnxKEz7W8zDhRd7cZfuqQfwf7FJVVvFo7KoWDDlIdOk9Dk9bK9uv5t0Yyoh1TzmBeCfRLje1NQhXfMmJeqQ7vgzVWqqa96Hxf9yLIrfBct4FDMX+PTvmc4P" +
            "gJQpS9gpVdrRkOypq2xXZZzYDyv6LxieLabFEzhf+CfUBd6i72Jr2ffokfqy5edj/kXUAqktbt/vd04aMyLh50XB2/fipj9MpprULSYVKiqgMUVSsU3ttd43" +
            "h/GBHxbx0YcKP49jg3U+HanfMinxt8hfgx3yjf3MkoTUhzuWx+Z8ADQ65OEJN8fRAX5i3/Tf79xiv57gCUlYwrkrRqK4SR224jYiHpXKaFTqicdaYrM+kPrK" +
            "1vEoWDrTlwVy9DSph4yzcyoexd0FZXXTQIzVkU3duVU8CqlEaBiIjA6VaVFfPUs8qvSqYKtR7wX3G9vHo7wq5O3RRW5mxdnvPGucdqZ4VCXjUbiqoekU1cWy" +
            "YYcH7HgU6IiSypwoj6ZT0GKmrFuccfsh9jX9jB+6oHj32Jm3YKYWOkNaufLaCmNTdNIEqW/zWrlPc/ks6cW0WCWqL6jKp9NBuTifcotziKI9M29ckMlmGL1H" +
            "bgHrlhnO/XMn65QdHGrhz/NEUn9E1GIY5zjJSNYsimR1qi5WJfzpYsjU8fb3ed3brzDzUV7EuLRYOHN31p8MnU1n7HPkJysKeRoDqX8nYgDjITBELTHDijcb" +
            "TjaRimnXoxhvFAtoQabeaD8k1jb1MMNvi2KWBevn6RC/y698nEjli1CeJEDqiIo4QI3kgmrNYRY8Q2qDNMzyQp/vuQU8tjp9BP/tkl4NxDJrwQw5pHgcemtw" +
            "i+h9NCVtfgL/bTAlemG3RspjqkMePIgXoabebhsvXh0aYUeWsni1EOPj2bjyyNVu8S3GKx744tVoxDqyLBELwff8GtSYRr1Zvx7Cal7t7yiLwliNvIJb4Z7Y" +
            "XNgh1uFsON9cDPF1nNhRdhiSHY4l1a5wh2URskUdFPUOK5RBTEtctqgZtBjIyqS0doZ7YnNhhw+ThovhOI8HvMul7JA11NRwOVwIDf+Gv2sTdRynOemHrYx9" +
            "lC0RyWC83EQN555th3gjfxwVlHdg4SzcGi1qyIkIoB5o12gh7PDp9rMc+UImL3oF5dBB60WLmpAlOnk3Z27uSup5koWSNooiXaFAUnvchT+iGvRkTyGLGk43" +
            "Zwd0dt8ZtLIT0jpNbdElMJOYt5h6nSlnMsS3aHmHV9gGrsgD+UAszj4WyjZOtG+XVrhZpzLHAXKYSd/kjiKn+tcXtazd+u3pGa5aQ6WhfwyZlpR1nFuZaj5P" +
            "Zbq6BG0D611ZIe/zSQ/wuohjY0Rv0U8Ek1B7QQ1mgsqkDTL1JNuhR3HKQaBlufNwashql2iFtZD1n8VJDGxn8DX02hXZoUe2kZM0qCKKxSq/8TjFzNHL+ynI" +
            "GrCkMWoYko+qUA6VtJslMdvh4+Tna6gLOkUpKLAOb44+akNTGE+U/evh9tfJ5ud9vxf0OISM3hc9G/XKir+KI5dL8jhIPU4azofizCaFaDeAwhnBZEj9REQu" +
            "UZbU8PtCw0RpCD7bhbnysAbrREpqGED9s4Zn2kfFOg9GMJhPJlHRG5PNz2l06Iy7PFzm0uKReoV29tE6cASjeJkjqZr7V9Wu+LXSIpHt9Ur7u0KWoLIk3TCp" +
            "DYfuEQCVw6jiSZsXPWVzemC0TjkO0OJbM3eK/YtGZRwrOynKknYod28nm/hiGMxoyPNC/5IU+Gvw2LRi1H7+Oeop0E9GJWbnod5VoyxvwmM5/KCdtrM2flks" +
            "/cYrYt8BZB2Q+qGGNciakCwYl0kefLHS8ET7MaVhORJ5c2BUrnmstGoDPJuQpd9tnw7RB+BsDSVRtps+ezY6+RVLb8i1cUB4gBqvEqbZADMk1C7GGicoK+9A" +
            "wdpIjNo40/4DIakGSXinhSivDoXvJQapIpQ+6tMwwsqzjpif5NE7v3Xndi9J1PQyCVO7VIwIKbzvffKu7V+RkNRAnT8ESQfOJgtIT1OfV9Rrh/B21E/2bJcd" +
            "v1YajszYF2p4zalrtnnRxNDwCeOc3nL42gGUtiLH1doxddjVecwWoj62zMNt1cZhM6Z3Fsquw13qpNNic7F5GJnWaralkrLraWpknPxcbB655NTFP7nYzvgh" +
            "x8KF8V13GScJgbr08MWnLtZZdfTr1HiOwKSuM6hnLj0pqZamOKO+Sb3e3qdq4tyZ2rX15kYNnustFEm9rWLm5yNNU18wqHO/PiG/6xuKWW6e6/UE/V33GLLO" +
            "JU1r+KtQx9ufYGIobaiZNalU2ZkyJ1LUSMoZNnPO6PxMksoFdaJ9g9SvIc3MBCWlxYq62aSMXDVSVmpomKuecsN5t7LZU661euXZ3yWR1HU6O9M57dCkbjhP" +
            "SaYs85YEeo1Hr3n0mq2/amZ5m9F5eRv0N5q63XiLdrn5rT3f2rPt6OBp6iE3M6hH9zy6PWXIOu2at++3l2b73tPuXovi7Chn+yqkrJxOUBdbM03qtLvPlnUX" +
            "lnNRekd1sXkUWkuPXo28TlD0uLzfuINA7bXisaXtLw3buNxdV7J+cjEW890eLSe1fO/lyg4XWBeKsvUjWUZP2W/sceBYRBZ1sf06Fmd3MinTDoEanvp1KMa7" +
            "LionVMukbjfuLi02T9518td/8uuk5UpOKJsyZf0cNQTOYJQ0TX3G+ZH7EeN2ZZZGvdWCh1zM2wSfsW5yIbVF6eE2q3nj4TPOS+5HrLuc8Ray8LRTbVGnG1QU" +
            "rVIZyJo1KDP79HKcZVmvWcqoXJs2qBssqlqlojKqV2Rdb1F5tEplUdWg/n/O3gdYjuo89OyZHk1fmdH0BTlmwK2eKyvrkJgXRshAy2r1iEAAO8LGxH8kTPDI" +
            "FwQGAS1kWy2rNTNJFPznXRScZzvah6Jnb56fvd7gpexUSqnIZJalFK+jUrK1eQVvV0t14ioX+8ql6pdKOR3XeN5+f87pPqd77pUq96RMuOin7/Q53/nOn++c" +
            "71uvvdiFGnbmldBRX1dhy+9SW7Dj4EPWBc772W41G1TqLTMzdOpunepIipwUyDWbZrtE/ZX2EhlaftERiWtb+BCWpTXb5rS2estjnmJRS5REBPzUy5TMYFvN" +
            "Epq/vKGCtwJkO34YZr1Hi0g68wo5ED0ts/yHYeVwfx4tYdWyRLf8uoWsIq5+NaNum98uDVp0t0KlbjaP5TEF5hUn8uUNJKWGN5sn8lgz8wtQsbiBlFO3X04W" +
            "3dfT3/l9WHm5MJ/Cl3d+VKYGykjpUMZbTkRJd2gwIXKnCTrSak6Vd5lIfVSjRPJKTl3pYYjtJkVin4qzKKaWFY1axKiPnBC5l2fW7aA2kiyNKuZKbwDDVlIi" +
            "Iy9IA41stXXqtfrRtfQpvzeGN+hk22N/Pb62HoZ841Fvwx+Lm4RraCHe2SnpYTPff83XQ9BEeuun6+GbZl97fc93S7UChtVbSDQ7f732BhwsQK9ceou90ru2" +
            "DxstLfIJ9O1iuaCdV99XM/VrWgQDZ7Fa9PfV/F17tRr6vUrpRBXbe70WpXk2ZwaLOmnlu0p522N/sVy8xUh7r8vfdd9lKJ+o8nfdqlIRtZtIktvEYqAlbdWz" +
            "Ug1/TZNVZJ+mJ/5YGu1mWxldXMNb5vWXJBbI0jdPlWaHlqYbQV5DnZqWqOu1CAYzag3q23wecxq+9lqPKX1ehl5FjmYWZwFnMKdZpfTZHNcbeIbYXhCF5qM2" +
            "1DAtfdcHShpF9QPSpaAMSLpIKdI+YjynvJQESTh1U673cCFs4ECe1Cd1fUeE1F8olo3nZW/Bo8GIDrZBPRGZ0HTqG+aO8ioKOKZAFkhiRqfKszlGK5Ac1jCp" +
            "z5Olrhyw3XEtA18FBgZWQY2kMWHKLFMfUikPL8NGHWSwJM0kz/Gm1/DuUi+jNOBAXoLSrHmySnuioe95NOJJWjNZ5bueM31ljSKyoDdaTafeqWF+mJPG62Kv" +
            "rX/XHVoLUp6BFh7WeibHvOd1uU79lemV170dykGN06Tp1V4CLpxD9Uv95eQU53mZJ+vmfEeP8we/H8C3AOKpNfxyV10/E2GqWBHJSBbsvSataoN2WFXq9ry/" +
            "WFKL03l3BEMUcqomInVfQaGsHmtiT1yJzylTpQZKhEa+jerQlTu+iJi2MQ47F7WGavwNfheNXmZfPkXADG2U71G12B+BXdtDJVn4+Da/RNtO2iTH0lvjuHKb" +
            "BddMRPny0W7YTm1ZP5V6rf6xYt1AVyHatCppYR4KzESBuSjE2VJHoZ7UKcxEgxe/7FPWKfOieRGzSgAXa9R/r3+g0A26jdqgK1SYI6xlrpgsKyZpKiXXbOzT" +
            "ocgZTAF3KqdiqmPR8sVMxHe2Wx7GGMZdYUgXupOm3sdM3aNSpImOx9fUc8oqW4DPKVaU9Rff6HfEcxq+PJ6ULMdHjG+qssROARlPXPQtdF6Vdb2WWT6IOrjT" +
            "NXoLA7BPkwZZeZSizUQfMXY3etpsDhZ7oQeF5wa0UOW5AambGr+kzsuwNwdZCyhr0JjkM4oWboeom/QaIkU2W1A8HkuyrtcihQ4jfOoXLoYL6QJa3sk6aj2z" +
            "+l2BTqG9XiB73UiaPLLmfdfOsqxFtPJCljVZhbp9HtXhGWU16s+UdRStKWnFwJa3V0OLjWc3S1D0OUVvDbKHC16j1+g1w3pIZyl8uqRT/0ZfR0l7XffqPbLy" +
            "KRBVWWrMzwDvpeRWvqAGFeoOvYaYjqzlw0wU1SNRQ5xTyjV8X+m7mPLRmWfKMyLkdErt5cCnNiRZ6jwUlmqoZ+QBWYt+h7Z5GODPDIUsvQ0/avz2Oj1qItjS" +
            "gyubV7Z8fuPJa/Zcc99bcHAvlaKSIlUKLGucPriyBahtryPlzqf+Yl2vLOvIymaMs3bPNXvq97XvQ6PQqFLl+NinkdoI1MY9169G/a/rdla+y9l8atvJjSc3" +
            "vv628G38cK9K3Vmltp2CcvLa16+NVqEuzmnD1ubPmyDr2j0oq8IwtaPa8ltOYf1AUuTOp367FLEWW/741a9e/ep1K+tvXHfjOqfmwNxyBf115PjWV68+D9Sf" +
            "rEr9j9X+Onh88/Grd1+9e/2NIM1Z1zEXzCvpr+NXn7/6/Po/WX9jzQGqNYf664Yez/z4vuP7Vracf8c9N99z6563Rm+lo4uFMvUvpaj1z24+t/nc1ju3Hdh4" +
            "4Job3BtcvCG5WOmv/7MUaQ2jiZ/bd+cWDIP+2NtueOsNb8U5qUy9tRTnFqlXt5zfxpRcu5Wpd677SIk6t3zh0Plt57cduDUOOAyM51SpB0sUxsRE6ie3MYVc" +
            "mfpoJX4vSCNq97YDgvFaVer9FUrUcNsBruEcWWG55fdB22/df/X+q3/xunevf+t6ZwOuVxpmmbprHgXcrdfdet3bNsynfl+zbKK/Nt8F1Duvu3X929a769x1" +
            "Veqlxj3ltgBZd129/zqgNrxtg7sOMxC2zbJG6fFgf2czSrpz652gUTdef8P1Hj3Zq+rhr2u/ObeP9RDa/ZoD19+ALdiqtvx/Levh7c/uO/6O8+9Ajbrn1gi1" +
            "cI4e/tfGneXWWD6/fP5mQTnzqWjdL5d6ubG5tXkFbO+ejXveImxoxYp+e92NFWpFpdq9xlKF+sq6d5YomAxAGlp6ohrzqbK1AWO7GS39SbDz4Sqyqra3teRs" +
            "dsDOn7k2pSd4S3Ntb9kewr5DUrR7mEd9ZQ7V2tLacnLLmWvPvC3kWW9ODd9dlbWlg7LeFq5aw39o9MqSoN1Xrr5n/WPrb1h/w7qOBbZ3DnXLXGo3UI+tpzyq" +
            "5kKtSr1Lb/elBlE/WH/P+htqyMyXdetcWefX/2C9t241WW8ttSFTMOddtecq7yrKRVtfrJepu9a9pyTf2Xrq6lPXvb7+dTvCdwH4oqAi65ersq7m73r9Kk9Q" +
            "C/Uqpce6D8KVrUi9ftXrV52xhazS/LUHdlLFWUoc0JkNnkLV6FzI3GXitT58oBEMdap4JTEM0GeFzhc+PZmYuyx8/4Xh9IYadVyJzxb7kSf2GrzTgFX5ruYS" +
            "jGfkCml7tAiNIAtmAnHiRbeOBlavuQiygkCv4ZumGld/6ovTIXFikFgDCyjXqVAP6pTHXjza4cH/DuweUyOd2lPyBhLV5JtUu5By6as0ar0WsRYoOvcSFNRw" +
            "iWWVqF/Q9l/ZkUEnWZhwGwK1RBlsW023q/fyHdr+Kz2SdCYLkw0TessK7W7i1c2F5ssl6l2lHLtJb9KZtDD/NPbywFyiTI9uhbq9RCUqZS1hJuUKtS4/n28N" +
            "nB7m8AkdPD+hvT+2PT9ZwCTQocxcgZQ8S8F34qCH4oyHW2NipW0OItHBaK85VeSTIq8fyqITL0GZ9EiYtnKwwVeoPYWsnEokZdFLRJblFdRzyvsUPFWK6GyI" +
            "NSqxUgsfxvq+47cjfXwdUM6IMPNP2E7bfAYlKNcP/BL1DeXGnYNPOfD0ihmgQivCnM2OU6GUEzNB0RmUybKYckvUwCxWDr0BvvyEXm5CsbCXExyXFj5caDWK" +
            "yN/4XcU6KgQq8SatiT0WejihV6Dk2mvpdqPQwwGGw+lMHNCo5ljYjQHpbwddey31uwo97FHsskF70hT5w5AxO5bTbDfVKOh7jJtz301r0OlxkIVJM7/FgsFW" +
            "8ahJ0UJujT1KL/NzbnGKh60B/wzRHuI7mbCg3pnbKNRePA+VmjGuMUOxXjC2rCLr7tw36mCIQAxj0pbnGjBO6GALtdDpqbL0CI1xwGdKSZ3P/wegvYsY61rL" +
            "a7LHuF7ztU19tNhJU1qbQRvsNeaEiPWx3NLObWA3j7LIYgsLQBa7LEs/B4h9j7wUZapVqeFdWg3DBfQ38CkU2F57qb3odPxgqNdQj0w+DWhuYFk4kq2ejbOD" +
            "anm5hmqmoZkfAZUSJS02Ui3f1Sz2TZoPEeaUhVSTtUSzQ1mWfpslPQKWd2G8jq0ozst4MLuIeSH6OrWrSlljEUtol0WzJWZTL1FqrpbUGyzAqGzwqSHODvNm" +
            "FWyNqqzJOikL7WGPdEqXpcdOh9lhAcq6CcVIwnmZ55TT3XJ/3VWav4Cik/UxtTzM5laVuknPbkzUROoGfhdStluh1D1RdgwobA0hC+evXoXaCxbgdvUuC/pg" +
            "YMUxaPB8rt7UKzzMe7XbEUTheG4N6ExUpdS71XuNZcXjM4SBQjevUJZcS+VUcetjr2YP8baN12E/YKIxaPHV7/oL9cYdeug6pPWtwqNXvd23V8tAQZQnvXqF" +
            "f6hK/ZXuoYsxpwGd9+anvfMp7f5hjFe4Lk89p5xVYmt08NSWz2zzu9j6zW+m7i71Mj2KNuU5b07VVOq44uNgfyqY9EavHpqJdvs7UqQhdZciy1+VUm+M74UV" +
            "0R2Kr0369OS8Mpl76xap3cq9FGYwjJH0Q82nlnI9bNKU06FAzbR/MFeXtZR/F99ZIU8KhT6Rvpd51ED1tUUOe+dsjCso61i+w83UvjIVRF18O7AWpUYIcTm6" +
            "ct4aa1EPzKXWlvVtznuLMVUokhi+Zsc9YXFaXr1njtQj5KnEeBGuC5QNnHkGM8uves98r3FWvAAV0Wwohj5KO2OqN8b1e+Z78zhmVEcfo9kgBbLMLPcsFzWU" +
            "1M25tUGvHq6We86AfXKr3jPfq6y+0KtHmTxxdWOtflMfqXea2xVZHbahtCZa/Z75XlhH3aHoIekuhghiWavcM98L6w3dQ4dePQwTNDHYS1m28ZLS/EQx33pN" +
            "aGU572Y6U62Gevfb57uyCwOcjRRZIE2zbDdpVBDxPQz0s8mbJTQ3VGqo37eJeuFiCjZUevTm1/B6bR1FFPvZNqxmefm7dKqw8mvNDjc17iztlyPcZeOMYq3e" +
            "hvdr91LwBjCofiNqhrWwpr/yUa3oce2+TYcYsPT1nll49Hi8qNQxZYYlWXhnBh/Am4M8I7C40a5Rd1aosEQl4vVSQf2ZsnLAm8T+AtYwNENTvQmflmai0h2Y" +
            "OBInN2ETfXpFNuCpqVKvKh5wun+M93pgrkzMRMs/XqZ+o0SRrHpq6vmRVeoB4zURdw7fL7c6DXwOaxu0gpWzHtnDgdOb5VmRH8hf7OLv2n673UAK9pXiBj1Z" +
            "Kczn7YSzPPv1A8b/K14igySyh/Rg1wJppnyHQM98dznEFdTD4u483UZ1GkThKnsi7upjjmh/oFM3m4fkDgC+rOFC6Y7F3RC+qR9B/fzBTMnOjVSkUC2mbKRw" +
            "d85UVKGW8vhRJCtgiqUxlRlIqdwDyq1gfIHQClpuqwttCNKkLMxhHWv5wx/QVrDpiK6iO6IVqQ2xv9Cb3urMugWl3t9IR3h9i0I/YysSBS2IG0uFQUpdRyUj" +
            "MFDeIgZzbhp8tkT95S12FnVZ31DWUWkfDICzkPey6GGQ1G7NbFXWt+ntLbUPT2tCNwwhKREzLEatlq2I1O8JajqCaRf/o80Uvw9MicKoAfwWjakXKAZyLsvP" +
            "20LoIcesdagSBSUjXWMfqzWk79IotZdfk1Gn+jR+8Fk79LHoZZrPy/VDqoirj29apvhCt4sFdQo5ylWOr6dH+vh6Ln+pM8P2CIQ0loXaC0xZ1n+vy4iaoo6+" +
            "IfSQtL7Gkqo1/HCuT+mIlkrc+ia1In0X/rKF8brtYnzlmeVHyShvZGhFcU4kcwx0C+14QDk/FLK49ZtjIUu2PMmyivH1wVxWXkNN51mfprYq6+Y80hq2vNET" +
            "VFNq1ED8RbPSqFT2X9iG+IeUUYk21KlQ78zX2IJxhPWlkZIWX1Wi5Bms0GCh9WwPWeOrspbysy/MMYS3KnOdN+WbWPhYsKJur7DzS+aTqj2EFTPb+bF8K0W2" +
            "FyyvZtlkZHKwhV7Db+RfRbJyK+/vcna1BwV1Td6GOD80nEY+EynjC638Ulup4f1K3B7sMbS9pL+F9u7KdkW79NY4rsQjEvNDl0alKSlh5/M5D6m96gvrfssn" +
            "WZqdj0CSPwginTqgUH5ldgBZu2KQFQzVGt6vW2zYI5bHF7TGkrek2977lV0bjhW8iC20w2TtiIwIKMx3UFC35KsUmlE6C05u2UR/eUsOlPaiSu1V1jY0p7D9" +
            "bUo7Cv0FkvyeXkMtTuAo68Ps4GJ/CS9Mje2Gv6jX8Lga02mU4XcBxTYKWxHacCnqlSk1wlU6gpkoWHDVdUqK1JK/6Do6tay2oU+UolEpy+q5iqx9xjNKDC6o" +
            "D8YSg2kYB2KKMTIcFwr8XzfbmcYqVeSjT62MI5C5ksIYfvjjdKfHMoXar8SQTM247bgdIWnadWRcOKflntqeRip1SJE1tbFCFcptB9OdmUK9qNQwwRq6FHjK" +
            "pdgfImag358eS5UzaaR+u5BlZ3YMf5apaTd2g5zKNOr7Sl6zcW1qx7bvLgpKxid0+tNRWKLGSstPSZZXolygolINi3ej4z5GM+xRAFSMjudCabfdDac2TGP1" +
            "RgtSRS9PgMLECcRgXyHXdu3T9jSOfL2GxUnF+LbEh402h0Cl28H0Y59oX3y/Luv7ip9ochvGQPRQFgZ0LSh7+pSvyfq5iLJCEWT6UURXDaFgBKVshKtkjK7r" +
            "hv5w+nS6LCkvz5aLUckyWLVRPCawINNRTNeI2pEbg309nB0qZNn5CnYClOfL47gEJcnYtVE79ofZciHLzrPKYoybKO5EBRVz7Fp0t8XBKHs0Wy5Gyh+pegh1" +
            "hBEdGzFRcYBliKvA7GgaquPrWwVFscmzUQ8obI2ZQk2PZkfUkfJVRdYUqB5FmcrgTw4juuoEHM426UG1v75ckhUGhk+Rq0aU3CiOmTmi9tczyvw1Xk6eToch" +
            "JrvgsISeCIvZPdvNNqQtVQ8Le5gchvVNn4gOvYZBpuXaU3u6IWqpsr6k3ISfLKNt8+QLPyGp7Z4CKixRT+nUEM+XBeUT1X2jIusJJWszxrufdlsUWwk1ajYS" +
            "fYxtfzg7qFInCzuPVN+h2EpMBRTVWFCKHhY7qQQj6wcNj/UpBX2iC+pRK2qBFk4Pq/11u/m5on59jJ3OkrKcakcO9plG7VXaMLNEOD0qGAupiMo57aoWe69i" +
            "2TIr7rYwFy9ZxAzsYTsvM7D0BTVSYrNgxLOOuyBkpV0Kx0my/G62SbXYo3zNRlRXp6Slx7ZVa/imQiF3xlZryBY7wK8aqr38pnlUpWzoZfg2pqZ5/NpZf1qi" +
            "Pq3J6vRhjqUkS2k/DnwR4latH1KbGp/RKB8lETMFin+A0TyP+4yvKyuiyWgQefQitiWj/1LAWtedbtIt9tdVnR+lxzyKzt4uCrTgaZj/Y436nlncmR6PJqMe" +
            "Zdbp9DhuFUnC3HAlWW/mUVaYSyllkCMtAMZXct3udFMW6NQRjUpGISZdi/FuA4cnDQIf+iAqtfwXFGqCca6iKAYKmx1LgHHq9FbEln9clfWBNA7pvY6DhXsL" +
            "JQV6y29qfEr/rmMhiCJOKMa0Qn3CeFW8oAk4cBYXO/MwkhSeKMmC/362G9iS+qyggoLqEkWJaIgJ8f+PerNu0GXqb0SeLEWW9XoXWp/fmnJeJMqMdNFxLVnD" +
            "S/XPlKgz9pkuGe1ekU8J/33afdkuqC/y6SaFfeMCs5ia7UnkK3pZ1I+p5+dSWaxDWaxTny9TDW++rH7QltSN+VgeclBFltUX2ZcikWvPn4pWZ+pgHa0onhHk" +
            "Z7vdsI+RUwXAmgyLWVhRWTPzHwX1ZYUirwZQmZflyfl8ZHCBSdRPa0g9T+c26HHJT5GdcMdLFIGWW51koWVs+9Y/1riGz1N8UUmRLBdq2EtZM0I/FDV0WvYs" +
            "p1aoDYvsXiesS/bQxQu6rvre3Au8oXvJvmBJ6gWNumBd6s76AefLoZUNv9wP/Fn/UldSz5P2tjUOtJTjkPfEWzIoIA12KudMST07hxoGrpr1IkRqGMxyWWdE" +
            "zrsi19xp+w3KqBaoWX3AFmBszNO2pP7nEkVMiYLVFFKwGmDqXP0bZVmU8W0YaJQ/pNiYJyxJfatSw9UpWcOVOpos5fQetXB7GorxH3bIguNGbGj/2JJtuELj" +
            "qzhRJ2oEWljoRuR40BqUHfHHguJoq3p8ql6/10/DVIx/yuaB2uj77amN0j5hvMDxKhVvRthOsY4eylJiLjixPbOlrH0Uo1XPNHfBvETR4Wkm6vGMRHXsgiaa" +
            "3F9fLrUh9ZhN+tHLI0iAbmAvy/46SHEddZ2fdV1Py94osqOiTmGPoR5+vqTzM3vYDXxRO6m9UMMhtKLUwxcourtirY2O8TruIlQrKjPTiWiQSH22RHmw6sj6" +
            "Sr7SPGupzEn9CeP3yaPq5vnVsJwEWRjGBcqASkiRKLyOlwhZ381nIq2OVhaIjH5sGPnHkTX8WV3uzVXuDO2PtHyAmH0pnx1+Vh/llJ8XWMF3Ybos0qN6vjPN" +
            "M21/wvjnejSHOmvHGLPZUygvzvMHI7Wi2HlZMOInLAOivHixN3XTnHqCIqBGSnw0+C73DOww89GFq2ZcWcE65wRFT0bqON4ty+9pUGu4sDc64uPYyguvqThi" +
            "5SeMJ0k3opKn+wzNRUISvVvFVVizNTOzGvcy6sZUiSII8rpeH+cUIYvnIrABrp3VpEZhBNRMi8OUYg2P8VwUcxvSskpG/UTqPxVUjeNLZQHusDNiKMkhLXbQ" +
            "ckwtqYf/jmooI1Jhga1kf+pPhRyKBs7HKnkNv1vn3ahexwzrOMp7y+f300NRw2XjFRHLdGJlffxPdHehkrMS9GXgD7L+pMvUz/PYfRRLGmvUczQrQrGed0Vh" +
            "2k/EemPZ+JHIroXxwnEcku0ziui/vP3wdkWDVEhiivewE5aE+tBzS7Kwfjrl5ZkoMStXFsd0R6rI0orxoeNBBvWb5KsUpH5b+S6kYGPnF9SQqSjLvwqp9xT7" +
            "FDPrTwOUFXh6Vth4MO0BZatUfiqCsaSHcRwgtVCiIp3ycr/DuDvppxGsaHp+nqU1b49WpsQjQerjOZUABaORYmRrrdhBqrhltmzYxbkoyqK5zhfuM6XXFv1O" +
            "IQ2/64FC1k6U5Xtq/Shm84KzECu5r5eNz9EqBfZQeJZ0xFf+dDuP8+xSWPPpQfwzkz5r7wm5a6Bzq1jYNrWfKVI5/DdgRlKjPirrN8p6keEL95IsHKccVrVL" +
            "SX+s6KHIvIYzvxdV2sGVVASScurn9X+bnwPyGQ9aUa2PjXjXlOvXL/rrcyqF8ckXh4aqh9Menjelmh4WJ2YT/DLkeroWBsY0JFnKSLHNP8gpjp8+3V6hjoga" +
            "Kt/14eJUdJRui4STTs3iS7HlnSLGFVKPKDvRdERn+Ir2ynacdrN8/sKWF3rYB/3YlxmS0m2UkGUV3/WxvIbA7ZBUWavidqH1y7C7+WRhAfBEbuCW5IAODqZQ" +
            "d7Xln1BORUQ/h76i86gZWaj2MVIdpYbAhL5oQd0eIpmOxv2CelyVFMW7fMOp2MN4F+lhTl2Tnx5MRrjYRa9aOds1jpVU00M1JmFKGRuDpWCpiKLeFhqMGlV8" +
            "1y/kZ19YwyjE02R3l6uNZBqVA1WjRsrpN9ioftRDX5KjzyhGdkTXeTUL8ARqOF2KoQyXNIuNWSK0lt+b2176rtE0Gg6gLA0NtUxL3zVSTlNxNsp6KAv//mG+" +
            "RmJm0lWpz6i60c+2T6mGhRxBjSbad92r+B3AitJphr9Umpk7aqQ1pPZqJyl8m8Qf6FQAq8PMVGuovPPFXAMoa6nIF96mFI+UIbKmUsVbMwNsazrAOjqaLYXR" +
            "1c00O69mASbLsS9a8pekzXbZYi9mXfW2M1KPaKdR2UG88eLrdrQjc4UUNdyny0IKpLEkLgHaGkv9ru8pb83wdC7bh/drvKViXvEX4476VYbxMKz0yAJgfgVO" +
            "f95KYTbFYHjtll4cNzvK/raHjVfrYuXQ5ZwidPq6qdN1XHlqSx46vIHqgoYcY0rmJiiYBCyfZJoiWRPGafd2JJGs4d+I86hCVroJqcITyLIcB6xbTl2qx2Ku" +
            "HOfSsk1e13fZV0n+SgcTiWW5bw+pL1Sprg8UnRALjyUeP8IsdUxSG8zfyVcO4y530JILdeyyg7PtFn7OQtaGfK4sqJAoV/sBalRQfLcnzyYCG4j53r12u/Du" +
            "YX8JdwIuNHjL0Zl0I7FdcABifxuWaRC5TL0k93pRvlnpJf3EZ58gefdaZe8e9tfThSza2qAnUUpqk2o17aZ1ojn9Vd+R37WP89ApG99ke7Yj7ju8z/Y4bwIl" +
            "tPLjfrYj2YrUCp8D5Ok7kkM4h/mRI7JEFrEu0U4lh1jWQfYhhgq3nD4ax06U54kdiKylURxnTyfLSD1PJ4EadQhWbkPKmFlkwUZmmOWyztT/qGhDKskR4TOL" +
            "lfzSsJuCGh5KDkrqG2XqaFahAqIKWd+gvDCapKPokQviIApyBpb5MayKclnn6n9SkpUeQSoeBqoszbv3MKxtxBqAAx1DmWxNr2UHLo5hGM1uE/q5ZWfNqCl7" +
            "+Udy/5VTyba0X1BtFyiXtLBdUH8u9l8iHA1L24GcU0hrobS2NQVFkdRjBSW22smOLKfY4iB1wi6ofZzfoaeWZGu2DT1o4sSnx+dR5MG5jfXwjDxzULXjMPna" +
            "ho4WB5X08DDLep7O9Apicig97MV+XGQrpgzuQHkxzLKHJPXFss4fhs1e7ChUm2SpevhC/VOa5WV/mSdaQi0OWDzZyy9wDUsU2CjH1Ri0pVNYdzL1XRm5q6se" +
            "SSVdT6RFbeXJ85xO7EpZ8jyqLM13Cl+gtPRxXsMf0vkGzv/qIVECK6rYD5SCb15h3yFmop/V8/sb3WIhqd75EDc/wB6nI6mHf0d7PZJVHEf5sLqEv95VCsjq" +
            "Fxb7n+u5s0mpIawTA/YwIYH/xMze6bHCzj9uKEdLrPP9ge9xGltuC7K/Lo4VV1KfNcoHUrB2C8hLUfQX/Jy2p3bsypbfJy22cvg1Frc/MDO3tPUte2pFOfWU" +
            "SgmrjVSEXnfuMTGvTG32g2F//Vt5OCftDbQG3TWhRhRXgrqBfRapruyvPFPekGYV7uUuaDBOyuxctl17ljPcX7Eui27DwhrdZ/eemM5JkhcU/XW4kCUfvAAV" +
            "ggnlhQNLO63IegR2iMeU2xugsTxbRZS3q5/v9Zb8XrGvfAT2y0fzGxWYfYkzKbtIjfK9w1LkpcpZyiOwQ8zPiKy0S4etIVoZSfFOCm/PJd1JLus9+U0MrKHI" +
            "zEeylJ3Kku9l/eK1ySPGM0ruYJSWBbFP/tR4NsrPo3qZl3Yntko9q9wwIQonkJyCvcMSUdp37Ve87eMa5eaLfZix8Lvy06+lGGo4UWr4JbNw5k7MrDul88DA" +
            "x5YXVG/qqadR3F9H1F32QfSw0fkkekLsYXEy0in25thfn1LPo7wo8qDTHBezhM1WOY/C/npKOY8K2c1IPsPT3Vlt/nkUUke1EzPyG+IhqPtGd2bKMwd30XeK" +
            "NT1+13Gll2NfhI4M+XZzszhzCIuTAOyvT6v9Raf5+GSP7rKIHSzsYfvqDhH7a7+mG1FA7x48lMR7rxamh/fUs0qkQu2mE+e9akWSahOFPabKelHXKMyk1ncw" +
            "Y5rQeawdnStpvfyi+ZxyZ4kpHl1MwS6qdBrFGvW7yndllNMPNF6MStJCOsWa2Dr1Re276NZGrGrv1KtSP8+tDWnUkZC9a/60O7Wmln4eldoF9VxxSw/Po5CK" +
            "TvXxXvnMDqT2LqrnUahRBwuN6oJGIYV6iJ7OWm6jUKO6qh6OVT08FvFI8d9AyszPRhZjt9j9PmJ8W8lUzv3l+1IPiSBLk/bVd2OPGG+a6tN/tIkZZnwMZY9B" +
            "SwzwnELv5a+od2BAFsV177V6LGueFjK1X7NRTBU1lFTSV6lrirtYduiHGIAj4tWdakf5PEqSjxh7lVtwKVnfNpdY6jyfR8EKQNH5X8hHJdbP89leYxAO0Ybi" +
            "RCpbKlrkEeN92pwCBirUZwfUpsjIDqkngVjD57Qa8uVBjIIj7ag4JdJquFe5tYi3zOJgKH5Q2vzzqEeMkfl7eh+7dEUH5gdm+ESqbG1G2ljOulRHYCQ1FJTq" +
            "48AafryYU7qpLy55wPrkBFhs7TzKVKkDKrUzEl6ydgBUPlICTz2PwhoqcXtgrCQe3Y/CRXZ3VpxHOep5FFKPKbLI0oc0PyiUi6c9tl7D42oNhc8HeqwPM1Et" +
            "P0Us1fDryq2qCdmbKEKKrY2YYT39FAtrGGnfle7MEIOpKIAa5mft2omZ3stk20ZZHOFVVsxKWlOpoob7jX+q4RuEsQ21c5IF9HViTu7EmFfwrR96VJF6pqAc" +
            "egVoYQaI+UVSGym7FnzRzkmP/8YprOnmy0qMrIZ+zv3GzbS7wS9KPPH6EmQNjHklNTKTqbtpfYj9m+xMPKoFyFqlhiAJ3+zsNw7S3QNcoSTHUj9dSOm10Xwq" +
            "q/FLpP3G/bQGwJPedGfaEbl3VuGAsmZUQ96N4mkSyPJES82vY02+KdpvPFL3+CLGNuNqfqms5skrstdxwUdDTPUFNd5IL4gUqrhB0svfjzO1o06vnh3jtvE2" +
            "zlElicEcSY7RJOo36ztZ1o7x1WPKX6jm8Cte3vbEyyemfli7XZw5jjfyn8d7/jKX2rzCevigmF0nQo8yzCliraZTCVH/uXYn62FXfg/KSi4r6wEhK3m7kLWG" +
            "9kpZb6E7FWM7oagFpAHW1FprtCB1C52YgR6idnTpbUl3SkVmlCoXpH6RTkVopMBODb37zKS11QpTvy/0EPT3iqlGfRvrBr/brk1s8S4dWmuXNiaXqKcdgy0A" +
            "xWZZBEP0q4ZpmBPqubF4DV/kzmNdjijywX6jVadYOnis3iTKlq/npQ7vUvTRF7J+kbUXXcSmQS/Lc8Yoa2RBfa/2cakbXfnKGSwm6cfqvfxGbb/Uw5wS/bWm" +
            "Ht6h6eGEJF1O5/9z7UM5VdTvcnr4Yo38RPZ4w2RB3jRba5xkFlN72M7bkwWeHUiatTY1Kcby4mRR1n21L0so9x1SHxLU5DqkpLRVZJks62KN5soutP3Gicg3" +
            "mWF8kFVyUaY2a9RXpe3dlm5MTSzQWzb/f9WSEfWj2meFrGRjcrUcDUDNHycW13Ad3cUiu3FdelkK7+UjdbZ2K1vRt4/Xi+ykZqJlKM2PZmE4LVJkEaR2MYVt" +
            "KLLH6jnyBspBa0dQ36j9KoXRGK+XoyM1dyljaimXxNKY+svadpa1Xo58tPSqLDX3ZS+v4f2yhhtFlAgrUbLrqbetQpFpYL/x92R7UeeTq/jPsg6qWTkjeXSU" +
            "j+W/rH2AZV3Lsx61hhbxoOD5b0Dq/xYUaP1VE5WqlWsnY7QYxqOwcsDdaLI9G3m/JI/9PAt0RxzHYrbtrN9iv4ozpTXYo8YLnN/8cDryfpWPaDHQIWov9inm" +
            "OE77FF6n53SmImPuo8bj4swcqKfpqJaKZ6cW92mGN5n6rV6Tzs4dNzWZel5Q2chntx/8byYoT1Ai2zfsh5n6Vv0PiUqOwJdhJEG8soyeUJMl4Y1u9hm57lSs" +
            "LJH6NnmXUJaH/jwoHq1X6YZlF2+quJIS3thHjf/I3qVecjS6CQPj4GGt5+IZDa4UkMIc6+yV4voh9Ur9K7yYRWm/hA8cfPyr4buQmtosjW8EytX5o8b/I7Kv" +
            "GoPxx2Ht0OKUYRivDvU7Al12rEarjlnsLJnZCCmxhw3Ho8RO6GJar4UxGrENMS/1RbvVbraazXaexf5RwywiaSwZhyeUQI6lTWo4MvClJialaTSd3MOMVL6T" +
            "GoxHk6eBQpcDxiU0C6oJ4s7mvv1HYd2LbYgeFOnLa1Z9edv5zbvflXp4sP4dpOZ48xzVmwcKvxBBL3TRRj1qPETn8yDrNj/I332RzrXoZmR2W3qbvHDpuFKj" +
            "4vq/J1nCl6dnT2O/3GG5QPRhp4SzA46UP0XqYHoYvXJOpPry6M7R0eQIPUzwyY4LjXqh/r8jJXx5QaT68qZ0E4ypVoxzdSbGV1x/lWRlh6f8FrDw5UXkyzua" +
            "HiVzETvxtC+/6wWipC9PzeEX4EtHpI6ggXICZKTO8x1pfqkX8Tml/lJvU9ZK2xQxwDphzmpTol4VN3UnjyZP8wVgSoMibgA4sFfPeCNrG9bMnBqyNfC0Z/xo" +
            "Il7qiXx8aHZcp33KzvClHsk6oVF04+7RdJTGkuLIKUB1iWJZzQsKFdMN/2Q5e5R9qMLLS35eF3MajrJHhUb5qFHcy6+RbrBFzEaaHpJ3DbSQNAo1UJ6zoawv" +
            "0Fk02EPVp1z25pFOeX6m66HJeujoelh48yhcjRfIGj7BfocBeUPRXNM7Sbyv3aHoFA6ecI46FJAW/h6fR8oLbNm8tBvZdA2C/ieGlVSHzofo9dyI6u3FuR7u" +
            "EZ6spIs3RFrSGwf/T2binRu8z+34aOc5Ww+P5T11uqe3CDsGG1VI+AtdplxjhutSnImQciT1Qv1Fnh3gv+bvsNGiUw2B6eN4oVu/3izX3hfqE/ousLD4Oo9/" +
            "yKLjmRy+skDNx5ePbX+Wf9cL9a+RLYT5posPy7jEbZblUwQFaA3084MFKMbydwqKn5YhBV+pUg7K8mZ9WcOz7IfFGzoHB2GP38/15M1t8jfSBaZ6q9l+Q9hs" +
            "pJ6Vtw+P8msgvkvNL3tyyqm3m+iJEHPKD6VHtZ+EIApPojhPJmqJM92UbQrpqlS9WYcRJmX9UGSiBFkkqhiZKAvaclNKl5/rbZW6lN9onYySCL0VfDUffyI8" +
            "hXYNt2E37LY1U2aHS/U/Lm5VRild9ebnejF615HqNrtNuw1fNVWo35N3o6IUc135yg/oS0hXJet20z6tzJWXxD1zPMNOo0ilsNeohsiosh4D6tHKe72sSw1S" +
            "ypgbR7NIUp08NkvxKuOk9brNEafVH69TUBsUyskpUB+nBHV8b5hTN+b3voo6ZnZUrV/OcA1/1yg7YiP7DL6iq5RpLKkiZ6vyhs7KuuU0sXEwGxaybszvOqpv" +
            "CpHKzTeV2J/FBbU7P6tUZBGFC4KiTOOpQr1Q/y3x8i7fLbR73Zd6qP9524sVaitue0zJ+1GZocQ77J5xMk74Rg/P0WKRE3zYdph6qf6QuDgTFruL7kvb7xMz" +
            "IF9roZxlrfZuKesl4Tef5q9NiOrzYxuOgEC1Q895XsN9tGtr66+XukM38Fz13a5SmPpc+Q2dLV7s4Uu/VaiDNC9XZPmll3dKYeq3yxTIGgaB145Wp87Qi8LS" +
            "e70u+sy193p54daQt6rKb+iAilenzs2RhYz+8q5KnVqNWkPWCr0n0t/rvSTf6/U8DkXp5fdaWpL6vXnv9cIMqXw15srbNy5Tz9MuYKLuVp1e/6VDKb8bFTmS" +
            "OUMyypPU4ZwS+87gJVi35u9GxbwEFEZBcKQexqu/1+uspocr9f9Qeq0n+isIotXb8P6SRl3g93qdttdeQ+fvp7PKCuWsTf1QjEr97Z2HEWGcauH3/kgdmkPR" +
            "a7g5FMc/esz4u/ryHCpjqmy1h9Ky/Z14TzSXmls/ruEfVF7QQYGVGr2i037Aaosa/or52ersAPNeVLH001Fhe/85v1UVlGT5FVnDYUEdn09RrITVqCfolmmk" +
            "nenge71sqydfm+b7CcpTGTM1UihxXhIAdRDHpR8WTSh2PES9j9ZRkXo5it/r7USL3RERcuQdqWa7GSD1pPYuOz9B6p8ZZQeLV5yeuBiLvmCev04KimPnUQmi" +
            "UXYkzt/qBcVds1wPT4r5K83PkaDgiz1+58feQb4YI3KUPGZ8V9xazJSTJ5Dmw95UShNxEEBS0M6pk/K9nnjnx3WcCirm94RYS4yCQNQnjXtrGBN+bFAmGPYX" +
            "Q1nlXLmWUtTwTxpfr91DNopPHImqpbXJKiWls8pPGodrHt0gKiLtIjVepeDtBKb8OdRkLoOnhEx9vYY+RI7Yz1xmZmZizoswjCfBmaCWicIT4MtSlqS+Wnu0" +
            "REluXpHUn9XQso0pa4SMjZxR/tlJpZA3vovUL9YoW5OJsSYNovDEUXy/elrOxcL6ILVTUpagoCa6b0Q5M6ecuZ80ruOTW6aMnOJIcJp/hEqDZXk1isNJkVIl" +
            "lZoy2nKixbodGJghAanfMd6T9zJHb57U1i5IfcfYmffylVOnjHdKnZc+98sUpoqRIqgrkPVfjEFJN1bzOMjC1MPKn7xS6h+MT+SyrpwKuJct2cvCTyeLulYS" +
            "haldFaqIqK2enavUvWQB8IwKSl3IUjSq7INg6mn2fzVFjFXWrZxSNUpKZI26Ne+j/G7FZfvrmLG9RF1JLz9U64uWL2SlazBso56v3Sf6SKHm2houbDeeqb1P" +
            "6nzxXWtQLOuR2vtZe5Us6+llqUBQCeqUYn9X8S6bPDscIN8oWmOyXqa8hzRZpbCsvbWPaF+ydh1Tm6l7aw8ahaYXX5au5tejGv7MoGvFTbBQzSKGX67DeVx4" +
            "/i6wUib3Mnp8QPfssVXcUBtb6kyitgfLclWdz/sr13s9Cj1aU5J1O896Jmq6zLKcVPRI8VDVuDUorr451qxvXruiZvI9v1m0IUW2RsrkFlRaTTLM1ZjaW/tN" +
            "MSoxgqn8tok1sfS5lXnsEaSO1vbK2QFbUdaRZzlNHkmk/nrceMX4ZZ6JxJw3No01ypjs+uNgD2/UqMlaDOZiIuoS2Q2jJtt9XiYDUWj2HAiK5y+ZwXwNCiUJ" +
            "6h3FvMw1tMaWMb9wBhxB7RK9fDlKRMwlyq3tznVD9tR4fsFbEKKGLtmogkpWpzB2u5D1xzVq+Qb5I0i34J9NvpUBpaavPAbkG0Xq3axRZcoylPYu5iWmjtV+" +
            "Wcpq5JSmDcWNkwn5tZmi/A4tRZYl1lRCG9RbIKGg/j+DMlHW4c/Uc81aQ6fwvs3jxlUsy+Q573IUz8uoUb+i6eG4tlZJhPY2eTavC1mGYa1RhMV83LiRbK/U" +
            "qLG1VknRWy2oBwz5p6+c2sn3N8wroOhOBX/XztpHr5Sy6C4GyTpd+xXuZY4R2aSeU4umG5nQqNNSNyRlVW2FpBLaZyF1uHaDpBpENTV56i5F0ajnWeflSLHE" +
            "369re35nLxI1/G/GDdKyiTghqxZFN/6b8W+uiOKVFd8We9y4yDqfW9G1La+UdbFkey83TpjaQLNeYQ/Ha2jvkjkwJVVai65JJWJU/ozaUpmV1xwpS0JWm+/A" +
            "4Pgq7EZztTIQ4+sGvumU777Qjq4m63m6DYLU7Wx764YlZ8rVbfbXrFR812Z1ryfm5FVbw/qaoO6t8UXwcTGrrDp/PW/KGn6TR4pue+tlXWJN/kE+vr7JK6IG" +
            "HQMWdl4l8tX9D/Lx9ZXauyRlVC22ZrVhxfoDIesrxfhqlHRDlSV2z28KWWd5x8FvHSm1MKxw2nluCKvYgYzNvzWzGlN/z/3VpoAgDY4VZChzqroq+kltasRE" +
            "fYO1tylbg+fz4u9XV4hv1h4T3/WN2q/LPdEcaqKtY9+sccs/YVyqf7Ia5dKlrDeY5wmKZ9ElEPT5CK8UUoeqUS4DOowrKIo8PotV6kBZFgYkwqtN5pna67UM" +
            "WJLl+v4wpzbkkU9KXBszG58BKmJZ7tSbhZJSvWbSJxX1obSiZmRBDa3MiuyoGwXqd3Xyl/4lqk2UVVDToKBuVGoY6DVsUntYZ7CGbtEWSO3OX9DMkWXmNXSn" +
            "Q5V6VbzmRv9X7pMa0vFps2OerL1unKl1zJZJWendpiOpJ6pesyH1F7ThSWr7U8S07LbbFtTfiPxf7DWjk1sMxuZ0GijpYm2KDFFNIYmpxzWvWc/whh7KwvqZ" +
            "U6JQVttqK9Qt5G131OI5nBXNvFi7aABX8034N5sePvuOh9Q+8oBrPg58xwp/9wnzkjHDUjtdO2HCbzBigtv2kbqf4lToUU7aTrvZti7ULtSYeqN2Aakmtkbb" +
            "Y6riT+FXs9YFs0RZ9MyVqDPiDFbxqEQUV82ml4gm3umf2fgiFn4bsz/lCeNH9X9XpgK3i8wbyNBLgJdt+E0Xfh/LNvyxeGumUEISUVBetk4jhRc0FOoPqhRI" +
            "yymQdZplKdQ+6mXtJAifL7V7tmedoUwtZ2unag7oB7UH/CC1Qm85lXMZPMp3I2TAZk6pl8+aLjN2u82y5njNwh46vewejmRihCbS02dJPZXLEmfmgvKAmkqK" +
            "dT6n9pE3sF3u5zZrFL9eumRckDolNGqF3kprsY4wVLXtWqfNN1g3QDtcC36DbS96+WBVe/ESImpU7VKNZc2MS7UT1gnUKMrD/ISIiaFREfw3+wToIVMoK6cC" +
            "pl4gu6FHnuwEnVaHrAZaALC9VsfqtNFFwn6HJ1bztbHNrqHtLayv508F9d36R0o3KoS0BkirsbUBWU0obscZ5tSB/EaFUgIKGmGeInvjW2AB6FKSpH5WP6Dc" +
            "xNCoFlAmvpc4a4PVcH1H+qSeMH4lf/Ooes0ivAJkRWZEJ/JRl+z8sLC9/yzj2+j+r8Bf8Ft+0zfPgqzYplukfiHrujxvRYlq+Q3f9M0p1NG36KarW1BP0HdF" +
            "em7G2OtQjzVPmdgaoL/WCo2tttP0mTqsBBUQvYUZ2CjwBlAoDagTgmLtfZLGV6Zlt8Ora5Qxr84z+hlse3R/twrqU8Ijpd26Zoe5WAWcFZTjsocONWokfEqJ" +
            "6v+KUwzOgi1vUWvYUDCWTyD18DnN/yX8WcPMz9o4V0655YkKcuq74vZRJsa/6OVh7MTt2DrLugEaRaMSRgpTfyfuzpVkxagdMfWWkITGVcRPPyB8UmnheVl7" +
            "b05nlUj1L+PzUoo44TxgfJXO9LJaUptcQUnolBipD4rYnVdCsYfugPH12uNr+rzmec2QeuLKKUtSX60dXdNPpp3a5rL+rDZaw08232t2wPjF2g6xe1LOeGr6" +
            "yVDZa4bUHQVlqbuZ1b1mBwyPqMQam2t7N8QoqicGU7uJSkonw4mWUTIfe7RrO2D8jvFrV+Ch0L0VB4xj/yrq1L+K+i/GU1fgh9J9UgeMf/hXUb1aoO7jld2d" +
            "7stSPVkHjAdrd3OktdK+ruzLSjVqD++X2+Uz2kKa6pMa5NRdFSrfG2p+s8KTdQD25tvIhk6usOCZOfQy+YnStc6htcLWZm/toct4hqp+Iqzhb63lF5rrJ8Ia" +
            "HlrTLzTPT4Q1fFh4eK6sfky5ZLEnlnbGUNNPlFVfJ4+ve/meg6YXY3O+D4Z6jPTwVtLDiaWeW4/1saG9p01rTN0jd/GrSFJlpYK6l/pLngpNrKrnRfhSjMLj" +
            "c8B4sfYYnU4IJudUD0yqRpkm6ii9AZ/Y6hmUZDXPDdfPTAX1BFO2QrB9tir+HkE9aby7eZuh/zzVPnXdytVFOX2o9AeI2l763SWd2jqP+lSFMoyZu7K1KPOo" +
            "p5t3z6EuXnd+uyxvHK1S327eVaX6F3ec2nZqK5eX58j6WvOOCnXJmfYlM5/66+adc6mV7bK8PKeGf7nuptLvzncWbuls+Ty1xPFFLM/2qlS5DS9u/9YtJ7eJ" +
            "FiTqXIX6x3W3lOt3HRPHrz7eebZzrnMurNbwH9ftLFM7V247RRQxnXNRlTq+7vbS7z7jXNj27D69VKlfr1Cz/uWoFyrUT4MLhy5P3VOh3jh84jLUX677YIn6" +
            "cWc2OnEIyr6ilKl/WfeRsiz4rgvbLhy6cFCWeS2/u0ztJFmrSmLq/WWqT9TBtajj68oW4Afemf9hZRnK5uNYtj7bO7dYpcoj5Qfbp8dyauuzW89tPedVW76s" +
            "UT848q0nO3e0llv7VqA8C1xV58+te0+JenPnmSed5dbyyr7jm5EoazxSF9cFVY0K1upjpK5v9udQJy5DfbzSGj/1LzxzOT38QkV7Z8HlR8qWZvm7nnJUG7qy" +
            "dZ49fKK5q2Khzl+WGjTLLT91dVkrW+dRuyrfpcuap/Pfbf76HCtaWPn5dv67zfdWqU0XdyjUqEr9H02/Su0ES71ma/x8nqyuJmsOde+6sqyLW51HnbtBf/e1" +
            "9vH4ejYsU19Z974SdWH72dtOSTu//VkoZev7pPFcxfZe8H6y89QWYeu3Prt4brFKvbLu7oosoIQsGMm9qqXH8XVvmdp69vCpQyuHzm+9gGX7hTmzw5bm/ZX5" +
            "azo6BRzMzNtWoA0vHKlS72p+oPS7N46QrINg27ae2D5f1ruaH5pLnUKLePACUhVZTxlbGntUwnpD3IDvKGXoD+My9fG5lFowVpNO9Rtqy5+2HmyX30d1OrE3" +
            "i8rUb2jU1Mab8pejtjQeVv49sGfdqHpDP56Vaniy8YTWPi9b0zVfOzH1eOOjerPWX67c06/KerzxMZ1qvowZ5C5DvWgWuuG3fXrr1Ous9tZJUm+aan/FXb+7" +
            "1lsnSX0/z4qOss5uP0Nvljoce7rFb53a+VungioyDg/bZ/tnPP2tkwgjXaqhl8efP2Ff6g/9QOZOnfuSqKCkubvQvQSzfxDJF7trUfuVeF8nrFl/7bdOBVWY" +
            "8RM2vgjCPLKXo9RIhvLVkuut9gqmoIrogm9c9q1T0fLPajVc+61TQX2uSl1W1jN5BgrDCLu9/ktHMpmdTCqvDETRUqnPKlTYxwxD/NpJvHCloLCcu7WgvqRE" +
            "/Ou5L/XTQ/qrJXrr5MhXSwVVRPzr0VunrKDEa5G2X6aeUOJVynx37VW0o6C+rbTh5d86SaqTn+qPrUukhe4q+lRIQ+ponjMI5Fwh9XUl//JpfLntzS+qzX7K" +
            "uEmz82TV3NVeOqnW5hPKv7s2xu6cK2uoUps0e3gac9/O+RmWbO9NjcOanaeXTsH8l04FdVjLAYpfNiVfgV6GJerZxic1KljzrVNBHShTa7x1KvqryOjti5dO" +
            "yttx7aWTShU1DMRLJ/Wdk/rSqaDUzLyB6/XPHOVx4uSjCzPLUvz0uKC+p0Qy9H0PZMnx5clX5PlLJ1U31EaNg3iUHZH5wjBvq/7WqehldRqcipdO+Vsn8a47" +
            "KLXhpsbTqiw/K95V0TNyjsbdrlBHtBqWXzr5yksn+fM0zERPFNFg7TwidDES8SILtEhmF/GWnwZ7qMVNhdmf3nwq0UUcqGnkpF2V2q+Mr8SKXM5fXeQlaEX4" +
            "78C5OnVQi1iLMaFbUZvnMPpnC6X5mSbrRY1Kbaihr8c/waCoqa3GaEUq1mJ+YvRzPf4JUEHa1anvK3mKxzWQ5Tp5FFSShRFkArV+TB1RI0LbsLrBflWyf/pz" +
            "qGfUOJz2pJt6YeSJLH7AoDOa1FfNuoJUpFAiJnREUWGxeC479GyMUDS/v8ZW6ocey2mFouUpMzJmeVVlfUnJCzOx0yDEeOE9R+gS51J27LMKU/6uhFrD4bew" +
            "vSbnrkQ9xIj6SuY17K9HS/F7HZKVR+GBJSbGFtb760vmR5SWp5i1GFioiH4C9sPz0+7E1KlHyxGhe2ocnhbYq2p/vahEC88jQos8tJiD1qHIQOUa6hm9MdIt" +
            "WuzCyqBaYtTkskaN1RqK17OFRonsAhXq83MiQms5NSjKRVkPH1Z0I+mmAa2Ve9jm3M8YUwc1KtH08LCiG5Td0GM9dFh3KfHNtKKHj6h62CeNCkXEmjwXuN+e" +
            "anqo2o0JUFnAMZoU3QVd9G2d+rYa99vMaKZUcq/iVQ9seUULub8+qUeEtjmquRIPao4ePmEOND2MUA/LcXh6kaaJTxtfUfZEZLN9WhHKlV4PI6BErmqvkfq6" +
            "EvU3gV0vR5FWC2h9nkG1oIpRyaOrvGZD6xa5qfZdI+W7UKM8h/9sc8CF/81zdIv9pkIhx7H/CzvK/+b75e/6olZDDpSiFvxNEV9cUidKlM4wV6ZG5pe174rc" +
            "KhXE0Pal7xpr30WrKKWWvNKb9svf9VFlpMDSpKdnThEjrJ2aOqXYqG6606OY2nrOYIqjZ+naq55jj61QjmSUSKO5RfcRk5pOPaBQYOlFhgeSGHH2GaDstDRS" +
            "ntSopAubKBHCBWba2KWFSlahPqtSGEna56ywmCkIM16jcaxSj6kUzGAR5a7g2ZXnPKfr2/pMtElb945tsFG+WEWBJJeejMeaJExz82r9oFgln+1GvahHue5F" +
            "wX/PPM+Wd8imxiynOE570H0ZKV6HIkN7UqK6kprl1GsiZ1BgZQ7lu+9Rtmb6J55ZnMmZyJjmNXxN3FuGPUNXUHl2Z52S9UNK5hrz8bsqmeszHym+WzSrzWoF" +
            "9QfFd/klys9gZMl7Z3jLUlI/reerrwbYWJbGLYGyvKgb2UxNFVk/red72DbMU6U6kqxutYacUQ4mhGBwMAkHIYWeCnu9RTyUgi3K1J6aMtuey+8jicLlc+ZG" +
            "/eRQcjAUFOfH7sAIkRRnbSiop0hW+J7wIEY7EVyvhweCSFnTWktk9ixk/XmdZ6LYiXakPRElRfzgkdRZa2o6FYoj42HOcI66U4x+mIe8wB+6l+wLlnqDkqmv" +
            "KecGND6YCDnLO+Z4r1Ix3cbEfPeB43oiBqbINI5rSsyErt8mZeoLghoGpfOrHtZwPvVC/Zt0AsCnStpJFObX6M7s07Z6B1W2/HflSVRXP4kKYqK6b8ylvkU7" +
            "ec53r8qilui/YZ+25lH/S36KAlR8ZdSrdd4vD9xBPx1gAWVEC4DjswNzpHvSWhBJDxvieS5SvN5I3ATz3RMVDYQNgH1X1j1pOYbUqWauUfw6eOAM+sm+BJge" +
            "FCELc6F3T5lORdZrIu/SwE376XbSXf7ztF2O3PNWqySJe/kPNY0q8tXhmgFb5NIcjXqt/iV5dkg5791enh+Pct3PbLW/pKyH6F7lxJzZc6P7dDD79wWzqocU" +
            "odGa2cOu64sYnEJ7ce4aIlWp4RfJRr3BJ1E9J9QKnnI4U0u9wzsk6qzIDDXDkyjMPh8qBX4ymLuKO8bSzv8uzQ5vMMN/lmcUYXGybmapd5NnRP1Q5Aw6a2WB" +
            "6KVIFJHxXqWkrKU8WxOdlvmCkz8Oxo7UGaakxZ5Bi+DhhCLLx6iRkooVqshOArJgS63MK4hCWxT3klXqK4WsfuznWdcjfJiB0UFjQ5bCzi+Z98n1kJMECdl6" +
            "Kr2l3hIY+7CVWovilvZQ+S65tkn8BG29pICDn07allGDVeqaPGdriCNs64DmFTT0PQxv1eJol51cL5i6JadSB2SFyYB+BJVZGUX/VSVxDeUZUboJqKP4ZVh4" +
            "mgidNO9lX+sveeaQ7kxGUIBKqTBFd79FPOiZUsPHixoGyZEkwtiJKakh2BpRQ1USU6H6XUeIASrFPla0UKUOghXFHWIaZUcdt5Ldte1gNmlpbpzxaDxiSkZ2" +
            "TY9ko3nZXTvddFM+JEeGoP5cvAxKIm+HzN6n5XftJsVA9sc5NRSypiORK1TP7yqiXLIsSV0SUafSODvmu46a3xWjQ3ezvH7yq5DqmCJjo5H5ft/BrKucdxX+" +
            "CStXjL4rjWG3oH5aH+WBk7NjMlMr53elOLduKihVVrGOCg34slJ+V19ExyxTBzku1tMYTzPMI0XJdHeu7WDev7aI92BRdlyivprH04TFnecLDwqlusNvBCpr" +
            "y1uokorJio4fTfyQzIsjYr7RGxW7veFie6q8SQRaUJRn5Ol0p6B8R1KUw5MoO5clqLs5xviVRISWJf+uK4gIXRSgHuKXQXlE6Pa8iNBeWVZM727mZXfVIkKX" +
            "ZL1AsUwvHxE6L2JUfudyEaGPVqmYMr2qEaGD+RGhFU7KukxE6Iqsg9RfVxARutBFkjWiuMmYxcOTbuUOefJaoIOYO7GtvXQV34X9NVlOR6nve3kMb09q4nTD" +
            "VKb2bspXzkj9rqSGOFEKinWx2+4qlF3UMKbXH2tGhMbMv2rqEKBe4+yra0WEVnXDkN917MoiQmsaJfTw8hGhNYrXUdzqrdKKDbUe82ppGh9zf8UiSvOq2V3V" +
            "vKzC9n6T9uZJX4ne7cmx5Xixn+zQJA2ZyjO9drKuL9IFFNlnKf/sptJCj+w8nxFxbldfy+4a+/BVO/NnUIGRW9GfFdlX+duq+V3zDLCF7b1U55PblHJJutXs" +
            "rmowxZwqXnIZPSY5vytnd/WhjvOoL9LpwWQ0wLiFeDLUa/VklmH0OUyLGYy3AgFTZHtH6RHO75afQTGFGb2KNmxL6pv0vnLcx8jTHeHLE3YX19fdrJCCIyyQ" +
            "vUw5g0Yp11COf3qe5wSCcvLFfJ+ps5THh/Peh+QTkf411w/cs3izhdvdptKV1B8LKh2hL4+CQXMvw8+sT/Nyl4pC/VD017gvZj1u+wCL70JbgJET4x9fyufU" +
            "v5eyYk5oBlRAvrwg6AuqK+pnM/WM8XOZLdcIgpdH2cGoVySXVQudwIjTQKSeVKkj2dbImF84HxdTdhHb2Q9G6cG0l5YmHll06uFcVgA1BG5xHpdplJfnUR0G" +
            "Q5wNjmReZpTLVGGQesYsLoTPhjOc547AWrRTyjJWotQcoEZnOByytE5Zmk59SfUFOMM+SPPLdZwq516y5eU9vaEXD8Nn0n1pLVT6q5MPk5YMcUCU3KcM4+Ew" +
            "PQRtWFP72c+pYr+MLS/vRw394dA79NLBMz1dNyTTVmQVvYxUdOjMwTNbdVm+kKNSu+mlpBuc7p/uvwF9x/n03DUKU2h4gIAy6w/z7H3BmtS+OmaidPtu/3QX" +
            "Xx5fXhpTXyAKpc02zujlcbBmQepzlKkB1jRDyj05opfpNZlLcl5h6kWiXh69PLpyaoXWUShrOBoyZaxVDEH9KVGCGWHGxctTrwiNCuNwGFqhldF7dpEAW5SW" +
            "dibC1NNid5PSalBSvBuX012Z+lH9A2L/lQxTS7yt0ayUOjUYObWXqTgdpjdhbCNZw0jRwzJ1M80OfnC2f7Y/7U9rRb1WK0i9JNZRoo+h5YeX0QykXLaigY+y" +
            "Nk8vK4ll3U3aa5DGz7YPLzNKpPb+Ls2weGcVWv0g2PnBaha7OG1/Jo8IPYswA2x6ON0FxVitsHV7xvgT8SJ+GEYobd9lZOUU54cd0kowW16LmtYkpeainUVT" +
            "lLcMZdccS28WVnSvcpMQqCHuF6AMpkap1FRqpNxmmYXTKMN67ssGFVmmaudHyv2NWTiLiVquUvrscE3u14sjGGHDdFu6ld+cF9og7bWrUHI2j2OiDqcHw4oW" +
            "uSXq7+sfFy0fARUtwzeV8n1JOepIeV9+U3eKNeynt6VGOGd2cHJJXMP8JPBIegzKaLptulWex5VtatFfxb2U6ZFsJMpm7lksxd9gKDWUM+w0nPppkAbZ1qxX" +
            "2JuYrI0qianDeQ2nUL+sn20DTpERV6hDYLFPskcf9iM+32uibPKpmdiJPe5z+JoWeUowlwr6z5H6HlH4G9y1uR5lkcWcaP2kDyv4GPO0iP0zUjZSK7QWxazB" +
            "cUBJvKCFMZsxUCiJZMGeDP82keGJqT8W1JQzJnQwc/0Uc711gRohhTce3HiWZ1A6BBb7T9lid2GfEtPu3Bsas9rUwnxhk5ExasVIoXd12pfUz8W6F73mIC2g" +
            "VTKMQMoX1k3hy9pxO4bfBTJvDVJ2nmN3TLcc6Ad718zspD/pG8MFbiO/yOmL1B8UntvulH3mgkqphkTFgZ8psl4RHtVJl1bmkTfwliKMZ0DRV2GdHBh+Ewqe" +
            "Ts1ENmX8LrknwkzlIXocwUphNkLMTQlran+B9iCUWSav4Y9EtB/MHYqEN4iWkEqsCa75XcNreCLHUJ49GL/rsdyPnXTJF9Dz0FLUoI64Y4Ch1aRdZpHtGb/r" +
            "i7K/RrjdwDkY9TCjbKBGX4ZR4v1H0cvfFDtEsIKgZwFtsaZApbijFFSdc82DdjB1RuTWAZ3CjHNFBm9ojzHuDkVo+nqIPjSphz8S3lvUqKGbZ4SmHH5jlIWb" +
            "DWiPoD9T2vDn9f9Y3MMAabN+sIgjZYpZ/LqoG0bcjLF2qkb9vP5nkurilwGF2ot6iJm1aYS1mepP7aK/vpN/F90X92ikYDQF6EEcKfW4PRSyFD1Ua4j/DbZr" +
            "sF6agrQEawit6Aazvk69IvMT9VPMGRSJ3ASaHmLBk5+ZNTUl9eV8X4mWUKfGgqoLaZL6kbgPAHo4ykI5l6YYf9k2hEYZTt0BSbbMaoTUIamFsK9BzWUKdB53" +
            "kV1KD+XUXRepXA8viTwjaG2wFrC/hn7GXjawJUibqOVHUguRujHPip728QTRjzBiDbQ8tN8EqbAZBkCpmeVR1kFxkyXr+56zhDpPOatqpBlCD5vkaS5kXSry" +
            "f/Vj31lySQ99IY2C3sB01kQfZHeaUz8Up9+pjaaQba/LcxDYbJoU4Mvg2zzcd7NOIfU/yfGFIwX9lT2X7hmAbvTJ08BUNOzDTlBQf5KPZWTavfYi1hA00UIr" +
            "OqFRCVTYxJxcoo6HYDbPLQC2BknCtgBrKL6rAV8Fdt5Xdf6HnKEMahhD4RoOaU0Cbd3Hk/z6sKhh8V3/W+W7ZpSdG3OToeeoPuS7t6SJtqzhqfzsy/edXtAL" +
            "lmaYdxxqnKLPYNiUNVT66xfMP8rHMsx8btAJOsMOSAMO7Hy/OWrGYK99tBtFf12T+6Qm6Fs6gm62kKLJJNbYpv7yMScMBiNTKZnFA0dYOqKrGBwf25r0BQWy" +
            "Xu6/3C+ov5cjBb4Crby3hKYMrWFi85iso5332446Un7B/LSc9dDPEaKHnuLqQA15/LeouIHaGtfkI4UygB0Le3FvSvGiU9CoMZR2gMk6X7ZfttXv+g/SX0nf" +
            "FcP2DWwo5stWqYB6UGl52RrjPnpisjBbgoKzg012LWgE8FUBWoCp8l1fVtoQ38FQgfWGoPrNAKQRJWV9yrhIGWw58kNSw5t1a8VKIftD1Ec1KuuuQZk0UwP1" +
            "txSLeyyiSiC1Rp7n2gTPzoD6J87azJnNjbWyUDPFslp0Ps9ZBigbcne1bMikZf0JUTvodgTHichMHENrUFYKMxpSHbJsYxGJPa3RyrGaDVlQkz7XsEN3sTgq" +
            "RYrxopBaTdYvobYg9SJH75Qtn+fKDkt5hrlwtFWkHuKoqRhFKadSPXNLvpfgW77YXxwD2ZC9bOtRX0Jt/5FaLOtvOXY6ZjYXulHOUBzNoS5SXBGZGwQtTlKb" +
            "n90FysZCDx/Po/1gVnk8j58Xy4X+RHciKP4uGd0kXVsPTSmrQ+cbBbWGHtaSjTDihB7+IeshaUZGdnsV7eijPk36rIe4PqQYNKgbfZ7T52qGRn1VRBekExtB" +
            "VU9hYR9H1Nhm6tski/J113KqVinbeA03ES0vIq6TJnJ/JXOycOAOOqynFPsIqYeYMiUFdnBOFg5a8ZiS+lshS2h9pb9UrY+MgnpAyJrIGhrz9RfmjDrooajh" +
            "shINkq3UqvrRldbmIscIKk6siaqe2JBuwO6CqX+ijAbFuEUrFa5yQp+SNKZ+68qpmqRO1fYZMoNMitmxjTWKDbMA6cZrtf15THeeHdaIl5R/1/dofI3FWFnT" +
            "ztfIYgs7/wGFAs5eY1RugO8i6mrK5Scj+1DeY3O1kuTau5uybEtqbTuf5OPrFto7SDuPbz/mZ73H0Zzk42sf+5cFNe3OcCaqrTI/5DV8rfZhYeexpN0ivleq" +
            "ZXqnkYJx62tM0erL0vur3L95NkzKLI399WBpVCrxkRQ5VBrSYr9RO1C0hkF5qtWsY9oJGKx4zEzUcMQ1hJLQulpvBc1O5TV8rUa3nW2Fom9OpdXiUlDU8m/h" +
            "230iSp3WX9pJ5ZT2SZlow7fQ7VmRyUKjcgnFeaXNd9s/bbxpqtFj8DZQJRQB3hrFWxz5z6eNhxr3apTTLROek+IdDkOl+g01YhLeHaqIcsIgOVKm7rlcDR3Q" +
            "vxK1paGGUAm6QTVfoJuN0mM6dbL0HnYI+4fyE3CYg0rU6cYD+svWijSgRklcph68AqosS/U8DiN5IbPj8atgiqFst+0T7dkw9gvqReXlAgbT5jzlnHEVdzSu" +
            "fcK+0B4OfV+V9X2ziH0U9zh/Z0eNe9ButVc2XNzteTpVtPyw54v6dWTcA7x4Z6/Y0zjWKC//rtXfcNPdj6fTZZU6ehkKuwzP7NNDBfWe/FUL3Xwpl/zG0xRv" +
            "wuTUfuUNHe9V55V4OD2synrGPFW0fDSnxEN5C+mg2l/FK6Q5r+dBKWfkiUgPqm34JeX1fZWily3owTisU9+/nKzhkPwe6RG9v+RIwRvE8r5yfvMG7z267e6F" +
            "TbNW3FK/q3jb3hNZWjsyyAomY+dbUhuilv5dHylRTpHblW/RuCtzqOK1qTcvtyvU740K9YTyNnCebrTgO+mW1KOqbnx7zZbHVoxHdBtOa8P9ZhFlZZ7utvAF" +
            "c+QP1R5D6qk1KXy/3GI9VKg3ldgReEPCm/uj29FPGzdpli2Ym98V5rSSjdrUUOP2uEEpuStfBQ/SksX+4L9K1uHGQ7oVDejWuFIivCUVl6l9JcovU0HVzn+w" +
            "EZaYSi5Zf1qR9ReNx3RZ/SpVreHXlddwPt1A6hTay1EPnJXudFPk65QS94CvYOtRD/y2ewKoOFapkTJSAnoR0MEbTzLqAcdXcN/YNAUDXFBq3IMg9CMt2oG4" +
            "D3uiO3P0OeWmxqfU+TWK4yhvB76JHHRnbhzorXFTQ42WMCzyuvp0Qarrdl9Gql/Ww6c1WaXMrlBOA+UH5V7+jF7DPN6BFPgyvg3QqM/A+FIjd83AqnuLUDpR" +
            "J1xIW0kjaU6alPOlplJbGr+mfdfQp4jfeBmuETYG9aSuZhSU1MbGDo3yxcTcWwCmMWhO6lA0SUzdVWpDpugyJtQPamhWqS3aWMY4TMjAVznwVa1k3WQdRygt" +
            "U2r8w9mx2TFkok4KFHDrEmsedbqhR2gEaT63INYR2hCjUs+hfqNMxUQBk65Kefn4cntBRF4zfguDD/BQ/VthM2wmIneSpJ5R3koPwyE+JwzxEUcapr2kkzjQ" +
            "Iu3EomyOCrVfiR/l9vB5Cr3iHIjHfiirAdJgd6XKuj2XFfTwNauHPsSC6WD9eEc20Gr4m9rs4FBybY8fF8EEJltD7WmkHlIovNWL8TbwcjtewE2ZIm5sFtSX" +
            "lHe+bbrBidJIFl32BZ2y1GyTkrpPpeiduC8pqHJRQ318qXHMOPqA53DACdwv4lv4cj8j9USF8n2xju+mzFmJpVMbNe11I7rHKsKRQGtwdPSKRm1s7NUothY6" +
            "lczRw4+pbcEvo9mAWo510jxpvo45HkSepqK/PqasRV0Ro4MZzO9wqjYVOz6Vek8ekUzGOWg5Ii8MlFOcF0LcklC190F13YucSw9prJa1Yq5gRpnatMa3HtSW" +
            "V2Nj4is4nE8wMluPrvoOWBcr/XWvRrn0Bq7DNpEvqs/pr++b79U0ihigemQDgGmSJlrjkqz3arL4/jbvXVETgbKqGnVT41bNitKrSrJQSWPSZLur21CmdmkW" +
            "yu8pFortLo7Hku39YOM2TZbT8xZ7Cz2UhdLqYjxWqF367EAvP3vCGk6a5dEva/herYZxL+5FnaxD9prG/rjyZUi9X6e8GO+zCopbr0p9ULPYs8jv+bmsdEPV" +
            "PknqvlIbxj0hawNR1jxqr3K+0Y78gc9WHmZMsB2NsA6lFtLJyi5NN9SopAHfqQBGzgsDU540qbK+ZxZxOF34w/QOdhEzf/QaPbTvJue73VVaOdyl2Q1+dcvz" +
            "AuvgPFlvmh/QaujD7ibq4RwbtdJmKmaGxBiUqN9SqdiP4jD2gHGyVtZORW7HtCLrQyVZQPUi7OkFkrVKDTVZKAmoKVJO1qRzMLNMHTb+ZV05qO/pIytbP7/x" +
            "nmv2XLOn2Wuj9ShH70Tq3XOolY33bATKXY3a1CzH4naPrGz+vPkDlOYurkrdPIdaAQpldVatYTmmbnDk1LZTG09u3HPtHn4k3JhHlSO7vkzU6xv3dPG4AR9M" +
            "VKmHK7GCgyMtkvU6xi5YRdbDlSjowdFTCoWhYKrUe9ftKP2uvaW1vHLHypbdW1rmjW+/8e2O1bGqVDl2ehvjOt9xfvn8lgPbmHIq1NPr+vMpkHVg440bW1c5" +
            "dpX6eCV2enuZIlYv37Plno03bPSuwixAl/+uZ/cdX351y6tbzr/jsVu9t/KKqkqV488/u/wqUOe3nL81vlU8IqtQd1Z6+dl955bPLe9+x+6bd9984Nb51AuV" +
            "1sC40Svbzm/bffNjb3vsbbAscMo9dnhOPPMTyyuHzm87f9v01uhWXhmFTlUPyzGQLyCz7cEd8nR0nh7+YyWa8YnlE4fOH7p46x755M+ZN77uq8gCaTvO75j2" +
            "RYCGOdT/pe2kDOOVfa9sfWXr/qvfed2tG94GhbKgmWWqXYq4fmLfha3nt/5k+0+u+8l6f4OzAbWwVdGoTimCN/TW1nNb79q6/zqW5q6bJ6tTkgXUwXNb92/f" +
            "f92tTFku1LGqvVXdWFk+f/P5m3ffegB7DJdVC1XqvfOobeeBAT3E8LBzdKOqh+f2Qdm2GzRq9/UHrufHblXq7iq1zNSB98QuRcqsUBcreRDcsLWlteXkxvSa" +
            "tImbiaU51mZLsxx43I3Azl99cuN97fvo8GtpjmV7V/PG0u/gL9/c2XoSbFtKq8r51LZqDTd3thHlriZrSyWzBjT11lPbTl7r2WF7dWpHldoG1vfaM4Lqza2h" +
            "X6W2OFvObDtz7ZkuPexszaMqdh6pbWduO3MtvsnGg8cqdW8lXwD85Ztbm1e2XNz44Ns9G3PQLTarVFmj4C/f19pyatvFHTFTVtX2PlfRDaQWNp+Ett9z1Z63" +
            "9EDSYr1K3TZX1sltMFu+PbrKa/bmUF+pzilhaxlquOXUbQ/iHNv05nzXVyp2HqlTy0D1H8SoQm1vzpzySiUPgpAFs+yDb4+oDTv1KnXnHMpZhjbciLKoFUtU" +
            "BKsvNYrsNJgGIZ9C4XrcnFiDds/tOfgGTadU/9dsOB3CrqHNOxS8MTqwQxjN+P5HpTY23qfJihxYuTbw7Gps7jIHuGpzypFdkfqwRsUBrq2phlg/ouJhmXpI" +
            "i8SL35UuJLyHgvot2T23E+ALpTKlfhf5WzpiXwPr68Eq1F839PjzMP84REFrDKwlaMG4xDD1m2tQPdtzh3OoLQ01bQZG3Zi0Jo3JOs5etctcAu3FF8plSp2J" +
            "oiiLJs5kA+/WQJq5ZAHllqmNjfeUZTmTdbksbPtmp90uUX1thg3D1KN2t8j7Dl8WWj3LaRfxqpnylDMHPBv28NGu2BkmZmLR8134tesVcaSRCpXTDTpFadNJ" +
            "DeW4A8rGVYBDcYAKar+y42jTCRaHCRQ5lCzYucFfR+fgGrWsUz4GnEvF2RXWECm/RL1pqimB8IQ4k+dQFsblxNsIGHI1iPTxdVTbf8VMdYmiKI9QXD/WqY0N" +
            "NY40n2Cl1B4T3EXZHNw1iMq9rMaQ9EEWUF1RP4wcylSsUy8q5zZLGNCpM+hM2lKjYKxYvTZG12+3dOoDimZgEKjESew8exVQGDrZabka9X3zNxS/Hh3AtpI2" +
            "3UyvkW6gzmMIhBL1fk13B6iHKmXNo76t2EMn9D3YxDqCAssBbY9dj2oW6v2l7nxdimgZtvmkEWUlMDtjYBdXo75k7lVsNWkUnWtyW6SgUV4Lo2zqsr5fqqHn" +
            "8ckrtiFLCh1dC5G6SbtTgVY+JCs/qfGoBLvR7jhBybLd1NitW2w+rc2p0J5H6SdLUx/PlMhDUWO7sdRebMl8typ1lyaroIQdtXuteTVUb0fMgszJWimex9NM" +
            "BBbb9rpOUKWW9fmLqNRiCtoQqX71u7SZyI/wTKkpsm0C1bM73U5QpdTzjZlPNbRShZpXw68rZ+YRDJVBL7mOLTZrB1reVrtJeWFVPVTPRbMwGSRbJ8BRNjua" +
            "lzFYSMtxY5X6njK+UFbSm3Qmm0AajeWe5c2VtalxhzanJL3Ewxry7IBWvtVstdtBeb2h5v6IQvKIXEc2AFsD5r2O3cIMyrHeX+qZeRalYXJwsl1kzrMTbEEX" +
            "2rBkozY11D1sHKU9mIvo3HBMFtGz6VKLX+6vD2ptCLK2TroTmynQeBuvH7gadcR4VeRfHrJHr8NngDTzQRnT+SusqOj2X2w4OfWI0MCh8JfBmgjsDVicZnGW" +
            "OqacmbHhC+q1+m7No0fn141BY0JnvTLXIeY3jAwnr+Fr9T3So+cXHr0BWgFLnizTbbdcEtfwydxbFi3i6WtG3rmkLTK/Sk6r4SXxQm02nA3phpIT4Zltg2fm" +
            "SZHVU5N1qf5w4ZnD00byHgK1Lq+hKaniuzbkt3RQWkwnm7gaTflMlM56q1THlDYqXcLYZxMo483jreO3G283rhJxcMR3uQol98spRj0bELcVqB3jggJ5iUb9" +
            "tC53N9FSSFSyNNkK3NXj68brRaQe0I0E+qutUNIeRr20TF2VU/RdBXWQNEp6N0RoXPShtPPTeVNm203pdSxTny559EL256m9bHJr4Jt9pmLqL+mb61Dw3h55" +
            "UnjG5D7jTJsoyyfqhfqy6kdxOk5Phmku9VcIjJvr4Uh62ZRgumnh0bNkpl70S/k59ZygMN5xTB69fCVlJ8X6Unim5Eh5RvHNkXeTUuQIH2AhqaZq72viHaL0" +
            "6IkaQv3IoyctN0lSdZ51ozdYGvAqauDsakFp5uMEegrDKwWKRl2q/4agQqYcKK1JW3ptmMNb6oGmh9Jih4OwJ6S1Bug9aMoRKalCo95q3p2vvXo9WEcx0x5A" +
            "fw3ycVyW9Vr9Q4pHD+8CLgp/Xt4WJG1q6G34W4pHDy8QehQgn6kkz8+rUy+J2BGFT69HoYJI65sil6uwbYVu/E39oWK/IfzRuGor/HncigXDLX+XsFFo5UPh" +
            "0SMrXycPVg3veI+NV0uW7Tcl5bFHj25FYLvXhb+shtQ5MSJ13SBZuUePrHxdegJxRtllPKxYtksiossMfY6LPb4TkXsd2c+GVLmGe3LfHPnYFsg311R9c2Pj" +
            "eIV6Qvfo8b0NMbLE7AVfdc6Y5S/pkfqY5psTstYVHr35NXxY8TkKWRvUGwds1crUe6S9Jhs/6U0WJ53xwrg1bpBPtMZ38j+hae/95m36Kgop5JCyirk8UGIR" +
            "HDGuMaXcsMdBbpPFyQKUDeNCB6G3PmHcrIyvW8x+MacMJksTXH1dN1lPO22URd/1qvFuTdZSvo6imWhfcjC5Otk4uQqKnWcvrp0z9pe+615tFQVrr0VYfV03" +
            "EXsw/qofl77rlvzOUhrhLJTgWrRgRLt/okJ9IJdE1FaQ9PZJvtubR33W+Hn908XpS7/lN5yGa7RlTmV8xRMaEd0kneWnS0iNlTMbvLXZcMf2GGSJzPVE+QNf" +
            "o34k3uwj0/TxJirGs8K2m5DHMTPwJg3IGqiUnceOYFl4s5QiYPEODOtINfQ0Ss2Egi+sncDptliaRW+7BukgG8ThVDkzQ+r3tNMoP3D6DlLAkSRgsnAazTTq" +
            "OSXbBbVi0Ooep/ol5O0FOSBppp3Ofdb4C/MzGuUA1aI2pBd8tWr9kPLyO4H4/hMMjrOID58tzqWOc3i05IFNxpjXKvWkRnl+x6XYYxa3PLRgz+v5FGG7oPYr" +
            "u+x0hO5vktWkHoM2DGHcRRQLWaf2KVTa76GsNucSV6mpJuuV+qfyVhehPfBhtiUzgvM7P473IVsSKX5TjL8hEqmuaA2iMqJEVB9BnaEXNIb4LQU6oWhs1GM1" +
            "fgHG8U9U6kciTjvJiUQQLKm/9CqGo674hj5S/pNcLXMd+kYwxt0NrVFSM6thNBO1fkx9R6dG4z5SbOmRwrVQmbLNrym6S/UMjK6Q9v9z9j5AbhznoecMBsRA" +
            "MQRAUfJqaIEA+Ccr2aGt4R9TWBJcLP9oacmS/EeyTdmSBS1XS8embFC2aUhacmGHYeSEoX1JfOaVeTznnn3Oy0XvVI7PxSrzyXM8Fk2TPNr1ksrLJXyqOUdR" +
            "dHcqFe5Z54eXQu3d96d7prtnsAuTXbbIBX77dX/99dff9HR/7XAGpQVraFDbnbNSVouL1QKqxBEAnlEZGpK4ho8pFkUNr7BtsN/gE3SoIszmHduh3MVNWYkX" +
            "xVcUHSI1LA01693ufEihIlmuxRT1F6Y+GEaS2A4/oNmh7GfFDulHw5JKfVrZmUY6bFmeVcD8nj0xvnS7kNQz8YqDalXLUqVoVNLPO2Q8pA3heyMz0/vrkEoJ" +
            "xiJvI5mqxiD16/FT2yKeK88UM0VxM724ib5j+dPNaQy8Y1mPKtpYak1Vc7WMmxG+Bq2pi54N/PzUsaVWLEvJ29MCb4gzSiluFfXXdGNa9fJILSp3muC5u2w1" +
            "W4pmBzxj2MbZoVInC7XiGirZmVqnWidqX3a/TDMD2Tv43iGUY+1jTZX6M9XPQx1P1q6J0cX7XwYHBk8OnzR976JzVKVq2dpx8PM8lnFMDnB2aC/4eg0XlTto" +
            "KJNG7UTtEkpjai1moIIadsz+motsFzcQlZtltl2pRfKjTbx/oSGloTaeVm3+KLj0qcYUeURX9BnUsFtfmNL76wllnEDoW0n0l1+BiWZJG1/qrXzoA2hOqYlZ" +
            "hU8J1mEu8vSxrN00tNiZ8qqnwdeQNujcev+2bnnBa1ZjhqkvqrN5y5s6jV4togabBqsXPHXO4xrqMUBjyqvlwR9ynELWUR+UdQ6p5xOUZ1L+QkWl3m297vxP" +
            "6qpjSZRFrdDP4m+929rlXJG6wOdW8/ui8GeqrD9XV8zw3QRlkZHfF9m14OeqrNej2WG0PF0SU99LtmtxpXa9HuXSoRq6A5du5lCZGmbDUO8nerf1xeyXtbdv" +
            "eAI4bhm3CX+mfgdl/XcqU+Kycg1/qLYrVe9p1B8rFGYA4JPZskVc8GywTpn9ZcgZQf3Jiu3in+nUxZXbtajHomiHN5SR3B9B8Gcx9VL2pNZfuiWmWSH38nG9" +
            "l0uY00yVA72stYqpU0lZBoUndXXqzcyv2SvpwdTIu62fZ5/WZCljhSzCHCW/ig51m8Ia/h3b0qIsSSb+LH1UpsvTewup96yaNXYs6DZlWpOkThrU8tYkdXhk" +
            "lA5TPY2kXtAlmf7GGJFp3kZ6Dc02MHO95jnMUTmK0u13o7Ux8yeyNS1ZelTw0QSfgbkMcd2Rbnti6iVBDVuyKCwR9KlG7aM8SzQ+WrJgHgBLlhI9mZZCyhPL" +
            "GSGR+nYqRXUUBamBRn2T8gQOalER41fYHee4FrkQZBbZjdZ+uncJx10sS5VHtSAGM0Pm6N6gjdbb6ZkIP4t12I8Y1gh+inmNFiypjYOZS1JWTc4moo41Wcia" +
            "HcybK9sl7zWLfbVslWyTVeNVsKXofhLUxn8vmUVVExYXoQnOp5sTtyFttBbodi2IFGr8dMOFbtGQaX4xrzt8jjMfZ/1FWacjL4i1k5xSP4h0uFWxrMOU0aVH" +
            "v51GeilRKMdAnHeYqR9F/iKdWrL1wr18HmvY4iL6uCQK6iEqcTZjpK5hf2l2ISkax6nUo85/o0VfikWiv49lKbfJIPWiTkVjN5qVIzKm6hGl9lcYyyxF9Y24" +
            "jdZx56u658W3G9q3hU6iW+hY89+kl4FYuNeo51borzczXzdmh8iOnUjz4g3RVCTrP2REtIzrKGSx6viAQtmooGjU5uimIa6bYMD2oG4OPd/YuFpxTMnRutG6" +
            "kvmeMgdpJaVlrBGkfrgipVshW9QPFDvUi6r72OMgdT7zl8k+Rm8vC4xFbJnUvOyvk9GsRYVqx5rh5NbCa0PLZK5rpH6gzXUqJQpRei+vJQ8gJcm5gTImCi9P" +
            "vYB5x6xh5A8XaE6JY+RBwtuQJ4KnzLhl6G3+cjnPJmyFNSitY6P1a85pLe6K5weUkZTD1PfpxhC8/TPALJ9RHwdcQ/KE3K5jVt7KR9S344hByGGK9Fdjiqzf" +
            "jmt4OJphZV+No/kF9r01UUyPLbw1tg0tRM4pC3RvoCRW0nw8w/75CnNKmua/mbmsRdbGvCz6S/eJOFe+FM8pi6p9KLJKQovRXMkzrGpRkoq9D/1O9IiC2m79" +
            "3xBLxbmPAsca8Uf9xnZrlb2dfha4YcqpzZhRP0dqmn+TG7rmeUNNkvL5dmviJincwRW40U/dVEr7fLvl27ibpWeFTp/XktxQuxeYPJHdl59SPj2k8A1dEP8c" +
            "9ysZpxXj38i5xbZbZ2zcP99zybLdnjtK8x3lzClSfkThjp5R1ECj/sqm85W4Sl5D0krlQktkT6LLhpCindUl4SVGUH1J2X1hUe+0ZK7FMKEHRffKN5DaTFqS" +
            "ZRSlfmO79b9bW5XfNEqa/jna4aS0aHe01QeRJLaoJyk3Ju4ZDMUuitT6ueJT+ny79Rjlq6QdiiXex5qUZn6K1Cf4d0U/T2rE/BRriJq3XPEUM9I24txfikWV" +
            "VqY44xVTj9lbpEWVlrcoLiFRv2/vIOsNaE4MRozlvmG9/0JvqMNorI60DeUb260fWHexD3LMc+8JLxV9Y7v1jzdlh1W7KX7TOD5Kyqrau8kHBWP5qED4KFfx" +
            "vfx2eJTvFd8gWU17hr2Qw+dR01sWylymol1PCeuFUuI8gKYmk59tt1600X9Q1OlwVId/13o48dl2q0MZ5AIxvngM6RpJfoZUR3wiI7A0Sv8Ma4i2QVlJS/j/" +
            "y3pRsnqm/KjFy1mUTn3Hfldk88v7+Q5SdkdQW9g2eJyMpCh3H0X2SL1FWSsoL56IikyJOOqHDhfpAYoZeieFkVmLC2c5VSgbYw1k8L99m6ln+Hm5FVGtXoKS" +
            "spjaYb1pbUDK0bNemBaF+zg4Zyy2ZweMlHctS+H+atp17nKGz5Co/8e6k7y/3D+VoMTYlzsD60T9n9bGmEr6ajv6xOE9Uky9ab1n2Vkv+oR2gPAJcmyX8Ieu" +
            "qH3qKKZPoGUBxRs7rLfZdKrFDuT4txO6ULXhsKy32S3WYbRHUaMcKSvQqKP2b9GcIm47TPyZtvj9I+/WwQyvTL1TUOkzQ2DJTLw92u3I1PM22kZ086O7jCwL" +
            "ZZWJOktxFNWwxLc4mrJo/nFkDT3RX+siXfVGRA1yjx/nsGXqHcqMMcJ6Rf7hmGI7FP2SKi3aTyj2FDL125HNj6Qijk/8o0VtUnp5xAiLdleGtqQa6pwyinJF" +
            "TGSzHe7imcjmeS1Is/pYDr1TZGpKmStTKbEziLMk94VFrWPb4F62Un2ALfcT+MKiXrTfQRHsaIr3psq8wQ1hhxN0nFXc8ZdLkSX263Af+8IOf3tZO7TYekUW" +
            "1YawQ5c8gBx5aX5ejle5j5ap5q9Ayf56hW2edyen9rH8TLVDRz6nuKNmL96vF0Q6Qeqv+bnSZm84yh8KeeDl60Ib2yJ/mCYt8oY5Kxf7qH8WY7k3YpyIUU4z" +
            "Cspi6lY7Gl+p4ySwpQ+1FI/9oP2QFefiDskjm0+G8Y5d6aN+z56l2vc5Ytd6TP40oOgbKY57d1iftN8XjWUz0w7FhPwESDtpcHT5gvqAoOgbJkWxE3lljepw" +
            "RFSET4qjn6GCqL8k9a5lKWXOI9tl6mN2LR4p1qjZgXYH27Gf/xjPKaWRlKwhzeWyXefploHoGbamstB6fh5HWxMeyhPUx0ZSfVs+x1OchZmVRX+ds+/nGtYw" +
            "lrJ0WY64v0LqAhhPUB8SVC9BcTSOfS330Q3IR62Fp5t7tGeXQOSG1oulzXFI7dGpVNrS5ri11mUxlnvLFiuKmljWZY6IlqmdLoupf7QeUp6u1BpGt07Y5urB" +
            "Wus2+yNs3c5o0lzFWgsR0cNEpehDe2oU8VREPRY9ay5Pxc+Ca60pXoHh0er2Is/HJ4+48BmbtpghkDpET4i877UXcfHpA86OHj/TM/VZe6scKW5UHCvaYY6U" +
            "fD4PLc5LhFRTnSv1mgpp0VN9RP3A2hL3oLNcL+t2uCvFDk3etMPvWg1VVqTB5S3qu9a0IauXQiuUw3b4qPaUH0aRT1zS7HCWLWpUiS0qirTQoh6VlDO+Rb3N" +
            "fkJZ80hn0+zwntgOHaV3I4sKhR3G145HdlhUraOX4KMVH2EbD0Y2r3w3/n5ku1JWW9hhi+0wtkFXsXorzm2l2vxIz6b0sr7ew9QDIyhz3Uil/syaGU3ZwQhZ" +
            "f2bdv7ysKErSfe/dUaS/3OjSx9dla3tCVjoln46R+it68tV6SvSXFRchS/wcqL+1JmkuHXHfD53v0WZoUcOdadoY0SrZro3se52UothgvM4VOlzDh3TKvMeI" +
            "aqivEq+FZ6KPrGQbtryxJ4xmh0/QmnnP8Ng9bQaTszt4eptt/hP2fXINVnJu2vwl4g+7TzXcxX4+OZ/Y6vwQyRLji5/atPnE6DHd0/NY/hpFlXI1KkjUbjp+" +
            "W2HLVSykHpNUSfWB8mSajJ6jNwjUrq9wBMunKdyUGIDq1+YVaVu26yt0E0pKDZ34eTx+W8HPlS1rLZ0O7ruDUhPTaMSXp+JObodto6etBzD1CaKGNaY8eV0r" +
            "UIGrRRyRzbesezK8BotZNprMyMu/sSe0kSLtq2Xtp/N6A2dYq1aqlUIzH13hTXvG1d6y5RN9y1qg8w5Yw6mpqebUgievTsWdsMKTmvMXUgu0S2dYOzY1dWxq" +
            "ocGXuk7hniPZLiFJjCSknqWVwL67xHeNsix8CdriGoaihqEcg1TDb8oathamphaaKIuvn62pkvQVNdT8B2jWo/5qVhr5hrhEGtcM6Lmfn/2t6I5pph7jXq4R" +
            "1SxIHUKr8LRTOnVP5j56PsP+ajTxELjorRLKqkORWRWl2TDFseig1qCs2F4sywnpyUmlmoJ6y95H8f6wVME7HT1xlXwxPiXRFlkn5a03SL0/81HaobsEPZat" +
            "8F2Q8kxGvD6hx2wt6zepXWS9VUu1+KI8naZbVCDa9aCgulP5pnJ1fZXPZMSUHKVsUc8JaglPE0grxPMwroynkha1QLeu9EtLSC1WVKqkU+JWHmGHXdI8Shou" +
            "FvjimSnchRHIfJ+arEBQPWG9KKvAN7Qes3D/G52hSa/h2sz9ope7VYu14elnRnSL8oQdUqRHe8XKTUWDJT671BErqFZ0+zP3F0UpKGsqH/dXkT2UjNKYWohk" +
            "/SbZBsrqTnmyv/AsV0me8OlG7xBQvQ1Rw/1ifA1rlalsU/GGrvEspfnDA6KXwaaQklddJ6g4dkabf4D8Idh8MVuJLqyOTo3Elm4pNn975iEeKbUm2ry8Irtm" +
            "leIT4NIW5d9a1gTpcOBWqpgNOrJ6D72oPsOqHnuCdIgnmjSqSaeyRlIe2QaO5UJF4So0N1h6/eLx5WXeTzUEqqlSwvOOoCboTDHe3lmYKjQjzU/RXg8nzour" +
            "j5QJukmZ2jWFHDNE1fSRolJPULaEgbNUylWxZKf40mE6yWHjc8N04p0vUnizxhCpGlBTuSk6xFqTVKhQg4iaoNPcfbdRLXiFRjburwKe5fQVL2opXnSCZge0" +
            "XV2HWMf+SEr0F9g891ehIfxvYnaw4MdyfHmZD5OPol6W0sh+Q9wVpVFqDR/nXlb7a4rq53YMYiGi9lNeEdFfOHvRBeg96C289yqmmjRdLwi/sYM0D5EDax50" +
            "T2cea7Sr1+JGWlBh/GU49VYEtSDmFKBa1F94orDWL+FOu65Yua4QI6nfsjpZPQufV9iQm8/NZ+ZXb7jz/ERvfnq+PVHfUPbU7yCl301QKXweKWd+9VuSAs43" +
            "qKeNrKTN7EQWJIGsiXedn0dqer4+V2+a1L40yplfM7Hz/HwgKL9p1vB9RrtkDT9/9/mDPeCghu/2E+162GjXAlOTMDPFVEOnXsruNWq4gWu4en4ntgvrWN9g" +
            "tuslI59eM3uMqdo8tYuoI2a79mTVDI0Nb66wNzuTm818b+f5Ddfmp9fV1/nrypvzBZN6SKMOFeaz2Ms/2PnywRvz07M+UN7mfEWnHtDz6eUP5WeghrOZ2ckf" +
            "PH0SqPYsyaqa/aVng+x6DxRmsjOZ+R3zR8/PQatm67P+Zs+Q9ZvaLR5efgPocCI34Wxond9wY0Mw194AGkzY4W9qWYwqXrMwge1yJlrnf2tIvVWf95ueQd2i" +
            "WS8wQK2Ddq27AyxqLpiZhgIWpdkUUo9q1DagZkHz863/4ej5gwHZPFALOtUxbuTxCs3ikdwR98gdSzvPL/7dZ8LPtD/TaSbt8CnDDo8xtWOpFVELSTt8RLeo" +
            "wlR2W+6Is815eef5owHpQ68fU/tNKgeynCNADUdSP1QyXPn+Ie9a/iJa4o751vm5G/PBbH29v768xdtWKOhUnIWv4Q+9a4WLhUu5eaQODucvz7YP+Fv8LV5L" +
            "p37sqL3cBWl78zMFsPodE0fPYP1m/dnyOpBV1anHDeqSN1OYR2mLS9yqWS9BdbJ6BtRKflsBe3nCPrP6TJMtqjxhWtTZrJ7Z1ctvpBE255yfPL0zmAPbmPM3" +
            "lo3+ekrLcgmaz0+Ab5vITDhNu9dECq2w3DSpZoICWZJCrzGfpI5pNzXgWJnIz2ZnszDCJs/A+LJm6jPlDV7DpPSx3ARqHqU5W3ee3yjG15xnyPpmdtKo4TxS" +
            "mXlrzu5NWFzD+XKCmjKpwjzPD3aPZoc06iWjhhVva+FI9khuW+b85BnU/Bxofs5LjJQHjXZtI+oIUOe5v1La9bKpeW+iwJrfZvcOoSzor0PlBLUrIYvaxdQh" +
            "aNehJPX2rJpvuVHfW76U353dnV3nnDkU7LV213eXD+TXZStmf+mzeaN+0btYuAjSoL82BPP19fX13nqTeljTfNffW57Jk8fONCfZX5dhpBSqJqXODgv+Xk9Q" +
            "ztRO4eWRmjLbdZ9Wv2v+Ne8S6sM5s0C6mC/Pw5BLzETqHTQNGMufycNIBi91phUcaR/xv1i+Rx/JSOl5HRf8eU/YoTM1SbZ7AHS4JTtltutBTRvXvOswf+Gs" +
            "cn5SjK5Zb7Pert+2Xnfel7YHxRZnCe3kKiJTH0nd0SgoJ20XJFLTqbJY2ihZ9eyE9m49LkuLyfXlmGqm7DPoOXhKX11rNrVx11iyeoY2No+khq3R1NaR1KiW" +
            "mTo0KZUbp7+SPaZS31HyLCXq54yu4ZbRNRypjcZN9fLd2eKIfYrmGVO9hr9l7NcYIc2QdTM1XKMN03F6i2t4p/oD5X0IZq6xEu9HJLXa2G8YF9C88i+V+oHz" +
            "DkXSuDb/9pvUxs6R2hiO0Ma7rG+VGiP6OO00saT+9LZ3LkOl28a7rIeLNyPr0782swwzijr3tpuhXr4pWX9624b0vcOLeh4MU/PvSf2E8wWN1sbutBpGOR3S" +
            "qVW33ozmf+bOLqOLdO5d1un8I8vqMJ1q33Iz1F/eenMWtT2ttziLwYgeQ+vdOqK/RK6mEdQ9y9WwNcp69y7TqmHrV7KNKLPFKFkjqNbodt1t7bKrvIczQ4X8" +
            "pczYy/tfsNRF8WnfF1J3MYWc2KsdveeMuLZgeC3sbmuvvTamMkRG3iyW1VakMXWnSokdg3od21SkNK7hb8XtcpRWpdRQpTZKWU6iXZo0vV0TRg17iRpORzWM" +
            "27VR06G69yWw1Xa1lRr+zK7zkqdn5a0slZyoKc1agfh+GQp/CSkvQ/tgsfvydNy1yLuPZBZIzn7q0xc8cR72bmtNZkJSnvixaGHcY+2IykfUO2OqElFOkmoo" +
            "1Fu806lCdSvyfnN1X0oo6tiJXuswNcNUlfPNxTto4vfL/YhqCOqv6axZJIulaXu/pKyOqCXL2qfKKnJepvhdvXxX1IlewyG1lvPBNkUKsZrIMBHtJZD7qTnX" +
            "XZcy+CI1z5RYl7dExkR1vzet5xO1IKh76E2WkCRlqfsWOIdUJKspqCelrKrIj6DX0AlF/QYi9x9SC5kp3lrgR2/ZilL7cT5d35KvgouCuo+prnzLplK8Ezv+" +
            "UFLfzOxgKn7LluP8cXGeZZl7riA2vd5tnc805fYH/nW8d1zu+4h2l8saMvU1eYZO2KA6LtWRLFvXENQOSRX1XVzSA0iGWyeprUzluD3S4mMPEEvi8cLUZFxD" +
            "jYrPKrSF7ao1pLHMoz8rJGYkp3vrsngLgdRm3nXPozjHe7xjm5f+iesnqRf5XADLkT7bjj1UoDDlyEe9yHv1JeUoHljsqQwVXUhZF+z3yLFckJocVcOGQu3V" +
            "vI3ceSNHCb6ji8d/I2qXL7URzxCKX5MnFvQa/oxPBnm6bxOWpZDxiEFqgr1oXXGuQjPcMl3r0mPvz7yHX1RJaTmRW9SJCU9x5jminsjcac4O2AORzfP804gm" +
            "HUm9S6W4ZeIElrTCJLU/s5WpuOIF2Wfcrk7UV3I03239AeeRVodrnLtP9FaSeiKziWV5olVUwzgGUK0i1sbvSm/ji/0e7G/i/Y7ifIl49Umy7rJuZTtEDSg9" +
            "ahb8BJ7cHPheFqkH2Tbgt8e7L9vR3NOhvzNFKz54xgKod7CsLNTJ7kU7SkyK5ZG0LFPbJaWck+koJaLsmHrQfq+IUuJZrm+UMNppyEq8y3rGfkSc1pFnpZSz" +
            "Q3KvbrQXFFRYQOo52nGHXRHNB9EcLuXwCTWiXKY6lB+AqSCi+mJWjaTxTkwXZOWR+ob9bukPnXg3T0exCCwYRflg/SGtByA1KTxAfNpI9bRMNsTI5D1Wd1lf" +
            "t+9mWTl5A0FbiS3UghTvDrjLeolP+pc5069oV9RXLJHl4L9D7GmgHLm/V5l70qwwoJUFC7VIVEOl7JGUrVK/oNNwqqQ4YpUl0KUBVeTdzjlL3eUc1TOIrD2g" +
            "/aw0VoC6i/alsO0GmlVodigssSdqeBftWrQiP7Es5dC5HaA28xkftxcx8oQYF53De2l4pDwgKWmFVhx/9qUeI/tl6hk+DVdgfbAu9CcMWXB2nha28Yycl3Px" +
            "ybBQixXiwr2A1BNyhpWyhL+pJxiUJe3wuUhW3GOh0sPS48Q9jtQr1rvYNhw9AkpaR492LrNtODy+XMUSNVuUtiEIV9rhOyM77Fnp0oJYVmSHjdgOneVlqXa4" +
            "Nfa9VjDSi7IsqF+WqWZESRmmRSnzg6DW8tnbrJWR7UqOr+moXTBjC+qdTDlx5GRSYUw5soZTypwitZAYJ3IkR+2aUeaUYOT4isakqOFW2S5nRW/jxO2KdOjE" +
            "50oTnLQOIUuMr8jPq7aRHF18+wX6+Xh8ybnBN+aG2GNLP/+HvCqSJ91rc3kcqfGM4otxwpQvZgdLmx3iWUXKYUrORA3RrsjfWKHBydlBpfYq85ecX/viuaQr" +
            "5jBf2rHNVCeSpT7f6TGH9ALYa6zDP7T3RLMez5V9RVI32rjXkfMXfPkdYFGbadLrxHOGHOnKqSkrOjleFtQ2jeqZjBZvS+od9KxXZv3YvaQk9YwWfEtSkxqV" +
            "KsuVvjKkdZt3iDgK+0PMeDQDGCXaT+8LiuMo7A+Z4SCQ343OJcS79zuC+oj9INWQRgNJEec61eLGeeW5XR1DVk+VpJyC0KnvUmwj+ilHca3yrBivrPCcwc96" +
            "77DOcyzKkTA/A8R7TaOnJI64UZZH1Nd5pNhCUkEr6vM6WacvZH2HvA34OllDLMXoqTunrhi1wR6Z4jgqHG2FhiWGgmoo1AoWFVE8f7WVSIn8kKM/YaqrZ0gV" +
            "6SmgbSkzlENPCAnd98STMI+UvXTiQvrkHkfSWlFW0CgbCVIPirNdwjakVbn6ibAgGulIcRzVkZFS/EY5cQ5HxrY8Uh5kKh4n5lhxesoZko4YKRs4e0yGbJBL" +
            "IVohjddYab6pR+NrS0ypNhWvrsQnhURWgXdARER+XjKqLKn56LYxlbpbpXKa5eqUIusVyqYVxUpsB4mzYPGTejvFDq10D+Wa1vsLOmsWxRRybT62whF2uF1a" +
            "b1Q302P3tJaxRW0kH+Xz3DTSz/P9UH7k57cmZ4eRrZI+iuOoaH53lP6OS/QeY5ryHiD120QFMeUajBuvogeCkjORL3WYGGE9xTbids2IdinjMjE7yDEZt2ur" +
            "aFeworcJlHY1iQpjz5FiG71o/qqLkfJO6UUzwt7VkpMrYhxjyXZ9g8YX1S3NYxdjq+eZkv38H1LM1pMrJTlFWuTX1Fk5Lyhf9/P6+FJnZRpdedGuHZKS84Ja" +
            "Il0EWrue4bw9HGeaVCl+D6FTHEdFskxOWc1lbZRFu3bH2uBWpJwH53slOmL++rj1L3SKNhRnvVYsDuZnQ+oheX55HMplymVv44Z221q5QD0FdTM1rNI8FkZn" +
            "h1Yoooa77Dk6TQDFGqPA95Bq3hT1ojgrDRGTreYSkC3pKE8EvsUZyT5u/Yx7ucZUkPJUpD7dS+pHvEZUs1zz3WHMxE8TeA4Uqb+nyLxXE2fEbDVfkfpUz7K6" +
            "gnLsm7GNf7Ee/JWogKiiLbMLjmVRkaxH4tP3Y9SPqX91U+36Vzdlh9/gc75kG2omq9B4g81vb2Uvf8XeJc/eKj2l9q/6bMqZTz5uvUQ6lBalv0fVOZQmZV3l" +
            "dQB8B2ivbFF4YxFSr9BbC3wvOVZ/0Rnwj1t/d1NU8ab6y6PnrzEZ4tjbbCVqnPqxJbKsm/Fs3k21q3qT7RL5UuwxC1Hn6MkXLMrqWfGZOX0lwFfWwtk2zknP" +
            "ZquE6gvj92xEkbf5Ca0SI6VbYDvFQ5H9CqoZUepIaRt2Kzmmvsb+sKSuAgZW8r2KXLmQ7XpEeFHdg5prYJJhWS9SpNcr9RyTMTXCb6dZ1ou8tlzST87H0jpG" +
            "HZF6XJnNrTEKW9Tj0WzecxMxckrpRdQDIl/KOLICQVXtm6nhLntezMuB21uhUCYvopqUB0Zkllqx9IWsOAYYj2JZL3Kkhzm9cnFsnP48Hwjf+zjEAHvY99ai" +
            "99lq7BpH88TjyUykohigqsTVWsyrSgwjaq+k4n0wUcRr7nyQlHNT/VWlWY/+NYZFcVacx5UYYDyKZakxwDj1kzUUeaTHsF+ZFfhxJXL4VbQRRw7jWJSkvsGe" +
            "DS1K2b+hPJlrb9OkRX2FfS9TRfXpKWIUP8SRw+MQOXAMQJRqh/qzl/DMA3sgqH3sD2uKFSp2qGdDkdQrlIFHyTu4bOEY4HHrF1HkYI1RJCVjAGvMIm2DY4Bx" +
            "rJAtkWXdjD/0bnJ83Vy7HhN+vjemJSJ1jldua9Fa7UiPw3t+2G+E9EwU1BSrMNd5VK9oDy32orRyK60wXl/Tqcga2aJ+wtl+ato6Q1Fbs1FWpHqihiJPYE3s" +
            "nSvqebU03+uw1TPFMUDEaSvsRkYvR3rRF+17uV3FtHVXY1xaoRjLMgbQa9UzqSiLD1LPW0+RLM5Lof/2kFZz9Z9alDPyefAbD4hnWD0PEedVDC0jVxNlf3ze" +
            "6ogbDcwccJwPw8iVxfs+iNrOlGtSvN5sUK6sIeXHLlHuES3nFPq+KC+IzB4lavgizea9UpyxRO4WoSz6eszrYPZPpL5Dd0lYIpt6XEK7S7KMnF5Chz+iXHAi" +
            "t49GYSyYoGpcQ7FbrEo7FZUoIaQ3Zm0tmvVF/g2kxGxe0/YCCqpj6bt9+oL6Efv5qnzbLvUeUgyZjH8x48Tz1ltsUVUtUxV9Fze5doyYvkPvAp63nuEaivwo" +
            "+qpDx7QnW+rwKd4RVIzyiIiCGSD6en4ztt4sUs+xDgtk8ytalIX+haimyJs6lh2KGn7DfkqOFK2f6Z5nu69nvXTlSPmG/clRFHI6VZLUc7yrqijexsUjnu5E" +
            "17Io0ls7ruHXZd5Ug+IahkmKZJ2X+7GrqpflHcRtKzSexUI6j4jUHoVSfI09iHfAqCuBRIl4Q2TqUXbA2miHbePprS9kXZVULV7zl/3VNdbN4hp+hWZY1KGW" +
            "35v82sDMmBf18s947zePL8UOeE90aHooytT/vPV91mHVqpl5/pBK5P7DOYSoaR5fBhUQlcg4KKhP2us5QyPtgYlLEO/1iAu+zygxtYVtQ5cU78Uy60jUR3l3" +
            "RNFsAT/LJnMisg4/J/OmJnLbSVmaD3BY1tcSz8ux3+inUD1BPSgy/pnzVz/ZyzbaPVPvTa9hOhXV8P3as7nqpZIWFYganmPN1+QO53hNpaONr3hPEVKhXJ1z" +
            "zLXlTuTl1TUEHl8vUv556AWxQyreI6Xv3ZKrxCyL4yiwQ213eWhQpqyQdwTVlP0WopdJh5a+QoLWwtQH1fkr0mSoUdGaivAbP+HZoWpmTeQ721NmPaJe4z2B" +
            "NSVzryOffxYiL9WP9+0BdRRmohl+b554+0oxk/mcQpkMj4K3eT/l8kr1LIYHkjmrj4o4qiP3MqlzVZHzwWlrs/QGHKm96nvzeC516ByI0zNGQkfU8Gm+HcWM" +
            "mBxabzYyevKdJkfBoj4h7lTR1wkjSvspP40ehTjqY6yN6L6O6ER1yYiSaMWhQdSP7ANRDbXvuHQ7imvEVlENt4kz7YnVl1I0EqLRwxm8j8oVGJe+oz/TcHTv" +
            "qFbdF+36e17tceiNnvp2OMczoTqjoZ01BEXP5lKWSlXFqFP6rC9sg+Mon3di6J7PSXouzsR7VDwFdJKUy7ZhWkaZqOdoDdZPj5nMOUNkrUdqimXZhv1Gdqja" +
            "bl/Y4TO0KoJU38wVnIiS+o6kOI7i1VU9dsc8/KEW5Q+i8fUcrfZ0+A4SY1TqcQHvI2dZX1dk9VVJFDWFbiyrr8j6hm6HGeVkixuNymi1mfPPIyXuhXESq4fc" +
            "X466ctwVFsVxlIhNi5otVk07jKmrknK1J2S2+VLPeAqRI2WUZ+uVrFIvxbPJ8XWv8BuBGfvUyCMn4h2kOI7qpsdMKbFVV1BTggpWpIKI+iTtS+lHe4UjxlZj" +
            "zHge7Qtqk6RMWa6VyPIsKY6jOgmGd2Qk86izHXIclRb7MGVGVyyL33F0UyMtM0ri50amHkqfvyhmGjV/fY6eU/rKvUzRWHZF9kktD3BfeOyPiBom5hTadWi8" +
            "kRKyzpEOe3Jvjroy5KoneOVOaKZCXk11Entl+E4lx4yFF0QNN42SlVMoS8pim/+JuE3G2Dck94242uyg1PBeykKSuoJX0ld7MVpZEBTnThc7PjWGYyv1rdNA" +
            "UD/hGBtlFY01qaJ8zo97WdbwNYrZAkffj9KTsko9I88zyroTdPg/WuLO8fRimwWpHZm/xrPsdI69P7q4cUHqLRvvh8X8p/24uKMK39mD1MuCisvyNFIb6VbZ" +
            "+C76YUmWfin9JnGm/lanViRH1TCq5ch27ctclzosmUW/51kW7q//Or2/7GSR+Zaxv/6XqL+W3GQ5ZpkFqf2Zf2OJ+811WY5+B7VJ/c+p1FLKLeox9UVbvSF6" +
            "VB+phamXVqJS+uuQ/adplDO6IPVj+9sim0U4qrh6Ydv4T6m2MbqwrBuWchP1GAWphcwbIi+Fej/8StRb9glLuSFalsTtAZxgekH4jbfs76ZRVIaOXmLqx/ZJ" +
            "lRJ6Gsii2G5XvKdHai3fKy1uUCTPMqLvSJ41JOpfrOOWet/pyoVt42Ys6kX7uDEqg7SiZQtb2fcmfwNSX0vzAMqITPYcU38+WpaTxpt+PtXbp/reHZl/MqiV" +
            "WLao88pI6aeXhBfdn/k7VZbuQVNbyZp/PtaGParEyaylF/0h2kaiDaN9KWv+G+maV3QurT22+XiuHLqJYqulGWVBxxr+ran5Fbw9j8qvSW/jGiVlRMaj8luq" +
            "NpbxpDgieVSus3KrPqxlvJk6BqU6VcFSjIqeFWed9fVVHzcy5Rw7tsBlCkqjQsWkfmPVfbqsheZCU3xXFi+F+pBBYTFJk7qU1XM6yaoRyX9SqUNGq44dk2ST" +
            "C3Am9Z5VDxjaAB3iH7pLoYJ/PD1rKlMfTugwwSU0n1u1f0S7ov4qFAtJ6vHRVJUK/DGp7qo9Rg0XhO64boVCIZV6IEFFWBX+FNNkve58LtHHUDqyVKOiUk9l" +
            "f8fUe1UpFVlMWd2krO5Ut5ooKnVWy4EsrJcb1sCSZonrrJ9njyUtSv3TxWJq4+fZ48tT9Mek/kv2s8mxfCzSJZbmVNOkHln1tJkT69gQCo3m6I9JPZX9qJ7p" +
            "ekEWqQiwRKOfsV1t3TIiMyQjLGEplkxZe7Kf1K1JaB5aM1WcYvvNVXM5nTqb1X1UF/4sdKPRTzZcNXwb2oaq1urC6KJTJ9OsN6WMloUWOKro2vhqso9Tit6u" +
            "Lxr9xaOrqhUPik69pOWeJWnNolEKzYJneuwHE7KaXdPTJ+eUh01/SGODzarCxTep9bn3j/S9op+bZlY39FEPJSkYHVUqeNtOxaskPPaNBCVmvdgJeElZuVX7" +
            "kppXfHWuiGUlPy96tcoDBf11GvUbq6b1fMusdbD1AhSUmCvkCknKyMYPNWRNVGmEMJfsr48ktHEsdsDFYqqfP6tlrYfR3G1SqbDWm1xPXRp6tsfNmZL/VPlP" +
            "mqwJ64dOHDn0Lc/h0m90GtcK1249cWuulHOhA2r9jk7FPqpve04FClBQyWuF67deLwFVKgI16KqUngMZOSrw9NLtXsuzrBxSz+rUg6lUH6nCKOo3tbEc2g1g" +
            "Gs45e9AcrrlROlUqQqsqWv2Y+oQm6xwwGlUqVistk7pFm2H7doMpp78wWHgFtBFxR3VKjaP6zrlUqmpQP3QeVvILdwo+lmx4uN+54V275eTbUBug+VK/oevw" +
            "Y6o23E7OL/q5jhs+O2jcuOXULaeQKp0sdZt6DdVRGbj1rA+lngvtfheoW0Hvbi5XKA0M6v0a1c5hDf1c6DB1gqhiaahRbedJxQ7PgD0V4H9naD142D6xKXdP" +
            "rpWr5KbOtgZtVRtzCjWQdgi2MfRvbLq2pVgrVgu1Yat/WJX1HUUbA/s0SCo4p0FSxxr41zaduCNXyxWLteEO3eZ/rOlwYA+JO0N34t3wT23K3YF3sbzSGh7W" +
            "++uLWg2HUMMmPD1BDRvDyVd2QA3xeanVf1aneoo94QpB0z2PlE1U61QrjfqxFh9iDc9QwTdJS93rO5Cq1pZag+d12+hq1jtwzrsVKe3ZV3acbVVrUyBLpzY7" +
            "s1EPByW/ggUsA9+ePnnywPEtuS2gjUKuOrxj4KvU0xEVljpE+YI6c+DUltwOoCrQtjsGjZg6oNzv0HPCUpusnqRBfc+sLaAsmCaGd3R8lWorViipNtoh6ObM" +
            "504SZcr6IyWDN+qikSlQ5oyuBZax6QQy1cqUaVF/5DyljOSB23RxYytmVby26RRSEIE1Wyb1aWe/QjWcCqUw8Oi2rzf9S8ihJVZVS5yw/kCtodtwPZEvCsaJ" +
            "df1JYO7JVYuVZmvwOVXWA1n1OQU8vZ0HH3ra7jQPgd848Tby18W+4dkeyOq+9zSMrtNEveG9IamS6nmRulu7P2Vg5e0CFBhhC13vknfiFvBRxVxtOKlTD2c/" +
            "kOLnz9l9oK6vRlmki2fNGj6steu0fcbmGg69N1afQi9qzA1IHTPu8elb55zT4HthpDSG3rU7irVCqZKgHs4+ZswOyGANB43rq0/VwF8XK1V1nDD1pEGdS1DV" +
            "2nDRbNdebf7yc+Wcn/Mz7e6wfI20UczpXp4p1feGjp+j4obd4aZrq0+9rcj91ViuvwSV8cFfD/xLq6m/crnSsGK26306lcHykoP9BRTOyq7u5ZP9Be1ysX4o" +
            "C/Qu+uuUNg9xf+kRUejUXaDc0Bk0BpPQXzBXnqkNmmYNH9FriC2D/w9Z8zi/5k6VhjuX62VJ9Z3+0ai/SmcNatr6phKl9NyO59Wv7du7d27vtr3bZjZPbJ54" +
            "bc3FfNbyCqGjUx9XfBRRBy8iNbNtHgpQlyoFq1Hoa9RLShwFvldQEyhpdvO6desvVfC4u2dQ1xx1fAWlNnFzc/PwZ2J+AmThEXmgXJU668Q67JVCYF747BsP" +
            "vPHA3ENzM3MTc+96dU3BylsdL9Co/9V5QpHUr3j+5bsPzR2an5s/MnFkYv4epLxyx9DG95QZNiihNl749MW5+Zl5/iNq2K2Erk7N61T7zKffOITAESjzd762" +
            "Bjfwd6s6dc15f7o2ZpbTxmnnPrW/oF2F+sWDE3vXcZl/rYUHDBoFXdYF51GF6tc8/+Sd1+4E7UEPT0zMtV5dfRI8eL/U0yzqqnaXBM6Wef/kgYv75qGnJ6CW" +
            "r7WOrybbKKnU605bo/pVkLb+4kFqFZTXWpdAH42i3q7XlTiK69honDyI+vj83JG5z8+/2rq2umINSnov/3r2oEF5jZP3vHH3kc8cwX5uvbETGVUXSOWyT2ux" +
            "KMnacunga/Nk81RD5PR25bJf1NqFt7Be++KbCvVmCnXaeUAZlWHFq+ehv+ZB9TAm129e//MqjpRKrm+MyrZi8/1Ww3+BdLEV+mvbul+2Xl2zF2xj4AZGfz2q" +
            "WVTfA80fuEaa3zy7bfNrtUsF0Lwh66qu+VK/6XVQ8/PYqtltW35Zew0p1+yvB7R7Rvolr4GWODuzbm7d3Oy2izWw3Xy/FDg69bFEfxXqr352/jPrDq07ND/1" +
            "mqQ0Wf+bJgueHcAOLz516eC6vZuhrJu7VLt4a97yDZt/3XlMt160w6cvfnb+gYkHJg7NH7pYO3mrZ3WMUflNZ0rpL7+cr2ens/tmZtbt3rwOSuY/FedzWSuf" +
            "1/3GBbWXSx0fenm6sO/izNwMDOSJidtfLb2aw5aFhj/coWm+jtIOHp+Z371udt26dZnXijiWQR+G31Dn5aDmlz2k9kEvM1XKpVDfVHwvtKuRrxfaxw/OwCgG" +
            "O1y3+fZfluaxhoY/fF2714x8m59HLzWP7dq25a075lz0AGa7Pmx6tnbhIHoAsKk7t23BGiY92zXtNrSg1mnE1JE7j9zzy1oa9U1nu9oun/tr7951u9dluL/m" +
            "UvvroRH9hS3bsO3V4qtF6i+jhtuN/gLN7zs+MzMzsVvtL5OaMfsL2gX9NTMxO7FuYl16L3/BmVXGid/woL+ypI2Jg6DDO39Z+2UN54eGF7cMtTGrtqvRqOfb" +
            "Jz97DWc98Iefv+et2iHo5YYxf31dfU7BPu4UgCK9H9y2ZdvtR0qY2rZhaP7rit8AqqFSR+45kkqtt/4i83n8vsdlutAu4P0ibWgcR4r49k2uAXkQszP1HzK8" +
            "vhGUsYRemB8uDpAq1KngvcxliE/LrgdFyjpPdwcD5RPnTXsoq84rFrRqsdTyc14GipOHJ2mm/mOGtRF4ba+dxzJc7BQ6BVyvwBgY6peD4uKa00knbhcuGvew" +
            "VRUohYDbVWzn2rkOFGyXT+sr2C7PltQfsjYqXMIC3o4TFttFYNwO3RzNaznIVRzZriOyXUKPQodQaD1GSPNZWtSuLzC1OgD9hXnQfItqiLKE5stuGZ+voMh2" +
            "/YcMW1RvE+u+nR8sDlqsddQ43uSBMk7aWGLqM5LahP2FOsQa1kmLOnUtov5j5mFBYQnK0x70MfYA9RVqvgy9BT2lyfr/MtJvANOQ+iDLwNWiSFYBpB2PqM3R" +
            "XIl2MU26Q9sQDOo9I+0wtt6284XY5kEOWyKUVjvbJmrY8l3QoqtSu6Jnc64d2xTWUVo8ahItSqd+x5AkqU5xNLXZOSEptCcoPMKGi8xIezoX9TG36/fjdk2G" +
            "xDEFtig4H570sag1fC6qYYhFUJ0ilpjxNG0cUNaIgjXAFELhBYAS+mi45xQr5BrG66I9tnrWRraeq8dew65ElmH2V6/BeuT+quekPpDTqQOKxwZLvI08VR79" +
            "TT1bJ+vwM2WyjhcU6rhzf0ytpjrmgwJQYBkkreY7NLpslfqhEhH1iBCeA2VF47is9DFTbdUOCzHVzsWj3zeoH2ur36KGmhahXZrFM6W+2RceR5OGT+om9Wbm" +
            "oPp90F87j3cFYbuAWSR/TZ43JpH6okaB5yjgziwcnUgtgRU3XH1crre+Sz4q8EO/7bcb4LW9pcU6TOl+vpwvF8rZpUUviz67kqMXK45RQ5wdyNfT749mB5RV" +
            "Rk9PviOu4XN6DSuyhh1Rw0ZKDd/M/AFTjUCMk7CC3xUjBalopFQU6hnFz3O79BoOeS4yargoZTVQVrtiUlhHnCcrmiwRLT8Jpd7zoZRxjp3Ok+bBr6EUXkfL" +
            "Rr1cdz4VjxPy9W2PRpeYH8ASwfeeIT9/PfK9vx6NlN5a8vVlnJnRK9bJz4NtgJfPk5c/rlAf0qjAl5ScHbxcnry8Sr2Z+WzUrt7hng+6bMjZMo42KjCnFO0T" +
            "dtyuI1LW4UDMYcJj4wxWjNt1XWvX04o2cDZibRBFkcNSC2MGrOEJhfpiJIt02CBZrQ76eeHpJSVlvROir4PK01fgXNh1YdfZZ18+ijuJQndp8eyzF3aZ9zy+" +
            "09qhvaPHTy9ueWVyaXFAO5CWFl+ZvLZFZ5Ca0NZFcZf38bWnNr28MGyF7qD28sKpxvG15l1+SB1QZdkBUFjDpcW+O2y9DDU8sVa/AZRr+GmjhtfW33gealjj" +
            "Gt54Pq2GO7KfM6ktryDV4nvUXnk+vV2mNqIagqSXj5599vjaJPVt4x096HD9yU1nJs/v7EMdzy+emTwzmaRe19a+LCcLltrc1dw1OBosLjmnaheaOF4DKzDa" +
            "pa5v9NwX3DPu8EB/MVykPWm1i0dhtt5k6vAp4z56y3kBbPX8+nOTKOuV4kWMNcuBpVPrtXVR3C1/HpjBDmBKS+6lZtAE36XdKMnter9BkX3sYi0GTpiD+LLa" +
            "rdQLOvWkpkE+J3biAN8OiXlE+kT5BvWQTtGJgxNPoiyQBFSn2K02KnoN12hrsPI84alJGFeLodOHKLtbXKj6ObNdnzJsA2t56gDfXweywBpx85Few7uzH0vI" +
            "CuzrQha0qjQodWsdQ9YHtfVeeabk2uSwhR4eKWxXPWtSv5OgLq6/sUPWkGRp9eN2aXdK0rmP485Ze8nuLwZQxw6+BXLzfI2MQmlvK+gsxQnnrLNkD5A6AJRb" +
            "cb2SVdSp9xqyes6XbZBlDRbDo6Htg6SKW6D7D1TqfpWiM4HHwfu9TLLwZF0TqIpRwz9TohRpF2efHYjd0oMaeJsnzRtHkZo3NHjpwCvPx9Qrz186kPSiP1Bu" +
            "5pUnbE4dPrcGcz6F7rk1pw4nbzdFyqwheNHDwxb6635p2Dp1mL2oTr3uPJIYKV8GT492EYIfPfvsl3fx+V+VOpvdb/go9IgqleYPn9bep0hPf/bw1AJ60akF" +
            "9IdJP/909pE06tmlRfa96dTrzqGE9V7/HI5krCF63+ufS2r+bLaT8L1s8Tw7wPyw4+L6lf38yU3o51H3MBOBnz+5KW12eCaFonMCdLtpOrVHe5PFuoB+BlmB" +
            "A318FP+V1MYebRccWy/7tT7pAv+VpsPPaL73uHPKefnZl58dLvbBzy/VLh0L0GuvDmx9TvmCNqecds+75z83wDkF94C3bixe3BFuCRPaaOt+3j2z/uVNw2cH" +
            "OBOVzk5dal5ohJY5pzxl7O8Fac659UIa2OGNoxd3BrfrOtxpPZoz71HVb09Nu0t1pzV3U9SPchXzh8aN0ulUI4WKv5ku63dzH0hQ6OP5u/HfdOrt7s3J6qRS" +
            "y91hu9PK5B5JZZJ/V6m/vSlt/EbuQ6YulN8/HCFr3y3J/lL7KO2W3Z3WN25ppFL87fS7eXdaD7tbUxh53236zbdILaeNUbJ+5i5vh+nU3+a2JS1K0/xwMa2G" +
            "SQojlKiGqe06dFM13Jq/Gep3b8KimjCbK+vzmDCj2SuFTlckQmviWX48Dd61Oip1QI1S2vDpFObVGthTdNx8AfN0uL0a/KVjTcfUcfXdDcqqcsYIvra0Sfm0" +
            "rBL8tWO1Y+oHytsllmWVAhdPsKKkAZ6vLcHPFElcwxmD6hk1DFJqeEB94uB21fqupI5ZAycs9VrJdu1JtsuFmEu0q++QLKNdbWef+moAtx21AtwbQVe/DqwQ" +
            "81xMwU+7qqy7s5odSn04ffgMc5hgbgyqn9bLrzs7R1AL1hL3cQuaZ+jwdaehvwRGLbqY4QStYwgctGpKrR9S59R3bdyuKdTGgqQw49kx+KlG/YW6YjYNNVmw" +
            "FnsQUw4g8l1CvdesRaC6Vlun9iepVlgaOES5Ya23CJxB/ZEuC2rYawWlIUiaAmpoA9UiWdM69XGdOgaSakOQdAwpp18LFpPU69r7L1FH0V9oUUL3C3oN79b2" +
            "ERHTIvulk1QDzMpcI1lGL+/QZbHm7QFpfmFkf+1O6y97Qe3lRH99O2v4eZSGz1022jyOf2iVZk9MTZnUMe4vpMTo6prU1ezdOgX66JX65DcGFunCYJi6x6SO" +
            "BaW+cwyooUX10+yCqe8479Z7q4ln7vFEfxH+F1rkoZrJkeKb46uKN3v26bwc5kOw2Iu2dc3/lioL7zfNYU6fAl2LiVmAKI9Mw+yve4wxaZXkmKxy1qJSspdf" +
            "d7T+qqt+g2roCKq+jAfQ/MaCShntapk1rIVOH7xoEf4KtuFYtbQabtRlNTDjEWs+qmGK5jcla7ii5n+gypomEcDwBbMNoCg7TlNchKjIuks3lwbMlvD8KjnK" +
            "xJHor++oHoDHfw3GF43/puqxFWpKm5cz6kzc5Au85SytLotMafNyjudHORN3eZbuO6igIvzOmDruTCpUjjRAs0+FqArPSg1LXT6YAh3uUagmeYqoZ7nH4WfJ" +
            "GqoUt0uvoYw+Mhr1kNYumon1drlDO9muezVZC+qsSl4O29WkdDkx1dZWe6rUstANSuRluth3oTukK9PVdunzMus+slkcoTSv6wc5psCi7lX+jaNjyP2MHvcY" +
            "zH/O0EJ/lTEodVRmSFbIGZ2whq0ezJoLCUqdlzMka4EiE+ovoILSwD4G/zBlfcCo4ZK0KZhZMTpaohrqvazOyxlBDe2+oMLWwF2C2dak/kjpZaaAwWihxfP6" +
            "APr4mNbHTH1M08UxoAZu0KJ4YTFAWU6aDlvJ/rKjsbiAujd7DKkpgzqGESh7QZih8QTyVKKGr6seO9lftVH9de8K/TVM6a9vG7NehmyK+gsojKiGhiSmmgaF" +
            "WgSrJypAyk5SV7Oa75UxRolGJb7nSEhiaptBHcOYsETxTIvrl0tQ33HeZfpD9u0V6K2K9PhJzftGf2GWoR6PSjGzJ72oOi9ncD8OZmpyyDHixQy5noP/KST6" +
            "a5PRx33OrMOU26NZM2mH6ryc1f1GU861VSu7jAfQvM1UPEMn27XV0GGXn54alBIMnlm6KTo8p6zcZqJnLtHLC73oWcxs151aHzdY70VKZRzN7AWjhq87W0Z7" +
            "0ZH99QPNNip8pyjPxA2kQrKNXKKG+mxekU+gzFUw8xHO7BnDDnekzcvCa9DMbid1+B1t1ovifo0yvegG6y3KI80ZQvul04XThTPZM5ytGp8oi73CycqZypnC" +
            "jeLA5W8h9XbasyRub6i9tPocFsxbx/nqar3J05Nn4H/4dlCunW+wyryDy7bssOSvaaz2VjfsBlE9nPlWe5OVycrqxppBiTKi2VzDMu2qYkn9NSzpHOUWJklr" +
            "UNKZ1edqg1KUS41q+BkhC6TVUBoU8PR9NywF6O13gqzJxmp8p2AJaRusXZlvRO+HBqW/q91Yc+NWzEoxcPulsBbULteur7m+5saaYWngyswnG6wa7/uC30H7" +
            "o9dAsbuYn9mlp7wWtWsS2gX+Ppa1I4N5KiiXlzuo3ajeqFJeCZIVgKxrU9cr1yvD6hA8PmeS5Bp+UoRQPbe/3d8BxfEpgxDdVlIMCpcKDxUeKi65A6cubqLc" +
            "YB3kvQe43b0WLoZH2kfa2/C8E8Yc3F9nJs+uvlGBdpVCcc/jBmsfy8rCE0Mt3NHZ0tnSdTp2xw4pP1uvcrxyonKp8lhx6Mr7pZA6wPs3MlYGqJq/o3Eb9hfl" +
            "QsSsbsWLhZnCTPETuaHbF3dScX99XvQyaL42vPWMjYVySGMd1/RWYxnWUPOcB42pP4j7qzas3bCxDDHHNVCgxclg8iJRQ62/fkfYfGABteaMdRLKGcy86AQ1" +
            "0EYDyxn4fQMntvkdmVOarGurcY8BSXOxv1BWMIm72lgWU/szfyKtF2xjWFtafdY+aw8F1Wv1dvZ2ntiJ+xZEu2ym/lzI6rsD+GxpzSv2K/aSPXRwvSFoBTuD" +
            "ndeJUtv1AOUwYVnDFsiqnbVfRgpsuY+yQNqXiVpSRuWnMn8lZZUwQ9hSCyWRrFKfsxQdvXCUqVhWbIeW22917sC9UF8jO8RbKYIi7nxq5950l2BW5/uQmPqC" +
            "QvWBwt1kEVUKCmHh50V8hz50OhG1P/OUpJyw1bm9K87esucAH0X7Oi+VwOYVO9yf+RxTeCcFyGIKzy6GnImT9oO+WcIatiOb35V5OvKHg9KwdCZ/yjoF1oGj" +
            "GeywgpZxooK6UP3hrsxJMZbJompLlevWdeuGJfqrgnsIrpMGob+isVzMHIq8aL82qJ2+DfeSnEabpzGJe1WObzoP/jB04v56B2mDM20TVT4DtnsaawgW3/Ow" +
            "HPfOgz/sKzWsZj4enySoNTxMqdJgP1+i5ymYj7KNQqPgLZTkzmCknpUeuwbU6srqpt3kzPMlipl3WpMFKJU1MFZc6dluyxyO7PDcmtOrz6yOxzJ6bBjJk8cn" +
            "j68+ufrMrX1Htuu2zPOSKhlUiUblJFGTQImxwjX8XHyiBdrV9Jr0RMoeG72v1cR2VcC3qe36fbVda5rQLuHna4LamWzX3VENA/clmIlOi5kI15RCrOHO3uRJ" +
            "rN+a82vCqF13Z74StWtQi+cvKUtQk0DBk46k9mXmpZ9vhVPtdZ11HfC9ZLuUARGCAuiv441TlbOlAd0Hj9Rh7i+kFsPF/jOd9R3KZ0t3VGAW1J0WMEBVwX4d" +
            "aYd3Zx5lP58FWZvb6+i8HudpRllrrNVoUce9k5UbRZyLpi32NgeVGnbW+et8qiHNRGi/q3sNpK5XPlEcQHw3LeaU52Kq1cc5Be1JWhRYYq/Za56onCjiuJSy" +
            "vpk5JuavoBUudrZByfXdPu4tKZHeWYdrYA515V4drOFhKavW397Z3Lgd52XhbZCr4gw2U3moeB1kyRp+isYy3WKPOpzrz/dz/QxGDuDj0TJaBZS182QTvK/Q" +
            "4b+1/p2yEyN0vHzBe2vL3MRsbnbm53tm98xm5ibemip4+kmTfwvx4ax2gqZReWPLq3fObZifm595bc/8gbmJVze+0dTPBSB1n055Bf/V9TN7d+/dvfvCnt27" +
            "Z2Ze3VjwG55JPWLIKvhvHZiZA2ofUHtm5t86BJQh698pO4JCp1GoeMMtc9tmJ2bnoV0HZtfNbRtOVTz9pAnKelST1a1U/DfW792AB28uHZg5sHfijWbFT7br" +
            "/YkavrGeajhD7Zp7Y2MhhXrUoEAWtGuGqBlo1/DpNFm7tF0EjUK+/Or6uYl2bjb3c6ftzK2b25AvNyqB0a4Dhqxr/qtb5u6cn58/+NqB16C/5rYe2pqU9V6d" +
            "KhTKF9fPzO6eof7aMzO7d0Nauz6S0Mar6+cPzs7M7kGbmp+dm0hSv5nV92I1Kq/fDjWcmJuA2q3H+iGjv9lH6knTDne8cc/cBLYKrfDQ1kOGFaZp3gcDf2PL" +
            "awdm52d3cw0Pba00ku2a122jWmkgNT+PY2Ue/ntoG1BVk9LPtbGsNo6vfSBrpp071PQ8v2D212P6CbUiUiSJKJAFVKdoytLOL7sejS/QO5Y9s7tnZ+agv7zE" +
            "+Ho42V93ErNvFn3AzFyK9b49u1FrVzmfL+9dN53bnQPLcKYzeyfy5XLe7K+3Z9WVJfI35bl17Qn2Nm3o50LZ80LH7C/tpL+bz2fLs+vRR7V3h7ughhNvbSyU" +
            "ywkdPqRRnpf1f87a2PPziDI9wNu1NSJu19x6qGEOauhgDdPbNaO3C0bK3J1zol34N2hXJdmuB/QaQrteWw8+Kq7huwvlpG3sT7Trl3eC7UJ/AXVgljx2o2h6" +
            "Nu2Un5MH20CfAdRuoHbP5kCWn/dMbahrX4Fb9vL+xYmLe4O9F/YEey7OXNyY98vaSWmkCtldhh1m63PrZ/dC2T2LssDP58t+Xm9XQcvMEJbAeuvCDnkumnkL" +
            "KUMbb8/eu7LmC7rmA+tvMrvFK5FA7EDF/OKBM+1Mu/VcvegX+qVyJe/lIQKT2buQYosKLTlXi2zOTtut495qoLyKVylo1JtpsmyS5eApCb8IVMEDaUAdi6l7" +
            "I1k61XbaI6nVjtxLTJwtd8K1nbqLxS8OIJbDOlaaKiXPwuA9Hwrltpkq4c47QS1K6j3RWc5QUKxBprB+rI1YElMfNihFVkmjFmPqP2da6kuYPMU5uAnjFuuW" +
            "3i29/LSHmWzLHjgPRRv/OXNvOnVr71ak2kw1yg1P0/xeub80J5YW+d67W4Gr4LmJsOajJE3zrqPJykaygOutCSrTlWniPJQV2cZXM4/EfeXIPg7cMIfn/HwY" +
            "kKj5SoOPWuTaknqcKVtSdMsH3gvOVAGoFmZbU6l/nblPWiE9FwhZTptlCQ5a5enUh4wacvb6NsnqFCUlZHXkSPmUsCf8ttzvSDuqS3gKoQEMPrNjHYudeHwd" +
            "TlC0y5l2LHeKjYqgmkB1JfU6rcBY4t6+WBLOmrx/W8qqdmNZr2eelxYvKGZYkk5VI1n3Zz4m7aGg2NPbrLf1Vger6SQkWBSer2/4Hh3tQOppXgcoimQ4Fr0S" +
            "rVp3WHf07hAUZW8VVAdrGVj7Mx+U1qTKwnWvW4Nbgjye38FsvPhIDH3m5XymPqpTRUkFt0LxDKqBG6MC659YVmwbuAM4J86aYsd6mEm8QH1cbEtt/JN4Nuf7" +
            "HYQ3zIV0PhV6ueJXBqBB9IbQpoi6Lk5lkl+zI+t1Yiv0UPc1ktWRdvh65gNxDS3Fa+Qo8xc8KHsejxQwr6iX73K2RT5EWn1AXsoC34uyylDQz8c5GgPrIacZ" +
            "vTIXLSOrb6Ofx1PB4KWAyue9quLZdkXvU+KW9Wzp5es5ZEgWaF2lJhNUwJSLp/yQQi8PsrQa3hd5UW4X1g/HSh1PixaxhvlqAWYitYYPRc8pGgVW/y3XJ2pQ" +
            "8pBq6u26z9Sh8PM4M6Akr5qv5Jsm9bBKiXEZuBEFfi0PknTKjt88uuBD88KS0fMWeoVpsl0f/HUeSnUh7uWp+EwA2j1S6HfpHCOe3AXKw15Wdfhr8dsK6en5" +
            "5tDCNJQ6UWWkvKxXjGRNxG98XDGnsI+/NShMV6K5AevXVWXtM2to8f0QPV5xrNQrUEfQotquX4ufKzUK1w5xpVJQTQ+0GM9Evxbn7XEF5UpmulgnBmIb0H02" +
            "sg6kHlmGald9oCi2UXrsAnjse0VsAhaRGS+OQupDcfSUGy+OugDz8kwkq50ZL45C6r6Yyo0XR12AiOhjyj59PCmzchyFVDuiQpVaJo66ABHRI/H5HqrjynEU" +
            "Uo8qNURq5TjqAkRE0X62PMxAm8aJo5CK1m0KGrVMHIWav1++PKxaW8aLoy5AHPW++KUjcyvGURcgInqUehYjINT7OHEUUk8JCtcAxoujLkBE9AFpT66fHS+O" +
            "Qmp/TBXGi6NwpHxW2hNEM+PFUUh9MaZK48VRF2CGfU6TNU4chVTPoFaOoy5ARETZtDzo4wnrgIiIKlpEpMZR9WKdqU/Jl9HzEbVCHHXBOswREcraZj0Z2RNS" +
            "q3ur6Uw4R0QUsWEKBaY+wVTVuqf3pLDdqlUzqJZO/VPmw8LbTOd4JK8cRyH1hPRROTn+V4qjLkAc9ajm2caJo7C/HhH9xd5wnDjqAsywUZQCfp5H2Epx1AWI" +
            "N3ZFng0jp3HiqAsQOWyNqGlHjubl4yikpg3fu3IcdcG6O1ojAi/AXnTFOAqpR9IoGUeJmEiPoy5Yvx3Ny2qPcRzFHjsZRyG1PzojFc8OHEcBVUqLoy5AHHVv" +
            "vL3kjt6mceIo7OXdKnXbOHHUBYgcouirBLNIeZw46gLEUXtNWSvGUSjrgXgmgrE4ThyF1IdjWQq1XByF1AcUSrRrhTgKqWh1rthLUOlx1CXranZSP4fY6q0n" +
            "263Vnb6Td/J7CnvOGidULln/Jaufaglb02vba+u2XyvbAztvF548vvaVlkn9wpS12NszvQciG6feKsN4Tpf1C2MXXEz5LczxkU5NrNptUNMHJDPAHEl7Tu55" +
            "JYWa0duF1B6VOnUgSb1nlZnyHaVNx9yebIqsjyZuuwhbwfpw/becl2pfA+qF1Bp+adW7Daq3aO3pZSAGrlKPrc2naP5Lq7YmqN6e3u7pzDT1s7f2hRSqu+ru" +
            "pKx9KIusA/t57fUE9cNVm0ZQbbYpkHW9ZlIdo5d7i+StMWOM1L2TdXKLJvVek2pFVEtQLZP6qnFKAnor04ZSz/jHvAxQmUImm8kdM6l7DCogyleoXIK6auZY" +
            "WAwX227H9Vt+C2/YqbiFxWKil69qp+8VarGxOJr6hXF7Qkz5i547dAsjqP2GzevUqBpuMu0Jexj8hu+gB8DMJSdqSb+x0+yvTIBUy68N7NNI1ZLUL7LGNQyL" +
            "vRxL81t926NMLmnUe8wa5iDKUaiTKdTV7LShw7YNfs0GXSx64NsKdnYxl+IP7zNG8rfsl6Ccbp1unbSHKGnxRIK6YthhKGSV7TLIGqIsOynr/8q2zP6yUdrX" +
            "7NOLp0kWSjOpn2c3m34eIkTw87dbt5dvL4OPyu85lUI1E7MDULe3bwdPf3t/JJXQ/B5rD8qq1/ooy7H2FBJUblXKTLReUtYIWd1V95jepkXzF9WQfe/1BPUX" +
            "q3ameOx2RBVSqa8n/GHAsoAr354nWadSqO0JilcB/NvLtcHtOH+ltetd5rHQAzDvge7rtfJtX7NfsI+n+N5uws9bizE1sF9Ym0Z9fdVEktqNnHWbf1sZRnNv" +
            "7fHWytqgOYX6GakXQFpS1l+saiRl7ZveQzUEzb/gpNXwxqr3pFDAkW14e/LrTyVO+l6yziUoqOEMUNRfX3NeWH98fZqsbUlqX88VsoC6npD1Y2sVrVQEpRB3" +
            "ipU92jgqS4WzypfxDmxaT8vhPaxI3U/vU2h/WaPh8aMoL5tXxOmlLsTLQYlvuMW71ZGaIFm9UrjTx+dN+u38ff67X+/4Qa1XEquLuR5RLXpqY1kVryAeRvmF" +
            "QoEebVkW3ZGMskQNPy0pvMHMq1pqAarebRBFt/DKdj2dwdA5qIWtbrfRafpNOm6Dhe+qa0wPDkO0U6O71gsBZcn9sbU/MxfVEGSJh2yWg/Vr1PuNkGUp2jhM" +
            "+9kCeCLudpt+pVzR62d124PJkHUI0qSs++m9Q68UtFCHjXLBuKA7Z1UKA7dv8+3MfJPyj62v8vphK+x2Ol2/Yqk9Rn2WH8CzmLx1ui9s4zCvYtWCw51Oo419" +
            "ZWofc6aGjuQ6RP0BPdH3QIdA+dzDMQUcPAAwhW/jfiaouzIPsG208PBA0g4bVh+ecMg2XNwRzbZxVySrW06n8NZTsCmNmqB1UZTVIbsraIVklVlWz1Xt8KMk" +
            "C/qrXBHfVe0QZHkoy8KzOFEv30W701HWoNYtd62mVrpWt4w1hKcduiVZ1vB+WlkKkGp1vYb4dmyHQLX6YIc9tg0hq8U7q0t9kLVQxu9Jm2Jq4JE2SnRHuxtT" +
            "z7P1giympjRqCFRfUiVJ3Z/ZJ+ywQwccWB/SBinvbA5sypmmN/99i3v5/sxHSIdAlelYBOkwpooWRol9t03vaqX1tnjNvMayKimymrk+yQpppzfL2p95P9tG" +
            "DWXFtqtQKMtp0xteWcMnaNURtdH1m/WKMbaK6Df8QQtGZQZKjrPP/Nj6Lu0zh7G8OOg2p1WL55E26PYXyW/k2API8fUR0V/dtjdtWhRZ8HS/02+BJeasAtkI" +
            "UU+wHS522xWNiixxug/yeiitKKlT1F9Bq9/xcJFB1x/8xJtutNF2+e7unqjh9+XaV6u/06tjCrGiOpLbXrvR7h/FrFLSnpD6SuZ9pA3M6lzQelhKA2pRSMvJ" +
            "8fVtplpAae2iv0H9vHanE7SSNeTlPdR9o92Ybk5LqyfrnW5Md9vhInkNGCc9UcN/yMyJeHcAvYz9HHveynSzPehgf/FN5HKvDtbw46z5Lmoeik5Nd6G3QOt8" +
            "33lOyvo27Z6F+j3fbTeB0+YhkNVt958NEtT3xf6NXis83MD0uJodgjngUk+FfKjTs0NLtutBMfOH00iplogLS5XGoBXiiQDHckJbtutHGRFVtsK2Py0tUdF+" +
            "2Sv3az08BaXI+vu4hr5fB+tQ7beM9euX5MwQRvHGP2T2C6rf7k7zWIn0Ua/4zQae0wkS1JOiXZJStFivItXq45kFB890WVENH5TtOtwBfTSjmRn+W66UGx62" +
            "CjX4LUXW/5uJduks9v2GsA5h8+VqueJ1a30xV/bFN38Cfv7TvO6Vs+j997A2AL8DxcKC75b4b506FB9Xp5G6n+IN6Hm83x7+gmdKGviihnwq+MKSuMcX53of" +
            "81AiNcH7AWjksKxhzS9Dsbj0S/Jvfh2Kz7L2i3McyGiyylJadGswSmss1ZB6OvO7vPbncoxEuYHxc2aglYJpY+GsMEj9CVGYkUVS3UbXhwI+HSlxI28bi6S+" +
            "xHv1ce8F3X21hLOf361DSaEGEXVKUi7LGrZIUqqsoaC+l9knT92LY4Pgkdo4V+F8EMreQh16vrdU417+nngPq1FPBm3cmdW2oj62OhCcNSLqvNwf5UVUK6yH" +
            "9baQJXurQa/2lkqSekC+1YtkEUUcypIUROAVSZ21PyQsik9O4Y3NniULzq7xvzz6lKnHIiqw5K3P6rfSqH9jv4/tMFUWSkunHkzIWpk6a39B2GGPLIprKC2W" +
            "/uVq/yIqsP+Yj93XOLIS98dboqT8C6nv27/HFFgUji/+RN4hzb89+ldE/bP9hxSlIIW+dVjCIiO9tH+x3/hgbBkZnP/CGtuF9BsNioe54C1wTD0p39BRuoGw" +
            "FRInvY3UjKDcoYvUrewPy9IOkZKSOuQ3Yr3nFeoRtngvlhVTKC2uXyWiXH4PK/qY70aX8wq3Xo258VOmnhlNaf9SKZtqKP0actq3RsiyKXJQKflchAUp7d+C" +
            "OsjvEEvsedHb4JtT0f68lwdbxhc3+C+IWBod9jYH+U2x8FDs2YQXxY0eBeivAvxN+FHp2RZ5txjODrR7dthq1KmwnWfB4rPi79PwvzrPDot85wKOkySVFVRW" +
            "9dlk85mOfHPjcsvA9wrP2/W68AyA/9+dxjIQub9+AvOyoCKPPWSPbXXzXdAF/j/8fZq9vJwrfyD2H9JolrODlCU5S+d+Yv37zLNaDWl2qMeyBBXNDEzJOIqM" +
            "k/bAtNtt8qHgOylvtZ+NtBh57H8Qca9MU4L5fsNp8vL5dp5nB5xdQau+zNWG7fqINjsMFvuLKIvGSb6T53kZRgtES15Zyvr3mfdLqiJkiRqiLGCwjigLnvaX" +
            "alJWICJYOoru4m6xAUSsOD+EVj8Pxe1kO9luXUQONUl1JFWUFM8qfaZK3SyUOs6ezQa3DGv4cW0mAklPMsOt6hRAloW6kBpE6k2xo1WmRCEdImfh3RcUERUo" +
            "HvIHNUldFnFUGO2cGyeOugxx1BcpJZPYqztWHHVZxFH8rGmNGUddFnFUiqxl4qjLIo7qR3tux4mjLos4akBZCyS1Uhx1GSKiF8TO9ID2EY4TRyH1R1ENmVo5" +
            "jroMEdFMvN9uzDjqchRH9VRqhTjqMkRE90rKGTeOuhzFUQq1Yhx1GeKNR+Ld/fZ4cRRSjyeoleKoyyKOavMZCXu8iAiphyLr7Y1NnbWPaHY4Thx1Gb77X8md" +
            "35Qrepw46rKIo5Ci3CpjxVGXrev2VyTlpFAp/2K/8SHaJyLtcJw4Cql2bBljxlGXZRxlyz2m48RRl2UcRSuRmAhonDjqMkREHdHL8klv5TgKqS8oZ1PGi6Mu" +
            "Q0T0cHzeYcw4CqknDGrlOOqyiKPIMpxx46jLIo4KbfaG48VRl0UcFXI/Z8eLo5Ca45lI+I1x4qjLURxFNcyOF0ddhniDz3GENtnHWHHU5SiOCm169zFWHHU5" +
            "iqNo/OfGi6Mux+tRQofjxFHYroe1nd/jxFGXoziKVu2z48VR2K6H4jmlMF4cBZ5N3q7F4zI7ThyF1GdjypWx3vJxFNbwMVXWWHHUZYijfkejxomjroBn+6zY" +
            "Wz6daxfGi6OuQBx1VGQIabt4H8M4cdQViKM+Ine05+qF8eKoKxBHPS13cDu4K3icOOoKREQnxB5MP9cojBdHIfV1sc8xplaKo65ARHQy2iPtF8aLo5D6E2pX" +
            "iHtRCuPFUVcgIro/3i9+x3hxFFIflc9EVWvHeHHUFYiI7pNUqbd6vDgKqQ+Y1Ipx1BWINx4VvdyhXh4njkLqKdah0yE7HCeOugIREe0zx72zjqRWioiQ+iBb" +
            "769EnbWP0unZPtTQK4wXR12x/tH+b5lyu6VGVa4RLVhc0v6F1PftU2K/eL+Id6uME0ddsf7Z/pNIllcZbz3qinijSm9YaGfqOHEUUvORZQTeeHHUFYiIxDo2" +
            "UuXx4iikHpWy7khS6XHUFYiIPs8WZWMfjxdHIfWssEPfHTeOugIR0X7ewe3wnTbjxFFISZuX1Mpx1BWIiJ4QttEujhtHXRFZwjD3qKRWjqOuQET0JPtet14o" +
            "e+PFUUh9StSwXvC98eKoK9Y36Rm2B7KCQt3TohQLo6i0OOoKzMvPibkyxJMshXEiqSvWt+ktDMoCP59HJvbySkQ0rVM/EudhkQuL9Ty2LOJGRFIwluV5oopV" +
            "5Xh55TjqCsRRH5cJZFu4N32cOOoKxFEfi3dj3zZeHHUFopQPRTNRb8t4cRRq/ndiWZM9f5w4CqkvKFTgjxNHYQ3bMbV6vDjqSnwPHVI7xoujrlo76O0tnirh" +
            "n0CMUqYiPHsUo5DuhxQRXbWeIJv3c74bUZ4aR2mRjYijrlpb6QkRb6tsZ9kDwFCO4iisoRJFiTjqKozlz4sa8q1W4AEa9Hvrcu6Jaojjq8uyfpeeU+rZTpFv" +
            "tRJRVBRHRTVU4iikjqMsPChUYCqOonDukbOQGkddtf44g+sAeGtqW8ZevhiTCUqOFKQw41+72C+ij4qoVFnDiPqefAO+Gqxw9XhxFFI8UjDzWG9yvDjqqnVd" +
            "xGycsXC8OAqpD5rUinHUVetFG98h+pmOSAW6XIwioxSkcDbvZDqupLToq5QW21y1fkQREVihsPlBzfyW9m+KN1DWEa6h23Esa7yI6Kr1M3pD186FxbDIslZe" +
            "WcIafplso1NsK5QaEUXWoVB/T+tR0qLSVpbSqB28a7HMfYXxBtuCbImMT3g2X3KWHKYeE30s8oS0+jWVYs3LvUyYBxGpd7BF3RbLimMv9gDJiAgpinsNqqNE" +
            "X14iIroq1ojwjmtr7Dd0SHVHUyMioqsQ23yY/aFISTtORIQUvsnyXelFx4mIrlo9WrdpF9rC21A8FEVEGNlEEVFbRkRI/Z5GDWJ/mMd4KC0iugqRw6Pssckb" +
            "jhMPMTVLFFv8OPEQUt8Xd9FOZ/Hc1ngrS1chchA3lWcxIhpvZemqdUWch8Vx2c6NFxFdtf5ZrGJNQ8ums8l4SMjSVpbAA2TeJ/0hPfcOKEZp10fHQ0gNMtFZ" +
            "s0lrUpwnoIgopIhI+FEtIsJ2Cd8rxgrIwtiGR0pezrCeFhFhuz6o1VCNiDojIiLUvMj4l2cvsHI8xNRh8b4ymGQKpS0XD3EN21ENkeqPERFdtZw4J6HOkRbT" +
            "I6Lr1jb7/WKNqJ7B8dx3+668ZzNZ8FOmPihXlpzxqY30jiOwQqDKK1L4rIvUvVENx5ElqUd5zQHPjBZx3QbbL28CTRb8FKnn7WfoKZtWKoosK3RGFdmuL9mz" +
            "0anW8WV9Ka6hOz71j/a9Ijtu7xZ8Ng94TzndBcq78zhPLfW6NbCHNlMPWeIk5mpB1WjPDlGkuYgJraGg/sGeYllFPtdKshwhS0oTLNIDh6n3ynWAkiornerb" +
            "TD1KERHnEUBt4GlieSNtsuCnTD2WQo0qkvoEtSvuL/z5KEk9OtnM1O4EtXINz9qfFitLXdcTFgU2ZaeWyKIC+7Piib5f7VYwGxyWka2Cz5C6bn8mykSAK0t8" +
            "d/UoSlpUYDfjdRsPdzD3hEVB4dbQPmzuY+gvYVH3sUXBE33o0e52V+ldxZpwL/bAGlpsG01pvbeSbfBZFFXbliwQPVsD3Q41qucsTz1oTwu/4efiu4OXK0ht" +
            "JVnxas941Jfs7UomErSN0ZYRW9SX6K20Ti1XmPo98gC8spTPr1zDvqAeJKrv+jlc4VzOh8bUM/ZHZQ6YHN98vJyPQi/F1McF1RmDQktE6jkay3zKH59i+yMt" +
            "XpX1nN2OdNhe0YtK6jr7KMxrfytnW+w5sihzSWTD7KOui72OQK3RKfrdzGDRqL+zG5LKT5fxzlbLsI3A0n02U7siqo2+t9TTLZ5GmVJDov4P6i/MPhDSOhvU" +
            "sRbpGUvsa2hkDqmGLq/B4nmQSiioHmYwiO1R4foOU2/ZH5M19FBWn06TGJqA0rf7tEOBqTLngiv1MO++r0hyA8X647ri081PrV32DD2NyieO5a2XvShS79XW" +
            "bcajHqSxXIcn33omGpWjRrIYy0jdp1F0JmqFKOWn1lOkw3jdZiXbRetFCm3eL2IGnPGpP6Q5xc/xOxh8oh8d2YQOP9Ej1SGq+ytR/0izec8LVvc8Ps+xcpSC" +
            "1PsEFUhqxSjlp/BciSMlyAdrgrwYlWImivRt8d0Y/BwtqX2SKjAVuIqcaHQxNRBU1d5G/dXOydyuy/l5jgHQDnm1J7JDZ1k7hFgBqSZ5gHpk8+Gy1hsKO2yS" +
            "rHpuvJES2+GTwg6Zo8hhDDs8yBZV7BQjalS8EVEde56eRTvFdm5Fyo2pT2prRONRb9mYQKuXZ3vCiEqJNizV77ajKAWpKbZDSZW0yDU1tkFqG8vKywy+EaXJ" +
            "YSvsRlSLZVU0yjEltbUaPkg1lCsw48U2P4XYZoe2bjMe9SDVUK4RjUtN277ibdAKe9bowvEGUh8QslDaSjGKpJ4kbUiLkjcXjS5sGxx90cpobmUvypEDUo/T" +
            "qGwLfYy2wji2+an1NZqXURtIDUr90vLtGgjqoEYNVqghUyE9pwQFvCVF2FNKnBxbMHu2kOYvmP8rQUWz3ehJTWUk9ZqQFRYiWQal2m87ojCOCguhqKHkTA8v" +
            "xyVTeXpfGVZC8thxXJOIbJTY5qfW2+nNCDzZ7Ah2iOw2tSjeoKLMX2BPHG9k+N0ozg74HFVTbGNkbPNTq8Z7YIQsfGe+cmzzN9ZXaV0UfXHbxSgAbabSKDSK" +
            "OFB9SoLmi9RkjYrIQv831vfozL6a15FuUGiILGlUCj7+K87k/zfWv+ZTz7bMYgYRetHzKpTBTaH8io/Z9fkEKFIHBRVQDQOoIWfqK3biAlQDTwmGjqzhYeEv" +
            "4Hk0F90Z0ISWNYrdqGANm92qrOHfiF1wlL1QePputYkn8bvVDpZKt9KFf0cMUq9HFO7uwf/2S80K59yrUu3wbxXSRb8UU7+rUXifV7MpKKU0G91qTH2V394q" +
            "z8thzfe9ToWWmynVnZ9rQPEKhUqub8t2fTTeb3OrOONKb/YqncL/39m3B8mRlHdWTbe6B662evbWjigFperRKQJw2H+0LBMqodruEVpW7Bv2wUm8tsXAyrCv" +
            "EgJUC63pBsQsLMMsxgavWd3a2IfxRWDHHr6L4I8Nu4PbWDuwQmGH7TsTmiDq5LMtJGKpuNBxtZ6O8X2PzKzsqprp8k0CqxX66cvK/PLLX2Z+j5CyzWE6Qxcw" +
            "rcSUspSXDkqb570odDi7H84XaQhIariW7TQThXp/hnqdQPkoyRVzxSgbUC81ZQ+/q3L3xVKjaljjwWI91NI7ujAe2Sw/plA8y7CG2m5go/6GspH2BpN2UtBD" +
            "+CvrPF9YrcGWcqhhUkiKOxWy/lbmW1Y6j7rBeqgwIfcvm6/v0lsAyVB6iDkZXV0Lobn+pEdV5cR8qbyOrUToYdoLMPsEayG0IMJIVYzBLdMoygbZTNqIklpo" +
            "Ixb0dxBghb0M9cUpFPePUFJ7GdXVUZwFXdPDJuih73RQDy2ph+ggh0lQ1ap8lt/NG/jWPvY5V0jku6yFMHqsvbQqqYIdo57hbJB1w2YvHbBprTAAPQytojQr" +
            "pYg5RD0hvY/eML6ZZSUKZUtpqPfQx5Sk/Y3xDxzxBNrRn6P9vEn5GH3SJJ5lTArpB12qyydG4/W1U1meQMChjYpgtnidkH2C5naiIGmPNbtxQmZ1FFYDq9Bg" +
            "bkpNC4VG6dbmg1oOSZSElpfWlcSU6OFl+e5QixvoXwJzjQV4OsJqEArGBW2vn8kyVSWUsdlvMJdKWxbtD8r2ovX2XSdRqFdllISJNzdcJ8fi9SVXZYdQ0IGx" +
            "NoYPqRySnNuVViVKEiiWRXWDmhlKz9C4JNYljgg0MfIuzhfY3rGGekTL3xtLVIAotvNk6THdS5DJWlCzzHdS0kZ5uJblaJCl1+38Qu20lhuTLT3uRB5mclUN" +
            "UYGG0nJINkavG/EKcztoe/vceNxhXO3YzFDvzlD0UkTZeDSdJzsPlt61k1o2X3dlFns3Suo7HbW6bLG6QJLrNzJZr6/dqXJWj25aQp1qdRyH853yeuzwPuQ0" +
            "sz1Fri/oH+wOS/RdoQ82ACyMJfZk6GFgedBDDfVYhtpDt7BNMICEchXKDlxP/67XZ1WoWqMWyopx//LRepJlY5Tren5T/65HNdSYUag+EaGiDJW2pKz7zRfq" +
            "07kWk97a3vN7V01obfpf8/yRtb3JVNak+82/qE/nxUp71l5oZqPdMOuY0+2kczLJ5cW63/yrXObJhFANs0G4hmkdsY6UoW7LyVrbu6b1cO3I2sm0gHptqsIL" +
            "o1b3Zl+1fvLCyaSXR71x13TGv3R4YXl9eb223oP/Qls7emG52MOf2zWduy8ZXji6dpRQPYE6WkS9ZdfduUxU0Md96/sA0SZp8HcUv+sv6odzspyjVq0OI8jf" +
            "tbq4tpC2i6OxlEctWzWrplB71zpF1M/lMq3FQ+dtFkhrSGl7z+8vQ70tjzpKqJoY+71r+9PCyB+td6f659coj+NcY6WB/wu/tub82vQoIup9U6jJEOVAGzZ6" +
            "+M86fONkmEc9nss8mQ4DRiFO/MqvpTnUszmNoj5OoaxasYdh/dEcajJ0m3bTHlKDX7nNtNDDv8jldUwRBX+/3bN7OG9uLY9B1Iv1R3KyUpbV3EnWX9UfLpFl" +
            "1wgD/3RhBIt6GObywcbDCDN2YtZDWMtkN4wLcAbMo+7JoVL6HtLfNqMm7bSdH/kgh/LnAGU2Mq3HerYF1NtK5gu1V9qAVXOjgCrYjaELcubMuSE0+JVtTobx" +
            "sGg37i1olMWaMWTNSEtQr9/Vy/XQxTypSqPqMPbFWf7F/y/Ui7nMk0lvHv4k4G6B+bqlcQv8+uj80bxuvJizvWCx9ylUm1FWAfV8br6SIVp2mK2bRSux9Peb" +
            "Vwp7irO3vpdQbcTUjzhH0hLb+65cDy/sW0PreQtY0Vu2s73RrnvKbK+OKrHz9xQyTyakvbBKboFWswpjwajbCrIIg6i2fYt11C2x87cXMjR2lh1cKTfXYX2d" +
            "N8/vXVss7l+35/JwAor2B+tmXimA2lu0vb9Q0Ci02LAmb5YW+3wp6mgJCrQeUTeTnS9B/dyuO/O7w+1uE8bjFt711vat7UtKUA/kLcAx95jVpPl6A+AAVdbD" +
            "u/MW4Hb3qEXjTjvsvvLRuC+POuYuC9Qt5agfwsk3EDVRZAzoYOBjMvJc62hVGxH1zhxqZSB87vS2gKRWngJ+CCd63s37GspdKMpy4MSRKNSrc5xXP9ZQjlNE" +
            "+SArVqj/q3zMVCT9YGsg/L+11lkI58caSu5fMrq1FOVETqLJMrUqbyKvQCkKcBrqDap+SizzCiDKKbbQlXcOOPI9MfIcpQ6jEbiONU8NfmyrUacGO02qzfKx" +
            "PGrgOq7AIIpwjQas7gz1tyJmn2LU5cgvuAtYf8uat+cbVoOlNezaxMzm6468rBXXkdJAEiHgZ05HvZlyEWS5I+qdssYVTeS94w+NxykPJ1UopKhi/YZSa3SL" +
            "hbU5cPR/aPQo5jFUsyXOTiXNhjNYKmQdZ89PNVt2p6xZXIpCoR6f+1IWEQ+/I0+7082NgigI0h7fViDqtxFlUrQ0/E7+jlLdVUbBgO6xWoj6TCaLYoq3kYXS" +
            "1O0Xop6ditkvR+GNVBBlqGc5l6kpYuLRH3fRweIdHXEr7dSdhtMAHbEaE3FXiagPyjhkiQoZQ3Vn8J4DUa5t281JMzUZ9YyoQQO4OUQtYFk38gyep9HmBjhL" +
            "SDMZ9V7OESRiwH2FcuVJ3rGhNUAnLVui3sVewcbSDM3Aeyy6xaLReF7qobmDFoZ8Iy1vHX9o3MsxI8qubS9J18NTFK8nZYG0flmz6AaMM/KxjTqqrA1L6w6s" +
            "TqFREYpY2fmsRsbYIA8xtACdfOssdNysKuoPjTfWDigUy4K5XZhuZLHnkynUUR0FsroRZvqbbvrewN91UllsQM1hDwM/6ORbNGV731y7X92z8XdtlexfYOWn" +
            "vuv1Wu0qJWsh3/yFSN1i83c9oKNq26Cc1J3+rkAfDRjFlYgKz7C2zzewGWhJrblUQ92eRw2yFWKjzcZWtxu2WF3cw7fkUGq++M8TBlDKXjOqm0N1I8LgqmIM" +
            "7Q/TqNdnlWsokyUlGeoEPLuGq/Yxtx6oW6wfalXeMtQAZxlGjvcWdx73MLcxjXpnYeRBiuPRSFCj/chuTGqJ9l3367J4ljvUP0AKrOVZQTNVdXYvq33ZwFe/" +
            "ejUehah3qzhErg03m0ddhn35ttxr4GwedVlVeuUqTdV41GVgRA9k973N/nwVHoUoeb+B76EUlTGTR10G7X2PLqtRhUddBh61rKHGVhUehSMvrA3srzxfs3kU" +
            "ou7OUFY1HoXzdae8t1WoWTwK5+veTFajGo+6DDzqLt4r8f3ArsajLgPfODvlWV2FR10GHvVeEbMfNv1GNR51GXjUKfmqR57wVXgU9vCr4p0Yt9FqPApRvy88" +
            "q0OPUBV41GWZBYJj9p1qPApRz0kvbnv7Hk7zqMvAiB6Svvrkt1SFRyHqo8qjNXaq8ajLwIgeVJ7VeDtfhUch6mH5VkH+2FV41GXgUX0tIr4aj7oMPOrjRlaF" +
            "shqPugw86sSUF3cVHnUZeNRH2GY0OB62Co+6rO2wuMKwl7N5FKxKVRGVKuRZVXjUZdiJfPWStUSyZvMoRN2RvX9RTpfZPOqy8Q+cBYJs6BKVz8Ndr9CcaMr2" +
            "vl7tlYawvbgv78ykLhv/Z+6EbuetckZETEqTtVB7fzWUxqRwvrrZq1kDucdsHoWoe7T9a8mqwqNw5A9lqDq+0M3mUZe12nBo53GHnc2jcORvy76rxSM/i0ch" +
            "6l0F1CwedVl7KabdfL4Kj8LvekiTtTRfhUdtwK63JGIexa5cyqKmeRSi+P1LRleWsqgcj9owrs69Y8onEE8OzgwetQG6odZXQ/Io3M934lEbxr9VtRdlhCVw" +
            "FL/Io0In41GIkisltmI7Jm4TFVFOqPGoDePn1TlFRmZvDQJnZx61YXSyc4oYxVksClGSR8F8WUsVeRSi7pWSbDnPs3gUzte9uVnG+XJpHW/Ho3C+7svP10oQ" +
            "7MyjNozDIupZ+sHutINZDr9MbxifIx8YqU/b7WDWFI/aMN5P+3IWubCtpIVs/9owInrZ79dDKat8/8IdrCP3L+zhF4XvN8c9V+FRiPptirCW0fflLKo7xaM2" +
            "jK9SdncZs1+FRTHqN/B2DlChvT2L8jQWhahvk26MnJHw4lYsirwiy3kUot7PkbcSFUruJdlXkUdtGN/Jop4pEnmBOJuojbtQxqIY9eAUCnhUqFC+2FmmWBSi" +
            "mEdJf+xqPGoDeNQnaQzZU78aj9oAHvWeKd/vKjxqQ/AoKasaj9owFtU9APt9GUYJi4KVpfOoDeMdtXdMreUyFpXnURvGQ7WsuhaszbkqPApRt+VQs3kUWht5" +
            "GjXqcWOpUeU+agP4ofTfADtq8xl21n0UjqHcHZYaStaM+6gN49bae+Xu0IgbVVgUy5JV3kb1UV3Ml2JR5TwKUUey75Iof2cehT3sZTsRM2yeJWd7HoWot5eh" +
            "nJ14FI78neq7eF/ZovGg2S1lUYxSjMga3yRQO7IoHo0H9VmuV+FR+nyBpJtYkjuDR0Xmt+k9JduJQJLvwEEvnMdZz8cxsSYi6p3TqBWwELACgckAStZ2kXGt" +
            "iUA9T69L/Tosc7GXwypEhjbfr8f18ZwuDT1sGXVJZPthd0h5v+Gjs3O934inUHFTynpV3CxJaVu4upwIeggMCKXlYjgk6kPTOTEQ5UcOogDXKEdtSi9uoy/2" +
            "PcTheoJRhNWr+/nrqPcWUEj1EJVsi5I1TZbQatTlaDjzfh04yFxoJoaeF2aiUA8JawicUo6hD6PY8BthDeMVYoXhyApE/Rf5rmcpzrbioCx0/p7rm8XIJUbd" +
            "o1CCf61QyWpAhbVwCpUqWQ/TrWMW14ZsCRc/BTw0+7WyeCJE3TMVu4R7m0OhBMjItkNF9F2LDcmI2E8Wt3JyJi6J7GTUXZyDS8rqcBQFozjep4j6Np2XF7E3" +
            "DX7hcNmFG3ZgjATMx9PJ9cX8MGn1SZ89RHUB1cbIou1Q3+HqPyrmyWNPWQyRsBlXhnLmuiJaR40GGDMwhs2F2oL5IsWyiGxa1BYE6m5x2hAo8l1FlFN7sZah" +
            "VKYrQv0C3X5rspx5hwwvoFhWqDIhcZYWRt2Vk4WzXCYr1FDPUw/l2rL7DlooMIh9qts3KjRpo05MociuwaT17fKILkZ9hjL+SRT0MMRYGXSfR9yoJBqUZd2j" +
            "aRTwfbC8GHYDs9fMoop03UXUOxRLWarjqlyJgMkYYEPBrvH79rS1lqhjOUYUoNMy2qf6uKGPR3Zqi8yHalkl5THZm4BPxyBt3JiKPdf2lI/VMj9Y5hvdyMc9" +
            "xSpY+akeypMv73lgDTvhQoK20BrvotVV0sM3K89q2ivJZkcdPOcC7qYyG4qoWxWPYm6DezkwLZCV1KfnWUedqN2jjYaURW8Twl6X9fAP+NZRxLVtDeiewPHr" +
            "EVnDUMsnoNvevxR3Dksig9TKgA4adX/Or3VqiYr9RI2XPCUyfyBeEOQKQ2k+ZVgCLav1VRaCLO6OUfK8LG8BGBXmUFncHaIuy0ooRizmC3dY6CF+F+wpWVRg" +
            "ovUwi1wAHtXiERkEkRWRFYWdiLMJqPg5uS8/IFmU4F7MNkLc82p6doVp1Am5E7UyFEmaSzSULis2Tos6I+T1QbUaMIId9hS6MDPcpJ22A9d1PfxPO701GTDq" +
            "aZGNPxYVHjAiA1Ce7zFq0kY3eA9+3PbkXEqoD9CZqKNVGjGMtBk00ZJihMykFdjyx2pd2BMGjPo4lZLB2c9QE0C5BZQNciNCcbbVUPp9cMRTC/sHPfToqzz+" +
            "CXqTc/hVjPq6qE6CmWhExFNr0A4EatIeeF2FSgXqRRHxFGnSkib0y1sQKCnL7U2GoUI9U0ClmMFYyeoKlAeoSPVQvBOpF98RZgv2O2hMsQHn9Swcigs3TQa+" +
            "K1GfFN5bEjUGVIhJyBGDM4U422u90JoMokCilE+FqGkwOsgolGS5xMvxp7Vub9ybyTotZQnUmFBY6xgUyRUYRLUmTwRClkcxqmMpq8bVh5MAJp42x7iH9Ut9" +
            "iqGJeunBeD+ijkkvHeUdgbOcthdcSgINmElvEHiBHXjdSW9yOD2EqL2Udy6W48FxN+2kLROEYt1YfjOC/bArZe2lzMkKxZHPoEcLJagB9DDZzyvlPyvvI0Zh" +
            "ZFvaS4YLA2MQD9E/ezDoDrorW/Cr9NNJyKg/5Zom7OmEqBZG2aXDDqASQm0p1OTT6VOM+iPWXlNWNWFZ6RCT3CFmJUJa5QEOUMPkNOvhb7FnJEprjuR3wdhh" +
            "oWfqXxRE3cGAMU9JuzHg2h/Ka2m0HD+ZrISRH/liFDA+xYb1/1I7vSmxWDciUUmC+we/dzYeJj2iHQ6d3HBZgyZOWpObIs5KYQw4ftkUPkvEzeNTyZDCqfhd" +
            "DyWBLNu7ALjQYtQ5WbWCaqWyNEQFhOJYHUK1fySkSY3yyUYJm4jZXjCfI1o2HzVqq+cB0oN/gkb5jPotgVK6aKSIarttHEVGdTMU6eHdtCp1ewi2t+l6dSy9" +
            "7KPGB/LGDfjwpJcI1OdVD5knoe0NPMuVWjjgsQdpYKUE6jjd6SFqrNhL2sRjMqeoTFsu3S/zmp602Ioep5US4f6goV5qWYiDluq2195qTW5F1J0UKRmxLOHt" +
            "qKQJnDQDFvw6DRj1EUKlmr2hrLBid2CUSyjQj3baZY36lKzjU1OY5vda8948lVJNlcXueoBZkTbqS8qT0FCZZCdtq20JFFpfbjBfAvUsVWpIuFaLRMEKc7rz" +
            "VFI96Q26AYFwtuTu8F3KMMzSpLWhfQ9lEWrS64ofQAlZz3D9ZVyPliGyaYyH/cjnF2aeX3phtmGXnezh+XqGdR5RWKhZoJJzPt4oM4YbDOQL7a32gFCPk50n" +
            "S8NFxgUuDjuhJg8lYbyZkPWseJWmeiHihDMaxr3I19Z/gL3z2pM9aVeOPGVBb3K9eZYFqGEYRVEwCAIXrDW2bjfAvVzN1wVCjVqA8xgF/RsmUTQAFA47NvjB" +
            "bKByDL8rcn5yJXL5XaP7kkEIRgBlgTSeL5TVlahRGQpGMQRhhBOqMVGoK2APPyv8N2LhwTGbRyHq1xRKZlucxaOuACM6LXJIoh+B06jCoxB1TtQLSGqdpkTt" +
            "zKOuwMh/RXh9xC0+783mUYiiqhB4dm/JvDizeNQV2Im+kGVLkHmdZvAoRH1Fixxn1CwehT18TGYiaI1fV41HIeqciMgE1O5qPErJwqjRVjxfjUch6myGcqrx" +
            "qCvAowbSO6oVu6FThUddgf3rgtTDZtjCHszmUVcEI8Jzbdzsux2nCo9C1GdkjYFmqFA78yhcKf+N/Yjwazzfr8KjEPXXCpV2ce5n86grgn1hTHsCqyCIqvAo" +
            "1MPfE/mVQJrHcz+LR6Gszws9pGwEnSo8qkwPZ/OoK8CIntKykvY7VXgUos5nK8WPO1V4FGrUc4LPx2Yo7M0sHoWo31WWDVHoPzeLR10BRnROy47baVThUYha" +
            "y2yvyiqwM4+6Ivz0MGeYb1tONR6FqAHxSEK51XjUFWBEjwhvMQxGn5+vwqMQdVpE+WOIuELtyKNwlp+mUxt/mWNVYVKI+rrIFTyF2pFJXTGe4l2PctFi7p0q" +
            "TAp1/jdE9k5dFtj5ttXdjkddAUZEOca9UW98oN+vxqMQdV6iDmeonXnUFeBRj3FFE290YHxyqV+FR10BHkXhgm2UNe5U41E4GqoOeG98mmXN4lGI+pasuQKo" +
            "uF+FR10BRvQZUatlBDg5HjvzKER9SUPF/So86h+NOwoxj5F78cBo7+hkOlyjKN/b9vnu9J9A1O35qEzv4oGLy4x6xXyl9vK+wMujbtt1LIeadC+eWTs5OjkZ" +
            "rpOs248G3TzqyV135FHtiwfWTkvUK+bt+4JeHnW+EKMaeRcPXzwzPp0OL5qXapdqF/f5XhH1QAG1gagz6XCDUW8q9vD5XMwjo6CHp7GH6+alI5eWiz18fteJ" +
            "/Hd5G72Lp8eAumRCO3rp1KBXHPn8fMW+c8patvadP7zahJE36/scr4haKkG5y9byao9Rt5XOV36Wk3Moyz6zeutqS4x8uzhfBVTgnLWW1/evthn1j/uLqDt2" +
            "vb2ghxsHR0KjVmurtZ+c8oPifB0rjPykp2vvoFemUXcWRh50XtPDV08NVoqodxa0d+Mso15g1Nki6k93PVTWwzMkC6Rdqg16fqGHr5Xo4QQsatoj3dg36eV1" +
            "F1H/u6DzA0TBStkC1CvmSmEsGPVgKWp8GlGXjq4MByWoO3J5Kgyj/9YU9XC53sM5fsX8zoGNEj28N4cKD8NMAep8b7WGdmPSK6KK8xX2nju1trx6GrSXNOr3" +
            "D1zozZ6vpPd3Qx31Rwc2CqgTjby18d1Bb7wX602s0hg6bnE0TjTy8xXgGJ5E1Lr5ypGVYVAyX43GUmHkx6i7oBuoT/cuBytlqGMF1MvLGeqJghYSKrdSUu/P" +
            "SeNxpVCUOuqhV0S9t2ChxsvY0uErtVdgVW4Ni+vr+K6goL1/rvaU8yDt4puKso4XIv3BYh84vz9by5ODRRv1tYJuDOyXD50/NDo0GV7oofVdrxV7+LVczhnc" +
            "U14+/PKh0VN4VkDMenNQQH0/ly0BZV0EFO4paLHXa1u9Yg+/X5A1aAPq8PipdPijbVGNXUem7fWtnVPzuE4OrL6Bs28EbcfOo+4pWNH4VveMdWbtwOoetvOv" +
            "7ndbxZHPZ2aID3UecZbX9q29YbVJI3+gKOt4iSwfUWfWDiNq3Zz0iqiN3HcR6iz08TDnCLq4L2gVtXdj1135tUyotd56cx125a0D+R0FUT/b9Y48aug84p6y" +
            "z3C2BNiVS1C/2LiriAJZ64cBhbO1r4j6J5GZnLK52a7rWnpdITrgoPdbk6osNw3KxokojysbUo66KMDqbrKeJh7No8XEj7n+BZ0j8A0YUTXyj8I8muJCQ8nB" +
            "mkT+QuLGlI+W3gkahkL1+ZTdxqSUjpXr30LSViho6AWCqGPihhPP80BX3YDJP7QAa4x2Uh+9c0eGQpmMQp9bvgWg474nUSuMClKSNi1rSdxv4MmH6KqRNRiT" +
            "TtoGWXyDDF+VCFlLdHYYY/7CLhw33W69HEX3i1gJQ3wX3pnDCRtGPoRjfIDlFFVzjaCBGQXxPQJzKadGZDDqcYOzb/J8BRoGUBaiklpscDbaCXkt/ZOxl7Pw" +
            "teNDsR+i3zxV0VJzNh/ZSXOJXiQYx7KWOEcrjCGwfieAbceeai810yZViSPvjYk5INQPTNyJRm3ooRtqFVP5sic0Eiduj1qG0I2R0I0blB+bUO2I/uSUHhqg" +
            "G5gX3aQbWk0P7+XRaIVW7psYhTVC2HuDZmxsMup94rvSHMpVssjzg1BjoRseeYxDD3t4U4C13GSVW/j1It76xL1RG1ZKy7BhVlv4robzRVWb24DBymLzK0am" +
            "hxMHcyWR1pNuxEoP99L78qhNkpyIpASs70bqk6w2eWK0aDRa4yajnpWyeunugaG39JDoYY3v1JewzkSTv4v4YXvci3cnhpwxtSqN1KYaB6wd5EvAKPLv7eFt" +
            "aEL+cTxjrpizlDOVG6yJElUjjyDU3tiJhGbocwa6C3oo3+FiofN7KRsJ6ka8OzLyM+YSqi8yUozNVGjvfZyLoIW3BEHHm9JcD1blBMejRe+L0OTI893XuEc3" +
            "haG+JvHL0hDzb4179JIBoqkmC6Dup3wOOPKR71JFPCnJoj8YYO28HugvycLMyIx6VM6Xj5L0Hlo4josZaknN8hu5Mi/eCy86iwUrbyBixO+t+NfYjOJK5Xjr" +
            "6jregr2QGw2DYhxwHdNLUixslENviNhDus1YlLX+1ErpjXF98dsO9pBQD1O1C1zJvojDkiPokm6wnccYVkTJkT9Ot98x9HCwECx0FzKrxpY+BZ2nHqIGKyt6" +
            "nP1S2nh31+10F3SL3SUUf1eMOalbEvUw56zG2172886sNa1nwLTEWOC6FHvlwxwdjBZqgfuo70QpjYZAwQqTu8Nx8p4dtzEXMYzhwrQNtecxnmLRWKQXV7w7" +
            "ZSt6nHwdQQ9v9SkSYxrlWZNWWqOKqviiqWzvw7SWR7CWOxTnxfMlNcqdh5Vi9lUue7mnPMw7Eaxl9q93NV30FCqWKLG++J5t1IuHycloMVhka90V8xUsgGUT" +
            "uwN6c6aih8/Q7TfeQKWno/6gzxg1X2gRCZUQakI+6thDXikxvWTgjHU17ehaaS2BMUzkTmQEJOtzvJsPx8P0ZNqP+v5iJi1YGFC0EvRP5pGn7/oxWDa60+PK" +
            "HeZIcSp6gnHdYNKTNRxFREETUcfmVhRqRCjMaO57gUt3ZQHWfhVWZCFyE4r++LGwbDRFDfaapKyfnk8udJibWdvNgFWNCLVEN9KAwTh6YAj8apa2kPThHeVW" +
            "T824ETipkHWabm7x/pD7qDhVQPeAA6x0y2tGcKoWo+iNowcorDBi0ncxpxqs6KhFQlFWaEStUWoVkEaZ+JFTJa0IZUXdSGJo3SwOYD/jMfwK3RIjxsDM89jD" +
            "GueEBlYVAKonUJ2JL/NP48hTDLhPZreBHgHEqfDaFgbetd3mVm2FNVlwKkZ9ilFIoGxGIWMBToWo1guA2priVDzyH2AUbkENg2rejPF1CpPUu5Z3CVBy5bj1" +
            "gPKg/1h4wQHKozkTswzW0aP+2a82t0yFanBuctTDj5XoYerJO2LQw560xKkf9+R8PStRTR551MO0TTmGI6wxvDX0xC6d8u6S9bCBNWQyjUpQo8hJHbVXahTo" +
            "fJvn6wO0w6IW6qgUc9CTk7qOGrip0Ci+1SfNaLHvEfcQ5xkQ1D+PeTPv0AajvqFQ7FWL9TEFaihRXdRdYkeM+gq9ScFvt5VGUZZx1HnWQ7G7ABtIaQQZ9Ts6" +
            "qiZzkw+63YGuvRNfRzEzN3gjb5JG0btgSLrhtibNCc0yztigKTXqGL/eshY22QuOODCi8E0ONKrL2lufKNRericl9ZDroQA/wozG+Jw9AZzkBJHNEWqovY/L" +
            "lYJ6SL4ztEsjtwftxQq+Ug+D5kTIOj33ZWFtpm0bVg3A6DkefejhYkDMlHXjefZ15M2nOVY6n/aiwA23lB66fWBUapafJr8vWseNkbBRcSvCCg+U35klsdZH" +
            "fiJ0/mmuYm/xV0mdj7owhBrKVqi4h6g3ciZ5GIuRWJNke13hRt+RtpetL+vUj2FffoJfOEAzZO5mtr30jhpI28usakKZ639s7GJfYswJr1YKZf7GAEK0HA5X" +
            "E+bVjMwZUW9mO4+K3ZS7A2X/d/GlyPcxXpTZdiS5GKAO0w47auPJSmbW43dObPiYB6uszWsMWIvo4WFaKSPkPdp3of+FfGHD1ayzKkTdSfWX5erKviuyfX6K" +
            "6jIGexkZck+5k/fK9mgKlbaiFmN0FFbM5j3lGY5CwgOKZYgK4mM5X45lXWquTLMq0t6X5kR23AGOovBOpcwk6FBhty6BlVecSsTe/dh4lj3GI1opDRnzT5UG" +
            "8MID9pQVxancBseoIephRgW0WkQP4zbOFbAA3IkUpxq0pKxH2fsoIhvQ4FU5Zj8E/PFeaMO6NAXXcZD38foa8FcFtF/WDO0WB7blNliAmthhXZkvHHv4KMvy" +
            "aIdQ30UPiF3a9QSrCuYnoiIHop5iWd0MBeuS72O6AfaPUfNd5KWEuip4VKL5mc/mUVcFj0q0DIizedRVwaP60nN5rgqPuip4lPDgFOtrFo+6Cvbwa7KHwgNx" +
            "No9C1O9Ij1vlFTyLR10VtjeRnoTNKjzqquBRqZZtcTaPwvn6iPTvxbsSi1+oYx9vjJAV+biDiRNcZHEkLc7XJzNPXfYKnsmjcL7ey1kTm4Qh/1443+8J8XYK" +
            "9q8L2v7lz0d2LFCPcN6upuohnQlC4TizoaMsifJETsLMY3w2j8KRX89mWfiZz+JRV4ERPUIa1Rd3aVV4FKI+Rqgwp4c78airYAF+XfjB0ixX4lGIel56jIvo" +
            "rtk8CjXqN3WNalbhUYj6ppAlvNor8KiCHuL9PfD5OKCbS9/3UrCA+t0Ur0qP+LzQQvL9xlNw3EsQFfjtFHRjwNo7z3dTrFEfltrbrMqjEPWr26LQLbCAamZr" +
            "Ocw00ZzNo64KHtVnlGk0q/Coq8CITgs9jIUezuZRVwWP4h4yajaPugo86gMc/YEzXK/Go66K+6hI4irxqKvAox4Uq5LW5FwVHnVV8CgV+2FX4VFXgRGh73dK" +
            "ums0qvGoq4JHAaqmf9fOPOoqMKKPi5Uicy3O5lGIGqoean7mO/IoHPn3kAcXIaxqPOqquI+K8c5OQyV7yIq6Np581RnWEywFe/hu4XFH4y5x7b7bITvv2pPa" +
            "ytTtFKM+JFFNHZUEOFlgFe2t2pZ2O5WaPPKfET0cZbvDDB51FdjXl4nNjLH+t/CExz0leSqCn8FggL5KTbHH+inV8sIehiI/W66HIM3votss9lDsstp3DXSU" +
            "0MMY73+7hKIeirswi7/rGli2TwhfR/IYr1fhUddgX/5izs98No+6Bpbtg5l/r8WeurN41DXgUU8Kr0VZZ3s2j7omGBF5Bbdir+NU4VHXRMQTVTDEs3VQhUdd" +
            "E3dfI/JpR02vwqOuwf71vMx/2OpQtsXZPOqavLfBe7aesX+0WIVH4Xyd53sbrBG9J/ar8Cg5XzCw6BW4f7xYhUfhfNG9jQ1nxPZ4t/AKxnsAUA10aH9Vv8US" +
            "uTvwu86JXKvkdW9V4VE48t9QqET4mc/iUdeAET2hdD7TqJ151LUs3oH00Ler8KhrsMP+NkcutNDeVuNR14RfpUB51XgUatTvi3s8rNSIHvyzeRSiXmQPZER1" +
            "g6AKj1J6yH7mVHV8No9C1OdYD9sSNZtHoR4+If3MlUbN4lGI+gT3EPXwUNypwqNQo35TaBRXsKTImxk86prwnqXMuM3Y7s9X4VHXgBGdlbIwO4tVhUch6vOZ" +
            "FbVDqwqPugY86pSIJQhdp1ONR12DffnjmXe6W41HXQMe9T6ZlVTzM9+ZR10DHvUJzn3YSrBwZKcKj7omoj/yvt80Il5E7AZ3MGY2Kb0NMuqryvaGtvT/Rb3H" +
            "IAxsaG/k+7scjYN0U0FVVBGl6l/HKMvFWw7GREIW9/Cg6CHvKRkKdnMM+VCoAaGk3ThO1XIND3W3v1iNR8n5Akw7Phx2qvGoa8bjfLMEVn58eLyo/MwzHuVq" +
            "PMpJhcV+nG9TGbVfebVnPMrVeBSgWNZh8uLGNxT4+/1OpwqPuiai/Ggl9+JD/bDKfRT28FyG8jtZD/dQsVvY+TQe5chdj2tJSFlhX+NRiNJ51ELX5e+6bryF" +
            "7qNkbW5j+l1PY1E6j7punKQov7AZtWQ2uIxH6SxK51HXjV+me4C+lWWDQ93CrJHIo1BzizzqunE/7ctUiNRWspqw+8N6zHavaR513Vibe4FzEnqJxxZqFoti" +
            "1HfwuzxEhV4VFoWozxMjCkUxWF79oBldkLUtj7pufIssdsfCPD1y3CWL2o5HXQdGRBzbGfvQOGp05rseouilzB8H0HzDqMKjcOTpxOHEh+JDKsNru8MvN67V" +
            "utTcMvLveoh6IodCjRfvPZ7dmn7XYx6FevgxLYeRkXvXy1iUzqNwvr4qsguGGkfZiUXxfFG8eR34Wl1aa5wvrpct9yGLvE3k/oWop6dkUSxne9Cl2K9IoshH" +
            "ZRFnDKVdN77H8SnGojXdx514FKK+wXWuUFqrGo9CjXqBNUpIwsg9HDlkUdvxKET9J5IUtQVjE6iuQhV5FM4XvbXtHx0aCXut8SjFoqZ5FNqNiLU3GLuZbvC7" +
            "nv6qp7/rXTfeThZg3Il9rDfMVg00iuLuXPdCe0u9VrjzgT0hHoWoIaMOjf0MlQjUj8gaih1lftDi+saoUb+Wm+VZLIrniyN2uRisZCk786jrxov87mDo9jBR" +
            "PEqyKEtjUYz6RA6FVgp5lM6iLI1FIeoo3WKBonrSwxt3f9pMOtjYYktvpJR06roR0T0b7PZe5CkWhWPBbSB1nm3phO5trhtHaE/hjGyGejVHa80tY1822nm6" +
            "PbhunKI3Diqm7WYj6PNvgGFlnRf9O5MMuYeH6US/UOe/utp9FKIwzncB9pSXWhlq5/uo68bDdBrtUNHZjj217xFjY0wwdR+FqC8rVLaDiR5qkvT7qOsir/54" +
            "f3x6rJJxzeJR10WVbUB9enw6Q+3Mo64bn2MP5P3jM+O9EoW7SgfpK/KoVpFHIYp2otOAymQBO2LSC0ygVeRRqFE48skgOSdXJflzDgYUtNztaXc9xoo/acvv" +
            "+nXxXdBOS1l8H6XfRun3UTjyK7wTBfGtYyf7LkxEjDdLf9Iq3kch6kuMuhWak1mpFMwhorZaxfuoV42z5tvkmUievvB1qrzVYvTABdQ3zXerXARcJYNeBbZp" +
            "qNeIesa8V5xT4uxmyYy3QaGGMur+4n3UdtJqiUB903xMofwmxg2ktbQW18qy4mGmB+7hD8yPaWeHyCUL0MQ7yGKjuPw2ov7WPK1QwDIcYTkwx1pJk6gb5if5" +
            "rEenPZdsB/expDVlD8+aPZkfoBnn54sqF3ITGcbIVwNRd4q3Njwv0xkW/lbpH46v9grBqBqiHjOXVKR/v7Ewb6i8UGKGDD0/W9xgWc+Y71QoPI/mZznLmIZv" +
            "ZAnVAsf5Oig0Cm84cb7GMxqjDstZrkVNvyLqD8yj6hZL9nBs7tQQ9WfmQ+K+l1CuIf5t+4Yo9qym+im0b83CSNQpdR+F+1Y11C3EbYh/tsV9VAXU/cQqUQvR" +
            "tnsB68ZOjTUqyPSQNKpcA7PsrjxfauTRboj50nOscotkCgoxX29RNqpDOp/Ns47raNk7EXVrdsMp7g9Z08dTskKBYdRZKQsr+WBeAVt82baNv+ut6oaz3+Qv" +
            "m406af6yrHfV5LpGVVAr5hFeX2SxmbWNajs1tjZ3FvIeJDtoPFvRG+Z7xbkVLJRE7aBPbKPG5tvFbU/fxnwshrFzH3lP+XvzHtHDCNa/7OO2uwPlacZV+e7s" +
            "jshS91Hb9bDJsv7M/FWVFSdUd0TbWeyx2FP+2nyfdovlqPHYbkQY9X3zdnkTqPqXSEsILa+Licmod0qUJik2sxYaektrPPI9Uc8ktjvzEldi4cXKZFlj85h8" +
            "hbG1MZyy8VlLBGqDItRIlvqyWOynsdbTRFj7pMaoR8StYzbyiW5dGMMNUPK7/r2QBaxVm2VNjqHkGKnJ3ObvSZa0vZn+0p6rJBACG6F+ajxt3ke811dceQdu" +
            "Q7f4iPpj8ySfUzI2X9t+paCFRtTXzbtzd0Q78ShmRIi6tzpKMCLsIb5X+hbegvKeBxywGZfqO+XLaTNqKM5fgLJnoFoS9bJ5ls8OXqTyQM1iRIgqk5VsIysR" +
            "su4xbxM3gfIuEGc0x4PUGsNoOkR9k8YQbwKjFlcooTGc2o+y1ZWQFyWO/BE58o3pkc9y4mY7UTjHsr5u3lWYr9SUGV3z+aAxGy2i3md26VaEX+dmc5Sx0N4j" +
            "4tQmtbcKapn2SnzO+NfI+j3uYQPzl1dH/bH5qLg/jNo8HrP5xk+NH5kh6cagPfhXoF42qYoH3lV6XA9t3Ny5Iep/mY+TrADznVWW9TRxUS4JotvdPA/qaNwG" +
            "R+N2eQeroTLdKGNE+F1vFbLkrY3k5OMpPcwkMgpXSj8nS3JyXZqUyN+FJ0S/hU2dl2fO8lniNvp91M7MZiTWF8nS7kSqaNS3iG/gvY3MKpTsiGF7+C3zRE5W" +
            "siNXTgn1beIbU3dEO6ISgXpPQda2HAX3J0KdMx8UVY0SW93ObS+LuM1PjT80P0qrEi2bL7Nc7qC7aZNRH1EouZpxByvHJFS54qfAUnq0lvEpVEW3z+A2iLpL" +
            "WICOyn0xi9v81PiisFG+vaDJik2d0fQ1jefd/A+J2/gN0N6GQonM14l2DpU6nxrcQ4oMmsdqBPLLeCwUc6AWGdy4hzfIRnV4h5VZwhDBjRCpaIiaEOovSaMW" +
            "LXyF6euyFLMpchtEnSTLRi8+1tR85XhXxm1uGMc45ydnnqw5MtOabfH7spYXC/3pU4rlvCF8HUc15svihb4duXQjrVXKopyE4tXshvEBPo0Kr4+FOrMA/Kst" +
            "x9JycKEPSOD6NsvibOF0R1STlTL4Bdx1bB3lu07gse/BDeN58u4bU75KHkPifHAGDnytfxH1r8X32DeEz+1Yy3IZ8/tXEOTqf0VdfslG1Isiv+h0lsukFcBH" +
            "eJk03w0GXVmF6obKFs7+NllOzaDrBVM1W6dQp2UcInp9OOS/ga9mfhj5YcD3yyTLE/OVmIxaV95HIidhC30+osiHb1Ezhte/XtDi1woc+SzL5ZLTd8TNrYee" +
            "ACpPIGU/sz0L49qohwN6GSFZWU7CdtIlFPRPzVgAE996SfXwnOYF54tsq4O2yzoIZK5BlXkxsGPQTtQsnyvkMkUmjZ4YblZ5reNijgY1yzL7NN3bWB31zhmg" +
            "0QotWb0uhP+r4wdJe1xj1FDpfGj5rnw3Q9RU1bvQ9eWMYQ+/Ju5t0Ndj6tUMnyKyWrkDvYcvc1wbnbOBE3nyvWLSxvRvnswEh7kP27pG/ZqmUfzOgRqFb226" +
            "RkHveomG+oa6/9dQbXw3m0ZldehwviJZz7c1dvF2GeNMki7luOvgmFsdWpWuSxoV63ooUb54NcPMeKSHLhN91F5voulhqG6W+pQBVbyoOIEjclyKfH+2TXFt" +
            "JqMiWYu6NYVyNe1llPeSQnEdhOnbHtREHHnsmZxlz6exV3r4GXUTGAvGzDd1g8DVawD6+ix/lP1t+N7Gkh53sOD9TAtJD2E0I6WHT7Me0p05vjJLXxs3oK9S" +
            "NazxtTny2KvqhvEMrWX00elkp+WW7/Jb23TVRqwKlAjUZ5Uvj7pzoPXlRdMotPWRhyzlhnGnsPOY5dLJslxS3hocda2+ni8rX98wnhW1uVF3M3+gpM3eh9mO" +
            "gi0IpB7ex/kqc1ku5btZV/vBf+e6gYh6viTLJaO6uZ8MdVBam5ru6UReJm6Q+8FtkOfrKcokT3dEU/5R+OJOPhLs7kQJFHGNyfn6aJn3ke9jUR595HmN2bxS" +
            "eL7Ib+Yw++iQ7b3Vp+OEhsJLCRdXGI/8MueeVV56vJrFSkZ5HbHDgiSOHkQUxy+PeoDy9Wg4+c5JUsiVxhUeQXJfFlkuhc8SeVXAXhRQ6+IjLXkipRrqOeER" +
            "RNJCQ+bVgI2YcvBGNIaBmK2a7OFIfldP9ZCi4bAqmifc03DPc9uBiIa7obKFGzZmTOjL8WiBlQoGJA0sN6DA/HoDIesB8/yuw4Wsbi8fWN27enKVs5+ZL++L" +
            "cnndHjD/dFe3kJFs7eD5k+cJdd48X1t7U+TlUa/s8vO5Mbtry6snGycbZsNcra0tp4V8eojqFlAXl1cRR9kPL5aiXivk4ArdjYNrB9bPrA/Xa2u1jTeFbhnq" +
            "zmK+yoNrZzJU5BVRexp3l2TUXBeo9drFN5X1cE/jXSWZDC9lqFNlqPOFjGSJ/9yHKbsg5haD8Xi5kL0T5+tIEfWmtX1rB1Z3Y8a/VfMnB4rz9Xwhk2Ey+MIp" +
            "a7lxpnGIUa/sG3gV5mvw3Kn6cuPAaltg2sXvKvYwcn9yEPXws8PPkh7+5E1RUJyvQpZLd9I9vwxaODxfO1+b9MKgbOR7Jfn0QONPNlDnYeSjUlRJFr6DqIer" +
            "ErVSRP1Ko5DX0Z30LuIcD9fNi9hDtwz1YIluCFTt4j5AlejGHY37yzUKeoj5NCfDsh5+vJHPBol5E7d666e5j1u9Qake5oco9J97cm25TlkuwQbU7j7ou0XU" +
            "3QU9xCyXa5jlkrS3mL8T9fCOgh6irMbpRo/18NKBfB8RdU8BdYFQqwL16sEi6rnGrcXR6JJ2kD2cDP2S+frvBd0AFIw8o145AiNfYjdONIpW9OI+wBz5LMkq" +
            "16gTjaN5FPfvCPfwJ2fLUF8r2Hmw2PvPKzt/3lyrRd0iaqkga41WitwdylBfLqLci/qeAhaxuKd8v5Adl1blsobqFW3U13Y9WG57WXvRjjaLPdwoZFtNuxtn" +
            "Lgrbe7G20UxLLMD3C3mJMZvxpTPQhpgB+VLt4rD4XT/bdTyfUbNHGIlqTkrmK5+VNPa/9zha+ca+1TfgOnml9li7uFLuKa6U4MLZNc5y2XqFMwy38qjjJTmQ" +
            "nzu1dor2hybmdn3sgG8XUXeUo5bXD8P6gh7+pFdEFbOSJsEa9HD9AGWexOzO+wK7OF9F1Etn4ct6a5Tl8mLpnvL9Yp7b4O/O0m5+eB1zftZeLenhz3blrWg6" +
            "vHCWMG2ar30rBVmvwcmX89yG9GIj0o23nZIf300/HT/FKKcmZYWqWpPXdtt5BBBaL/60lPWzuXsVStZrQke7gig37LIkRj2oUCFHqoKskj66SS9DvSo8JLmG" +
            "EudL6bYDWRYja146TM5J1JHaJ5WsRMSOr7QH3iBXkh7O8+eyMfyl2ocLPey2u+1pSYAaxgMd9RG5Grk61LYoXRZXDRvLiOca1vPs+vzjsHs1Hho8u4W1ULZW" +
            "MK/+a7KinBYP3/W7Po8a5TbFiiu2B5hL9soKV0JB1PsNraIc7g2dbgeOROR4T1VeAItlGtZu2rjb9yXqSWO6et1KZ0X0zxGVV6i0RmutNRkMBOoYnUb1PBX6" +
            "XaPe3EDWNEHU0JiuXleOwfPDRFSgeM1YElkF9Lp8cOLKNzqBBd2JkPUBuj2YziqwTR9VtQucr+8Y09Xr0De62FYGE666cprH8Bu5rAI5BaRAGr16CqI4vpKy" +
            "W4j6WkUUndwQdVaiXqQbMyWtWSoNZK1oVVdw5J8QL0ois4BBme5YaR1xm+3adoN0aqsxaPB3iTpZnFUA7+oxtLLjYCY/rhqErv5TVVdeMwYUGSQkiUhkn3Au" +
            "NfK+d0mmtyZwiHpS+LwYKkodUUGH6pxzPZNc1ZXXRPazcKqeZ5luuD7V1TgQ7+dZfs4QdShVpoqSGYNxHOAs0yi+ZtxNPRR16IzxNhpFX+fLqiuI+lgFlM21" +
            "WpQevsRZILJYf7QB0nDkfqQdRSv6IYUaG4awon7RirppO7Oi3+UsfLJ6nYi+97qOP93wJ+pKjfobUYGd15esRBfMkPXPooY115WUFfbgy4LpFgVgswcZ6nGJ" +
            "Utk+EBXkUd3M0mMPPyFGYyzywEjU9A/Gt2WydtfOqFevrHpotxcUfvQePiOq5Iy1rAIBaK8TOpn2YnNsd63NVVcQ9VQWsy9QXVwpdBeaNTuwvXVADQaIupNG" +
            "niqoYq4E8UqH61lYbAdXlyUqbE1srG7wmvG4tPO8mkU8TEChiy4n8JNWAGRtkTfka8YPRCWUsZbhBvewwSCSw8CVxmAn3PL4nIOoZ8Xdn7AcjELfby6EwleH" +
            "wAr+BFE9qYcDiVIYlBVQMJyo8NK1ob0AqKArZ/lTOkqMIt1EaT/d7p/0Jm15DtsEHnVcegHU+sJDchaP2gQedUK95+Nd8WJ9No/aBEYk2Rfem/cph8EsHoWo" +
            "ExkKK+zZs3nUJliAJw3pZ9tpsNfSLB61CTxqoH0X14aaxaM2gRF9NEOJUZzFoxD1pERxbgZrNo/aBIv9Be1lBN/aZvOoTVErkzAtrpg9m0challGc7c4mmA2" +
            "j9oUFYcx97WsUDabR23C/vUZ3b/XqsKjEKUqeWH1OrsKj9pUeSrYH5tv2mfxqE3gUaNCVoFZPGpTVZSjVx4P68bM5lE4hv8xl1VgNo/aBB71B6xNgMIabVV4" +
            "1CbwqP+QvSHa20jL8ahNwXspu6jQw9k8Cr/r0ezNd3c1HrUJjOhxkcEgq0M3i0dtypp3Nt3ra6ideNSm8HMQ73poAazZPApn+XeV9pLdsGfzqE1gRJ/Q/BzY" +
            "Y2EWj0JUVAE1zaM2gUedlu8aKqPLLB6FVjRUL8VJq2NV4VGbsH+dFH7b+P614FThUZuwf30oe5VuLcxX4VGbxv/I7Dx6Mc1X4VGI+oSSFbc681V4FI7h05ks" +
            "sNfcR3r725ZJbRr/MqfZ+abcY+HLutvzqE1gRI8X3vVm8ShEfa6QVWAWj9oU1QZpde3pL3Q6VXjUJvCop+Sr2Z7YkaidedQmMCL2ZqEdrA2WozObRyHqGyLi" +
            "lvIRuH1/No/aVPWXwUbBiMjx2JlHoR5+VsrCzAedKjxqYvw9ZdPSY1SBo3g7sShEvbUm36T6DY4CJxblbs+iEHWThgolznN35FGIeneGsqW0MvaV8aiJ0VK3" +
            "PZ16R0SPz+JRE+OR2qezdwp0ySEehUxqex41MY6p+6hFQCw2WNbOPApRkn114Ls6O6B0WVx9Vc8qMJtHTWD/wu+KndiFNl+NR01gh+XaVSDtkIzKBB7V6bAd" +
            "LOVRiHqCUYdihVrBgq078KiJcZJz96ETlDg3zOZRiPoco1o7o3QeNTE+TGfzTC9Kd8ocj5oYnyffHt2/lwKFS5qLFXPPJmcQtTb3X1V2C8TNZlGI+h75VOhZ" +
            "BWbzqInxLcp1jx6S4Xbcq8CjJsYrFOerf1mprBWdR+HI022PM3blHGc8SmNRUzwKv4tvD8Y3g0ZV5FH4XZSvsjPmLFACsxOLYtQnOVZaoIh5hQGjSllU9l36" +
            "LJdoB3qKaDwKZxnHELSwFYpXk1ksitfyubxGhcVmhVZkRcEKzxiihhVQNqEGCvU/2dcRPehd9hedxaIQ9fO1R5XtTYQH4iweNTHM2gekDbVC4ce9M4tC1Jtr" +
            "H5Qo6v5sFoWof56TPVys9+3YrsKjECUD4ftWrKF24lETYJXsOdOvJ2rsixxq+j5qYrylFulj2BC3UTuwKEQ9wxVD9oMNVVkFZvEoRGFn4/3xU7FCzeJRE+Nh" +
            "ztzlxL5cy2znHWWxaX1N8aiJ8TmRISSL2EdZO/MoHMNV5tjzY9iLOLfIziyKUb/JdsOC/csTqIGiUSUsClE/YG9MYwkzBARyt5Q8ys14VDvjUSjri2LXgy+7" +
            "VXIv8ovalkf9i3FwDl+ll+pLtKNgzi3f6VgdzDJSH9fHc+M5uhnHSHKKt/IFiiKsG2PSikl3EJBLGiz5uA6/O4e3QFTJawp1eO7t01yj68w78yCt3qkDH5vr" +
            "I45j1o0+lUtg1LumUCtdSisIKOQ5/bl4CjUQqIOU3WKpHtv4ZZNgQm6siZtAD6GPNdHD2nQPj3OuKoxPa4Kk3koP696BQUJZcxxRIWN3+pSmHlF30I2Z3sMA" +
            "FRBwIeDihogEyfXwg5S/N8uLBSgrgO+K4O9JGhwxmcWDStSH5/DdfLQbG9mchT6ctccd2JluHu8ev260a7SLK5cgMjQ8gXoXocYS5ceAijuEwd/dhXkSGBUr" +
            "1ANzb2ON3zMi7hU5fb/fiRfGC8Cs5klWU+oGzpctUO+YRvmE6gAGUTcxaiRQgUDdoOiqRTGCFiiGM8/OwB34vX5tqaaiggz8rkCgHqAx7AgUEHlXokA3snE3" +
            "eQwZZcyhh8niXF+g8MDlYEIclDQnZY3UyLNu7J67Tzs3WIKF4lrBM0G/IXRKxBhKWQcps9DSHFtQstMiFAP9jSk6U0YFmRj/IdfXpzS7K/xWBSrJo0Q9KdTD" +
            "M5pGkX+nG7BzdCvEqomsvxR3MlEadQedK3UUmhdERVi3i2rj6ahArJS3E48akU3rdBY7i07fhWbhWPRZe1XUvy806jh5EYz2SFTYCTNUsy/iisciCq8rUB+c" +
            "o+wW86ObGNVxQJZDmZB4vpRe9I2O0RUa9Sml88y9QJrfd0iWzdJkbgxd1pvJHvIJiuYZ3bId3LCA0+KXzY1rWQ6PSIz8m8n3IGM2yLMIBbSaUE22USOSJ1G7" +
            "59DrA3ojUA6G4COlpGhB0KiGtKNcNYx1Yz+tL6mHgKIf0kNb10O2iFI3jpMsXBV0xx6Q5Z3vYI4ysL3xXCx0fmQsGR+iYjqMOqGfKAMHV+U8YsJ6wii1Vh5R" +
            "Y8iy5Kl3JXBpLUP/6v16nzAoBxvWHPyQknWcUaTzYA1df55taF9JYtwYMB8XqF+iyoZo5yUbmgThfILNAsvblGMBmmB8QURKIuphkqWjIgcxgGrhC4vcv0bG" +
            "RQO1HlE+7XqI4rGfBKlDqHnYU3SMibJ+VcjylSyJ0mRxfLZC/bnq4RHSQ7Dyh6XFDjv9Tt+J5+N50HTcm2tSC3+Zihghan2uyznTDgjPnjBeBMtLFntkjerS" +
            "XiOKZ4st9kFtpcCJwwFJC9CcJWuJOIDUjCXj34E0lvUE6aFxQMpKwrgfL45xL3LGrxvvGvPuQDvsy8avCN04xa+3N48OjA8IVBifBOZ3c3zL+N9Aa9HuUJvW" +
            "qHWqQTNakPtXGiWdBHaieHe8G+szijGEr/pH7bueIFnwXWIMkyih8QBpGUowgA8qWU9wLm6FSqM0gi/br6PGOdT/Aw==";
        private static readonly object TemplateLock = new object();
        private static List<OcrTemplate> _templates;

        public static OcrRecognitionResult Recognize(
            GrayImage image,
            string languageHint,
            double minimumConfidence,
            Stopwatch stopwatch,
            int deadlineMilliseconds,
            System.Threading.CancellationToken cancellationToken)
        {
            var result = new OcrRecognitionResult { Status = "lowConfidence", Text = string.Empty };
            if (image == null || image.Width < 20 || image.Height < 20)
            {
                result.Status = "unsupportedPageComposition";
                result.Warnings.Add("The raster image is too small for full-page OCR.");
                return result;
            }
            if ((long)image.Width * image.Height > 40000000L)
            {
                result.Status = "resourceLimit";
                result.Warnings.Add("The raster image exceeds the 40 million pixel OCR budget.");
                return result;
            }

            try
            {
                List<OcrTemplate> templates = GetTemplates(languageHint);
                bool[] ink = Binarize(image);
                SuppressBorderNoise(ink, image.Width, image.Height);
                ink = DeskewConservatively(ink, image.Width, image.Height);
                List<int[]> lines = FindLineBands(ink, image.Width, image.Height);
                if (lines.Count == 0)
                {
                    result.Warnings.Add("No text lines were detected in the page image.");
                    return result;
                }

                var lineGlyphs = new List<List<OcrGlyph>>();
                var allGlyphs = new List<OcrGlyph>();
                foreach (int[] band in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopwatch.ElapsedMilliseconds >= deadlineMilliseconds) return DeadlineResult();
                    List<OcrGlyph> glyphs = SegmentLine(ink, image.Width, image.Height, band[0], band[1]);
                    foreach (OcrGlyph glyph in glyphs)
                    {
                        Classify(glyph, templates);
                    }
                    glyphs = RefineGlyphHypotheses(glyphs, ink, image.Width, image.Height, band[0], band[1], templates);
                    ApplyLineCaseContext(glyphs);
                    foreach (OcrGlyph glyph in glyphs) allGlyphs.Add(glyph);
                    if (glyphs.Count > 0) lineGlyphs.Add(glyphs);
                }

                if (allGlyphs.Count < 12)
                {
                    result.Warnings.Add("Too few glyphs were segmented to produce reliable full-page OCR.");
                    return result;
                }

                Dictionary<char, OcrTemplate> adaptive = BuildAdaptiveTemplates(allGlyphs);
                if (adaptive.Count > 0)
                {
                    foreach (OcrGlyph glyph in allGlyphs)
                    {
                        if (glyph.Score >= 0.97) continue;
                        char character;
                        double score;
                        MatchTemplates(glyph, adaptive.Values, out character, out score);
                        if (score > glyph.Score + 0.015)
                        {
                            glyph.Character = character;
                            glyph.Score = score;
                        }
                    }
                }

                var text = new StringBuilder();
                foreach (List<OcrGlyph> glyphs in lineGlyphs)
                {
                    foreach (List<OcrGlyph> segment in SplitAtLargeGutters(glyphs, image.Width))
                    {
                        if (text.Length > 0) text.Append('\n');
                        var lineText = new StringBuilder();
                        double medianWidth = Median(segment.Select(item => item.Width).ToList());
                        for (int i = 0; i < segment.Count; i++)
                        {
                            if (i > 0)
                            {
                                int gap = segment[i].X - (segment[i - 1].X + segment[i - 1].Width);
                                if (gap > Math.Max(2.0, medianWidth * 0.40)) lineText.Append(' ');
                            }
                            lineText.Append(segment[i].Character);
                        }
                        text.Append(CorrectRecognizedLine(lineText.ToString()));
                    }
                }

                List<double> scores = allGlyphs.Select(item => item.Score).OrderBy(value => value).ToList();
                double average = scores.Average();
                double lowerDecile = scores[Math.Min(scores.Count - 1, Math.Max(0, scores.Count / 10))];
                result.Confidence = Math.Max(0.0, Math.Min(1.0, average * 0.75 + lowerDecile * 0.25));
                if (result.Confidence >= minimumConfidence)
                {
                    result.Status = "recognized";
                    result.Text = text.ToString();
                }
                else
                {
                    result.Warnings.Add("OCR confidence " + result.Confidence.ToString("0.000") + " was below the required " + minimumConfidence.ToString("0.000") + "; uncertain text was suppressed.");
                }
                return result;
            }
            catch (PlatformNotSupportedException ex)
            {
                result.Status = "classifierUnavailable";
                result.Warnings.Add("The System.Drawing classifier is unavailable in this runtime: " + ex.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Status = "recognitionFailed";
                result.Warnings.Add("OCR recognition failed safely: " + ex.Message);
                return result;
            }
        }

        private static OcrRecognitionResult DeadlineResult()
        {
            var result = new OcrRecognitionResult { Status = "deadlineExceeded", Text = string.Empty };
            result.Warnings.Add("The 100-second OCR soft deadline was reached; uncertain partial text was suppressed.");
            return result;
        }

        private static List<OcrTemplate> GetTemplates(string languageHint)
        {
            if (_templates == null)
            {
                lock (TemplateLock)
                {
                    if (_templates == null) _templates = BuildTemplates();
                }
            }
            if (languageHint == "en") return _templates.Where(item => item.Character <= 127).ToList();
            return _templates;
        }

        private static List<OcrTemplate> BuildTemplates()
        {
            var result = new List<OcrTemplate>();
            byte[] compressed = Convert.FromBase64String(EmbeddedPrototypeModel);
            using (var input = new MemoryStream(compressed, false))
            using (var inflater = new DeflateStream(input, CompressionMode.Decompress))
            using (var reader = new BinaryReader(inflater, Encoding.UTF8, false))
            {
                if (reader.ReadByte() != (byte)'O' || reader.ReadByte() != (byte)'C' ||
                    reader.ReadByte() != (byte)'R' || reader.ReadByte() != (byte)'2')
                {
                    throw new InvalidDataException("The embedded OCR prototype model has an invalid signature.");
                }
                int width = reader.ReadByte();
                int height = reader.ReadByte();
                int count = reader.ReadUInt16();
                if (width != NormalizedWidth || height != NormalizedHeight || count <= 0)
                    throw new InvalidDataException("The embedded OCR prototype model dimensions are invalid.");
                int wordCount = (NormalizedWidth * NormalizedHeight + 63) / 64;
                for (int item = 0; item < count; item++)
                {
                    var template = new OcrTemplate
                    {
                        Character = (char)reader.ReadUInt16(),
                        Aspect = reader.ReadUInt16() / 1000.0,
                        Foreground = reader.ReadUInt16(),
                        Bits = new ulong[wordCount]
                    };
                    for (int word = 0; word < wordCount; word++) template.Bits[word] = reader.ReadUInt64();
                    template.Holes = CountHoles(template.Bits);
                    result.Add(template);
                }
            }
            return result;
        }

        private static bool[] Binarize(GrayImage image)
        {
            int[] histogram = new int[256];
            foreach (byte value in image.Pixels) histogram[value]++;
            long total = image.Pixels.Length;
            long sum = 0;
            for (int i = 0; i < 256; i++) sum += (long)i * histogram[i];
            long backgroundWeight = 0;
            long backgroundSum = 0;
            double bestVariance = -1;
            int threshold = 180;
            for (int i = 0; i < 256; i++)
            {
                backgroundWeight += histogram[i];
                if (backgroundWeight == 0) continue;
                long foregroundWeight = total - backgroundWeight;
                if (foregroundWeight == 0) break;
                backgroundSum += (long)i * histogram[i];
                double meanBackground = backgroundSum / (double)backgroundWeight;
                double meanForeground = (sum - backgroundSum) / (double)foregroundWeight;
                double variance = backgroundWeight * (double)foregroundWeight * (meanBackground - meanForeground) * (meanBackground - meanForeground);
                if (variance > bestVariance) { bestVariance = variance; threshold = i; }
            }
            int midtones = 0;
            for (int value = 24; value <= 231; value++) midtones += histogram[value];
            if (midtones * 100 < image.Pixels.Length)
                return Binarize(image, threshold);
            return BinarizeAdaptive(image, threshold);
        }

        private static bool[] Binarize(GrayImage image, int threshold)
        {
            var result = new bool[image.Pixels.Length];
            int dark = 0;
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = image.Pixels[i] <= threshold;
                if (result[i]) dark++;
            }
            if (dark > result.Length * 55 / 100)
            {
                for (int i = 0; i < result.Length; i++) result[i] = !result[i];
            }
            return result;
        }

        private static bool[] BinarizeAdaptive(GrayImage image, int globalThreshold)
        {
            int width = image.Width;
            int height = image.Height;
            int tileSize = 64;
            var result = new bool[image.Pixels.Length];
            int dark = 0;
            for (int tileY = 0; tileY < height; tileY += tileSize)
            {
                int bottom = Math.Min(height, tileY + tileSize);
                for (int tileX = 0; tileX < width; tileX += tileSize)
                {
                    int right = Math.Min(width, tileX + tileSize);
                    int minimum = 255;
                    int maximum = 0;
                    long sum = 0;
                    int count = 0;
                    for (int y = tileY; y < bottom; y++)
                    {
                        int row = y * width;
                        for (int x = tileX; x < right; x++)
                        {
                            int pixel = image.Pixels[row + x];
                            minimum = Math.Min(minimum, pixel);
                            maximum = Math.Max(maximum, pixel);
                            sum += pixel;
                            count++;
                        }
                    }
                    double mean = count == 0 ? 255 : sum / (double)count;
                    int contrast = maximum - minimum;
                    int localThreshold = contrast < 24
                        ? globalThreshold
                        : Math.Min(globalThreshold + 28, (int)Math.Round(mean - Math.Max(8.0, contrast * 0.12)));
                    for (int y = tileY; y < bottom; y++)
                    {
                        int row = y * width;
                        for (int x = tileX; x < right; x++)
                        {
                            bool isDark = image.Pixels[row + x] <= localThreshold;
                            result[row + x] = isDark;
                            if (isDark) dark++;
                        }
                    }
                }
            }
            if (dark > result.Length * 55 / 100)
            {
                for (int index = 0; index < result.Length; index++) result[index] = !result[index];
            }
            return result;
        }

        private static bool[] DeskewConservatively(bool[] ink, int width, int height)
        {
            var samples = new List<int>(30000);
            int stride = Math.Max(1, (int)Math.Sqrt(Math.Max(1.0, ink.Length / 30000.0)));
            for (int y = 0; y < height; y += stride)
            {
                int row = y * width;
                for (int x = 0; x < width; x += stride)
                {
                    if (ink[row + x]) samples.Add(row + x);
                }
            }
            if (samples.Count < 200) return ink;

            double bestAngle = 0;
            double zeroScore = ProjectionScore(samples, width, height, 0);
            double bestScore = zeroScore;
            for (int step = -4; step <= 4; step++)
            {
                double angle = step * 0.5;
                if (angle == 0) continue;
                double score = ProjectionScore(samples, width, height, angle);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAngle = angle;
                }
            }
            if (Math.Abs(bestAngle) < 0.4 || bestScore < zeroScore * 1.06) return ink;

            var output = new bool[ink.Length];
            double slope = Math.Tan(bestAngle * Math.PI / 180.0);
            double centerX = (width - 1) / 2.0;
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    if (!ink[row + x]) continue;
                    int targetY = y + (int)Math.Round((x - centerX) * slope);
                    if (targetY >= 0 && targetY < height) output[targetY * width + x] = true;
                }
            }
            return output;
        }

        private static double ProjectionScore(List<int> samples, int width, int height, double angle)
        {
            var rows = new int[height];
            double slope = Math.Tan(angle * Math.PI / 180.0);
            double centerX = (width - 1) / 2.0;
            foreach (int position in samples)
            {
                int x = position % width;
                int y = position / width;
                int targetY = y + (int)Math.Round((x - centerX) * slope);
                if (targetY >= 0 && targetY < height) rows[targetY]++;
            }
            double score = 0;
            foreach (int count in rows) score += (double)count * count;
            return score;
        }

        private static List<List<OcrGlyph>> SplitAtLargeGutters(List<OcrGlyph> glyphs, int pageWidth)
        {
            var result = new List<List<OcrGlyph>>();
            if (glyphs == null || glyphs.Count == 0) return result;
            double medianWidth = Median(glyphs.Select(item => item.Width).ToList());
            double gutter = Math.Max(pageWidth * 0.075, medianWidth * 4.5);
            var current = new List<OcrGlyph>();
            current.Add(glyphs[0]);
            for (int index = 1; index < glyphs.Count; index++)
            {
                int gap = glyphs[index].X - (glyphs[index - 1].X + glyphs[index - 1].Width);
                if (gap > gutter)
                {
                    result.Add(current);
                    current = new List<OcrGlyph>();
                }
                current.Add(glyphs[index]);
            }
            if (current.Count > 0) result.Add(current);
            return result;
        }

        private static void SuppressBorderNoise(bool[] ink, int width, int height)
        {
            int marginX = Math.Max(2, width / 100);
            int marginY = Math.Max(2, height / 100);
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < marginX; x++) ink[row + x] = false;
                for (int x = Math.Max(marginX, width - marginX); x < width; x++) ink[row + x] = false;
            }
            for (int y = 0; y < marginY; y++) Array.Clear(ink, y * width, width);
            for (int y = Math.Max(marginY, height - marginY); y < height; y++) Array.Clear(ink, y * width, width);

            var noisyColumns = new List<int>();
            for (int x = marginX; x < width - marginX; x++)
            {
                int count = 0;
                for (int y = marginY; y < height - marginY; y++) if (ink[y * width + x]) count++;
                if (count > height * 55 / 100) noisyColumns.Add(x);
            }
            foreach (int x in noisyColumns)
            {
                for (int y = marginY; y < height - marginY; y++)
                {
                    for (int dx = -1; dx <= 1; dx++) if (x + dx >= 0 && x + dx < width) ink[y * width + x + dx] = false;
                }
            }

            var noisyRows = new List<int>();
            for (int y = marginY; y < height - marginY; y++)
            {
                int count = 0;
                int row = y * width;
                for (int x = marginX; x < width - marginX; x++) if (ink[row + x]) count++;
                if (count > width * 80 / 100) noisyRows.Add(y);
            }
            foreach (int y in noisyRows)
            {
                for (int dy = -1; dy <= 1; dy++) if (y + dy >= 0 && y + dy < height) Array.Clear(ink, (y + dy) * width, width);
            }
            RemoveConnectedNoise(ink, width, height);
        }

        private static void RemoveConnectedNoise(bool[] ink, int width, int height)
        {
            var visited = new bool[ink.Length];
            var pixels = new List<int>(256);
            int borderX = Math.Max(6, width / 40);
            int borderY = Math.Max(6, height / 40);
            for (int seed = 0; seed < ink.Length; seed++)
            {
                if (!ink[seed] || visited[seed]) continue;
                pixels.Clear();
                pixels.Add(seed);
                visited[seed] = true;
                int minX = seed % width;
                int maxX = minX;
                int minY = seed / width;
                int maxY = minY;
                for (int head = 0; head < pixels.Count; head++)
                {
                    int position = pixels[head];
                    int x = position % width;
                    int y = position / width;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx;
                            if (nx < 0 || nx >= width) continue;
                            int neighbor = ny * width + nx;
                            if (!ink[neighbor] || visited[neighbor]) continue;
                            visited[neighbor] = true;
                            pixels.Add(neighbor);
                        }
                    }
                }

                int componentWidth = maxX - minX + 1;
                int componentHeight = maxY - minY + 1;
                bool nearBorder = minX < borderX || maxX >= width - borderX ||
                    minY < borderY || maxY >= height - borderY;
                bool borderRule = nearBorder &&
                    ((componentHeight > Math.Max(40, height / 18) && componentWidth < Math.Max(12, width / 80)) ||
                     (componentWidth > Math.Max(80, width / 5) && componentHeight < Math.Max(12, height / 100)));
                bool isolatedSpeck = pixels.Count <= 2;
                if (borderRule || isolatedSpeck)
                {
                    foreach (int position in pixels) ink[position] = false;
                }
            }
        }

        private static List<int[]> FindLineBands(bool[] ink, int width, int height)
        {
            int left = Math.Max(1, width / 100);
            int right = Math.Max(left + 1, width - left);
            int minimumInk = Math.Max(2, width / 700);
            var bands = new List<int[]>();
            int start = -1;
            int gap = 0;
            int allowedGap = Math.Max(3, height / 400);
            for (int y = Math.Max(1, height / 200); y < height - Math.Max(1, height / 200); y++)
            {
                int count = 0;
                int row = y * width;
                for (int x = left; x < right; x++) if (ink[row + x]) count++;
                bool active = count >= minimumInk;
                if (active)
                {
                    if (start < 0) start = y;
                    gap = 0;
                }
                else if (start >= 0)
                {
                    gap++;
                    if (gap > allowedGap)
                    {
                        int end = y - gap;
                        if (end - start + 1 >= 3) bands.Add(new[] { start, end });
                        start = -1;
                        gap = 0;
                    }
                }
            }
            if (start >= 0) bands.Add(new[] { start, height - 1 });
            return bands;
        }

        private static List<OcrGlyph> SegmentLine(bool[] ink, int width, int height, int top, int bottom)
        {
            List<OcrComponent> components = FindLineComponents(ink, width, height, top, bottom);
            if (components.Count == 0) return new List<OcrGlyph>();
            double medianHeight = Median(components.Where(item => item.Height >= 3).Select(item => item.Height).ToList());
            double medianPixels = Median(components.Where(item => item.Pixels >= 3).Select(item => item.Pixels).ToList());
            var groups = new List<OcrComponent>();
            var attachments = new List<OcrComponent>();
            foreach (OcrComponent component in components)
            {
                bool small = component.Height < Math.Max(3.0, medianHeight * 0.48) ||
                    component.Pixels < Math.Max(3.0, medianPixels * 0.16);
                if (small) attachments.Add(component);
                else groups.Add(component);
            }
            if (groups.Count == 0)
            {
                groups.AddRange(attachments);
                attachments.Clear();
            }

            bool combined;
            do
            {
                combined = false;
                for (int leftIndex = 0; leftIndex < groups.Count && !combined; leftIndex++)
                {
                    for (int rightIndex = leftIndex + 1; rightIndex < groups.Count; rightIndex++)
                    {
                        OcrComponent left = groups[leftIndex];
                        OcrComponent right = groups[rightIndex];
                        int overlap = Math.Min(left.MaxX, right.MaxX) - Math.Max(left.MinX, right.MinX) + 1;
                        int minimumWidth = Math.Min(left.Width, right.Width);
                        int verticalGap = Math.Max(0, Math.Max(left.MinY, right.MinY) - Math.Min(left.MaxY, right.MaxY) - 1);
                        int unionHeight = Math.Max(left.MaxY, right.MaxY) - Math.Min(left.MinY, right.MinY) + 1;
                        bool nestedOrBroken = overlap >= Math.Max(2, (int)Math.Round(minimumWidth * 0.58)) &&
                            verticalGap <= Math.Max(3.0, medianHeight * 0.24) &&
                            unionHeight <= medianHeight * 1.45;
                        if (!nestedOrBroken) continue;
                        left.Merge(right);
                        groups.RemoveAt(rightIndex);
                        combined = true;
                        break;
                    }
                }
            }
            while (combined);

            foreach (OcrComponent attachment in attachments.OrderByDescending(item => item.Pixels))
            {
                OcrComponent best = null;
                double bestScore = double.MaxValue;
                foreach (OcrComponent group in groups)
                {
                    int overlap = Math.Min(group.MaxX, attachment.MaxX) - Math.Max(group.MinX, attachment.MinX) + 1;
                    double centerDistance = Math.Abs((group.MinX + group.MaxX) / 2.0 - (attachment.MinX + attachment.MaxX) / 2.0);
                    int verticalGap = Math.Max(0, Math.Max(group.MinY, attachment.MinY) - Math.Min(group.MaxY, attachment.MaxY) - 1);
                    bool aligned = overlap > 0 || centerDistance <= Math.Max(2.0, group.Width * 0.38);
                    if (!aligned || verticalGap > Math.Max(4.0, medianHeight * 0.62)) continue;
                    double score = centerDistance + verticalGap * 1.5;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = group;
                    }
                }
                if (best != null) best.Merge(attachment);
                else if (attachment.Pixels >= 3) groups.Add(attachment);
            }

            groups = groups.OrderBy(item => item.MinX).ThenBy(item => item.MinY).ToList();
            var glyphs = new List<OcrGlyph>();
            foreach (OcrComponent group in groups)
            {
                OcrGlyph glyph = CreateGlyph(ink, width, height, group.MinX, group.MinY, group.Width, group.Height);
                if (glyph != null)
                {
                    glyph.LineHeight = bottom - top + 1;
                    glyphs.Add(glyph);
                }
            }
            return glyphs;
        }

        private static List<OcrComponent> FindLineComponents(bool[] ink, int width, int height, int top, int bottom)
        {
            int bandHeight = bottom - top + 1;
            var visited = new bool[Math.Max(0, bandHeight * width)];
            var queue = new List<int>(128);
            var result = new List<OcrComponent>();
            for (int y = top; y <= bottom; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int position = y * width + x;
                    int local = (y - top) * width + x;
                    if (!ink[position] || visited[local]) continue;
                    var component = new OcrComponent { MinX = x, MaxX = x, MinY = y, MaxY = y };
                    queue.Clear();
                    queue.Add(position);
                    visited[local] = true;
                    for (int head = 0; head < queue.Count; head++)
                    {
                        int current = queue[head];
                        int currentX = current % width;
                        int currentY = current / width;
                        component.MinX = Math.Min(component.MinX, currentX);
                        component.MaxX = Math.Max(component.MaxX, currentX);
                        component.MinY = Math.Min(component.MinY, currentY);
                        component.MaxY = Math.Max(component.MaxY, currentY);
                        component.Pixels++;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int nextY = currentY + dy;
                            if (nextY < top || nextY > bottom) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nextX = currentX + dx;
                                if (nextX < 0 || nextX >= width) continue;
                                int next = nextY * width + nextX;
                                int nextLocal = (nextY - top) * width + nextX;
                                if (!ink[next] || visited[nextLocal]) continue;
                                visited[nextLocal] = true;
                                queue.Add(next);
                            }
                        }
                    }
                    if (component.Pixels >= 2) result.Add(component);
                }
            }
            return result;
        }

        private static List<OcrGlyph> RefineGlyphHypotheses(
            List<OcrGlyph> glyphs,
            bool[] ink,
            int width,
            int height,
            int top,
            int bottom,
            IEnumerable<OcrTemplate> templates)
        {
            if (glyphs.Count < 2) return glyphs;
            double medianWidth = Median(glyphs
                .Where(item => item.Height >= Math.Max(3, (bottom - top + 1) / 2))
                .Select(item => item.Width)
                .ToList());
            var splitResult = new List<OcrGlyph>();
            foreach (OcrGlyph glyph in glyphs)
            {
                OcrGlyph bestLeft = null;
                OcrGlyph bestRight = null;
                double bestPairScore = 0;
                bool wide = glyph.Width > Math.Max(medianWidth * 1.32, glyph.Height * 0.78) &&
                    !(glyph.Character == '%' && glyph.Holes >= 2);
                if (wide)
                {
                    int minimumPart = Math.Max(2, (int)Math.Round(medianWidth * 0.38));
                    int first = glyph.X + minimumPart;
                    int last = glyph.X + glyph.Width - minimumPart;
                    var candidates = new List<int[]>();
                    for (int split = first; split <= last; split++)
                    {
                        int columnInk = 0;
                        for (int y = glyph.Y; y < glyph.Y + glyph.Height; y++) if (ink[y * width + split]) columnInk++;
                        candidates.Add(new[] { split, columnInk });
                    }
                    foreach (int[] candidate in candidates
                        .OrderBy(item => item[1])
                        .ThenBy(item => Math.Abs(item[0] - (glyph.X + glyph.Width / 2)))
                        .Take(8))
                    {
                        int split = candidate[0];
                        if (candidate[1] > Math.Max(3, glyph.Height / 3)) continue;
                        OcrGlyph left = CreateGlyph(ink, width, height, glyph.X, glyph.Y, split - glyph.X, glyph.Height);
                        OcrGlyph right = CreateGlyph(ink, width, height, split + 1, glyph.Y, glyph.X + glyph.Width - split - 1, glyph.Height);
                        if (left == null || right == null) continue;
                        left.LineHeight = glyph.LineHeight;
                        right.LineHeight = glyph.LineHeight;
                        Classify(left, templates);
                        Classify(right, templates);
                        double pairScore = (left.Score + right.Score) * 0.325 + Math.Min(left.Score, right.Score) * 0.35;
                        if (pairScore > bestPairScore)
                        {
                            bestPairScore = pairScore;
                            bestLeft = left;
                            bestRight = right;
                        }
                    }
                }
                if (bestLeft != null && bestRight != null &&
                    bestPairScore > glyph.Score + 0.035 && Math.Min(bestLeft.Score, bestRight.Score) >= 0.50)
                {
                    splitResult.Add(bestLeft);
                    splitResult.Add(bestRight);
                }
                else splitResult.Add(glyph);
            }

            var merged = new List<OcrGlyph>();
            for (int index = 0; index < splitResult.Count; index++)
            {
                OcrGlyph current = splitResult[index];
                if (index + 1 < splitResult.Count)
                {
                    OcrGlyph next = splitResult[index + 1];
                    int gap = next.X - (current.X + current.Width);
                    int combinedWidth = next.X + next.Width - current.X;
                    bool brokenCandidate = gap <= Math.Max(2, (int)Math.Round(medianWidth * 0.12)) &&
                        combinedWidth <= medianWidth * 1.38 &&
                        (Math.Min(current.Width, next.Width) <= medianWidth * 0.32 || Math.Min(current.Score, next.Score) < 0.55);
                    if (brokenCandidate)
                    {
                        int combinedTop = Math.Min(current.Y, next.Y);
                        int combinedBottom = Math.Max(current.Y + current.Height, next.Y + next.Height);
                        OcrGlyph joined = CreateGlyph(ink, width, height, current.X, combinedTop, combinedWidth, combinedBottom - combinedTop);
                        if (joined != null)
                        {
                            joined.LineHeight = Math.Max(current.LineHeight, next.LineHeight);
                            Classify(joined, templates);
                            if (joined.Score > (current.Score + next.Score) / 2.0 + 0.055)
                            {
                                merged.Add(joined);
                                index++;
                                continue;
                            }
                        }
                    }
                }
                merged.Add(current);
            }
            return merged;
        }

        private static void ApplyLineCaseContext(List<OcrGlyph> glyphs)
        {
            int letters = 0;
            int uppercase = 0;
            int tall = 0;
            foreach (OcrGlyph glyph in glyphs)
            {
                if (!char.IsLetter(glyph.Character)) continue;
                letters++;
                if (char.IsUpper(glyph.Character)) uppercase++;
                if (glyph.LineHeight > 0 && glyph.Height >= glyph.LineHeight * 0.70) tall++;
            }
            bool allCaps = letters >= 3 && tall * 100 >= letters * 72 && uppercase * 100 >= letters * 40;
            if (!allCaps) return;
            foreach (OcrGlyph glyph in glyphs)
            {
                if (char.IsLetter(glyph.Character)) glyph.Character = char.ToUpperInvariant(glyph.Character);
            }
            OcrGlyph last = glyphs.Count == 0 ? null : glyphs[glyphs.Count - 1];
            if (last != null && last.Character == '@') last.Character = '®';
        }

        private static string CorrectRecognizedLine(string value)
        {
            string line = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
            if (line.Length == 0) return line;

            string regulatory = ClosestCanonicalLine(
                line,
                new[] { "PRODUCT MONOGRAPH", "MONOGRAPHIE DE PRODUIT" },
                0.28);
            if (regulatory != null) return regulatory;

            string date = CorrectDateLine(line);
            if (date != null) return date;

            string numeric = CorrectNumericLine(line);
            if (numeric != null) return numeric;

            line = Regex.Replace(line, @"[lI|\]]\s*%", "1%");
            line = Regex.Replace(line, @"\s+['""]$", string.Empty);
            return line;
        }

        private static string ClosestCanonicalLine(string value, IEnumerable<string> candidates, double maximumDistanceRatio)
        {
            string normalized = NormalizeForDistance(value);
            if (normalized.Length == 0) return null;
            string best = null;
            int bestDistance = int.MaxValue;
            foreach (string candidate in candidates)
            {
                string target = NormalizeForDistance(candidate);
                int distance = EditDistance(normalized, target);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }
            int denominator = Math.Max(normalized.Length, NormalizeForDistance(best).Length);
            return bestDistance <= Math.Max(1, (int)Math.Floor(denominator * maximumDistanceRatio)) ? best : null;
        }

        private static string CorrectDateLine(string value)
        {
            Match match = Regex.Match(
                value,
                @"^\s*([0-9Il|\]\s]{1,5})\s+([A-Za-zÀ-ÿ]{3,12})\s+([0-9OÛÜIl|\]\s]{4,7})\s*$",
                RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            string day = MapNumericToken(match.Groups[1].Value);
            string year = MapNumericToken(match.Groups[3].Value);
            if (day.Length < 1 || day.Length > 2 || year.Length != 4) return null;
            int dayNumber;
            int yearNumber;
            if (!int.TryParse(day, out dayNumber) || dayNumber < 1 || dayNumber > 31 ||
                !int.TryParse(year, out yearNumber) || yearNumber < 1900 || yearNumber > 2200) return null;
            string[] months =
            {
                "janvier", "février", "mars", "avril", "mai", "juin", "juillet", "août", "septembre", "octobre", "novembre", "décembre",
                "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"
            };
            string month = ClosestCanonicalLine(match.Groups[2].Value, months, 0.45);
            if (month == null) return null;
            return dayNumber + " " + month + " " + yearNumber;
        }

        private static string CorrectNumericLine(string value)
        {
            string compact = Regex.Replace(value, @"\s+", string.Empty);
            if (compact.Length < 4 || compact.Length > 16) return null;
            string mapped = MapNumericToken(compact);
            if (mapped.Length != compact.Length) return null;
            int originalDigits = compact.Count(char.IsDigit);
            if (originalDigits * 100 < compact.Length * 45 || !mapped.All(char.IsDigit)) return null;
            return mapped;
        }

        private static string MapNumericToken(string value)
        {
            var result = new StringBuilder();
            foreach (char raw in value)
            {
                if (char.IsWhiteSpace(raw)) continue;
                if (char.IsDigit(raw)) { result.Append(raw); continue; }
                char character = char.ToUpperInvariant(raw);
                if (character == 'I' || character == 'L' || character == '|' || character == ']') result.Append('1');
                else if (character == 'O' || character == 'Û' || character == 'Ü' || character == 'D' || character == 'Q') result.Append('0');
                else if (character == 'Z') result.Append('2');
                else if (character == 'S') result.Append('5');
                else if (character == 'G') result.Append('6');
                else if (character == 'B') result.Append('8');
                else if (character == 'Ç') result.Append('9');
                else return string.Empty;
            }
            return result.ToString();
        }

        private static string NormalizeForDistance(string value)
        {
            var result = new StringBuilder();
            foreach (char raw in (value ?? string.Empty).ToUpperInvariant())
            {
                char character = raw;
                if ("ÀÂÄ".IndexOf(character) >= 0) character = 'A';
                else if (character == 'Ç') character = 'C';
                else if ("ÉÈÊË".IndexOf(character) >= 0) character = 'E';
                else if ("ÎÏ".IndexOf(character) >= 0) character = 'I';
                else if ("ÔÖ".IndexOf(character) >= 0) character = 'O';
                else if ("ÙÛÜ".IndexOf(character) >= 0) character = 'U';
                if (char.IsLetterOrDigit(character)) result.Append(character);
            }
            return result.ToString();
        }

        private static int EditDistance(string left, string right)
        {
            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];
            for (int index = 0; index <= right.Length; index++) previous[index] = index;
            for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
            {
                current[0] = leftIndex;
                for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                {
                    int substitution = previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                    current[rightIndex] = Math.Min(Math.Min(previous[rightIndex] + 1, current[rightIndex - 1] + 1), substitution);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[right.Length];
        }

        private static OcrGlyph CreateGlyph(bool[] ink, int width, int height, int x, int y, int glyphWidth, int glyphHeight)
        {
            int minX = x + glyphWidth;
            int minY = y + glyphHeight;
            int maxX = x - 1;
            int maxY = y - 1;
            int pixels = 0;
            for (int py = Math.Max(0, y); py < Math.Min(height, y + glyphHeight); py++)
            {
                for (int px = Math.Max(0, x); px < Math.Min(width, x + glyphWidth); px++)
                {
                    if (!ink[py * width + px]) continue;
                    pixels++;
                    minX = Math.Min(minX, px);
                    maxX = Math.Max(maxX, px);
                    minY = Math.Min(minY, py);
                    maxY = Math.Max(maxY, py);
                }
            }
            if (pixels < 2 || maxX < minX || maxY < minY) return null;
            int actualWidth = maxX - minX + 1;
            int actualHeight = maxY - minY + 1;
            ulong[] bits;
            int foreground;
            NormalizeGlyph(ink, width, height, minX, minY, actualWidth, actualHeight, out bits, out foreground);
            if (foreground <= 0) return null;
            return new OcrGlyph
            {
                X = minX,
                Y = minY,
                Width = actualWidth,
                Height = actualHeight,
                Bits = bits,
                Foreground = foreground,
                Holes = CountHoles(bits)
            };
        }

        private static void NormalizeGlyph(bool[] source, int sourceWidth, int sourceHeight, int x, int y, int width, int height, out ulong[] bits, out int foreground)
        {
            int minX = x + width;
            int minY = y + height;
            int maxX = x - 1;
            int maxY = y - 1;
            for (int py = Math.Max(0, y); py < Math.Min(sourceHeight, y + height); py++)
            {
                for (int px = Math.Max(0, x); px < Math.Min(sourceWidth, x + width); px++)
                {
                    if (!source[py * sourceWidth + px]) continue;
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;
                }
            }
            bits = new ulong[(NormalizedWidth * NormalizedHeight + 63) / 64];
            foreground = 0;
            if (maxX < minX || maxY < minY) return;
            int actualWidth = maxX - minX + 1;
            int actualHeight = maxY - minY + 1;
            double scale = Math.Min((NormalizedWidth - 2) / (double)Math.Max(1, actualWidth), (NormalizedHeight - 2) / (double)Math.Max(1, actualHeight));
            int drawWidth = Math.Max(1, (int)Math.Round(actualWidth * scale));
            int drawHeight = Math.Max(1, (int)Math.Round(actualHeight * scale));
            int offsetX = (NormalizedWidth - drawWidth) / 2;
            int offsetY = (NormalizedHeight - drawHeight) / 2;
            for (int ty = 0; ty < drawHeight; ty++)
            {
                int sy = minY + Math.Min(actualHeight - 1, (int)(ty / scale));
                for (int tx = 0; tx < drawWidth; tx++)
                {
                    int sx = minX + Math.Min(actualWidth - 1, (int)(tx / scale));
                    if (!source[sy * sourceWidth + sx]) continue;
                    int index = (offsetY + ty) * NormalizedWidth + offsetX + tx;
                    bits[index >> 6] |= 1UL << (index & 63);
                }
            }
            foreach (ulong value in bits) foreground += PopCount(value);
        }

        private static void Classify(OcrGlyph glyph, IEnumerable<OcrTemplate> templates)
        {
            char character;
            double score;
            MatchTemplates(glyph, templates, out character, out score);
            glyph.Character = character;
            glyph.Score = score;
        }

        private static void MatchTemplates(OcrGlyph glyph, IEnumerable<OcrTemplate> templates, out char character, out double score)
        {
            character = '?';
            score = 0;
            double secondCharacterScore = 0;
            foreach (OcrTemplate template in templates)
            {
                if (glyph.LineHeight > 0 && glyph.Height > glyph.LineHeight * 0.62 &&
                    "\"'.,:;".IndexOf(template.Character) >= 0) continue;
                int intersection = 0;
                for (int i = 0; i < glyph.Bits.Length; i++) intersection += PopCount(glyph.Bits[i] & template.Bits[i]);
                double dice = (2.0 * intersection) / Math.Max(1, glyph.Foreground + template.Foreground);
                double glyphAspect = glyph.Width / Math.Max(1.0, glyph.Height);
                double aspect = Math.Min(glyphAspect, template.Aspect) / Math.Max(0.05, Math.Max(glyphAspect, template.Aspect));
                double topology = glyph.Holes == template.Holes ? 1.0 : Math.Max(0.0, 1.0 - Math.Abs(glyph.Holes - template.Holes) * 0.55);
                double similarity = dice * 0.80 + aspect * 0.10 + topology * 0.10;
                if (similarity > score)
                {
                    if (template.Character != character) secondCharacterScore = score;
                    character = template.Character;
                    score = similarity;
                }
                else if (template.Character != character && similarity > secondCharacterScore)
                {
                    secondCharacterScore = similarity;
                }
            }
            score = Math.Max(0.0, Math.Min(1.0, score * 0.90 + Math.Min(0.10, Math.Max(0.0, score - secondCharacterScore))));
        }

        private static Dictionary<char, OcrTemplate> BuildAdaptiveTemplates(List<OcrGlyph> glyphs)
        {
            var result = new Dictionary<char, OcrTemplate>();
            foreach (OcrGlyph glyph in glyphs.Where(item => item.Score >= 0.90).OrderByDescending(item => item.Score))
            {
                if (!result.ContainsKey(glyph.Character))
                {
                    result[glyph.Character] = new OcrTemplate
                    {
                        Character = glyph.Character,
                        Bits = (ulong[])glyph.Bits.Clone(),
                        Foreground = glyph.Foreground,
                        Holes = glyph.Holes,
                        Aspect = glyph.Width / Math.Max(1.0, glyph.Height)
                    };
                }
            }
            return result;
        }

        private static int PopCount(ulong value)
        {
            value = value - ((value >> 1) & 0x5555555555555555UL);
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            return (int)((((value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL) * 0x0101010101010101UL) >> 56);
        }

        private static int CountHoles(ulong[] bits)
        {
            int area = NormalizedWidth * NormalizedHeight;
            var visited = new bool[area];
            var queue = new int[area];
            int holes = 0;
            for (int seed = 0; seed < area; seed++)
            {
                bool foreground = (bits[seed >> 6] & (1UL << (seed & 63))) != 0;
                if (foreground || visited[seed]) continue;
                int head = 0;
                int tail = 0;
                queue[tail++] = seed;
                visited[seed] = true;
                bool touchesBorder = false;
                while (head < tail)
                {
                    int position = queue[head++];
                    int x = position % NormalizedWidth;
                    int y = position / NormalizedWidth;
                    if (x == 0 || y == 0 || x == NormalizedWidth - 1 || y == NormalizedHeight - 1) touchesBorder = true;
                    if (x > 0) AddBackground(position - 1, bits, visited, queue, ref tail);
                    if (x + 1 < NormalizedWidth) AddBackground(position + 1, bits, visited, queue, ref tail);
                    if (y > 0) AddBackground(position - NormalizedWidth, bits, visited, queue, ref tail);
                    if (y + 1 < NormalizedHeight) AddBackground(position + NormalizedWidth, bits, visited, queue, ref tail);
                }
                if (!touchesBorder) holes++;
            }
            return holes;
        }

        private static void AddBackground(int position, ulong[] bits, bool[] visited, int[] queue, ref int tail)
        {
            if (visited[position] || (bits[position >> 6] & (1UL << (position & 63))) != 0) return;
            visited[position] = true;
            queue[tail++] = position;
        }

        private static double Median(List<int> values)
        {
            if (values == null || values.Count == 0) return 1;
            values.Sort();
            int middle = values.Count / 2;
            return values.Count % 2 == 0 ? (values[middle - 1] + values[middle]) / 2.0 : values[middle];
        }
    }

    private sealed class PdfFont
    {
        private readonly PdfTextDocument _document;
        private readonly PdfDictionary _dictionary;
        private readonly Dictionary<string, string> _toUnicode = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _differences = new Dictionary<int, string>();
        private readonly Dictionary<int, double> _widths = new Dictionary<int, double>();
        private readonly List<int> _codeLengths = new List<int>();
        private readonly bool _composite;
        private readonly string _encoding;
        private readonly double _missingWidth;

        public PdfFont(PdfTextDocument document, PdfDictionary dictionary)
        {
            this._document = document;
            this._dictionary = dictionary ?? new PdfDictionary();
            this.BaseFont = PdfTextDocument.GetName(this._document.Resolve(this._dictionary.Get("BaseFont"))) ?? string.Empty;
            this.IsBold = this.BaseFont.IndexOf("Bold", StringComparison.OrdinalIgnoreCase) >= 0 ||
                this.BaseFont.IndexOf("Black", StringComparison.OrdinalIgnoreCase) >= 0;
            this.IsItalic = this.BaseFont.IndexOf("Italic", StringComparison.OrdinalIgnoreCase) >= 0 ||
                this.BaseFont.IndexOf("Oblique", StringComparison.OrdinalIgnoreCase) >= 0;
            this._composite = string.Equals(
                PdfTextDocument.GetName(this._document.Resolve(this._dictionary.Get("Subtype"))),
                "Type0",
                StringComparison.Ordinal);

            PdfValue encodingValue = this._document.Resolve(this._dictionary.Get("Encoding"));
            PdfName encodingName = encodingValue as PdfName;
            PdfDictionary encodingDictionary = PdfTextDocument.AsDictionary(encodingValue);
            if (encodingName != null) this._encoding = encodingName.Value;
            else if (encodingDictionary != null)
            {
                this._encoding = PdfTextDocument.GetName(this._document.Resolve(encodingDictionary.Get("BaseEncoding"))) ?? "StandardEncoding";
                ParseDifferences(this._document.Resolve(encodingDictionary.Get("Differences")) as PdfArray);
            }
            else this._encoding = this._composite ? "Identity-H" : "StandardEncoding";

            PdfDictionary descriptor = PdfTextDocument.AsDictionary(this._document.Resolve(this._dictionary.Get("FontDescriptor")));
            this._missingWidth = descriptor == null ? 500 : PdfTextDocument.GetNumber(this._document.Resolve(descriptor.Get("MissingWidth")), 500);
            ParseWidths();
            ParseToUnicode();
            if (this._codeLengths.Count == 0) this._codeLengths.Add(this._composite ? 2 : 1);
            this._codeLengths.Sort();
            this._codeLengths.Reverse();
        }

        public string BaseFont { get; private set; }
        public bool IsBold { get; private set; }
        public bool IsItalic { get; private set; }

        public string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            var result = new StringBuilder(bytes.Length);
            int position = 0;
            while (position < bytes.Length)
            {
                bool mapped = false;
                foreach (int length in this._codeLengths)
                {
                    if (length <= 0 || position + length > bytes.Length) continue;
                    string unicode;
                    if (this._toUnicode.TryGetValue(CodeKey(bytes, position, length), out unicode))
                    {
                        result.Append(unicode);
                        position += length;
                        mapped = true;
                        break;
                    }
                }
                if (mapped) continue;

                if (this._composite)
                {
                    int length = this._codeLengths.Count == 0 ? 2 : this._codeLengths[this._codeLengths.Count - 1];
                    if (length <= 0 || position + length > bytes.Length) length = 1;
                    int code = BytesToInt(bytes, position, length);
                    if ((this._encoding == "Identity-H" || this._encoding == "Identity-V") && code > 0 && code <= 0x10ffff)
                    {
                        AppendCodePoint(result, code);
                    }
                    else result.Append('\uFFFD');
                    position += length;
                }
                else
                {
                    int code = bytes[position++];
                    string difference;
                    if (this._differences.TryGetValue(code, out difference)) result.Append(difference);
                    else result.Append(DecodeSingleByte(code, this._encoding));
                }
            }
            return result.ToString();
        }

        public double Measure(byte[] bytes, double fontSize, double characterSpacing, double wordSpacing, double horizontalScale)
        {
            if (bytes == null || bytes.Length == 0) return 0;
            double total = 0;
            int position = 0;
            while (position < bytes.Length)
            {
                int length = this._composite ? GetCodeLength(bytes, position) : 1;
                int code = BytesToInt(bytes, position, Math.Min(length, bytes.Length - position));
                double width;
                if (!this._widths.TryGetValue(code, out width)) width = this._missingWidth;
                total += width / 1000.0 * fontSize;
                total += characterSpacing;
                if (length == 1 && code == 32) total += wordSpacing;
                position += Math.Max(1, length);
            }
            return total * horizontalScale;
        }

        private int GetCodeLength(byte[] bytes, int position)
        {
            foreach (int length in this._codeLengths)
            {
                if (position + length <= bytes.Length && this._toUnicode.ContainsKey(CodeKey(bytes, position, length))) return length;
            }
            int fallback = this._codeLengths.Count == 0 ? 2 : this._codeLengths[this._codeLengths.Count - 1];
            return position + fallback <= bytes.Length ? fallback : 1;
        }

        private void ParseDifferences(PdfArray differences)
        {
            if (differences == null) return;
            int code = 0;
            foreach (PdfValue item in differences.Items)
            {
                PdfNumber number = item as PdfNumber;
                PdfName name = item as PdfName;
                if (number != null) code = (int)number.Value;
                else if (name != null)
                {
                    this._differences[code++] = GlyphNameToUnicode(name.Value);
                }
            }
        }

        private void ParseWidths()
        {
            int firstChar = PdfTextDocument.GetInt(this._document.Resolve(this._dictionary.Get("FirstChar")), 0);
            PdfArray widths = this._document.Resolve(this._dictionary.Get("Widths")) as PdfArray;
            if (widths != null)
            {
                for (int i = 0; i < widths.Items.Count; i++)
                {
                    this._widths[firstChar + i] = PdfTextDocument.GetNumber(this._document.Resolve(widths.Items[i]), this._missingWidth);
                }
            }

            if (!this._composite) return;
            PdfArray descendants = this._document.Resolve(this._dictionary.Get("DescendantFonts")) as PdfArray;
            PdfDictionary descendant = descendants != null && descendants.Items.Count > 0
                ? PdfTextDocument.AsDictionary(this._document.Resolve(descendants.Items[0]))
                : null;
            if (descendant == null) return;
            double defaultWidth = PdfTextDocument.GetNumber(this._document.Resolve(descendant.Get("DW")), 1000);
            PdfArray compositeWidths = this._document.Resolve(descendant.Get("W")) as PdfArray;
            if (compositeWidths == null) return;
            int position = 0;
            while (position < compositeWidths.Items.Count)
            {
                int first = PdfTextDocument.GetInt(this._document.Resolve(compositeWidths.Items[position++]), -1);
                if (first < 0 || position >= compositeWidths.Items.Count) break;
                PdfValue next = this._document.Resolve(compositeWidths.Items[position++]);
                PdfArray explicitWidths = next as PdfArray;
                if (explicitWidths != null)
                {
                    for (int i = 0; i < explicitWidths.Items.Count; i++)
                    {
                        this._widths[first + i] = PdfTextDocument.GetNumber(this._document.Resolve(explicitWidths.Items[i]), defaultWidth);
                    }
                }
                else
                {
                    int last = PdfTextDocument.GetInt(next, first);
                    if (position >= compositeWidths.Items.Count) break;
                    double width = PdfTextDocument.GetNumber(this._document.Resolve(compositeWidths.Items[position++]), defaultWidth);
                    int cap = Math.Min(last, first + 65535);
                    for (int code = first; code <= cap; code++) this._widths[code] = width;
                }
            }
        }

        private void ParseToUnicode()
        {
            PdfStream stream = this._document.Resolve(this._dictionary.Get("ToUnicode")) as PdfStream;
            if (stream == null) return;
            byte[] cmap;
            try { cmap = this._document.DecodeStream(stream); }
            catch (Exception) { return; }
            var parser = new PdfParser(cmap);
            var operands = new List<PdfValue>();
            while (!parser.AtEnd)
            {
                PdfValue value = parser.ReadValue(false);
                if (value == null) break;
                PdfKeyword keyword = value as PdfKeyword;
                if (keyword == null)
                {
                    operands.Add(value);
                    continue;
                }

                if (keyword.Value == "begincodespacerange")
                {
                    int count = LastInteger(operands);
                    for (int i = 0; i < count; i++)
                    {
                        PdfString start = parser.ReadValue(false) as PdfString;
                        PdfString end = parser.ReadValue(false) as PdfString;
                        if (start != null && start.Bytes.Length > 0 && !this._codeLengths.Contains(start.Bytes.Length)) this._codeLengths.Add(start.Bytes.Length);
                    }
                }
                else if (keyword.Value == "beginbfchar")
                {
                    int count = LastInteger(operands);
                    for (int i = 0; i < count; i++)
                    {
                        PdfString source = parser.ReadValue(false) as PdfString;
                        PdfString target = parser.ReadValue(false) as PdfString;
                        AddUnicodeMapping(source, target);
                    }
                }
                else if (keyword.Value == "beginbfrange")
                {
                    int count = LastInteger(operands);
                    for (int i = 0; i < count; i++)
                    {
                        PdfString start = parser.ReadValue(false) as PdfString;
                        PdfString end = parser.ReadValue(false) as PdfString;
                        PdfValue target = parser.ReadValue(false);
                        AddUnicodeRange(start, end, target);
                    }
                }
                operands.Clear();
            }
        }

        private void AddUnicodeMapping(PdfString source, PdfString target)
        {
            if (source == null || target == null || source.Bytes.Length == 0) return;
            this._toUnicode[CodeKey(source.Bytes, 0, source.Bytes.Length)] = DecodeUnicodeBytes(target.Bytes);
            if (!this._codeLengths.Contains(source.Bytes.Length)) this._codeLengths.Add(source.Bytes.Length);
        }

        private void AddUnicodeRange(PdfString start, PdfString end, PdfValue target)
        {
            if (start == null || end == null || start.Bytes.Length == 0 || start.Bytes.Length != end.Bytes.Length) return;
            int first = BytesToInt(start.Bytes, 0, start.Bytes.Length);
            int last = BytesToInt(end.Bytes, 0, end.Bytes.Length);
            if (last < first || last - first > 65535) return;
            PdfArray targets = target as PdfArray;
            PdfString baseTarget = target as PdfString;
            for (int code = first; code <= last; code++)
            {
                byte[] sourceBytes = IntToBytes(code, start.Bytes.Length);
                PdfString targetString = null;
                if (targets != null)
                {
                    int index = code - first;
                    if (index < targets.Items.Count) targetString = targets.Items[index] as PdfString;
                }
                else if (baseTarget != null)
                {
                    byte[] incremented = (byte[])baseTarget.Bytes.Clone();
                    IncrementBigEndian(incremented, code - first);
                    targetString = new PdfString(incremented);
                }
                if (targetString != null) this._toUnicode[CodeKey(sourceBytes, 0, sourceBytes.Length)] = DecodeUnicodeBytes(targetString.Bytes);
            }
            if (!this._codeLengths.Contains(start.Bytes.Length)) this._codeLengths.Add(start.Bytes.Length);
        }

        private static int LastInteger(List<PdfValue> operands)
        {
            if (operands.Count == 0) return 0;
            PdfNumber number = operands[operands.Count - 1] as PdfNumber;
            return number == null ? 0 : Math.Max(0, Math.Min(100000, (int)number.Value));
        }

        private static string DecodeUnicodeBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            int position = bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff ? 2 : 0;
            if (((bytes.Length - position) & 1) != 0)
            {
                var direct = new StringBuilder(bytes.Length - position);
                for (int i = position; i < bytes.Length; i++) direct.Append((char)bytes[i]);
                return direct.ToString();
            }
            var result = new StringBuilder((bytes.Length - position) / 2);
            while (position + 1 < bytes.Length)
            {
                result.Append((char)((bytes[position] << 8) | bytes[position + 1]));
                position += 2;
            }
            return result.ToString();
        }

        private static string CodeKey(byte[] bytes, int offset, int length)
        {
            const string hex = "0123456789ABCDEF";
            var result = new char[length * 2 + 2];
            result[0] = (char)('0' + Math.Min(9, length));
            result[1] = ':';
            for (int i = 0; i < length; i++)
            {
                int value = bytes[offset + i];
                result[2 + i * 2] = hex[value >> 4];
                result[3 + i * 2] = hex[value & 15];
            }
            return new string(result);
        }

        private static int BytesToInt(byte[] bytes, int offset, int length)
        {
            int result = 0;
            for (int i = 0; i < length; i++) result = (result << 8) | bytes[offset + i];
            return result;
        }

        private static byte[] IntToBytes(int value, int length)
        {
            byte[] result = new byte[length];
            for (int i = length - 1; i >= 0; i--) { result[i] = (byte)value; value >>= 8; }
            return result;
        }

        private static void IncrementBigEndian(byte[] value, int increment)
        {
            for (int i = value.Length - 1; i >= 0 && increment > 0; i--)
            {
                int sum = value[i] + (increment & 255);
                value[i] = (byte)sum;
                increment = (increment >> 8) + (sum >> 8);
            }
        }

        private static char DecodeSingleByte(int code, string encoding)
        {
            if (code >= 32 && code <= 126) return (char)code;
            if (string.Equals(encoding, "WinAnsiEncoding", StringComparison.Ordinal) || string.IsNullOrEmpty(encoding))
            {
                if (code >= 160 && code <= 255) return (char)code;
                switch (code)
                {
                    case 128: return '\u20AC';
                    case 130: return '\u201A';
                    case 131: return '\u0192';
                    case 132: return '\u201E';
                    case 133: return '\u2026';
                    case 134: return '\u2020';
                    case 135: return '\u2021';
                    case 136: return '\u02C6';
                    case 137: return '\u2030';
                    case 138: return '\u0160';
                    case 139: return '\u2039';
                    case 140: return '\u0152';
                    case 142: return '\u017D';
                    case 145: return '\u2018';
                    case 146: return '\u2019';
                    case 147: return '\u201C';
                    case 148: return '\u201D';
                    case 149: return '\u2022';
                    case 150: return '\u2013';
                    case 151: return '\u2014';
                    case 152: return '\u02DC';
                    case 153: return '\u2122';
                    case 154: return '\u0161';
                    case 155: return '\u203A';
                    case 156: return '\u0153';
                    case 158: return '\u017E';
                    case 159: return '\u0178';
                    default: return code == 9 || code == 10 || code == 13 ? (char)code : '\uFFFD';
                }
            }
            if (code >= 160 && code <= 255) return (char)code;
            if (code == 9 || code == 10 || code == 13) return (char)code;
            return code >= 32 ? (char)code : '\uFFFD';
        }

        private static string GlyphNameToUnicode(string name)
        {
            if (string.IsNullOrEmpty(name)) return "\uFFFD";
            int suffix = name.IndexOf('.');
            if (suffix > 0) name = name.Substring(0, suffix);
            if (name.Length == 1) return name;
            if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length > 3 && ((name.Length - 3) % 4) == 0)
            {
                var result = new StringBuilder();
                for (int i = 3; i + 3 < name.Length; i += 4)
                {
                    int code;
                    if (!TryParseHex(name.Substring(i, 4), out code)) return "\uFFFD";
                    AppendCodePoint(result, code);
                }
                return result.ToString();
            }
            if (name[0] == 'u' && name.Length >= 5 && name.Length <= 7)
            {
                int code;
                if (TryParseHex(name.Substring(1), out code))
                {
                    var result = new StringBuilder();
                    AppendCodePoint(result, code);
                    return result.ToString();
                }
            }

            switch (name)
            {
                case "space": return " ";
                case "nbspace": return "\u00A0";
                case "hyphen": case "minus": return "-";
                case "endash": return "\u2013";
                case "emdash": return "\u2014";
                case "bullet": return "\u2022";
                case "periodcentered": return "\u00B7";
                case "ellipsis": return "\u2026";
                case "quoteleft": return "\u2018";
                case "quoteright": return "\u2019";
                case "quotesingle": return "'";
                case "quotedbl": return "\"";
                case "quotedblleft": return "\u201C";
                case "quotedblright": return "\u201D";
                case "quotesinglbase": return "\u201A";
                case "quotedblbase": return "\u201E";
                case "Euro": return "\u20AC";
                case "sterling": return "\u00A3";
                case "yen": return "\u00A5";
                case "cent": return "\u00A2";
                case "copyright": return "\u00A9";
                case "registered": return "\u00AE";
                case "trademark": return "\u2122";
                case "degree": return "\u00B0";
                case "plusminus": return "\u00B1";
                case "multiply": return "\u00D7";
                case "divide": return "\u00F7";
                case "mu": return "\u00B5";
                case "fi": return "fi";
                case "fl": return "fl";
                case "ffi": return "ffi";
                case "ffl": return "ffl";
                case "AE": return "\u00C6";
                case "ae": return "\u00E6";
                case "OE": return "\u0152";
                case "oe": return "\u0153";
                case "Lslash": return "\u0141";
                case "lslash": return "\u0142";
                case "Oslash": return "\u00D8";
                case "oslash": return "\u00F8";
                case "germandbls": return "\u00DF";
                case "dagger": return "\u2020";
                case "daggerdbl": return "\u2021";
                case "paragraph": return "\u00B6";
                case "section": return "\u00A7";
                default: return LatinGlyphName(name);
            }
        }

        private static string LatinGlyphName(string name)
        {
            string[] suffixes = { "grave", "acute", "circumflex", "tilde", "dieresis", "ring", "cedilla" };
            char[] combining = { '\u0300', '\u0301', '\u0302', '\u0303', '\u0308', '\u030A', '\u0327' };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (name.EndsWith(suffixes[i], StringComparison.Ordinal) && name.Length > suffixes[i].Length)
                {
                    string root = name.Substring(0, name.Length - suffixes[i].Length);
                    if (root.Length == 1) return root + combining[i];
                }
            }
            return "\uFFFD";
        }

        private static bool TryParseHex(string value, out int result)
        {
            result = 0;
            if (string.IsNullOrEmpty(value)) return false;
            for (int i = 0; i < value.Length; i++)
            {
                int digit;
                char c = value[i];
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else return false;
                if (result > 0x10ffff / 16) return false;
                result = result * 16 + digit;
            }
            return true;
        }

        private static void AppendCodePoint(StringBuilder builder, int code)
        {
            if (code < 0 || code > 0x10ffff || (code >= 0xd800 && code <= 0xdfff))
            {
                builder.Append('\uFFFD');
            }
            else if (code <= 0xffff) builder.Append((char)code);
            else
            {
                code -= 0x10000;
                builder.Append((char)(0xd800 + (code >> 10)));
                builder.Append((char)(0xdc00 + (code & 0x3ff)));
            }
        }
    }

    private struct Matrix
    {
        public double A;
        public double B;
        public double C;
        public double D;
        public double E;
        public double F;

        public Matrix(double a, double b, double c, double d, double e, double f)
        {
            this.A = a; this.B = b; this.C = c; this.D = d; this.E = e; this.F = f;
        }

        public static Matrix Identity
        {
            get { return new Matrix(1, 0, 0, 1, 0, 0); }
        }

        public static Matrix Translation(double x, double y)
        {
            return new Matrix(1, 0, 0, 1, x, y);
        }

        public static Matrix Multiply(Matrix left, Matrix right)
        {
            return new Matrix(
                left.A * right.A + left.C * right.B,
                left.B * right.A + left.D * right.B,
                left.A * right.C + left.C * right.D,
                left.B * right.C + left.D * right.D,
                left.A * right.E + left.C * right.F + left.E,
                left.B * right.E + left.D * right.F + left.F);
        }

        public double TransformX(double x, double y) { return this.A * x + this.C * y + this.E; }
        public double TransformY(double x, double y) { return this.B * x + this.D * y + this.F; }
    }

    private sealed class TextFragment
    {
        public double X;
        public double Y;
        public double EndX;
        public double EndY;
        public double FontSize;
        public string Text;
        public bool IsBold;
        public bool IsItalic;
        public bool IsSuperscript;
        public bool IsSubscript;
        public SemanticInfo Semantic;
        public int Sequence;
    }

    private sealed class ContentState
    {
        public Matrix Ctm = Matrix.Identity;
        public Matrix TextMatrix = Matrix.Identity;
        public Matrix TextLineMatrix = Matrix.Identity;
        public PdfFont Font;
        public double FontSize = 12;
        public double CharacterSpacing;
        public double WordSpacing;
        public double HorizontalScale = 1;
        public double Leading;
        public double Rise;
        public bool InText;

        public ContentState Clone()
        {
            return new ContentState
            {
                Ctm = this.Ctm,
                TextMatrix = this.TextMatrix,
                TextLineMatrix = this.TextLineMatrix,
                Font = this.Font,
                FontSize = this.FontSize,
                CharacterSpacing = this.CharacterSpacing,
                WordSpacing = this.WordSpacing,
                HorizontalScale = this.HorizontalScale,
                Leading = this.Leading,
                Rise = this.Rise,
                InText = this.InText
            };
        }
    }

    private sealed class MarkedContentState
    {
        public bool InArtifact;
        public SemanticInfo Semantic;
    }

    private sealed class ContentInterpreter
    {
        private readonly PdfTextDocument _document;
        private readonly PdfPage _page;
        private readonly bool _includeArtifacts;
        private readonly System.Threading.CancellationToken _cancellationToken;
        private readonly List<TextFragment> _fragments = new List<TextFragment>();
        private readonly Dictionary<int, PdfFont> _fontObjectCache = new Dictionary<int, PdfFont>();
        private readonly Dictionary<string, PdfFont> _fontNameCache = new Dictionary<string, PdfFont>(StringComparer.Ordinal);
        private int _sequence;

        public ContentInterpreter(
            PdfTextDocument document,
            PdfPage page,
            bool includeArtifacts,
            System.Threading.CancellationToken cancellationToken)
        {
            this._document = document;
            this._page = page;
            this._includeArtifacts = includeArtifacts;
            this._cancellationToken = cancellationToken;
        }

        public PageContentResult Extract(bool richMarkdown)
        {
            PdfValue contents = this._document.Resolve(this._page.Dictionary.Get("Contents"));
            var streams = new List<PdfStream>();
            PdfStream single = contents as PdfStream;
            PdfArray array = contents as PdfArray;
            if (single != null) streams.Add(single);
            else if (array != null)
            {
                foreach (PdfValue item in array.Items)
                {
                    PdfStream stream = this._document.Resolve(item) as PdfStream;
                    if (stream != null) streams.Add(stream);
                }
            }

            var state = new ContentState();
            foreach (PdfStream stream in streams)
            {
                this._cancellationToken.ThrowIfCancellationRequested();
                Interpret(this._document.DecodeStream(stream), this._page.Resources, state, false, null, 0);
            }
            ApplyPageRotation();
            if (!richMarkdown)
            {
                return new PageContentResult
                {
                    Text = ReconstructText(this._fragments),
                    TableCount = 0,
                    UsedTaggedStructure = false
                };
            }
            return ReconstructMarkdown(
                this._fragments,
                this._document.GetSemanticTablesForPage(this._page.ObjectNumber),
                this._page.ObjectNumber);
        }

        private void Interpret(
            byte[] content,
            PdfDictionary resources,
            ContentState initialState,
            bool inheritedArtifact,
            SemanticInfo inheritedSemantic,
            int depth)
        {
            if (content == null || content.Length == 0 || depth > 12) return;
            var parser = new PdfParser(content);
            var operands = new List<PdfValue>();
            var graphicsStack = new Stack<ContentState>();
            var markedStack = new Stack<MarkedContentState>();
            var state = initialState.Clone();
            bool inArtifact = inheritedArtifact;
            SemanticInfo semantic = inheritedSemantic;
            int operationCount = 0;

            while (!parser.AtEnd)
            {
                if ((operationCount++ & 1023) == 0) this._cancellationToken.ThrowIfCancellationRequested();
                PdfValue value = parser.ReadValue(false);
                if (value == null) break;
                PdfKeyword keyword = value as PdfKeyword;
                if (keyword == null)
                {
                    operands.Add(value);
                    continue;
                }

                string op = keyword.Value;
                if (op == "BI")
                {
                    parser.SkipInlineImage();
                    operands.Clear();
                    continue;
                }
                if (op == "q") graphicsStack.Push(state.Clone());
                else if (op == "Q" && graphicsStack.Count > 0) state = graphicsStack.Pop();
                else if (op == "cm" && operands.Count >= 6)
                {
                    Matrix matrix = MatrixFromOperands(operands, operands.Count - 6);
                    state.Ctm = Matrix.Multiply(state.Ctm, matrix);
                }
                else if (op == "BT")
                {
                    state.InText = true;
                    state.TextMatrix = Matrix.Identity;
                    state.TextLineMatrix = Matrix.Identity;
                }
                else if (op == "ET") state.InText = false;
                else if (op == "Tf" && operands.Count >= 2)
                {
                    PdfName name = operands[operands.Count - 2] as PdfName;
                    state.FontSize = Number(operands[operands.Count - 1], state.FontSize);
                    if (name != null) state.Font = GetFont(resources, name.Value);
                }
                else if (op == "Tc" && operands.Count >= 1) state.CharacterSpacing = Number(operands[operands.Count - 1], 0);
                else if (op == "Tw" && operands.Count >= 1) state.WordSpacing = Number(operands[operands.Count - 1], 0);
                else if (op == "Tz" && operands.Count >= 1) state.HorizontalScale = Number(operands[operands.Count - 1], 100) / 100.0;
                else if (op == "TL" && operands.Count >= 1) state.Leading = Number(operands[operands.Count - 1], 0);
                else if (op == "Ts" && operands.Count >= 1) state.Rise = Number(operands[operands.Count - 1], 0);
                else if (op == "Tm" && operands.Count >= 6)
                {
                    state.TextMatrix = MatrixFromOperands(operands, operands.Count - 6);
                    state.TextLineMatrix = state.TextMatrix;
                }
                else if (op == "Td" && operands.Count >= 2)
                {
                    MoveTextLine(state, Number(operands[operands.Count - 2], 0), Number(operands[operands.Count - 1], 0));
                }
                else if (op == "TD" && operands.Count >= 2)
                {
                    double x = Number(operands[operands.Count - 2], 0);
                    double y = Number(operands[operands.Count - 1], 0);
                    state.Leading = -y;
                    MoveTextLine(state, x, y);
                }
                else if (op == "T*") MoveTextLine(state, 0, -state.Leading);
                else if (op == "Tj" && operands.Count >= 1)
                {
                    ShowString(state, operands[operands.Count - 1] as PdfString, inArtifact, semantic);
                }
                else if (op == "TJ" && operands.Count >= 1)
                {
                    ShowArray(state, operands[operands.Count - 1] as PdfArray, inArtifact, semantic);
                }
                else if (op == "'")
                {
                    MoveTextLine(state, 0, -state.Leading);
                    if (operands.Count >= 1) ShowString(state, operands[operands.Count - 1] as PdfString, inArtifact, semantic);
                }
                else if (op == "\"")
                {
                    if (operands.Count >= 3)
                    {
                        state.WordSpacing = Number(operands[operands.Count - 3], state.WordSpacing);
                        state.CharacterSpacing = Number(operands[operands.Count - 2], state.CharacterSpacing);
                        MoveTextLine(state, 0, -state.Leading);
                        ShowString(state, operands[operands.Count - 1] as PdfString, inArtifact, semantic);
                    }
                }
                else if (op == "BMC" || op == "BDC")
                {
                    markedStack.Push(new MarkedContentState { InArtifact = inArtifact, Semantic = semantic });
                    PdfName tag = operands.Count > 0 ? operands[0] as PdfName : null;
                    inArtifact = inArtifact || (tag != null && string.Equals(tag.Value, "Artifact", StringComparison.Ordinal));
                    if (op == "BDC" && operands.Count >= 2)
                    {
                        PdfValue properties = operands[operands.Count - 1];
                        PdfName propertyName = properties as PdfName;
                        if (propertyName != null)
                        {
                            PdfDictionary propertyResources = PdfTextDocument.AsDictionary(
                                this._document.Resolve(resources == null ? null : resources.Get("Properties")));
                            properties = propertyResources == null ? null : propertyResources.Get(propertyName.Value);
                        }
                        PdfDictionary propertyDictionary = PdfTextDocument.AsDictionary(this._document.Resolve(properties));
                        int mcid = propertyDictionary == null
                            ? -1
                            : PdfTextDocument.GetInt(this._document.Resolve(propertyDictionary.Get("MCID")), -1);
                        if (mcid >= 0)
                        {
                            SemanticInfo tagged = this._document.GetSemanticInfo(this._page.ObjectNumber, mcid);
                            if (tagged != null) semantic = tagged;
                        }
                    }
                }
                else if (op == "EMC")
                {
                    if (markedStack.Count > 0)
                    {
                        MarkedContentState marked = markedStack.Pop();
                        inArtifact = marked.InArtifact;
                        semantic = marked.Semantic;
                    }
                    else
                    {
                        inArtifact = inheritedArtifact;
                        semantic = inheritedSemantic;
                    }
                }
                else if (op == "Do" && operands.Count >= 1)
                {
                    PdfName name = operands[operands.Count - 1] as PdfName;
                    if (name != null) InterpretForm(resources, name.Value, state, inArtifact, semantic, depth + 1);
                }
                operands.Clear();
            }
        }

        private void InterpretForm(
            PdfDictionary resources,
            string name,
            ContentState state,
            bool inArtifact,
            SemanticInfo semantic,
            int depth)
        {
            PdfDictionary xobjects = PdfTextDocument.AsDictionary(this._document.Resolve(resources == null ? null : resources.Get("XObject")));
            PdfStream form = xobjects == null ? null : this._document.Resolve(xobjects.Get(name)) as PdfStream;
            if (form == null || !string.Equals(PdfTextDocument.GetName(this._document.Resolve(form.Dictionary.Get("Subtype"))), "Form", StringComparison.Ordinal)) return;
            PdfDictionary formResources = PdfTextDocument.AsDictionary(this._document.Resolve(form.Dictionary.Get("Resources"))) ?? resources;
            PdfArray matrixValues = this._document.Resolve(form.Dictionary.Get("Matrix")) as PdfArray;
            Matrix formMatrix = Matrix.Identity;
            if (matrixValues != null && matrixValues.Items.Count >= 6)
            {
                formMatrix = new Matrix(
                    Number(matrixValues.Items[0], 1), Number(matrixValues.Items[1], 0),
                    Number(matrixValues.Items[2], 0), Number(matrixValues.Items[3], 1),
                    Number(matrixValues.Items[4], 0), Number(matrixValues.Items[5], 0));
            }
            ContentState formState = state.Clone();
            formState.Ctm = Matrix.Multiply(state.Ctm, formMatrix);
            Interpret(this._document.DecodeStream(form), formResources, formState, inArtifact, semantic, depth);
        }

        private PdfFont GetFont(PdfDictionary resources, string resourceName)
        {
            string localKey = (resources == null ? "0" : resources.GetHashCode().ToString()) + ":" + resourceName;
            PdfFont cached;
            if (this._fontNameCache.TryGetValue(localKey, out cached)) return cached;
            PdfDictionary fonts = PdfTextDocument.AsDictionary(this._document.Resolve(resources == null ? null : resources.Get("Font")));
            PdfValue fontValue = fonts == null ? null : fonts.Get(resourceName);
            PdfReference fontReference = fontValue as PdfReference;
            if (fontReference != null && this._fontObjectCache.TryGetValue(fontReference.ObjectNumber, out cached))
            {
                this._fontNameCache[localKey] = cached;
                return cached;
            }
            PdfDictionary fontDictionary = PdfTextDocument.AsDictionary(this._document.Resolve(fontValue));
            cached = new PdfFont(this._document, fontDictionary);
            this._fontNameCache[localKey] = cached;
            if (fontReference != null) this._fontObjectCache[fontReference.ObjectNumber] = cached;
            return cached;
        }

        private void ShowString(ContentState state, PdfString value, bool inArtifact, SemanticInfo semantic)
        {
            if (value == null) return;
            PdfFont font = state.Font ?? new PdfFont(this._document, null);
            string text = font.Decode(value.Bytes);
            double advance = font.Measure(
                value.Bytes,
                Math.Abs(state.FontSize),
                state.CharacterSpacing,
                state.WordSpacing,
                state.HorizontalScale);
            Capture(state, text, advance, inArtifact, font.IsBold, font.IsItalic, semantic);
            state.TextMatrix = Matrix.Multiply(state.TextMatrix, Matrix.Translation(advance, 0));
        }

        private void ShowArray(ContentState state, PdfArray array, bool inArtifact, SemanticInfo semantic)
        {
            if (array == null) return;
            PdfFont font = state.Font ?? new PdfFont(this._document, null);
            var text = new StringBuilder();
            double advance = 0;
            foreach (PdfValue item in array.Items)
            {
                PdfString value = item as PdfString;
                PdfNumber adjustment = item as PdfNumber;
                if (value != null)
                {
                    text.Append(font.Decode(value.Bytes));
                    advance += font.Measure(
                        value.Bytes,
                        Math.Abs(state.FontSize),
                        state.CharacterSpacing,
                        state.WordSpacing,
                        state.HorizontalScale);
                }
                else if (adjustment != null)
                {
                    double movement = -adjustment.Value / 1000.0 * Math.Abs(state.FontSize) * state.HorizontalScale;
                    if (movement > Math.Abs(state.FontSize) * 0.20 && text.Length > 0 && !char.IsWhiteSpace(text[text.Length - 1]))
                    {
                        text.Append(' ');
                    }
                    advance += movement;
                }
            }
            Capture(state, text.ToString(), advance, inArtifact, font.IsBold, font.IsItalic, semantic);
            state.TextMatrix = Matrix.Multiply(state.TextMatrix, Matrix.Translation(advance, 0));
        }

        private void Capture(
            ContentState state,
            string text,
            double advance,
            bool inArtifact,
            bool bold,
            bool italic,
            SemanticInfo semantic)
        {
            if (string.IsNullOrEmpty(text) || (inArtifact && !this._includeArtifacts)) return;
            if (this._fragments.Count >= MaximumTextFragmentsPerPage)
            {
                throw new PdfExtractionException(
                    "PDF_RESOURCE_LIMIT",
                    "A page exceeded the " + MaximumTextFragmentsPerPage + " text-fragment processing limit.");
            }
            double startX = state.TextMatrix.E;
            double startY = state.TextMatrix.F + state.Rise;
            double endX = state.TextMatrix.TransformX(advance, state.Rise);
            double endY = state.TextMatrix.TransformY(advance, state.Rise);
            double x = state.Ctm.TransformX(startX, startY);
            double y = state.Ctm.TransformY(startX, startY);
            double transformedEndX = state.Ctm.TransformX(endX, endY);
            double transformedEndY = state.Ctm.TransformY(endX, endY);
            this._fragments.Add(new TextFragment
            {
                X = x,
                Y = y,
                EndX = transformedEndX,
                EndY = transformedEndY,
                FontSize = Math.Max(1, Math.Abs(state.FontSize)),
                Text = text,
                IsBold = bold,
                IsItalic = italic,
                IsSuperscript = state.Rise > Math.Abs(state.FontSize) * 0.15,
                IsSubscript = state.Rise < -Math.Abs(state.FontSize) * 0.15,
                Semantic = semantic,
                Sequence = this._sequence++
            });
        }

        private static void MoveTextLine(ContentState state, double x, double y)
        {
            state.TextLineMatrix = Matrix.Multiply(state.TextLineMatrix, Matrix.Translation(x, y));
            state.TextMatrix = state.TextLineMatrix;
        }

        private static Matrix MatrixFromOperands(List<PdfValue> operands, int start)
        {
            return new Matrix(
                Number(operands[start], 1), Number(operands[start + 1], 0),
                Number(operands[start + 2], 0), Number(operands[start + 3], 1),
                Number(operands[start + 4], 0), Number(operands[start + 5], 0));
        }

        private static double Number(PdfValue value, double defaultValue)
        {
            PdfNumber number = value as PdfNumber;
            return number == null ? defaultValue : number.Value;
        }

        private void ApplyPageRotation()
        {
            int rotation = ((this._page.Rotation % 360) + 360) % 360;
            if (rotation == 0) return;
            foreach (TextFragment fragment in this._fragments)
            {
                double x = fragment.X;
                double y = fragment.Y;
                double endX = fragment.EndX;
                double endY = fragment.EndY;
                if (rotation == 90)
                {
                    fragment.X = y; fragment.Y = this._page.Width - x;
                    fragment.EndX = endY; fragment.EndY = this._page.Width - endX;
                }
                else if (rotation == 180)
                {
                    fragment.X = this._page.Width - x; fragment.Y = this._page.Height - y;
                    fragment.EndX = this._page.Width - endX; fragment.EndY = this._page.Height - endY;
                }
                else if (rotation == 270)
                {
                    fragment.X = this._page.Height - y; fragment.Y = x;
                    fragment.EndX = this._page.Height - endY; fragment.EndY = endX;
                }
            }
        }

        private sealed class TextLine
        {
            public readonly List<TextFragment> Fragments = new List<TextFragment>();
            public double Baseline;
            public double MaxFontSize;
            public int FirstSequence = int.MaxValue;
        }

        private sealed class MarkdownBlock
        {
            public int Order;
            public int FirstSequence;
            public double Top;
            public string Role;
            public bool IsListItem;
            public SemanticTable Table;
            public readonly List<TextFragment> Fragments = new List<TextFragment>();
        }

        private static string ReconstructText(List<TextFragment> fragments)
        {
            var useful = fragments
                .Where(fragment => !string.IsNullOrWhiteSpace(NormalizeFragment(fragment.Text)))
                .OrderByDescending(fragment => fragment.Y)
                .ThenBy(fragment => fragment.X)
                .ThenBy(fragment => fragment.Sequence)
                .ToList();
            if (useful.Count == 0) return string.Empty;

            var lines = new List<TextLine>();
            foreach (TextFragment fragment in useful)
            {
                TextLine best = null;
                double bestDistance = double.MaxValue;
                for (int i = Math.Max(0, lines.Count - 4); i < lines.Count; i++)
                {
                    TextLine candidate = lines[i];
                    double tolerance = Math.Max(2.25, Math.Min(candidate.MaxFontSize, fragment.FontSize) * 0.70);
                    double distance = Math.Abs(candidate.Baseline - fragment.Y);
                    if (distance <= tolerance && distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
                if (best == null)
                {
                    best = new TextLine { Baseline = fragment.Y, MaxFontSize = fragment.FontSize };
                    lines.Add(best);
                }
                int count = best.Fragments.Count;
                best.Baseline = (best.Baseline * count + fragment.Y) / (count + 1);
                best.MaxFontSize = Math.Max(best.MaxFontSize, fragment.FontSize);
                best.FirstSequence = Math.Min(best.FirstSequence, fragment.Sequence);
                best.Fragments.Add(fragment);
            }

            lines = lines.OrderByDescending(line => line.Baseline).ThenBy(line => line.FirstSequence).ToList();
            var output = new StringBuilder();
            TextLine previousLine = null;
            foreach (TextLine line in lines)
            {
                line.Fragments.Sort(delegate(TextFragment left, TextFragment right)
                {
                    int x = left.X.CompareTo(right.X);
                    return x != 0 ? x : left.Sequence.CompareTo(right.Sequence);
                });
                string lineText = BuildLine(line.Fragments);
                if (string.IsNullOrWhiteSpace(lineText)) continue;
                if (output.Length > 0)
                {
                    double verticalGap = previousLine == null ? 0 : previousLine.Baseline - line.Baseline;
                    output.Append(verticalGap > Math.Max(previousLine.MaxFontSize, line.MaxFontSize) * 1.75 ? "\n\n" : "\n");
                }
                output.Append(lineText.Trim());
                previousLine = line;
            }
            return output.ToString();
        }

        private static string BuildLine(List<TextFragment> fragments)
        {
            var result = new StringBuilder();
            TextFragment previous = null;
            bool previousEndedWhitespace = false;
            foreach (TextFragment fragment in fragments)
            {
                string text = NormalizeFragment(fragment.Text);
                if (string.IsNullOrWhiteSpace(text)) continue;
                bool beginsWhitespace = char.IsWhiteSpace(text[0]);
                bool endsWhitespace = char.IsWhiteSpace(text[text.Length - 1]);
                text = text.Trim();
                if (result.Length > 0 && previous != null)
                {
                    double previousEnd = Math.Max(previous.X, previous.EndX);
                    double gap = fragment.X - previousEnd;
                    double referenceSize = Math.Max(1, Math.Min(previous.FontSize, fragment.FontSize));
                    char last = result[result.Length - 1];
                    char first = text[0];
                    bool punctuationJoin = ".,;:!?)]}%".IndexOf(first) >= 0;
                    bool openingJoin = "([{#$".IndexOf(last) >= 0;
                    if (!char.IsWhiteSpace(last) && !char.IsWhiteSpace(first) && !punctuationJoin && !openingJoin)
                    {
                        if (gap > referenceSize * 2.2) result.Append('\t');
                        else if (previousEndedWhitespace || beginsWhitespace || gap > referenceSize * 0.12) result.Append(' ');
                    }
                }
                result.Append(text);
                previous = fragment;
                previousEndedWhitespace = endsWhitespace;
            }
            return result.ToString();
        }

        private static PageContentResult ReconstructMarkdown(
            List<TextFragment> fragments,
            List<SemanticTable> semanticTables,
            int pageObjectNumber)
        {
            var useful = fragments
                .Where(fragment => !string.IsNullOrWhiteSpace(NormalizeFragment(fragment.Text)))
                .ToList();
            if (useful.Count == 0)
            {
                return new PageContentResult { Text = string.Empty, TableCount = 0, UsedTaggedStructure = false };
            }

            bool tagged = useful.Any(fragment => fragment.Semantic != null);
            var used = new HashSet<TextFragment>();
            var blocks = new List<MarkdownBlock>();

            foreach (IGrouping<int, TextFragment> tableGroup in useful
                .Where(fragment => fragment.Semantic != null && fragment.Semantic.TableId != 0)
                .GroupBy(fragment => fragment.Semantic.TableId))
            {
                List<TextFragment> tableFragments = tableGroup.ToList();
                SemanticTable table = semanticTables.FirstOrDefault(candidate => candidate.Id == tableGroup.Key);
                if (table == null)
                {
                    table = BuildSyntheticTable(tableGroup.Key, tableFragments);
                }
                var block = new MarkdownBlock
                {
                    Table = table,
                    Order = tableFragments.Min(fragment => fragment.Semantic.StructureOrder),
                    FirstSequence = tableFragments.Min(fragment => fragment.Sequence),
                    Top = tableFragments.Max(fragment => fragment.Y),
                    Role = "Table"
                };
                block.Fragments.AddRange(tableFragments);
                blocks.Add(block);
                foreach (TextFragment fragment in tableFragments) used.Add(fragment);
            }

            foreach (IGrouping<int, TextFragment> listGroup in useful
                .Where(fragment => !used.Contains(fragment) && fragment.Semantic != null && fragment.Semantic.ListItemId != 0)
                .GroupBy(fragment => fragment.Semantic.ListItemId))
            {
                List<TextFragment> listFragments = listGroup.ToList();
                var block = CreateMarkdownBlock(listFragments, "LI");
                block.IsListItem = true;
                blocks.Add(block);
                foreach (TextFragment fragment in listFragments) used.Add(fragment);
            }

            foreach (IGrouping<int, TextFragment> blockGroup in useful
                .Where(fragment => !used.Contains(fragment) && fragment.Semantic != null && fragment.Semantic.BlockId != 0)
                .GroupBy(fragment => fragment.Semantic.BlockId))
            {
                List<TextFragment> blockFragments = blockGroup.ToList();
                string role = blockFragments
                    .Select(fragment => fragment.Semantic.BlockRole)
                    .FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate)) ?? "P";
                blocks.Add(CreateMarkdownBlock(blockFragments, role));
                foreach (TextFragment fragment in blockFragments) used.Add(fragment);
            }

            foreach (IGrouping<int, TextFragment> taggedRemainder in useful
                .Where(fragment => !used.Contains(fragment) && fragment.Semantic != null)
                .GroupBy(fragment => fragment.Semantic.StructureOrder))
            {
                List<TextFragment> remainder = taggedRemainder.ToList();
                blocks.Add(CreateMarkdownBlock(remainder, "P"));
                foreach (TextFragment fragment in remainder) used.Add(fragment);
            }

            List<TextFragment> untagged = useful.Where(fragment => !used.Contains(fragment)).ToList();
            if (untagged.Count > 0)
            {
                foreach (TextLine line in CreateLines(untagged))
                {
                    var block = new MarkdownBlock
                    {
                        Order = int.MaxValue,
                        FirstSequence = line.FirstSequence,
                        Top = line.Baseline,
                        Role = InferLineRole(line, useful)
                    };
                    block.Fragments.AddRange(line.Fragments);
                    blocks.Add(block);
                }
            }

            blocks = blocks
                .OrderByDescending(block => block.Top)
                .ThenBy(block => block.Order)
                .ThenBy(block => block.FirstSequence)
                .ToList();

            var output = new StringBuilder();
            int tableCount = 0;
            for (int index = 0; index < blocks.Count; index++)
            {
                MarkdownBlock block = blocks[index];
                string markdown;
                if (block.Table != null)
                {
                    markdown = RenderSemanticTable(block.Table, block.Fragments, pageObjectNumber);
                    if (!string.IsNullOrWhiteSpace(markdown)) tableCount++;
                }
                else markdown = RenderMarkdownBlock(block);
                if (string.IsNullOrWhiteSpace(markdown)) continue;
                if (output.Length > 0) output.Append("\n\n");
                output.Append(markdown.Trim());
            }

            return new PageContentResult
            {
                Text = output.ToString(),
                TableCount = tableCount,
                UsedTaggedStructure = tagged
            };
        }

        private static MarkdownBlock CreateMarkdownBlock(List<TextFragment> fragments, string role)
        {
            var block = new MarkdownBlock
            {
                Role = role,
                Order = fragments.Where(fragment => fragment.Semantic != null)
                    .Select(fragment => fragment.Semantic.StructureOrder)
                    .DefaultIfEmpty(int.MaxValue)
                    .Min(),
                FirstSequence = fragments.Min(fragment => fragment.Sequence),
                Top = fragments.Max(fragment => fragment.Y)
            };
            block.Fragments.AddRange(fragments);
            return block;
        }

        private static SemanticTable BuildSyntheticTable(int tableId, List<TextFragment> fragments)
        {
            var table = new SemanticTable { Id = tableId, Order = 0 };
            foreach (IGrouping<int, TextFragment> rowGroup in fragments
                .Where(fragment => fragment.Semantic.RowId != 0)
                .GroupBy(fragment => fragment.Semantic.RowId)
                .OrderBy(group => group.Min(fragment => fragment.Semantic.StructureOrder)))
            {
                var row = new SemanticRow
                {
                    Id = rowGroup.Key,
                    Order = rowGroup.Min(fragment => fragment.Semantic.StructureOrder)
                };
                foreach (IGrouping<int, TextFragment> cellGroup in rowGroup
                    .Where(fragment => fragment.Semantic.CellId != 0)
                    .GroupBy(fragment => fragment.Semantic.CellId)
                    .OrderBy(group => group.Min(fragment => fragment.Semantic.StructureOrder)))
                {
                    TextFragment first = cellGroup.First();
                    row.Cells.Add(new SemanticCell
                    {
                        Id = cellGroup.Key,
                        Order = cellGroup.Min(fragment => fragment.Semantic.StructureOrder),
                        Role = first.Semantic.CellRole,
                        PageObjectNumber = 0
                    });
                }
                table.Rows.Add(row);
            }
            return table;
        }

        private static string RenderSemanticTable(
            SemanticTable table,
            List<TextFragment> fragments,
            int pageObjectNumber)
        {
            var fragmentsByCell = fragments
                .Where(fragment => fragment.Semantic != null && fragment.Semantic.CellId != 0)
                .GroupBy(fragment => fragment.Semantic.CellId)
                .ToDictionary(group => group.Key, group => group.ToList());
            var rows = table.Rows
                .Where(row => row.PageObjectNumber == pageObjectNumber ||
                    row.Cells.Any(cell => cell.PageObjectNumber == pageObjectNumber || fragmentsByCell.ContainsKey(cell.Id)))
                .OrderBy(row => row.Order)
                .ToList();
            if (rows.Count == 0)
            {
                rows = BuildSyntheticTable(table.Id, fragments).Rows;
            }
            if (rows.Count == 0 || fragmentsByCell.Count == 0) return string.Empty;

            int columnCount = rows.Max(row => Math.Max(1, row.Cells.Sum(cell => Math.Max(1, cell.ColumnSpan))));
            columnCount = Math.Min(64, Math.Max(1, columnCount));
            var renderedRows = new List<List<string>>();
            var rowSpans = new int[columnCount];
            foreach (SemanticRow row in rows)
            {
                List<List<string>> splitRows;
                if (TrySplitCategoryRow(row, fragmentsByCell, columnCount, out splitRows))
                {
                    renderedRows.AddRange(splitRows);
                    continue;
                }
                var rendered = Enumerable.Repeat(string.Empty, columnCount).ToList();
                int column = 0;
                foreach (SemanticCell cell in row.Cells.OrderBy(candidate => candidate.Order))
                {
                    while (column < columnCount && rowSpans[column] > 0) column++;
                    if (column >= columnCount) break;
                    List<TextFragment> cellFragments;
                    string value = fragmentsByCell.TryGetValue(cell.Id, out cellFragments)
                        ? RenderCell(cellFragments)
                        : string.Empty;
                    rendered[column] = value;
                    int columnSpan = Math.Min(Math.Max(1, cell.ColumnSpan), columnCount - column);
                    int rowSpan = Math.Max(1, cell.RowSpan);
                    for (int offset = 0; offset < columnSpan; offset++)
                    {
                        if (rowSpan > 1) rowSpans[column + offset] = Math.Max(rowSpans[column + offset], rowSpan);
                    }
                    column += columnSpan;
                }
                renderedRows.Add(rendered);
                for (int i = 0; i < rowSpans.Length; i++) if (rowSpans[i] > 0) rowSpans[i]--;
            }

            if (renderedRows.All(row => row.All(string.IsNullOrWhiteSpace))) return string.Empty;
            var output = new StringBuilder();
            AppendMarkdownTableRow(output, renderedRows[0]);
            output.Append('\n');
            AppendMarkdownTableRow(output, Enumerable.Repeat("---", columnCount).ToList());
            for (int rowIndex = 1; rowIndex < renderedRows.Count; rowIndex++)
            {
                output.Append('\n');
                AppendMarkdownTableRow(output, renderedRows[rowIndex]);
            }
            return output.ToString();
        }

        private static bool TrySplitCategoryRow(
            SemanticRow row,
            Dictionary<int, List<TextFragment>> fragmentsByCell,
            int columnCount,
            out List<List<string>> renderedRows)
        {
            renderedRows = null;
            List<SemanticCell> cells = row.Cells.OrderBy(cell => cell.Order).ToList();
            if (cells.Count < 2 || cells.Any(cell => cell.ColumnSpan != 1 || cell.RowSpan != 1)) return false;
            var linesByCell = new List<List<TextLine>>();
            foreach (SemanticCell cell in cells)
            {
                List<TextFragment> cellFragments;
                linesByCell.Add(fragmentsByCell.TryGetValue(cell.Id, out cellFragments)
                    ? CreateLines(cellFragments)
                    : new List<TextLine>());
            }
            List<TextLine> leading = linesByCell[0];
            if (leading.Count < 2 || !LineIsBold(leading[0]) || LineIsBold(leading[1])) return false;
            int dataRows = leading.Count - 1;
            if (dataRows > 24 || linesByCell.Skip(1).All(lines => lines.Count == 0)) return false;
            if (linesByCell.Skip(1).Any(lines => lines.Count != 0 && lines.Count != dataRows)) return false;

            int aligned = 0;
            int comparisons = 0;
            for (int cellIndex = 1; cellIndex < linesByCell.Count; cellIndex++)
            {
                List<TextLine> lines = linesByCell[cellIndex];
                if (lines.Count == 0) continue;
                for (int lineIndex = 0; lineIndex < dataRows; lineIndex++)
                {
                    comparisons++;
                    double tolerance = Math.Max(3.0, Math.Min(leading[lineIndex + 1].MaxFontSize, lines[lineIndex].MaxFontSize) * 0.55);
                    if (Math.Abs(leading[lineIndex + 1].Baseline - lines[lineIndex].Baseline) <= tolerance) aligned++;
                }
            }
            if (comparisons == 0 || aligned * 5 < comparisons * 4) return false;

            renderedRows = new List<List<string>>();
            var category = Enumerable.Repeat(string.Empty, columnCount).ToList();
            category[0] = BuildRichLine(leading[0].Fragments, true).Trim();
            renderedRows.Add(category);
            for (int lineIndex = 0; lineIndex < dataRows; lineIndex++)
            {
                var rendered = Enumerable.Repeat(string.Empty, columnCount).ToList();
                rendered[0] = BuildRichLine(leading[lineIndex + 1].Fragments, true).Trim();
                for (int cellIndex = 1; cellIndex < Math.Min(cells.Count, columnCount); cellIndex++)
                {
                    List<TextLine> lines = linesByCell[cellIndex];
                    if (lines.Count > lineIndex) rendered[cellIndex] = BuildRichLine(lines[lineIndex].Fragments, true).Trim();
                }
                renderedRows.Add(rendered);
            }
            return true;
        }

        private static bool LineIsBold(TextLine line)
        {
            List<TextFragment> useful = line.Fragments
                .Where(fragment => !string.IsNullOrWhiteSpace(NormalizeFragment(fragment.Text)))
                .ToList();
            return useful.Count > 0 && useful.Count(fragment => fragment.IsBold) * 4 >= useful.Count * 3;
        }

        private static void AppendMarkdownTableRow(StringBuilder output, List<string> cells)
        {
            output.Append('|');
            foreach (string cell in cells)
            {
                output.Append(cell ?? string.Empty);
                output.Append('|');
            }
        }

        private static string RenderCell(List<TextFragment> fragments)
        {
            var lines = CreateLines(fragments);
            var output = new StringBuilder();
            foreach (TextLine line in lines)
            {
                string text = BuildRichLine(line.Fragments, true);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (output.Length > 0) output.Append("<br>");
                output.Append(text.Trim());
            }
            return output.ToString();
        }

        private static string RenderMarkdownBlock(MarkdownBlock block)
        {
            List<TextLine> lines = CreateLines(block.Fragments);
            if (lines.Count == 0) return string.Empty;
            if (block.IsListItem)
            {
                List<TextFragment> labels = block.Fragments
                    .Where(fragment => fragment.Semantic != null && fragment.Semantic.IsListLabel)
                    .ToList();
                List<TextFragment> bodies = block.Fragments
                    .Where(fragment => fragment.Semantic == null || !fragment.Semantic.IsListLabel)
                    .ToList();
                string label = labels.Count == 0 ? string.Empty : RenderFlowingText(CreateLines(labels));
                string body = bodies.Count == 0 ? RenderFlowingText(lines) : RenderFlowingText(CreateLines(bodies));
                return RenderListItem(label, body);
            }

            string text = RenderFlowingText(lines);
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            if (!string.IsNullOrEmpty(block.Role) && block.Role.Length == 2 && block.Role[0] == 'H' &&
                block.Role[1] >= '1' && block.Role[1] <= '6')
            {
                int level = block.Role[1] - '0';
                return new string('#', level) + " " + StripOuterBold(text);
            }
            if (string.Equals(block.Role, "Quote", StringComparison.Ordinal)) return "> " + text;
            if (string.Equals(block.Role, "Code", StringComparison.Ordinal)) return "```\n" + UnescapeMarkdown(text) + "\n```";
            if (string.Equals(block.Role, "Caption", StringComparison.Ordinal) && !IsOuterBold(text)) return "**" + text + "**";
            if (LooksLikeBullet(text)) return NormalizeBullet(text);
            return text;
        }

        private static string RenderFlowingText(List<TextLine> lines)
        {
            var output = new StringBuilder();
            foreach (TextLine line in lines)
            {
                string text = BuildRichLine(line.Fragments, false).Trim();
                if (text.Length == 0) continue;
                if (output.Length > 0)
                {
                    bool dehyphenate = output[output.Length - 1] == '-' && StartsWithLowercaseText(text);
                    if (dehyphenate) output.Length--;
                    else output.Append(' ');
                }
                output.Append(text);
            }
            return output.ToString();
        }

        private static string RenderListItem(string label, string body)
        {
            label = StripMarkdownFormatting(label).Trim();
            body = body.Trim();
            if (label.Length == 0) return NormalizeBullet(body);
            if (Regex.IsMatch(label, @"^\d+[\.)]$") || Regex.IsMatch(label, @"^\d+\.$"))
            {
                return label.TrimEnd('.', ')') + ". " + body;
            }
            if (label == "-" || label == "–" || label == "—" || label == "•" || label == "▪" ||
                label == "◦" || label == "●" || label == "o")
            {
                return "- " + body;
            }
            return "- " + (label.Length == 0 ? string.Empty : label + " ") + body;
        }

        private static bool LooksLikeBullet(string text)
        {
            string plain = StripMarkdownFormatting(text).TrimStart();
            return plain.StartsWith("•", StringComparison.Ordinal) || plain.StartsWith("▪", StringComparison.Ordinal) ||
                plain.StartsWith("◦", StringComparison.Ordinal) || plain.StartsWith("●", StringComparison.Ordinal);
        }

        private static string NormalizeBullet(string text)
        {
            string trimmed = text.TrimStart();
            if (trimmed.Length > 0 && "•▪◦●".IndexOf(trimmed[0]) >= 0) trimmed = trimmed.Substring(1).TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal)) return trimmed;
            return "- " + trimmed;
        }

        private static string InferLineRole(TextLine line, List<TextFragment> pageFragments)
        {
            var sizes = pageFragments.Select(fragment => fragment.FontSize).OrderBy(size => size).ToList();
            double median = sizes.Count == 0 ? 12 : sizes[sizes.Count / 2];
            string plain = BuildLine(line.Fragments).Trim();
            bool mostlyBold = line.Fragments.Count > 0 &&
                line.Fragments.Count(fragment => fragment.IsBold) * 2 >= line.Fragments.Count;
            if (plain.Length <= 180 && line.MaxFontSize >= median * 1.45) return "H1";
            if (plain.Length <= 180 && line.MaxFontSize >= median * 1.22) return "H2";
            if (plain.Length <= 140 && mostlyBold && plain.Any(char.IsLetter)) return "H3";
            return "P";
        }

        private static List<TextLine> CreateLines(IEnumerable<TextFragment> source)
        {
            var useful = source
                .Where(fragment => !string.IsNullOrWhiteSpace(NormalizeFragment(fragment.Text)))
                .OrderByDescending(fragment => fragment.Y)
                .ThenBy(fragment => fragment.X)
                .ThenBy(fragment => fragment.Sequence)
                .ToList();
            var lines = new List<TextLine>();
            foreach (TextFragment fragment in useful)
            {
                TextLine best = null;
                double bestDistance = double.MaxValue;
                for (int i = Math.Max(0, lines.Count - 6); i < lines.Count; i++)
                {
                    TextLine candidate = lines[i];
                    double tolerance = Math.Max(2.25, Math.Min(candidate.MaxFontSize, fragment.FontSize) * 0.70);
                    double distance = Math.Abs(candidate.Baseline - fragment.Y);
                    if (distance <= tolerance && distance < bestDistance)
                    {
                        best = candidate;
                        bestDistance = distance;
                    }
                }
                if (best == null)
                {
                    best = new TextLine { Baseline = fragment.Y, MaxFontSize = fragment.FontSize };
                    lines.Add(best);
                }
                int count = best.Fragments.Count;
                best.Baseline = (best.Baseline * count + fragment.Y) / (count + 1);
                best.MaxFontSize = Math.Max(best.MaxFontSize, fragment.FontSize);
                best.FirstSequence = Math.Min(best.FirstSequence, fragment.Sequence);
                best.Fragments.Add(fragment);
            }
            foreach (TextLine line in lines)
            {
                line.Fragments.Sort(delegate(TextFragment left, TextFragment right)
                {
                    int x = left.X.CompareTo(right.X);
                    return x != 0 ? x : left.Sequence.CompareTo(right.Sequence);
                });
            }
            return lines.OrderByDescending(line => line.Baseline).ThenBy(line => line.FirstSequence).ToList();
        }

        private static string BuildRichLine(List<TextFragment> fragments, bool inTable)
        {
            var output = new StringBuilder();
            TextFragment previous = null;
            string previousText = null;
            bool previousEndedWhitespace = false;
            foreach (TextFragment fragment in fragments)
            {
                string raw = NormalizeFragment(fragment.Text);
                if (string.IsNullOrWhiteSpace(raw)) continue;
                bool beginsWhitespace = char.IsWhiteSpace(raw[0]);
                bool endsWhitespace = char.IsWhiteSpace(raw[raw.Length - 1]);
                string text = raw.Trim();
                if (output.Length > 0 && previous != null && !string.IsNullOrEmpty(previousText))
                {
                    double previousEnd = Math.Max(previous.X, previous.EndX);
                    double gap = fragment.X - previousEnd;
                    double referenceSize = Math.Max(1, Math.Min(previous.FontSize, fragment.FontSize));
                    char last = previousText[previousText.Length - 1];
                    char first = text[0];
                    bool punctuationJoin = ".,;:!?)]}%".IndexOf(first) >= 0;
                    bool openingJoin = "([{#$".IndexOf(last) >= 0;
                    if (!char.IsWhiteSpace(last) && !char.IsWhiteSpace(first) && !punctuationJoin && !openingJoin &&
                        (previousEndedWhitespace || beginsWhitespace || gap > referenceSize * 0.12))
                    {
                        output.Append(' ');
                    }
                }
                string escaped = EscapeMarkdown(text, inTable);
                if (fragment.IsBold && fragment.IsItalic) escaped = "***" + escaped + "***";
                else if (fragment.IsBold) escaped = "**" + escaped + "**";
                else if (fragment.IsItalic) escaped = "*" + escaped + "*";
                if (fragment.IsSuperscript) escaped = "<sup>" + escaped + "</sup>";
                else if (fragment.IsSubscript) escaped = "<sub>" + escaped + "</sub>";
                output.Append(escaped);
                previous = fragment;
                previousText = text;
                previousEndedWhitespace = endsWhitespace;
            }
            string result = output.ToString();
            while (result.IndexOf("****", StringComparison.Ordinal) >= 0)
            {
                result = result.Replace("****", string.Empty);
            }
            while (result.IndexOf("** **", StringComparison.Ordinal) >= 0)
            {
                result = result.Replace("** **", " ");
            }
            while (result.IndexOf("* *", StringComparison.Ordinal) >= 0)
            {
                result = result.Replace("* *", " ");
            }
            return result;
        }

        private static string EscapeMarkdown(string value, bool inTable)
        {
            string result = value.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_");
            if (inTable) result = result.Replace("|", "\\|");
            return result;
        }

        private static string StripMarkdownFormatting(string value)
        {
            return value.Replace("**", string.Empty).Replace("*", string.Empty)
                .Replace("<sup>", string.Empty).Replace("</sup>", string.Empty)
                .Replace("<sub>", string.Empty).Replace("</sub>", string.Empty);
        }

        private static bool IsOuterBold(string value)
        {
            return value.Length >= 4 && value.StartsWith("**", StringComparison.Ordinal) && value.EndsWith("**", StringComparison.Ordinal);
        }

        private static string StripOuterBold(string value)
        {
            return IsOuterBold(value) ? value.Substring(2, value.Length - 4) : value;
        }

        private static string UnescapeMarkdown(string value)
        {
            return value.Replace("\\*", "*").Replace("\\_", "_").Replace("\\\\", "\\");
        }

        private static bool StartsWithLowercaseText(string value)
        {
            string plain = StripMarkdownFormatting(value);
            foreach (char current in plain)
            {
                if (char.IsLetter(current)) return char.IsLower(current);
            }
            return false;
        }

        private static string NormalizeFragment(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var result = new StringBuilder(value.Length);
            bool whitespace = false;
            for (int i = 0; i < value.Length; i++)
            {
                char current = value[i];
                if (current == '\0' || current == '\uFFFD')
                {
                    if (current == '\uFFFD') result.Append(current);
                    continue;
                }
                if (char.IsWhiteSpace(current))
                {
                    whitespace = true;
                    continue;
                }
                if (whitespace && result.Length > 0) result.Append(' ');
                whitespace = false;
                result.Append(current);
            }
            if (whitespace && result.Length > 0) result.Append(' ');
            return result.ToString();
        }
    }
}
