export type LogLevel = 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal'

export interface LogEntry {
  id: string
  timestamp: string
  level: LogLevel
  message: string
  renderedMessage?: string
  exception?: string
  sourceContext?: string
  correlationId?: string
  requestId?: string
  traceId?: string
  spanId?: string
  properties: Record<string, unknown>
  rawJson: string
  lineOffset: number
  fileName: string
}

export interface LogFile {
  name: string
  sizeBytes: number
  sizeFormatted: string
  lastModified: string
  isActive: boolean
  format: 'Clef' | 'PlainText'
  lineCount: number
}

export interface PagedResult<T> {
  items: T[]
  totalCount: number
  page: number
  pageSize: number
  hasNextPage: boolean
  performance?: QueryPerformanceMetrics
}

export interface QueryPerformanceMetrics {
  durationMs: number
  serverPeakMemoryBytes: number
  serverPeakMemoryFormatted: string
}

export interface LogQuery {
  fileName?: string
  fileNames?: string[]
  from?: string
  to?: string
  levels?: LogLevel[]
  searchText?: string
  sourceContext?: string
  correlationId?: string
  requestId?: string
  page?: number
  pageSize?: number
  sortBy?: string
  sortDescending?: boolean
}

export interface DashboardStats {
  totalLogs: number
  errors: number
  warnings: number
  fatals: number
  verboses: number
  debugs: number
  informations: number
  activeFiles: number
  totalFileSizeBytes: number
  errorsByHour: TimeSeriesPoint[]
  logsByDay: TimeSeriesPoint[]
  logsByLevel: LevelDistributionPoint[]
  topSources: SourceDistributionPoint[]
}

export interface TimeSeriesPoint {
  timestamp: string
  count: number
}

export interface LevelDistributionPoint {
  level: string
  count: number
  color: string
}

export interface SourceDistributionPoint {
  source: string
  count: number
}
