import { Button } from "@fluentui/react-components";
import { Download, FileWarning } from "lucide-react";
import { useState } from "react";
import { createBinaryArtifactUrl } from "../dashboardApi";
import { BinaryValueMetadata, displayBinaryFileName, formatByteCount } from "../dashboardUtils";
import { DebugSnapshot } from "../types";
import { CopyableValue } from "./CopyableValue";

export function BinaryAttachmentValue({
  snapshot,
  propertyName,
  metadata,
}: {
  snapshot: DebugSnapshot;
  propertyName: string;
  metadata: BinaryValueMetadata;
}) {
  const [message, setMessage] = useState<{ text: string; isError: boolean } | null>(null);
  const [isChecking, setIsChecking] = useState(false);
  const fileName = displayBinaryFileName(metadata.fileName);
  const details = [
    metadata.contentType ?? "Unknown type",
    formatByteCount(metadata.length),
    metadata.representation,
  ].join(" · ");
  const displayValue = `${fileName} · ${details}`;

  const download = async () => {
    setIsChecking(true);
    setMessage(null);
    const url = createBinaryArtifactUrl(snapshot, propertyName);
    try {
      const response = await fetch(url, { method: "HEAD", cache: "no-store" });
      if (response.status === 200) {
        setMessage({ text: "Download starting…", isError: false });
        window.location.assign(url);
      } else if (response.status === 410) {
        setMessage({
          text: "The staged file is no longer available; successful runs remove temporary files.",
          isError: true,
        });
      } else if (response.status === 404) {
        setMessage({ text: "The captured file could not be found.", isError: true });
      } else {
        setMessage({ text: `File availability check failed (${response.status}).`, isError: true });
      }
    } catch (reason) {
      const detail = reason instanceof Error ? reason.message : String(reason);
      setMessage({ text: `File availability check failed: ${detail}`, isError: true });
    } finally {
      setIsChecking(false);
    }
  };

  return (
    <span className="binary-value-cell">
      <CopyableValue
        label={propertyName}
        displayValue={displayValue}
        copyValue={displayValue}
        displayContent={(
          <span className="binary-value-metadata">
            <span className="binary-value-name">{fileName}</span>
            <span className="binary-value-details">{details}</span>
          </span>
        )}
      />
      <Button
        appearance="subtle"
        size="small"
        icon={message?.isError ? <FileWarning size={14} /> : <Download size={14} />}
        aria-label={`Download ${fileName}`}
        title={message?.text ?? `Download ${fileName}`}
        disabled={isChecking}
        onClick={(event) => {
          event.stopPropagation();
          void download();
        }}
      />
      {message && (
        <span
          className={`binary-value-message${message.isError ? " error" : ""}`}
          role={message.isError ? "alert" : "status"}
        >{message.text}</span>
      )}
    </span>
  );
}
