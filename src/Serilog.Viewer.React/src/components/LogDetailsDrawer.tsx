import { X, Copy, ChevronDown, ChevronRight } from "lucide-react";
import { useState } from "react";
import type { LogEntry } from "@/types";
import { LevelBadge } from "./LevelBadge";
import { Timestamp } from "./Timestamp";

interface LogDetailsDrawerProps {
  entry: LogEntry | null;
  onClose: () => void;
}

function Section({
  title,
  children,
  defaultOpen = true,
}: {
  title: string;
  children: React.ReactNode;
  defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div className="border border-[#30363d] rounded-lg overflow-hidden">
      <button
        className="w-full flex items-center justify-between px-4 py-2.5 bg-[#161b22] hover:bg-[#1c2128] text-sm font-semibold text-[#e6edf3] transition-colors"
        onClick={() => setOpen(!open)}
      >
        {title}
        {open ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
      </button>
      {open && <div className="p-4 bg-[#0d1117] text-sm">{children}</div>}
    </div>
  );
}

function copyToClipboard(text: string) {
  navigator.clipboard.writeText(text).catch(() => {});
}

export function LogDetailsDrawer({ entry, onClose }: LogDetailsDrawerProps) {
  if (!entry) return null;

  const props = Object.entries(entry.properties ?? {}).filter(
    ([k]) =>
      ![
        "SourceContext",
        "CorrelationId",
        "RequestId",
        "TraceId",
        "SpanId",
      ].includes(k),
  );

  return (
    <aside className="fixed inset-y-0 right-0 w-[480px] max-w-full bg-[#0d1117] border-l border-[#30363d] shadow-2xl z-50 flex flex-col">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-3 border-b border-[#30363d] shrink-0">
        <div className="flex items-center gap-2">
          <LevelBadge level={entry.level} />
          <Timestamp value={entry.timestamp} />
        </div>
        <button
          onClick={onClose}
          className="text-[#8b949e] hover:text-[#e6edf3] transition-colors"
          aria-label="Close"
        >
          <X size={18} />
        </button>
      </div>

      {/* Body */}
      <div className="flex-1 overflow-y-auto p-4 space-y-3">
        {/* Message */}
        <Section title="Message">
          <p className="text-[#e6edf3] leading-relaxed font-mono text-xs break-words whitespace-pre-wrap">
            {entry.message}
          </p>
        </Section>

        {/* Metadata */}
        <Section title="Metadata">
          <dl className="space-y-1.5">
            {[
              ["File", entry.fileName],
              ["Source", entry.sourceContext],
              ["Correlation ID", entry.correlationId],
              ["Request ID", entry.requestId],
              ["Trace ID", entry.traceId],
              ["Span ID", entry.spanId],
            ].map(([label, value]) =>
              value ? (
                <div key={label} className="flex gap-2 text-xs">
                  <dt className="w-28 shrink-0 text-[#8b949e]">{label}</dt>
                  <dd className="text-[#e6edf3] font-mono break-all">
                    {value}
                  </dd>
                </div>
              ) : null,
            )}
          </dl>
        </Section>

        {/* Structured Properties */}
        {props.length > 0 && (
          <Section title={`Properties (${props.length})`}>
            <dl className="space-y-1.5">
              {props.map(([key, value]) => (
                <div key={key} className="flex gap-2 text-xs">
                  <dt className="w-36 shrink-0 text-[#8b949e] font-mono">
                    {key}
                  </dt>
                  <dd className="text-[#e6edf3] font-mono break-all">
                    {JSON.stringify(value)}
                  </dd>
                </div>
              ))}
            </dl>
          </Section>
        )}

        {/* Exception */}
        {entry.exception && (
          <Section title="Exception">
            <pre className="text-[#f85149] text-xs font-mono whitespace-pre-wrap break-words leading-relaxed">
              {entry.exception}
            </pre>
          </Section>
        )}

        {/* Raw JSON */}
        <Section title="Raw JSON" defaultOpen={false}>
          <div className="relative">
            <button
              onClick={() => copyToClipboard(entry.rawJson)}
              className="absolute top-2 right-2 flex items-center gap-1 text-xs text-[#8b949e] hover:text-[#e6edf3] bg-[#161b22] border border-[#30363d] px-2 py-1 rounded transition-colors"
            >
              <Copy size={12} />
              Copy
            </button>
            <pre className="text-[#e6edf3] text-xs font-mono whitespace-pre-wrap break-words bg-[#161b22] rounded-lg p-4 pr-20 leading-relaxed overflow-x-auto">
              {(() => {
                try {
                  return JSON.stringify(JSON.parse(entry.rawJson), null, 2);
                } catch {
                  return entry.rawJson;
                }
              })()}
            </pre>
          </div>
        </Section>
      </div>
    </aside>
  );
}
