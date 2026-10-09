using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// A deliberately bounded HTML/CSS compiler, not a browser renderer. No Office,
// NuGet dependency, file-system access, or outbound network request at runtime.
public class Script : ScriptBase
{
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Ct = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    private const string Mime = "application/vnd.openxmlformats-officedocument.presentationml.presentation";
    private const int MaxHtmlBytes = 5 * 1024 * 1024;
    private const int MaxMediaBytes = 20 * 1024 * 1024;
    private const int MaxOutputBytes = 50 * 1024 * 1024;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Diagnostic> _warnings = new List<Diagnostic>();
    private readonly List<Rule> _rules = new List<Rule>();
    private readonly Dictionary<string, Asset> _assets = new Dictionary<string, Asset>();
    private bool _strict = true;
    private int _cells;
    private int _objects;
    private int _mediaBytes;
    private int _slideNumber;

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        try
        {
            string operation = Context.OperationId;
            try { operation = Encoding.UTF8.GetString(Convert.FromBase64String(operation)); }
            catch (FormatException) { }
            if (operation != "ConvertHtmlToPptx" && operation != "ValidateHtmlSlides")
                throw new Problem("UNKNOWN_OPERATION", "Unknown operation.", 400);
            if (Context.Request.Content == null)
                throw new Problem("INVALID_REQUEST", "A JSON request body is required.", 400);
            // The host has already materialized the HTTP body. Bound it before JSON parsing.
            string body = await Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (body.Length > MaxHtmlBytes * 6 + 4096)
                throw new Problem("LIMIT_EXCEEDED", "The request body is too large.", 413);
            JObject request;
            try { request = JObject.Parse(body); }
            catch (JsonException) { throw new Problem("INVALID_REQUEST", "The body must be a JSON object.", 400); }
            if (request["html"] == null || request["html"].Type != JTokenType.String)
                throw new Problem("INVALID_REQUEST", "html must be a string.", 400);
            string html = (string)request["html"];
            if (String.IsNullOrWhiteSpace(html)) throw new Problem("INVALID_REQUEST", "html cannot be empty.", 400);
            if (Encoding.UTF8.GetByteCount(html) > MaxHtmlBytes)
                throw new Problem("LIMIT_EXCEEDED", "HTML exceeds 5 MiB UTF-8.", 413);
            if (request["strict"] != null && request["strict"].Type != JTokenType.Boolean)
                throw new Problem("INVALID_REQUEST", "strict must be a Boolean.", 400);
            _strict = request["strict"] == null || (bool)request["strict"];
            foreach (JProperty prop in request.Properties())
                if (prop.Name != "html" && prop.Name != "fileName" && prop.Name != "strict")
                    Unsupported("REQUEST_PROPERTY", "Unsupported request property: " + prop.Name, "request");
            string fileName = "Presentation.pptx";
            if (request["fileName"] != null)
            {
                if (request["fileName"].Type != JTokenType.String)
                    throw new Problem("INVALID_REQUEST", "fileName must be a string.", 400);
                fileName = (string)request["fileName"];
                if (String.IsNullOrWhiteSpace(fileName) || fileName.Length > 180 ||
                    fileName.Any(c => c < 32 || "\\/:*?\"<>|".IndexOf(c) >= 0))
                    throw new Problem("INVALID_REQUEST", "fileName must be a plain file name of at most 180 characters.", 400);
                if (!fileName.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) fileName += ".pptx";
            }

            try { XmlConvert.VerifyXmlChars(html); }
            catch (XmlException) { throw new Problem("INVALID_HTML", "HTML contains characters that cannot be represented in XML.", 422); }
            Node document = ParseHtml(html);
            foreach (Node style in Descendants(document).Where(n => n.Tag == "style")) ParseSheet(style.TextContent());
            var slides = new List<Slide>();
            CollectSlides(document, Defaults(), slides);
            if (slides.Count == 0) throw new Problem("NO_SLIDES", "Supply one or more <section class=\"slide\"> elements.", 422);
            double width = slides[0].Width, height = slides[0].Height;
            if (slides.Any(s => Math.Abs(s.Width - width) > 0.001 || Math.Abs(s.Height - height) > 0.001))
                throw new Problem("SLIDE_SIZE_MISMATCH", "All slide sections must have the same width and height.", 422);
            var response = new JObject
            {
                ["slideCount"] = slides.Count,
                ["slideWidthPx"] = width,
                ["slideHeightPx"] = height,
                ["objectCount"] = _objects,
                ["cellCount"] = _cells,
                ["warnings"] = JArray.FromObject(_warnings),
                ["slides"] = new JArray(slides.Select(s => new JObject
                {
                    ["index"] = s.Index, ["name"] = s.Name,
                    ["textCount"] = s.TextCount, ["tableCount"] = s.TableCount, ["imageCount"] = s.ImageCount
                }))
            };
            if (operation == "ValidateHtmlSlides") response["valid"] = true;
            else
            {
                byte[] bytes = WritePackage(slides, width, height);
                response["fileName"] = fileName;
                response["contentType"] = Mime;
                response["fileContent"] = Convert.ToBase64String(bytes);
            }
            response["elapsedMilliseconds"] = _clock.ElapsedMilliseconds;
            return JsonResponse(200, response);
        }
        catch (Problem ex)
        {
            return JsonResponse(ex.Status, new JObject { ["error"] = new JObject
            {
                ["code"] = ex.Code, ["message"] = ex.Message,
                ["slideIndex"] = _slideNumber, ["location"] = ex.Location,
                ["warnings"] = JArray.FromObject(_warnings)
            }});
        }
        catch (OperationCanceledException)
        {
            return JsonResponse(408, new JObject { ["error"] = new JObject { ["code"] = "CANCELLED", ["message"] = "Conversion was cancelled." } });
        }
        catch (Exception)
        {
            return JsonResponse(500, new JObject { ["error"] = new JObject { ["code"] = "CONVERSION_FAILED", ["message"] = "Conversion failed unexpectedly." } });
        }
    }

    private HttpResponseMessage JsonResponse(int status, JObject body)
    {
        return new HttpResponseMessage((HttpStatusCode)status) { Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None)) };
    }

    private void CheckBudget()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (_clock.ElapsedMilliseconds > 110000) throw new Problem("TIME_LIMIT", "Conversion exceeded its 110-second safety budget.", 408);
    }

    private void Unsupported(string code, string message, string location)
    {
        if (_strict) throw new Problem(code, message, 422, location);
        Warn(code, message, location);
    }

    private void Warn(string code, string message, string location)
    {
        if (_warnings.Count >= 500) throw new Problem("LIMIT_EXCEEDED", "More than 500 diagnostics were produced.", 413);
        _warnings.Add(new Diagnostic { code = code, message = message, location = location, slideIndex = _slideNumber });
    }

    private sealed class Problem : Exception
    {
        public string Code; public int Status; public string Location;
        public Problem(string code, string message, int status, string location = "") : base(message)
        { Code = code; Status = status; Location = location; }
    }
    private sealed class Diagnostic { public string code; public string message; public string location; public int slideIndex; }
    private sealed class Node
    {
        public string Tag; public string Text; public string Svg;
        public Dictionary<string, string> Attr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<Node> Children = new List<Node>();
        public string Get(string name) { string value; return Attr.TryGetValue(name, out value) ? value : null; }
        public string Location { get { return Tag + (Get("id") == null ? "" : "#" + Get("id")); } }
        public string TextContent() { return Text ?? String.Concat(Children.Select(c => c.TextContent())); }
    }
    private sealed class Rule { public string Selector; public int Specificity; public int Order; public Dictionary<string, string> Values; }
    private sealed class Asset { public string Name; public string Ext; public byte[] Bytes; }
    private sealed class Slide
    {
        public int Index; public string Name; public double Width; public double Height;
        public XElement Xml; public List<XElement> Rels = new List<XElement>();
        public int TextCount; public int TableCount; public int ImageCount; public int NextId = 2;
        public int NextRel = 2;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.Children)
        {
            yield return child;
            foreach (Node descendant in Descendants(child)) yield return descendant;
        }
    }

    // Small tokenizer with explicit end-tag validation. Common HTML void tags and
    // quoted/unquoted attributes are accepted; implied non-void closing tags are not.
    private Node ParseHtml(string html)
    {
        var root = new Node { Tag = "root" };
        var stack = new List<Node> { root };
        int index = 0, count = 0;
        while (index < html.Length)
        {
            CheckBudget();
            if (++count > 30000) throw new Problem("LIMIT_EXCEEDED", "HTML exceeds 30,000 tokens.", 413);
            Node parent = stack[stack.Count - 1];
            if (html[index] != '<')
            {
                int end = html.IndexOf('<', index); if (end < 0) end = html.Length;
                parent.Children.Add(new Node { Tag = "#text", Text = HttpUtility.HtmlDecode(html.Substring(index, end - index)) });
                index = end; continue;
            }
            if (html.IndexOf("<!--", index, StringComparison.Ordinal) == index)
            {
                int end = html.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (end < 0) throw new Problem("INVALID_HTML", "Unclosed HTML comment.", 422);
                index = end + 3; continue;
            }
            int openingIndex = index;
            int tagEnd = FindTagEnd(html, index + 1);
            string token = html.Substring(index + 1, tagEnd - index - 1).Trim();
            if (token.Equals("!doctype html", StringComparison.OrdinalIgnoreCase)) { index = tagEnd + 1; continue; }
            if (token.StartsWith("!") || token.StartsWith("?"))
                throw new Problem("INVALID_HTML", "Only the standard HTML doctype is allowed; XML declarations and DTDs are not.", 422);
            if (token.StartsWith("/"))
            {
                string closing = token.Substring(1).Trim().ToLowerInvariant();
                if (stack.Count == 1 || parent.Tag != closing)
                    throw new Problem("INVALID_HTML", "Unexpected closing tag: " + closing + ". Close non-void elements explicitly.", 422);
                stack.RemoveAt(stack.Count - 1); index = tagEnd + 1; continue;
            }
            bool selfClosing = token.EndsWith("/", StringComparison.Ordinal);
            if (selfClosing) token = token.Substring(0, token.Length - 1).TrimEnd();
            Match match = Regex.Match(token, @"^([A-Za-z][A-Za-z0-9:-]*)", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (!match.Success) throw new Problem("INVALID_HTML", "Invalid opening tag.", 422);
            var node = new Node { Tag = match.Value.ToLowerInvariant() };
            ParseAttributes(token, match.Length, node);
            parent.Children.Add(node);
            index = tagEnd + 1;
            if (node.Tag == "svg" && !selfClosing)
            {
                int end = html.IndexOf("</svg", index, StringComparison.OrdinalIgnoreCase);
                if (end < 0) throw new Problem("INVALID_SVG", "Unclosed SVG.", 422);
                int close = FindTagEnd(html, end + 2);
                if (!html.Substring(end + 2, close - end - 2).Trim().Equals("svg", StringComparison.OrdinalIgnoreCase))
                    throw new Problem("INVALID_SVG", "Invalid SVG closing tag.", 422);
                node.Svg = html.Substring(openingIndex, close + 1 - openingIndex);
                index = close + 1; continue;
            }
            if (node.Tag == "svg" && selfClosing)
            { node.Svg = html.Substring(openingIndex, tagEnd + 1 - openingIndex); continue; }
            if (node.Tag == "style")
            {
                int end = html.IndexOf("</style", index, StringComparison.OrdinalIgnoreCase);
                if (end < 0) throw new Problem("INVALID_HTML", "Unclosed style element.", 422);
                int close = FindTagEnd(html, end + 2);
                if (!html.Substring(end + 2, close - end - 2).Trim().Equals("style", StringComparison.OrdinalIgnoreCase))
                    throw new Problem("INVALID_HTML", "Invalid style closing tag.", 422);
                node.Text = html.Substring(index, end - index); index = close + 1; continue;
            }
            if (node.Tag == "script") throw new Problem("ACTIVE_CONTENT", "JavaScript is not supported.", 422, node.Location);
            bool isVoid = new[] { "img", "br", "hr", "meta", "col", "link", "input", "wbr" }.Contains(node.Tag);
            if (!selfClosing && !isVoid)
            {
                if (stack.Count >= 64) throw new Problem("LIMIT_EXCEEDED", "HTML nesting exceeds 64 levels.", 413);
                stack.Add(node);
            }
        }
        if (stack.Count != 1) throw new Problem("INVALID_HTML", "Unclosed element: " + stack[stack.Count - 1].Tag, 422);
        return root;
    }

    private static int FindTagEnd(string text, int start)
    {
        char quote = '\0';
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c == '\'' || c == '"') quote = c;
            else if (c == '>') return i;
        }
        throw new Problem("INVALID_HTML", "Unclosed tag or attribute quote.", 422);
    }

    private static void ParseAttributes(string token, int index, Node node)
    {
        while (index < token.Length)
        {
            while (index < token.Length && Char.IsWhiteSpace(token[index])) index++;
            if (index == token.Length) break;
            int start = index;
            while (index < token.Length && !Char.IsWhiteSpace(token[index]) && token[index] != '=') index++;
            string name = token.Substring(start, index - start).ToLowerInvariant();
            if (!Regex.IsMatch(name, @"^[A-Za-z_:][A-Za-z0-9_:.-]*$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                throw new Problem("INVALID_HTML", "Invalid attribute name.", 422);
            while (index < token.Length && Char.IsWhiteSpace(token[index])) index++;
            string value = "";
            if (index < token.Length && token[index] == '=')
            {
                index++; while (index < token.Length && Char.IsWhiteSpace(token[index])) index++;
                if (index == token.Length) throw new Problem("INVALID_HTML", "Missing attribute value.", 422);
                char quote = token[index];
                if (quote == '\'' || quote == '"')
                {
                    index++; start = index;
                    while (index < token.Length && token[index] != quote) index++;
                    if (index == token.Length) throw new Problem("INVALID_HTML", "Unclosed attribute.", 422);
                    value = token.Substring(start, index - start); index++;
                }
                else
                {
                    start = index; while (index < token.Length && !Char.IsWhiteSpace(token[index])) index++;
                    value = token.Substring(start, index - start);
                }
            }
            if (node.Attr.ContainsKey(name)) throw new Problem("INVALID_HTML", "Duplicate attribute: " + name, 422, node.Location);
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                throw new Problem("ACTIVE_CONTENT", "Event handlers are not supported.", 422, node.Location);
            node.Attr[name] = HttpUtility.HtmlDecode(value);
        }
    }

    private static Dictionary<string, string> Defaults()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { { "font-family", "Arial" }, { "font-size", "18px" }, { "color", "#000000" },
          { "font-weight", "normal" }, { "font-style", "normal" }, { "text-decoration", "none" },
          { "text-align", "left" }, { "white-space", "normal" } };
    }
    private static readonly string[] Inherited = { "font-family", "font-size", "color", "font-weight", "font-style", "text-decoration", "text-align", "white-space", "line-height" };
    private static readonly string[] AllowedCss = {
        "position", "left", "top", "width", "height", "background", "background-color",
        "font-family", "font-size", "font-weight", "font-style", "color", "text-align", "vertical-align",
        "text-decoration", "white-space", "line-height", "padding", "padding-left", "padding-right",
        "padding-top", "padding-bottom", "border", "border-left", "border-right", "border-top", "border-bottom",
        "border-collapse", "table-layout", "display"
    };

    private Dictionary<string, string> Declarations(string text, string location)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        foreach (string part in text.Split(';'))
        {
            if (String.IsNullOrWhiteSpace(part)) continue;
            int colon = part.IndexOf(':');
            if (colon < 1) throw new Problem("INVALID_CSS", "Expected CSS property:value.", 422, location);
            string key = part.Substring(0, colon).Trim().ToLowerInvariant();
            string value = part.Substring(colon + 1).Trim();
            if (!AllowedCss.Contains(key)) { Unsupported("UNSUPPORTED_CSS", "Unsupported CSS property: " + key, location); continue; }
            if (value.IndexOf("!important", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) >= 0)
            { Unsupported("UNSUPPORTED_CSS", "Unsupported CSS value for " + key, location); continue; }
            if (key == "display" && value != "none" && value != "block")
            { Unsupported("UNSUPPORTED_LAYOUT", "Only display:block and display:none are supported.", location); continue; }
            if (key == "position" && value != "absolute" && value != "relative")
            { Unsupported("UNSUPPORTED_LAYOUT", "Only explicit absolute/relative positioning is supported.", location); continue; }
            result[key == "background" ? "background-color" : key] = value;
        }
        return result;
    }

    private void ParseSheet(string text)
    {
        text = Regex.Replace(text, @"/\*.*?\*/", "", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        int index = 0;
        while (index < text.Length)
        {
            if (String.IsNullOrWhiteSpace(text.Substring(index))) break;
            int open = text.IndexOf('{', index), close = open < 0 ? -1 : text.IndexOf('}', open + 1);
            if (open < 0 || close < 0 || text.IndexOf('{', open + 1, close - open - 1) >= 0)
                throw new Problem("INVALID_CSS", "Stylesheets must contain simple selector { declarations } rules.", 422);
            string selectors = text.Substring(index, open - index).Trim();
            var values = Declarations(text.Substring(open + 1, close - open - 1), "stylesheet");
            foreach (string raw in selectors.Split(','))
            {
                string selector = raw.Trim();
                if (!Regex.IsMatch(selector, @"^(?:[A-Za-z][A-Za-z0-9-]*)?(?:[.#][A-Za-z_][A-Za-z0-9_-]*)?$", RegexOptions.None, TimeSpan.FromSeconds(1)) || selector.Length == 0)
                { Unsupported("UNSUPPORTED_SELECTOR", "Only tag, .class, #id, tag.class and tag#id selectors are supported: " + selector, "stylesheet"); continue; }
                _rules.Add(new Rule { Selector = selector, Values = values, Order = _rules.Count,
                    Specificity = (selector.Contains("#") ? 100 : selector.Contains(".") ? 10 : 0) + (Char.IsLetter(selector[0]) ? 1 : 0) });
                if (_rules.Count > 500) throw new Problem("LIMIT_EXCEEDED", "Stylesheet exceeds 500 selectors.", 413);
            }
            index = close + 1;
        }
    }

    private static bool Matches(Node node, string selector)
    {
        int marker = selector.IndexOfAny(new[] { '.', '#' });
        string tag = marker < 0 ? selector : selector.Substring(0, marker);
        if (tag.Length > 0 && !node.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) return false;
        if (marker < 0) return true;
        string value = selector.Substring(marker + 1);
        return selector[marker] == '#' ? node.Get("id") == value : HasClass(node, value);
    }
    private static bool HasClass(Node node, string value)
    { return (node.Get("class") ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains(value); }

    private Dictionary<string, string> Style(Node node, Dictionary<string, string> parent)
    {
        var style = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in Inherited) if (parent.ContainsKey(key)) style[key] = parent[key];
        if (node.Tag == "b" || node.Tag == "strong" || node.Tag == "th") style["font-weight"] = "bold";
        if (node.Tag == "i" || node.Tag == "em") style["font-style"] = "italic";
        if (node.Tag == "u") style["text-decoration"] = "underline";
        foreach (Rule rule in _rules.Where(r => Matches(node, r.Selector)).OrderBy(r => r.Specificity).ThenBy(r => r.Order))
            foreach (var pair in rule.Values) style[pair.Key] = pair.Value;
        foreach (var pair in Declarations(node.Get("style") ?? "", node.Location)) style[pair.Key] = pair.Value;
        if (node.Get("width") != null && !style.ContainsKey("width")) style["width"] = node.Get("width");
        if (node.Get("height") != null && !style.ContainsKey("height")) style["height"] = node.Get("height");
        ValidateStyle(style, node.Location);
        return style;
    }

    private void ValidateStyle(Dictionary<string, string> style, string location)
    {
        foreach (var item in style.ToArray())
        {
            bool valid = true;
            if (item.Key == "font-weight") valid = new[] { "normal", "bold", "400", "700" }.Contains(item.Value);
            if (item.Key == "font-style") valid = item.Value == "normal" || item.Value == "italic";
            if (item.Key == "text-decoration") valid = item.Value == "none" || item.Value == "underline";
            if (item.Key == "text-align") valid = new[] { "left", "center", "right", "justify" }.Contains(item.Value);
            if (item.Key == "vertical-align") valid = new[] { "top", "middle", "bottom" }.Contains(item.Value);
            if (item.Key == "white-space") valid = item.Value == "normal" || item.Value == "pre-wrap";
            if (item.Key == "table-layout") valid = item.Value == "fixed";
            if (item.Key == "border-collapse") valid = item.Value == "collapse";
            if (item.Key == "background" || item.Key == "background-color" || item.Key == "color") Color(item.Value, location);
            if (!valid) { Unsupported("UNSUPPORTED_CSS", "Unsupported " + item.Key + ": " + item.Value, location); style.Remove(item.Key); }
        }
    }

    private static string Get(Dictionary<string, string> style, string key, string fallback)
    { string value; return style.TryGetValue(key, out value) ? value : fallback; }
    private static double Number(string text, string location)
    {
        try { double value = XmlConvert.ToDouble(text); if (Double.IsInfinity(value) || Double.IsNaN(value)) throw new FormatException(); return value; }
        catch (Exception ex) { if (!(ex is FormatException) && !(ex is OverflowException)) throw; throw new Problem("INVALID_LENGTH", "Invalid finite number: " + text, 422, location); }
    }
    private static double Length(string value, string location, bool percentage = false, double reference = 0)
    {
        Match match = Regex.Match(value.Trim(), @"^([+-]?(?:\d+(?:\.\d*)?|\.\d+))(px|pt|in|cm|mm|%)?$", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) throw new Problem("INVALID_LENGTH", "Use explicit px, pt, in, cm, mm or unitless pixel lengths: " + value, 422, location);
        double result = Number(match.Groups[1].Value, location);
        string unit = match.Groups[2].Value;
        if (unit == "%")
        { if (!percentage) throw new Problem("INVALID_LENGTH", "Percentages are only supported for table columns and line-height.", 422, location); return result * reference / 100; }
        if (unit == "pt") result *= 96.0 / 72;
        if (unit == "in") result *= 96;
        if (unit == "cm") result *= 96 / 2.54;
        if (unit == "mm") result *= 96 / 25.4;
        if (Math.Abs(result) > 20000) throw new Problem("INVALID_LENGTH", "Length exceeds 20,000 pixels.", 422, location);
        return result;
    }
    private static long Emu(double pixels) { return (long)Math.Round(pixels * 9525); }

    private static string Color(string value, string location)
    {
        value = value.Trim().ToLowerInvariant();
        if (value == "transparent" || value == "none") return null;
        var named = new Dictionary<string, string> { { "black", "000000" }, { "white", "FFFFFF" }, { "red", "FF0000" }, { "green", "008000" },
            { "blue", "0000FF" }, { "gray", "808080" }, { "grey", "808080" }, { "yellow", "FFFF00" }, { "navy", "000080" }, { "silver", "C0C0C0" } };
        if (named.ContainsKey(value)) return named[value];
        if (Regex.IsMatch(value, @"^#[0-9a-f]{3}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
            return String.Concat(value.Skip(1).Select(c => new string(c, 2))).ToUpperInvariant();
        if (Regex.IsMatch(value, @"^#[0-9a-f]{6}$", RegexOptions.None, TimeSpan.FromSeconds(1))) return value.Substring(1).ToUpperInvariant();
        Match rgb = Regex.Match(value, @"^rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)$", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (rgb.Success)
        {
            int[] values = rgb.Groups.Cast<Group>().Skip(1).Select(g => (int)Number(g.Value, location)).ToArray();
            if (values.All(v => v >= 0 && v <= 255)) return String.Concat(values.Select(v => v.ToString("X2")));
        }
        throw new Problem("INVALID_COLOR", "Use #RGB, #RRGGBB, rgb(r,g,b), a documented named colour, or transparent: " + value, 422, location);
    }

    private void Attributes(Node node, params string[] additional)
    {
        foreach (string name in node.Attr.Keys)
            if (name != "id" && name != "class" && name != "style" && name != "data-name" && name != "width" && name != "height" && !additional.Contains(name))
                Unsupported("UNSUPPORTED_ATTRIBUTE", "Unsupported attribute: " + name, node.Location);
    }

    private void RejectProperties(Dictionary<string, string> style, Node node, params string[] properties)
    {
        foreach (string key in properties)
            if (style.ContainsKey(key))
            { Unsupported("UNSUPPORTED_STYLE_CONTEXT", key + " is not supported on " + node.Tag + " in this context.", node.Location); style.Remove(key); }
    }

    private void CollectSlides(Node node, Dictionary<string, string> parent, List<Slide> slides)
    {
        CheckBudget();
        if (node.Tag == "#text")
        { if (!String.IsNullOrWhiteSpace(node.Text)) Unsupported("CONTENT_OUTSIDE_SLIDE", "Visible text must be inside a slide section.", "document"); return; }
        if (node.Tag == "head")
        {
            foreach (Node child in node.Children)
                if (child.Tag != "#text" && child.Tag != "style" && child.Tag != "meta" && child.Tag != "title")
                    Unsupported("UNSUPPORTED_HTML", "Unsupported head element: " + child.Tag, child.Location);
            return;
        }
        if (node.Tag == "style" || node.Tag == "meta" || node.Tag == "title") return;
        Attributes(node);
        var style = Style(node, parent);
        if (Get(style, "display", "block") == "none") return;
        if (node.Tag == "section" && HasClass(node, "slide"))
        {
            if (slides.Count >= 100) throw new Problem("LIMIT_EXCEEDED", "At most 100 slides are supported.", 413);
            _slideNumber = slides.Count + 1;
            double width = Length(Get(style, "width", "1280px"), node.Location);
            double height = Length(Get(style, "height", "720px"), node.Location);
            if (width < 96 || height < 96 || width > 5376 || height > 5376)
                throw new Problem("INVALID_SLIDE_SIZE", "Slide dimensions must be between 96 and 5,376 pixels (1–56 inches at 96 DPI).", 422, node.Location);
            var slide = new Slide { Index = _slideNumber, Name = node.Get("data-name") ?? node.Get("id") ?? "Slide " + _slideNumber, Width = width, Height = height };
            string background = Color(Get(style, "background-color", Get(style, "background", "#ffffff")), node.Location);
            var tree = ShapeTree();
            slide.Xml = new XElement(P + "sld", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XAttribute(XNamespace.Xmlns + "p", P),
                new XElement(P + "cSld", new XAttribute("name", slide.Name),
                    new XElement(P + "bg", new XElement(P + "bgPr", Fill(background), new XElement(A + "effectLst"))), tree),
                new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")));
            slide.Rels.Add(Relationship("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"));
            foreach (Node child in node.Children) Place(child, style, slide, tree, 0, 0);
            slides.Add(slide); return;
        }
        if (node.Tag != "root" && node.Tag != "html" && node.Tag != "body")
        { Unsupported("CONTENT_OUTSIDE_SLIDE", "Only html/body wrappers and slide sections are allowed outside slides.", node.Location); return; }
        foreach (Node child in node.Children) CollectSlides(child, style, slides);
    }

    private void Place(Node node, Dictionary<string, string> parent, Slide slide, XElement tree, double offsetX, double offsetY)
    {
        CheckBudget();
        if (node.Tag == "#text")
        { if (!String.IsNullOrWhiteSpace(node.Text)) Unsupported("UNPOSITIONED_TEXT", "Place text in an explicitly sized element.", "slide"); return; }
        if (node.Tag == "style") return;
        // SVG attributes belong to the SVG image; ValidateSvg checks that document.
        if (node.Tag != "svg") Attributes(node, node.Tag == "img" ? "src" : "", node.Tag == "img" ? "alt" : "", "data-fallback-src");
        var style = Style(node, parent);
        if (Get(style, "display", "block") == "none") return;
        if (node.Tag == "section") throw new Problem("NESTED_SLIDE", "Slide sections cannot be nested.", 422, node.Location);
        if (!new[] { "div", "p", "h1", "h2", "h3", "h4", "h5", "h6", "span", "table", "img", "svg" }.Contains(node.Tag))
        { Unsupported("UNSUPPORTED_HTML", "Unsupported positioned element: " + node.Tag, node.Location); return; }
        double x = offsetX + Length(Required(style, "left", node), node.Location);
        double y = offsetY + Length(Required(style, "top", node), node.Location);
        double width = Length(Required(style, "width", node), node.Location);
        double height = Length(Required(style, "height", node), node.Location);
        if (width <= 0 || height <= 0) throw new Problem("INVALID_BOUNDS", "Element width and height must be positive.", 422, node.Location);
        if (x < 0 || y < 0 || x + width > slide.Width + 0.01 || y + height > slide.Height + 0.01)
            throw new Problem("OUT_OF_BOUNDS", "Element extends outside its slide canvas.", 422, node.Location);
        if (++_objects > 10000) throw new Problem("LIMIT_EXCEEDED", "At most 10,000 positioned objects are supported per request.", 413);
        int id = slide.NextId++;
        string name = node.Get("data-name") ?? node.Get("id") ?? node.Tag + " " + id;
        if (node.Tag == "table")
        { tree.Add(TableShape(node, style, x, y, width, height, id, name)); slide.TableCount++; return; }
        if (node.Tag == "svg" || node.Tag == "img")
        {
            RejectProperties(style, node, "border", "border-left", "border-right", "border-top", "border-bottom", "padding", "padding-left", "padding-right", "padding-top", "padding-bottom", "background-color");
            tree.Add(Picture(node, style, slide, x, y, width, height, id, name)); slide.ImageCount++; return;
        }
        RejectProperties(style, node, "border-left", "border-right", "border-top", "border-bottom", "table-layout", "border-collapse");
        bool container = node.Children.Any(c => c.Tag == "table" || c.Tag == "img" || c.Tag == "svg" ||
            Declarations(c.Get("style") ?? "", c.Location).ContainsKey("left") || _rules.Any(r => Matches(c, r.Selector) && r.Values.ContainsKey("left")));
        if (container)
        {
            tree.Add(TextShape(node, style, x, y, width, height, id, name, false));
            foreach (Node child in node.Children) Place(child, style, slide, tree, x, y);
        }
        else { tree.Add(TextShape(node, style, x, y, width, height, id, name, true)); slide.TextCount++; }
    }

    private static string Required(Dictionary<string, string> style, string key, Node node)
    {
        if (!style.ContainsKey(key)) throw new Problem("MISSING_GEOMETRY", "Positioned elements need left, top, width and height. Missing " + key, 422, node.Location);
        return style[key];
    }

    private static XElement Fill(string color)
    { return color == null ? new XElement(A + "noFill") : new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color))); }
    private static XElement Transform(double x, double y, double width, double height)
    { return new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", Emu(x)), new XAttribute("y", Emu(y))), new XElement(A + "ext", new XAttribute("cx", Emu(width)), new XAttribute("cy", Emu(height)))); }
    private static XElement ShapeTree()
    {
        return new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", new XElement(A + "xfrm",
                new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
                new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))));
    }

    private XElement TextShape(Node node, Dictionary<string, string> style, double x, double y, double width, double height, int id, string name, bool content)
    {
        return new XElement(P + "sp",
            new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)), new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
            new XElement(P + "spPr", Transform(x, y, width, height), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")),
                Fill(Color(Get(style, "background-color", Get(style, "background", "transparent")), node.Location)), Border(Get(style, "border", "none"), "ln", node.Location)),
            TextBody(node, style, P + "txBody", content));
    }

    private double[] Padding(Dictionary<string, string> style, string location)
    {
        string[] parts = Get(style, "padding", "0").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 1 || parts.Length > 4) throw new Problem("INVALID_CSS", "padding needs 1–4 lengths.", 422, location);
        double[] p = parts.Select(v => Length(v, location)).ToArray();
        double[] sides = { p[0], p.Length > 1 ? p[1] : p[0], p.Length > 2 ? p[2] : p[0], p.Length > 3 ? p[3] : p.Length > 1 ? p[1] : p[0] };
        string[] names = { "top", "right", "bottom", "left" };
        for (int i = 0; i < 4; i++) if (style.ContainsKey("padding-" + names[i])) sides[i] = Length(style["padding-" + names[i]], location);
        if (sides.Any(v => v < 0)) throw new Problem("INVALID_CSS", "Padding cannot be negative.", 422, location);
        return sides;
    }

    private XElement TextBody(Node node, Dictionary<string, string> style, XName bodyName, bool content)
    {
        double[] pad = Padding(style, node.Location);
        var body = new XElement(bodyName, new XElement(A + "bodyPr", new XAttribute("wrap", "square"),
            new XAttribute("lIns", Emu(pad[3])), new XAttribute("rIns", Emu(pad[1])), new XAttribute("tIns", Emu(pad[0])), new XAttribute("bIns", Emu(pad[2])),
            new XAttribute("anchor", Get(style, "vertical-align", "top") == "middle" ? "ctr" : Get(style, "vertical-align", "top") == "bottom" ? "b" : "t"),
            new XElement(A + "noAutofit")), new XElement(A + "lstStyle"));
        var paragraphs = new List<XElement>();
        XElement paragraph = Paragraph(style, node.Location);
        bool whitespace = false;
        if (content) Inline(node, style, paragraph, paragraphs, ref whitespace, true);
        if (paragraph.Elements(A + "r").Any() || paragraph.Elements(A + "br").Any() || paragraphs.Count == 0) paragraphs.Add(paragraph);
        foreach (XElement p in paragraphs)
        { p.Add(new XElement(A + "endParaRPr", new XAttribute("lang", "en-US"))); body.Add(p); }
        return body;
    }

    private XElement Paragraph(Dictionary<string, string> style, string location)
    {
        string align = Get(style, "text-align", "left");
        var props = new XElement(A + "pPr", new XAttribute("algn", align == "center" ? "ctr" : align == "right" ? "r" : align == "justify" ? "just" : "l"));
        if (style.ContainsKey("line-height"))
        {
            string v = style["line-height"];
            Match unitless = Regex.Match(v, @"^\d+(?:\.\d+)?$", RegexOptions.None, TimeSpan.FromSeconds(1));
            double amount = v.EndsWith("%") ? Number(v.Substring(0, v.Length - 1), location) / 100 : unitless.Success ? Number(v, location) : -1;
            if (amount == -1)
            {
                double pixels = Length(v, location); if (pixels <= 0) throw new Problem("INVALID_CSS", "line-height must be positive.", 422, location);
                props.Add(new XElement(A + "lnSpc", new XElement(A + "spcPts", new XAttribute("val", (int)Math.Round(pixels * 75)))));
            }
            else
            {
                if (amount <= 0 || amount > 20) throw new Problem("INVALID_CSS", "line-height multiplier must be in (0,20].", 422, location);
                props.Add(new XElement(A + "lnSpc", new XElement(A + "spcPct", new XAttribute("val", (int)Math.Round(amount * 100000)))));
            }
        }
        props.Add(new XElement(A + "buNone"));
        return new XElement(A + "p", props);
    }

    private void Inline(Node node, Dictionary<string, string> style, XElement paragraph, List<XElement> paragraphs, ref bool whitespace, bool root)
    {
        CheckBudget();
        if (node.Tag == "#text")
        {
            string text = node.Text;
            if (Get(style, "white-space", "normal") == "normal")
            {
                text = Regex.Replace(text, @"[\t\r\n\f ]+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
                if (whitespace || !paragraph.Elements(A + "r").Any()) text = text.TrimStart(' ');
                whitespace = text.EndsWith(" ", StringComparison.Ordinal);
            }
            else text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Length == 0) return;
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) paragraph.Add(new XElement(A + "br"));
                if (lines[i].Length > 0) paragraph.Add(new XElement(A + "r", RunProperties(style, node.Location), new XElement(A + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), lines[i])));
            }
            return;
        }
        if (!root)
        {
            Attributes(node);
            if (!new[] { "span", "b", "strong", "i", "em", "u", "br", "p", "div", "h1", "h2", "h3", "h4", "h5", "h6" }.Contains(node.Tag))
            { Unsupported("UNSUPPORTED_HTML", "Unsupported inline element: " + node.Tag, node.Location); return; }
            style = Style(node, style);
            if (Get(style, "display", "block") == "none") return;
            RejectProperties(style, node, "left", "top", "width", "height", "position", "background-color", "border", "border-left", "border-right", "border-top", "border-bottom", "padding", "padding-left", "padding-right", "padding-top", "padding-bottom", "vertical-align", "table-layout", "border-collapse");
        }
        if (node.Tag == "br") { paragraph.Add(new XElement(A + "br")); whitespace = false; return; }
        foreach (Node child in node.Children)
        {
            bool block = child.Tag == "p" || child.Tag == "div" || Regex.IsMatch(child.Tag, "^h[1-6]$");
            if (block)
            {
                if (paragraph.Elements(A + "r").Any() || paragraph.Elements(A + "br").Any())
                { paragraphs.Add(new XElement(paragraph)); paragraph.RemoveNodes(); paragraph.Add(Paragraph(style, node.Location).Nodes()); }
                var childStyle = Style(child, style);
                var blockParagraph = Paragraph(childStyle, child.Location); bool childWhitespace = false;
                Inline(child, style, blockParagraph, paragraphs, ref childWhitespace, false);
                paragraphs.Add(blockParagraph); whitespace = false;
            }
            else Inline(child, style, paragraph, paragraphs, ref whitespace, false);
        }
    }

    private static XElement RunProperties(Dictionary<string, string> style, string location)
    {
        double px = Length(Get(style, "font-size", "18px"), location);
        if (px < 1.34 || px > 533.33) throw new Problem("INVALID_FONT_SIZE", "Font size must be between 1 and 400 points.", 422, location);
        string font = Get(style, "font-family", "Arial").Split(',')[0].Trim().Trim('\'', '"');
        if (font.Length == 0 || font.Length > 128) throw new Problem("INVALID_FONT", "Invalid font-family.", 422, location);
        string color = Color(Get(style, "color", "#000000"), location);
        if (color == null) throw new Problem("INVALID_COLOR", "Text colour cannot be transparent.", 422, location);
        return new XElement(A + "rPr", new XAttribute("lang", "en-US"), new XAttribute("sz", (int)Math.Round(px * 75)),
            new XAttribute("b", new[] { "bold", "700" }.Contains(Get(style, "font-weight", "normal")) ? 1 : 0),
            new XAttribute("i", Get(style, "font-style", "normal") == "italic" ? 1 : 0),
            new XAttribute("u", Get(style, "text-decoration", "none") == "underline" ? "sng" : "none"),
            Fill(color), new XElement(A + "latin", new XAttribute("typeface", font)), new XElement(A + "ea", new XAttribute("typeface", font)), new XElement(A + "cs", new XAttribute("typeface", font)));
    }

    private static XElement Border(string value, string name, string location)
    {
        if (value == "none" || value == "0") return new XElement(A + name, new XElement(A + "noFill"));
        string[] parts = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) throw new Problem("INVALID_BORDER", "Borders use '<width> solid|dashed|dotted <colour>'.", 422, location);
        double width = Length(parts[0], location);
        string dash = parts[1];
        if (width < 0 || width > 100 || !new[] { "solid", "dashed", "dotted" }.Contains(dash))
            throw new Problem("INVALID_BORDER", "Invalid border width or style.", 422, location);
        return new XElement(A + name, new XAttribute("w", Emu(width)), Fill(Color(String.Join(" ", parts.Skip(2)), location)),
            new XElement(A + "prstDash", new XAttribute("val", dash == "dashed" ? "dash" : dash == "dotted" ? "dot" : "solid")));
    }

    private sealed class Cell
    {
        public Node Node; public Dictionary<string, string> Style;
        public int Row; public int Col; public int RowSpan; public int ColSpan;
    }

    private static readonly string[] CellDefaults = { "background-color", "border", "border-left", "border-right", "border-top", "border-bottom", "padding", "padding-left", "padding-right", "padding-top", "padding-bottom", "vertical-align" };

    private static void InheritCellDefaults(Dictionary<string, string> target, Dictionary<string, string> source)
    {
        foreach (string key in CellDefaults) if (!target.ContainsKey(key) && source.ContainsKey(key)) target[key] = source[key];
    }

    private XElement TableShape(Node table, Dictionary<string, string> style, double x, double y, double width, double height, int id, string name)
    {
        var rows = new List<Node>();
        var rowStyles = new List<Dictionary<string, string>>();
        var columns = new List<Node>();
        foreach (Node child in table.Children)
        {
            if (child.Tag == "#text") { if (!String.IsNullOrWhiteSpace(child.Text)) throw new Problem("INVALID_TABLE", "Text must be inside table cells.", 422, table.Location); continue; }
            if (child.Tag == "colgroup")
            {
                Attributes(child);
                var colGroupStyle = Declarations(child.Get("style") ?? "", child.Location);
                foreach (string key in colGroupStyle.Keys) Unsupported("UNSUPPORTED_STYLE_CONTEXT", "Put column width declarations on individual col elements rather than colgroup.", child.Location);
                foreach (Node col in child.Children) { if (col.Tag == "#text" && String.IsNullOrWhiteSpace(col.Text)) continue; if (col.Tag != "col") throw new Problem("INVALID_TABLE", "colgroup only accepts col elements.", 422); columns.Add(col); }
                continue;
            }
            if (child.Tag == "tr") { rows.Add(child); rowStyles.Add(Style(child, style)); }
            else if (child.Tag == "thead" || child.Tag == "tbody" || child.Tag == "tfoot")
            {
                Attributes(child); var groupStyle = Style(child, style);
                RejectProperties(groupStyle, child, "left", "top", "width", "height", "position", "table-layout", "border-collapse");
                if (Get(groupStyle, "display", "block") == "none") throw new Problem("UNSUPPORTED_TABLE_LAYOUT", "Remove hidden table groups from the supplied HTML.", 422, child.Location);
                foreach (Node row in child.Children)
                {
                    if (row.Tag == "#text" && String.IsNullOrWhiteSpace(row.Text)) continue;
                    if (row.Tag != "tr") throw new Problem("INVALID_TABLE", "Table groups only accept tr elements.", 422);
                    var rowStyle = Style(row, groupStyle); InheritCellDefaults(rowStyle, groupStyle);
                    rows.Add(row); rowStyles.Add(rowStyle);
                }
            }
            else throw new Problem("INVALID_TABLE", "Unsupported table child: " + child.Tag, 422, table.Location);
        }
        if (rows.Count == 0 || rows.Count > 256) throw new Problem("INVALID_TABLE", "A table needs 1–256 rows.", 422, table.Location);
        var grid = new Cell[rows.Count, 64];
        int columnCount = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            CheckBudget(); Attributes(rows[r]); int c = 0;
            RejectProperties(rowStyles[r], rows[r], "left", "top", "width", "position", "table-layout", "border-collapse");
            if (Get(rowStyles[r], "display", "block") == "none")
                throw new Problem("UNSUPPORTED_TABLE_LAYOUT", "Remove hidden rows from the supplied HTML rather than using display:none on table rows.", 422, rows[r].Location);
            foreach (Node node in rows[r].Children)
            {
                if (node.Tag == "#text" && String.IsNullOrWhiteSpace(node.Text)) continue;
                if (node.Tag != "td" && node.Tag != "th") throw new Problem("INVALID_TABLE", "tr only accepts td/th elements.", 422, rows[r].Location);
                Attributes(node, "colspan", "rowspan");
                while (c < 64 && grid[r, c] != null) c++;
                int cs = Span(node.Get("colspan")), rs = Span(node.Get("rowspan"));
                if (c + cs > 64 || r + rs > rows.Count) throw new Problem("INVALID_TABLE", "Cell span exceeds the table grid (max 64 columns).", 422, node.Location);
                var cellStyle = Style(node, rowStyles[r]);
                RejectProperties(cellStyle, node, "left", "top", "width", "height", "position", "table-layout", "border-collapse");
                if (Get(cellStyle, "display", "block") == "none")
                    throw new Problem("UNSUPPORTED_TABLE_LAYOUT", "Remove hidden cells and update the grid rather than using display:none on cells.", 422, node.Location);
                // Cell fills and borders inherit table/row defaults explicitly.
                InheritCellDefaults(cellStyle, rowStyles[r]); InheritCellDefaults(cellStyle, style);
                var cell = new Cell { Node = node, Style = cellStyle, Row = r, Col = c, RowSpan = rs, ColSpan = cs };
                for (int rr = r; rr < r + rs; rr++) for (int cc = c; cc < c + cs; cc++)
                { if (grid[rr, cc] != null) throw new Problem("INVALID_TABLE", "Overlapping merged cells.", 422, node.Location); grid[rr, cc] = cell; }
                c += cs; columnCount = Math.Max(columnCount, c);
            }
        }
        if (columnCount == 0) throw new Problem("INVALID_TABLE", "Table has no cells.", 422, table.Location);
        for (int r = 0; r < rows.Count; r++) for (int c = 0; c < columnCount; c++)
            if (grid[r, c] == null) throw new Problem("INVALID_TABLE", "Ragged table grid. Supply all cells accounting for colspan/rowspan.", 422, table.Location);
        _cells += rows.Count * columnCount;
        if (_cells > 20000) throw new Problem("LIMIT_EXCEEDED", "Table grids exceed 20,000 cells per request.", 413);
        double[] widths = new double[columnCount];
        if (columns.Count > 0)
        {
            if (columns.Count != columnCount) throw new Problem("INVALID_TABLE", "colgroup must define one col for each grid column.", 422, table.Location);
            for (int i = 0; i < widths.Length; i++)
            {
                Attributes(columns[i]); var colStyle = Style(columns[i], style);
                RejectProperties(colStyle, columns[i], "left", "top", "height", "position", "background-color", "border", "padding", "display", "table-layout", "border-collapse");
                widths[i] = Length(Required(colStyle, "width", columns[i]), columns[i].Location, true, width);
            }
            if (widths.Any(v => v <= 0) || Math.Abs(widths.Sum() - width) > 0.05)
                throw new Problem("INVALID_TABLE", "Column widths must be positive and sum to the table width (or 100%).", 422, table.Location);
        }
        else for (int i = 0; i < widths.Length; i++) widths[i] = width / columnCount;
        double[] heights = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++) heights[i] = rowStyles[i].ContainsKey("height") ? Length(rowStyles[i]["height"], rows[i].Location) : 0;
        int automatic = heights.Count(v => v == 0);
        double remaining = height - heights.Sum();
        if ((automatic > 0 && remaining <= 0) || (automatic == 0 && Math.Abs(remaining) > 0.05) || heights.Any(v => v < 0))
            throw new Problem("INVALID_TABLE", "Explicit row heights must fit the table height; unspecified rows share the remaining height.", 422, table.Location);
        for (int i = 0; i < heights.Length; i++) if (heights[i] == 0) heights[i] = remaining / automatic;
        var nativeTable = new XElement(A + "tbl", new XElement(A + "tblPr"), new XElement(A + "tblGrid", widths.Select(w => new XElement(A + "gridCol", new XAttribute("w", Emu(w))))));
        for (int r = 0; r < rows.Count; r++)
        {
            var row = new XElement(A + "tr", new XAttribute("h", Emu(heights[r])));
            for (int c = 0; c < columnCount; c++)
            {
                Cell cell = grid[r, c]; bool origin = r == cell.Row && c == cell.Col;
                var tc = new XElement(A + "tc");
                if (c == cell.Col && cell.ColSpan > 1) tc.SetAttributeValue("gridSpan", cell.ColSpan);
                if (r == cell.Row && cell.RowSpan > 1) tc.SetAttributeValue("rowSpan", cell.RowSpan);
                if (c > cell.Col) tc.SetAttributeValue("hMerge", 1);
                if (r > cell.Row) tc.SetAttributeValue("vMerge", 1);
                tc.Add(TextBody(cell.Node, cell.Style, A + "txBody", origin));
                double[] pad = Padding(cell.Style, cell.Node.Location);
                var props = new XElement(A + "tcPr", new XAttribute("marL", Emu(pad[3])), new XAttribute("marR", Emu(pad[1])), new XAttribute("marT", Emu(pad[0])), new XAttribute("marB", Emu(pad[2])),
                    new XAttribute("anchor", Get(cell.Style, "vertical-align", "top") == "middle" ? "ctr" : Get(cell.Style, "vertical-align", "top") == "bottom" ? "b" : "t"));
                foreach (var pair in new[] { new[] { "left", "lnL" }, new[] { "right", "lnR" }, new[] { "top", "lnT" }, new[] { "bottom", "lnB" } })
                    props.Add(Border(Get(cell.Style, "border-" + pair[0], Get(cell.Style, "border", "none")), pair[1], cell.Node.Location));
                props.Add(Fill(Color(Get(cell.Style, "background-color", Get(cell.Style, "background", "transparent")), cell.Node.Location)));
                tc.Add(props); row.Add(tc);
            }
            nativeTable.Add(row);
        }
        return new XElement(P + "graphicFrame",
            new XElement(P + "nvGraphicFramePr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)), new XElement(P + "cNvGraphicFramePr"), new XElement(P + "nvPr")),
            new XElement(P + "xfrm", new XElement(A + "off", new XAttribute("x", Emu(x)), new XAttribute("y", Emu(y))), new XElement(A + "ext", new XAttribute("cx", Emu(width)), new XAttribute("cy", Emu(height)))),
            new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/table"), nativeTable)));
    }

    private static int Span(string value)
    {
        if (value == null) return 1;
        int result; if (!Int32.TryParse(value, out result) || result < 1 || result > 256) throw new Problem("INVALID_TABLE", "colspan/rowspan must be integers in 1–256.", 422);
        return result;
    }

    private Asset AddAsset(byte[] bytes, string ext)
    {
        string key;
        using (SHA256 hash = SHA256.Create()) key = ext + Convert.ToBase64String(hash.ComputeHash(bytes));
        Asset existing; if (_assets.TryGetValue(key, out existing)) return existing;
        if (bytes.Length > MaxMediaBytes - _mediaBytes) throw new Problem("LIMIT_EXCEEDED", "Embedded media exceeds 20 MiB.", 413);
        _mediaBytes += bytes.Length;
        var asset = new Asset { Name = "asset" + (_assets.Count + 1) + "." + ext, Bytes = bytes, Ext = ext };
        _assets[key] = asset; return asset;
    }

    private Asset DataImage(string source, string location)
    {
        if (source == null) throw new Problem("INVALID_IMAGE", "Images need an embedded data URL.", 422, location);
        Match svgUrl = Regex.Match(source, @"^data:image/svg\+xml(?:;utf8|;charset=utf-8)?,([\s\S]+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (svgUrl.Success)
            return AddAsset(ValidateSvg(DecodeSvgUrl(svgUrl.Groups[1].Value, location), location), "svg");
        Match match = Regex.Match(source, @"^data:image/(png|jpeg|svg\+xml);base64,([\s\S]+)$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (!match.Success) throw new Problem("EXTERNAL_ASSET", "Only embedded PNG/JPEG base64 and SVG base64 or UTF-8 URL-encoded data URLs are supported. External URLs are not fetched.", 422, location);
        byte[] bytes;
        if (match.Groups[2].Length > MaxMediaBytes * 2) throw new Problem("LIMIT_EXCEEDED", "Image data URL is too large.", 413, location);
        try { bytes = Convert.FromBase64String(match.Groups[2].Value); }
        catch (FormatException) { throw new Problem("INVALID_IMAGE", "Image data is not valid base64.", 422, location); }
        string type = match.Groups[1].Value.ToLowerInvariant();
        if (type == "svg+xml") return AddAsset(ValidateSvg(Encoding.UTF8.GetString(bytes), location), "svg");
        if (type == "png")
        {
            if (bytes.Length < 33 || !bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                Encoding.ASCII.GetString(bytes, 12, 4) != "IHDR") throw new Problem("INVALID_IMAGE", "Invalid PNG header.", 422, location);
        }
        else if (bytes.Length < 4 || bytes[0] != 255 || bytes[1] != 216 || bytes[bytes.Length - 2] != 255 || bytes[bytes.Length - 1] != 217)
            throw new Problem("INVALID_IMAGE", "Invalid JPEG header/trailer.", 422, location);
        return AddAsset(bytes, type == "jpeg" ? "jpg" : "png");
    }

    private string DecodeSvgUrl(string encoded, string location)
    {
        if (encoded.Length > MaxMediaBytes * 3) throw new Problem("LIMIT_EXCEEDED", "SVG data URL is too large.", 413, location);
        // Decode the data payload once as UTF-8 URI bytes, not form data:
        // a literal '+' must stay '+', and %25 must not trigger a second decode.
        var utf8 = new UTF8Encoding(false, true);
        try
        {
            byte[] input = utf8.GetBytes(encoded);
            byte[] decoded = new byte[input.Length];
            int count = 0;
            for (int i = 0; i < input.Length; i++)
            {
                if ((i & 4095) == 0) CheckBudget();
                if (input[i] != (byte)'%') { decoded[count++] = input[i]; continue; }
                int high = i + 1 < input.Length ? HexDigit(input[i + 1]) : -1;
                int low = i + 2 < input.Length ? HexDigit(input[i + 2]) : -1;
                if (high < 0 || low < 0)
                    throw new Problem("INVALID_IMAGE", "SVG data URL contains an invalid percent escape.", 422, location);
                decoded[count++] = (byte)((high << 4) | low); i += 2;
            }
            return utf8.GetString(decoded, 0, count);
        }
        catch (EncoderFallbackException) { throw new Problem("INVALID_IMAGE", "SVG data URL must contain valid UTF-8 text.", 422, location); }
        catch (DecoderFallbackException) { throw new Problem("INVALID_IMAGE", "SVG data URL must contain valid UTF-8 text.", 422, location); }
    }

    private static int HexDigit(byte value)
    {
        if (value >= '0' && value <= '9') return value - '0';
        if (value >= 'A' && value <= 'F') return value - 'A' + 10;
        if (value >= 'a' && value <= 'f') return value - 'a' + 10;
        return -1;
    }

    private byte[] ValidateSvg(string svg, string location)
    {
        XElement root;
        try
        {
            using (var sr = new StringReader(svg))
            using (var reader = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxHtmlBytes }))
                root = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { throw new Problem("INVALID_SVG", "SVG must be well-formed XML without a DTD.", 422, location); }
        XNamespace svgNs = "http://www.w3.org/2000/svg";
        if (root.Name.LocalName != "svg" || (root.Name.Namespace != svgNs && root.Name.NamespaceName.Length != 0))
            throw new Problem("INVALID_SVG", "Expected an SVG root element.", 422, location);
        if (root.Name.NamespaceName.Length == 0)
            foreach (XElement element in root.DescendantsAndSelf()) if (element.Name.NamespaceName.Length == 0) element.Name = svgNs + element.Name.LocalName;
        int count = 0;
        foreach (XElement element in root.DescendantsAndSelf())
        {
            CheckBudget();
            if (++count > 20000 || element.Ancestors().Count() > 64) throw new Problem("LIMIT_EXCEEDED", "SVG exceeds 20,000 elements or 64 levels.", 413, location);
            if (new[] { "script", "foreignobject", "animate", "animatetransform", "animatemotion", "set" }.Contains(element.Name.LocalName.ToLowerInvariant()))
                throw new Problem("ACTIVE_CONTENT", "SVG must be static and cannot contain scripts, animation or foreignObject.", 422, location);
            foreach (XAttribute attr in element.Attributes().ToArray())
            {
                string n = attr.Name.LocalName.ToLowerInvariant(); string v = attr.Value.Trim();
                if (n.StartsWith("on")) throw new Problem("ACTIVE_CONTENT", "SVG event handlers are not allowed.", 422, location);
                if (n == "href" && !v.StartsWith("#", StringComparison.Ordinal))
                    throw new Problem("EXTERNAL_ASSET", "SVG href references must be local #fragment references.", 422, location);
                if (n == "style" && (v.Contains("\\") || v.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0 || v.IndexOf("expression(", StringComparison.OrdinalIgnoreCase) >= 0))
                    throw new Problem("UNSUPPORTED_SVG", "SVG inline CSS must not use escapes, imports or expressions.", 422, location);
                foreach (Match reference in Regex.Matches(v, @"url\(([^)]*)\)", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                    if (!reference.Groups[1].Value.Trim().Trim('\'', '"').StartsWith("#", StringComparison.Ordinal))
                        throw new Problem("EXTERNAL_ASSET", "SVG url() references must be local #fragment references.", 422, location);
            }
            if (element.Name.LocalName == "style") throw new Problem("UNSUPPORTED_SVG", "SVG must use presentation attributes/inline styles rather than style blocks.", 422, location);
        }
        // Layout attributes are consumed by the compiler, not passed to the image renderer.
        root.Attribute("data-fallback-src")?.Remove(); root.Attribute("data-name")?.Remove();
        return Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
    }

    private XElement Picture(Node node, Dictionary<string, string> style, Slide slide, double x, double y, double width, double height, int id, string name)
    {
        Asset asset = node.Tag == "svg" ? AddAsset(ValidateSvg(node.Svg, node.Location), "svg") : DataImage(node.Get("src"), node.Location);
        string relationship = "rId" + slide.NextRel++;
        slide.Rels.Add(Relationship(relationship, "image", "../media/" + asset.Name));
        var blip = new XElement(A + "blip", new XAttribute(R + "embed", relationship));
        if (asset.Ext == "svg")
        {
            Asset fallback;
            if (node.Get("data-fallback-src") != null)
            {
                fallback = DataImage(node.Get("data-fallback-src"), node.Location);
                if (fallback.Ext == "svg") throw new Problem("INVALID_IMAGE", "SVG fallback must be PNG or JPEG.", 422, node.Location);
            }
            else
            {
                // Office uses asvg:svgBlip; a transparent raster fulfils the required
                // base blip relationship. Older clients need a caller-supplied preview.
                fallback = AddAsset(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg=="), "png");
                Warn("SVG_FALLBACK_MISSING", "SVG requires a compatible Office client. Supply data-fallback-src with a PNG/JPEG preview for clients that do not render SVG.", node.Location);
            }
            string rasterRelationship = "rId" + slide.NextRel++;
            slide.Rels.Add(Relationship(rasterRelationship, "image", "../media/" + fallback.Name));
            blip.SetAttributeValue(R + "embed", rasterRelationship);
            XNamespace svg = "http://schemas.microsoft.com/office/drawing/2016/SVG/main";
            blip.Add(new XElement(A + "extLst", new XElement(A + "ext", new XAttribute("uri", "{96DAC541-7B7A-43D3-8B79-37D633B846F1}"),
                new XElement(svg + "svgBlip", new XAttribute(XNamespace.Xmlns + "asvg", svg), new XAttribute(R + "embed", relationship)))));
        }
        return new XElement(P + "pic", new XElement(P + "nvPicPr",
            new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name), new XAttribute("descr", node.Get("alt") ?? name)),
            new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))), new XElement(P + "nvPr")),
            new XElement(P + "blipFill", blip, new XElement(A + "stretch", new XElement(A + "fillRect"))),
            new XElement(P + "spPr", Transform(x, y, width, height), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
    }

    private static XElement Relationship(string id, string type, string target)
    { return new XElement(Rel + "Relationship", new XAttribute("Id", id), new XAttribute("Type", OfficeRel + type), new XAttribute("Target", target)); }
    private static XElement Relationships(params XElement[] relationships) { return new XElement(Rel + "Relationships", relationships); }
    private static XElement ColorMap()
    {
        return new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
            Enumerable.Range(1, 6).Select(i => new XAttribute("accent" + i, "accent" + i)), new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink"));
    }

    private byte[] WritePackage(List<Slide> slides, double width, double height)
    {
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                var types = new XElement(Ct + "Types", new XElement(Ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(Ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
                foreach (string ext in _assets.Values.Select(a => a.Ext).Distinct())
                    types.Add(new XElement(Ct + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", ext == "svg" ? "image/svg+xml" : ext == "jpg" ? "image/jpeg" : "image/png")));
                AddType(types, "/ppt/presentation.xml", "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
                AddType(types, "/ppt/slideMasters/slideMaster1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml");
                AddType(types, "/ppt/slideLayouts/slideLayout1.xml", "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml");
                AddType(types, "/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml");
                var presentation = new XElement(P + "presentation", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R),
                    new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", 2147483648L), new XAttribute(R + "id", "rId1"))),
                    new XElement(P + "sldIdLst", slides.Select(s => new XElement(P + "sldId", new XAttribute("id", 255 + s.Index), new XAttribute(R + "id", "rId" + (s.Index + 1))))),
                    new XElement(P + "sldSz", new XAttribute("cx", Emu(width)), new XAttribute("cy", Emu(height)), new XAttribute("type", "custom")),
                    new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000)), new XElement(P + "defaultTextStyle"));
                var presentationRels = Relationships(Relationship("rId1", "slideMaster", "slideMasters/slideMaster1.xml"));
                foreach (Slide slide in slides)
                {
                    CheckBudget();
                    AddType(types, "/ppt/slides/slide" + slide.Index + ".xml", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml");
                    presentationRels.Add(Relationship("rId" + (slide.Index + 1), "slide", "slides/slide" + slide.Index + ".xml"));
                    WriteXml(zip, "ppt/slides/slide" + slide.Index + ".xml", slide.Xml);
                    WriteXml(zip, "ppt/slides/_rels/slide" + slide.Index + ".xml.rels", Relationships(slide.Rels.ToArray()));
                }
                WriteXml(zip, "[Content_Types].xml", types);
                WriteXml(zip, "_rels/.rels", Relationships(Relationship("rId1", "officeDocument", "ppt/presentation.xml")));
                WriteXml(zip, "ppt/presentation.xml", presentation);
                WriteXml(zip, "ppt/_rels/presentation.xml.rels", presentationRels);
                WriteXml(zip, "ppt/slideMasters/slideMaster1.xml", new XElement(P + "sldMaster", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R),
                    new XElement(P + "cSld", ShapeTree()), ColorMap(), new XElement(P + "sldLayoutIdLst", new XElement(P + "sldLayoutId", new XAttribute("id", 2147483649L), new XAttribute(R + "id", "rId1"))),
                    new XElement(P + "txStyles", new XElement(P + "titleStyle"), new XElement(P + "bodyStyle"), new XElement(P + "otherStyle"))));
                WriteXml(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Relationships(Relationship("rId1", "slideLayout", "../slideLayouts/slideLayout1.xml"), Relationship("rId2", "theme", "../theme/theme1.xml")));
                WriteXml(zip, "ppt/slideLayouts/slideLayout1.xml", new XElement(P + "sldLayout", new XAttribute("type", "blank"), new XAttribute("preserve", 1), new XAttribute(XNamespace.Xmlns + "a", A),
                    new XElement(P + "cSld", new XAttribute("name", "Blank"), ShapeTree()), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
                WriteXml(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Relationships(Relationship("rId1", "slideMaster", "../slideMasters/slideMaster1.xml")));
                WriteXml(zip, "ppt/theme/theme1.xml", Theme());
                foreach (Asset asset in _assets.Values)
                {
                    CheckBudget();
                    using (Stream dest = zip.CreateEntry("ppt/media/" + asset.Name, CompressionLevel.Optimal).Open()) dest.Write(asset.Bytes, 0, asset.Bytes.Length);
                }
            }
            if (stream.Length > MaxOutputBytes) throw new Problem("LIMIT_EXCEEDED", "Generated presentation exceeds 50 MiB.", 413);
            CheckBudget(); return stream.ToArray();
        }
    }

    private static void AddType(XElement types, string path, string mime)
    { types.Add(new XElement(Ct + "Override", new XAttribute("PartName", path), new XAttribute("ContentType", mime))); }
    private static void WriteXml(ZipArchive zip, string path, XElement xml)
    {
        using (Stream stream = zip.CreateEntry(path, CompressionLevel.Optimal).Open())
        using (XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false })) xml.Save(writer);
    }
    private static XElement Theme()
    {
        string[] names = { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };
        string[] colors = { "000000", "FFFFFF", "1F497D", "EEECE1", "4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47", "0563C1", "954F72" };
        var scheme = new XElement(A + "clrScheme", new XAttribute("name", "Default"));
        for (int i = 0; i < names.Length; i++) scheme.Add(new XElement(A + names[i], new XElement(A + "srgbClr", new XAttribute("val", colors[i]))));
        Func<string, XElement> font = n => new XElement(A + n, new XElement(A + "latin", new XAttribute("typeface", "Arial")), new XElement(A + "ea", new XAttribute("typeface", "")), new XElement(A + "cs", new XAttribute("typeface", "")));
        Func<XElement> fill = () => new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")));
        return new XElement(A + "theme", new XAttribute("name", "HTML Slides"), new XElement(A + "themeElements", scheme,
            new XElement(A + "fontScheme", new XAttribute("name", "Arial"), font("majorFont"), font("minorFont")),
            new XElement(A + "fmtScheme", new XAttribute("name", "Default"),
                new XElement(A + "fillStyleLst", Enumerable.Range(0, 3).Select(i => fill())),
                new XElement(A + "lnStyleLst", Enumerable.Range(0, 3).Select(i => new XElement(A + "ln", new XAttribute("w", 12700), fill(), new XElement(A + "prstDash", new XAttribute("val", "solid"))))),
                new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(i => new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
                new XElement(A + "bgFillStyleLst", Enumerable.Range(0, 3).Select(i => fill())))), new XElement(A + "objectDefaults"), new XElement(A + "extraClrSchemeLst"));
    }
}
