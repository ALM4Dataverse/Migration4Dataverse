using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Migration4Dataverse.Engine.Storage;

namespace Migration4Dataverse.Engine.Scheduling;

internal static class MigrationDebugCapture
{
    private static readonly string[] SensitivePropertyFragments =
    [
        "password",
        "secret",
        "token",
        "credential",
        "authorization",
        "apikey",
        "privatekey",
        "connectionstring",
        "sas"
    ];

    public static IReadOnlyList<MigrationDebugSnapshot> CaptureInput(
        StoredBatch batch,
        Func<string, int, string?>? resolveParentLineage = null)
    {
        using var document = JsonDocument.Parse(batch.PayloadJson
            ?? throw new InvalidOperationException($"Batch '{batch.BatchId}' has no payload to capture."));
        var records = GetRecords(document.RootElement);
        var parentReferences = GetParentReferences(document.RootElement, records.Count, resolveParentLineage);
        var snapshots = new List<MigrationDebugSnapshot>();
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (record.ValueKind == JsonValueKind.Object &&
                record.TryGetProperty("Source", out var source) &&
                source.ValueKind == JsonValueKind.Object)
            {
                var parentLineageId = GetLineageId(batch, source, index);
                snapshots.Add(CreateSnapshot(
                    batch,
                    "SourceInput",
                    index,
                    parentLineageId,
                    parentReferences[index],
                    source));
                CaptureChildren(batch, record, index, parentLineageId, snapshots);
            }
            else
            {
                snapshots.Add(CreateSnapshot(
                    batch,
                    "BatchInput",
                    index,
                    GetLineageId(batch, record, index),
                    parentReferences[index],
                    record));
            }
        }

