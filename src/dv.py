"""Shared Dataverse Web API helper for the Sales Process Configurator build.

Set the target environment before running any step, for example:

    export DATAVERSE_URL="https://yourorg.crm.dynamics.com"

Authentication uses the Azure CLI, so sign in first with `az login`.
"""
import datetime
import json, os, subprocess, time, urllib.parse, urllib.request, urllib.error

ORG = os.environ.get("DATAVERSE_URL", "").rstrip("/")
if not ORG:
    raise SystemExit(
        "DATAVERSE_URL is not set.\n"
        '  export DATAVERSE_URL="https://yourorg.crm.dynamics.com"'
    )
API = ORG + "/api/data/v9.2"
PREFIX = os.environ.get("SPC_PREFIX", "spc")
SOLUTION = os.environ.get("SPC_SOLUTION", "SalesProcessConfigurator")
_tok = {"v": None, "exp": 0}


def token(force=False):
    # az serves tokens from its own cache, so a token it just handed us may only have
    # minutes of life left. Trust the token's real expiry, not when we asked for it --
    # a long-running script (a design pass takes ~10 min) otherwise 401s mid-flight.
    if _tok["v"] and not force and time.time() < _tok["exp"] - 300:
        return _tok["v"]
    out = json.loads(subprocess.check_output(
        ["az", "account", "get-access-token", "--resource", ORG, "-o", "json"], text=True))
    _tok["v"] = out["accessToken"]
    exp = out.get("expires_on") or out.get("expiresOn")
    try:
        _tok["exp"] = float(exp)
    except (TypeError, ValueError):
        # Older az emits a local-time string instead of an epoch.
        try:
            _tok["exp"] = datetime.datetime.fromisoformat(str(exp)).timestamp()
        except Exception:
            _tok["exp"] = time.time() + 900
    if force:
        # A forced refresh that returns the same expired token would loop for ever.
        _tok["exp"] = max(_tok["exp"], time.time() + 60)
    return _tok["v"]


def call(method, path, body=None, headers=None, solution=False):
    url = path if path.startswith("http") else API + "/" + path.lstrip("/")
    url = urllib.parse.quote(url, safe=":/?&$=,()'*+%@.-_~!;")
    data = json.dumps(body).encode() if body is not None else None
    h = {"Authorization": "Bearer " + token(), "Accept": "application/json",
         "OData-MaxVersion": "4.0", "OData-Version": "4.0"}
    if data:
        h["Content-Type"] = "application/json"
    if solution:
        h["MSCRM.SolutionUniqueName"] = SOLUTION
    if headers:
        h.update(headers)
    req = urllib.request.Request(url, data=data, headers=h, method=method)
    refreshed = False
    # Publishing is single threaded per environment and a solution import anywhere in the org
    # locks it out with a 429. Every build script publishes, so the retry belongs here rather
    # than being re-invented, badly, in each one.
    for attempt in range(8):
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                raw = r.read()
                loc = r.headers.get("OData-EntityId")
                if not raw:
                    return {"_location": loc}
                out = json.loads(raw)
                if loc:
                    out["_location"] = loc
                return out
        except urllib.error.HTTPError as e:
            body = e.read().decode(errors="ignore")
            if e.code == 429 and attempt < 7:
                wait = 20 * (attempt + 1)
                print(f"    publish locked, retrying in {wait}s ({attempt + 1}/7)")
                time.sleep(wait)
                req = urllib.request.Request(url, data=data, headers=h, method=method)
                continue
            if e.code == 401 and not refreshed:
                # The token died in flight. Get a fresh one and replay once.
                refreshed = True
                h["Authorization"] = "Bearer " + token(force=True)
                req = urllib.request.Request(url, data=data, headers=h, method=method)
                continue
            raise RuntimeError(f"{method} {url} -> {e.code}\n{body[:1500]}") from None


def get(path):
    return call("GET", path)


def post(path, body, solution=False):
    return call("POST", path, body, solution=solution)


def patch(path, body, solution=False):
    return call("PATCH", path, body, headers={"If-Match": "*"}, solution=solution)


def upsert(path, body):
    """PATCH with If-None-Match omitted so it creates or updates by alternate key."""
    return call("PATCH", path, body, headers={"If-Match": "*"})


def new_id(resp):
    loc = resp.get("_location", "")
    return loc.split("(")[-1].rstrip(")") if loc else None


def find_one(entityset, filt, select="*"):
    r = get(f"{entityset}?$select={select}&$filter={filt}&$top=1")
    v = r.get("value", [])
    return v[0] if v else None


def label(text, lcid=1033):
    return {"@odata.type": "Microsoft.Dynamics.CRM.Label",
            "LocalizedLabels": [{"@odata.type": "Microsoft.Dynamics.CRM.LocalizedLabel",
                                 "Label": text, "LanguageCode": lcid}]}


def publish_all():
    call("POST", "PublishAllXml", {})
