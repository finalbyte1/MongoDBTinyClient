using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

namespace MongoDBTinyClient.Core;

/// <summary>Conversions between BSON values and the text the user sees and types.</summary>
internal static class BsonFormat
{
    /// <summary>Shell syntax (ObjectId("..."), ISODate("...")) is the easiest to read and edit by hand.</summary>
    private static readonly JsonWriterSettings ShellCompact = new() { OutputMode = JsonOutputMode.Shell };

    /// <summary>Relaxed Extended JSON is the standard format for exported files (mongoimport friendly).</summary>
    private static readonly JsonWriterSettings RelaxedIndented = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson, Indent = true };
    private static readonly JsonWriterSettings RelaxedCompact = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

    private const int MaxCellLength = 200;

    public static string ToEditableJson(BsonValue value)
    {
        var sb = new StringBuilder();
        WriteShell(sb, value, 0);
        return sb.ToString();
    }

    public static string ToCompactJson(BsonValue value)
    {
        var sb = new StringBuilder();
        WriteShell(sb, value, -1);
        return sb.ToString();
    }

    /// <summary>
    /// Shell-syntax JSON writer. The driver's writer prints doubles with 17 digits (498.07999999999998);
    /// here they get the shortest form that round-trips (498.08). indent &lt; 0 = single line.
    /// </summary>
    private static void WriteShell(StringBuilder sb, BsonValue value, int indent)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                var doc = value.AsBsonDocument;
                if (doc.ElementCount == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                int child = indent < 0 ? -1 : indent + 1;
                bool firstElement = true;
                foreach (var element in doc)
                {
                    sb.Append(firstElement ? "" : ",");
                    NewLine(sb, child);
                    sb.Append(new BsonString(element.Name).ToJson(ShellCompact)).Append(": ");
                    WriteShell(sb, element.Value, child);
                    firstElement = false;
                }
                NewLine(sb, indent);
                sb.Append('}');
                return;

            case BsonType.Array:
                var array = value.AsBsonArray;
                if (array.Count == 0) { sb.Append("[]"); return; }
                // Short arrays of plain values stay on one line: [1, 2, 3]
                bool inline = indent < 0 || (array.All(v => !v.IsBsonDocument && !v.IsBsonArray) && array.Count <= 12);
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(inline ? ", " : ",");
                    if (!inline) NewLine(sb, indent + 1);
                    WriteShell(sb, array[i], inline ? -1 : indent + 1);
                }
                if (!inline) NewLine(sb, indent);
                sb.Append(']');
                return;

            case BsonType.Double when double.IsFinite(value.AsDouble):
                var text = value.AsDouble.ToString("R", CultureInfo.InvariantCulture);
                // Keep a decimal point so that the value is read back as a double, not as an integer
                sb.Append(text.IndexOfAny(new[] { '.', 'E', 'e' }) < 0 ? text + ".0" : text);
                return;

            default:
                sb.Append(value.ToJson(ShellCompact));
                return;
        }
    }

    private static void NewLine(StringBuilder sb, int indent)
    {
        if (indent < 0) sb.Append(' ');
        else sb.Append('\n').Append(' ', indent * 2);
    }

    /// <summary>Short text for a grid cell.</summary>
    public static string ToCellText(BsonValue? value)
    {
        if (value == null) return string.Empty;
        string text = value.BsonType switch
        {
            BsonType.String => value.AsString,
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
            BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture),
            BsonType.Double => value.AsDouble.ToString("R", CultureInfo.InvariantCulture),
            BsonType.Decimal128 => value.AsDecimal128.ToString(),
            BsonType.Boolean => value.AsBoolean ? "true" : "false",
            BsonType.DateTime => FormatDate(value.ToUniversalTime()),
            BsonType.Null => "null",
            BsonType.Document => ToCompactJson(value),
            BsonType.Array => ToCompactJson(value),
            _ => ToCompactJson(value)
        };
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > MaxCellLength ? text[..MaxCellLength] + "…" : text;
    }

    /// <summary>Values that can be edited directly in a grid cell (not documents, arrays or binary).</summary>
    public static bool IsInlineEditable(BsonValue? value) => value == null || value.BsonType switch
    {
        BsonType.String or BsonType.Int32 or BsonType.Int64 or BsonType.Double or BsonType.Decimal128
            or BsonType.Boolean or BsonType.DateTime or BsonType.Null or BsonType.ObjectId => true,
        _ => false
    };

    /// <summary>
    /// Converts the text typed in a cell back to BSON, keeping the original type when possible.
    /// For a missing field (or null) the type is guessed from the text.
    /// </summary>
    public static BsonValue ParseCell(string text, BsonValue? original)
    {
        text = text.Trim();
        var type = original?.BsonType ?? BsonType.Null;

        switch (type)
        {
            case BsonType.String:
                return new BsonString(text);
            case BsonType.Int32:
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return new BsonInt32(i);
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long big)) return new BsonInt64(big);
                throw new FormatException($"'{text}' is not an integer.");
            case BsonType.Int64:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return new BsonInt64(l);
                throw new FormatException($"'{text}' is not an integer.");
            case BsonType.Double:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return new BsonDouble(d);
                throw new FormatException($"'{text}' is not a number (use '.' as decimal separator).");
            case BsonType.Decimal128:
                if (Decimal128.TryParse(text, out var dec)) return new BsonDecimal128(dec);
                throw new FormatException($"'{text}' is not a decimal number.");
            case BsonType.Boolean:
                if (bool.TryParse(text, out bool b)) return BsonBoolean.Create(b);
                throw new FormatException("Type true or false.");
            case BsonType.DateTime:
                if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                    return new BsonDateTime(dt);
                throw new FormatException($"'{text}' is not a date (e.g. 2024-05-31T12:00:00Z).");
            case BsonType.ObjectId:
                if (ObjectId.TryParse(text, out var oid)) return new BsonObjectId(oid);
                throw new FormatException($"'{text}' is not a valid ObjectId (24 hex characters).");
            default:
                return GuessValue(text);
        }
    }

    /// <summary>Accepts any JSON / shell literal (42, true, "x", ISODate(...), {...}); anything else is a string.</summary>
    public static BsonValue GuessValue(string text)
    {
        if (text.Length == 0) return BsonNull.Value;
        try
        {
            return BsonDocument.Parse("{\"v\":" + text + "}")["v"];
        }
        catch
        {
            return new BsonString(text);
        }
    }

    public static BsonDocument ParseDocument(string text, string what)
    {
        text = text.Trim();
        if (text.Length == 0) return new BsonDocument();
        try
        {
            return BsonDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new FormatException($"{what} is not a valid JSON document: {ex.Message}", ex);
        }
    }

    /// <summary>Parses an aggregation pipeline: a JSON array of stages, or a single stage.</summary>
    public static List<BsonDocument> ParsePipeline(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return new List<BsonDocument>();
        try
        {
            if (text.StartsWith('['))
            {
                var array = BsonSerializer.Deserialize<BsonArray>(text);
                return array.Select(v => v.AsBsonDocument).ToList();
            }
            return new List<BsonDocument> { BsonDocument.Parse(text) };
        }
        catch (Exception ex)
        {
            throw new FormatException("The pipeline must be a JSON array of stages: " + ex.Message, ex);
        }
    }

    /// <summary>Reads a JSON array of documents, a single document, or one document per line (JSON Lines).</summary>
    public static List<BsonDocument> ParseImportFile(string text)
    {
        text = text.Trim().TrimStart('﻿');
        if (text.Length == 0) return new List<BsonDocument>();
        if (text.StartsWith('['))
            return BsonSerializer.Deserialize<BsonArray>(text).Select(v => v.AsBsonDocument).ToList();

        try
        {
            return new List<BsonDocument> { BsonDocument.Parse(text) };
        }
        catch
        {
            var docs = new List<BsonDocument>();
            using var reader = new StringReader(text);
            string? line;
            int lineNumber = 0;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { docs.Add(BsonDocument.Parse(line)); }
                catch (Exception ex) { throw new FormatException($"Line {lineNumber}: {ex.Message}", ex); }
            }
            return docs;
        }
    }

    public static void WriteJsonArray(TextWriter writer, IEnumerable<BsonDocument> documents)
    {
        writer.Write('[');
        bool first = true;
        foreach (var doc in documents)
        {
            writer.Write(first ? "\n" : ",\n");
            writer.Write(doc.ToJson(RelaxedIndented));
            first = false;
        }
        writer.Write(first ? "]\n" : "\n]\n");
    }

    public static void WriteJsonLines(TextWriter writer, IEnumerable<BsonDocument> documents)
    {
        foreach (var doc in documents)
            writer.WriteLine(doc.ToJson(RelaxedCompact));
    }

    public static void WriteCsv(TextWriter writer, IReadOnlyList<string> columns, IEnumerable<BsonDocument> documents)
    {
        writer.WriteLine(string.Join(",", columns.Select(CsvEscape)));
        foreach (var doc in documents)
        {
            var cells = columns.Select(c => doc.TryGetValue(c, out var v) ? CsvValue(v) : string.Empty);
            writer.WriteLine(string.Join(",", cells.Select(CsvEscape)));
        }
    }

    private static string CsvValue(BsonValue v) => v.BsonType switch
    {
        BsonType.Document or BsonType.Array => v.ToJson(RelaxedCompact),
        BsonType.Null => string.Empty,
        _ => ToCellText(v)
    };

    private static string CsvEscape(string s) =>
        s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string FormatDate(DateTime utc) =>
        utc.TimeOfDay == TimeSpan.Zero
            ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture);

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1) { bytes /= 1024; u++; }
        return bytes.ToString(u == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    /// <summary>Hides the password of a connection string (mongodb://user:****@host).</summary>
    public static string MaskConnectionString(string connectionString)
    {
        int scheme = connectionString.IndexOf("://", StringComparison.Ordinal);
        int at = connectionString.LastIndexOf('@');
        if (scheme < 0 || at < scheme) return connectionString;
        int colon = connectionString.IndexOf(':', scheme + 3);
        if (colon < 0 || colon > at) return connectionString;
        var sb = new StringBuilder();
        sb.Append(connectionString, 0, colon + 1).Append("****").Append(connectionString, at, connectionString.Length - at);
        return sb.ToString();
    }
}
