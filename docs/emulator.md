# Sensor emulator

`src/SlopGarden.Emulator` is a virtual garden for demos and tests without hardware: sensors driven by
signal models, actuators that change those sensors while they are on, and cameras that serve a static
garden photo with a timestamp overlay. The client talks to it through the built-in `emulator-sensor`,
`emulator-actuator` and `emulator-camera` plugins.

```bash
dotnet run --project src/SlopGarden.Emulator          # http://0.0.0.0:5090
```

| URL | What |
|---|---|
| `http://localhost:5090/` | Control page: live values, overrides, actuator states and stall faults, camera previews, time scale |
| `http://localhost:5090/openapi/v1.json` | OpenAPI document of the REST API below |

Settings (`appsettings.json`, `--Emulator:Key value` or `Emulator__Key=value`):

| Key | Default | |
|---|---|---|
| `--urls` | `http://0.0.0.0:5090` | Listen address |
| `Emulator:ScenarioFile` | *(none)* | JSON scenario loaded at startup and rewritten whenever definitions change. Without it the built-in demo garden is used and changes are lost on restart. |
| `Emulator:TickMilliseconds` | `200` | Physics step (20–5000 ms) |

Point a client at a non-default emulator with `Plugins:Emulator:BaseUrl` (client setting) or per
component with the `emulatorUrl` setting.

## Default scenario

| Sensors | Actuators | Cameras |
|---|---|---|
| `tomato-soil-moisture`, `basil-soil-moisture`, `lettuce-soil-moisture` (drying), `tomato-soil-temperature`, `greenhouse-air-temperature`, `greenhouse-light` (daily sine), `greenhouse-air-humidity` (random walk), `water-tank-level` (constant) | `tomato-valve`, `lettuce-valve`, `basil-drip`, `main-pump`, `grow-light`, `heater`, `fertilizer-doser` | `tomato-cam`, `lettuce-cam` |

Couplings: the valves raise their bed's soil moisture (1.2–2 %/min) and drain the tank; the pump drains
the tank; the grow light adds light; the heater warms the air. `POST /api/scenario/reset` restores it.

### Hydroponic NFT rack

The default scenario also contains a nutrient film technique (NFT) rack with its own reservoir:

| Id | Kind / metric | Behaviour |
|---|---|---|
| `hydro-ec` | `ec_ms_cm` | Starts at 1.6, rises ~0.02/h as plants take up water; +2.0/min while a nutrient doser runs |
| `hydro-ph` | `ph` | Starts at 6.0, drifts up ~0.03/h; −1.5/min while `doser-ph-down` runs, +1.5/min for `doser-ph-up` |
| `hydro-water-temperature` | `water_temperature_c` | Daily sine 18–23 °C |
| `hydro-reservoir-level` | `water_level_pct` | Starts at 80 %, drops ~0.4 %/h (evaporation and uptake) |
| `hydro-flow` | `flow_l_min` | 0 when the pump is off; reads 3.2 L/min (`while_on` coupling) while `nft-pump` runs and is not stalled |
| `hydro-dissolved-oxygen` | `dissolved_oxygen_mg_l` | ~7.5 mg/L while `air-pump` runs (capped at 7.6); falls ~0.6 mg/L per hour without aeration |
| `nft-pump` | `pump` | Circulation pump feeding the channels |
| `air-pump` | `pump` | Air stone in the reservoir |
| `doser-nutrient-a`, `doser-nutrient-b` | `fertilizer_doser` | Two-part nutrient peristaltic pumps |
| `doser-ph-down`, `doser-ph-up` | `fertilizer_doser` | pH adjustment pumps |
| `hydro-cam` | camera (`nft-rack.jpg`) | Top-down view of the rack with lettuce heads in the channels |

Typical demo: configure `nft-pump` as a `circulation_pump` with `continuousRun` and a flow watch on
`hydro-flow`, then press **Stall** on the pump card (or `PUT /api/actuators/nft-pump/fault`): the pump still
reports on, the flow drops to 0 and the client raises a `flow_stall` event and switches the pump off.

## Model

- **Signal models** (`model.type`): `constant` (`value`); `sine` (`min`, `max`, `periodSec`, `phase` =
  seconds into the local-time period where the wave crosses its midpoint rising); `random_walk` (`min`,
  `max`, `step` per √s, `start`); `drying` (`start`, `ratePerHour`, `floor`). `floor`/`ceiling` clamp any model.
