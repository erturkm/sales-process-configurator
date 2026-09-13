"""Step 22: publish the team and personal workload dashboard web resource.

Uploads webresources/workload.html and adds a "My work" nav item to the Sales Process
Configurator app so it opens as a full page dashboard.
"""
import base64
import os
import re
import time

import dv

P = dv.PREFIX
WR_NAME = f"{P}_workload.html"
WR_DISPLAY = "My Work Dashboard"
HERE = os.path.dirname(os.path.abspath(__file__))
HTML_PATH = os.path.join(HERE, "webresources", "workload.html")

# Chart.js is bundled as a same origin web resource rather than pulled from a CDN: model driven
# apps restrict external script origins and the dashboard has to work on a locked down tenant.
LIB_NAME = f"{P}_chartjs.js"
LIB_PATH = os.path.join(HERE, "webresources", "chartjs.js")

APP_UNIQUE = f"{P}_SalesProcessConfiguratorApp"
SITEMAP_NAME = f"{P}_spcsitemap"
SUBAREA_ID = f"{P}_sub_workload"


LOCK_CODES = ("0x80071151", "0x80048543")


def wait_for_imports(minutes=30):
    """This org periodically installs managed first party solutions in the background. While one
    is running it holds a lock that fails writes and publishes alike, so wait it out first."""
    for _ in range(minutes * 2):
        try:
            running = dv.get("importjobs?$select=solutionname,progress"
                             "&$filter=completedon eq null&$orderby=startedon desc")["value"]
        except RuntimeError:
            return
        if not running:
            return
        job = running[0]
        print(f"  . waiting for '{job.get('solutionname')}' import "
              f"({job.get('progress', 0):.0f}%)", flush=True)
        time.sleep(30)
    print("  ! an import is still running; continuing anyway")


def retry(fn, label, tries=40):
    """Runs fn, retrying while the environment reports a solution lock."""
    for attempt in range(1, tries + 1):
        try:
            return fn()
        except RuntimeError as e:
            if not any(c in str(e) for c in LOCK_CODES):
                raise
            if attempt == tries:
                raise
            print(f"  . {label} blocked by a solution import, retry {attempt}", flush=True)
            time.sleep(30)


def publish(parts, label):
    """Managed solution installs run in the background in this org and hold an exclusive lock
    on Publish, returning 429. Wait the install out rather than failing the deploy."""
    xml = f"<importexportxml>{parts}</importexportxml>"
    retry(lambda: dv.post("PublishXml", {"ParameterXml": xml}), f"publish {label}")
    print(f"  + {label} published")
    return True


def upload_one(name, path, display, description, wrtype):
    content = base64.b64encode(open(path, "rb").read()).decode()
    wr = dv.find_one("webresourceset", f"name eq '{name}'", "webresourceid,name")
    if wr:
        dv.patch(f"webresourceset({wr['webresourceid']})", {"content": content}, solution=True)
        print("  ~ web resource", name)
        return wr["webresourceid"]
    wid = dv.new_id(dv.post("webresourceset", {
        "name": name,
        "displayname": display,
        "description": description,
        "webresourcetype": wrtype,
        "content": content,
    }, solution=True))
    print("  + web resource", name)
    return wid


def upload():
    """Returns (library id, dashboard id). The library must exist before the page loads it."""
    lib = upload_one(LIB_NAME, LIB_PATH, "Chart.js",
                     "Chart.js 4.4.4 (MIT), bundled for the workload dashboard charts.", 3)
    page = upload_one(WR_NAME, HTML_PATH, WR_DISPLAY,
                      "Combined personal and team workload dashboard for tasks and deals.", 1)
    return lib, page


def add_sitemap_item():
    sm = dv.find_one("sitemaps", f"sitemapname eq '{SITEMAP_NAME}'",
                     "sitemapid,sitemapname,sitemapxml")
    if not sm:
        print("  ! sitemap not found; run step11 first")
        return None
    xml = sm["sitemapxml"]
    if SUBAREA_ID in xml:
        xml = re.sub(rf'<SubArea Id="{SUBAREA_ID}".*?</SubArea>', "", xml, flags=re.S)

    sub = (
        f'<SubArea Id="{SUBAREA_ID}" '
        f'Url="$webresource:{WR_NAME}" '
        'Client="All,Web" AvailableOffline="false" PassParams="false">'
        '<Titles><Title LCID="1033" Title="My work" /></Titles>'
        '</SubArea>'
    )

    # The dashboard is a runtime view, not configuration, so it belongs at the top of the
    # Runtime area rather than buried next to the designer.
    area = re.search(r'<Area Id="area_Runtime".*?</Area>', xml, flags=re.S)
    if not area:
        area = re.search(r'<Area\b.*?</Area>', xml, flags=re.S)
    if not area:
        print("  ! could not find an area in the sitemap")
        return sm["sitemapid"]

    seg = area.group(0)
    # A SubArea must follow the group's own <Titles>, so anchor on the first existing SubArea
    # and insert before it; that puts "My work" first in the group.
    first = re.search(r'<SubArea\b', seg)
    if first:
        at = area.start() + first.start()
    else:
        at = area.start() + seg.index("</Group>")
    xml = xml[:at] + sub + xml[at:]

    dv.patch(f"sitemaps({sm['sitemapid']})", {"sitemapxml": xml}, solution=True)
    print("  ~ sitemap subarea added")
    return sm["sitemapid"]


def main():
    wait_for_imports()
    lib_id, wid = retry(upload, "web resource upload")
    publish(f"<webresources><webresource>{lib_id}</webresource>"
            f"<webresource>{wid}</webresource></webresources>", "web resources")

    app = dv.find_one("appmodules", f"uniquename eq '{APP_UNIQUE}'", "appmoduleid,uniquename")
    app_id = app["appmoduleid"] if app else None
    sm_id = retry(add_sitemap_item, "sitemap update")

    parts = (f"<webresources><webresource>{lib_id}</webresource>"
             f"<webresource>{wid}</webresource></webresources>")
    if app_id:
        parts += f"<appmodules><appmodule>{app_id}</appmodule></appmodules>"
    if sm_id:
        parts += f"<sitemaps><sitemap>{sm_id}</sitemap></sitemaps>"
    publish(parts, "app and sitemap")

    print(f"\nStandalone: {dv.ORG}/WebResources/{WR_NAME}")
    if app_id:
        print(f"In app    : {dv.ORG}/main.aspx?appid={app_id}")
    print("done")


if __name__ == "__main__":
    main()
