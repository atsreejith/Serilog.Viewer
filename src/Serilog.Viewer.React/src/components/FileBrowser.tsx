import { useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { formatDistanceToNow } from "date-fns";
import {
  FileText,
  RefreshCw,
  HardDrive,
  Clock,
  Zap,
  Download,
  Trash2,
} from "lucide-react";
import { api } from "@/services/api";
import { useAppStore } from "@/store/appStore";
import type { LogFile } from "@/types";

function FileRow({
  file,
  selected,
  onToggle,
  onDownload,
  onDelete,
  canDownload,
  canDelete,
  busy,
}: {
  file: LogFile;
  selected: boolean;
  onToggle: (name: string) => void;
  onDownload: (name: string) => void;
  onDelete: (name: string) => void;
  canDownload: boolean;
  canDelete: boolean;
  busy: boolean;
}) {
  return (
    <div
      className={`group w-full flex items-stretch rounded-lg transition-colors text-xs border ${
        selected
          ? "bg-[#58a6ff]/10 border border-[#58a6ff]/30 text-[#e6edf3]"
          : "border-transparent hover:bg-[#161b22] text-[#8b949e] hover:text-[#e6edf3]"
      }`}
    >
      <button
        onClick={() => onToggle(file.name)}
        className="flex min-w-0 flex-1 items-start gap-2.5 px-3 py-2 text-left"
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

      {(canDownload || canDelete) && (
        <div className="flex shrink-0 items-center gap-0.5 pr-1.5 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity">
          {canDownload && (
            <button
              onClick={() => onDownload(file.name)}
              disabled={busy}
              className="h-6 w-6 grid place-items-center rounded text-[#8b949e] hover:text-[#e6edf3] hover:bg-[#30363d] disabled:opacity-40"
              title={`Download ${file.name}`}
              aria-label={`Download ${file.name}`}
            >
              <Download size={12} />
            </button>
          )}
          {canDelete && (
            <button
              onClick={() => onDelete(file.name)}
              disabled={busy}
              className="h-6 w-6 grid place-items-center rounded text-[#8b949e] hover:text-[#ff7b72] hover:bg-[#f85149]/10 disabled:opacity-40"
              title={`Delete ${file.name}`}
              aria-label={`Delete ${file.name}`}
            >
              <Trash2 size={12} />
            </button>
          )}
        </div>
      )}
    </div>
  );
}

export function FileBrowser() {
  const queryClient = useQueryClient();
  const [busyFile, setBusyFile] = useState<string | null>(null);
  const initializedDefaultSelection = useRef(false);
  const {
    data: files = [],
    refetch,
    isLoading,
  } = useQuery({
    queryKey: ["files"],
    queryFn: () => api.getFiles(),
    refetchInterval: 30_000,
  });

  const {
    filters,
    setFilter,
    setFiles,
    fileDownloadEnabled,
    fileDeleteEnabled,
  } = useAppStore();

  // Sync to store
  useEffect(() => {
    if (files.length > 0) setFiles(files);
  }, [files, setFiles]);

  useEffect(() => {
    if (initializedDefaultSelection.current || filters.selectedFiles.length > 0)
      return;

    const activeFiles = files.filter((file) => file.isActive);
    if (activeFiles.length === 0) return;

    initializedDefaultSelection.current = true;
    setFilter(
      "selectedFiles",
      activeFiles.map((file) => file.name),
    );
  }, [files, filters.selectedFiles.length, setFilter]);

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

  async function handleDownload(name: string) {
    setBusyFile(name);
    try {
      await api.downloadFile(name);
    } catch {
      window.alert(`Unable to download ${name}.`);
    } finally {
      setBusyFile(null);
    }
  }

  async function handleDelete(name: string) {
    const confirmed = window.confirm(`Delete log file "${name}"?`);
    if (!confirmed) return;

    setBusyFile(name);
    try {
      await api.deleteFile(name);
      if (filters.selectedFiles.includes(name)) {
        setFilter(
          "selectedFiles",
          filters.selectedFiles.filter((f) => f !== name),
        );
      }
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["files"] }),
        queryClient.invalidateQueries({ queryKey: ["logs"] }),
        queryClient.invalidateQueries({ queryKey: ["dashboard-stats"] }),
      ]);
    } catch {
      window.alert(`Unable to delete ${name}.`);
    } finally {
      setBusyFile(null);
    }
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
            onDownload={handleDownload}
            onDelete={handleDelete}
            canDownload={fileDownloadEnabled}
            canDelete={fileDeleteEnabled}
            busy={busyFile === file.name}
          />
        ))}
      </div>
    </div>
  );
}
