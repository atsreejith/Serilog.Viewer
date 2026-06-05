import type { LogLevel } from "@/types";

/**
 * Raw hex colour for each log level. Use these for chart strokes, fills,
 * or any place that needs a plain CSS colour value.
 */
export const LOG_LEVEL_HEX: Record<LogLevel, string> = {
  Verbose: "#8b949e",
  Debug: "#58a6ff",
  Information: "#3fb950",
  Warning: "#d29922",
  Error: "#f85149",
  Fatal: "#bc8cff",
};

/**
 * Full Tailwind + display config for each log level.
 *
 * - `label`      – short 3-char badge text
 * - `color`      – Tailwind text class
 * - `bg`         – Tailwind background class (10% opacity tint)
 * - `dot`        – raw hex (alias for LOG_LEVEL_HEX[level])
 * - `borderText` – combined Tailwind border + text classes (for toggle buttons)
 */
export const LOG_LEVEL_CONFIG: Record<
  LogLevel,
  { label: string; color: string; bg: string; dot: string; borderText: string }
> = {
  Verbose: {
    label: "VRB",
    color: "text-[#8b949e]",
    bg: "bg-[#8b949e]/10",
    dot: "#8b949e",
    borderText: "border-[#8b949e] text-[#8b949e]",
  },
  Debug: {
    label: "DBG",
    color: "text-[#58a6ff]",
    bg: "bg-[#58a6ff]/10",
    dot: "#58a6ff",
    borderText: "border-[#58a6ff] text-[#58a6ff]",
  },
  Information: {
    label: "INF",
    color: "text-[#3fb950]",
    bg: "bg-[#3fb950]/10",
    dot: "#3fb950",
    borderText: "border-[#3fb950] text-[#3fb950]",
  },
  Warning: {
    label: "WRN",
    color: "text-[#d29922]",
    bg: "bg-[#d29922]/10",
    dot: "#d29922",
    borderText: "border-[#d29922] text-[#d29922]",
  },
  Error: {
    label: "ERR",
    color: "text-[#f85149]",
    bg: "bg-[#f85149]/10",
    dot: "#f85149",
    borderText: "border-[#f85149] text-[#f85149]",
  },
  Fatal: {
    label: "FTL",
    color: "text-[#bc8cff]",
    bg: "bg-[#bc8cff]/10",
    dot: "#bc8cff",
    borderText: "border-[#bc8cff] text-[#bc8cff]",
  },
};
