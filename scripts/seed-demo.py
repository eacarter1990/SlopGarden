#!/usr/bin/env python3
"""Wire the emulator-backed client into the seeded demo garden through the public API.

Approves the pending demo device, adopts every hardware item the emulator plugin discovered and
targets it at the right bed/plant, then adds demo safety interlocks and a schedule. Idempotent.

Usage: scripts/seed-demo.py [--server http://localhost:5080] [--user admin] [--password slopgarden]
"""
import argparse
import http.cookiejar
import json
import sys
import time
import urllib.error
import urllib.request

# emulator id -> (target type, bed name, plant name, extra update args)
MAPPING = {
    "tomato-soil-moisture": ("bed", "Tomato bed", None, {}),
    "tomato-soil-temperature": ("bed", "Tomato bed", None, {}),
    "basil-soil-moisture": ("plant", "Greenhouse herbs", "Genovese basil", {}),
    "lettuce-soil-moisture": ("bed", "Lettuce bed", None, {}),
    "greenhouse-air-temperature": ("bed", "Greenhouse herbs", None, {}),
    "greenhouse-air-humidity": ("bed", "Greenhouse herbs", None, {}),
    "greenhouse-light": ("bed", "Greenhouse herbs", None, {}),
    "water-tank-level": ("garden", None, None, {}),
    "tomato-valve": ("bed", "Tomato bed", None, {}),
    "lettuce-valve": ("bed", "Lettuce bed", None, {}),
    "basil-drip": ("plant", "Greenhouse herbs", "Genovese basil", {}),
    "main-pump": ("garden", None, None, {}),
    "grow-light": ("bed", "Greenhouse herbs", None, {}),
    "heater": ("bed", "Greenhouse herbs", None, {}),
    "fertilizer-doser": ("garden", None, None, {"flowRateMlPerMin": 60}),
    "tomato-cam": ("bed", "Tomato bed", None, {}),
    "lettuce-cam": ("bed", "Lettuce bed", None, {}),
}


class Api:
    def __init__(self, base):
        self.base = base.rstrip("/")
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def call(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(self.base + path, data=data, method=method, headers={"Content-Type": "application/json"})
        try:
            with self.opener.open(req, timeout=30) as res:
                text = res.read().decode()
                return json.loads(text) if text else None
        except urllib.error.HTTPError as e:
            raise RuntimeError(f"{method} {path} -> {e.code}: {e.read().decode()}") from None

    def op(self, tool, **args):
        return self.call("POST", f"/api/ops/{tool}", args)


def wait(what, probe, timeout=120):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = probe()
        if value:
            return value
        time.sleep(2)
    sys.exit(f"Timed out waiting for {what}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--server", default="http://localhost:5080")
    ap.add_argument("--user", default="admin")
    ap.add_argument("--password", default="slopgarden")
    ap.add_argument("--device-name", default="Demo Pi (emulated)")
    a = ap.parse_args()

    api = Api(a.server)
    wait("server", lambda: _try(lambda: api.call("GET", "/api/health")))
    api.call("POST", "/api/auth/login", {"username": a.user, "password": a.password})

    def find_device():
        devices = api.op("list_devices")
        return next((d for d in devices if d["status"] in ("pending", "approved")), None)

    device = wait("the client to register (is it running?)", find_device)
    if device["status"] == "pending":
        print(f"Approving device {device['hostname']}")
        api.op("approve_device", deviceId=device["id"], name=a.device_name)
    device_id = device["id"]

    detail = wait("discovered hardware", lambda: (lambda d: d if len(d["discovered"]) >= len(MAPPING) else None)(api.op("get_device", deviceId=device_id)))
    beds = {b["name"]: b["id"] for b in api.op("list_beds")}
    plants = {p["name"]: p["id"] for p in api.op("list_plants")}

    adopted = {}
    for h in detail["discovered"]:
        emu_id = h["suggestedSettings"].get("sensorId") or h["suggestedSettings"].get("actuatorId") or h["suggestedSettings"].get("cameraId") or h["address"]
        if emu_id not in MAPPING:
            continue
        target_type, bed, plant, extra = MAPPING[emu_id]
        if h.get("adoptedComponentId"):
            adopted[emu_id] = (h["kind"], h["adoptedComponentId"])
            continue
        args = {"discoveredId": h["id"], "targetType": target_type, "readIntervalSec": 5}
        if bed:
            args["bedId"] = beds[bed]
        if plant:
            args["plantId"] = plants[plant]
        r = api.op("adopt_discovered_hardware", **args)
        adopted[emu_id] = (r["kind"], r["componentId"])
        if extra and r["kind"] == "actuator":
            api.op("update_actuator", actuatorId=r["componentId"], **extra)
        print(f"  + {r['kind']:<8} {r['name']}  ->  {plant or bed or 'garden'}")

    sensors = api.op("list_sensors", deviceId=device_id)
    channel = {s["settings"].get("sensorId"): s["channels"][0]["id"] for s in sensors if s["channels"]}
    for s in sensors:
        m = s["channels"][0]["metric"] if s["channels"] else None
        if m == "soil_moisture_pct" and s["channels"][0].get("alertLow") is None:
            api.op("update_sensor_channel", channelId=s["channels"][0]["id"], alertLow=20)

    limits = api.op("list_safety_limits")
    if not any(l["limitType"] == "tank_low_lockout" for l in limits) and "water-tank-level" in channel:
        api.op("set_safety_limit", scopeType="global", limitType="tank_low_lockout", value=15, channelId=channel["water-tank-level"],
               notes="No pumping or watering when the water tank is below 15 %")
    if not any(l["limitType"] == "max_temperature_cutoff" for l in limits) and "greenhouse-air-temperature" in channel and "heater" in adopted:
        api.op("set_safety_limit", scopeType="actuator", scopeId=adopted["heater"][1], limitType="max_temperature_cutoff", value=30,
               channelId=channel["greenhouse-air-temperature"], notes="Heater off above 30 °C greenhouse air")

    schedules = api.op("list_schedules")
    if not any(s["schedule"]["name"] == "Basil morning drip" for s in schedules) and "basil-drip" in adopted:
        api.op("create_schedule", name="Basil morning drip", kind="actuator", cron="30 7 * * *", actuatorId=adopted["basil-drip"][1], durationSec=45)
    if not any(s["schedule"]["kind"] == "ai_review" for s in schedules):
        api.op("create_schedule", name="Daily AI garden check", kind="ai_review", cron="0 8 * * *",
               payload={"prompt": "Morning check: compare soil moisture to each plant's target and water anything that is too dry."})

    print(f"Demo garden wired: {len(adopted)} components on device {device_id}")


def _try(f):
    try:
        return f()
    except Exception:
        return None


if __name__ == "__main__":
    main()