        return snapshots;
    }

    public static IReadOnlyList<string> GetPrimaryLineageIds(StoredBatch batch)
    {
        using var document = JsonDocument.Parse(batch.PayloadJson
            ?? throw new InvalidOperationException($"Batch '{batch.BatchId}' has no payload to capture."));
        var records = GetRecords(document.RootElement);
        return records
            .Select((record, index) =>
            {
                if (record.ValueKind == JsonValueKind.Object &&
                    record.TryGetProperty("Source", out var source) &&
                    source.ValueKind == JsonValueKind.Object)
                {
                    record = source;
                }

                return GetLineageId(batch, record, index);
            })
            .ToArray();
    }

    public static IReadOnlyList<MigrationDebugSnapshot> CaptureStage(
        StoredBatch batch,
        MigrationDebugStage stage,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? parentBatchLineageIds = null,
        IReadOnlyList<string>? inputLineageIds = null)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage.StageName);
        using var document = JsonDocument.Parse(stage.RecordsJson);
        var records = GetRecords(document.RootElement);
        var primaryLineageIds = inputLineageIds ?? GetPrimaryLineageIds(batch);
        if (stage.ParentRecordIndices is not null && stage.ParentRecordReferences is not null)
        {
            throw new ArgumentException(
                $"Debug stage '{stage.StageName}' cannot specify both parent indices and parent references.",
                nameof(stage));
        }
        if (stage.ParentRecordIndices is { } parentRecordIndices &&
            parentRecordIndices.Count != records.Count)
        {
            throw new ArgumentException(
                $"Debug stage '{stage.StageName}' has {parentRecordIndices.Count} parent mappings for {records.Count} output records.",
                nameof(stage));
        }
        if (stage.ParentRecordReferences is { } parentRecordReferences &&
            parentRecordReferences.Count != records.Count)
        {
            throw new ArgumentException(
                $"Debug stage '{stage.StageName}' has {parentRecordReferences.Count} parent reference mappings for {records.Count} output records.",
                nameof(stage));
        }

        var snapshots = new List<MigrationDebugSnapshot>(records.Count);
        for (var index = 0; index < records.Count; index++)
        {
            IReadOnlyList<string> parentLineageIds = Array.Empty<string>();
            string lineageId;
            if (stage.ParentRecordIndices is { } mappings)
            {
                var recordParents = mappings[index];
                ArgumentNullException.ThrowIfNull(recordParents);
                if (recordParents.Any(parentIndex => parentIndex < 0 || parentIndex >= primaryLineageIds.Count))
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(stage),
                        $"Debug stage '{stage.StageName}' contains a parent record index outside the input batch.");
                }

                lineageId = GetLineageId(batch, records[index], index);
                parentLineageIds = recordParents
                    .Select(parentIndex => primaryLineageIds[parentIndex])
                    .Where(parentLineageId => parentLineageId != lineageId)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            }
            else if (stage.ParentRecordReferences is { } referencesByOutput)
            {
                lineageId = GetLineageId(batch, records[index], index);
                var parentLineageIdsForRecord = new List<string>();
                var references = referencesByOutput[index]
                    ?? throw new ArgumentException(
                        $"Debug stage '{stage.StageName}' contains a null parent reference list.",
                        nameof(stage));
                foreach (var reference in references)
                {
                    if (reference is null || string.IsNullOrWhiteSpace(reference.BatchId))
                    {
                        throw new ArgumentException(
                            $"Debug stage '{stage.StageName}' contains an empty parent batch reference.",
                            nameof(stage));
                    }

                    IReadOnlyList<string> referencedLineageIds;
                    if (string.Equals(reference.BatchId, batch.BatchId, StringComparison.Ordinal))
                    {
                        referencedLineageIds = primaryLineageIds;
                    }
                    else
                    {
                        if (!batch.ParentBatchIds.Contains(reference.BatchId, StringComparer.Ordinal))
                        {
                            throw new ArgumentException(
                                $"Debug stage '{stage.StageName}' references batch '{reference.BatchId}', which is not a parent of batch '{batch.BatchId}'.",
                                nameof(stage));
                        }

                        if (parentBatchLineageIds is null)
                        {
                            throw new InvalidOperationException(
                                $"Input lineage IDs for parent batch '{reference.BatchId}' were not loaded.");
                        }
                        if (!parentBatchLineageIds.TryGetValue(
                                reference.BatchId,
                                out var loadedLineageIds))
                        {
                            throw new InvalidOperationException(
                                $"Input lineage IDs for parent batch '{reference.BatchId}' were not loaded.");
                        }

                        referencedLineageIds = loadedLineageIds;
                    }

                    if (reference.RecordIndex < 0 || reference.RecordIndex >= referencedLineageIds.Count)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(stage),
                            $"Debug stage '{stage.StageName}' references record index {reference.RecordIndex} outside parent batch '{reference.BatchId}'.");
                    }

                    var parentLineageId = referencedLineageIds[reference.RecordIndex];
                    if (!string.Equals(parentLineageId, lineageId, StringComparison.Ordinal) &&
                        !parentLineageIdsForRecord.Contains(parentLineageId, StringComparer.Ordinal))
                    {
                        parentLineageIdsForRecord.Add(parentLineageId);
                    }
                }

                parentLineageIds = parentLineageIdsForRecord;
            }
            else
            {
                lineageId = index < primaryLineageIds.Count
                    ? primaryLineageIds[index]
                    : GetLineageId(batch, records[index], index);
            }

            snapshots.Add(CreateSnapshot(
                batch,
                stage.StageName,
                index,
                lineageId,
                parentLineageIds,
                records[index]));
        }

        return snapshots;
    }

    private static IReadOnlyList<IReadOnlyList<string>> GetParentReferences(
        JsonElement payload,
        int recordCount,
        Func<string, int, string?>? resolveParentLineage)
    {
        var referencesByRecord = Enumerable.Range(0, recordCount)
            .Select(_ => (IReadOnlyList<string>)Array.Empty<string>())
            .ToArray();
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("__LineageReferences", out var references))
        {
            return referencesByRecord;
        }
        if (references.ValueKind != JsonValueKind.Array || references.GetArrayLength() != recordCount)
        {
            throw new InvalidDataException("Batch lineage metadata must contain one reference list per record.");
        }
        if (resolveParentLineage is null)
        {
            throw new InvalidOperationException("A lineage resolver is required when batch lineage metadata is present.");
        }

        var index = 0;
        foreach (var recordReferences in references.EnumerateArray())
        {
            if (recordReferences.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"Batch lineage metadata for record {index} must be an array.");
            }
            var lineageIds = new List<string>();
            foreach (var reference in recordReferences.EnumerateArray())
            {
                if (reference.ValueKind != JsonValueKind.Object ||
                    !reference.TryGetProperty("BatchId", out var batchIdElement) ||
                    !reference.TryGetProperty("RecordIndex", out var recordIndexElement) ||
                    batchIdElement.ValueKind != JsonValueKind.String ||
                    !recordIndexElement.TryGetInt32(out var parentRecordIndex) ||
                    parentRecordIndex < 0)
                {
                    throw new InvalidDataException($"Batch lineage metadata for record {index} is invalid.");
                }
                var parentBatchId = batchIdElement.GetString();
                if (string.IsNullOrWhiteSpace(parentBatchId))
                {
                    throw new InvalidDataException($"Batch lineage metadata for record {index} is invalid.");
                }
                var lineageId = resolveParentLineage(parentBatchId, parentRecordIndex)
                    ?? throw new InvalidDataException(
                        $"Parent record {parentRecordIndex} in batch '{parentBatchId}' has no captured output lineage.");
                if (!lineageIds.Contains(lineageId, StringComparer.Ordinal))
                {
                    lineageIds.Add(lineageId);
                }
            }
            referencesByRecord[index++] = lineageIds;
        }

        return referencesByRecord;
    }

    public static MigrationDebugSnapshot CaptureOutcomes(StoredBatch batch, string outcomesJson)
    {
        using var document = JsonDocument.Parse(outcomesJson);
        return CreateSnapshot(
            batch,
            "BatchOutcome",
            -1,
            $"batch-{batch.BatchId}",
            [],
            document.RootElement);
    }

    public static string CreateLineageId(string runId, string batchId, JsonElement record, int recordIndex)
    {
        var value = FindStableKey(record);
        var identity = value is null
            ? $"{runId}\0{batchId}\0record\0{recordIndex}"
            : $"{runId}\0key\0{value.Value.GetRawText()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"row-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static void CaptureChildren(
        StoredBatch batch,
        JsonElement wrapper,
        int parentIndex,
        string parentLineageId,
        ICollection<MigrationDebugSnapshot> snapshots)
    {
        if (!wrapper.TryGetProperty("Children", out var children) ||
            children.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var childJob in children.EnumerateObject())
        {
            var rows = childJob.Value.ValueKind == JsonValueKind.Array
                ? childJob.Value.EnumerateArray().ToArray()
                : [childJob.Value];
            for (var rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var row = rows[rowIndex];
                var lineageId = GetLineageId(batch, row, parentIndex * 100_000 + rowIndex);
                snapshots.Add(CreateSnapshot(
                    batch,
                    $"DependencyOutput:{childJob.Name}",
                    rowIndex,
                    lineageId,
                    [parentLineageId],
                    row));
            }
        }
    }

    private static MigrationDebugSnapshot CreateSnapshot(
        StoredBatch batch,
        string stageName,
        int recordIndex,
        string lineageId,
        IReadOnlyList<string> parentLineageIds,
        JsonElement payload)
    {
        var node = JsonNode.Parse(payload.GetRawText());
        Redact(node);
        return new MigrationDebugSnapshot(
            batch.RunId,
            batch.BatchId,
            batch.StepId,
            stageName,
            recordIndex,
            batch.Attempts,
            lineageId,
            parentLineageIds,
            node?.ToJsonString() ?? "null",
            DateTimeOffset.UtcNow);
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToArray())
            {
                if (SensitivePropertyFragments.Any(fragment =>
                        property.Key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    objectNode[property.Key] = "[REDACTED]";
                }
                else
                {
                    Redact(property.Value);
                }
            }
        }
        else if (node is JsonArray arrayNode)
        {
            foreach (var item in arrayNode)
            {
                Redact(item);
            }
        }
    }

    private static IReadOnlyList<JsonElement> GetRecords(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
        {
            return payload.EnumerateArray().ToArray();
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("Records", out var records) &&
            records.ValueKind == JsonValueKind.Array)
        {
            return records.EnumerateArray().ToArray();
        }

        return [payload];
    }

    private static string GetLineageId(StoredBatch batch, JsonElement record, int index)
    {
        return CreateLineageId(batch.RunId, batch.BatchId, record, index);
    }

    private static JsonElement? FindStableKey(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in record.EnumerateObject())
        {
            if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("recordid", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("sourceid", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("primarykey", StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
