using System;
using System.Collections.Generic;
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
    private const int MaxPages = 1000;
    private const int MaxPageCharacters = 6291456;
    private const int FrontMatterNonEmptyPages = 5;

    private static readonly Regex NumericHeadingRegex = new Regex(
        @"^(?<number>\d+(?:\.\d+)*)[\.)]?\s+(?<title>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly Regex DateIsoRegex = new Regex(
        @"\b(?<year>(?:19|20)\d{2})[-/.](?<month>0?[1-9]|1[0-2])[-/.](?<day>0?[1-9]|[12]\d|3[01])\b",
        RegexOptions.Compiled);

    private static readonly Regex DateDayFirstRegex = new Regex(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th|er|e)?\s+(?<month>[A-Za-zÀ-ÿ\.]+)\s*,?\s*(?<year>(?:19|20)\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DateMonthFirstRegex = new Regex(
        @"\b(?<month>[A-Za-zÀ-ÿ\.]+)\s+(?<day>0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?\s*,?\s*(?<year>(?:19|20)\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ControlNumberRegex = new Regex(
        @"(?<!\d)(?<value>\d{5,9})(?!\d)",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownUrlRegex = new Regex(
        @"\]\((?<url>https?://[^\s<>\)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BareUrlRegex = new Regex(
        @"(?<url>https?://[^\s<>\]\)]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);
        if (!string.Equals(operationId, "ParseProductMonograph", StringComparison.Ordinal))
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "UNKNOWN_OPERATION",
                "Unknown operation: " + operationId,
                null);
        }

        return await HandleParseProductMonographAsync().ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> HandleParseProductMonographAsync()
    {
        JObject request;
        try
        {
            string content = this.Context.Request.Content == null
                ? null
                : await this.Context.Request.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(content))
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_REQUEST", "Request body is required.", null);
            }

            request = JObject.Parse(content);
        }
        catch (JsonException ex)
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_JSON", "Request body is not valid JSON: " + ex.Message, null);
        }

        string sourceUrl = ReadOptionalString(request["sourceUrl"]);
        if (request["sourceUrl"] != null && request["sourceUrl"].Type != JTokenType.Null)
        {
            Uri sourceUri;
            if (string.IsNullOrWhiteSpace(sourceUrl) || sourceUrl.Length > 4096 ||
                !Uri.TryCreate(sourceUrl, UriKind.Absolute, out sourceUri) ||
                !(string.Equals(sourceUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_SOURCE_URL", "sourceUrl must be an absolute HTTP or HTTPS URL no longer than 4096 characters.", null);
            }
        }

        List<PageChunk> pages;
        JObject validationDetails;
        string validationMessage;
        if (!TryReadPages(request["pages"], out pages, out validationMessage, out validationDetails))
        {
            return CreateErrorResponse(HttpStatusCode.BadRequest, "INVALID_PAGES", validationMessage, validationDetails);
        }

        List<LineInfo> lines = BuildLines(pages);
        List<HeadingInfo> headings = BuildHeadings(lines, pages);
        int patientInformationStart = FindPatientInformationStart(lines);

        JObject language = DetectLanguage(pages);
        JObject controlNumber = ExtractControlNumber(pages, lines);
        JObject template = ClassifyTemplate(headings, patientInformationStart);
        JObject dates = ExtractDates(pages, lines);

        bool indicationsAmbiguous;
        HeadingInfo indicationsHeading = SelectPrimarySectionHeading(
            headings,
            "indications",
            "1",
            patientInformationStart,
            out indicationsAmbiguous);

        bool contraindicationsAmbiguous;
        HeadingInfo contraindicationsHeading = SelectPrimarySectionHeading(
            headings,
            "contraindications",
            "2",
            patientInformationStart,
            out contraindicationsAmbiguous);

        JObject indications = indicationsAmbiguous
            ? CreateAmbiguousSection(headings.Where(h => h.Type == "indications" && !h.IsTableOfContents).ToList())
            : BuildSection(indicationsHeading, headings, pages, lines);

        JObject contraindications = contraindicationsAmbiguous
            ? CreateAmbiguousSection(headings.Where(h => h.Type == "contraindications" && !h.IsTableOfContents).ToList())
            : BuildSection(contraindicationsHeading, headings, pages, lines);

        JObject pediatrics = BuildPopulationGroup(
            "pediatrics",
            "1.1",
            headings,
            pages,
            lines,
            indicationsHeading,
            patientInformationStart);

        JObject geriatrics = BuildPopulationGroup(
            "geriatrics",
            "1.2",
            headings,
            pages,
            lines,
            indicationsHeading,
            patientInformationStart);

        var sections = new JObject
        {
            ["indications"] = indications,
            ["pediatrics"] = pediatrics,
            ["geriatrics"] = geriatrics,
            ["contraindications"] = contraindications
        };

        var warnings = new List<string>();
        AddDocumentWarnings(pages, language, controlNumber, template, dates, sections, warnings);
        string documentStatus = DetermineDocumentStatus(pages, language, controlNumber, template, dates, sections);

        var response = new JObject
        {
            ["documentStatus"] = documentStatus,
            ["sourceUrl"] = string.IsNullOrWhiteSpace(sourceUrl) ? JValue.CreateNull() : new JValue(sourceUrl),
            ["pageCount"] = ResolveDocumentPageCount(pages),
            ["pagesReceived"] = pages.Count,
            ["language"] = language,
            ["controlNumber"] = controlNumber,
            ["template"] = template,
            ["dates"] = dates,
            ["sections"] = sections,
            ["warnings"] = new JArray(warnings)
        };

        return CreateJsonResponse(HttpStatusCode.OK, response);
    }

    private static bool TryReadPages(
        JToken pagesToken,
        out List<PageChunk> pages,
        out string message,
        out JObject details)
    {
        pages = new List<PageChunk>();
        message = null;
        details = null;

        JArray input = pagesToken as JArray;
        if (input == null || input.Count == 0)
        {
            message = "pages must be a non-empty array of page chunks.";
            return false;
        }

        if (input.Count > MaxPages)
        {
            message = "pages cannot contain more than " + MaxPages + " entries.";
            return false;
        }

        int previousPage = 0;
        int? expectedPageCount = null;
        var metadataValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < input.Count; index++)
        {
            JObject item = input[index] as JObject;
            if (item == null)
            {
                message = "Each pages entry must be an object.";
                details = new JObject { ["index"] = index };
                return false;
            }

            JObject metadata = item["metadata"] as JObject;
            int? pageNumber = ReadPositiveInt(metadata == null ? null : metadata["pageNumber"])
                ?? ReadPositiveInt(item["pageNumber"]);
            if (!pageNumber.HasValue)
            {
                message = "Every page chunk must provide a positive page number in metadata.pageNumber or pageNumber.";
                details = new JObject { ["index"] = index };
                return false;
            }

            if (pageNumber.Value <= previousPage)
            {
                message = "Page numbers must be strictly ascending and unique.";
                details = new JObject
                {
                    ["index"] = index,
                    ["pageNumber"] = pageNumber.Value,
                    ["previousPageNumber"] = previousPage
                };
                return false;
            }

            previousPage = pageNumber.Value;

            int? pageCount = ReadPositiveInt(metadata == null ? null : metadata["pageCount"])
                ?? ReadPositiveInt(item["pageCount"]);
            if ((metadata != null && metadata["pageCount"] != null && metadata["pageCount"].Type != JTokenType.Null &&
                 !ReadPositiveInt(metadata["pageCount"]).HasValue) ||
                (item["pageCount"] != null && item["pageCount"].Type != JTokenType.Null &&
                 !ReadPositiveInt(item["pageCount"]).HasValue))
            {
                message = "pageCount must be a positive integer when provided.";
                details = new JObject { ["index"] = index };
                return false;
            }
            if (pageCount.HasValue)
            {
                if (!expectedPageCount.HasValue)
                {
                    expectedPageCount = pageCount;
                }
                else if (expectedPageCount.Value != pageCount.Value)
                {
                    message = "All page chunks must report the same document page count.";
                    details = new JObject
                    {
                        ["index"] = index,
                        ["expectedPageCount"] = expectedPageCount.Value,
                        ["actualPageCount"] = pageCount.Value
                    };
                    return false;
                }
            }

            string contentType = ReadOptionalString(item["contentType"]);
            if (string.IsNullOrWhiteSpace(contentType) ||
                !contentType.StartsWith("text/markdown", StringComparison.OrdinalIgnoreCase))
            {
                message = "Every page chunk must have contentType text/markdown.";
                details = new JObject
                {
                    ["index"] = index,
                    ["contentType"] = contentType == null ? JValue.CreateNull() : new JValue(contentType)
                };
                return false;
            }

            JToken textToken = item["text"];
            if (textToken == null || textToken.Type != JTokenType.String)
            {
                message = "Every page chunk must have a string text value. Use an empty string for pages without consumable text.";
                details = new JObject { ["index"] = index };
                return false;
            }

            string text = textToken.ToString();
            if (text.Length > MaxPageCharacters)
            {
                message = "A page chunk exceeded the per-page character guard.";
                details = new JObject
                {
                    ["index"] = index,
                    ["characterCount"] = text.Length,
                    ["maximum"] = MaxPageCharacters
                };
                return false;
            }

            bool truncated;
            if (!TryReadOptionalBoolean(item["truncated"], out truncated))
            {
                message = "truncated must be a Boolean when provided.";
                details = new JObject { ["index"] = index };
                return false;
            }

            List<string> pageWarnings;
            if (!TryReadStringArray(item["warnings"], out pageWarnings))
            {
                message = "warnings must be an array of strings when provided.";
                details = new JObject { ["index"] = index };
                return false;
            }

            string ocrStatus = ReadOptionalString(item["ocrStatus"]);
            if (item["ocrStatus"] != null && item["ocrStatus"].Type != JTokenType.Null && item["ocrStatus"].Type != JTokenType.String)
            {
                message = "ocrStatus must be a string when provided.";
                details = new JObject { ["index"] = index };
                return false;
            }
            bool severeIntegrityRisk = IsSevereOcrStatus(ocrStatus);
            if (severeIntegrityRisk && !string.IsNullOrWhiteSpace(text))
            {
                pageWarnings.Add("Text was suppressed because the upstream OCR status was " + ocrStatus + ".");
                text = string.Empty;
            }

            if (metadata != null)
            {
                foreach (string field in new[] { "fileName", "pdfVersion", "fileSizeBytes", "title" })
                {
                    JToken value = metadata[field];
                    if (value == null || value.Type == JTokenType.Null || string.IsNullOrWhiteSpace(value.ToString()))
                    {
                        continue;
                    }

                    string serialized = value.ToString(Formatting.None);
                    string existing;
                    if (!metadataValues.TryGetValue(field, out existing))
                    {
                        metadataValues[field] = serialized;
                    }
                    else if (!string.Equals(existing, serialized, StringComparison.Ordinal))
                    {
                        message = "Document metadata must be consistent across page chunks.";
                        details = new JObject
                        {
                            ["index"] = index,
                            ["field"] = field,
                            ["expected"] = existing,
                            ["actual"] = serialized
                        };
                        return false;
                    }
                }
            }

            bool warningIntegrityRisk = pageWarnings.Any(IsIntegrityWarning);
            pages.Add(new PageChunk
            {
                InputIndex = index,
                PageNumber = pageNumber.Value,
                PageCount = pageCount,
                Text = text,
                ContentType = contentType,
                Truncated = truncated,
                Warnings = pageWarnings,
                OcrStatus = ocrStatus,
                HasIntegrityRisk = truncated || warningIntegrityRisk || severeIntegrityRisk,
                HasSevereIntegrityRisk = severeIntegrityRisk
            });
        }

        if (expectedPageCount.HasValue && pages[pages.Count - 1].PageNumber > expectedPageCount.Value)
        {
            message = "A page number cannot exceed the document page count.";
            details = new JObject
            {
                ["pageNumber"] = pages[pages.Count - 1].PageNumber,
                ["pageCount"] = expectedPageCount.Value
            };
            return false;
        }

        for (int i = 1; i < pages.Count; i++)
        {
            if (pages[i].PageNumber != pages[i - 1].PageNumber + 1)
            {
                pages[i].HasIntegrityRisk = true;
                pages[i - 1].HasIntegrityRisk = true;
            }
        }

        return true;
    }

    private static List<LineInfo> BuildLines(List<PageChunk> pages)
    {
        var lines = new List<LineInfo>();
        int order = 0;
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            string text = pages[pageIndex].Text ?? string.Empty;
            int offset = 0;
            while (offset < text.Length)
            {
                int contentEnd = offset;
                while (contentEnd < text.Length && text[contentEnd] != '\r' && text[contentEnd] != '\n')
                {
                    contentEnd++;
                }

                int end = contentEnd;
                if (end < text.Length && text[end] == '\r') end++;
                if (end < text.Length && text[end] == '\n') end++;

                string raw = text.Substring(offset, contentEnd - offset);
                lines.Add(new LineInfo
                {
                    Order = order++,
                    PageIndex = pageIndex,
                    PageNumber = pages[pageIndex].PageNumber,
                    StartOffset = offset,
                    EndOffset = end,
                    Raw = raw,
                    Normalized = NormalizeForDetection(raw)
                });
                offset = end;
            }
        }

        return lines;
    }

    private static List<HeadingInfo> BuildHeadings(List<LineInfo> lines, List<PageChunk> pages)
    {
        var headings = new List<HeadingInfo>();
        foreach (LineInfo line in lines)
        {
            HeadingInfo heading;
            if (TryCreateHeading(line, out heading))
            {
                headings.Add(heading);
            }
        }

        var explicitTocPages = new HashSet<int>(
            lines.Where(line =>
                    line.Normalized == "table of contents" ||
                    line.Normalized == "table des matieres" ||
                    line.Normalized == "table of content")
                .Select(line => line.PageNumber));

        var tocLikeCounts = new Dictionary<int, int>();
        foreach (HeadingInfo heading in headings)
        {
            if (LooksLikeTocEntry(heading.Line.Raw))
            {
                int count;
                tocLikeCounts.TryGetValue(heading.Line.PageNumber, out count);
                tocLikeCounts[heading.Line.PageNumber] = count + 1;
            }
        }

        foreach (HeadingInfo heading in headings)
        {
            int count;
            tocLikeCounts.TryGetValue(heading.Line.PageNumber, out count);
            heading.IsTableOfContents = LooksLikeTocEntry(heading.Line.Raw) ||
                explicitTocPages.Contains(heading.Line.PageNumber) || count >= 3;
        }

        return headings.OrderBy(h => h.Line.Order).ToList();
    }

    private static bool TryCreateHeading(LineInfo line, out HeadingInfo heading)
    {
        heading = null;
        string trimmed = line.Raw.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 240)
        {
            return false;
        }

        int markdownLevel = 0;
        while (markdownLevel < trimmed.Length && trimmed[markdownLevel] == '#') markdownLevel++;
        if (markdownLevel > 0)
        {
            trimmed = trimmed.Substring(markdownLevel).TrimStart();
        }

        trimmed = TrimMarkdownDecoration(trimmed);
        string candidateTitle = trimmed;
        string number = null;
        int level = markdownLevel > 0 ? markdownLevel : 0;

        Match numeric = NumericHeadingRegex.Match(trimmed);
        if (numeric.Success)
        {
            number = numeric.Groups["number"].Value;
            candidateTitle = TrimMarkdownDecoration(numeric.Groups["title"].Value);
            level = number.Count(c => c == '.') + 1;
        }

        string prefix = candidateTitle;
        int colon = candidateTitle.IndexOf(':');
        bool hasInlineBody = false;
        if (colon > 0 && colon < 100)
        {
            string possiblePrefix = TrimMarkdownDecoration(candidateTitle.Substring(0, colon));
            string prefixType = ClassifyHeadingType(NormalizeForDetection(possiblePrefix));
            if (prefixType != null)
            {
                prefix = possiblePrefix;
                hasInlineBody = !string.IsNullOrWhiteSpace(candidateTitle.Substring(colon + 1));
            }
        }

        string normalizedTitle = NormalizeForDetection(prefix);
        string type = ClassifyHeadingType(normalizedTitle);

        bool generic = numeric.Success || markdownLevel > 0 || IsKnownMajorHeading(normalizedTitle);
        if (type == null && !generic)
        {
            return false;
        }

        if (level == 0)
        {
            level = type == "pediatrics" || type == "geriatrics" ? 2 : 1;
        }

        heading = new HeadingInfo
        {
            Line = line,
            Display = line.Raw.Trim(),
            NormalizedTitle = normalizedTitle,
            Type = type,
            Number = number,
            Level = level,
            IsModernNumbered = numeric.Success,
            HasInlineBody = hasInlineBody
        };
        return true;
    }

    private static string ClassifyHeadingType(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        if (normalized == "indications" ||
            normalized == "indications and clinical use" ||
            normalized == "indication and clinical use" ||
            normalized == "indications et usage clinique")
        {
            return "indications";
        }

        if (normalized == "contraindications" ||
            normalized == "contra indication" ||
            normalized == "contre indications" ||
            normalized == "contre indication")
        {
            return "contraindications";
        }

        if (normalized == "pediatrics" || normalized == "paediatrics" ||
            normalized == "pediatric" || normalized.StartsWith("pediatrics ", StringComparison.Ordinal) ||
            normalized.StartsWith("pediatric population", StringComparison.Ordinal) ||
            normalized == "pediatrie" || normalized.StartsWith("population pediatrique", StringComparison.Ordinal) ||
            normalized.StartsWith("enfants et adolescents", StringComparison.Ordinal) || normalized == "enfants")
        {
            return "pediatrics";
        }

        if (normalized == "geriatrics" || normalized == "geriatric" ||
            normalized.StartsWith("geriatrics ", StringComparison.Ordinal) ||
            normalized.StartsWith("geriatric population", StringComparison.Ordinal) ||
            normalized == "geriatrie" || normalized.StartsWith("population geriatrique", StringComparison.Ordinal) ||
            normalized.StartsWith("personnes agees", StringComparison.Ordinal) || normalized == "aines")
        {
            return "geriatrics";
        }

        return null;
    }

    private static JObject DetectLanguage(List<PageChunk> pages)
    {
        List<PageChunk> front = pages.Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .Take(FrontMatterNonEmptyPages)
            .ToList();

        int enTitle = 0;
        int frTitle = 0;
        int enVocabulary = 0;
        int frVocabulary = 0;
        var evidence = new JArray();

        foreach (PageChunk page in front)
        {
            string normalized = NormalizeForDetection(page.Text);
            AddLanguageEvidence(normalized, "product monograph", "en", "title", 8, page.PageNumber, evidence, ref enTitle);
            AddLanguageEvidence(normalized, "monographie de produit", "fr", "title", 8, page.PageNumber, evidence, ref frTitle);
            AddLanguageEvidence(normalized, "including patient medication information", "en", "title", 3, page.PageNumber, evidence, ref enTitle);
            AddLanguageEvidence(normalized, "incluant les renseignements sur le medicament pour le patient", "fr", "title", 3, page.PageNumber, evidence, ref frTitle);

            AddLanguageEvidence(normalized, "indications and clinical use", "en", "vocabulary", 2, page.PageNumber, evidence, ref enVocabulary);
            AddLanguageEvidence(normalized, "warnings and precautions", "en", "vocabulary", 2, page.PageNumber, evidence, ref enVocabulary);
            AddLanguageEvidence(normalized, "dosage and administration", "en", "vocabulary", 2, page.PageNumber, evidence, ref enVocabulary);
            AddLanguageEvidence(normalized, "date of initial authorization", "en", "vocabulary", 2, page.PageNumber, evidence, ref enVocabulary);
            AddLanguageEvidence(normalized, "control no", "en", "vocabulary", 1, page.PageNumber, evidence, ref enVocabulary);

            AddLanguageEvidence(normalized, "indications et usage clinique", "fr", "vocabulary", 2, page.PageNumber, evidence, ref frVocabulary);
            AddLanguageEvidence(normalized, "mises en garde et precautions", "fr", "vocabulary", 2, page.PageNumber, evidence, ref frVocabulary);
            AddLanguageEvidence(normalized, "posologie et administration", "fr", "vocabulary", 2, page.PageNumber, evidence, ref frVocabulary);
            AddLanguageEvidence(normalized, "date de l autorisation initiale", "fr", "vocabulary", 2, page.PageNumber, evidence, ref frVocabulary);
            AddLanguageEvidence(normalized, "date de l homologation initiale", "fr", "vocabulary", 2, page.PageNumber, evidence, ref frVocabulary);
            AddLanguageEvidence(normalized, "de controle", "fr", "vocabulary", 1, page.PageNumber, evidence, ref frVocabulary);
        }

        string titleChoice = ScoreChoice(enTitle, frTitle, 4);
        string vocabularyChoice = ScoreChoice(enVocabulary, frVocabulary, 2);
        string value = "unknown";
        string status = "unknown";
        double confidence = 0.0;

        if (titleChoice != null && vocabularyChoice != null && titleChoice != vocabularyChoice)
        {
            status = "conflicting";
        }
        else if (titleChoice != null)
        {
            value = titleChoice;
            status = "found";
            confidence = vocabularyChoice == titleChoice ? 0.99 : 0.94;
        }
        else if (vocabularyChoice != null)
        {
            value = vocabularyChoice;
            status = "found";
            confidence = 0.82;
        }

        return new JObject
        {
            ["value"] = value,
            ["status"] = status,
            ["confidence"] = confidence,
            ["scores"] = new JObject
            {
                ["englishTitle"] = enTitle,
                ["frenchTitle"] = frTitle,
                ["englishVocabulary"] = enVocabulary,
                ["frenchVocabulary"] = frVocabulary
            },
            ["evidence"] = evidence
        };
    }

    private static void AddLanguageEvidence(
        string haystack,
        string phrase,
        string language,
        string strategy,
        int weight,
        int page,
        JArray evidence,
        ref int score)
    {
        if (haystack.IndexOf(phrase, StringComparison.Ordinal) < 0) return;
        score += weight;
        evidence.Add(new JObject
        {
            ["language"] = language,
            ["strategy"] = strategy,
            ["phrase"] = phrase,
            ["page"] = page,
            ["weight"] = weight
        });
    }

    private static JObject ExtractControlNumber(List<PageChunk> pages, List<LineInfo> lines)
    {
        HashSet<int> frontPages = new HashSet<int>(pages.Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .Take(FrontMatterNonEmptyPages)
            .Select(page => page.PageNumber));

        var matches = new List<ControlCandidate>();
        for (int index = 0; index < lines.Count; index++)
        {
            LineInfo line = lines[index];
            if (!frontPages.Contains(line.PageNumber) || !IsControlLabel(line.Normalized)) continue;

            List<Match> numberMatches = ControlNumberRegex.Matches(line.Raw).Cast<Match>().ToList();
            var evidenceLines = new List<LineInfo> { line };
            if (numberMatches.Count == 0)
            {
                for (int next = index + 1; next < lines.Count && next <= index + 3; next++)
                {
                    if (lines[next].PageNumber != line.PageNumber) break;
                    if (string.IsNullOrWhiteSpace(lines[next].Raw)) continue;
                    if (IsControlLabel(lines[next].Normalized) || TryCreateHeadingForBoundary(lines[next])) break;
                    evidenceLines.Add(lines[next]);
                    numberMatches = ControlNumberRegex.Matches(lines[next].Raw).Cast<Match>().ToList();
                    break;
                }
            }

            foreach (Match match in numberMatches)
            {
                matches.Add(new ControlCandidate
                {
                    Value = match.Groups["value"].Value,
                    PageNumber = line.PageNumber,
                    Label = line.Raw.Trim(),
                    Evidence = string.Join("\n", evidenceLines.Select(item => item.Raw).Where(raw => !string.IsNullOrWhiteSpace(raw)))
                });
            }
        }

        List<IGrouping<string, ControlCandidate>> groups = matches.GroupBy(candidate => candidate.Value).ToList();
        if (groups.Count == 0)
        {
            return new JObject
            {
                ["value"] = JValue.CreateNull(),
                ["status"] = "notFound",
                ["matchedLabel"] = JValue.CreateNull(),
                ["page"] = JValue.CreateNull(),
                ["rawEvidence"] = JValue.CreateNull(),
                ["confidence"] = 0.0
            };
        }

        if (groups.Count > 1)
        {
            return new JObject
            {
                ["value"] = JValue.CreateNull(),
                ["status"] = "ambiguous",
                ["matchedLabel"] = JValue.CreateNull(),
                ["page"] = JValue.CreateNull(),
                ["rawEvidence"] = new JArray(matches.Select(candidate => candidate.Evidence).Distinct()),
                ["confidence"] = 0.0,
                ["candidates"] = new JArray(groups.Select(group => group.Key))
            };
        }

        ControlCandidate selected = groups[0].First();
        return new JObject
        {
            ["value"] = selected.Value,
            ["status"] = "found",
            ["matchedLabel"] = selected.Label,
            ["page"] = selected.PageNumber,
            ["rawEvidence"] = selected.Evidence,
            ["confidence"] = 0.99
        };
    }

    private static bool TryCreateHeadingForBoundary(LineInfo line)
    {
        HeadingInfo ignored;
        return TryCreateHeading(line, out ignored);
    }

    private static JObject ClassifyTemplate(List<HeadingInfo> headings, int patientInformationStart)
    {
        List<HeadingInfo> actual = headings.Where(heading =>
                !heading.IsTableOfContents &&
                (patientInformationStart < 0 || heading.Line.Order < patientInformationStart))
            .ToList();

        bool modernIndications = actual.Any(h => h.Type == "indications" && h.Number == "1");
        bool modernContraindications = actual.Any(h => h.Type == "contraindications" && h.Number == "2");
        int numberedTopLevel = actual.Where(h => h.IsModernNumbered && h.Level == 1)
            .Select(h => h.Number)
            .Distinct(StringComparer.Ordinal)
            .Count();
        bool numberedPopulation = actual.Any(h => h.Type == "pediatrics" && h.Number == "1.1") ||
            actual.Any(h => h.Type == "geriatrics" && h.Number == "1.2");

        bool legacyIndications = actual.Any(h => h.Type == "indications" && !h.IsModernNumbered);
        bool legacyContraindications = actual.Any(h => h.Type == "contraindications" && !h.IsModernNumbered);
        int legacyMajorHeadings = actual.Count(h => !h.IsModernNumbered && h.Level == 1);

        string family = "unknown";
        JToken isModern = JValue.CreateNull();
        double confidence = 0.0;
        var evidence = new JArray();

        if (modernIndications && modernContraindications && (numberedTopLevel >= 3 || numberedPopulation))
        {
            family = "modern";
            isModern = new JValue(true);
            confidence = numberedTopLevel >= 4 ? 0.99 : 0.95;
            foreach (HeadingInfo h in actual.Where(h => h.IsModernNumbered).Take(8))
            {
                evidence.Add(CreateHeadingEvidence(h));
            }
        }
        else if (!modernIndications && !modernContraindications && legacyIndications && legacyContraindications && legacyMajorHeadings >= 3)
        {
            family = "legacy";
            isModern = new JValue(false);
            confidence = 0.94;
            foreach (HeadingInfo h in actual.Where(h => !h.IsModernNumbered && h.Level == 1).Take(8))
            {
                evidence.Add(CreateHeadingEvidence(h));
            }
        }

        return new JObject
        {
            ["isModern"] = isModern,
            ["family"] = family,
            ["status"] = family == "unknown" ? "unknown" : "found",
            ["confidence"] = confidence,
            ["evidence"] = evidence
        };
    }

    private static JObject ExtractDates(List<PageChunk> pages, List<LineInfo> lines)
    {
        HashSet<int> frontPages = new HashSet<int>(pages.Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .Take(FrontMatterNonEmptyPages)
            .Select(page => page.PageNumber));

        var candidates = new List<DateCandidate>();
        foreach (LineInfo line in lines.Where(item => frontPages.Contains(item.PageNumber)))
        {
            AddDateMatches(line, lines, DateIsoRegex, candidates);
            AddDateMatches(line, lines, DateDayFirstRegex, candidates);
            AddDateMatches(line, lines, DateMonthFirstRegex, candidates);
        }

        candidates = candidates
            .GroupBy(candidate => candidate.PageNumber + "|" + candidate.IsoValue + "|" + candidate.RawText, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.LineOrder)
            .ThenBy(candidate => candidate.MatchIndex)
            .ToList();

        var observed = new JArray(candidates.Select(CreateObservedDate));
        List<DateCandidate> explicitInitial = candidates.Where(candidate => candidate.LabelKind == "initialAuthorization").ToList();
        List<DateCandidate> explicitRevision = candidates.Where(candidate => candidate.LabelKind == "revision").ToList();

        JObject initial = CreateExplicitDateResult(explicitInitial);
        JObject revision = CreateExplicitDateResult(explicitRevision);

        HashSet<int> titlePages = new HashSet<int>(pages.Where(page => frontPages.Contains(page.PageNumber))
            .Where(page =>
            {
                string normalized = NormalizeForDetection(page.Text);
                return normalized.Contains("product monograph") || normalized.Contains("monographie de produit");
            })
            .Select(page => page.PageNumber));
        List<DateCandidate> fallbackCandidates = candidates.Where(candidate => titlePages.Contains(candidate.PageNumber)).ToList();
        bool fallbackEligible = fallbackCandidates.Count > 0 && fallbackCandidates.Count <= 2;

        if ((string)initial["status"] == "notFound" && fallbackEligible)
        {
            DateCandidate earliest = fallbackCandidates.OrderBy(candidate => candidate.Value).First();
            if (earliest.LabelKind != "revision")
            {
                initial = CreateDateResult(earliest, "found", "chronologicalInference", fallbackCandidates.Count == 1 ? 0.58 : 0.72);
            }
        }

        if ((string)revision["status"] == "notFound" && fallbackEligible && fallbackCandidates.Count >= 2)
        {
            DateCandidate latest = fallbackCandidates.OrderByDescending(candidate => candidate.Value).First();
            DateTime initialValue;
            if (latest.LabelKind != "initialAuthorization" && TryReadResultDate(initial, out initialValue) && latest.Value > initialValue)
            {
                revision = CreateDateResult(latest, "found", "chronologicalInference", 0.72);
            }
        }

        DateTime parsedInitial;
        DateTime parsedRevision;
        if (TryReadResultDate(initial, out parsedInitial) && TryReadResultDate(revision, out parsedRevision) && parsedRevision < parsedInitial)
        {
            initial = CreateConflictingDateResult(initial, "Initial authorization occurs after revision.");
            revision = CreateConflictingDateResult(revision, "Revision occurs before initial authorization.");
        }

        return new JObject
        {
            ["initialAuthorization"] = initial,
            ["revision"] = revision,
            ["observedDates"] = observed
        };
    }

    private static void AddDateMatches(LineInfo line, List<LineInfo> allLines, Regex regex, List<DateCandidate> candidates)
    {
        foreach (Match match in regex.Matches(line.Raw))
        {
            int year;
            int month;
            int day;
            if (!int.TryParse(match.Groups["year"].Value, out year) ||
                !int.TryParse(match.Groups["day"].Value, out day))
            {
                continue;
            }

            if (match.Groups["month"].Value.All(char.IsDigit))
            {
                if (!int.TryParse(match.Groups["month"].Value, out month)) continue;
            }
            else
            {
                month = ParseMonth(match.Groups["month"].Value);
                if (month == 0) continue;
            }

            DateTime value;
            try
            {
                value = new DateTime(year, month, day);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            string prefix = line.Raw.Substring(0, match.Index).Trim();
            string labelKind;
            string label;
            FindNearestDateLabel(prefix, out labelKind, out label);

            if (labelKind == null)
            {
                LineInfo previous = allLines.LastOrDefault(candidate => candidate.Order < line.Order && candidate.PageNumber == line.PageNumber && !string.IsNullOrWhiteSpace(candidate.Raw));
                if (previous != null && line.Order - previous.Order <= 2)
                {
                    string previousKind = GetDateLabelKind(previous.Normalized);
                    if (previousKind != null)
                    {
                        labelKind = previousKind;
                        label = previous.Raw.Trim();
                    }
                }
            }

            candidates.Add(new DateCandidate
            {
                Value = value,
                IsoValue = value.ToString("yyyy-MM-dd"),
                RawText = match.Value,
                Label = label,
                LabelKind = labelKind,
                PageNumber = line.PageNumber,
                LineOrder = line.Order,
                MatchIndex = match.Index
            });
        }
    }

    private static void FindNearestDateLabel(string prefix, out string labelKind, out string label)
    {
        labelKind = null;
        label = null;
        if (string.IsNullOrWhiteSpace(prefix)) return;

        string[] segments = Regex.Split(prefix, @"(?:<br\s*/?>|\r?\n|\|)", RegexOptions.IgnoreCase);
        for (int index = segments.Length - 1; index >= 0; index--)
        {
            string segment = segments[index].Trim(' ', ':', '-', '–', '—');
            if (string.IsNullOrWhiteSpace(segment)) continue;
            string kind = GetDateLabelKind(NormalizeForDetection(segment));
            if (kind == null) continue;
            labelKind = kind;
            label = segment;
            return;
        }

        labelKind = GetDateLabelKind(NormalizeForDetection(prefix));
        label = labelKind == null ? null : prefix.Trim(' ', ':', '-', '–', '—');
    }

    private static JObject BuildPopulationGroup(
        string type,
        string preferredNumber,
        List<HeadingInfo> headings,
        List<PageChunk> pages,
        List<LineInfo> lines,
        HeadingInfo indicationsHeading,
        int patientInformationStart)
    {
        List<HeadingInfo> candidates = headings.Where(heading =>
                heading.Type == type &&
                !heading.IsTableOfContents &&
                (patientInformationStart < 0 || heading.Line.Order < patientInformationStart))
            .ToList();

        if (candidates.Count == 0)
        {
            return new JObject
            {
                ["primary"] = CreateNotFoundSection(),
                ["additional"] = new JArray()
            };
        }

        int indicationsEnd = indicationsHeading == null
            ? -1
            : FindSectionEndOrder(indicationsHeading, headings, lines);

        HeadingInfo primary = candidates
            .OrderByDescending(candidate => PopulationScore(candidate, preferredNumber, indicationsHeading, indicationsEnd))
            .ThenBy(candidate => candidate.Line.Order)
            .First();

        var additional = new JArray();
        foreach (HeadingInfo candidate in candidates.Where(candidate => candidate != primary).OrderBy(candidate => candidate.Line.Order))
        {
            additional.Add(BuildSection(candidate, headings, pages, lines));
        }

        return new JObject
        {
            ["primary"] = BuildSection(primary, headings, pages, lines),
            ["additional"] = additional
        };
    }

    private static int PopulationScore(HeadingInfo heading, string preferredNumber, HeadingInfo indications, int indicationsEnd)
    {
        int score = 0;
        if (heading.Number == preferredNumber) score += 120;
        if (heading.IsModernNumbered) score += 20;
        if (indications != null && heading.Line.Order > indications.Line.Order && heading.Line.Order < indicationsEnd) score += 100;
        if (heading.HasInlineBody) score += 10;
        return score;
    }

    private static HeadingInfo SelectPrimarySectionHeading(
        List<HeadingInfo> headings,
        string type,
        string preferredNumber,
        int patientInformationStart,
        out bool ambiguous)
    {
        ambiguous = false;
        List<HeadingInfo> candidates = headings.Where(heading =>
                heading.Type == type &&
                !heading.IsTableOfContents &&
                (patientInformationStart < 0 || heading.Line.Order < patientInformationStart))
            .ToList();

        if (candidates.Count == 0) return null;

        var scored = candidates.Select(candidate => new
        {
            Heading = candidate,
            Score = SectionHeadingScore(candidate, preferredNumber, headings)
        }).OrderByDescending(item => item.Score).ThenBy(item => item.Heading.Line.Order).ToList();

        if (scored.Count > 1 && scored[0].Score == scored[1].Score &&
            scored[0].Heading.Line.PageNumber != scored[1].Heading.Line.PageNumber)
        {
            ambiguous = true;
            return null;
        }

        return scored[0].Heading;
    }

    private static int SectionHeadingScore(HeadingInfo heading, string preferredNumber, List<HeadingInfo> headings)
    {
        int score = 0;
        if (heading.Number == preferredNumber) score += 150;
        else if (heading.IsModernNumbered) score += 30;
        else score += 70;

        if (HasBodyAfter(heading, headings)) score += 30;
        if (heading.Line.PageNumber <= 10) score += 5;
        return score;
    }

    private static bool HasBodyAfter(HeadingInfo heading, List<HeadingInfo> headings)
    {
        if (heading.HasInlineBody) return true;
        HeadingInfo next = headings.FirstOrDefault(candidate =>
            !candidate.IsTableOfContents && candidate.Line.Order > heading.Line.Order && candidate.Level <= heading.Level);
        return next == null || next.Line.Order > heading.Line.Order + 1;
    }

    private static JObject BuildSection(
        HeadingInfo start,
        List<HeadingInfo> headings,
        List<PageChunk> pages,
        List<LineInfo> lines)
    {
        if (start == null) return CreateNotFoundSection();

        HeadingInfo endHeading = headings.FirstOrDefault(candidate =>
            !candidate.IsTableOfContents && candidate.Line.Order > start.Line.Order && candidate.Level <= start.Level);

        int startPageIndex = start.Line.PageIndex;
        int endPageIndex = endHeading == null ? pages.Count - 1 : endHeading.Line.PageIndex;
        int endOffsetOnLastPage = endHeading == null ? pages[endPageIndex].Text.Length : endHeading.Line.StartOffset;
        var slices = new JArray();
        var markdownParts = new List<string>();
        bool incomplete = false;

        for (int pageIndex = startPageIndex; pageIndex <= endPageIndex; pageIndex++)
        {
            PageChunk page = pages[pageIndex];
            int sliceStart = pageIndex == startPageIndex ? start.Line.StartOffset : 0;
            int sliceEnd = pageIndex == endPageIndex ? endOffsetOnLastPage : page.Text.Length;
            sliceStart = Math.Max(0, Math.Min(sliceStart, page.Text.Length));
            sliceEnd = Math.Max(sliceStart, Math.Min(sliceEnd, page.Text.Length));
            string markdown = page.Text.Substring(sliceStart, sliceEnd - sliceStart);

            if (markdown.Length == 0 && pageIndex == endPageIndex && endHeading != null) continue;

            slices.Add(new JObject
            {
                ["pageNumber"] = page.PageNumber,
                ["startOffset"] = sliceStart,
                ["endOffset"] = sliceEnd,
                ["markdown"] = markdown
            });
            markdownParts.Add(markdown);
            if (page.HasIntegrityRisk) incomplete = true;

            if (pageIndex > startPageIndex && page.PageNumber != pages[pageIndex - 1].PageNumber + 1)
            {
                incomplete = true;
            }
        }

        if (endHeading == null && !HasCompleteDocumentCoverage(pages)) incomplete = true;
        string rawMarkdown = string.Join("\n\n", markdownParts);
        if (NormalizeForDetection(rawMarkdown) == start.NormalizedTitle) incomplete = true;

        return new JObject
        {
            ["status"] = incomplete ? "incomplete" : "found",
            ["matchedHeading"] = start.Display,
            ["startPage"] = start.Line.PageNumber,
            ["endPage"] = slices.Count == 0 ? start.Line.PageNumber : (int)slices[slices.Count - 1]["pageNumber"],
            ["rawMarkdown"] = rawMarkdown,
            ["pageSlices"] = slices,
            ["urls"] = ExtractUrls(markdownParts),
            ["confidence"] = incomplete ? 0.70 : 0.97
        };
    }

    private static JObject CreateNotFoundSection()
    {
        return new JObject
        {
            ["status"] = "notFound",
            ["matchedHeading"] = JValue.CreateNull(),
            ["startPage"] = JValue.CreateNull(),
            ["endPage"] = JValue.CreateNull(),
            ["rawMarkdown"] = "",
            ["pageSlices"] = new JArray(),
            ["urls"] = new JArray(),
            ["confidence"] = 0.0
        };
    }

    private static JObject CreateAmbiguousSection(List<HeadingInfo> candidates)
    {
        return new JObject
        {
            ["status"] = "ambiguous",
            ["matchedHeading"] = JValue.CreateNull(),
            ["startPage"] = JValue.CreateNull(),
            ["endPage"] = JValue.CreateNull(),
            ["rawMarkdown"] = "",
            ["pageSlices"] = new JArray(),
            ["urls"] = new JArray(),
            ["confidence"] = 0.0,
            ["candidates"] = new JArray(candidates.Select(CreateHeadingEvidence))
        };
    }

    private static int FindSectionEndOrder(HeadingInfo start, List<HeadingInfo> headings, List<LineInfo> lines)
    {
        HeadingInfo end = headings.FirstOrDefault(candidate =>
            !candidate.IsTableOfContents && candidate.Line.Order > start.Line.Order && candidate.Level <= start.Level);
        return end == null ? (lines.Count == 0 ? int.MaxValue : lines[lines.Count - 1].Order + 1) : end.Line.Order;
    }

    private static int FindPatientInformationStart(List<LineInfo> lines)
    {
        foreach (LineInfo line in lines)
        {
            string normalized = line.Normalized;
            if (normalized.StartsWith("part iii consumer information", StringComparison.Ordinal) ||
                normalized.StartsWith("part iii patient medication information", StringComparison.Ordinal) ||
                normalized.StartsWith("partie iii renseignements", StringComparison.Ordinal) ||
                normalized.StartsWith("partie iii information", StringComparison.Ordinal) ||
                normalized == "patient medication information" ||
                normalized == "renseignements sur le medicament pour le patient")
            {
                return line.Order;
            }
        }

        return -1;
    }

    private static void AddDocumentWarnings(
        List<PageChunk> pages,
        JObject language,
        JObject control,
        JObject template,
        JObject dates,
        JObject sections,
        List<string> warnings)
    {
        if (!HasCompleteDocumentCoverage(pages)) warnings.Add("The received page chunks do not cover every source page contiguously.");
        if (pages.Any(page => page.Truncated)) warnings.Add("At least one upstream page chunk was truncated.");
        if (pages.Any(page => page.HasSevereIntegrityRisk)) warnings.Add("At least one page had low-confidence or unsupported OCR and was not trusted.");
        if ((string)language["status"] != "found") warnings.Add("Document language could not be accepted deterministically.");
        if ((string)control["status"] != "found") warnings.Add("Control number was not accepted deterministically.");
        if ((string)template["status"] != "found") warnings.Add("Product-monograph template family could not be accepted deterministically.");
        if ((string)dates["initialAuthorization"]["status"] != "found") warnings.Add("Initial authorization date was not accepted deterministically.");
        if ((string)sections["indications"]["status"] != "found") warnings.Add("Indications section is absent, ambiguous, or incomplete.");
        if ((string)sections["contraindications"]["status"] != "found") warnings.Add("Contraindications section is absent, ambiguous, or incomplete.");

        foreach (string warning in pages.SelectMany(page => page.Warnings))
        {
            if (!warnings.Contains(warning, StringComparer.Ordinal)) warnings.Add(warning);
        }
    }

    private static string DetermineDocumentStatus(
        List<PageChunk> pages,
        JObject language,
        JObject control,
        JObject template,
        JObject dates,
        JObject sections)
    {
        if (!pages.Any(page => !string.IsNullOrWhiteSpace(page.Text))) return "unsupported";

        bool ambiguous = pages.Any(page => page.HasSevereIntegrityRisk) ||
            (string)language["status"] == "conflicting" ||
            (string)control["status"] == "ambiguous" ||
            (string)dates["initialAuthorization"]["status"] == "ambiguous" ||
            (string)dates["revision"]["status"] == "ambiguous" ||
            (string)sections["indications"]["status"] == "ambiguous" ||
            (string)sections["contraindications"]["status"] == "ambiguous";
        if (ambiguous) return "needsReview";

        bool partial = !HasCompleteDocumentCoverage(pages) || pages.Any(page => page.HasIntegrityRisk) ||
            (string)language["status"] != "found" ||
            (string)control["status"] != "found" ||
            (string)template["status"] != "found" ||
            (string)dates["initialAuthorization"]["status"] != "found" ||
            (string)sections["indications"]["status"] != "found" ||
            (string)sections["contraindications"]["status"] != "found";
        return partial ? "partial" : "parsed";
    }

    private static bool HasCompleteDocumentCoverage(List<PageChunk> pages)
    {
        if (pages.Count == 0 || pages[0].PageNumber != 1) return false;
        for (int index = 1; index < pages.Count; index++)
        {
            if (pages[index].PageNumber != pages[index - 1].PageNumber + 1) return false;
        }

        int? pageCount = pages.Select(page => page.PageCount).FirstOrDefault(value => value.HasValue);
        return !pageCount.HasValue || pages[pages.Count - 1].PageNumber == pageCount.Value;
    }

    private static int ResolveDocumentPageCount(List<PageChunk> pages)
    {
        int? pageCount = pages.Select(page => page.PageCount).FirstOrDefault(value => value.HasValue);
        return pageCount ?? pages[pages.Count - 1].PageNumber;
    }

    private static bool IsControlLabel(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        return Regex.IsMatch(normalized, @"\b(?:submission\s+|presentation\s+)?control\s+(?:no|number)\b") ||
            Regex.IsMatch(normalized, @"^control\s+\d{5,9}$") ||
            Regex.IsMatch(normalized, @"\b(?:n(?:\s+o)?|no|numero|o\s+n)\s+de\s+controle\b") ||
            normalized.Contains("controle de la presentation") ||
            normalized.Contains("numero de la demande") ||
            normalized.Contains("numerodecontrole");
    }

    private static string GetDateLabelKind(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized.Contains("initial authorization") || normalized.Contains("autorisation initiale") ||
            normalized.Contains("homologation initiale") ||
            normalized.Contains("initial approval") || normalized.Contains("original date of approval"))
        {
            return "initialAuthorization";
        }

        if (normalized.Contains("date of revision") || normalized.Contains("last revision") ||
            normalized.Contains("date revised") || normalized.Contains("date de revision"))
        {
            return "revision";
        }

        return null;
    }

    private static JObject CreateExplicitDateResult(List<DateCandidate> candidates)
    {
        if (candidates.Count == 0) return CreateMissingDateResult();
        List<IGrouping<string, DateCandidate>> groups = candidates.GroupBy(candidate => candidate.IsoValue).ToList();
        if (groups.Count > 1)
        {
            return new JObject
            {
                ["status"] = "ambiguous",
                ["isoValue"] = JValue.CreateNull(),
                ["rawText"] = JValue.CreateNull(),
                ["matchedLabel"] = JValue.CreateNull(),
                ["page"] = JValue.CreateNull(),
                ["confidence"] = 0.0,
                ["extractionBasis"] = "explicitLabelConflict",
                ["evidence"] = new JArray(candidates.Select(CreateObservedDate))
            };
        }

        return CreateDateResult(groups[0].First(), "found", "explicitLabel", 0.99);
    }

    private static JObject CreateMissingDateResult()
    {
        return new JObject
        {
            ["status"] = "notFound",
            ["isoValue"] = JValue.CreateNull(),
            ["rawText"] = JValue.CreateNull(),
            ["matchedLabel"] = JValue.CreateNull(),
            ["page"] = JValue.CreateNull(),
            ["confidence"] = 0.0,
            ["extractionBasis"] = JValue.CreateNull(),
            ["evidence"] = new JArray()
        };
    }

    private static JObject CreateDateResult(DateCandidate candidate, string status, string basis, double confidence)
    {
        return new JObject
        {
            ["status"] = status,
            ["isoValue"] = candidate.IsoValue,
            ["rawText"] = candidate.RawText,
            ["matchedLabel"] = candidate.Label == null ? JValue.CreateNull() : new JValue(candidate.Label),
            ["page"] = candidate.PageNumber,
            ["confidence"] = confidence,
            ["extractionBasis"] = basis,
            ["evidence"] = new JArray(CreateObservedDate(candidate))
        };
    }

    private static JObject CreateObservedDate(DateCandidate candidate)
    {
        return new JObject
        {
            ["isoValue"] = candidate.IsoValue,
            ["rawText"] = candidate.RawText,
            ["matchedLabel"] = candidate.Label == null ? JValue.CreateNull() : new JValue(candidate.Label),
            ["page"] = candidate.PageNumber,
            ["confidence"] = candidate.LabelKind == null ? 0.80 : 0.99,
            ["extractionBasis"] = candidate.LabelKind == null ? "observed" : "explicitLabel",
            ["roleHint"] = candidate.LabelKind == null ? JValue.CreateNull() : new JValue(candidate.LabelKind)
        };
    }

    private static JObject CreateConflictingDateResult(JObject original, string reason)
    {
        return new JObject
        {
            ["status"] = "ambiguous",
            ["isoValue"] = JValue.CreateNull(),
            ["rawText"] = original["rawText"],
            ["matchedLabel"] = original["matchedLabel"],
            ["page"] = original["page"],
            ["confidence"] = 0.0,
            ["extractionBasis"] = "chronologyConflict",
            ["evidence"] = original["evidence"],
            ["reason"] = reason
        };
    }

    private static bool TryReadResultDate(JObject result, out DateTime value)
    {
        value = default(DateTime);
        if (result == null || (string)result["status"] != "found") return false;
        string iso = (string)result["isoValue"];
        Match match = DateIsoRegex.Match(iso ?? string.Empty);
        if (!match.Success) return false;
        try
        {
            value = new DateTime(
                int.Parse(match.Groups["year"].Value),
                int.Parse(match.Groups["month"].Value),
                int.Parse(match.Groups["day"].Value));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ParseMonth(string raw)
    {
        string value = NormalizeForDetection(raw).Replace(" ", string.Empty);
        var months = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["january"] = 1, ["jan"] = 1, ["janvier"] = 1, ["janv"] = 1,
            ["february"] = 2, ["feb"] = 2, ["fevrier"] = 2, ["fevr"] = 2, ["fev"] = 2,
            ["march"] = 3, ["mar"] = 3, ["mars"] = 3,
            ["april"] = 4, ["apr"] = 4, ["avril"] = 4, ["avr"] = 4,
            ["may"] = 5, ["mai"] = 5,
            ["june"] = 6, ["jun"] = 6, ["juin"] = 6,
            ["july"] = 7, ["jul"] = 7, ["juillet"] = 7, ["juil"] = 7,
            ["august"] = 8, ["aug"] = 8, ["aout"] = 8,
            ["september"] = 9, ["sep"] = 9, ["sept"] = 9, ["septembre"] = 9,
            ["october"] = 10, ["oct"] = 10, ["octobre"] = 10,
            ["november"] = 11, ["nov"] = 11, ["novembre"] = 11,
            ["december"] = 12, ["dec"] = 12, ["decembre"] = 12
        };

        int month;
        return months.TryGetValue(value, out month) ? month : 0;
    }

    private static JArray ExtractUrls(IEnumerable<string> markdownParts)
    {
        var hits = new List<KeyValuePair<int, string>>();
        int baseOffset = 0;
        foreach (string markdown in markdownParts)
        {
            string part = markdown ?? string.Empty;
            foreach (Regex regex in new[] { MarkdownUrlRegex, BareUrlRegex })
            {
                foreach (Match match in regex.Matches(part))
                {
                    string value = match.Groups["url"].Value.TrimEnd('.', ',', ';', ':', '!', '?', ')');
                    Uri uri;
                    if (Uri.TryCreate(value, UriKind.Absolute, out uri))
                    {
                        hits.Add(new KeyValuePair<int, string>(baseOffset + match.Groups["url"].Index, value));
                    }
                }
            }

            baseOffset += part.Length + 1;
        }

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<int, string> hit in hits.OrderBy(item => item.Key))
        {
            if (seen.Add(hit.Value)) values.Add(hit.Value);
        }

        return new JArray(values);
    }

    private static JObject CreateHeadingEvidence(HeadingInfo heading)
    {
        return new JObject
        {
            ["heading"] = heading.Display,
            ["page"] = heading.Line.PageNumber,
            ["number"] = heading.Number == null ? JValue.CreateNull() : new JValue(heading.Number)
        };
    }

    private static string ScoreChoice(int english, int french, int minimumDifference)
    {
        if (english == 0 && french == 0) return null;
        if (english - french >= minimumDifference) return "en";
        if (french - english >= minimumDifference) return "fr";
        return null;
    }

    private static bool LooksLikeTocEntry(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return Regex.IsMatch(raw, @"\.{3,}\s*\d+\s*$") ||
            Regex.IsMatch(raw, @"\s{3,}\d+\s*$");
    }

    private static bool IsKnownMajorHeading(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        foreach (string prefix in new[]
        {
            "product monograph", "monographie de produit", "part i", "part ii", "part iii", "partie i", "partie ii", "partie iii",
            "serious warnings", "warnings and precautions", "warning and precautions", "mises en garde", "precautions",
            "adverse reactions", "effets indesirables", "drug interactions", "interactions medicamenteuses",
            "dosage and administration", "posologie et administration", "overdosage", "surdosage",
            "action and clinical pharmacology", "mode d action et pharmacologie clinique", "clinical pharmacology", "pharmacologie clinique",
            "storage stability and disposal", "storage conditions", "entreposage stabilite et elimination", "conditions d entreposage",
            "dosage forms strengths composition and packaging", "formes posologiques teneurs composition et conditionnement",
            "clinical trials", "essais cliniques", "microbiology", "microbiologie", "toxicology", "toxicologie", "references",
            "special populations", "populations particulieres", "use in specific populations"
        })
        {
            if (normalized == prefix || normalized.StartsWith(prefix + " ", StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string TrimMarkdownDecoration(string value)
    {
        string result = value.Trim();
        bool changed = true;
        while (changed && result.Length > 1)
        {
            changed = false;
            foreach (string marker in new[] { "**", "__", "*", "_", "`" })
            {
                if (result.StartsWith(marker, StringComparison.Ordinal) && result.EndsWith(marker, StringComparison.Ordinal) && result.Length > marker.Length * 2)
                {
                    result = result.Substring(marker.Length, result.Length - marker.Length * 2).Trim();
                    changed = true;
                }
            }
        }

        return result.TrimEnd(':').Trim();
    }

    private static string NormalizeForDetection(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        bool previousSpace = true;
        foreach (char original in value)
        {
            if (original >= '\u0300' && original <= '\u036f') continue;
            char character = FoldCharacter(char.ToLowerInvariant(original));
            if (character == '\0')
            {
                if (original == 'œ' || original == 'Œ' || original == 'æ' || original == 'Æ')
                {
                    builder.Append(original == 'æ' || original == 'Æ' ? "ae" : "oe");
                    previousSpace = false;
                    continue;
                }

                character = ' ';
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousSpace = false;
            }
            else if (!previousSpace)
            {
                builder.Append(' ');
                previousSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static char FoldCharacter(char character)
    {
        switch (character)
        {
            case 'à': case 'á': case 'â': case 'ä': case 'ã': case 'å': return 'a';
            case 'ç': return 'c';
            case 'è': case 'é': case 'ê': case 'ë': return 'e';
            case 'ì': case 'í': case 'î': case 'ï': return 'i';
            case 'ñ': return 'n';
            case 'ò': case 'ó': case 'ô': case 'ö': case 'õ': return 'o';
            case 'ù': case 'ú': case 'û': case 'ü': return 'u';
            case 'ý': case 'ÿ': return 'y';
            case 'æ': case 'œ': return '\0';
            case 'º': return 'o';
            default:
                return char.IsLetterOrDigit(character) ? character : ' ';
        }
    }

    private static bool IsIntegrityWarning(string warning)
    {
        string normalized = NormalizeForDetection(warning);
        return normalized.Contains("truncat") || normalized.Contains("missing page") ||
            normalized.Contains("low confidence") || normalized.Contains("unsupported") ||
            normalized.Contains("output limit") || normalized.Contains("incomplete");
    }

    private static bool IsSevereOcrStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return false;
        string normalized = NormalizeForDetection(status).Replace(" ", string.Empty);
        return normalized == "lowconfidence" || normalized.StartsWith("unsupported", StringComparison.Ordinal) ||
            normalized == "resourcelimit" || normalized == "invalidpdf";
    }

    private static bool TryReadOptionalBoolean(JToken token, out bool value)
    {
        value = false;
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type != JTokenType.Boolean) return false;
        value = token.Value<bool>();
        return true;
    }

    private static bool TryReadStringArray(JToken token, out List<string> values)
    {
        values = new List<string>();
        if (token == null || token.Type == JTokenType.Null) return true;
        JArray array = token as JArray;
        if (array == null || array.Any(item => item.Type != JTokenType.String)) return false;
        values.AddRange(array.Select(item => item.ToString()));
        return true;
    }

    private static int? ReadPositiveInt(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        int value;
        if ((token.Type == JTokenType.Integer || token.Type == JTokenType.String) && int.TryParse(token.ToString(), out value) && value > 0)
        {
            return value;
        }

        return null;
    }

    private static string ReadOptionalString(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        return token.Type == JTokenType.String ? token.ToString().Trim() : null;
    }

    private static string DecodeOperationId(string operationId)
    {
        try
        {
            byte[] data = Convert.FromBase64String(operationId);
            return Encoding.UTF8.GetString(data);
        }
        catch (FormatException)
        {
            return operationId;
        }
    }

    private static HttpResponseMessage CreateJsonResponse(HttpStatusCode statusCode, JObject body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage CreateErrorResponse(HttpStatusCode statusCode, string code, string message, JObject details)
    {
        var error = new JObject
        {
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };
        if (details != null) ((JObject)error["error"])["details"] = details;
        return CreateJsonResponse(statusCode, error);
    }

    private sealed class PageChunk
    {
        public int InputIndex { get; set; }
        public int PageNumber { get; set; }
        public int? PageCount { get; set; }
        public string Text { get; set; }
        public string ContentType { get; set; }
        public bool Truncated { get; set; }
        public List<string> Warnings { get; set; }
        public string OcrStatus { get; set; }
        public bool HasIntegrityRisk { get; set; }
        public bool HasSevereIntegrityRisk { get; set; }
    }

    private sealed class LineInfo
    {
        public int Order { get; set; }
        public int PageIndex { get; set; }
        public int PageNumber { get; set; }
        public int StartOffset { get; set; }
        public int EndOffset { get; set; }
        public string Raw { get; set; }
        public string Normalized { get; set; }
    }

    private sealed class HeadingInfo
    {
        public LineInfo Line { get; set; }
        public string Display { get; set; }
        public string NormalizedTitle { get; set; }
        public string Type { get; set; }
        public string Number { get; set; }
        public int Level { get; set; }
        public bool IsModernNumbered { get; set; }
        public bool HasInlineBody { get; set; }
        public bool IsTableOfContents { get; set; }
    }

    private sealed class ControlCandidate
    {
        public string Value { get; set; }
        public int PageNumber { get; set; }
        public string Label { get; set; }
        public string Evidence { get; set; }
    }

    private sealed class DateCandidate
    {
        public DateTime Value { get; set; }
        public string IsoValue { get; set; }
        public string RawText { get; set; }
        public string Label { get; set; }
        public string LabelKind { get; set; }
        public int PageNumber { get; set; }
        public int LineOrder { get; set; }
        public int MatchIndex { get; set; }
    }
}
