import { Search, X, Filter } from "lucide-react";
import { useAppStore } from "@/store/appStore";
import type { LogLevel } from "@/types";

const LEVELS: LogLevel[] = [
  "Verbose",
  "Debug",
  "Information",
  "Warning",
  "Error",
  "Fatal",
];

const LEVEL_COLORS: Record<LogLevel, string> = {
  Verbose: "border-[#8b949e] text-[#8b949e]",
  Debug: "border-[#58a6ff] text-[#58a6ff]",
  Information: "border-[#3fb950] text-[#3fb950]",
  Warning: "border-[#d29922] text-[#d29922]",
  Error: "border-[#f85149] text-[#f85149]",
  Fatal: "border-[#bc8cff] text-[#bc8cff]",
};

export function FilterBar() {
  const { filters, setFilter, resetFilters } = useAppStore();

  const hasFilters =
    filters.searchText ||
    filters.levels.length ||
    filters.from ||
    filters.to ||
    filters.sourceContext ||
    filters.correlationId ||
    filters.requestId;

  function toggleLevel(level: LogLevel) {
    const current = filters.levels;
    setFilter(
      "levels",
      current.includes(level)
        ? current.filter((l) => l !== level)
        : [...current, level],
    );
  }

  return (
    <div className="flex flex-wrap items-center gap-2 px-4 py-2.5 border-b border-[#21262d] bg-[#0d1117]">
      {/* Search */}
      <div className="relative flex-1 min-w-48">
        <Search
          size={14}
          className="absolute left-2.5 top-1/2 -translate-y-1/2 text-[#484f58]"
        />
        <input
          type="text"
          placeholder="Search messages..."
          value={filters.searchText}
          onChange={(e) => setFilter("searchText", e.target.value)}
          className="w-full bg-[#161b22] border border-[#30363d] rounded-lg pl-8 pr-3 py-1.5 text-xs text-[#e6edf3] placeholder:text-[#484f58] focus:outline-none focus:border-[#58a6ff] transition-colors"
        />
      </div>

      {/* Level toggles */}
      <div className="flex items-center gap-1">
        {LEVELS.map((level) => (
          <button
            key={level}
            onClick={() => toggleLevel(level)}
            className={`px-2 py-1 text-[10px] font-mono font-semibold rounded border transition-colors ${
              filters.levels.includes(level)
                ? `${LEVEL_COLORS[level]} bg-current/10`
                : "border-[#30363d] text-[#484f58] hover:border-[#8b949e] hover:text-[#8b949e]"
            }`}
          >
            {level.slice(0, 3).toUpperCase()}
          </button>
        ))}
      </div>

      {/* Date range */}
      <input
        type="datetime-local"
        value={filters.from}
        onChange={(e) => setFilter("from", e.target.value)}
        className="bg-[#161b22] border border-[#30363d] rounded-lg px-2.5 py-1.5 text-xs text-[#e6edf3] focus:outline-none focus:border-[#58a6ff] transition-colors"
        title="From"
      />
      <input
        type="datetime-local"
        value={filters.to}
        onChange={(e) => setFilter("to", e.target.value)}
        className="bg-[#161b22] border border-[#30363d] rounded-lg px-2.5 py-1.5 text-xs text-[#e6edf3] focus:outline-none focus:border-[#58a6ff] transition-colors"
        title="To"
      />

      {/* Source context */}
      <input
        type="text"
        placeholder="Source context"
        value={filters.sourceContext}
        onChange={(e) => setFilter("sourceContext", e.target.value)}
        className="bg-[#161b22] border border-[#30363d] rounded-lg px-2.5 py-1.5 text-xs text-[#e6edf3] placeholder:text-[#484f58] focus:outline-none focus:border-[#58a6ff] transition-colors w-40"
      />

      {/* Correlation ID */}
      <input
        type="text"
        placeholder="Correlation ID"
        value={filters.correlationId}
        onChange={(e) => setFilter("correlationId", e.target.value)}
        className="bg-[#161b22] border border-[#30363d] rounded-lg px-2.5 py-1.5 text-xs text-[#e6edf3] placeholder:text-[#484f58] focus:outline-none focus:border-[#58a6ff] transition-colors w-36"
      />

      {/* Request ID */}
      <input
        type="text"
        placeholder="Request ID"
        value={filters.requestId}
        onChange={(e) => setFilter("requestId", e.target.value)}
        className="bg-[#161b22] border border-[#30363d] rounded-lg px-2.5 py-1.5 text-xs text-[#e6edf3] placeholder:text-[#484f58] focus:outline-none focus:border-[#58a6ff] transition-colors w-32"
      />

      {/* Reset */}
      {hasFilters && (
        <button
          onClick={resetFilters}
          className="flex items-center gap-1 text-xs text-[#f85149] hover:text-[#ff7b72] transition-colors border border-[#f85149]/30 rounded-lg px-2 py-1.5"
        >
          <X size={12} />
          Clear
        </button>
      )}

      <div className="ml-auto flex items-center gap-1 text-[10px] text-[#484f58]">
        <Filter size={10} />
        <span>{hasFilters ? "Filtered" : "All logs"}</span>
      </div>
    </div>
  );
}