- **Noise**: gaussian with standard deviation `noise`, rounded to `decimals`.
- **Couplings** (`mode`, default `rate`):
  - `rate`: while `actuatorId` is on, `sensorId` changes by `ratePerMinute` × level (default level 1)
    per simulated minute. Stateful models (drying, random walk, constant) keep the change; on `sine` it is an
    offset that fades with time constant `offsetDecaySec` (default 1800 s).
  - `while_on`: while `actuatorId` is on, `sensorId` reads `value` × level (plus the sensor's noise) instead of
    its model; when it is off the sensor follows its own model again. Meant for flow meters and similar
    "only while running" signals. Several active `while_on` couplings on one sensor add up.
- **Faults**: a *stalled* actuator keeps its on/off state (so the client sees what it commanded) but all of
  its couplings are inactive — a dead pump, a clogged line or an empty reservoir. Faults are runtime state;
  they are not saved in the scenario file and are cleared by a reset or scenario load.
- **Time scale**: simulated seconds per real second (0 < factor ≤ 100000). Physics and the sine clock use
  simulated time; override durations are real seconds.
- **Override**: pins a sensor to a value (optionally for `durationSec`), e.g. to simulate a broken probe
  or an empty tank.

## REST API

All bodies are JSON (camelCase). Errors are `{"error": "..."}` with 400 (invalid input), 404 (unknown id)
or 409 (duplicate id).

### Sensors

| Method | Path | Body / query | Returns |
|---|---|---|---|
| GET | `/api/sensors` | | All sensors with definition, current value and override |
| GET | `/api/sensors/{id}` | | One sensor |
| GET | `/api/sensors/{id}/value` | | `{id, value, metric, unit, time}` — what plugins read (noise applied) |
| POST | `/api/sensors` | sensor definition | 201 + sensor |
| PUT | `/api/sensors/{id}` | any of `name, metric, unit, model, noise, decimals, offsetDecaySec` | Updated sensor |
| DELETE | `/api/sensors/{id}` | | 204 (couplings to it are removed) |
| POST | `/api/sensors/{id}/override` | `{value, durationSec?}` | Sensor |
| DELETE | `/api/sensors/{id}/override` | | Sensor |

Sensor definition:

```json
{
  "id": "pepper-soil-moisture",
  "name": "Pepper pot soil moisture",
  "metric": "soil_moisture_pct",
  "unit": "%",
  "model": { "type": "drying", "start": 50, "ratePerHour": 0.8, "floor": 5, "ceiling": 100 },
  "noise": 0.2,
  "decimals": 1
}
```

### Actuators

| Method | Path | Body | Returns |
|---|---|---|---|
| GET | `/api/actuators` | | `{id, name, kind, on, level, since, stalled}` per actuator |
| GET | `/api/actuators/{id}` | | Actuator including its recent `history` (state changes and faults) |
| PUT | `/api/actuators/{id}/state` | `{on, level?, source?}` (level 0–1) | Actuator |
| PUT | `/api/actuators/{id}/fault` | `{stalled}` | Actuator. `stalled: true` disables its couplings while it keeps reporting on |

### Cameras

| Method | Path | Body / query | Returns |
|---|---|---|---|
| GET | `/api/cameras` | | `{id, name, image, customImage, snapshotUrl}` per camera |
| GET | `/api/cameras/{id}` | | One camera |
| GET | `/api/cameras/{id}/snapshot` | `?width=&height=` | JPEG with camera name and simulated time overlaid (the unmodified image if SkiaSharp's native library is unavailable) |
| PUT | `/api/cameras/{id}/image` | raw JPEG/PNG/WebP body | Replaces the camera image (until restart/reset) |

### Scenario

| Method | Path | Body | Returns |
|---|---|---|---|
| GET | `/api/scenario` | | Name, time scale, simulated time, sensors, actuators, cameras, couplings |
| GET | `/api/scenario/export` | | Scenario document (same format as `Emulator:ScenarioFile`) |
| PUT | `/api/scenario` | scenario document | Replaces everything |
| POST | `/api/scenario/reset` | | Restores the default scenario |
| POST | `/api/scenario/time-scale` | `{factor}` | `{timeScale}` |

Scenario document:

```json
{
  "name": "Balcony",
  "timeScale": 1,
  "sensors": [ { "id": "pot-moisture", "name": "Pot", "metric": "soil_moisture_pct", "unit": "%",
                 "model": { "type": "drying", "start": 45, "ratePerHour": 1, "floor": 5 } },
               { "id": "pot-flow", "name": "Pot pump flow", "metric": "flow_l_min", "unit": "L/min",
                 "model": { "type": "constant", "value": 0, "floor": 0 } } ],
  "actuators": [ { "id": "pot-pump", "name": "Pot pump", "kind": "pump" } ],
  "cameras": [ { "id": "pot-cam", "name": "Pot cam", "image": "tomato-bed.jpg" } ],
  "couplings": [ { "actuatorId": "pot-pump", "sensorId": "pot-moisture", "ratePerMinute": 3 },
                 { "actuatorId": "pot-pump", "sensorId": "pot-flow", "mode": "while_on", "value": 1.5 } ]
}
```

Camera `image` is a bundled image (`tomato-bed.jpg`, `lettuce-bed.jpg`, `nft-rack.jpg`) or a path to a JPEG/PNG
file. The bundled images are rendered by `scripts/generate-emulator-images.py`.

## Examples

```bash
# Simulate an empty water tank for 10 minutes (trips tank-level interlocks)
curl -X POST localhost:5090/api/sensors/water-tank-level/override -H 'content-type: application/json' \
     -d '{"value": 3, "durationSec": 600}'

# Run the garden 60× faster (a 1-minute watering moves moisture as much as 1 hour would)
curl -X POST localhost:5090/api/scenario/time-scale -H 'content-type: application/json' -d '{"factor": 60}'

# Simulate a dead NFT pump (still "on", but no flow) and fix it again
curl -X PUT localhost:5090/api/actuators/nft-pump/fault -H 'content-type: application/json' -d '{"stalled": true}'
curl -X PUT localhost:5090/api/actuators/nft-pump/fault -H 'content-type: application/json' -d '{"stalled": false}'

# Use your own photo for a camera
curl -X PUT localhost:5090/api/cameras/tomato-cam/image -H 'content-type: image/jpeg' --data-binary @bed.jpg
```
