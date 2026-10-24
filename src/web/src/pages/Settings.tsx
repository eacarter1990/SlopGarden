import { Alert, Badge, Button, Card, Code, CopyButton, Group, MultiSelect, NumberInput, PasswordInput, Select, SimpleGrid, Stack, Switch, Table, Tabs, Text, TextInput } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { IconPlus } from '@tabler/icons-react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { api } from '../api/client'
import type { User } from '../api/types'
import { roleLabel, useAuth } from '../auth'
import { PageTitle, TrustBadge } from '../components/Widgets'
import { ago, titleCase } from '../format'

async function act<T>(fn: () => Promise<T>, ok = 'Saved'): Promise<T | undefined> {
  try {
    const r = await fn()
    notifications.show({ color: 'green', message: ok })
    return r
  } catch (e) {
    notifications.show({ color: 'red', title: 'Failed', message: (e as Error).message })
  }
}

interface AiSettings {
  providers: { id: string; name: string; baseUrl: string; defaultModel: string; hasApiKey: boolean }[]
  assignments: { job: string; providerId?: string | null; model: string; temperature?: number | null; maxOutputTokens?: number | null; contextMessageLimit: number }[]
  webSearch: {
    provider: string
    baseUrl?: string | null
    mode: 'auto' | 'custom'
    hasApiKey: boolean
    providers: string[]
    status: { job: string; mode: 'openai_native' | 'custom' | 'none'; model?: string | null; reason: string; fallbackAvailable: boolean }[]
  }
}

const jobLabels: Record<string, string> = {
  chat: 'Chat assistant',
  scheduled_review: 'Scheduled reviews',
  vision: 'Camera / vision',
  reports: 'Reports',
  calibration: 'Calibration',
}

const presets = [
  { label: 'OpenAI', baseUrl: 'https://api.openai.com/v1', model: 'gpt-4.1-mini' },
  { label: 'LM Studio (local)', baseUrl: 'http://localhost:1234/v1', model: '' },
  { label: 'Ollama (local)', baseUrl: 'http://localhost:11434/v1', model: 'llama3.1' },
  { label: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', model: '' },
]

function ProviderForm({ existing, onDone }: { existing?: AiSettings['providers'][number]; onDone: () => void }) {
  const [name, setName] = useState(existing?.name ?? 'OpenAI')
  const [baseUrl, setBaseUrl] = useState(existing?.baseUrl ?? 'https://api.openai.com/v1')
  const [apiKey, setApiKey] = useState('')
  const [model, setModel] = useState(existing?.defaultModel ?? 'gpt-4.1-mini')
  const [models, setModels] = useState<string[]>([])
  const [testing, setTesting] = useState(false)
  const test = async () => {
    setTesting(true)
    const r = await api.post<{ ok: boolean; models: string[]; error?: string }>('/api/settings/ai/providers/test', { baseUrl, apiKey: apiKey || null, providerId: existing?.id ?? null })
    setTesting(false)
    if (r.ok) {
      setModels(r.models)
      notifications.show({ color: 'green', message: `Connected · ${r.models.length} models` })
    } else notifications.show({ color: 'red', title: 'Connection failed', message: r.error })
  }
  return (
    <Stack>
      <Group gap="xs">{presets.map((p) => <Button key={p.label} size="xs" variant="light" onClick={() => { setName(p.label); setBaseUrl(p.baseUrl); if (p.model) setModel(p.model) }}>{p.label}</Button>)}</Group>
      <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} />
      <TextInput label="Base URL (OpenAI-compatible)" value={baseUrl} onChange={(e) => setBaseUrl(e.currentTarget.value)} description="…/v1 — the server calls {base}/chat/completions" />
      <PasswordInput label="API key" placeholder={existing?.hasApiKey ? '•••••••• (stored encrypted; leave empty to keep)' : 'sk-… (leave empty for local servers)'} value={apiKey} onChange={(e) => setApiKey(e.currentTarget.value)} />
      <Group align="flex-end">
        {models.length > 0 ? <Select style={{ flex: 1 }} label="Default model" searchable data={models} value={model} onChange={(v) => setModel(v ?? '')} /> : <TextInput style={{ flex: 1 }} label="Default model" value={model} onChange={(e) => setModel(e.currentTarget.value)} />}
        <Button variant="light" loading={testing} onClick={test}>Test & list models</Button>
      </Group>
      <Group justify="flex-end">
        <Button onClick={async () => {
          const body = { name, baseUrl, apiKey: apiKey || null, defaultModel: model }
          await act(() => (existing ? api.put(`/api/settings/ai/providers/${existing.id}`, body) : api.post('/api/settings/ai/providers', body)))
          onDone()
          modals.closeAll()
        }}>Save provider</Button>
      </Group>
    </Stack>
  )
}

