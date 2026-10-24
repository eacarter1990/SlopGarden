import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { notifications } from '@mantine/notifications'
import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react'

type Handler = (payload: any) => void

interface LiveContextValue {
  connected: boolean
  on: (event: string, handler: Handler) => () => void
}

const LiveContext = createContext<LiveContextValue>({ connected: false, on: () => () => {} })

const invalidations: Record<string, string[][]> = {
  readings: [['overview'], ['sensors'], ['plant'], ['device']],
  actuatorState: [['overview'], ['actuators'], ['plant'], ['device'], ['actuator-history']],
  deviceStatus: [['overview'], ['devices'], ['device']],
  aiActivity: [['overview'], ['ai-activities']],
  proposal: [['proposals'], ['overview']],
  emergencyStop: [['overview'], ['estops']],
  cameraFrame: [['camera-frames']],
  notification: [['notifications'], ['overview']],
}

const areaKeys: Record<string, string[][]> = {
  devices: [['devices'], ['device']],
  sensors: [['sensors'], ['device'], ['plant'], ['overview']],
  actuators: [['actuators'], ['device'], ['plant'], ['overview']],
  cameras: [['cameras'], ['device'], ['plant'], ['overview']],
  schedules: [['schedules']],
  safety: [['safety'], ['estops'], ['overview']],
  notifications: [['notifications'], ['overview']],
  commands: [['actuator-history']],
  'actuator-runs': [['actuator-history']],
}

/** One SignalR connection per tab; pages subscribe to events and queries refresh automatically. */
export function LiveProvider({ children }: { children: ReactNode }) {
  const qc = useQueryClient()
  const [connected, setConnected] = useState(false)
  const handlers = useRef(new Map<string, Set<Handler>>())
  const connRef = useRef<HubConnection | null>(null)
  const pending = useRef(new Set<string>())
  const timer = useRef<number | null>(null)

  useEffect(() => {
    const flush = () => {
      timer.current = null
      for (const k of pending.current) qc.invalidateQueries({ queryKey: JSON.parse(k) })
      pending.current.clear()
    }
    const schedule = (keys: string[][]) => {
      keys.forEach((k) => pending.current.add(JSON.stringify(k)))
      if (timer.current === null) timer.current = window.setTimeout(flush, 700)
    }

    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/live')
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30000, 1000 * 2 ** ctx.previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build()
    connRef.current = conn

    const dispatch = (event: string) => (payload: unknown) => {
      handlers.current.get(event)?.forEach((h) => h(payload))
      if (invalidations[event]) schedule(invalidations[event])
      if (event === 'changed') {
        const areas: string[] = (payload as { areas?: string[] })?.areas ?? []
        if (areas.includes('all')) schedule([[]])
        areas.forEach((a) => schedule(areaKeys[a] ?? [[a]]))
      }
    }
    for (const e of [...Object.keys(invalidations), 'changed', 'chat']) conn.on(e, dispatch(e))

    conn.on('notification', (n: { title: string; body: string; severity: string; browser: boolean }) => {
      if (n.browser) {
        if ('Notification' in window && Notification.permission === 'granted' && document.hidden) new Notification(n.title, { body: n.body.replace(/[*_`>#]/g, ''), icon: '/favicon.svg' })
        return
      }
      notifications.show({
        title: n.title,
        message: n.body.replace(/[*_`]/g, '').slice(0, 220),
        color: n.severity === 'critical' ? 'red' : n.severity === 'warning' ? 'yellow' : 'green',
        autoClose: n.severity === 'critical' ? false : 6000,
      })
    })

    conn.onreconnecting(() => setConnected(false))
    conn.onreconnected(() => {
      setConnected(true)
      schedule([[]])
    })
    conn.onclose(() => setConnected(false))

    let stopped = false
    const start = async () => {
      try {
        await conn.start()
        setConnected(true)
      } catch {
        if (!stopped) window.setTimeout(start, 3000)
      }
    }
    start()
    return () => {
      stopped = true
      if (conn.state !== HubConnectionState.Disconnected) conn.stop()
    }
  }, [qc])

  const on = (event: string, handler: Handler) => {
    if (!handlers.current.has(event)) handlers.current.set(event, new Set())
    handlers.current.get(event)!.add(handler)
    return () => handlers.current.get(event)?.delete(handler)
  }

  return <LiveContext.Provider value={{ connected, on }}>{children}</LiveContext.Provider>
}

export const useLive = () => useContext(LiveContext)

export function useLiveEvent(event: string, handler: Handler) {
  const { on } = useLive()
  const ref = useRef(handler)
  ref.current = handler
  useEffect(() => on(event, (p) => ref.current(p)), [event, on])
}
