# MongoDB connector

The `mongodb` connector reads documents from a collection and upserts or inserts documents into a target collection. The caller supplies an `IMongoClient` or `IMongoDatabase` in `-Connections`; the engine does not create credentials or dispose the caller's client.

## Source

A MongoDB source requires `Options.Collection`. When the connection is an `IMongoClient`, also set `Options.Database`.

| Option | Default | Description |
|---|---:|---|
| `Collection` | Required | Collection to read. |
| `Database` | `null` | Database name when the connection is an `IMongoClient`. |
| `Filter` | `{}` | Optional MongoDB filter as a JSON/BSON document. |
| `Sort` | `Source.Key` or `_id` | Field used to order documents. The source key must be unique and strictly increasing. |

Documents are converted to JSON-compatible records. `ObjectId` values become hexadecimal strings, dates become ISO 8601 JSON values, binary values become base64 strings, and nested documents and arrays remain nested.

```powershell
$steps = @(
    @{
        Id = 'Accounts'
        Type = 'source'
        Inputs = @()
        Source = @{
            Type = 'mongodb'
            ConnectionId = 'mongo'
            Key = '_id'
            Options = @{
                Database = 'crm'
                Collection = 'accounts'
                Sort = '_id'
            }
        }
    }
)
```

The source count provider uses `CountDocumentsAsync` with the same filter. A positive `ExpectedRecordCount` takes precedence.

| BSON value | Type in the PowerShell mapping |
|---|---|
| Null, Boolean, integer, floating-point, Decimal128 | `$null`, `Boolean`, `Int64`, `Double` |
| String, ObjectId, regular expression, JavaScript | `System.String` |
| Date/time | `System.DateTime` for ISO-formatted values after default PowerShell JSON parsing |
| Binary | Base64 `System.String` |
| Embedded document / array | PowerShell dictionary / array |

## Target

A MongoDB target requires `Target.Table`, which is the target collection name. Set `Options.Database` when the connection is an `IMongoClient`.

| Option | Default | Description |
|---|---:|---|
| `Database` | `null` | Database name when the connection is an `IMongoClient`. |
| `KeyColumns` | Empty | Fields used to match an existing document. With keys, each record is upserted with `ReplaceOne(..., IsUpsert = true)`. Without keys, records are inserted. |

Each batch records a replay marker in the `_m4d_batch_ledger` collection. An identical retry is ignored; the same batch identity with different records fails. With no `KeyColumns`, a replayed batch is skipped by the marker, but a crash between document inserts and marker insertion can leave duplicate documents on the next attempt. Use `KeyColumns` for replay-safe writes.

| Logical mapped value | Accepted MongoDB representation |
|---|---|
| Null, Boolean, number, string | Corresponding JSON scalar parsed as BSON |
| Nested object / array | JSON object / array parsed as embedded BSON document / array |
| GUID, date/time, ObjectId | Strings remain strings unless expressed in MongoDB Extended JSON notation |
| Binary content | Not a file-backed binary target; Base64 text is stored as text. Use a binary-capable connector or custom target for streamed files. |

```powershell
@{
    Id = 'AccountsTarget'
    Type = 'target'
    Inputs = @('Accounts')
    Target = @{
        Type = 'mongodb'
        ConnectionId = 'mongo'
        Table = 'accounts_migrated'
        Options = @{
            Database = 'crm'
            KeyColumns = @('_id')
        }
    }
}
```

See [source connectors](./sources/README.md), [target connectors](./targets/README.md), and [migration step configuration](../configuration.md).
