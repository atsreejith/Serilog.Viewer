import type { LogLevel } from "@/types";
import { LOG_LEVEL_CONFIG as LEVEL_CONFIG } from "@/constants/logLevels";

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
