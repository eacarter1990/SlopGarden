import { Alert, Button, Collapse, NumberInput, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core'
import { useEffect, useState } from 'react'
import { api } from '../api/client'
import { useAuth } from '../auth'

export function LoginPage({ needsSetup }: { needsSetup: boolean }) {
  const { refresh, status } = useAuth()
  const db = status?.database
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [showDb, setShowDb] = useState(false)
  const [host, setHost] = useState(db?.host ?? 'db')
  const [port, setPort] = useState<number | string>(db?.port ?? 5432)
  const [database, setDatabase] = useState(db?.database ?? 'slopgarden')
  const [dbUser, setDbUser] = useState(db?.username ?? 'slopgarden')
  const [dbPassword, setDbPassword] = useState('')
  const [restarting, setRestarting] = useState(false)
  useEffect(() => {
    if (!db) return
    setHost(db.host)
    setPort(db.port)
    setDatabase(db.database)
    setDbUser(db.username)
  }, [db?.host, db?.port, db?.database, db?.username])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      if (needsSetup) await api.post('/api/auth/setup', { username, password, displayName: displayName || null })
      else await api.post('/api/auth/login', { username, password })
      await refresh()
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="sg-login">
      <div className="sg-login-card">
        <form onSubmit={submit}>
          <Stack>
            <Stack gap={4}>
              <img src="/favicon.svg" width={40} height={40} alt="" />
              <Title order={2} className="sg-serif" fw={560}>SlopGarden</Title>
              <Text c="dimmed" size="sm">{needsSetup ? 'Create the first Admin account for this server.' : 'The garden, on this network.'}</Text>
            </Stack>
            {error && <Alert color="red">{error}</Alert>}
            <TextInput label="Username" value={username} onChange={(e) => setUsername(e.currentTarget.value)} required autoFocus />
            {needsSetup && <TextInput label="Display name" value={displayName} onChange={(e) => setDisplayName(e.currentTarget.value)} />}
            <PasswordInput label="Password" value={password} onChange={(e) => setPassword(e.currentTarget.value)} required description={needsSetup ? 'At least 8 characters' : undefined} />
            {needsSetup && (
              <Stack gap="xs">
                <Button variant="subtle" size="compact-sm" onClick={() => setShowDb((v) => !v)} type="button">
                  {showDb ? 'Hide database settings' : 'Use a different PostgreSQL database'}
                </Button>
                <Collapse expanded={showDb}>
                  <Stack gap="xs">
                    <Text size="xs" c="dimmed">Currently {db?.host ?? 'localhost'}:{db?.port ?? 5432}/{db?.database ?? 'slopgarden'}. A remote database is saved, then the server restarts and this page comes back so you can create the Admin. No demo account is created.</Text>
                    <TextInput label="Host" value={host} onChange={(e) => setHost(e.currentTarget.value)} />
                    <NumberInput label="Port" value={port} onChange={setPort} min={1} max={65535} />
                    <TextInput label="Database" value={database} onChange={(e) => setDatabase(e.currentTarget.value)} />
                    <TextInput label="Database user" value={dbUser} onChange={(e) => setDbUser(e.currentTarget.value)} />
                    <PasswordInput label="Database password" value={dbPassword} onChange={(e) => setDbPassword(e.currentTarget.value)} />
                    <Button variant="light" type="button" loading={restarting} onClick={async () => {
                      setRestarting(true)
                      setError(null)
                      try {
                        await api.post('/api/auth/setup-database', { host, port: Number(port), database, username: dbUser, password: dbPassword, sslMode: 'Disable' })
                        setError('Saved. The server is restarting. This page will reload when it is back.')
                        const ping = window.setInterval(async () => {
                          try { await fetch('/api/health'); window.clearInterval(ping); window.location.reload() } catch { /* still down */ }
                        }, 1000)
                      } catch (err) {
                        setError((err as Error).message)
                        setRestarting(false)
                      }
                    }}>Save database and restart</Button>
                  </Stack>
                </Collapse>
              </Stack>
            )}
            <Button type="submit" loading={busy} fullWidth>{needsSetup ? 'Create Admin' : 'Sign in'}</Button>
          </Stack>
        </form>
      </div>
    </div>
  )
}
