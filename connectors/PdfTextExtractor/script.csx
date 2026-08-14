using System;
using System.Collections;
using System.Collections.Generic;
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
    private const int DefaultMaxInputBytes = 20 * 1024 * 1024;
    private const int AbsoluteMaxInputBytes = 32 * 1024 * 1024;
    private const int DefaultMaxOutputCharacters = 2 * 1024 * 1024;
    private const int AbsoluteMaxOutputCharacters = 6 * 1024 * 1024;
    private const int MaximumPageCount = 1000;
    private const int MaximumDecodedStreamBytes = 32 * 1024 * 1024;
    private const long MaximumTotalDecodedStreamBytes = 96L * 1024 * 1024;
    private const int MaximumTextFragmentsPerPage = 250000;
    private const int MaximumStructureElements = 500000;
    private const int MaximumStructureDepth = 128;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);
        if (string.Equals(operationId, "ExtractPdfText", StringComparison.Ordinal))
        {
            return await HandleExtractPdfAsync(false, false).ConfigureAwait(false);
        }
        if (string.Equals(operationId, "ExtractPdfPageChunks", StringComparison.Ordinal))
        {
            return await HandleExtractPdfAsync(true, false).ConfigureAwait(false);
        }
        if (string.Equals(operationId, "ExtractPdfMarkdown", StringComparison.Ordinal))
        {
            return await HandleExtractPdfAsync(false, true).ConfigureAwait(false);
        }
        if (string.Equals(operationId, "ExtractPdfMarkdownPageChunks", StringComparison.Ordinal))
        {
            return await HandleExtractPdfAsync(true, true).ConfigureAwait(false);
        }
        else
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "UNKNOWN_OPERATION",
                "Unknown operation: " + operationId);
        }
    }

    private async Task<HttpResponseMessage> HandleExtractPdfAsync(bool returnPageChunks, bool richMarkdown)
    {
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

        int maxInputBytes = GetBoundedInteger(request, "maxInputBytes", DefaultMaxInputBytes, 1024, AbsoluteMaxInputBytes);
        if (pdfBytes.Length > maxInputBytes)
        {
            return CreateErrorResponse(
                HttpStatusCode.RequestEntityTooLarge,
                "PDF_TOO_LARGE",
                "The decoded PDF is " + pdfBytes.Length + " bytes; the configured limit is " + maxInputBytes + " bytes.");
        }

        if (!LooksLikePdf(pdfBytes))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_PDF", "The decoded content does not contain a PDF header.");
        }

        ExtractionOptions options = new ExtractionOptions
        {
            StartPage = GetBoundedInteger(request, "startPage", 1, 1, MaximumPageCount),
            EndPage = GetNullableBoundedInteger(request, "endPage", 1, MaximumPageCount),
            IncludeArtifacts = GetBoolean(request, "includeArtifacts", false),
            IncludePageBreaks = GetBoolean(request, "includePageBreaks", true),
            ReturnPageChunks = returnPageChunks,
            RichMarkdown = richMarkdown,
            FileName = GetOptionalString(request, "fileName", 1024),
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
            ExtractionResult result = extractor.Extract();

            if (returnPageChunks)
            {
                return CreateJsonResponse(HttpStatusCode.OK, result.PageChunks);
            }

            JObject responseBody = new JObject
            {
                ["text"] = result.Text,
                ["pageCount"] = result.PageCount,
                ["pagesExtracted"] = result.PagesExtracted,
                ["startPage"] = result.StartPage,
                ["endPage"] = result.EndPage,
                ["characterCount"] = result.Text.Length,
                ["hasTextLayer"] = result.HasTextLayer,
                ["truncated"] = result.Truncated,
                ["warnings"] = new JArray(result.Warnings)
            };
            if (richMarkdown)
            {
                responseBody["contentType"] = "text/markdown";
                responseBody["tableCount"] = result.TableCount;
                responseBody["usedTaggedStructure"] = result.UsedTaggedStructure;
            }

            return CreateJsonResponse(HttpStatusCode.OK, responseBody);
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
                "PDF_EXTRACTION_FAILED",
                "The PDF text layer could not be extracted: " + ex.Message);
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
            return string.Equals(decoded, "ExtractPdfText", StringComparison.Ordinal) ||
                string.Equals(decoded, "ExtractPdfPageChunks", StringComparison.Ordinal) ||
                string.Equals(decoded, "ExtractPdfMarkdown", StringComparison.Ordinal) ||
                string.Equals(decoded, "ExtractPdfMarkdownPageChunks", StringComparison.Ordinal)
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

    private static int? GetNullableBoundedInteger(JObject body, string name, int minimum, int maximum)
    {
        JToken token = body[name];
        int value;
        if (token == null || token.Type == JTokenType.Null || !int.TryParse(token.ToString(), out value))
        {
            return null;
        }

        if (value < minimum) value = minimum;
        if (value > maximum) value = maximum;
        return value;
    }

    private static bool GetBoolean(JObject body, string name, bool defaultValue)
    {
        JToken token = body[name];
        bool value;
        return token != null && bool.TryParse(token.ToString(), out value) ? value : defaultValue;
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
