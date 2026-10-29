import { ConnectionInstanceSnapshot, ConnectionReference, MigrationConfiguration } from "../types";
import { endpointConnection } from "../dashboardUtils";
import { Text } from "./Text";
import { InfoRow } from "./InfoRow";

export function ConfigurationInspector({
  configuration,
  selectedJob,
  connectionInstances,
}: {
  configuration: MigrationConfiguration;
  selectedJob: string;
  connectionInstances: ConnectionInstanceSnapshot[];
}) {
  const job = configuration.jobs.find((candidate) => candidate.name === selectedJob);
  if (!job) {
    return <section className="panel configuration-inspector"><div className="inline-empty">Configuration for this step is unavailable.</div></section>;
  }

  const connectionsById = new Map(configuration.connections.map((connection) => [connection.id, connection]));
  const endpointReferences = [
    endpointConnection(job, connectionsById, "Source"),
    endpointConnection(job, connectionsById, "Target"),
  ].filter((connection): connection is ConnectionReference => Boolean(connection));
  const referencedConnectionIds = new Set(
    endpointReferences.map((connection) => connection.id),
  );
  const endpointTypesById = new Map(endpointReferences.map((connection) => [connection.id, connection.typeName]));
  const connections = configuration.connections.filter((connection) => referencedConnectionIds.has(connection.id));

  return (
    <section className="panel configuration-inspector">
      <div className="configuration-columns">
        <div>
          <Text variant="mediumPlus" className="section-title">Step configuration</Text>
          <div className="configuration-meta">
            <InfoRow label="Type" value={job.type} />
            <InfoRow label="Inputs" value={job.dependsOn.filter((id) => !job.completionDependencies.includes(id)).join(", ") || "None"} />
            <InfoRow label="Waits for completion" value={job.completionDependencies.join(", ") || "None"} />
            <InfoRow label="Reads connector source" value={job.hasSource ? "Yes" : "No"} />
          </div>
          <h4>Properties</h4>
          <pre className="configuration-json">{JSON.stringify(job.properties, null, 2)}</pre>
        </div>
        <div>
          <Text variant="mediumPlus" className="section-title">Connected endpoints</Text>
          {connections.length === 0 ? (
            <div className="inline-empty">This step has no named source or target connection.</div>
          ) : (
            <div className="configuration-list">
              {connections.map((connection) => {
                const snapshots = connectionInstances.flatMap((snapshot) =>
                  snapshot.connections
                    .filter((instance) => instance.id === connection.id)
                    .map((instance) => ({ phase: snapshot.phase, capturedUtc: snapshot.capturedUtc, instance })),
                );
                return (
                  <section className="configuration-connection" key={connection.id}>
                    <div className="configuration-connection-heading">
                      <strong>{endpointTypesById.get(connection.id) ?? connection.typeName?.split(".").at(-1) ?? "Connection"}</strong>
                      <span>{connection.id}</span>
                    </div>
                    <h4>Configured properties</h4>
                    <pre className="configuration-json">{JSON.stringify(connection.properties, null, 2)}</pre>
                    {snapshots.map((snapshot) => (
                      <div className="connection-snapshot" key={`${snapshot.phase}-${snapshot.capturedUtc}`}>
                        <h4>{snapshot.phase} instance · {new Date(snapshot.capturedUtc).toLocaleString()}</h4>
                        <div className="configuration-meta">
                          <InfoRow label="Runtime type" value={snapshot.instance.typeName ?? "Unknown"} />
                        </div>
                        <pre className="configuration-json">{JSON.stringify(snapshot.instance.properties, null, 2)}</pre>
                      </div>
                    ))}
                    {snapshots.length === 0 && <Text>No runtime instance snapshot is stored for this connection.</Text>}
                  </section>
                );
              })}
            </div>
          )}
        </div>
      </div>
      <Text variant="small" className="configuration-disclaimer">Configuration values are read-only; secret fields are redacted.</Text>
    </section>
  );
}
