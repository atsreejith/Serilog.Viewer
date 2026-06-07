import { useRef, useCallback, useMemo } from "react";
import { useInfiniteQuery } from "@tanstack/react-query";
import {
  createColumnHelper,
  flexRender,
  getCoreRowModel,
  useReactTable,
} from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import {
  ArrowUpDown,
  ChevronUp,
  ChevronDown,
  Download,
  Timer,
  MemoryStick,
} from "lucide-react";
import type { LogEntry } from "@/types";
import { LevelBadge } from "./LevelBadge";
import { Timestamp } from "./Timestamp";
import { useAppStore } from "@/store/appStore";
import { api } from "@/services/api";

const columnHelper = createColumnHelper<LogEntry>();

const ROW_HEIGHT = 36;

export function LogGrid() {
  const parentRef = useRef<HTMLDivElement>(null);
  const { filters, setFilter, toQuery, setSelectedEntry, selectedEntry } =
    useAppStore();

  const query = toQuery();

  const {
    data,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
    isLoading,
    isError,
  } = useInfiniteQuery({
    queryKey: ["logs", query],
    queryFn: ({ pageParam = 1 }) =>
      api.getLogs({ ...query, page: pageParam as number }),
    initialPageParam: 1,
    getNextPageParam: (lastPage) =>
      lastPage.hasNextPage ? lastPage.page + 1 : undefined,
  });

  const allRows = useMemo<LogEntry[]>(
    () => data?.pages.flatMap((p) => p.items) ?? [],
    [data],
  );
  const totalCount = data?.pages[0]?.totalCount ?? 0;
  const latestPage =
    data?.pages && data.pages.length > 0
      ? data.pages[data.pages.length - 1]
      : undefined;
  const performance = latestPage?.performance;

  function handleSort(col: string) {
    if (filters.sortBy === col) {
      setFilter("sortDescending", !filters.sortDescending);
    } else {
      setFilter("sortBy", col);
      setFilter("sortDescending", true);
    }
  }

  function SortIcon({ col }: { col: string }) {
    if (filters.sortBy !== col)
      return <ArrowUpDown size={11} className="opacity-30" />;
    return filters.sortDescending ? (
      <ChevronDown size={11} className="text-[#58a6ff]" />
    ) : (
      <ChevronUp size={11} className="text-[#58a6ff]" />
    );
  }

  const columns = [
    columnHelper.accessor("timestamp", {
      header: () => (
        <button
          className="flex items-center gap-1"
          onClick={() => handleSort("Timestamp")}
        >
          Timestamp <SortIcon col="Timestamp" />
        </button>
      ),
      size: 190,
      cell: (info) => <Timestamp value={info.getValue()} />,
    }),
    columnHelper.accessor("level", {
      header: () => (
        <button
          className="flex items-center gap-1"
          onClick={() => handleSort("Level")}
        >
          Level <SortIcon col="Level" />
        </button>
      ),
      size: 60,
      cell: (info) => <LevelBadge level={info.getValue()} />,
    }),
    columnHelper.accessor("message", {
      header: () => (
        <button
          className="flex items-center gap-1"
          onClick={() => handleSort("Message")}
        >
          Message <SortIcon col="Message" />
        </button>
      ),
      size: 600,
      cell: (info) => (
        <span className="truncate block text-[#e6edf3] font-mono text-xs">
          {info.getValue()}
        </span>
      ),
    }),
    columnHelper.accessor("sourceContext", {
      header: "Source",
      size: 200,
      cell: (info) => (
        <span className="truncate block text-[#8b949e] font-mono text-xs">
          {info.getValue() ?? "—"}
        </span>
      ),
    }),
    columnHelper.accessor("correlationId", {
      header: "Correlation ID",
      size: 140,
      cell: (info) => (
        <span className="truncate block text-[#8b949e] font-mono text-xs">
          {info.getValue() ?? "—"}
        </span>
      ),
    }),
  ];

  const table = useReactTable({
    data: allRows,
    columns,
    getCoreRowModel: getCoreRowModel(),
    columnResizeMode: "onChange",
  });

  const rows = table.getRowModel().rows;
  const rowVirtualizer = useVirtualizer({
    count: rows.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 20,
  });

  const virtualItems = rowVirtualizer.getVirtualItems();
  const totalHeight = rowVirtualizer.getTotalSize();

  const handleScroll = useCallback(
    (e: React.UIEvent<HTMLDivElement>) => {
      const { scrollTop, scrollHeight, clientHeight } = e.currentTarget;
      if (
        scrollHeight - scrollTop - clientHeight < 200 &&
        hasNextPage &&
        !isFetchingNextPage
      ) {
        fetchNextPage();
      }
    },
    [fetchNextPage, hasNextPage, isFetchingNextPage],
  );

  return (
    <div className="flex flex-col h-full">
      {/* Toolbar */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-[#21262d] bg-[#0d1117] shrink-0">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-[#8b949e]">
          {isLoading ? (
            <span className="animate-pulse">Loading…</span>
          ) : (
            <span>
              <span className="text-[#e6edf3] font-semibold">
                {allRows.length.toLocaleString()}
              </span>
              <span> of </span>
              <span className="text-[#e6edf3] font-semibold">
                {totalCount.toLocaleString()}
              </span>
              <span> entries</span>
            </span>
          )}
          {!isLoading && performance && (
            <span className="flex items-center gap-2 text-[11px] text-[#6e7681]">
              <span className="flex items-center gap-1">
                <Timer size={11} />
                {performance.durationMs.toLocaleString()} ms
              </span>
              <span className="flex items-center gap-1">
                <MemoryStick size={11} />
                {performance.serverPeakMemoryFormatted}
              </span>
            </span>
          )}
        </div>
        <div className="flex items-center gap-2">
          <button
            onClick={() => api.exportCsv(query)}
            className="flex items-center gap-1 text-xs text-[#8b949e] hover:text-[#e6edf3] border border-[#30363d] hover:border-[#8b949e] rounded px-2 py-1 transition-colors"
          >
            <Download size={11} /> CSV
          </button>
          <button
            onClick={() => api.exportJson(query)}
            className="flex items-center gap-1 text-xs text-[#8b949e] hover:text-[#e6edf3] border border-[#30363d] hover:border-[#8b949e] rounded px-2 py-1 transition-colors"
          >
            <Download size={11} /> JSON
          </button>
        </div>
      </div>

      {/* Table header */}
      <div className="shrink-0 bg-[#161b22] border-b border-[#30363d]">
        {table.getHeaderGroups().map((headerGroup) => (
          <div key={headerGroup.id} className="flex">
            {headerGroup.headers.map((header) => (
              <div
                key={header.id}
                className="px-3 py-2 text-xs font-semibold text-[#8b949e] uppercase tracking-wide shrink-0 flex items-center"
                style={{ width: header.getSize() }}
              >
                {flexRender(
                  header.column.columnDef.header,
                  header.getContext(),
                )}
              </div>
            ))}
          </div>
        ))}
      </div>

      {/* Virtualized rows */}
      <div
        ref={parentRef}
        className="flex-1 overflow-auto"
        onScroll={handleScroll}
      >
        {isError && (
          <div className="flex items-center justify-center h-32 text-[#f85149] text-sm">
            Failed to load logs. Check API connectivity.
          </div>
        )}

        {!isLoading && allRows.length === 0 && !isError && (
          <div className="flex items-center justify-center h-32 text-[#484f58] text-sm">
            No log entries match your filters.
          </div>
        )}

        <div style={{ height: totalHeight, position: "relative" }}>
          {virtualItems.map((virtualItem) => {
            const row = rows[virtualItem.index];
            const isSelected = selectedEntry?.id === row.original.id;
            const isError =
              row.original.level === "Error" || row.original.level === "Fatal";

            return (
              <div
                key={row.id}
                data-index={virtualItem.index}
                onClick={() =>
                  setSelectedEntry(isSelected ? null : row.original)
                }
                className={`absolute inset-x-0 flex items-center cursor-pointer border-b border-[#21262d] transition-colors ${
                  isSelected
                    ? "bg-[#58a6ff]/10 border-l-2 border-l-[#58a6ff]"
                    : isError
                      ? "hover:bg-[#f85149]/5"
                      : "hover:bg-[#161b22]"
                }`}
                style={{
                  top: virtualItem.start,
                  height: ROW_HEIGHT,
                }}
              >
                {row.getVisibleCells().map((cell) => (
                  <div
                    key={cell.id}
                    className="px-3 overflow-hidden shrink-0 flex items-center"
                    style={{ width: cell.column.getSize() }}
                  >
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </div>
                ))}
              </div>
            );
          })}
        </div>

        {isFetchingNextPage && (
          <div className="text-center py-4 text-xs text-[#484f58] animate-pulse">
            Loading more…
          </div>
        )}
      </div>
    </div>
  );
}