function AiTab() {
  const qc = useQueryClient()
  const { isAdmin } = useAuth()
  const { data } = useQuery({ queryKey: ['settings', 'ai'], queryFn: () => api.get<AiSettings>('/api/settings/ai') })
  const [search, setSearch] = useState<{ provider: string; baseUrl: string; apiKey: string; mode: 'auto' | 'custom' } | null>(null)
  const refresh = () => qc.invalidateQueries({ queryKey: ['settings', 'ai'] })
  if (!data) return null
  const ws = search ?? { provider: data.webSearch.provider, baseUrl: data.webSearch.baseUrl ?? '', apiKey: '', mode: data.webSearch.mode }
  return (
    <Stack>
      <Card>
        <Group justify="space-between" mb="xs">
          <div>
            <Text fw={650}>Model providers</Text>
            <Text size="xs" c="dimmed">Bring your own model: any OpenAI-compatible endpoint. API keys are encrypted at rest and never sent back to the browser.</Text>
          </div>
          {isAdmin && <Button size="xs" leftSection={<IconPlus size={14} />} onClick={() => modals.open({ title: 'Add AI provider', size: 'lg', children: <ProviderForm onDone={refresh} /> })}>Add provider</Button>}
        </Group>
        <Table>
          <Table.Tbody>
            {data.providers.map((p) => (
              <Table.Tr key={p.id}>
                <Table.Td fw={600}>{p.name}</Table.Td>
                <Table.Td ff="monospace" fz="xs">{p.baseUrl}</Table.Td>
                <Table.Td>{p.defaultModel}</Table.Td>
                <Table.Td>{p.hasApiKey ? <Badge color="green" variant="light">key stored</Badge> : <Badge color="gray" variant="light">no key</Badge>}</Table.Td>
                <Table.Td>{isAdmin && (
                  <Group gap={4} justify="flex-end">
                    <Button size="xs" variant="subtle" onClick={() => modals.open({ title: `Edit ${p.name}`, size: 'lg', children: <ProviderForm existing={p} onDone={refresh} /> })}>Edit</Button>
                    <Button size="xs" variant="subtle" color="red" onClick={async () => { await act(() => api.del(`/api/settings/ai/providers/${p.id}`), 'Removed'); refresh() }}>Remove</Button>
                  </Group>
                )}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
        {data.providers.length === 0 && <Alert mt="sm">No provider yet. Add one and enter your API key to start chatting.</Alert>}
      </Card>
      <Card>
        <Text fw={650}>Model per job</Text>
        <Text size="xs" c="dimmed" mb="xs">Use a cheap model for routine reviews and a stronger or vision model where it matters. "Context" caps how many recent messages are sent.</Text>
        <Table>
          <Table.Thead><Table.Tr><Table.Th>Job</Table.Th><Table.Th>Provider</Table.Th><Table.Th>Model</Table.Th><Table.Th>Context msgs</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>
            {data.assignments.map((a) => <AssignmentRow key={a.job} a={a} providers={data.providers} onSaved={refresh} disabled={!isAdmin} />)}
          </Table.Tbody>
        </Table>
      </Card>
      <WebSearchCard data={data} ws={ws} setSearch={setSearch} isAdmin={isAdmin} onSaved={() => { setSearch(null); refresh() }} />
    </Stack>
  )
}

const modeBadge: Record<string, [string, string]> = {
  openai_native: ['green', 'OpenAI built-in'],
  custom: ['blue', 'Custom provider'],
  none: ['gray', 'Unavailable'],
}

type SearchDraft = { provider: string; baseUrl: string; apiKey: string; mode: 'auto' | 'custom' }

/** Shows which web search backend the AI uses per job and lets Admins pick the custom provider or override OpenAI's built-in search. */
function WebSearchCard({ data, ws, setSearch, isAdmin, onSaved }: { data: AiSettings; ws: SearchDraft; setSearch: (s: SearchDraft) => void; isAdmin: boolean; onSaved: () => void }) {
  const status = data.webSearch.status
  const chat = status.find((s) => s.job === 'chat')
  const nativeAnywhere = status.some((s) => s.mode === 'openai_native')
  const openAiEligible = nativeAnywhere || (data.webSearch.mode === 'custom' && data.providers.some((p) => p.baseUrl.includes('api.openai.com')))
  const providerName = titleCase(data.webSearch.provider)
  return (
    <Card>
      <Text fw={650}>Web search for the AI</Text>
      <Text size="xs" c="dimmed" mb="sm">Used by the web_search tool for crop research, pests and regional planting dates.</Text>
      {chat?.mode === 'openai_native' ? (
        <Alert color="green" mb="sm" title={`Using OpenAI built-in web search (${chat.model})`}>
          The chat model runs on OpenAI, so searches use OpenAI's own web search tool with your OpenAI key — no extra search account needed.
          {chat.fallbackAvailable ? ` If it fails, ${providerName} is used instead.` : ' Optionally pick a custom provider below as a fallback.'}
        </Alert>
      ) : chat?.mode === 'custom' ? (
        <Alert color="blue" mb="sm" title={`Using custom provider: ${providerName}`}>{chat.reason}.</Alert>
      ) : (
        <Alert color="gray" mb="sm" title="No web search available">{chat?.reason ?? 'Configure a provider below'}. Choose a custom provider to enable search for bring-your-own models (LM Studio, Ollama, …).</Alert>
      )}
      {openAiEligible && (
        <Switch
          mb="sm"
          disabled={!isAdmin}
          label="Use a custom provider instead of OpenAI's built-in search"
          description="OpenAI search is billed per call by OpenAI; a custom provider (e.g. self-hosted SearXNG) may be cheaper."
          checked={ws.mode === 'custom'}
          onChange={(e) => setSearch({ ...ws, mode: e.currentTarget.checked ? 'custom' : 'auto' })}
        />
      )}
      <SimpleGrid cols={{ base: 1, sm: 3 }}>
        <Select
          label={chat?.mode === 'openai_native' && ws.mode !== 'custom' ? 'Fallback provider (optional)' : 'Custom provider'}
          description="Also used for jobs whose model isn't on OpenAI"
          data={data.webSearch.providers.map((p) => ({ value: p, label: titleCase(p) }))}
          value={ws.provider}
          onChange={(v) => setSearch({ ...ws, provider: v ?? 'none' })}
          disabled={!isAdmin}
        />
        {ws.provider === 'searxng' && <TextInput label="SearXNG URL" placeholder="http://searxng.local:8080" value={ws.baseUrl} onChange={(e) => setSearch({ ...ws, baseUrl: e.currentTarget.value })} />}
        {(ws.provider === 'tavily' || ws.provider === 'brave') && <PasswordInput label="API key" placeholder={data.webSearch.hasApiKey ? 'stored' : ''} value={ws.apiKey} onChange={(e) => setSearch({ ...ws, apiKey: e.currentTarget.value })} />}
      </SimpleGrid>
      {isAdmin && (
        <Group justify="flex-end" mt="sm">
          <Button size="xs" onClick={async () => { await act(() => api.put('/api/settings/ai/web-search', { provider: ws.provider, baseUrl: ws.baseUrl || null, apiKey: ws.apiKey || null, mode: ws.mode })); onSaved() }}>Save search</Button>
        </Group>
      )}
      <Table mt="md" fz="xs" verticalSpacing={4}>
        <Table.Thead><Table.Tr><Table.Th>Job</Table.Th><Table.Th>Search mode</Table.Th><Table.Th>Why</Table.Th></Table.Tr></Table.Thead>
        <Table.Tbody>
          {status.map((s) => (
            <Table.Tr key={s.job}>
              <Table.Td>{jobLabels[s.job] ?? s.job}</Table.Td>
              <Table.Td><Badge size="xs" variant="light" color={modeBadge[s.mode][0]}>{s.mode === 'custom' ? providerName : modeBadge[s.mode][1]}</Badge></Table.Td>
              <Table.Td c="dimmed">{s.reason}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Card>
  )
}

function AssignmentRow({ a, providers, onSaved, disabled }: { a: AiSettings['assignments'][number]; providers: AiSettings['providers']; onSaved: () => void; disabled: boolean }) {
  const [providerId, setProviderId] = useState<string | null>(a.providerId ?? providers[0]?.id ?? null)
  const [model, setModel] = useState(a.model || providers.find((p) => p.id === (a.providerId ?? providers[0]?.id))?.defaultModel || '')
  const [ctx, setCtx] = useState<number | string>(a.contextMessageLimit)
  return (
    <Table.Tr>
      <Table.Td>{jobLabels[a.job] ?? a.job}</Table.Td>
      <Table.Td><Select size="xs" data={providers.map((p) => ({ value: p.id, label: p.name }))} value={providerId} onChange={setProviderId} disabled={disabled} /></Table.Td>
      <Table.Td><TextInput size="xs" value={model} onChange={(e) => setModel(e.currentTarget.value)} disabled={disabled} /></Table.Td>
      <Table.Td><NumberInput size="xs" w={80} value={ctx} onChange={setCtx} min={4} max={200} disabled={disabled} /></Table.Td>
      <Table.Td>{!disabled && <Button size="xs" variant="light" disabled={!providerId || !model} onClick={async () => { await act(() => api.put(`/api/settings/ai/assignments/${a.job}`, { providerId, model, contextMessageLimit: Number(ctx) })); onSaved() }}>Save</Button>}</Table.Td>
    </Table.Tr>
  )
}

interface ChannelRow { id: string; kind: string; name: string; minSeverity: string; categories: string[]; enabled: boolean; hasWebhook: boolean }

function NotificationChannelsTab() {
  const qc = useQueryClient()
  const { isAdmin } = useAuth()
  const { data } = useQuery({ queryKey: ['settings', 'channels'], queryFn: () => api.get<ChannelRow[]>('/api/settings/notification-channels') })
  const [name, setName] = useState('Discord #garden')
  const [url, setUrl] = useState('')
  const [sev, setSev] = useState<string | null>('warning')
  const refresh = () => qc.invalidateQueries({ queryKey: ['settings', 'channels'] })
  return (
    <Stack>
      <Card>
        <Text fw={650} mb="xs">Channels</Text>
        <Table>
          <Table.Tbody>
            {(data ?? []).map((c) => (
              <Table.Tr key={c.id}>
                <Table.Td><Badge variant="light" color={c.kind === 'discord' ? 'indigo' : 'green'}>{c.kind}</Badge></Table.Td>
                <Table.Td fw={600}>{c.name}</Table.Td>
                <Table.Td>min severity: {c.minSeverity}</Table.Td>
                <Table.Td><Switch size="xs" checked={c.enabled} disabled={!isAdmin} onChange={async (e) => { await act(() => api.put(`/api/settings/notification-channels/${c.id}`, { kind: c.kind, name: c.name, enabled: e.currentTarget.checked })); refresh() }} /></Table.Td>
                <Table.Td>{isAdmin && (
                  <Group gap={4} justify="flex-end">
                    <Button size="xs" variant="subtle" onClick={async () => { const r = await api.post<{ ok: boolean; error?: string }>(`/api/settings/notification-channels/${c.id}/test`); notifications.show({ color: r.ok ? 'green' : 'red', message: r.ok ? 'Test sent' : r.error }) }}>Test</Button>
                    <Button size="xs" variant="subtle" color="red" onClick={async () => { await act(() => api.del(`/api/settings/notification-channels/${c.id}`), 'Removed'); refresh() }}>Remove</Button>
                  </Group>
                )}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Card>
      {isAdmin && (
        <Card>
          <Text fw={650} mb="xs">Add a Discord webhook</Text>
          <Group align="flex-end">
            <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} />
            <PasswordInput style={{ flex: 1 }} label="Webhook URL" description="Stored encrypted" value={url} onChange={(e) => setUrl(e.currentTarget.value)} />
            <Select label="Min severity" w={130} data={['info', 'warning', 'critical']} value={sev} onChange={setSev} />
            <Button disabled={!url} onClick={async () => { await act(() => api.post('/api/settings/notification-channels', { kind: 'discord', name, webhookUrl: url, minSeverity: sev })); setUrl(''); refresh() }}>Add</Button>
          </Group>
        </Card>
      )}
    </Stack>
  )
}

function UsersTab() {
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['users'], queryFn: () => api.get<User[]>('/api/users') })
  const [u, setU] = useState({ username: '', displayName: '', role: 'read_write', password: '' })
  const refresh = () => qc.invalidateQueries({ queryKey: ['users'] })
  return (
    <Stack>
      <Card>
        <Text size="sm" mb="sm"><b>Admin</b>: devices, server and database settings, users. <b>ReadWrite</b>: plants, schedules, approve AI changes. <b>ReadOnly</b>: sees everything, changes nothing.</Text>
        <Table>
          <Table.Thead><Table.Tr><Table.Th>User</Table.Th><Table.Th>Role</Table.Th><Table.Th>Last login</Table.Th><Table.Th>Active</Table.Th></Table.Tr></Table.Thead>
          <Table.Tbody>
            {(data ?? []).map((x) => (
              <Table.Tr key={x.id}>
                <Table.Td><Text fw={600} size="sm">{x.displayName}</Text><Text size="xs" c="dimmed">{x.username}</Text></Table.Td>
                <Table.Td><Select size="xs" w={140} data={Object.entries(roleLabel).map(([value, label]) => ({ value, label }))} value={x.role} onChange={async (role) => { await act(() => api.put(`/api/users/${x.id}`, { username: x.username, role })); refresh() }} /></Table.Td>
                <Table.Td>{ago(x.lastLoginAt)}</Table.Td>
                <Table.Td><Switch size="xs" checked={!x.isDisabled} onChange={async (e) => { await act(() => api.put(`/api/users/${x.id}`, { username: x.username, role: x.role, isDisabled: !e.currentTarget.checked })); refresh() }} /></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Card>
      <Card>
        <Text fw={650} mb="xs">Add user</Text>
        <Group align="flex-end">
          <TextInput label="Username" value={u.username} onChange={(e) => setU({ ...u, username: e.currentTarget.value })} />
          <TextInput label="Display name" value={u.displayName} onChange={(e) => setU({ ...u, displayName: e.currentTarget.value })} />
          <Select label="Role" w={140} data={Object.entries(roleLabel).map(([value, label]) => ({ value, label }))} value={u.role} onChange={(role) => setU({ ...u, role: role ?? 'read_only' })} />
          <PasswordInput label="Password" value={u.password} onChange={(e) => setU({ ...u, password: e.currentTarget.value })} />
          <Button onClick={async () => { await act(() => api.post('/api/users', u), 'User created'); setU({ username: '', displayName: '', role: 'read_write', password: '' }); refresh() }}>Create</Button>
        </Group>
      </Card>
    </Stack>
  )
}

interface Pat { id: string; name: string; tokenPrefix: string; scopes: string[]; createdAt: string; expiresAt?: string | null; lastUsedAt?: string | null; revokedAt?: string | null }

function TokensTab() {
  const qc = useQueryClient()
  const { canWrite } = useAuth()
  const { data } = useQuery({ queryKey: ['tokens'], queryFn: () => api.get<Pat[]>('/api/tokens') })
  const [name, setName] = useState('Claude Desktop')
  const [scopes, setScopes] = useState<string[]>(['read', 'propose'])
  const [days, setDays] = useState<number | string>(90)
  const [created, setCreated] = useState<string | null>(null)
  const refresh = () => qc.invalidateQueries({ queryKey: ['tokens'] })
  return (
    <Stack>
      <Card>
        <Text fw={650} mb={4}>Personal access tokens</Text>
        <Text size="xs" c="dimmed" mb="sm">For external MCP clients and scripts. The token acts as you, limited by its scopes. See the MCP tools page for connection details.</Text>
        {created && (
          <Alert color="green" mb="sm" title="Copy your token now — it won't be shown again">
            <Group><Code>{created}</Code><CopyButton value={created}>{({ copied, copy }) => <Button size="xs" onClick={copy}>{copied ? 'Copied' : 'Copy'}</Button>}</CopyButton></Group>
          </Alert>
        )}
        <Group align="flex-end" wrap="wrap">
          <TextInput label="Name" value={name} onChange={(e) => setName(e.currentTarget.value)} style={{ flex: '1 1 12rem' }} />
          <MultiSelect label="Scopes" style={{ flex: '1 1 18rem' }} styles={{ pill: { maxWidth: 'none' } }} data={[{ value: 'read', label: 'read' }, { value: 'propose', label: 'propose (changes need approval)', disabled: !canWrite }, { value: 'write', label: 'write (changes run directly)', disabled: !canWrite }]} value={scopes} onChange={setScopes} />
          <NumberInput label="Expires (days, 0 = never)" w={170} value={days} onChange={setDays} min={0} />
          <Button onClick={async () => { const r = await act(() => api.post<{ token: string }>('/api/tokens', { name, scopes, expiresInDays: Number(days) || null }), 'Token created'); if (r) setCreated(r.token); refresh() }}>Create token</Button>
        </Group>
      </Card>
      <Card>
        <Table>
          <Table.Thead><Table.Tr><Table.Th>Name</Table.Th><Table.Th>Token</Table.Th><Table.Th>Scopes</Table.Th><Table.Th>Last used</Table.Th><Table.Th>Expires</Table.Th><Table.Th /></Table.Tr></Table.Thead>
          <Table.Tbody>
            {(data ?? []).map((t) => (
              <Table.Tr key={t.id} style={{ opacity: t.revokedAt ? 0.5 : 1 }}>
                <Table.Td fw={600}>{t.name}</Table.Td>
                <Table.Td ff="monospace" fz="xs">{t.tokenPrefix}…</Table.Td>
                <Table.Td>{t.scopes.map((s) => <Badge key={s} size="xs" mr={2} variant="light">{s}</Badge>)}</Table.Td>
                <Table.Td>{ago(t.lastUsedAt)}</Table.Td>
                <Table.Td>{t.expiresAt ? ago(t.expiresAt) : 'never'}</Table.Td>
                <Table.Td>{t.revokedAt ? <Badge color="gray">revoked</Badge> : <Button size="xs" variant="subtle" color="red" onClick={async () => { await act(() => api.del(`/api/tokens/${t.id}`), 'Revoked'); refresh() }}>Revoke</Button>}</Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Card>
    </Stack>
  )
}

function AccountTab() {
  const [cur, setCur] = useState('')
  const [next, setNext] = useState('')
  return (
    <Card maw={420}>
      <Text fw={650} mb="xs">Change password</Text>
      <Stack>
        <PasswordInput label="Current password" value={cur} onChange={(e) => setCur(e.currentTarget.value)} />
        <PasswordInput label="New password" value={next} onChange={(e) => setNext(e.currentTarget.value)} description="At least 8 characters" />
        <Button onClick={async () => { await act(() => api.post('/api/users/me/password', { currentPassword: cur, newPassword: next }), 'Password changed'); setCur(''); setNext('') }}>Change password</Button>
      </Stack>
    </Card>
  )
}

interface ServerInfo { version: string; serverUrl: string; dataDirectory: string; overridesFile: string; discoveryEnabled: boolean; discoveryPort: number; runtime: string; os: string; database: { host: string; port: number; database: string; username: string; sslMode: string; extra?: string | null; hasPassword: boolean } }

function ServerTab() {
  const { data } = useQuery({ queryKey: ['settings', 'server'], queryFn: () => api.get<ServerInfo>('/api/settings/server') })
  const [db, setDb] = useState<Record<string, unknown> | null>(null)
  if (!data) return null
  const v = db ?? { ...data.database, password: '' }
  const set = (k: string) => (x: unknown) => setDb({ ...v, [k]: x })
  return (
    <Stack>
      <Card>
        <Text fw={650} mb="xs">Server</Text>
        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing={4}>
          <Text size="sm">URL for clients: <Code>{data.serverUrl}</Code></Text>
          <Text size="sm">Version: {data.version} · {data.runtime}</Text>
          <Text size="sm">Data directory: <Code>{data.dataDirectory}</Code></Text>
          <Text size="sm">LAN discovery: {data.discoveryEnabled ? `on (UDP ${data.discoveryPort})` : 'off'}</Text>
          <Text size="sm">OS: {data.os}</Text>
          <Text size="sm">Transport: HTTP on the local network (no TLS yet)</Text>
        </SimpleGrid>
      </Card>
      <Card>
        <Text fw={650} mb={4}>PostgreSQL connection</Text>
        <Text size="xs" c="dimmed" mb="sm">Local, LAN or remote database. Saved to <Code>{data.overridesFile}</Code>; restart the server to apply.</Text>
        <SimpleGrid cols={{ base: 1, sm: 3 }}>
          <TextInput label="Host" value={v.host as string} onChange={(e) => set('host')(e.currentTarget.value)} />
          <NumberInput label="Port" value={v.port as number} onChange={set('port')} />
          <TextInput label="Database" value={v.database as string} onChange={(e) => set('database')(e.currentTarget.value)} />
          <TextInput label="Username" value={v.username as string} onChange={(e) => set('username')(e.currentTarget.value)} />
          <PasswordInput label="Password" placeholder={data.database.hasPassword ? 'unchanged' : ''} value={v.password as string} onChange={(e) => set('password')(e.currentTarget.value)} />
          <Select label="SSL mode" data={['Disable', 'Prefer', 'Require', 'VerifyCA', 'VerifyFull']} value={v.sslMode as string} onChange={set('sslMode')} />
        </SimpleGrid>
        <TextInput mt="sm" label="Extra parameters" placeholder="Pooling=true;Timeout=30" value={(v.extra as string) ?? ''} onChange={(e) => set('extra')(e.currentTarget.value)} />
        <Group justify="flex-end" mt="sm">
          <Button variant="light" onClick={async () => { const r = await api.post<{ ok: boolean; version?: string; error?: string }>('/api/settings/server/database/test', v); notifications.show({ color: r.ok ? 'green' : 'red', title: r.ok ? 'Connected' : 'Connection failed', message: r.ok ? r.version : r.error }) }}>Test connection</Button>
          <Button onClick={() => act(() => api.put('/api/settings/server/database', v), 'Saved — restart the server to apply')}>Save</Button>
        </Group>
      </Card>
    </Stack>
  )
}

interface CatalogEntry { id: string; pluginId: string; version: string; displayName: string; publisher: string; sha256: string; status: string; signingKey?: { keyId: string; publisher: string } | null }
interface SigningKey { id: string; keyId: string; publisher: string; status: string; createdAt: string }

function PluginsTab() {
  const qc = useQueryClient()
  const { isAdmin } = useAuth()
  const { data: catalog } = useQuery({ queryKey: ['plugin-catalog'], queryFn: () => api.op<CatalogEntry[]>('list_plugin_catalog') })
  const { data: keys } = useQuery({ queryKey: ['plugin-keys'], queryFn: () => api.get<SigningKey[]>('/api/plugin-catalog/keys') })
  const [pub, setPub] = useState({ publisher: '', publicKeySpki: '' })
  const [entry, setEntry] = useState({ pluginId: '', version: '', displayName: '', publisher: '', sha256: '', signature: '', keyId: '' })
  const refresh = () => qc.invalidateQueries()
  return (
    <Stack>
      <Alert>Modeled on signed drivers: publishers sign (plugin id, version, SHA-256) with an ECDSA P-256 key. Devices verify signatures against these trusted keys. Unsigned plugins still load, with a warning; invalid signatures are refused.</Alert>
      <Card>
        <Text fw={650} mb="xs">Trusted signing keys</Text>
        <Table><Table.Tbody>{(keys ?? []).map((k) => <Table.Tr key={k.id}><Table.Td fw={600}>{k.publisher}</Table.Td><Table.Td ff="monospace" fz="xs">{k.keyId}</Table.Td><Table.Td><Badge variant="light" color={k.status === 'active' ? 'green' : 'red'}>{k.status}</Badge></Table.Td><Table.Td>{isAdmin && k.status === 'active' && <Button size="xs" variant="subtle" color="red" onClick={async () => { await act(() => api.post(`/api/plugin-catalog/keys/${k.id}/revoke`), 'Revoked'); refresh() }}>Revoke</Button>}</Table.Td></Table.Tr>)}</Table.Tbody></Table>
        {(keys ?? []).length === 0 && <Text size="sm" c="dimmed">No keys yet.</Text>}
        {isAdmin && (
          <Group align="flex-end" mt="sm">
            <TextInput label="Publisher" value={pub.publisher} onChange={(e) => setPub({ ...pub, publisher: e.currentTarget.value })} />
            <TextInput style={{ flex: 1 }} label="Public key (base64 SPKI)" description="from `SlopGarden.Client keygen`" value={pub.publicKeySpki} onChange={(e) => setPub({ ...pub, publicKeySpki: e.currentTarget.value })} />
            <Button onClick={async () => { await act(() => api.post('/api/plugin-catalog/keys', pub), 'Key trusted'); refresh() }}>Trust key</Button>
          </Group>
        )}
      </Card>
      <Card>
        <Text fw={650} mb="xs">Approved plugin catalog</Text>
        <Table>
          <Table.Thead><Table.Tr><Table.Th>Plugin</Table.Th><Table.Th>Version</Table.Th><Table.Th>Publisher</Table.Th><Table.Th>SHA-256</Table.Th><Table.Th>Status</Table.Th></Table.Tr></Table.Thead>
          <Table.Tbody>{(catalog ?? []).map((c) => <Table.Tr key={c.id}><Table.Td fw={600}>{c.displayName}<Text size="xs" c="dimmed">{c.pluginId}</Text></Table.Td><Table.Td>{c.version}</Table.Td><Table.Td>{c.publisher}</Table.Td><Table.Td ff="monospace" fz="10px">{c.sha256.slice(0, 16)}…</Table.Td><Table.Td><TrustBadge trust={c.status === 'approved' ? 'signed' : 'invalid'} /></Table.Td></Table.Tr>)}</Table.Tbody>
        </Table>
        {(catalog ?? []).length === 0 && <Text size="sm" c="dimmed">No catalog entries yet.</Text>}
        {isAdmin && (
          <SimpleGrid cols={{ base: 1, sm: 4 }} mt="sm">
            {(['pluginId', 'version', 'displayName', 'publisher', 'sha256', 'signature', 'keyId'] as const).map((k) => <TextInput key={k} size="xs" label={k} value={entry[k]} onChange={(e) => setEntry({ ...entry, [k]: e.currentTarget.value })} />)}
            <Button mt="lg" size="xs" onClick={async () => { await act(() => api.post('/api/plugin-catalog', entry), 'Added to catalog'); refresh() }}>Add signed build</Button>
          </SimpleGrid>
        )}
      </Card>
    </Stack>
  )
}

interface HardwareEntry { id: string; kind: string; manufacturer: string; model: string; protocol: string; metrics: string[]; pluginId: string; recommended: boolean; testedStatus: string; notes: string }

function HardwareTab() {
  const { data } = useQuery({ queryKey: ['hardware-catalog'], queryFn: () => api.op<HardwareEntry[]>('list_hardware_catalog') })
  return (
    <Card>
      <Text fw={650} mb="xs">Compatible hardware</Text>
      <Table>
        <Table.Thead><Table.Tr><Table.Th>Hardware</Table.Th><Table.Th>Kind</Table.Th><Table.Th>Protocol</Table.Th><Table.Th>Plugin</Table.Th><Table.Th>Status</Table.Th><Table.Th>Notes</Table.Th></Table.Tr></Table.Thead>
        <Table.Tbody>
          {(data ?? []).map((h) => (
            <Table.Tr key={h.id}>
              <Table.Td><Text size="sm" fw={600}>{h.model}</Text><Text size="xs" c="dimmed">{h.manufacturer}</Text></Table.Td>
              <Table.Td>{h.kind}</Table.Td>
              <Table.Td>{h.protocol}</Table.Td>
              <Table.Td ff="monospace" fz="xs">{h.pluginId}</Table.Td>
              <Table.Td><Group gap={4}>{h.recommended && <Badge size="xs" color="green">recommended</Badge>}<Badge size="xs" variant="light" color="gray">{h.testedStatus}</Badge></Group></Table.Td>
              <Table.Td fz="xs">{h.notes}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Card>
  )
}

export function SettingsPage() {
  const { isAdmin } = useAuth()
  const location = useLocation()
  const navigate = useNavigate()
  const tab = location.pathname.split('/')[2] || (isAdmin ? 'ai' : 'tokens')
  const tabs = [
    ...(isAdmin ? [['ai', 'AI models'], ['notifications', 'Notification channels'], ['users', 'Users & roles'], ['server', 'Server & database'], ['plugins', 'Plugin catalog']] : [['ai', 'AI models']]),
    ['hardware', 'Hardware catalog'],
    ['tokens', 'Access tokens'],
    ['account', 'Account'],
  ]
  return (
    <Stack>
      <PageTitle title="Settings" />
      <Tabs value={tab} onChange={(v) => navigate(`/settings/${v}`)} keepMounted={false}>
        <Tabs.List mb="md">{tabs.map(([v, l]) => <Tabs.Tab key={v} value={v}>{l}</Tabs.Tab>)}</Tabs.List>
        <Tabs.Panel value="ai"><AiTab /></Tabs.Panel>
        <Tabs.Panel value="notifications"><NotificationChannelsTab /></Tabs.Panel>
        <Tabs.Panel value="users"><UsersTab /></Tabs.Panel>
        <Tabs.Panel value="server"><ServerTab /></Tabs.Panel>
        <Tabs.Panel value="plugins"><PluginsTab /></Tabs.Panel>
        <Tabs.Panel value="hardware"><HardwareTab /></Tabs.Panel>
        <Tabs.Panel value="tokens"><TokensTab /></Tabs.Panel>
        <Tabs.Panel value="account"><AccountTab /></Tabs.Panel>
      </Tabs>
    </Stack>
  )
}
