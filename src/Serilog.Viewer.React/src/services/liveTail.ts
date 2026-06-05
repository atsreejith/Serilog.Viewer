import * as signalR from '@microsoft/signalr'
import type { LogEntry } from '@/types'

let connection: signalR.HubConnection | null = null

function getConnection(): signalR.HubConnection {
  if (!connection) {
    connection = new signalR.HubConnectionBuilder()
      .withUrl('/logviewer/hubs/logtail')
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build()
  }
  return connection
}

export const liveTailService = {
  async connect(): Promise<void> {
    const conn = getConnection()
    if (conn.state === signalR.HubConnectionState.Disconnected) {
      await conn.start()
    }
  },

  async disconnect(): Promise<void> {
    if (connection?.state !== signalR.HubConnectionState.Disconnected) {
      await connection?.stop()
    }
  },

  async subscribeToAll(): Promise<void> {
    await getConnection().invoke('SubscribeToAll')
  },

  async unsubscribeFromAll(): Promise<void> {
    await getConnection().invoke('UnsubscribeFromAll')
  },

  async subscribeToFile(fileName: string): Promise<void> {
    await getConnection().invoke('SubscribeToFile', fileName)
  },

  async unsubscribeFromFile(fileName: string): Promise<void> {
    await getConnection().invoke('UnsubscribeFromFile', fileName)
  },

  onNewEntry(handler: (entry: LogEntry) => void): () => void {
    const conn = getConnection()
    conn.on('NewLogEntry', handler)
    return () => conn.off('NewLogEntry', handler)
  },
}
