# Internal data-flow transformations

This reference describes the engine's separate data-flow pipeline. It is not part of the supported `Invoke-Migration` step contract.

> [!IMPORTANT]
> The C# transformation types and this pipeline are internal implementation details, not a supported public C# API. For the PowerShell steps supported by `Invoke-Migration`, see [migration step configuration](../../configuration.md).

PowerShell callers of this internal pipeline define it as a hashtable and put each Transform or Filter script block directly in that node's `Script` property. The engine extracts those blocks internally; the configuration does not need to be converted to JSON or paired with a separate script map.

## PowerShell configuration

`WriteJsonLinesAsync` reads JSON Lines, CSV, or Parquet file sources and writes the pipeline output as JSON Lines. This example uses PowerShell hashtables throughout and keeps the scripts inline:

```powershell
$pipeline = [ordered]@{
    Sources = @(
        @{
            Id = 'input'
            Type = 'jsonl'
            ConnectionId = 'files'
            Path = (Join-Path $PWD 'input.jsonl')
        }
    )
    Nodes = @(
        # Starts the pipeline with rows from the declared JSON Lines source.
        @{ Id = 'records'; Type = 'source'; SourceId = 'input' }
        @{
            # Keeps only rows marked active.
            Id = 'keep-active'
            Type = 'filter'
            Inputs = @('records')
            Script = {
                param($row)
                $row.Active
            }
        }
        @{
            # Trims customer names before writing the output.
            Id = 'normalize-name'
            Type = 'transform'
            Inputs = @('keep-active')
            Script = {
                param($row)
                @{
                    Id = $row.Id
                    Name = ([string]$row.Name).Trim()
                }
            }
        }
        @{
            # Sorts normalized rows by name before output.
            Id = 'ordered'
            Type = 'sort'
            Inputs = @('normalize-name')
            Keys = @(@{ Name = 'Name'; Type = 'string' })
        }
    )
    OutputNodeId = 'ordered'
}

[Migration4Dataverse.Engine.Transformations.DataFlowPipeline]::WriteJsonLinesAsync(
    $pipeline,
    (Join-Path $PWD 'output.jsonl'),
    [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
```

`Script` is valid only on Transform and Filter nodes. A Transform script can emit zero, one, or multiple JSON object rows per input. A Filter script retains the original row when its PowerShell output is truthy. Script blocks are executed as source text in an engine-owned PowerShell runspace, so caller closure state is not captured.

## Pipeline shape

A pipeline has `Sources`, `Nodes`, and one `OutputNodeId`. A source node refers to a declared source by `SourceId`; all other node types refer to their upstream nodes through `Inputs`.

| Field | Required | Description |
|---|---:|---|
| `Sources[].Id` | Yes | Unique source ID. |
| `Sources[].Type` | Yes | Connector type interpreted by the reader. The JSONL writer supports `jsonl`, `csv`, and `parquet` file sources. |
| `Sources[].ConnectionId` | Yes | Non-empty connector connection identifier. |
| `Sources[].Path` | File sources | Required for file source types. |
| `Sources[].Options` | No | Optional source options hashtable. |
| `Sources[].Identity` | Custom source | Stable identity required when `Type` is `custom`. |
| `Nodes[].Id` | Yes | Unique node ID. |
| `Nodes[].Type` | Yes | `source`, `append` (aliases `union-all`, `union_all`, `union all`), `merge`, `sort`, `transform`, or `filter`. |
| `Nodes[].SourceId` | Source node | ID of exactly one declared source. |
| `Nodes[].Inputs` | Non-source node | Upstream node IDs. Transform and Filter each require exactly one input. |
| `Nodes[].Script` | Transform and Filter | Inline PowerShell script block, executed for each input row. |
| `Nodes[].Keys` | Merge and Sort | Ordered typed key definitions. |
| `Nodes[].SortOptions` | Sort | Optional external-sort limits. |
| `OutputNodeId` | Yes | ID of the node to enumerate. |

All nodes must contribute to the output path, each node can have at most one consumer, and each source must be used by exactly one source node. The graph cannot contain cycles, duplicate IDs, duplicate inputs, or missing references. Transform scripts may emit multiple rows from an input; this does not add graph-level fan-out.

## PowerShell row scripts

- **Transform** treats every script output as a row. It can emit zero rows to drop the input, one row to replace it, or multiple rows to expand it. Every emitted value must serialize as a JSON object.
- **Filter** evaluates PowerShell output truthiness. Truthy output passes the original input row unchanged; no output or falsey output drops it.

Scripts receive the input JSON object as their first argument. Script failures identify the relevant node. The engine converts inline script blocks into its internal node-ID map and excludes them from the serialized pipeline definition.

## Sort key definition

| Field | Default | Description |
|---|---|---|
| `Name` | Required | JSON object property to compare. |
| `Type` | `string` | `string`, `bool`/`boolean`, `byte`, `short`/`int16`, `int`/`int32`, `long`/`int64`, `float`/`single`, `double`/`number`, `decimal`, `guid`, `date`, `datetime`/`datetimeoffset`. |
| `Descending` | `false` | Reverse value ordering for this key. |
| `NullsFirst` | `true` | Place null values first or last, independently of direction. |

String values use ordinal comparison. Date and datetime sort values must be JSON strings parseable as dates/timestamps; other types must have their corresponding JSON value kinds. Keys are compared in array order.

## Implemented nodes

- [Append / Union All](./append.md) — sequentially concatenate two or more streams and preserve duplicates.
- [Merge](./merge.md) — stream two pre-sorted inputs into one sorted stream.
- [Sort](./sort.md) — stable external-memory sort with configurable memory and disk bounds.
- [Transform](./transform.md) — apply an inline PowerShell script that can emit zero or more row objects.
- [Filter](./filter.md) — retain or discard each input row using inline PowerShell truthiness.

Graph fan-out (multiple consumers), joins, grouping, and other SSIS transformations are not implemented as pipeline nodes. See [architecture](../architecture.md) for execution boundaries.
