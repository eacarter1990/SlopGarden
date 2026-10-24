export type Role = 'admin' | 'read_write' | 'read_only'

export interface User {
  id: string
  username: string
  displayName: string
  email?: string | null
  role: Role
  isDisabled: boolean
  createdAt: string
  lastLoginAt?: string | null
}

export interface Garden {
  id: string
  name: string
  description: string
  environment: string
  locationSource: string
  latitude?: number | null
  longitude?: number | null
  elevationM?: number | null
  locationLabel?: string | null
  timezoneId: string
  timezoneSource: string
  hardinessZone?: string | null
  climateNotes?: string | null
  aiContext: string
  aiAutonomy: string
}

export interface Bed {
  id: string
  gardenId: string
  name: string
  kind: string
  environment?: string | null
  widthCm?: number | null
  lengthCm?: number | null
  depthCm?: number | null
  soilType?: string | null
  soilNotes?: string | null
  irrigationType: string
  sunExposure: string
  layoutX?: number | null
  layoutY?: number | null
  aiContext: string
  notes: string
  plantCount: number
}

export interface Plant {
  id: string
  bedId: string
  gardenId: string
  plantProfileId?: string | null
  profileName?: string | null
  name: string
  variety?: string | null
  quantity: number
  stage: string
  startedFrom: string
  plantedAt?: string | null
  expectedHarvestStart?: string | null
  expectedHarvestEnd?: string | null
  harvestPredictionNotes?: string | null
  aiContext: string
  notes: string
}

export interface PlantProfile {
  id: string
  slug: string
  commonName: string
  scientificName?: string | null
  family?: string | null
  category: string
  description: string
  waterNeed: string
  soilMoistureMinPct?: number | null
  soilMoistureMaxPct?: number | null
  lightHoursMin?: number | null
  lightHoursMax?: number | null
  airTempMinC?: number | null
  airTempMaxC?: number | null
  soilTempMinC?: number | null
  phMin?: number | null
  phMax?: number | null
  daysToMaturityMin?: number | null
  daysToMaturityMax?: number | null
  spacingCm?: number | null
  frostTolerance: string
  fertilizerNotes?: string | null
  isBuiltin: boolean
  createdByType: string
}

export interface LatestReading {
  sensorId: string
  sensorName: string
  channelId: string
  metric: string
  unit: string
  value?: number | null
  at?: string | null
  quality?: number | null
  alertLow?: number | null
  alertHigh?: number | null
}

export interface CareRange {
  metric: string
  min?: number | null
  max?: number | null
  source: string
}

export interface PlantOverview {
  plant: Plant
  readings: LatestReading[]
  targets: CareRange[]
  aiActive: boolean
}

export interface BedOverview {
  bed: Bed
  plants: PlantOverview[]
  readings: LatestReading[]
  aiActive: boolean
}

export interface ActuatorBrief {
  id: string
  name: string
  kind: string
  isOn: boolean
  stateSince?: string | null
  enabled: boolean
  targetType: string
  bedId?: string | null
  plantId?: string | null
  deviceId: string
}

export interface CameraBrief {
  id: string
  name: string
  targetType: string
  bedId?: string | null
  plantId?: string | null
  lastFrameAt?: string | null
  snapshotUrl: string
}

export interface EnvironmentContext {
  localTime: string
  localDate: string
  dayOfWeek: string
  timezoneId: string
  environment: string
  latitude?: number | null
  longitude?: number | null
  locationLabel?: string | null
  locationSource: string
  hemisphere: string
  season: string
  daylightHours?: number | null
  sunrise?: string | null
  sunset?: string | null
  isDaylight?: boolean | null
}

export interface EmergencyStop {
  id: string
  scopeType: string
  scopeId?: string | null
  active: boolean
  reason: string
  activatedByType: string
  activatedAt: string
}

export interface AiActivity {
  id: string
  gardenId?: string | null
  targetType: string
  targetId?: string | null
  kind: string
  summary: string
  status: string
  conversationId?: string | null
  proposalId?: string | null
  commandId?: string | null
  startedAt: string
  finishedAt?: string | null
}

export interface DeviceBrief {
  id: string
  name: string
  status: string
  isOnline: boolean
  lastSeenAt?: string | null
  unsignedPlugins: number
}

export interface GardenOverview {
  garden: Garden
  environment: EnvironmentContext
  beds: BedOverview[]
  gardenReadings: LatestReading[]
  actuators: ActuatorBrief[]
  cameras: CameraBrief[]
  devices: { total: number; online: number; pending: number; devices: DeviceBrief[] }
  activeEmergencyStops: EmergencyStop[]
  pendingProposals: number
  runningAiActivities: AiActivity[]
  unreadNotifications: number
}

export interface Calibration {
  type: string
  scale?: number | null
  offset?: number | null
  rawLow?: number | null
  valueLow?: number | null
  rawHigh?: number | null
  valueHigh?: number | null
  coefficients?: number[] | null
  clamp?: boolean
}

export interface Channel {
  id: string
  key: string
  metric: string
  unit: string
  displayName?: string | null
  calibration?: Calibration | null
  calibrationStatus: string
  minValid?: number | null
  maxValid?: number | null
  alertLow?: number | null
  alertHigh?: number | null
  lastValue?: number | null
  lastRawValue?: number | null
  lastQuality?: number | null
  lastValueAt?: string | null
}

