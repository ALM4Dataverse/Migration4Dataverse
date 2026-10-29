using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Migration4Dataverse.Engine.Scheduling;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Migration4Dataverse.Engine.Connectors;

public sealed class MongoSourceOptions
{
    public string? Database { get; init; }

    public string? Collection { get; init; }

    public JsonElement? Filter { get; init; }

    public string? Sort { get; init; }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (string.IsNullOrWhiteSpace(Collection))
        {
            throw new ArgumentException(
                $"MongoDB source '{stepId}' requires Options.Collection.");
        }
        if (Sort is not null && string.IsNullOrWhiteSpace(Sort))
        {
            throw new ArgumentException(
                $"MongoDB source '{stepId}' Sort cannot be empty when specified.");
        }
    }

    public static MongoSourceOptions FromJson(string optionsJson, string stepId)
    {
        try
        {
            var options = JsonSerializer.Deserialize<MongoSourceOptions>(
                              optionsJson,
                              ObjectStorageConnectorSupport.JsonOptions)
                          ?? throw new InvalidDataException(
                              $"MongoDB source options for '{stepId}' are empty.");
            options.Validate(stepId);
            return options;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"MongoDB source options for '{stepId}' are invalid JSON.",
                exception);
        }
    }
}

public sealed class MongoTargetOptions
{
    public string? Database { get; init; }

    public string[]? KeyColumns { get; init; }

    public void Validate(string stepId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        if (KeyColumns is not null && KeyColumns.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                $"MongoDB target '{stepId}' KeyColumns cannot contain empty names.");
        }
    }
}

internal static class MongoConnectionSupport
{
    public static IMongoDatabase GetDatabase(
        object connection,
        string? databaseName,
        string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is IMongoDatabase database)
        {
            return database;
        }
        if (connection is IMongoClient client)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                throw new ArgumentException(
                    $"MongoDB {description} requires Options.Database when its connection is an IMongoClient.");
            }
            return client.GetDatabase(databaseName);
        }

        throw new ArgumentException(
            $"MongoDB {description} requires an IMongoClient or IMongoDatabase connection; " +
            $"received '{connection.GetType().FullName}'.");
    }

    public static void ValidateConnection(object connection, string description)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not (IMongoClient or IMongoDatabase))
        {
            throw new ArgumentException(
                $"MongoDB {description} requires an IMongoClient or IMongoDatabase connection; " +
                $"received '{connection.GetType().FullName}'.");
        }
    }

    public static string GetBatchMarkerId(string runId, string stepId, string batchId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{runId}\0{stepId}\0{batchId}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static string GetPayloadHash(string recordsJson)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(recordsJson)));
    }
}

public sealed class MongoHierarchyRecordReader : IHierarchyRecordReader
{
    private readonly HierarchySourceLevel _level;
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly FilterDefinition<BsonDocument> _filter;
    private readonly SortDefinition<BsonDocument> _sort;
    private IAsyncCursor<BsonDocument>? _cursor;
    private List<BsonDocument>? _batch;
    private int _batchIndex;
    private JsonElement? _lookahead;
    private bool _disposed;

    public MongoHierarchyRecordReader(
        HierarchySourceLevel level,
        IMongoDatabase database,
        MongoSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(level.StepId);
        _level = level;
        _collection = database.GetCollection<BsonDocument>(options.Collection);
        _filter = options.Filter is { } filter
            ? BsonDocument.Parse(filter.GetRawText())
            : new BsonDocument();
        var sortField = options.Sort ?? level.KeyProperty;
        _sort = string.IsNullOrWhiteSpace(sortField)
            ? Builders<BsonDocument>.Sort.Ascending("_id")
            : Builders<BsonDocument>.Sort.Ascending(sortField);
    }

    public long RecordCount { get; private set; }

    public static async Task<long> CountRecordsAsync(
        IMongoDatabase database,
        string stepId,
        MongoSourceOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate(stepId);
        var collection = database.GetCollection<BsonDocument>(options.Collection);
        var filter = options.Filter is { } value
            ? BsonDocument.Parse(value.GetRawText())
            : new BsonDocument();
        return await collection.CountDocumentsAsync(
            filter,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<JsonElement?> PeekAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead.HasValue)
        {
            return _lookahead;
        }

        while (true)
        {
            if (_batch is not null && _batchIndex < _batch.Count)
            {
                _lookahead = ToJsonElement(_batch[_batchIndex++]);
                return _lookahead;
            }

            _batch = null;
            _cursor ??= await _collection.Find(_filter)
                .Sort(_sort)
                .ToCursorAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await _cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            _batch = _cursor.Current.ToList();
            _batchIndex = 0;
        }
    }

