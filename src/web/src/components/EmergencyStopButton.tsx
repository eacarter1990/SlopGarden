import { Button, Text, Textarea } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { IconHandStop } from '@tabler/icons-react'
import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { useAuth } from '../auth'

interface Props {
  scopeType: 'global' | 'garden' | 'bed' | 'plant' | 'reservoir' | 'device' | 'actuator'
  scopeId?: string
  label?: string
  compact?: boolean
}

function ReasonInput({ onChange }: { onChange: (v: string) => void }) {
  const [v, setV] = useState('')
  return <Textarea autoFocus label="Reason (optional)" placeholder="e.g. hose burst" value={v} onChange={(e) => { setV(e.currentTarget.value); onChange(e.currentTarget.value) }} />
}

/** Emergency stop at any level: switches off and locks every actuator in scope immediately. */
export function EmergencyStopButton({ scopeType, scopeId, label = 'Emergency stop', compact }: Props) {
  const { canWrite } = useAuth()
  const qc = useQueryClient()
  const trigger = () => {
    let reason = ''
    modals.openConfirmModal({
      title: `Emergency stop (${scopeType})`,
      children: <ReasonInput onChange={(v) => (reason = v)} />,
      labels: { confirm: 'Stop now', cancel: 'Cancel' },
      confirmProps: { color: 'red', leftSection: <IconHandStop size={16} /> },
      onConfirm: async () => {
        try {
          await api.op('trigger_emergency_stop', { scopeType, scopeId, reason: reason || 'Manual emergency stop' })
          notifications.show({ color: 'red', title: 'Emergency stop active', message: 'Actuators in scope are off and locked until released on the Safety page.' })
          qc.invalidateQueries()
        } catch (e) {
          notifications.show({ color: 'red', title: 'Stop failed', message: (e as Error).message })
        }
      },
    })
  }
  return (
    <Button
      className="sg-stop"
      color="red"
      variant="filled"
      size={compact ? 'sm' : 'sm'}
      leftSection={<IconHandStop size={16} />}
      onClick={trigger}
      disabled={!canWrite}
      aria-label={label === 'Stop' ? 'Stop all' : label}
    >
      {compact ? <Text span>{label}</Text> : label}
    </Button>
  )
}
