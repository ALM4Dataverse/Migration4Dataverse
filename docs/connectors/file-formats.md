# File formats

Storage and serialization are configured independently. Use `Type = 'file'` for local files or a provider connector such as `s3`; then select the serialization with a top-level `Format` descriptor. Format-specific settings belong under `Format.Options`, while filesystem and object-storage settings stay on the connector.

```powershell
Source = @{
    Type = 'file'
    Format = @{
        Type = 'csv'
        Options = @{
            ColumnTypes = @{ CreatedOn = 'datetime' }
        }
    }
    ConnectionId = 'input-files'
    Path = 'records.csv'
    Key = 'Id'
}

Target = @{
    Type = 's3'
    Format = @{
        Type = 'parquet'
        Options = @{
            SchemaByStep = @{
                Records = @(@{ Name = 'Id'; Type = 'string' })
            }
        }
    }
    ConnectionId = 'archive'
    Path = 'exports/records.parquet'
    Options = @{ Bucket = 'migration-archive' }
}
```

## Built-in formats

| `Format.Type` | Source | Target | Guide |
|---|---:|---:|---|
| `jsonl` | Yes | Yes | [JSON Lines source](./sources/jsonl.md) and [target](./targets/jsonl.md) |
| `csv` | Yes | Yes | [CSV source](./sources/csv.md) and [target](./targets/csv.md) |
| `parquet` | Yes | Yes | [Parquet source](./sources/parquet.md) and [target](./targets/parquet.md) |
| `excel` | Yes | No | [Excel source](./sources/excel.md) |
| `binary` | Yes | Yes | [Binary files and documents](./binary-files.md) |
| `custom` | Yes | Yes | See [custom stream callbacks](#custom-stream-callbacks) |

File sources use a `DirectoryInfo` plus relative `Path` or a `FileInfo` naming the exact file. Object-storage sources select objects using the extension(s) registered for the chosen built-in format; `Format.Extensions` can be used only with custom formats. Binary values in structured formats can be kept in sibling sidecar files; see [binary values and sidecar files](../concepts/binary-values-and-sidecars.md).

## Custom stream callbacks

Custom formats let a migration define serialization without introducing a new storage connector. The callbacks run in the engine's PowerShell runspaces. Source callbacks produce one record at a time; target callbacks receive only the current bounded target batch. The engine does not read or write an entire file into memory.

### Custom source

The source format requires a stable `Identity` for resume compatibility and a `Read` scriptblock. `Read` receives a readable .NET stream and a context with `StepId`, `Path`, `ObjectKey`, `Options`, and `BinaryStagingRoot`. It emits record objects to the PowerShell pipeline. Each emitted value must be an object/dictionary suitable for migration mapping.

```powershell
Source = @{
    Type = 'file'
    Format = @{
        Type = 'custom'
        Identity = 'vendor-records-v1'
        Extensions = @('.records')
        Options = @{ Delimiter = '|' }
        Read = {
            param($Stream, $Context)
            $reader = [System.IO.StreamReader]::new(
                $Stream,
                [System.Text.Encoding]::UTF8,
                $true,
                4096,
                $true)
            while ($null -ne ($line = $reader.ReadLine())) {
                $fields = $line.Split([char]$Context.Options.Delimiter)
                @{ Id = $fields[0]; Name = $fields[1] }
            }
        }
    }
    ConnectionId = 'input-files'
    Path = 'records.records'
    Key = 'Id'
}
```

For an object-storage source, `Format.Extensions` filters listed objects by suffix. If the object keys have no predictable extension, set connector `Options.IncludeAllExtensions = $true`; object directory markers are still skipped. Local file sources read the exact configured `Path` and do not filter by extension.

### Custom target

The target format requires a `Write` scriptblock. It receives the current `$Records` batch, a writable .NET stream, and a context with `StepId`, `Path`, `ObjectKey`, `Options`, `FirstBatch`, `LastBatch`, mutable `State`, and `BinaryStagingRoot`.

```powershell
Target = @{
    Type = 'file'
    Format = @{
        Type = 'custom'
        Extensions = @('.records')
        ContentType = 'application/vnd.example.records'
        Options = @{ Delimiter = '|' }
        Write = {
            param($Records, $OutputStream, $Context)
            $writer = [System.IO.StreamWriter]::new(
                $OutputStream,
                [System.Text.UTF8Encoding]::new($false),
                4096,
                $true)
            foreach ($record in $Records) {
                $writer.WriteLine("$($record.Id)$($Context.Options.Delimiter)$($record.Name)")
            }
            $writer.Flush()
        }
    }
    ConnectionId = 'output-files'
    Path = 'records.records'
}
```

The writer must write to the provided stream, flush buffered output, leave the stream open, and emit no PowerShell pipeline output. `FirstBatch` and `LastBatch` allow format headers and footers; `State` is shared across batch invocations for that target finalization. Local target output is staged and atomically published after all work succeeds. Object-storage output is staged locally and uploaded after graph completion; set `Format.Extensions` to control the staged file suffix and `Format.ContentType` to override the default MIME type. Object-storage `Options.ContentType`, when set, takes precedence over the format content type.

`MigrationBinaryValue` fields stay file-backed across record batches. A custom writer can stream the value using its helpers instead of converting it to Base64 or buffering it in a string. See [`New-MigrationBinaryValue`](../cmdlets/new-migration-binary-value.md).
