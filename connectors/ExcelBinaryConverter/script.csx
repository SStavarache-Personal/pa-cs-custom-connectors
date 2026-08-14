// SPDX-License-Identifier: Apache-2.0
//
// Value-only .xls/.xlsb to .xlsx conversion for Power Automate custom code.
// The BIFF8 and BIFF12 parsing approach is adapted from the Apache-2.0
// powerquery-driverless Xls.Workbook.pq and Xlsb.Workbook.pq readers:
// https://github.com/SStavarache-Personal/powerquery-driverless

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class Script : ScriptBase
{
    private const int MaxInputBytes = 25 * 1024 * 1024;
    private const int MaxOutputBytes = 75 * 1024 * 1024;
    private const int MaxCells = 500000;
    private const int MaxSheets = 1024;
    private static readonly TimeSpan ExecutionBudget = TimeSpan.FromSeconds(110);

    public override async Task<HttpResponseMessage> ExecuteAsync()
    {
        string operationId = DecodeOperationId(this.Context.OperationId);
        if (!string.Equals(operationId, "ConvertToXlsx", StringComparison.Ordinal))
        {
            return CreateErrorResponse(
                HttpStatusCode.BadRequest,
                "UNKNOWN_OPERATION",
                "Unknown operation: " + operationId,
                null);
        }

        return await ConvertToXlsxAsync().ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> ConvertToXlsxAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            JObject body = await ReadRequestBodyAsync().ConfigureAwait(false);
            string inputFileName = body["fileName"] == null
                ? null
                : body["fileName"].ToString();
            byte[] source = ReadFileContent(body["fileContent"]);

            if (source.Length == 0)
            {
                throw new ConnectorException(
                    HttpStatusCode.BadRequest,
                    "EMPTY_FILE",
                    "The supplied file is empty.");
            }

            if (source.Length > MaxInputBytes)
            {
                throw new ConnectorException(
                    HttpStatusCode.RequestEntityTooLarge,
                    "INPUT_TOO_LARGE",
                    "The decoded source file exceeds the 25 MB conversion limit.");
            }

            Action guard = delegate
            {
                this.CancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.Elapsed > ExecutionBudget)
                {
                    throw new ConnectorException(
                        HttpStatusCode.RequestTimeout,
                        "CONVERSION_TIMEOUT",
                        "Conversion exceeded the connector's 110-second safety budget.");
                }
            };

            WorkbookData workbook;
            string sourceFormat;

            if (BinaryHelpers.HasCfbSignature(source))
            {
                sourceFormat = "xls";
                workbook = new XlsReader(source, guard, MaxCells, MaxSheets).Read();
            }
            else if (BinaryHelpers.HasZipSignature(source))
            {
                sourceFormat = "xlsb";
                workbook = new XlsbReader(source, guard, MaxCells, MaxSheets).Read();
            }
            else
            {
                throw new ConnectorException(
                    HttpStatusCode.BadRequest,
                    "UNSUPPORTED_FILE_FORMAT",
                    "The file is neither a BIFF8 .xls compound file nor a BIFF12 .xlsb ZIP package.");
            }

            guard();
            byte[] output = XlsxWriter.Write(workbook, guard);
            if (output.Length > MaxOutputBytes)
            {
                throw new ConnectorException(
                    HttpStatusCode.RequestEntityTooLarge,
                    "OUTPUT_TOO_LARGE",
                    "The generated .xlsx file exceeds the 75 MB output limit.");
            }

            stopwatch.Stop();
            JObject responseBody = new JObject
            {
                ["fileName"] = BuildOutputFileName(inputFileName),
                ["contentType"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ["fileContent"] = Convert.ToBase64String(output),
                ["sourceFormat"] = sourceFormat,
                ["sheetCount"] = workbook.Sheets.Count,
                ["cellCount"] = workbook.CellCount,
                ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds
            };

            return CreateJsonResponse(HttpStatusCode.OK, responseBody);
        }
        catch (ConnectorException ex)
        {
            return CreateErrorResponse(ex.StatusCode, ex.Code, ex.Message, ex.Details);
        }
        catch (OperationCanceledException)
        {
            return CreateErrorResponse(
                HttpStatusCode.RequestTimeout,
                "CONVERSION_CANCELLED",
                "The conversion was cancelled by the connector runtime.",
                null);
        }
        catch (InvalidDataException ex)
        {
            return CreateErrorResponse(
                HttpStatusCodes.UnprocessableEntity,
                "INVALID_WORKBOOK",
                ex.Message,
                null);
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(
                HttpStatusCodes.UnprocessableEntity,
                "CONVERSION_FAILED",
                "The workbook could not be converted: " + ex.Message,
                null);
        }
    }

    private async Task<JObject> ReadRequestBodyAsync()
    {
        string content = await this.Context.Request.Content
            .ReadAsStringAsync()
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_REQUEST",
                "A JSON request body is required.");
        }

        try
        {
            return JObject.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_JSON",
                "The request body is not valid JSON: " + ex.Message);
        }
    }

    private static byte[] ReadFileContent(JToken token)
    {
        if (token is JObject contentObject)
        {
            token = contentObject["$content"];
        }

        if (token == null || token.Type != JTokenType.String)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "MISSING_FILE_CONTENT",
                "The 'fileContent' field must contain a base64 string or an object with a '$content' base64 string.");
        }

        string value = token.ToString().Trim();
        int comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            value = value.Substring(comma + 1);
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new ConnectorException(
                HttpStatusCode.BadRequest,
                "INVALID_BASE64",
                "The 'fileContent' field is not valid base64.");
        }
    }

    private static string BuildOutputFileName(string inputFileName)
    {
        if (string.IsNullOrWhiteSpace(inputFileName))
        {
            return "converted.xlsx";
        }

        string leaf = Path.GetFileName(inputFileName.Trim());
        if (string.IsNullOrWhiteSpace(leaf))
        {
            return "converted.xlsx";
        }

        string withoutExtension = Path.GetFileNameWithoutExtension(leaf);
        return string.IsNullOrWhiteSpace(withoutExtension)
            ? "converted.xlsx"
            : withoutExtension + ".xlsx";
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
            Content = CreateJsonContent(body.ToString(Newtonsoft.Json.Formatting.None))
        };
    }

    private static HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string code,
        string message,
        JObject details)
    {
        JObject error = new JObject
        {
            ["code"] = code,
            ["message"] = message
        };

        if (details != null)
        {
            error["details"] = details;
        }

        return CreateJsonResponse(statusCode, new JObject { ["error"] = error });
    }
}

internal sealed class ConnectorException : Exception
{
    public ConnectorException(HttpStatusCode statusCode, string code, string message)
        : this(statusCode, code, message, null)
    {
    }

    public ConnectorException(HttpStatusCode statusCode, string code, string message, JObject details)
        : base(message)
    {
        this.StatusCode = statusCode;
        this.Code = code;
        this.Details = details;
    }

    public HttpStatusCode StatusCode { get; private set; }
    public string Code { get; private set; }
    public JObject Details { get; private set; }
}

internal static class HttpStatusCodes
{
    // HttpStatusCode.UnprocessableEntity is absent from some .NET Standard 2.0
    // reference assemblies even though the numeric HTTP status is universal.
    public static readonly HttpStatusCode UnprocessableEntity = (HttpStatusCode)422;
}

internal enum CellValueKind
{
    Blank,
    Number,
    Text,
    Boolean,
    Error
}

internal enum DateStyleKind
{
    General = 0,
    Date = 1,
    DateTime = 2,
    Time = 3
}

internal sealed class CellData
{
    public int Row;
    public int Column;
    public CellValueKind Kind;
    public DateStyleKind DateStyle;
    public double NumberValue;
    public string TextValue;
    public bool BooleanValue;
}

internal sealed class SheetData
{
    private readonly HashSet<long> _coordinates = new HashSet<long>();

