import { Accordion, Badge, Card, Code, Group, Stack, Tabs, Text, TextInput } from '@mantine/core'
import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import type { JsonSchema } from '../api/types'
import { useToolDocs } from '../components/ProposalCard'
import { PageTitle } from '../components/Widgets'

function SchemaTable({ schema }: { schema: JsonSchema }) {
  const props = schema.properties ?? {}
  if (Object.keys(props).length === 0) return <Text size="xs" c="dimmed">No parameters</Text>
  return (
    <Stack gap={2}>
      {Object.entries(props).map(([k, p]) => (
        <Text key={k} size="xs">
          <Code>{k}</Code> <Text span c="dimmed">{Array.isArray(p.type) ? p.type.join(' | ') : p.type ?? 'object'}{p.format ? ` (${p.format})` : ''}{schema.required?.includes(k) ? ' · required' : ''}</Text>
          {p.description && <Text span> — {p.description}</Text>}
        </Text>
      ))}
    </Stack>
  )
}

export function McpDocsPage() {
  const { data: tools } = useToolDocs()
  const { data: server } = useQuery({ queryKey: ['server-url'], queryFn: () => api.get<{ serverUrl: string }>('/api/settings/server').catch(() => ({ serverUrl: window.location.origin })) })
  const [q, setQ] = useState('')
  const url = `${server?.serverUrl ?? window.location.origin}/mcp`
  const byCategory = useMemo(() => {
    const m = new Map<string, NonNullable<typeof tools>>()
    for (const t of tools ?? []) {
      if (q && !`${t.name} ${t.description}`.toLowerCase().includes(q.toLowerCase())) continue
      m.set(t.category, [...(m.get(t.category) ?? []), t])
    }
    return [...m.entries()]
  }, [tools, q])

  const cursorConfig = JSON.stringify({ mcpServers: { slopgarden: { url, headers: { Authorization: 'Bearer sgp_your_token' } } } }, null, 2)
  const curl = `curl -s ${url} -H "Authorization: Bearer sgp_your_token" -H "Content-Type: application/json" \\\n  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_garden_overview","arguments":{}}}'`

  return (
    <Stack>
      <PageTitle title="MCP tools" subtitle={`${tools?.length ?? 0} tool functions. The built-in assistant, external AI apps (via MCP) and this web app all use the same set.`} />
      <Card>
        <Text fw={650} mb={4}>Connect an external AI app</Text>
        <Text size="sm" mb="xs">
          MCP endpoint (Streamable HTTP): <Code>{url}</Code>. Authenticate with a <Link to="/settings/tokens">personal access token</Link>. Scopes: <b>read</b> = read tools only · <b>propose</b> = changes become approval prompts here · <b>write</b> = changes run directly (within your role).
        </Text>
        <Tabs defaultValue="cursor">
          <Tabs.List>
            <Tabs.Tab value="cursor">Cursor / Claude config</Tabs.Tab>
            <Tabs.Tab value="curl">curl</Tabs.Tab>
          </Tabs.List>
          <Tabs.Panel value="cursor" pt="xs"><Code block>{cursorConfig}</Code></Tabs.Panel>
          <Tabs.Panel value="curl" pt="xs"><Code block>{curl}</Code></Tabs.Panel>
        </Tabs>
      </Card>
      <TextInput placeholder="Filter tools…" value={q} onChange={(e) => setQ(e.currentTarget.value)} />
      {byCategory.map(([cat, list]) => (
        <Card key={cat}>
          <Text fw={650} mb="xs">{cat} <Text span c="dimmed" size="sm">({list.length})</Text></Text>
          <Accordion variant="separated" chevronPosition="left">
            {list.map((t) => (
              <Accordion.Item key={t.name} value={t.name}>
                <Accordion.Control>
                  <Group justify="space-between" wrap="wrap" gap="xs">
                    <div style={{ minWidth: 0, flex: '1 1 12rem' }}>
                      <Text ff="monospace" size="sm" fw={600} style={{ overflowWrap: 'anywhere' }}>{t.name}</Text>
                      <Text size="xs" c="dimmed" lineClamp={2}>{t.description}</Text>
                    </div>
                    <Group gap={4} wrap="wrap" justify="flex-end">
                      <Badge size="xs" color={t.effect === 'read' ? 'blue' : 'orange'} variant="light">{t.effect}</Badge>
                      {t.effect === 'mutate' && !t.noApprovalNeeded && <Badge size="xs" color="violet" variant="light">approval</Badge>}
                      {t.noApprovalNeeded && <Badge size="xs" color="red" variant="light">immediate</Badge>}
                      {t.routine && <Badge size="xs" color="teal" variant="light">routine</Badge>}
                      {!t.aiAllowed && <Badge size="xs" color="gray">humans only</Badge>}
                      <Badge size="xs" color="gray" variant="outline">{t.minRole}</Badge>
                    </Group>
                  </Group>
                </Accordion.Control>
                <Accordion.Panel>
                  <Stack gap="xs">
                    <Text size="sm">{t.description}</Text>
                    <Text size="xs" fw={600}>Signature</Text>
                    <Code block>{t.signature}</Code>
                    <Text size="xs" fw={600}>Parameters</Text>
                    <SchemaTable schema={t.inputSchema} />
                    <Text size="xs" fw={600}>Output: {t.outputType}</Text>
                    <Code block mah={260} style={{ overflow: 'auto', fontSize: 11 }}>{JSON.stringify(t.outputSchema, null, 2)}</Code>
                  </Stack>
                </Accordion.Panel>
              </Accordion.Item>
            ))}
          </Accordion>
        </Card>
      ))}
    </Stack>
  )
}
