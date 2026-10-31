import { useMemo } from "react";
import { BinaryValueMetadata, displayBinaryFileName, formatByteCount, friendlyStageName, getBinaryValueMetadata, parsePayload, snapshotKey } from "../dashboardUtils";
import { DebugSnapshot } from "../types";
import { Text } from "./Text";

interface BinaryLineageEntry {
  propertyName: string;
  snapshot: DebugSnapshot;
  metadata: BinaryValueMetadata;
  change: string;
}

export function BinaryLineageSummary({ snapshots }: { snapshots: DebugSnapshot[] }) {
  const entries = useMemo(() => {
    const previousByProperty = new Map<string, BinaryValueMetadata>();
    const binaryEntries: BinaryLineageEntry[] = [];
    for (const snapshot of [...snapshots].sort(
      (left, right) => Date.parse(left.capturedUtc) - Date.parse(right.capturedUtc),
    )) {
      for (const [propertyName, value] of Object.entries(parsePayload(snapshot.payloadJson))) {
        const metadata = getBinaryValueMetadata(value);
        if (!metadata) continue;
        const previous = previousByProperty.get(propertyName);
        const change = !previous
          ? "First captured"
          : previous.sha256 && previous.sha256 === metadata.sha256
            ? previous.fileName === metadata.fileName && previous.contentType === metadata.contentType
              ? "Bytes unchanged"
              : "Bytes unchanged; metadata changed"
            : previous.sha256 && metadata.sha256
              ? "Content changed"
              : "Hash unavailable";
        binaryEntries.push({ propertyName, snapshot, metadata, change });
        previousByProperty.set(propertyName, metadata);
      }
    }
    return binaryEntries;
  }, [snapshots]);

  if (entries.length === 0) return null;

  return (
    <section className="binary-lineage" aria-label="File value history">
      <div>
        <Text variant="mediumPlus">File values across this lineage</Text>
        <Text variant="small">Per-file size and content hash comparison; not transfer-throughput metrics.</Text>
      </div>
      <ol className="binary-lineage-list">
        {entries.map(({ propertyName, snapshot, metadata, change }, index) => (
          <li key={`${snapshotKey(snapshot)}:${propertyName}:${index}`}>
            <div className="binary-lineage-heading">
              <strong>{propertyName}</strong>
              <span>{snapshot.jobName} · {friendlyStageName(snapshot.stageName)}</span>
            </div>
            <div className="binary-lineage-details">
              <span>{displayBinaryFileName(metadata.fileName)}</span>
              <span>{metadata.contentType ?? "Unknown type"} · {formatByteCount(metadata.length)} · {metadata.representation}</span>
            </div>
            <div className="binary-lineage-change">
              <span>{change}</span>
              {metadata.sha256 && <code title={metadata.sha256}>SHA-256 {metadata.sha256.slice(0, 12)}</code>}
            </div>
          </li>
        ))}
      </ol>
    </section>
  );
}