    public SheetData(string name, int visibility)
    {
        this.Name = name;
        this.Visibility = visibility;
        this.Cells = new List<CellData>();
    }

    public string Name { get; private set; }
    public int Visibility { get; private set; }
    public List<CellData> Cells { get; private set; }

    public bool AddCell(CellData cell)
    {
        if (cell.Row < 0 || cell.Row > 1048575 || cell.Column < 0 || cell.Column > 16383)
        {
            throw new ConnectorException(
                HttpStatusCodes.UnprocessableEntity,
                "CELL_OUT_OF_RANGE",
                "A source cell falls outside the .xlsx worksheet grid.",
                new JObject { ["row"] = cell.Row + 1, ["column"] = cell.Column + 1 });
        }

        long key = ((long)cell.Row << 20) | (uint)cell.Column;
        if (!this._coordinates.Add(key))
        {
            return false;
        }

        this.Cells.Add(cell);
        return true;
    }
}

internal sealed class WorkbookData
{
    private readonly int _maxCells;
    private readonly int _maxSheets;

    public WorkbookData(bool date1904, int maxCells, int maxSheets)
    {
        this.Date1904 = date1904;
        this._maxCells = maxCells;
        this._maxSheets = maxSheets;
        this.Sheets = new List<SheetData>();
    }

    public bool Date1904 { get; private set; }
    public List<SheetData> Sheets { get; private set; }
    public int CellCount { get; private set; }

    public SheetData AddSheet(string name, int visibility)
    {
        if (this.Sheets.Count >= this._maxSheets)
        {
            throw new ConnectorException(
                HttpStatusCode.RequestEntityTooLarge,
                "TOO_MANY_SHEETS",
                "The workbook exceeds the supported sheet-count limit.");
        }

        if (string.IsNullOrEmpty(name) || name.Length > 31)
        {
            throw new ConnectorException(
                HttpStatusCodes.UnprocessableEntity,
                "INVALID_SHEET_NAME",
                "A worksheet has an invalid Excel sheet name.");
        }

        SheetData sheet = new SheetData(name, visibility);
        this.Sheets.Add(sheet);
        return sheet;
    }

    public void AddCell(SheetData sheet, CellData cell)
    {
        if (!sheet.AddCell(cell))
        {
            return;
        }

        this.CellCount++;
        if (this.CellCount > this._maxCells)
        {
            throw new ConnectorException(
                HttpStatusCode.RequestEntityTooLarge,
                "TOO_MANY_CELLS",
                "The workbook exceeds the 500,000 populated-cell conversion limit.");
        }
    }
}

internal static class BinaryHelpers
{
    private static readonly HashSet<int> DateBuiltins = new HashSet<int>(
        Enumerable.Range(14, 9)
            .Concat(Enumerable.Range(27, 10))
            .Concat(new[] { 45, 46, 47 })
            .Concat(Enumerable.Range(50, 9))
            .Concat(Enumerable.Range(71, 11)));

    public static bool HasCfbSignature(byte[] data)
    {
        byte[] signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        if (data == null || data.Length < signature.Length)
        {
            return false;
        }

        for (int i = 0; i < signature.Length; i++)
        {
            if (data[i] != signature[i]) return false;
        }

        return true;
    }

    public static bool HasZipSignature(byte[] data)
    {
        return data != null
            && data.Length >= 4
            && data[0] == 0x50
            && data[1] == 0x4B
            && ((data[2] == 0x03 && data[3] == 0x04)
                || (data[2] == 0x05 && data[3] == 0x06)
                || (data[2] == 0x07 && data[3] == 0x08));
    }

    public static ushort U16(byte[] data, int offset)
    {
        Require(data, offset, 2);
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    public static uint U32(byte[] data, int offset)
    {
        Require(data, offset, 4);
        return (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
    }

    public static ulong U64(byte[] data, int offset)
    {
        uint low = U32(data, offset);
        uint high = U32(data, offset + 4);
        return low | ((ulong)high << 32);
    }

    public static double F64(byte[] data, int offset)
    {
        Require(data, offset, 8);
        return BitConverter.ToDouble(data, offset);
    }

    public static byte[] Slice(byte[] data, int offset, int length)
    {
        Require(data, offset, length);
        byte[] result = new byte[length];
        Buffer.BlockCopy(data, offset, result, 0, length);
        return result;
    }

    public static void Require(byte[] data, int offset, int length)
    {
        if (data == null || offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException("The workbook contains a truncated binary structure.");
        }
    }

    public static double DecodeRk(uint value)
    {
        bool divideBy100 = (value & 1U) != 0;
        bool isInteger = (value & 2U) != 0;
        double result;

        if (isInteger)
        {
            int integer = (int)(value >> 2);
            if (integer >= 536870912)
            {
                integer -= 1073741824;
            }
            result = integer;
        }
        else
        {
            ulong bits = ((ulong)(value & 0xFFFFFFFCU)) << 32;
            result = BitConverter.Int64BitsToDouble((long)bits);
        }

        return divideBy100 ? result / 100.0 : result;
    }

    public static string ErrorText(byte code)
    {
        switch (code)
        {
            case 0x00: return "#NULL!";
            case 0x07: return "#DIV/0!";
            case 0x0F: return "#VALUE!";
            case 0x17: return "#REF!";
            case 0x1D: return "#NAME?";
            case 0x24: return "#NUM!";
            case 0x2A: return "#N/A";
            case 0x2B: return "#GETTING_DATA";
            default: return "#VALUE!";
        }
    }

    public static DateStyleKind DateStyleForFormat(int formatId, string customCode)
    {
        if (!DateBuiltins.Contains(formatId) && string.IsNullOrEmpty(customCode))
        {
            return DateStyleKind.General;
        }

        string code = customCode ?? BuiltinDateCode(formatId);
        if (string.IsNullOrEmpty(code))
        {
            return DateStyleKind.General;
        }

        bool inQuote = false;
        bool escaped = false;
        bool inBracket = false;
        bool hasDate = false;
        bool hasTime = false;
        bool hasMonth = false;

        for (int i = 0; i < code.Length; i++)
        {
            char ch = char.ToLowerInvariant(code[i]);
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\')
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (inQuote) continue;
            if (ch == '[')
            {
                inBracket = true;
                continue;
            }
            if (ch == ']')
            {
                inBracket = false;
                continue;
            }
            if (inBracket)
            {
                if (ch == 'h' || ch == 's') hasTime = true;
                continue;
            }
            if (ch == 'y' || ch == 'd') hasDate = true;
            if (ch == 'h' || ch == 's') hasTime = true;
            if (ch == 'm') hasMonth = true;
        }

        // A standalone month token is a date. When hour/second tokens are also
        // present and no year/day token exists, Excel interprets m as minutes.
        if (hasMonth && !hasDate && !hasTime) hasDate = true;

        if (!hasDate && !hasTime && DateBuiltins.Contains(formatId))
        {
            if ((formatId >= 18 && formatId <= 21) || (formatId >= 45 && formatId <= 47))
                hasTime = true;
            else
                hasDate = true;
        }

        if (hasDate && hasTime) return DateStyleKind.DateTime;
        if (hasTime) return DateStyleKind.Time;
        return hasDate ? DateStyleKind.Date : DateStyleKind.General;
    }

    private static string BuiltinDateCode(int formatId)
    {
        if ((formatId >= 18 && formatId <= 21) || (formatId >= 45 && formatId <= 47))
            return "hh:mm:ss";
        if (formatId == 22)
            return "yyyy-mm-dd hh:mm:ss";
        return DateBuiltins.Contains(formatId) ? "yyyy-mm-dd" : null;
    }

    public static CellData NumberCell(int row, int column, double value, DateStyleKind style)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new InvalidDataException("A numeric cell contains a non-finite IEEE value.");
        }

        return new CellData
        {
            Row = row,
            Column = column,
            Kind = CellValueKind.Number,
            NumberValue = value,
            DateStyle = style
        };
    }

