import type { LogLevel } from "@/types";

const LEVEL_CONFIG: Record<
  LogLevel,
  { label: string; color: string; bg: string; dot: string }
> = {
  Verbose: {
    label: "VRB",
    color: "text-[#8b949e]",
    bg: "bg-[#8b949e]/10",
    dot: "#8b949e",
  },
  Debug: {
    label: "DBG",
    color: "text-[#58a6ff]",
    bg: "bg-[#58a6ff]/10",
    dot: "#58a6ff",
  },
  Information: {
    label: "INF",
    color: "text-[#3fb950]",
    bg: "bg-[#3fb950]/10",
    dot: "#3fb950",
  },
  Warning: {
    label: "WRN",
    color: "text-[#d29922]",
    bg: "bg-[#d29922]/10",
    dot: "#d29922",
  },
  Error: {
    label: "ERR",
    color: "text-[#f85149]",
    bg: "bg-[#f85149]/10",
    dot: "#f85149",
  },
  Fatal: {
    label: "FTL",
    color: "text-[#bc8cff]",
    bg: "bg-[#bc8cff]/10",
    dot: "#bc8cff",
  },
};

interface LevelBadgeProps {
  level: LogLevel;
  className?: string;
}

export function LevelBadge({ level, className = "" }: LevelBadgeProps) {
  const config = LEVEL_CONFIG[level] ?? LEVEL_CONFIG.Information;
  return (
    <span
      className={`inline-flex items-center px-1.5 py-0.5 rounded text-xs font-mono font-semibold ${config.color} ${config.bg} ${className}`}
    >
      {config.label}
    </span>
  );
}

export function getLevelColor(level: LogLevel): string {
  return LEVEL_CONFIG[level]?.dot ?? "#3fb950";
}

export function getLevelTextColor(level: LogLevel): string {
  return LEVEL_CONFIG[level]?.color ?? "text-[#3fb950]";
}
