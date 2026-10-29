import { DashboardShell } from "./components/DashboardShell";
import { useDashboardController } from "./hooks/useDashboardController";

export default function App() {
  const controller = useDashboardController();
  return <DashboardShell controller={controller} />;
}
