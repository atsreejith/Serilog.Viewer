import { create } from 'zustand'
import type { LogEntry, LogFile, LogLevel, LogQuery } from '@/types'

interface FilterState {
  selectedFiles: string[]
  from: string
  to: string
  levels: LogLevel[]
  searchText: string
  sourceContext: string
  correlationId: string
  requestId: string
  page: number
  pageSize: number
  sortBy: string
  sortDescending: boolean
}

interface AppState {
  // App config
  liveTailEnabled: boolean
  setLiveTailEnabled: (enabled: boolean) => void

  // File browser
  files: LogFile[]
  setFiles: (files: LogFile[]) => void

  // Filters
  filters: FilterState
  setFilter: <K extends keyof FilterState>(key: K, value: FilterState[K]) => void
  resetFilters: () => void
  toQuery: () => LogQuery

  // Log details drawer
  selectedEntry: LogEntry | null
  setSelectedEntry: (entry: LogEntry | null) => void

  // Live tail
  liveTailActive: boolean
  liveTailPaused: boolean
  liveTailEntries: LogEntry[]
  setLiveTailActive: (active: boolean) => void
  setLiveTailPaused: (paused: boolean) => void
  addLiveTailEntry: (entry: LogEntry) => void
  clearLiveTailEntries: () => void
}

const defaultFilters: FilterState = {
  selectedFiles: [],
  from: '',
  to: '',
  levels: [],
  searchText: '',
  sourceContext: '',
  correlationId: '',
  requestId: '',
  page: 1,
  pageSize: 100,
  sortBy: 'Timestamp',
  sortDescending: true,
}

export const useAppStore = create<AppState>((set, get) => ({
  liveTailEnabled: false,
  setLiveTailEnabled: (enabled) => set({ liveTailEnabled: enabled }),

  files: [],
  setFiles: (files) => set({ files }),

  filters: { ...defaultFilters },
  setFilter: (key, value) =>
    set((state) => ({
      filters: {
        ...state.filters,
        [key]: value,
        ...(key !== 'page' ? { page: 1 } : {}),
      },
    })),
  resetFilters: () => set({ filters: { ...defaultFilters } }),
  toQuery: (): LogQuery => {
    const f = get().filters
    return {
      fileNames: f.selectedFiles.length ? f.selectedFiles : undefined,
      from: f.from || undefined,
      to: f.to || undefined,
      levels: f.levels.length ? f.levels : undefined,
      searchText: f.searchText || undefined,
      sourceContext: f.sourceContext || undefined,
      correlationId: f.correlationId || undefined,
      requestId: f.requestId || undefined,
      page: f.page,
      pageSize: f.pageSize,
      sortBy: f.sortBy,
      sortDescending: f.sortDescending,
    }
  },

  selectedEntry: null,
  setSelectedEntry: (entry) => set({ selectedEntry: entry }),

  liveTailActive: false,
  liveTailPaused: false,
  liveTailEntries: [],
  setLiveTailActive: (active) => set({ liveTailActive: active }),
  setLiveTailPaused: (paused) => set({ liveTailPaused: paused }),
  addLiveTailEntry: (entry) =>
    set((state) => ({
      liveTailEntries: state.liveTailPaused
        ? state.liveTailEntries
        : [entry, ...state.liveTailEntries].slice(0, 5000),
    })),
  clearLiveTailEntries: () => set({ liveTailEntries: [] }),
}))