    public JsonElement Take()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lookahead is not { } value)
        {
            throw new InvalidOperationException(
                $"MongoDB source '{_level.StepId}' has no buffered record.");
        }
        _lookahead = null;
        RecordCount++;
        return value;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }
        _disposed = true;
        _cursor?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static JsonElement ToJsonElement(BsonDocument document)
    {
        return JsonSerializer.SerializeToElement(ToObject(document));
    }

    private static object? ToObject(BsonValue value)
    {
        return value switch
        {
            BsonNull => null,
            BsonObjectId objectId => objectId.Value.ToString(),
            BsonString text => text.Value,
            BsonBoolean boolean => boolean.Value,
            BsonInt32 number => number.Value,
            BsonInt64 number => number.Value,
            BsonDouble number => number.Value,
            BsonDecimal128 number => ToDecimal(number),
            BsonDateTime dateTime => dateTime.ToUniversalTime(),
            BsonBinaryData binary => Convert.ToBase64String(binary.Bytes),
            BsonTimestamp timestamp => timestamp.Value,
            BsonRegularExpression regex => regex.Pattern,
            BsonJavaScript javascript => javascript.Code,
            BsonArray array => array.Select(ToObject).ToList(),
            BsonDocument document => document.Elements.ToDictionary(
                element => element.Name,
                element => ToObject(element.Value),
                StringComparer.Ordinal),
            _ => value.ToString()
        };
    }

    private static object ToDecimal(BsonDecimal128 value)
    {
        return decimal.TryParse(
            value.ToString(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var result)
            ? result
            : value.ToString();
    }
}

public static class MongoTargetWriter
{
    private const string BatchCollectionName = "_m4d_batch_ledger";

    public static void WriteBatch(
        object connection,
        string collectionName,
        string stepId,
        string runId,
        string batchId,
        string recordsJson,
        string optionsJson)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordsJson);
        var options = DeserializeOptions(optionsJson, stepId);
        options.Validate(stepId);
        var database = MongoConnectionSupport.GetDatabase(
            connection,
            options.Database,
            $"target '{stepId}'");
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var ledger = database.GetCollection<BsonDocument>(BatchCollectionName);
        var markerId = MongoConnectionSupport.GetBatchMarkerId(runId, stepId, batchId);
        var payloadHash = MongoConnectionSupport.GetPayloadHash(recordsJson);
        var existing = ledger.Find(Builders<BsonDocument>.Filter.Eq("_id", markerId))
            .FirstOrDefault();
        if (existing is not null)
        {
            var existingHash = existing.TryGetValue("PayloadHash", out var hash)
                ? hash.AsString
                : string.Empty;
            if (string.Equals(existingHash, payloadHash, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            throw new InvalidDataException(
                $"MongoDB target '{collectionName}' batch '{batchId}' was already written with different content.");
        }

        using var document = JsonDocument.Parse(recordsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"MongoDB target '{collectionName}' records must be a JSON array.");
        }
        var keyColumns = options.KeyColumns;
        foreach (var record in document.RootElement.EnumerateArray())
        {
            var bson = BsonDocument.Parse(record.GetRawText());
            if (keyColumns is { Length: > 0 })
            {
                var filters = keyColumns.Select(key =>
                {
                    if (!bson.TryGetValue(key, out var value))
                    {
                        throw new InvalidDataException(
                            $"MongoDB target '{collectionName}' record is missing key column '{key}'.");
                    }
                    return Builders<BsonDocument>.Filter.Eq(key, value);
                });
                var filter = Builders<BsonDocument>.Filter.And(filters);
                collection.ReplaceOne(filter, bson, new ReplaceOptions { IsUpsert = true });
            }
            else
            {
                collection.InsertOne(bson);
            }
        }

        ledger.InsertOne(new BsonDocument
        {
            ["_id"] = markerId,
            ["RunId"] = runId,
            ["StepId"] = stepId,
            ["BatchId"] = batchId,
            ["PayloadHash"] = payloadHash
        });
    }

    private static MongoTargetOptions DeserializeOptions(string optionsJson, string stepId)
    {
        try
        {
            return JsonSerializer.Deserialize<MongoTargetOptions>(
                       optionsJson,
                       ObjectStorageConnectorSupport.JsonOptions)
                ?? throw new InvalidDataException(
                    $"MongoDB target options for '{stepId}' are empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"MongoDB target options for '{stepId}' are invalid JSON.",
                exception);
        }
    }
}
