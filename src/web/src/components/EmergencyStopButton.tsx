import { Button, Textarea } from '@mantine/core'
import { modals } from '@mantine/modals'
import { notifications } from '@mantine/notifications'
import { IconHandStop } from '@tabler/icons-react'
import { useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { api } from '../api/client'
import { useAuth } from '../auth'

interface Props {
  scopeType: 'global' | 'garden' | 'bed' | 'plant' | 'device' | 'actuator'
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
  if (!canWrite) return null
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
    <Button color="red" variant={compact ? 'filled' : 'light'} size={compact ? 'xs' : 'sm'} leftSection={<IconHandStop size={16} />} onClick={trigger}>
      {label}
    </Button>
  )
}
