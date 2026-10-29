import { FluentProvider, webLightTheme } from "@fluentui/react-components";
import { TaskErrorDialog } from "./TaskErrorDialog";
import { DashboardHeader } from "./DashboardHeader";
import { RunNotices } from "./RunNotices";
import { DashboardContent } from "./DashboardContent";
import type { DashboardController } from "../hooks/useDashboardController";

export function DashboardShell({ controller }: { controller: DashboardController }) {
  return (
    <FluentProvider className="fluent-root" theme={webLightTheme}>
      <TaskErrorDialog controller={controller} />
      <div className="app-shell">
        <DashboardHeader controller={controller} />
        <RunNotices controller={controller} />
        <DashboardContent controller={controller} />
      </div>
    </FluentProvider>
  );
}