    public static CellData TextCell(int row, int column, string value)
    {
        if (value != null && value.Length > 32767)
        {
            throw new ConnectorException(
                HttpStatusCodes.UnprocessableEntity,
                "CELL_TEXT_TOO_LONG",
                "A text cell exceeds Excel's 32,767-character limit.");
        }

        return new CellData
        {
            Row = row,
            Column = column,
            Kind = CellValueKind.Text,
            TextValue = value ?? string.Empty
        };
    }

    public static CellData BooleanCell(int row, int column, bool value)
    {
        return new CellData
        {
            Row = row,
            Column = column,
            Kind = CellValueKind.Boolean,
            BooleanValue = value
        };
    }

    public static CellData ErrorCell(int row, int column, byte code)
    {
        return new CellData
        {
            Row = row,
            Column = column,
            Kind = CellValueKind.Error,
            TextValue = ErrorText(code)
        };
    }

    public static CellData BlankCell(int row, int column)
    {
        return new CellData { Row = row, Column = column, Kind = CellValueKind.Blank };
    }
}

internal sealed class BiffRecord
{
    public int Id;
    public int Offset;
    public int Length;
}

internal sealed class SegmentedBiffStringReader
{
    private readonly List<byte[]> _segments;
    private int _segment;
    private int _offset;

    public SegmentedBiffStringReader(List<byte[]> segments, int initialOffset)
    {
        this._segments = segments;
        this._segment = 0;
        this._offset = initialOffset;
    }

    public byte ReadByte()
    {
        Normalize();
        if (this._segment >= this._segments.Count)
            throw new InvalidDataException("A BIFF8 string is truncated.");
        return this._segments[this._segment][this._offset++];
    }

    public ushort ReadUInt16()
    {
        int a = ReadByte();
        int b = ReadByte();
        return (ushort)(a | (b << 8));
    }

    public uint ReadUInt32()
    {
        uint a = ReadUInt16();
        uint b = ReadUInt16();
        return a | (b << 16);
    }

    public void Skip(long count)
    {
        while (count > 0)
        {
            Normalize();
            if (this._segment >= this._segments.Count)
                throw new InvalidDataException("A BIFF8 string is truncated.");
            int available = this._segments[this._segment].Length - this._offset;
            int take = (int)Math.Min(count, available);
            this._offset += take;
            count -= take;
        }
    }

    public string ReadRichString(bool shortCount)
    {
        int characterCount = shortCount ? ReadByte() : ReadUInt16();
        byte flags = ReadByte();
        bool wide = (flags & 1) != 0;
        int richRuns = (flags & 8) != 0 ? ReadUInt16() : 0;
        uint extensionSize = (flags & 4) != 0 ? ReadUInt32() : 0;
        string result = ReadCharacters(characterCount, wide);
        Skip((long)richRuns * 4L + extensionSize);
        return result;
    }

    private string ReadCharacters(int count, bool wide)
    {
        StringBuilder builder = new StringBuilder(count);
        while (count > 0)
        {
            Normalize();
            if (this._segment >= this._segments.Count)
                throw new InvalidDataException("BIFF8 character data is truncated.");

            byte[] current = this._segments[this._segment];
            int unit = wide ? 2 : 1;
            int availableCharacters = (current.Length - this._offset) / unit;
            if (availableCharacters == 0)
            {
                this._segment++;
                this._offset = 0;
                if (this._segment >= this._segments.Count)
                    throw new InvalidDataException("BIFF8 character data is truncated.");
                byte continuationFlags = ReadByte();
                wide = (continuationFlags & 1) != 0;
                continue;
            }

            int take = Math.Min(count, availableCharacters);
            if (wide)
            {
                builder.Append(Encoding.Unicode.GetString(current, this._offset, take * 2));
            }
            else
            {
                for (int i = 0; i < take; i++)
                    builder.Append((char)current[this._offset + i]);
            }

            this._offset += take * unit;
            count -= take;
            if (count > 0 && this._offset >= current.Length)
            {
                this._segment++;
                this._offset = 0;
                if (this._segment >= this._segments.Count)
                    throw new InvalidDataException("BIFF8 character data is truncated.");
                byte continuationFlags = ReadByte();
                wide = (continuationFlags & 1) != 0;
            }
        }

        return builder.ToString();
    }

    private void Normalize()
    {
        while (this._segment < this._segments.Count
            && this._offset >= this._segments[this._segment].Length)
        {
            this._segment++;
            this._offset = 0;
        }
    }
}

internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFEU;
    private const uint FreeSector = 0xFFFFFFFFU;
    private readonly byte[] _file;
    private readonly Action _guard;
    private readonly ushort _majorVersion;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly List<uint> _fat;
    private readonly List<uint> _miniFat;
    private readonly List<DirectoryEntry> _directory;
    private readonly byte[] _miniStream;

    private sealed class DirectoryEntry
    {
        public string Name;
        public byte Type;
        public uint Start;
        public long Size;
    }

    public CompoundFile(byte[] file, Action guard)
    {
        this._file = file;
        this._guard = guard;
        if (!BinaryHelpers.HasCfbSignature(file) || file.Length < 512)
            throw new InvalidDataException("The file is not a valid Compound Binary File.");
        if (BinaryHelpers.U16(file, 28) != 0xFFFE)
            throw new InvalidDataException("The compound file uses an unsupported byte order.");

        this._majorVersion = BinaryHelpers.U16(file, 26);
        if (this._majorVersion != 3 && this._majorVersion != 4)
            throw new InvalidDataException("The compound file uses an unsupported format version.");

        int sectorShift = BinaryHelpers.U16(file, 30);
        int miniSectorShift = BinaryHelpers.U16(file, 32);
        if ((sectorShift != 9 && sectorShift != 12) || miniSectorShift != 6)
            throw new InvalidDataException("The compound file uses unsupported sector sizes.");

        this._sectorSize = 1 << sectorShift;
        this._miniSectorSize = 1 << miniSectorShift;
        this._miniCutoff = BinaryHelpers.U32(file, 56);

        uint fatSectorCount = BinaryHelpers.U32(file, 44);
        uint firstDirectorySector = BinaryHelpers.U32(file, 48);
        uint firstMiniFatSector = BinaryHelpers.U32(file, 60);
        uint miniFatSectorCount = BinaryHelpers.U32(file, 64);
        uint firstDifatSector = BinaryHelpers.U32(file, 68);
        uint difatSectorCount = BinaryHelpers.U32(file, 72);

        List<uint> fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < fatSectorCount; i++)
        {
            uint sector = BinaryHelpers.U32(file, 76 + i * 4);
            if (sector < 0xFFFFFFFCU) fatSectors.Add(sector);
        }

        uint difatSector = firstDifatSector;
        HashSet<uint> difatSeen = new HashSet<uint>();
        int difatEntriesPerSector = this._sectorSize / 4 - 1;
        for (uint d = 0; d < difatSectorCount && difatSector < 0xFFFFFFFCU; d++)
        {
            this._guard();
            if (!difatSeen.Add(difatSector))
                throw new InvalidDataException("The compound DIFAT chain contains a cycle.");
            int offset = SectorOffset(difatSector);
            for (int i = 0; i < difatEntriesPerSector && fatSectors.Count < fatSectorCount; i++)
            {
                uint sector = BinaryHelpers.U32(file, offset + i * 4);
                if (sector < 0xFFFFFFFCU) fatSectors.Add(sector);
            }
            difatSector = BinaryHelpers.U32(file, offset + this._sectorSize - 4);
        }

        if (fatSectors.Count < fatSectorCount)
            throw new InvalidDataException("The compound file FAT is incomplete.");

        this._fat = new List<uint>(fatSectors.Count * (this._sectorSize / 4));
        foreach (uint sector in fatSectors.Take((int)fatSectorCount))
        {
            int offset = SectorOffset(sector);
            for (int i = 0; i < this._sectorSize / 4; i++)
                this._fat.Add(BinaryHelpers.U32(file, offset + i * 4));
        }

        byte[] directoryBytes = ReadRegularChain(firstDirectorySector, -1);
        this._directory = new List<DirectoryEntry>();
        for (int offset = 0; offset + 128 <= directoryBytes.Length; offset += 128)
        {
            ushort nameBytes = BinaryHelpers.U16(directoryBytes, offset + 64);
            byte type = directoryBytes[offset + 66];
            string name = string.Empty;
            if (nameBytes >= 2 && nameBytes <= 64)
                name = Encoding.Unicode.GetString(directoryBytes, offset, nameBytes - 2);
            ulong size64 = this._majorVersion == 3
                ? BinaryHelpers.U32(directoryBytes, offset + 120)
                : BinaryHelpers.U64(directoryBytes, offset + 120);
            if (size64 > int.MaxValue)
                throw new ConnectorException(HttpStatusCode.RequestEntityTooLarge, "STREAM_TOO_LARGE", "A compound-file stream is too large to convert.");
            this._directory.Add(new DirectoryEntry
            {
                Name = name,
                Type = type,
                Start = BinaryHelpers.U32(directoryBytes, offset + 116),
                Size = (long)size64
            });
        }

        DirectoryEntry root = this._directory.FirstOrDefault(entry => entry.Type == 5);
        if (root == null)
            throw new InvalidDataException("The compound file has no root directory entry.");
        this._miniStream = root.Size == 0 ? new byte[0] : ReadRegularChain(root.Start, root.Size);

        this._miniFat = new List<uint>();
        if (miniFatSectorCount > 0 && firstMiniFatSector < 0xFFFFFFFCU)
        {
            long miniFatBytesRequested = (long)miniFatSectorCount * this._sectorSize;
            byte[] miniFatBytes = ReadRegularChain(firstMiniFatSector, miniFatBytesRequested);
            for (int offset = 0; offset + 4 <= miniFatBytes.Length; offset += 4)
                this._miniFat.Add(BinaryHelpers.U32(miniFatBytes, offset));
        }
    }

    public bool HasStream(string name)
    {
        return this._directory.Any(entry => entry.Type == 2 && string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public byte[] ReadWorkbookStream()
    {
        DirectoryEntry entry = this._directory
            .Where(item => item.Type == 2
                && (string.Equals(item.Name, "Workbook", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Name, "Book", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => string.Equals(item.Name, "Workbook", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();

        if (entry == null)
        {
            if (HasStream("EncryptedPackage") || HasStream("EncryptionInfo"))
                throw new ConnectorException(HttpStatusCodes.UnprocessableEntity, "ENCRYPTED_WORKBOOK", "Encrypted or password-protected workbooks are not supported.");
            throw new InvalidDataException("The compound file contains no BIFF Workbook stream.");
        }

        if (entry.Size < this._miniCutoff)
            return ReadMiniChain(entry.Start, entry.Size);
        return ReadRegularChain(entry.Start, entry.Size);
    }

    private byte[] ReadRegularChain(uint firstSector, long requestedSize)
    {
        using (MemoryStream output = new MemoryStream())
        {
            uint sector = firstSector;
            HashSet<uint> seen = new HashSet<uint>();
            while (sector < 0xFFFFFFFCU && (requestedSize < 0 || output.Length < requestedSize))
            {
                this._guard();
                if (sector >= this._fat.Count || !seen.Add(sector))
                    throw new InvalidDataException("A compound-file sector chain is invalid or cyclic.");
                int offset = SectorOffset(sector);
                int take = this._sectorSize;
                if (requestedSize >= 0)
                    take = (int)Math.Min(take, requestedSize - output.Length);
                output.Write(this._file, offset, take);
                sector = this._fat[(int)sector];
            }

            if (requestedSize >= 0 && output.Length != requestedSize)
                throw new InvalidDataException("A compound-file stream ended before its declared size.");
            return output.ToArray();
        }
    }

    private byte[] ReadMiniChain(uint firstSector, long requestedSize)
    {
        using (MemoryStream output = new MemoryStream())
        {
            uint sector = firstSector;
            HashSet<uint> seen = new HashSet<uint>();
            while (sector < 0xFFFFFFFCU && output.Length < requestedSize)
            {
                this._guard();
                if (sector >= this._miniFat.Count || !seen.Add(sector))
                    throw new InvalidDataException("A compound-file mini-sector chain is invalid or cyclic.");
                long sourceOffset = (long)sector * this._miniSectorSize;
                int take = (int)Math.Min(this._miniSectorSize, requestedSize - output.Length);
                if (sourceOffset < 0 || sourceOffset > this._miniStream.Length - take)
                    throw new InvalidDataException("A compound-file mini-sector points outside the mini stream.");
                output.Write(this._miniStream, (int)sourceOffset, take);
                sector = this._miniFat[(int)sector];
            }

            if (output.Length != requestedSize)
                throw new InvalidDataException("A mini-stream ended before its declared size.");
            return output.ToArray();
        }
    }

    private int SectorOffset(uint sector)
    {
        long offset = ((long)sector + 1L) * this._sectorSize;
        if (offset < 0 || offset > this._file.Length - this._sectorSize)
            throw new InvalidDataException("A compound-file sector points outside the file.");
        return (int)offset;
    }
}

internal sealed class XlsReader
{
    private readonly byte[] _file;
    private readonly Action _guard;
    private readonly int _maxCells;
    private readonly int _maxSheets;
    private byte[] _stream;
    private List<string> _sharedStrings;
    private List<int> _xfFormats;
    private Dictionary<int, string> _customFormats;

    private sealed class BoundSheet
    {
        public int Offset;
        public int Visibility;
        public int Type;
        public string Name;
    }

    public XlsReader(byte[] file, Action guard, int maxCells, int maxSheets)
    {
        this._file = file;
        this._guard = guard;
        this._maxCells = maxCells;
        this._maxSheets = maxSheets;
    }

    public WorkbookData Read()
    {
        this._stream = new CompoundFile(this._file, this._guard).ReadWorkbookStream();
        List<BiffRecord> globals = ReadRecords(0);
        if (globals.Count == 0 || globals[0].Id != 0x0809 || globals[0].Length < 4)
            throw new InvalidDataException("The BIFF Workbook stream does not begin with BOF.");
        if (BinaryHelpers.U16(this._stream, globals[0].Offset) != 0x0600)
            throw new ConnectorException(HttpStatusCodes.UnprocessableEntity, "UNSUPPORTED_BIFF_VERSION", "Only BIFF8 .xls files from Excel 97-2003 are supported.");
        if (globals.Any(record => record.Id == 0x002F))
            throw new ConnectorException(HttpStatusCodes.UnprocessableEntity, "ENCRYPTED_WORKBOOK", "Encrypted or password-protected workbooks are not supported.");

        bool date1904 = globals.Any(record => record.Id == 0x0022
            && record.Length >= 2
            && BinaryHelpers.U16(this._stream, record.Offset) == 1);

        this._customFormats = new Dictionary<int, string>();
        this._xfFormats = new List<int>();
        List<BoundSheet> bounds = new List<BoundSheet>();

        for (int i = 0; i < globals.Count; i++)
        {
            this._guard();
            BiffRecord record = globals[i];
            if (record.Id == 0x041E && record.Length >= 5)
            {
                int formatId = BinaryHelpers.U16(this._stream, record.Offset);
                this._customFormats[formatId] = ReadRecordString(globals, i, 2, false);
            }
            else if (record.Id == 0x00E0 && record.Length >= 4)
            {
                this._xfFormats.Add(BinaryHelpers.U16(this._stream, record.Offset + 2));
            }
            else if (record.Id == 0x0085 && record.Length >= 8)
            {
                List<byte[]> segments = CollectSegments(globals, i, 6);
                string name = new SegmentedBiffStringReader(segments, 0).ReadRichString(true);
                bounds.Add(new BoundSheet
                {
                    Offset = checked((int)BinaryHelpers.U32(this._stream, record.Offset)),
                    Visibility = this._stream[record.Offset + 4] & 3,
                    Type = this._stream[record.Offset + 5],
                    Name = name
                });
            }
        }

        BiffRecord sst = globals.FirstOrDefault(record => record.Id == 0x00FC);
        this._sharedStrings = sst == null ? new List<string>() : ReadSharedStrings(globals, globals.IndexOf(sst));

        if (bounds.Count == 0)
            throw new InvalidDataException("The BIFF workbook contains no BOUNDSHEET records.");
        BoundSheet unsupported = bounds.FirstOrDefault(sheet => sheet.Type != 0);
        if (unsupported != null)
            throw new ConnectorException(HttpStatusCodes.UnprocessableEntity, "UNSUPPORTED_SHEET_TYPE", "The value-only converter supports worksheets only; sheet '" + unsupported.Name + "' is a chart, macro, or dialog sheet.");

        WorkbookData workbook = new WorkbookData(date1904, this._maxCells, this._maxSheets);
        foreach (BoundSheet bound in bounds)
        {
            this._guard();
            SheetData sheet = workbook.AddSheet(bound.Name, bound.Visibility);
            ReadSheet(bound.Offset, workbook, sheet);
        }

        return workbook;
    }

    private List<BiffRecord> ReadRecords(int start)
    {
        if (start < 0 || start > this._stream.Length - 4)
            throw new InvalidDataException("A BIFF substream offset points outside the Workbook stream.");
        List<BiffRecord> records = new List<BiffRecord>();
        int position = start;
        while (position + 4 <= this._stream.Length)
        {
            this._guard();
            int id = BinaryHelpers.U16(this._stream, position);
            int length = BinaryHelpers.U16(this._stream, position + 2);
            if (position + 4 > this._stream.Length - length)
                throw new InvalidDataException("A BIFF record is truncated.");
            records.Add(new BiffRecord { Id = id, Offset = position + 4, Length = length });
            position += 4 + length;
            if (id == 0x000A) break;
        }
        return records;
    }

    private List<byte[]> CollectSegments(List<BiffRecord> records, int index, int relativeOffset)
    {
        BiffRecord first = records[index];
        if (relativeOffset < 0 || relativeOffset > first.Length)
            throw new InvalidDataException("A BIFF string offset is invalid.");
        List<byte[]> segments = new List<byte[]>
        {
            BinaryHelpers.Slice(this._stream, first.Offset + relativeOffset, first.Length - relativeOffset)
        };
        for (int i = index + 1; i < records.Count && records[i].Id == 0x003C; i++)
            segments.Add(BinaryHelpers.Slice(this._stream, records[i].Offset, records[i].Length));
        return segments;
    }

    private string ReadRecordString(List<BiffRecord> records, int index, int relativeOffset, bool shortCount)
    {
        return new SegmentedBiffStringReader(CollectSegments(records, index, relativeOffset), 0)
            .ReadRichString(shortCount);
    }

    private List<string> ReadSharedStrings(List<BiffRecord> records, int index)
    {
        List<byte[]> segments = CollectSegments(records, index, 0);
        if (segments[0].Length < 8)
            throw new InvalidDataException("The BIFF shared string table is truncated.");
        uint unique = BinaryHelpers.U32(segments[0], 4);
        if (unique > 1000000)
            throw new ConnectorException(HttpStatusCode.RequestEntityTooLarge, "TOO_MANY_SHARED_STRINGS", "The shared string table is too large to convert safely.");
        SegmentedBiffStringReader reader = new SegmentedBiffStringReader(segments, 8);
        List<string> values = new List<string>((int)unique);
        for (uint i = 0; i < unique; i++)
        {
            this._guard();
            values.Add(reader.ReadRichString(false));
        }
        return values;
    }

    private DateStyleKind StyleForXf(int xf)
    {
        int formatId = xf >= 0 && xf < this._xfFormats.Count ? this._xfFormats[xf] : 0;
        string custom;
        this._customFormats.TryGetValue(formatId, out custom);
        return BinaryHelpers.DateStyleForFormat(formatId, custom);
    }

    private string SharedString(uint index)
    {
        if (index >= this._sharedStrings.Count)
            throw new InvalidDataException("A BIFF cell refers to a shared-string index outside the SST.");
        return this._sharedStrings[(int)index];
    }

    private void ReadSheet(int start, WorkbookData workbook, SheetData sheet)
    {
        List<BiffRecord> records = ReadRecords(start);
        if (records.Count == 0 || records[0].Id != 0x0809)
            throw new InvalidDataException("A BIFF worksheet substream does not begin with BOF.");

        for (int i = 0; i < records.Count; i++)
        {
            this._guard();
            BiffRecord record = records[i];
            int o = record.Offset;
            switch (record.Id)
            {
                case 0x027E:
                    RequireRecord(record, 10);
                    AddNumber(workbook, sheet, BinaryHelpers.U16(this._stream, o), BinaryHelpers.U16(this._stream, o + 2), BinaryHelpers.DecodeRk(BinaryHelpers.U32(this._stream, o + 6)), BinaryHelpers.U16(this._stream, o + 4));
                    break;
                case 0x00BD:
                    RequireRecord(record, 12);
                    int rkCount = (record.Length - 6) / 6;
                    int rkRow = BinaryHelpers.U16(this._stream, o);
                    int firstRkColumn = BinaryHelpers.U16(this._stream, o + 2);
                    for (int n = 0; n < rkCount; n++)
                    {
                        int pair = o + 4 + n * 6;
                        AddNumber(workbook, sheet, rkRow, firstRkColumn + n, BinaryHelpers.DecodeRk(BinaryHelpers.U32(this._stream, pair + 2)), BinaryHelpers.U16(this._stream, pair));
                    }
                    break;
                case 0x0203:
                    RequireRecord(record, 14);
                    AddNumber(workbook, sheet, BinaryHelpers.U16(this._stream, o), BinaryHelpers.U16(this._stream, o + 2), BinaryHelpers.F64(this._stream, o + 6), BinaryHelpers.U16(this._stream, o + 4));
                    break;
                case 0x00FD:
                    RequireRecord(record, 10);
                    workbook.AddCell(sheet, BinaryHelpers.TextCell(BinaryHelpers.U16(this._stream, o), BinaryHelpers.U16(this._stream, o + 2), SharedString(BinaryHelpers.U32(this._stream, o + 6))));
                    break;
                case 0x0204:
                case 0x00D6:
                    RequireRecord(record, 9);
                    workbook.AddCell(sheet, BinaryHelpers.TextCell(BinaryHelpers.U16(this._stream, o), BinaryHelpers.U16(this._stream, o + 2), ReadRecordString(records, i, 6, false)));
                    break;
                case 0x0205:
                    RequireRecord(record, 8);
                    int boolRow = BinaryHelpers.U16(this._stream, o);
                    int boolColumn = BinaryHelpers.U16(this._stream, o + 2);
                    workbook.AddCell(sheet, this._stream[o + 7] == 1
                        ? BinaryHelpers.ErrorCell(boolRow, boolColumn, this._stream[o + 6])
                        : BinaryHelpers.BooleanCell(boolRow, boolColumn, this._stream[o + 6] != 0));
                    break;
                case 0x0201:
                    RequireRecord(record, 6);
                    workbook.AddCell(sheet, BinaryHelpers.BlankCell(BinaryHelpers.U16(this._stream, o), BinaryHelpers.U16(this._stream, o + 2)));
                    break;
                case 0x00BE:
                    RequireRecord(record, 8);
                    int blankCount = (record.Length - 6) / 2;
                    int blankRow = BinaryHelpers.U16(this._stream, o);
                    int firstBlankColumn = BinaryHelpers.U16(this._stream, o + 2);
                    for (int n = 0; n < blankCount; n++)
                        workbook.AddCell(sheet, BinaryHelpers.BlankCell(blankRow, firstBlankColumn + n));
                    break;
                case 0x0006:
                    RequireRecord(record, 20);
                    ReadFormula(records, i, workbook, sheet);
                    break;
            }
        }
    }

    private void ReadFormula(List<BiffRecord> records, int index, WorkbookData workbook, SheetData sheet)
    {
        BiffRecord record = records[index];
        int o = record.Offset;
        int row = BinaryHelpers.U16(this._stream, o);
        int column = BinaryHelpers.U16(this._stream, o + 2);
        int xf = BinaryHelpers.U16(this._stream, o + 4);
        bool special = BinaryHelpers.U16(this._stream, o + 12) == 0xFFFF;
        if (!special)
        {
            AddNumber(workbook, sheet, row, column, BinaryHelpers.F64(this._stream, o + 6), xf);
            return;
        }

        byte tag = this._stream[o + 6];
        if (tag == 0)
        {
            int next = index + 1;
            while (next < records.Count && (records[next].Id == 0x0221 || records[next].Id == 0x0236 || records[next].Id == 0x04BC || records[next].Id == 0x003C))
                next++;
            string value = next < records.Count && records[next].Id == 0x0207
                ? ReadRecordString(records, next, 0, false)
                : string.Empty;
            workbook.AddCell(sheet, BinaryHelpers.TextCell(row, column, value));
        }
        else if (tag == 1)
        {
            workbook.AddCell(sheet, BinaryHelpers.BooleanCell(row, column, this._stream[o + 8] != 0));
        }
        else if (tag == 2)
        {
            workbook.AddCell(sheet, BinaryHelpers.ErrorCell(row, column, this._stream[o + 8]));
        }
        else
        {
            workbook.AddCell(sheet, BinaryHelpers.BlankCell(row, column));
        }
    }

    private void AddNumber(WorkbookData workbook, SheetData sheet, int row, int column, double value, int xf)
    {
        workbook.AddCell(sheet, BinaryHelpers.NumberCell(row, column, value, StyleForXf(xf)));
    }

    private static void RequireRecord(BiffRecord record, int minimumLength)
    {
        if (record.Length < minimumLength)
            throw new InvalidDataException("A BIFF cell record is shorter than its required structure.");
    }
}

internal sealed class XlsbRecord
{
    public int Id;
    public int Offset;
    public int Length;
    public int Next;
}

internal sealed class XlsbReader
{
    private const long MaxExpandedWorkbookBytes = 128L * 1024L * 1024L;
    private readonly byte[] _file;
    private readonly Action _guard;
    private readonly int _maxCells;
    private readonly int _maxSheets;
    private long _expandedBytes;
    private ZipArchive _archive;
    private List<string> _sharedStrings;
    private List<int> _xfFormats;
    private Dictionary<int, string> _customFormats;

    private sealed class BundleSheet
    {
        public string Name;
        public string RelationshipId;
        public int Visibility;
    }

    private sealed class Relationship
    {
        public string Id;
        public string Type;
        public string Target;
    }

    public XlsbReader(byte[] file, Action guard, int maxCells, int maxSheets)
    {
        this._file = file;
        this._guard = guard;
        this._maxCells = maxCells;
        this._maxSheets = maxSheets;
    }

    public WorkbookData Read()
    {
        using (MemoryStream stream = new MemoryStream(this._file, false))
        using (this._archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
        {
            if (this._archive.GetEntry("xl/workbook.xml") != null && this._archive.GetEntry("xl/workbook.bin") == null)
                throw new ConnectorException(HttpStatusCode.BadRequest, "ALREADY_XLSX", "The supplied file is already an .xlsx workbook.");

            byte[] workbookPart = ReadPart("xl/workbook.bin", true);
            List<XlsbRecord> workbookRecords = ReadRecords(workbookPart);
            bool date1904 = workbookRecords.Any(record => record.Id == 0x0199
                && record.Length >= 1
                && (workbookPart[record.Offset] & 1) != 0);
            List<BundleSheet> bundles = ReadBundles(workbookPart, workbookRecords);

            List<Relationship> relationships = ReadRelationships("xl/_rels/workbook.bin.rels");
            Dictionary<string, Relationship> byId = relationships
                .Where(item => !string.IsNullOrEmpty(item.Id))
                .GroupBy(item => item.Id)
                .ToDictionary(group => group.Key, group => group.First());

            Relationship stylesRelationship = relationships.FirstOrDefault(item => EndsWithType(item.Type, "styles"));
            Relationship stringsRelationship = relationships.FirstOrDefault(item => EndsWithType(item.Type, "sharedStrings"));
            ReadStyles(stylesRelationship == null ? null : ResolvePartPath("xl/workbook.bin", stylesRelationship.Target));
            ReadSharedStrings(stringsRelationship == null ? null : ResolvePartPath("xl/workbook.bin", stringsRelationship.Target));

            WorkbookData workbook = new WorkbookData(date1904, this._maxCells, this._maxSheets);
            foreach (BundleSheet bundle in bundles)
            {
                this._guard();
                Relationship relationship;
                if (string.IsNullOrEmpty(bundle.RelationshipId) || !byId.TryGetValue(bundle.RelationshipId, out relationship))
                    throw new InvalidDataException("An .xlsb sheet relationship is missing.");
                if (!EndsWithType(relationship.Type, "worksheet"))
                    throw new ConnectorException(HttpStatusCodes.UnprocessableEntity, "UNSUPPORTED_SHEET_TYPE", "The value-only converter supports worksheets only; sheet '" + bundle.Name + "' is a chart, macro, or dialog sheet.");
                string partPath = ResolvePartPath("xl/workbook.bin", relationship.Target);
                SheetData sheet = workbook.AddSheet(bundle.Name, bundle.Visibility);
                ReadSheet(ReadPart(partPath, true), workbook, sheet);
            }

            return workbook;
        }
    }

    private byte[] ReadPart(string path, bool required)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (required) throw new InvalidDataException("A required .xlsb package part path is missing.");
            return null;
        }
        ZipArchiveEntry entry = this._archive.GetEntry(path);
        if (entry == null)
        {
            if (required) throw new InvalidDataException("The .xlsb package is missing part '" + path + "'.");
            return null;
        }
        if (entry.Length > MaxExpandedWorkbookBytes || this._expandedBytes + entry.Length > MaxExpandedWorkbookBytes)
            throw new ConnectorException(HttpStatusCode.RequestEntityTooLarge, "EXPANDED_WORKBOOK_TOO_LARGE", "The expanded .xlsb workbook exceeds the 128 MB safety limit.");

        using (Stream input = entry.Open())
        using (MemoryStream output = new MemoryStream())
        {
            byte[] buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                this._guard();
                output.Write(buffer, 0, read);
                if (output.Length > MaxExpandedWorkbookBytes)
                    throw new ConnectorException(HttpStatusCode.RequestEntityTooLarge, "EXPANDED_WORKBOOK_TOO_LARGE", "An expanded .xlsb part exceeds the safety limit.");
            }
            this._expandedBytes += output.Length;
            return output.ToArray();
        }
    }

    private List<XlsbRecord> ReadRecords(byte[] part)
    {
        List<XlsbRecord> records = new List<XlsbRecord>();
        int position = 0;
        while (position < part.Length)
        {
            this._guard();
            int start = position;
            byte first = part[position++];
            int id = first;
            if (first >= 0x80)
            {
                if (position >= part.Length) throw new InvalidDataException("An .xlsb record identifier is truncated.");
                id += part[position++] << 8;
            }

            int length = 0;
            int shift = 0;
            bool complete = false;
            for (int i = 0; i < 4; i++)
            {
                if (position >= part.Length) throw new InvalidDataException("An .xlsb record length is truncated.");
                byte current = part[position++];
                length |= (current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    complete = true;
                    break;
                }
                shift += 7;
            }
            if (!complete) throw new InvalidDataException("An .xlsb record length uses more than four bytes.");
            if (length < 0 || position > part.Length - length)
                throw new InvalidDataException("An .xlsb record is truncated.");
            records.Add(new XlsbRecord { Id = id, Offset = position, Length = length, Next = position + length });
            position += length;
            if (position <= start) throw new InvalidDataException("An .xlsb record made no forward progress.");
        }
        return records;
    }

    private static string ReadWideString(byte[] part, int offset, out int size)
    {
        uint count = BinaryHelpers.U32(part, offset);
        if (count == 0xFFFFFFFFU)
        {
            size = 4;
            return null;
        }
        if (count > 32767)
            throw new ConnectorException(HttpStatusCodes.UnprocessableEntity, "CELL_TEXT_TOO_LONG", "An .xlsb string exceeds Excel's cell-text limit.");
        long bytes = (long)count * 2L;
        if (bytes > int.MaxValue) throw new InvalidDataException("An .xlsb wide string is too large.");
        BinaryHelpers.Require(part, offset + 4, (int)bytes);
        size = 4 + (int)bytes;
        return Encoding.Unicode.GetString(part, offset + 4, (int)bytes);
    }

    private List<BundleSheet> ReadBundles(byte[] workbookPart, List<XlsbRecord> records)
    {
        List<BundleSheet> result = new List<BundleSheet>();
        foreach (XlsbRecord record in records.Where(item => item.Id == 0x019C))
        {
            if (record.Length < 16) throw new InvalidDataException("An .xlsb BrtBundleSh record is truncated.");
            int relSize;
            string relId = ReadWideString(workbookPart, record.Offset + 8, out relSize);
            int nameSize;
            string name = ReadWideString(workbookPart, record.Offset + 8 + relSize, out nameSize);
            result.Add(new BundleSheet
            {
                Name = name,
                RelationshipId = relId,
                Visibility = (int)Math.Min(2U, BinaryHelpers.U32(workbookPart, record.Offset))
            });
        }
        if (result.Count == 0) throw new InvalidDataException("The .xlsb workbook lists no sheets.");
        return result;
    }

    private List<Relationship> ReadRelationships(string path)
    {
        byte[] xml = ReadPart(path, true);
        try
        {
            XDocument document;
            using (MemoryStream stream = new MemoryStream(xml, false))
                document = XDocument.Load(stream, LoadOptions.None);
            XNamespace ns = "http://schemas.openxmlformats.org/package/2006/relationships";
            return document.Root.Elements(ns + "Relationship")
                .Select(element => new Relationship
                {
                    Id = (string)element.Attribute("Id"),
                    Type = (string)element.Attribute("Type"),
                    Target = (string)element.Attribute("Target")
                })
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The .xlsb workbook relationships XML is invalid: " + ex.Message);
        }
    }

    private void ReadSharedStrings(string path)
    {
        this._sharedStrings = new List<string>();
        if (string.IsNullOrEmpty(path)) return;
        byte[] part = ReadPart(path, false);
        if (part == null) return;
        foreach (XlsbRecord record in ReadRecords(part).Where(item => item.Id == 0x0013))
        {
            this._guard();
            if (record.Length < 5) throw new InvalidDataException("An .xlsb shared-string item is truncated.");
            int size;
            this._sharedStrings.Add(ReadWideString(part, record.Offset + 1, out size));
        }
    }

    private void ReadStyles(string path)
    {
        this._customFormats = new Dictionary<int, string>();
        this._xfFormats = new List<int>();
        if (string.IsNullOrEmpty(path)) return;
        byte[] part = ReadPart(path, false);
        if (part == null) return;
        bool inCellXfs = false;
        foreach (XlsbRecord record in ReadRecords(part))
        {
            if (record.Id == 0x04E9)
            {
                inCellXfs = true;
            }
            else if (record.Id == 0x04EA)
            {
                inCellXfs = false;
            }
            else if (record.Id == 0x002C && record.Length >= 6)
            {
                int size;
                this._customFormats[BinaryHelpers.U16(part, record.Offset)] = ReadWideString(part, record.Offset + 2, out size);
            }
            else if (record.Id == 0x002F && inCellXfs && record.Length >= 4)
            {
                this._xfFormats.Add(BinaryHelpers.U16(part, record.Offset + 2));
            }
        }
    }

    private DateStyleKind StyleForIndex(uint style)
    {
        int styleIndex = (int)(style & 0x00FFFFFFU);
        int formatId = styleIndex >= 0 && styleIndex < this._xfFormats.Count ? this._xfFormats[styleIndex] : 0;
        string custom;
        this._customFormats.TryGetValue(formatId, out custom);
        return BinaryHelpers.DateStyleForFormat(formatId, custom);
    }

    private string SharedString(uint index)
    {
        if (index >= this._sharedStrings.Count)
            throw new InvalidDataException("An .xlsb cell refers to a shared-string index outside the SST.");
        return this._sharedStrings[(int)index];
    }

    private void ReadSheet(byte[] part, WorkbookData workbook, SheetData sheet)
    {
        int currentRow = -1;
        foreach (XlsbRecord record in ReadRecords(part))
        {
            this._guard();
            if (record.Id == 0x0000)
            {
                if (record.Length < 4) throw new InvalidDataException("An .xlsb row header is truncated.");
                currentRow = checked((int)BinaryHelpers.U32(part, record.Offset));
                continue;
            }
            if (record.Id < 0x01 || record.Id > 0x0B) continue;
            if (currentRow < 0) throw new InvalidDataException("An .xlsb cell appears before its row header.");
            if (record.Length < 8) throw new InvalidDataException("An .xlsb cell record is truncated.");
            int column = checked((int)BinaryHelpers.U32(part, record.Offset));
            uint style = BinaryHelpers.U32(part, record.Offset + 4);
            int valueOffset = record.Offset + 8;
            CellData cell;
            switch (record.Id)
            {
                case 0x01:
                    cell = BinaryHelpers.BlankCell(currentRow, column);
                    break;
                case 0x02:
                    BinaryHelpers.Require(part, valueOffset, 4);
                    cell = BinaryHelpers.NumberCell(currentRow, column, BinaryHelpers.DecodeRk(BinaryHelpers.U32(part, valueOffset)), StyleForIndex(style));
                    break;
                case 0x03:
                    BinaryHelpers.Require(part, valueOffset, 1);
                    cell = BinaryHelpers.ErrorCell(currentRow, column, part[valueOffset]);
                    break;
                case 0x04:
                    BinaryHelpers.Require(part, valueOffset, 1);
                    cell = BinaryHelpers.BooleanCell(currentRow, column, part[valueOffset] != 0);
                    break;
                case 0x05:
                case 0x09:
                    BinaryHelpers.Require(part, valueOffset, 8);
                    cell = BinaryHelpers.NumberCell(currentRow, column, BinaryHelpers.F64(part, valueOffset), StyleForIndex(style));
                    break;
                case 0x06:
                case 0x08:
                    int stringSize;
                    cell = BinaryHelpers.TextCell(currentRow, column, ReadWideString(part, valueOffset, out stringSize));
                    break;
                case 0x07:
                    BinaryHelpers.Require(part, valueOffset, 4);
                    cell = BinaryHelpers.TextCell(currentRow, column, SharedString(BinaryHelpers.U32(part, valueOffset)));
                    break;
                case 0x0A:
                    BinaryHelpers.Require(part, valueOffset, 1);
                    cell = BinaryHelpers.BooleanCell(currentRow, column, part[valueOffset] != 0);
                    break;
                default:
                    BinaryHelpers.Require(part, valueOffset, 1);
                    cell = BinaryHelpers.ErrorCell(currentRow, column, part[valueOffset]);
                    break;
            }
            workbook.AddCell(sheet, cell);
        }
    }

    private static bool EndsWithType(string relationshipType, string suffix)
    {
        return !string.IsNullOrEmpty(relationshipType)
            && relationshipType.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePartPath(string sourcePart, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidDataException("An .xlsb relationship has no target.");
        string combined;
        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            combined = target.Substring(1);
        }
        else
        {
            int slash = sourcePart.LastIndexOf('/');
            string folder = slash < 0 ? string.Empty : sourcePart.Substring(0, slash + 1);
            combined = folder + target;
        }

        List<string> segments = new List<string>();
        foreach (string segment in combined.Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count == 0) throw new InvalidDataException("An .xlsb relationship escapes the package root.");
                segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }
        return string.Join("/", segments);
    }
}

internal static class XlsxWriter
{
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

    public static byte[] Write(WorkbookData workbook, Action guard)
    {
        if (workbook.Sheets.Count == 0)
            throw new InvalidDataException("The source workbook has no worksheets to write.");

        using (MemoryStream output = new MemoryStream())
        {
            using (ZipArchive archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                WriteEntry(archive, "[Content_Types].xml", BuildContentTypes(workbook.Sheets.Count));
                WriteEntry(archive, "_rels/.rels", RootRelationships());
                WriteEntry(archive, "xl/workbook.xml", BuildWorkbook(workbook));
                WriteEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationships(workbook.Sheets.Count));
                WriteEntry(archive, "xl/styles.xml", StylesXml());

                for (int i = 0; i < workbook.Sheets.Count; i++)
                {
                    guard();
                    WriteWorksheet(archive, "xl/worksheets/sheet" + (i + 1) + ".xml", workbook.Sheets[i], guard);
                }
            }
            return output.ToArray();
        }
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom, 4096, false))
            writer.Write(content);
    }

    private static void WriteWorksheet(ZipArchive archive, string path, SheetData sheet, Action guard)
    {
        List<CellData> cells = sheet.Cells
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToList();
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using (Stream stream = entry.Open())
        using (StreamWriter writer = new StreamWriter(stream, Utf8NoBom, 16384, false))
        {
            writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"");
            writer.Write(SpreadsheetNamespace);
            writer.Write("\">");
            if (cells.Count > 0)
            {
                string first = CellReference(cells.Min(cell => cell.Row), cells.Min(cell => cell.Column));
                string last = CellReference(cells.Max(cell => cell.Row), cells.Max(cell => cell.Column));
                writer.Write("<dimension ref=\"");
                writer.Write(first == last ? first : first + ":" + last);
                writer.Write("\"/>");
            }
            writer.Write("<sheetData>");

            int currentRow = -1;
            foreach (CellData cell in cells)
            {
                if ((cell.Column & 1023) == 0) guard();
                if (cell.Row != currentRow)
                {
                    if (currentRow >= 0) writer.Write("</row>");
                    currentRow = cell.Row;
                    writer.Write("<row r=\"");
                    writer.Write(currentRow + 1);
                    writer.Write("\">");
                }
                WriteCell(writer, cell);
            }
            if (currentRow >= 0) writer.Write("</row>");
            writer.Write("</sheetData></worksheet>");
        }
    }

    private static void WriteCell(StreamWriter writer, CellData cell)
    {
        writer.Write("<c r=\"");
        writer.Write(CellReference(cell.Row, cell.Column));
        writer.Write("\"");
        if (cell.DateStyle != DateStyleKind.General)
        {
            writer.Write(" s=\"");
            writer.Write((int)cell.DateStyle);
            writer.Write("\"");
        }

        switch (cell.Kind)
        {
            case CellValueKind.Blank:
                writer.Write("/>");
                return;
            case CellValueKind.Text:
                writer.Write(" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                writer.Write(EscapeCellText(cell.TextValue));
                writer.Write("</t></is></c>");
                return;
            case CellValueKind.Boolean:
                writer.Write(" t=\"b\"><v>");
                writer.Write(cell.BooleanValue ? "1" : "0");
                writer.Write("</v></c>");
                return;
            case CellValueKind.Error:
                writer.Write(" t=\"e\"><v>");
                writer.Write(EscapeXml(cell.TextValue, false));
                writer.Write("</v></c>");
                return;
            default:
                writer.Write("><v>");
                writer.Write(XmlConvert.ToString(cell.NumberValue));
                writer.Write("</v></c>");
                return;
        }
    }

    private static string BuildContentTypes(int sheetCount)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (int i = 1; i <= sheetCount; i++)
            builder.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        builder.Append("</Types>");
        return builder.ToString();
    }

    private static string RootRelationships()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";
    }

    private static string BuildWorkbook(WorkbookData workbook)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"").Append(SpreadsheetNamespace).Append("\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><workbookPr date1904=\"").Append(workbook.Date1904 ? "1" : "0").Append("\"/><sheets>");
        for (int i = 0; i < workbook.Sheets.Count; i++)
        {
            SheetData sheet = workbook.Sheets[i];
            builder.Append("<sheet name=\"").Append(EscapeXml(sheet.Name, true)).Append("\" sheetId=\"").Append(i + 1).Append("\"");
            if (sheet.Visibility == 1) builder.Append(" state=\"hidden\"");
            else if (sheet.Visibility == 2) builder.Append(" state=\"veryHidden\"");
            builder.Append(" r:id=\"rId").Append(i + 1).Append("\"/>");
        }
        builder.Append("</sheets></workbook>");
        return builder.ToString();
    }

    private static string BuildWorkbookRelationships(int sheetCount)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (int i = 1; i <= sheetCount; i++)
            builder.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");
        builder.Append("<Relationship Id=\"rId").Append(sheetCount + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        return builder.ToString();
    }

    private static string StylesXml()
    {
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><numFmts count=\"3\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd\"/><numFmt numFmtId=\"165\" formatCode=\"yyyy-mm-dd hh:mm:ss\"/><numFmt numFmtId=\"166\" formatCode=\"hh:mm:ss\"/></numFmts><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/><family val=\"2\"/><scheme val=\"minor\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"4\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"166\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
    }

    private static string CellReference(int row, int column)
    {
        int value = column + 1;
        StringBuilder letters = new StringBuilder();
        while (value > 0)
        {
            int remainder = (value - 1) % 26;
            letters.Insert(0, (char)('A' + remainder));
            value = (value - 1) / 26;
        }
        return letters.ToString() + (row + 1);
    }

    private static string EscapeCellText(string value)
    {
        if (value == null) return string.Empty;
        StringBuilder encoded = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char ch = value[i];
            if (ch == '_' && LooksLikeEscapedSequence(value, i))
            {
                encoded.Append("_x005F_");
                continue;
            }
            if ((ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') || ch == 0xFFFE || ch == 0xFFFF)
                encoded.Append("_x").Append(((int)ch).ToString("X4")).Append('_');
            else if (ch == '\r')
                encoded.Append("&#xD;");
            else
                encoded.Append(EscapeXml(ch.ToString(), false));
        }
        return encoded.ToString();
    }

    private static bool LooksLikeEscapedSequence(string value, int index)
    {
        if (index + 6 >= value.Length || (value[index + 1] != 'x' && value[index + 1] != 'X') || value[index + 6] != '_')
            return false;
        for (int i = index + 2; i < index + 6; i++)
        {
            char ch = value[i];
            bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
            if (!hex) return false;
        }
        return true;
    }

    private static string EscapeXml(string value, bool attribute)
    {
        if (value == null) return string.Empty;
        string result = value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        if (attribute)
            result = result.Replace("\"", "&quot;").Replace("'", "&apos;");
        return result;
    }
}