export interface Sensor {
  id: string
  deviceId: string
  deviceName?: string | null
  name: string
  pluginId: string
  settings: Record<string, unknown>
  readIntervalSec: number
  enabled: boolean
  targetType: string
  gardenId: string
  bedId?: string | null
  plantId?: string | null
  status: string
  lastError?: string | null
  channels: Channel[]
}

export interface Actuator {
  id: string
  deviceId: string
  deviceName?: string | null
  name: string
  kind: string
  pluginId: string
  settings: Record<string, unknown>
  enabled: boolean
  failsafeState: string
  flowRateMlPerMin?: number | null
  targetType: string
  gardenId: string
  bedId?: string | null
  plantId?: string | null
  isOn: boolean
  stateSince?: string | null
  status: string
}

export interface Camera {
  id: string
  deviceId: string
  deviceName?: string | null
  name: string
  pluginId: string
  settings: Record<string, unknown>
  captureWidth: number
  captureHeight: number
  frameIntervalSec: number
  enabled: boolean
  targetType: string
  bedId?: string | null
  plantId?: string | null
  lastFrameAt?: string | null
  snapshotUrl: string
}

export interface Device {
  id: string
  name: string
  hardwareId: string
  hostname: string
  status: string
  discoveredVia: string
  ipAddress?: string | null
  os: string
  architecture: string
  clientVersion: string
  gardenId?: string | null
  approvedAt?: string | null
  reportedTimezoneId?: string | null
  reportedLatitude?: number | null
  reportedLongitude?: number | null
  desiredConfigVersion: number
  appliedConfigVersion?: number | null
  configErrors: string[]
  lastSeenAt?: string | null
  isOnline: boolean
  bufferedReadings: number
  createdAt: string
  unsignedPlugins: number
  invalidPlugins: number
}

export interface PluginComponent {
  id: string
  kind: string
  displayName: string
  description: string
  settingsSchema: JsonSchema
  actuatorKinds?: string[] | null
}

export interface DevicePlugin {
  id: string
  pluginId: string
  version: string
  displayName: string
  kinds: string[]
  sha256: string
  source: string
  trust: string
  publisher?: string | null
  signatureKeyId?: string | null
  description?: string | null
  components: PluginComponent[]
  catalogEntryId?: string | null
}

export interface DiscoveredHardware {
  id: string
  deviceId: string
  pluginId: string
  kind: string
  address: string
  suggestedName: string
  suggestedSettings: Record<string, unknown>
  channels: { key: string; metric: string; unit: string }[]
  actuatorKind?: string | null
  adoptedComponentId?: string | null
}

export interface DeviceEvent {
  id: number
  time: string
  severity: string
  category: string
  message: string
}

export interface DeviceDetail {
  device: Device
  plugins: DevicePlugin[]
  discovered: DiscoveredHardware[]
  sensors: Sensor[]
  actuators: Actuator[]
  cameras: Camera[]
  recentEvents: DeviceEvent[]
}

export interface JsonSchema {
  type?: string | string[]
  description?: string
  properties?: Record<string, JsonSchema>
  required?: string[]
  enum?: string[]
  default?: unknown
  items?: JsonSchema
  format?: string
}

export interface Proposal {
  id: string
  conversationId?: string | null
  origin: string
  toolName: string
  arguments: Record<string, unknown>
  title: string
  rationale: string
  changes: { field: string; before: unknown; after: unknown }[]
  targetType: string
  targetId?: string | null
  risk: string
  status: string
  decidedAt?: string | null
  decisionNote?: string | null
  result?: unknown
  createdAt: string
}

export interface ChatMessage {
  id: string
  conversationId: string
  seq: number
  role: 'user' | 'assistant' | 'tool' | 'event'
  content: string
  toolCalls?: { id: string; name: string; arguments: string }[] | null
  toolCallId?: string | null
  toolName?: string | null
  proposalId?: string | null
  model?: string | null
  error?: string | null
  createdAt: string
}

export interface Conversation {
  id: string
  title: string
  createdAt: string
  updatedAt: string
}

export interface Schedule {
  id: string
  gardenId: string
  name: string
  kind: string
  cron: string
  timezoneId: string
  actuatorId?: string | null
  durationSec?: number | null
  amountMl?: number | null
  payload: Record<string, unknown>
  enabled: boolean
  createdByType: string
  lastRunAt?: string | null
  nextRunAt?: string | null
}

export interface SafetyLimit {
  id: string
  scopeType: string
  scopeId?: string | null
  limitType: string
  actuatorKind?: string | null
  value: number
  channelId?: string | null
  operator?: string | null
  enabled: boolean
  notes: string
  createdByType: string
}

export interface NotificationItem {
  id: string
  severity: string
  category: string
  title: string
  body: string
  targetType?: string | null
  targetId?: string | null
  createdAt: string
  readAt?: string | null
}

export interface ToolDoc {
  name: string
  category: string
  description: string
  effect: 'read' | 'mutate'
  minRole: Role
  aiAllowed: boolean
  noApprovalNeeded: boolean
  routine: boolean
  signature: string
  inputSchema: JsonSchema
  outputSchema: JsonSchema
  outputType: string
}

export interface ReadingPoint {
  time: string
  avg: number
  min: number
  max: number
  count: number
}

export interface ChannelSeries {
  channelId: string
  sensorId: string
  sensorName: string
  metric: string
  unit: string
  bucketMinutes: number
  points: ReadingPoint[]
}
