import type { LogEntry, LogFile, LogQuery, PagedResult, DashboardStats } from '@/types'

const BASE = '/logviewer/api'

async function fetchJson<T>(url: string, options?: RequestInit): Promise<T> {
  const res = await fetch(url, options)
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`)
  return res.json() as Promise<T>
}

function buildParams(query: LogQuery): URLSearchParams {
  const params = new URLSearchParams()
  if (query.fileName) params.set('file', query.fileName)
  if (query.fileNames?.length) query.fileNames.forEach(f => params.append('files', f))
  if (query.from) params.set('from', query.from)
  if (query.to) params.set('to', query.to)
  if (query.levels?.length) params.set('level', query.levels.join(','))
  if (query.searchText) params.set('search', query.searchText)
  if (query.sourceContext) params.set('sourceContext', query.sourceContext)
  if (query.correlationId) params.set('correlationId', query.correlationId)
  if (query.requestId) params.set('requestId', query.requestId)
  if (query.page) params.set('page', String(query.page))
  if (query.pageSize) params.set('pageSize', String(query.pageSize))
  if (query.sortBy) params.set('sortBy', query.sortBy)
  if (query.sortDescending !== undefined)
    params.set('sortDir', query.sortDescending ? 'desc' : 'asc')
  return params
}

export const api = {
  getFiles(): Promise<LogFile[]> {
    return fetchJson(`${BASE}/files`)
  },

  getLogs(query: LogQuery): Promise<PagedResult<LogEntry>> {
    return fetchJson(`${BASE}/logs?${buildParams(query)}`)
  },

  getStats(fileNames?: string[]): Promise<DashboardStats> {
    const params = new URLSearchParams()
    fileNames?.forEach(f => params.append('files', f))
    return fetchJson(`${BASE}/logs/stats?${params}`)
  },

  getDetails(fileName: string, offset: number): Promise<LogEntry> {
    return fetchJson(`${BASE}/details?fileName=${encodeURIComponent(fileName)}&offset=${offset}`)
  },

  async exportCsv(query: LogQuery): Promise<void> {
    const res = await fetch(`${BASE}/export/csv?${buildParams(query)}`, { method: 'POST' })
    if (!res.ok) throw new Error('Export failed')
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `logs-${Date.now()}.csv`
    a.click()
    URL.revokeObjectURL(url)
  },

  getConfig(): Promise<{ liveTailEnabled: boolean }> {
    return fetchJson(`${BASE}/config`)
  },

  async exportJson(query: LogQuery): Promise<void> {
    const res = await fetch(`${BASE}/export/json?${buildParams(query)}`, { method: 'POST' })
    if (!res.ok) throw new Error('Export failed')
    const blob = await res.blob()
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `logs-${Date.now()}.json`
    a.click()
    URL.revokeObjectURL(url)
  },
}
