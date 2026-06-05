import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { formatDistanceToNow } from "date-fns";
import { FileText, RefreshCw, HardDrive, Clock, Zap } from "lucide-react";
import { api } from "@/services/api";
import { useAppStore } from "@/store/appStore";
import type { LogFile } from "@/types";

function FileRow({
  file,
  selected,
  onToggle,
}: {
  file: LogFile;
  selected: boolean;
  onToggle: (name: string) => void;
}) {
  return (
    <button
      onClick={() => onToggle(file.name)}
      className={`w-full flex items-start gap-2.5 px-3 py-2 text-left rounded-lg transition-colors text-xs ${
        selected
          ? "bg-[#58a6ff]/10 border border-[#58a6ff]/30 text-[#e6edf3]"
          : "hover:bg-[#161b22] text-[#8b949e] hover:text-[#e6edf3]"
      }`}
    >
      <FileText
        size={13}
        className={`shrink-0 mt-0.5 ${selected ? "text-[#58a6ff]" : ""}`}
      />
      <div className="flex-1 min-w-0">
        <div className="flex items-center gap-1.5 truncate">
          <span className="truncate font-mono">{file.name}</span>
          {file.isActive && (
            <span className="shrink-0 flex items-center gap-0.5 text-[#3fb950]">
              <Zap size={9} />
              <span className="text-[9px]">live</span>
            </span>
          )}
        </div>
        <div className="flex items-center gap-2 mt-0.5 text-[#484f58]">
          <span className="flex items-center gap-0.5">
            <HardDrive size={9} />
            {file.sizeFormatted}
          </span>
          <span className="flex items-center gap-0.5">
            <Clock size={9} />
            {formatDistanceToNow(new Date(file.lastModified), {
              addSuffix: true,
            })}
          </span>
        </div>
      </div>
    </button>
  );
}

export function FileBrowser() {
  const {
    data: files = [],
    refetch,
    isLoading,
  } = useQuery({
    queryKey: ["files"],
    queryFn: () => api.getFiles(),
    refetchInterval: 30_000,
  });

  const { filters, setFilter, setFiles } = useAppStore();

  // Sync to store
  useEffect(() => {
    if (files.length > 0) setFiles(files);
  }, [files, setFiles]);

  const totalSize = files.reduce((sum, f) => sum + f.sizeBytes, 0);
  const activeCount = files.filter((f) => f.isActive).length;

  function toggleFile(name: string) {
    const current = filters.selectedFiles;
    setFilter(
      "selectedFiles",
      current.includes(name)
        ? current.filter((f) => f !== name)
        : [...current, name],
    );
  }

  function selectAll() {
    setFilter("selectedFiles", []);
  }

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center justify-between px-3 py-2 border-b border-[#21262d]">
        <span className="text-xs font-semibold text-[#8b949e] uppercase tracking-wide">
          Log Files
        </span>
        <button
          onClick={() => refetch()}
          className="text-[#8b949e] hover:text-[#e6edf3] transition-colors"
          aria-label="Refresh files"
        >
          <RefreshCw size={13} className={isLoading ? "animate-spin" : ""} />
        </button>
      </div>

      <div className="px-3 py-2 border-b border-[#21262d] text-[10px] text-[#484f58] flex gap-3">
        <span>{files.length} files</span>
        <span>{(totalSize / 1024 / 1024).toFixed(1)} MB total</span>
        {activeCount > 0 && (
          <span className="text-[#3fb950]">{activeCount} active</span>
        )}
      </div>

      <div className="flex-1 overflow-y-auto py-1 px-2 space-y-0.5">
        <button
          onClick={selectAll}
          className={`w-full px-3 py-1.5 text-xs text-left rounded-lg transition-colors ${
            filters.selectedFiles.length === 0
              ? "text-[#58a6ff] bg-[#58a6ff]/10"
              : "text-[#8b949e] hover:text-[#e6edf3] hover:bg-[#161b22]"
          }`}
        >
          All files
        </button>
        {files.map((file) => (
          <FileRow
            key={file.name}
            file={file}
            selected={filters.selectedFiles.includes(file.name)}
            onToggle={toggleFile}
          />
        ))}
      </div>
    </div>
  );
}
