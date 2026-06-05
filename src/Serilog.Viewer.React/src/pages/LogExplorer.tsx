import { FileBrowser } from "@/components/FileBrowser";
import { FilterBar } from "@/components/FilterBar";
import { LogGrid } from "@/components/LogGrid";
import { LogDetailsDrawer } from "@/components/LogDetailsDrawer";
import { useAppStore } from "@/store/appStore";

export function LogExplorer() {
  const { selectedEntry, setSelectedEntry } = useAppStore();

  return (
    <div className="flex h-full overflow-hidden">
      {/* Left panel — file browser */}
      <aside className="w-56 shrink-0 border-r border-[#21262d] flex flex-col overflow-hidden bg-[#0d1117]">
        <FileBrowser />
      </aside>

      {/* Main content */}
      <div className="flex-1 flex flex-col overflow-hidden">
        <FilterBar />
        <div className="flex-1 overflow-hidden">
          <LogGrid />
        </div>
      </div>

      {/* Right panel — details drawer */}
      <LogDetailsDrawer
        entry={selectedEntry}
        onClose={() => setSelectedEntry(null)}
      />
    </div>
  );
}
