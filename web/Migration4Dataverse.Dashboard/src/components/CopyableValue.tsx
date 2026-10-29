import { Button } from "@fluentui/react-components";
import { Check, Copy } from "lucide-react";
import { useState } from "react";

export function CopyableValue({
  label,
  displayValue,
  copyValue,
}: {
  label: string;
  displayValue: string;
  copyValue: string;
}) {
  const [copyStatus, setCopyStatus] = useState<{ message: string; isError: boolean } | null>(null);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(copyValue);
      setCopyStatus({ message: "Copied", isError: false });
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : String(reason);
      setCopyStatus({ message: `Copy failed: ${message}`, isError: true });
    }
  };

  return (
    <span className="grid-copy-cell">
      <span className="grid-cell-text" title={displayValue}>{displayValue}</span>
      <Button
        className="grid-copy-button"
        appearance="subtle"
        size="small"
        icon={copyStatus?.isError ? <Copy size={13} /> : copyStatus ? <Check size={13} /> : <Copy size={13} />}
        aria-label={`Copy ${label} value`}
        title={copyStatus?.message ?? `Copy ${label} value`}
        onClick={(event) => {
          event.stopPropagation();
          void copy();
        }}
      />
      {copyStatus && (
        <span
          className={`grid-copy-feedback${copyStatus.isError ? " error" : ""}`}
          role={copyStatus.isError ? "alert" : "status"}
        >{copyStatus.message}</span>
      )}
    </span>
  );
}
