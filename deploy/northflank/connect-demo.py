"""Connect the local demo through authenticated, audited application APIs."""
import http.cookiejar
import json
import os
import urllib.error
import urllib.request

base = os.environ.get("PLANTOPS_BASE_URL", "http://127.0.0.1:8080").rstrip("/") + "/api/v1/"
plant = "10000000-0000-0000-0000-000000000001"
asset = "30000000-0000-0000-0000-000000000001"
opener = urllib.request.build_opener(
    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
csrf = None

def request(path, method="GET", body=None):
    headers = {}
    if body is not None:
        headers["Content-Type"] = "application/json"
    if method != "GET":
        headers["X-CSRF-TOKEN"] = csrf
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(base + path, data, headers, method=method)
    try:
        with opener.open(req, timeout=30) as response:
            payload = response.read()
            return json.loads(payload) if payload else None
    except urllib.error.HTTPError as error:
        # Do not log request bodies, cookies or the login password.
        raise RuntimeError(f"{method} {path}: HTTP {error.code}") from None

def unique(items, description):
    if len(items) > 1:
        raise RuntimeError(f"Duplicate {description}; inspect configuration.")
    return items[0] if items else None

csrf = request("auth/csrf")["token"]
request("auth/login", "POST", {
    "userName": "admin", "password": os.environ.pop("DEMO_PASSWORD")})
csrf = request("auth/csrf")["token"]
sources_path = f"plants/{plant}/data-sources"
sources = request(sources_path)
source = unique([s for s in sources if s["kind"] == "OpcUa"
    and s["opcUa"]["endpointReference"] == "industrial-sim-workbench"], "source")
source_body = {"name": "IndustrialSim P-101", "kind": "OpcUa", "enabled": True,
    "opcUa": {"endpointReference": "industrial-sim-workbench", "publishingIntervalMs": 1000}}
if source is None:
    source = request(sources_path, "POST", source_body)
elif not source["enabled"]:
    source_body.update(name=source["name"], version=source["version"], opcUa=source["opcUa"])
    source = request(f"{sources_path}/{source['id']}", "PATCH", source_body)

points_path = f"assets/{asset}/measurement-points"
points = request(points_path)
targets = [
    ("VIBRATION_RMS", "Vibration RMS", "Vibration", "mm/s", "vibration"),
    ("TEMPERATURE", "IndustrialSim temperature", "Temperature", "degC", "temperature"),
    ("REFERENCE_TEMPERATURE", "IndustrialSim reference temperature", "Temperature", "degC", "referenceTemperature"),
]
for code, name, quantity, unit, node in targets:
    point = unique([p for p in points if p["code"] == code], f"point {code}")
    if point is None:
        point = request(points_path, "POST", {"code": code, "name": name,
            "quantity": quantity, "canonicalUnit": unit, "expectedIntervalMs": 1000,
            "staleAfterMs": 5000, "enabled": True})
    if not point["enabled"] or point["quantity"] != quantity or point["canonicalUnit"] != unit:
        raise RuntimeError(f"Incompatible point {code}; inspect configuration.")
    path = f"{points_path}/{point['id']}/source-mappings"
    mappings = request(path)
    identifier = f"plantops-p101/{node}"
    matching = unique([m for m in mappings if m["dataSourceId"] == source["id"]
        and json.loads(m["sourceParameters"])["Identifier"] == identifier], f"mapping {code}")
    for old in mappings:
        if not old["enabled"] or (matching and old["id"] == matching["id"]):
            continue
        old_source = next((s for s in sources if s["id"] == old["dataSourceId"]), None)
        if old_source is None or old_source["kind"] != "Simulation":
            raise RuntimeError(f"Conflicting custom mapping for {code}; inspect configuration.")
        body = {k: old[k] for k in ("dataSourceId", "sourceKey", "rawUnit", "scale", "offset", "version")}
        body["enabled"] = False
        request(f"{path}/{old['id']}", "PATCH", body)
    body = {"dataSourceId": source["id"], "sourceKey": "industrial-sim", "rawUnit": unit,
        "scale": 1, "offset": 0, "enabled": True, "opcUa": {
            "namespaceUri": "urn:industrial-sim:runtime", "identifierType": "String",
            "identifier": identifier, "samplingIntervalMs": 1000, "queueSize": 10,
            "samplingMode": "Periodic"}}
    if matching is None:
        request(path, "POST", body)
    elif not matching["enabled"]:
        body["version"] = matching["version"]
        request(f"{path}/{matching['id']}", "PATCH", body)
    print(f"Configured {code}", flush=True)
print("IndustrialSim demo connected. No model requests were made.", flush=True)
