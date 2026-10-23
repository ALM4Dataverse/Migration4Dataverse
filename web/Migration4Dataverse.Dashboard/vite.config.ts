import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../../src/Migration4Dataverse.Engine/Dashboard/wwwroot",
    emptyOutDir: true,
    sourcemap: false,
  },
});
