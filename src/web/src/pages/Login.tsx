import { Alert, Button, Center, Paper, PasswordInput, Stack, Text, TextInput, Title } from '@mantine/core'
import { useState } from 'react'
import { api } from '../api/client'
import { useAuth } from '../auth'

export function LoginPage({ needsSetup }: { needsSetup: boolean }) {
  const { refresh } = useAuth()
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

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
    <Center h="100vh" style={{ background: 'linear-gradient(135deg, var(--mantine-color-green-9), var(--mantine-color-teal-7))' }}>
      <Paper p="xl" w={380} shadow="xl" radius="lg">
        <form onSubmit={submit}>
          <Stack>
            <Stack gap={4} align="center">
              <img src="/favicon.svg" width={52} height={52} alt="" />
              <Title order={2}>SlopGarden</Title>
              <Text c="dimmed" size="sm" ta="center">{needsSetup ? 'Create the first Admin account for this server.' : 'AI-run garden automation'}</Text>
            </Stack>
            {error && <Alert color="red">{error}</Alert>}
            <TextInput label="Username" value={username} onChange={(e) => setUsername(e.currentTarget.value)} required autoFocus />
            {needsSetup && <TextInput label="Display name" value={displayName} onChange={(e) => setDisplayName(e.currentTarget.value)} />}
            <PasswordInput label="Password" value={password} onChange={(e) => setPassword(e.currentTarget.value)} required description={needsSetup ? 'At least 8 characters' : undefined} />
            <Button type="submit" loading={busy} fullWidth>{needsSetup ? 'Create Admin' : 'Sign in'}</Button>
          </Stack>
        </form>
      </Paper>
    </Center>
  )
}
