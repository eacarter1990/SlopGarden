# Arduino ADC bridge

The Raspberry Pi has no analog inputs. Besides an ADS1115 on I²C (`ads1115` plugin), any Arduino
(Uno, Nano, Pro Micro, …) connected by USB can serve as an analog-to-digital converter for soil
moisture probes, light-dependent resistors, potentiometers or pressure sensors. The client's
`arduino-serial-adc` plugin reads it.

## 1. Flash the sketch

Open [`slopgarden-adc-bridge/slopgarden-adc-bridge.ino`](slopgarden-adc-bridge/slopgarden-adc-bridge.ino)
in the Arduino IDE (or `arduino-cli compile --upload -b arduino:avr:nano -p /dev/ttyUSB0 slopgarden-adc-bridge`),
adjust `PINS`/`NAMES` to the inputs you use and upload. The serial monitor at 115200 baud should show:

```
# slopgarden-adc-bridge 1
A0=512,A1=300,A2=1023,A3=0
```

## 2. Wire and connect

- Probe signal → `A0`…`A7`, probe VCC → 5 V (or 3.3 V for 3.3 V probes; the Arduino's ADC reference is
  its supply voltage), GND → GND.
- Optional: power probes from a digital pin (`PROBE_POWER_PIN`) so resistive probes only conduct while
  measuring; this greatly slows electrode corrosion.
- Plug the Arduino into the Pi. It appears as `/dev/ttyUSB0` (CH340/FTDI) or `/dev/ttyACM0` (native USB).
  The service user is in the `dialout` group (set up by `install.sh`). Prefer the stable
  `/dev/serial/by-id/…` path when several USB serial devices are attached.

## 3. Add the sensor

Add a sensor with plugin `arduino-serial-adc` in the web app (or let the AI do it), one per input:

| Setting | Default | |
|---|---|---|
| `port` | *(required)* | `/dev/ttyUSB0`, `/dev/serial/by-id/usb-1a86_…`, `COM3` on Windows |
| `baud` | `115200` | Must match the sketch |
| `channel` | `A0` | Input name as printed by the sketch |
| `maxAgeSec` | `10` | A read fails (reported as a sensor error) when the newest line for the channel is older |

Several sensors may share one port; the client opens it once. The channel key is `raw` with metric
`adc_raw` (0–1023 on 10-bit boards). Calibrate it to a percentage with `two_point`, e.g. for a capacitive
soil probe measured in dry air (raw 620) and in water (raw 280):

```json
{ "type": "two_point", "rawLow": 620, "valueLow": 0, "rawHigh": 280, "valueHigh": 100, "clamp": true }
```

## Protocol

Plain text lines terminated by `\n` (a trailing `\r` is ignored), 8N1 at the configured baud rate.

- A data line is a list of `name=value` pairs separated by `,`, `;`, spaces or tabs, e.g.
  `A0=512,A1=300` or `A0=512 BAT=3.91`. Values are decimal numbers (`.` as decimal separator).
  Names are case-insensitive; the latest value per name is kept.
- Lines starting with `#` and malformed pairs are ignored, so a sketch can print diagnostics.
- The client asserts DTR when opening the port (which resets most Arduinos) and reopens the port every
  5 s after an error, e.g. when the cable is unplugged.

Any microcontroller (ESP32, RP2040, …) that prints this format works the same way.
