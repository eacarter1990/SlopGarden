// SlopGarden ADC bridge: prints the Arduino's analog inputs over USB serial for the
// SlopGarden client's "arduino-serial-adc" plugin. See docs/hardware/arduino-adc-bridge.md.
//
// Output, one line per interval at 115200 baud:  A0=512,A1=300,A2=1023,A3=0
// Lines starting with '#' are ignored by the client.

const unsigned long BAUD = 115200;
const unsigned long INTERVAL_MS = 1000;
const uint8_t SAMPLES = 8;  // averaged per reading to smooth noisy soil probes

// Inputs to report. On an Uno/Nano these are A0..A5; add A6/A7 on a Nano.
const uint8_t PINS[] = {A0, A1, A2, A3};
const char* const NAMES[] = {"A0", "A1", "A2", "A3"};
const uint8_t PIN_COUNT = sizeof(PINS) / sizeof(PINS[0]);

// Optional: power capacitive/resistive probes only while measuring to slow electrode corrosion.
// Set to a digital pin wired to the probes' VCC, or -1 to keep them powered.
const int PROBE_POWER_PIN = -1;
const unsigned long PROBE_SETTLE_MS = 50;

unsigned long lastSent = 0;

void setup() {
  Serial.begin(BAUD);
  if (PROBE_POWER_PIN >= 0) {
    pinMode(PROBE_POWER_PIN, OUTPUT);
    digitalWrite(PROBE_POWER_PIN, LOW);
  }
  Serial.println(F("# slopgarden-adc-bridge 1"));
}

int readAveraged(uint8_t pin) {
  analogRead(pin);  // the first conversion after switching the multiplexer is unreliable
  long sum = 0;
  for (uint8_t i = 0; i < SAMPLES; i++) {
    sum += analogRead(pin);
    delay(2);
  }
  return (int)(sum / SAMPLES);
}

void loop() {
  unsigned long now = millis();
  if (now - lastSent < INTERVAL_MS) return;
  lastSent = now;

  if (PROBE_POWER_PIN >= 0) {
    digitalWrite(PROBE_POWER_PIN, HIGH);
    delay(PROBE_SETTLE_MS);
  }

  for (uint8_t i = 0; i < PIN_COUNT; i++) {
    if (i > 0) Serial.print(',');
    Serial.print(NAMES[i]);
    Serial.print('=');
    Serial.print(readAveraged(PINS[i]));
  }
  Serial.println();

  if (PROBE_POWER_PIN >= 0) digitalWrite(PROBE_POWER_PIN, LOW);
}
