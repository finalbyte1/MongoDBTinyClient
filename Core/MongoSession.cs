using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDBTinyClient.Core;

internal sealed class CollectionInfo
{
    public required string Name { get; init; }
    public required string Type { get; init; } // "collection", "view", "timeseries"
    public bool IsView => Type == "view";
}

internal sealed class IndexInfo
{
    public required string Name { get; init; }
    public required string Keys { get; init; }
    public required string Options { get; init; }
    public bool IsIdIndex => Name == "_id_";
}

internal sealed class FindResult
{
    public required List<BsonDocument> Documents { get; init; }
    public long Total { get; init; }
}

/// <summary>A connection to one MongoDB deployment: a thin wrapper around the official driver.</summary>
internal sealed class MongoSession : IDisposable
{
    private readonly MongoClient _client;

    public string ConnectionString { get; }
    public string DisplayName { get; }

    /// <summary>Database named in the connection string, if any (useful when the user cannot list databases).</summary>
    public string? DefaultDatabase { get; }

    private MongoSession(string connectionString, MongoClient client, MongoUrl url)
    {
        ConnectionString = connectionString;
        _client = client;
        DefaultDatabase = url.DatabaseName;
        DisplayName = string.Join(", ", url.Servers.Select(s => s.ToString()));
        if (string.IsNullOrEmpty(DisplayName)) DisplayName = BsonFormat.MaskConnectionString(connectionString);
    }

    public static async Task<MongoSession> ConnectAsync(string connectionString, CancellationToken ct = default)
    {
        var url = MongoUrl.Create(connectionString);
        var settings = MongoClientSettings.FromUrl(url);
        if (url.ServerSelectionTimeout == MongoDefaults.ServerSelectionTimeout)
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
        if (url.ConnectTimeout == MongoDefaults.ConnectTimeout)
            settings.ConnectTimeout = TimeSpan.FromSeconds(8);
        settings.ApplicationName = "MongoDB Tiny Client";

        var client = new MongoClient(settings);
        // Ping a database we are surely allowed to talk to, so that errors show up now
        var probeDb = client.GetDatabase(url.DatabaseName ?? "admin");
        await probeDb.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
        return new MongoSession(connectionString, client, url);
    }

    public void Dispose() => _client.Dispose();

    public IMongoDatabase Database(string name) => _client.GetDatabase(name);

    public IMongoCollection<BsonDocument> Collection(string db, string collection) =>
        _client.GetDatabase(db).GetCollection<BsonDocument>(collection);

    // ============================================================ databases

