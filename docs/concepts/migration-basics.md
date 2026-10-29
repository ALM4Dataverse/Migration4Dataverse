# Migration basics

A migration moves rows from one place to another. You describe the steps and how rows flow between them; `Invoke-Migration` runs those steps and manages the work.

## The main parts

| Step | What it does |
|---|---|
| **Source** | Reads rows from a file, database, Dataverse, or a custom reader. |
| **Transform** | Changes a row, such as renaming fields, converting values, or building a target-shaped record. A transform can emit no rows, one row, or several rows for each input row. |
| **Filter** | Decides whether a row continues. A passing row keeps its original values; a row with no output or a falsey result is dropped. |
| **Target** | Writes rows to a file, database, Dataverse, or a custom writer. |

A simple flow is:

```text
CSV file -> Source -> Transform -> Filter -> Target -> Dataverse
```

Each step has an `Id`. Its `Inputs` names the step or steps that provide its rows. The flow does not have to be one straight line: a source can feed multiple steps, and dependencies or joins can coordinate related data. See [step configuration](../configuration.md) for the full supported graph and [dependencies](./dependencies.md) for row relationships, joins, and wait-for-completion behavior.

## Connections: where steps find data

A source or target's `ConnectionId` is a name that points to a runtime value supplied separately to `Invoke-Migration` with `-Connections`. For example, a step may say `ConnectionId = 'files'`, while the connection map associates `files` with a directory. A Dataverse connection ID points to an authenticated connection object.

This keeps the migration definition separate from live objects and credentials. File connectors use a directory or exact file; SQLite and DuckDB use a database path, with connections created by the engine; SQL Server, PostgreSQL, MySQL, and Oracle use caller-supplied ADO.NET connections; object-storage connectors use provider clients; HTTP sources use a URI or `HttpClient`; MongoDB uses an `IMongoClient` or `IMongoDatabase`; and Dataverse uses a connection created with `Rnwood.Dataverse.Data.PowerShell`. Custom connectors can use a caller-provided connection object. SQLite and DuckDB transforms use engine-managed databases, while Dataverse transforms use a Dataverse connection.

The connection value depends on the connector, so see [source connectors](../connectors/sources/README.md), [target connectors](../connectors/targets/README.md), and [connections and callbacks](./connections-and-callbacks.md) for the details. The [Dataverse source](../connectors/sources/dataverse.md) and [Dataverse target](../connectors/targets/dataverse.md) guides explain how this project works with the upstream module.

## Where to go next

- [Step configuration](../configuration.md) — exact step properties and transform/filter behavior.
- [Source connectors](../connectors/sources/README.md) and [target connectors](../connectors/targets/README.md) — supported data sources and destinations.
- [Connections and callbacks](./connections-and-callbacks.md) — runtime connection ownership and custom PowerShell callbacks.
- [Dependencies](./dependencies.md) — keyed relationships, joins, and step completion barriers.
- [PowerShell reference](../reference.md) — index of the supported migration contract.
