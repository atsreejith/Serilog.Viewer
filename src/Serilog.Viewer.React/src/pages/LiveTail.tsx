import { useEffect, useRef } from "react";
import {
  Play,
  Pause,
  Square,
  Trash2,
  Wifi,
  WifiOff,
  PackageOpen,
} from "lucide-react";
import { useAppStore } from "@/store/appStore";
import { liveTailService } from "@/services/liveTail";
import { LevelBadge } from "@/components/LevelBadge";
import { Timestamp } from "@/components/Timestamp";
import { LogDetailsDrawer } from "@/components/LogDetailsDrawer";

export function LiveTail() {
  const liveTailEnabled = useAppStore((s) => s.liveTailEnabled);
  const {
    liveTailActive,
    liveTailPaused,
    liveTailEntries,
    setLiveTailActive,
    setLiveTailPaused,
    addLiveTailEntry,
    clearLiveTailEntries,
    selectedEntry,
    setSelectedEntry,
  } = useAppStore();

  const scrollRef = useRef<HTMLDivElement>(null);
  const followRef = useRef(true);

  // Connect / disconnect
  useEffect(() => {
    if (liveTailActive) {
      liveTailService.connect().then(() => liveTailService.subscribeToAll());
      const off = liveTailService.onNewEntry((entry) => {
        addLiveTailEntry(entry);
        if (followRef.current && scrollRef.current) {
          scrollRef.current.scrollTop = 0;
        }
      });
      return () => {
        off();
        liveTailService.unsubscribeFromAll();
      };
    }
  }, [liveTailActive, addLiveTailEntry]);

  async function handleStart() {
    setLiveTailActive(true);
    setLiveTailPaused(false);
  }

  async function handleStop() {
    setLiveTailActive(false);
    setLiveTailPaused(false);
    await liveTailService.disconnect();
  }

  function handleTogglePause() {
    setLiveTailPaused(!liveTailPaused);
  }

  if (!liveTailEnabled) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-3 text-[#484f58]">
        <PackageOpen size={40} />
        <p className="text-sm font-medium text-[#8b949e]">
          Live Tail is not enabled
        </p>
        <p className="text-xs text-center max-w-xs">
          Install{" "}
          <code className="text-[#58a6ff]">Serilog.Viewer.Realtime</code> and
          call <code className="text-[#58a6ff]">.AddLogViewerRealtime()</code>{" "}
          in your host setup to enable real-time log streaming.
        </p>
      </div>
    );
  }

  return (
    <div className="flex h-full overflow-hidden">
      <div className="flex-1 flex flex-col overflow-hidden">
        {/* Toolbar */}
        <div className="flex items-center gap-3 px-4 py-2.5 border-b border-[#21262d] bg-[#0d1117] shrink-0">
          <div className="flex items-center gap-1.5">
            <div
              className={`w-2 h-2 rounded-full ${liveTailActive && !liveTailPaused ? "bg-[#3fb950] animate-pulse" : liveTailActive ? "bg-[#d29922]" : "bg-[#484f58]"}`}
            />
            <span className="text-xs text-[#8b949e]">
              {liveTailActive && !liveTailPaused
                ? "Live"
                : liveTailActive
                  ? "Paused"
                  : "Stopped"}
            </span>
          </div>

          {!liveTailActive ? (
            <button
              onClick={handleStart}
              className="flex items-center gap-1.5 text-xs bg-[#3fb950] hover:bg-[#3fb950]/80 text-[#0d1117] font-semibold px-3 py-1.5 rounded-lg transition-colors"
            >
              <Wifi size={13} /> Start Live Tail
            </button>
          ) : (
            <>
              <button
                onClick={handleTogglePause}
                className="flex items-center gap-1.5 text-xs bg-[#161b22] hover:bg-[#21262d] text-[#e6edf3] border border-[#30363d] px-3 py-1.5 rounded-lg transition-colors"
              >
                {liveTailPaused ? <Play size={13} /> : <Pause size={13} />}
                {liveTailPaused ? "Resume" : "Pause"}
              </button>
              <button
                onClick={handleStop}
                className="flex items-center gap-1.5 text-xs text-[#f85149] hover:text-[#ff7b72] border border-[#f85149]/30 hover:border-[#f85149] px-3 py-1.5 rounded-lg transition-colors"
              >
                <Square size={13} /> Stop
              </button>
            </>
          )}

          <button
            onClick={clearLiveTailEntries}
            className="flex items-center gap-1.5 text-xs text-[#8b949e] hover:text-[#e6edf3] transition-colors ml-auto"
          >
            <Trash2 size={13} /> Clear (
            {liveTailEntries.length.toLocaleString()})
          </button>
        </div>

        {/* Empty state */}
        {!liveTailActive && liveTailEntries.length === 0 && (
          <div className="flex flex-col items-center justify-center flex-1 gap-3 text-[#484f58]">
            <WifiOff size={40} />
            <p className="text-sm">
              Start Live Tail to stream log entries in real time.
            </p>
          </div>
        )}

        {/* Log stream */}
        <div
          ref={scrollRef}
          className="flex-1 overflow-y-auto font-mono text-xs"
        >
          {liveTailEntries.map((entry) => (
            <div
              key={entry.id}
              onClick={() =>
                setSelectedEntry(selectedEntry?.id === entry.id ? null : entry)
              }
              className={`flex items-start gap-3 px-4 py-1.5 border-b border-[#21262d] cursor-pointer transition-colors ${
                selectedEntry?.id === entry.id
                  ? "bg-[#58a6ff]/10"
                  : "hover:bg-[#161b22]"
              }`}
            >
              <Timestamp value={entry.timestamp} className="shrink-0 w-44" />
              <LevelBadge level={entry.level} className="shrink-0" />
              <span className="text-[#8b949e] shrink-0 w-40 truncate">
                {entry.sourceContext ?? ""}
              </span>
              <span
                className={`flex-1 truncate ${entry.level === "Error" || entry.level === "Fatal" ? "text-[#f85149]" : "text-[#e6edf3]"}`}
              >
                {entry.renderedMessage ?? entry.message}
              </span>
            </div>
          ))}
        </div>
      </div>

      <LogDetailsDrawer
        entry={selectedEntry}
        onClose={() => setSelectedEntry(null)}
      />
    </div>
  );
}