    public async Task<List<string>> ListDatabasesAsync()
    {
        try
        {
            using var cursor = await _client.ListDatabaseNamesAsync();
            var names = await cursor.ToListAsync();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (MongoCommandException) when (DefaultDatabase != null)
        {
            // Users with access to a single database are often not allowed to run listDatabases
            return new List<string> { DefaultDatabase };
        }
    }

    public Task DropDatabaseAsync(string name) => _client.DropDatabaseAsync(name);

    public async Task<BsonDocument> DatabaseStatsAsync(string db) =>
        await Database(db).RunCommandAsync<BsonDocument>(new BsonDocument("dbStats", 1));

    public async Task<BsonDocument> ServerStatusAsync() =>
        await Database("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1));

    // ============================================================ collections

    public async Task<List<CollectionInfo>> ListCollectionsAsync(string db)
    {
        using var cursor = await Database(db).ListCollectionsAsync();
        var docs = await cursor.ToListAsync();
        return docs
            .Select(d => new CollectionInfo
            {
                Name = d["name"].AsString,
                Type = d.GetValue("type", "collection").AsString
            })
            .Where(c => !c.Name.StartsWith("system.", StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task CreateCollectionAsync(string db, string name) => Database(db).CreateCollectionAsync(name);

    public Task DropCollectionAsync(string db, string name) => Database(db).DropCollectionAsync(name);

    public Task RenameCollectionAsync(string db, string oldName, string newName) =>
        Database(db).RenameCollectionAsync(oldName, newName);

    public Task<long> EstimatedCountAsync(string db, string collection) =>
        Collection(db, collection).EstimatedDocumentCountAsync();

    // ============================================================ documents

    public async Task<FindResult> FindAsync(string db, string collection, BsonDocument filter,
        BsonDocument? sort, BsonDocument? projection, int skip, int limit)
    {
        var coll = Collection(db, collection);
        var options = new FindOptions<BsonDocument>
        {
            Skip = skip,
            Limit = limit,
            Sort = sort is { ElementCount: > 0 } ? sort : null,
            Projection = projection is { ElementCount: > 0 } ? projection : null,
            MaxTime = TimeSpan.FromSeconds(60)
        };

        var findTask = coll.FindAsync(filter, options);
        // A count with an empty filter can use the much cheaper collection metadata
        var countTask = filter.ElementCount == 0
            ? coll.EstimatedDocumentCountAsync()
            : coll.CountDocumentsAsync(filter, new CountOptions { MaxTime = TimeSpan.FromSeconds(30) });

        using var cursor = await findTask;
        var docs = await cursor.ToListAsync();
        long total;
        try { total = await countTask; }
        catch (MongoException) { total = -1; } // count timed out or not allowed: show "?"
        return new FindResult { Documents = docs, Total = total };
    }

    /// <summary>Streams every document matching the filter (for exports).</summary>
    public async Task<List<BsonDocument>> FindAllAsync(string db, string collection, BsonDocument filter,
        BsonDocument? sort, BsonDocument? projection)
    {
        var options = new FindOptions<BsonDocument>
        {
            Sort = sort is { ElementCount: > 0 } ? sort : null,
            Projection = projection is { ElementCount: > 0 } ? projection : null
        };
        using var cursor = await Collection(db, collection).FindAsync(filter, options);
        return await cursor.ToListAsync();
    }

    public Task InsertAsync(string db, string collection, BsonDocument doc) =>
        Collection(db, collection).InsertOneAsync(doc);

    /// <summary>Unordered insert: documents that fail (e.g. duplicate _id) do not stop the others.</summary>
    public async Task<(long Inserted, int Failed, string? FirstError)> InsertManyAsync(
        string db, string collection, IReadOnlyList<BsonDocument> docs)
    {
        if (docs.Count == 0) return (0, 0, null);
        try
        {
            await Collection(db, collection).InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false });
            return (docs.Count, 0, null);
        }
        catch (MongoBulkWriteException<BsonDocument> ex)
        {
            return (ex.Result.InsertedCount, ex.WriteErrors.Count, ex.WriteErrors.FirstOrDefault()?.Message);
        }
    }

    public async Task ReplaceAsync(string db, string collection, BsonValue id, BsonDocument doc)
    {
        var result = await Collection(db, collection).ReplaceOneAsync(new BsonDocument("_id", id), doc);
        if (result.MatchedCount == 0)
            throw new InvalidOperationException("The document no longer exists (deleted by someone else?).");
    }

    public async Task SetFieldAsync(string db, string collection, BsonValue id, string field, BsonValue value)
    {
        var update = new BsonDocument("$set", new BsonDocument(field, value));
        var result = await Collection(db, collection).UpdateOneAsync(new BsonDocument("_id", id), update);
        if (result.MatchedCount == 0)
            throw new InvalidOperationException("The document no longer exists (deleted by someone else?).");
    }

    public async Task<long> DeleteByIdsAsync(string db, string collection, IEnumerable<BsonValue> ids)
    {
        var filter = new BsonDocument("_id", new BsonDocument("$in", new BsonArray(ids)));
        var result = await Collection(db, collection).DeleteManyAsync(filter);
        return result.DeletedCount;
    }

    public async Task<long> DeleteManyAsync(string db, string collection, BsonDocument filter)
    {
        var result = await Collection(db, collection).DeleteManyAsync(filter);
        return result.DeletedCount;
    }

    // ============================================================ indexes

    public async Task<List<IndexInfo>> ListIndexesAsync(string db, string collection)
    {
        using var cursor = await Collection(db, collection).Indexes.ListAsync();
        var docs = await cursor.ToListAsync();
        return docs.Select(d =>
        {
            var options = new BsonDocument(d.Where(e => e.Name is not ("v" or "key" or "name" or "ns")));
            return new IndexInfo
            {
                Name = d["name"].AsString,
                Keys = BsonFormat.ToCompactJson(d["key"]),
                Options = options.ElementCount == 0 ? string.Empty : BsonFormat.ToCompactJson(options)
            };
        }).ToList();
    }

    public Task<string> CreateIndexAsync(string db, string collection, BsonDocument keys, string? name, bool unique)
    {
        var options = new CreateIndexOptions { Unique = unique ? true : null, Name = string.IsNullOrWhiteSpace(name) ? null : name };
        var model = new CreateIndexModel<BsonDocument>(keys, options);
        return Collection(db, collection).Indexes.CreateOneAsync(model);
    }

    public Task DropIndexAsync(string db, string collection, string name) =>
        Collection(db, collection).Indexes.DropOneAsync(name);

    // ============================================================ queries

    public async Task<List<BsonDocument>> AggregateAsync(string db, string collection, List<BsonDocument> pipeline, int maxResults)
    {
        var definition = PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline);
        using var cursor = await Collection(db, collection).AggregateAsync(definition,
            new AggregateOptions { AllowDiskUse = true, MaxTime = TimeSpan.FromMinutes(5) });
        var results = new List<BsonDocument>();
        while (await cursor.MoveNextAsync())
        {
            foreach (var doc in cursor.Current)
            {
                results.Add(doc);
                if (results.Count >= maxResults) return results;
            }
        }
        return results;
    }

    public async Task<BsonDocument> RunCommandAsync(string db, BsonDocument command) =>
        await Database(db).RunCommandAsync<BsonDocument>(new BsonDocumentCommand<BsonDocument>(command));
}
