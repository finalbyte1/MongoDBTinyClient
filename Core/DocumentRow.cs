using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MongoDB.Bson;

namespace MongoDBTinyClient.Core;

/// <summary>A document shown as a grid row: one text cell per top-level field.</summary>
internal sealed class DocumentRow
{
    public DocumentRow(BsonDocument document, IReadOnlyList<string> columns, int number)
    {
        Document = document;
        Number = number;
        Cells = new ObservableCollection<string>(
            columns.Select(c => BsonFormat.ToCellText(document.TryGetValue(c, out var v) ? v : null)));
    }

    public BsonDocument Document { get; }

    /// <summary>1-based position across pages, shown in the "#" column.</summary>
    public int Number { get; }

    /// <summary>Observable, so bindings on Cells[i] refresh when a value changes, and can write back.</summary>
    public ObservableCollection<string> Cells { get; }

    public BsonValue? Id => Document.TryGetValue("_id", out var id) ? id : null;

    public void SetField(string field, BsonValue value, int column)
    {
        Document[field] = value;
        if (column >= 0 && column < Cells.Count) Cells[column] = BsonFormat.ToCellText(value);
    }

    /// <summary>Union of the top-level fields, in order of first appearance, with _id first.</summary>
    public static List<string> CollectColumns(IEnumerable<BsonDocument> documents)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var columns = new List<string>();
        foreach (var doc in documents)
            foreach (var element in doc)
                if (seen.Add(element.Name))
                    columns.Add(element.Name);

        if (columns.Remove("_id")) columns.Insert(0, "_id");
        return columns;
    }
}
