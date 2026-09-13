"""Step 11: model-driven app + sitemap for the Sales Process Configurator."""
import uuid
import dv

P = dv.PREFIX
APP_UNIQUE = f"{P}_SalesProcessConfiguratorApp"
APP_NAME = "Sales Process Configurator"

AREAS = [
    ("Configuration", "Configuration", [
        ("Process Templates", f"{P}_salesprocesstemplate"),
        ("Match Rules", f"{P}_matchrule"),
        ("Task Templates", f"{P}_processtask"),
        ("Document Packages", f"{P}_documentpackage"),
        ("Document Items", f"{P}_documentitem"),
    ]),
    ("Runtime", "Runtime", [
        ("Opportunities", "opportunity"),
        ("Applied Processes", f"{P}_appliedprocess"),
        ("Required Documents", f"{P}_opportunityrequireddocument"),
        ("Tasks", "task"),
        ("Agent Runs", f"{P}_agentrun"),
    ]),
    ("Sales Setup", "Setup", [
        ("Products", "product"),
        ("Accounts", "account"),
        ("SLAs", "sla"),
        ("Queues", "queue"),
        ("Teams", "team"),
    ]),
]

ENTITIES = sorted({e for _, _, subs in AREAS for _, e in subs})


def sitemap_xml():
    areas = ""
    for title, key, subs in AREAS:
        groups = ""
        subxml = ""
        for stitle, ent in subs:
            subxml += (f'<SubArea Id="sub_{uuid.uuid4().hex[:8]}" Entity="{ent}" Client="All,Outlook,OutlookLaptopClient,'
                       f'OutlookWorkstationClient,Web" AvailableOffline="true" PassParams="false">'
                       f'<Titles><Title LCID="1033" Title="{stitle}" /></Titles></SubArea>')
        groups += (f'<Group Id="grp_{uuid.uuid4().hex[:8]}" IsProfile="false">'
                   f'<Titles><Title LCID="1033" Title="{title}" /></Titles>'
                   f'{subxml}</Group>')
        areas += (f'<Area Id="area_{key}" ShowGroups="true">'
                  f'<Titles><Title LCID="1033" Title="{title}" /></Titles>'
                  f'{groups}</Area>')
    return f'<SiteMap IntroducedVersion="9.0">{areas}</SiteMap>'


ICON_SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32">
<rect x="2" y="4" width="12" height="8" rx="2" fill="#0F6CBD"/>
<rect x="2" y="20" width="12" height="8" rx="2" fill="#8661C5"/>
<rect x="19" y="12" width="11" height="8" rx="2" fill="#F4364C"/>
<path d="M14 8h3a2 2 0 0 1 2 2v4M14 24h3a2 2 0 0 0 2-2v-4" stroke="#0F1B2D" stroke-width="1.6" fill="none"/>
</svg>"""


def ensure_icon():
    import base64
    name = f"{P}_/icons/salesprocessconfigurator.svg"
    content = base64.b64encode(ICON_SVG.encode()).decode()
    wr = dv.find_one("webresourceset", f"name eq '{name}'", "webresourceid,name")
    body = {"name": name, "displayname": "Sales Process Configurator Icon",
            "webresourcetype": 11, "content": content}
    if wr:
        dv.patch(f"webresourceset({wr['webresourceid']})", {"content": content}, solution=True)
        return wr["webresourceid"]
    return dv.new_id(dv.post("webresourceset", body, solution=True))


def main():
    smname = f"{P}_spcsitemap"
    existing_sm = dv.find_one("sitemaps", f"sitemapname eq '{smname}'", "sitemapid,sitemapname")
    if not existing_sm:
        # The first cut of this fork inherited the case solution's sitemap NAME. Rename the row
        # in place rather than creating a second one - the app points at the id, so a rename
        # keeps the app wired while a new record would silently orphan the old nav.
        legacy = dv.find_one("sitemaps", f"sitemapname eq '{P}_cpcsitemap'",
                             "sitemapid,sitemapname")
        if legacy:
            # The platform validates the whole record on update, so the xml has to travel
            # with the rename or it answers "The site map is empty".
            dv.patch(f"sitemaps({legacy['sitemapid']})",
                     {"sitemapname": smname, "sitemapnameunique": smname,
                      "sitemapxml": sitemap_xml()}, solution=True)
            print(f"  ~ renamed sitemap {P}_cpcsitemap -> {smname}")
            existing_sm = {"sitemapid": legacy["sitemapid"]}
    if existing_sm:
        sm_id = existing_sm["sitemapid"]
        dv.patch(f"sitemaps({sm_id})", {"sitemapxml": sitemap_xml()}, solution=True)
        print("  ~ sitemap")
    else:
        sm_id = dv.new_id(dv.post("sitemaps", {"sitemapname": smname, "sitemapnameunique": smname, "sitemapxml": sitemap_xml()}, solution=True))
        print("  + sitemap", sm_id)

    app = dv.find_one("appmodules", f"uniquename eq '{APP_UNIQUE}'", "appmoduleid,uniquename")
    body = {
        "name": APP_NAME,
        "uniquename": APP_UNIQUE,
        "description": "Configure case process blueprints: targeting rules, SLAs, BPF stages, task plans and document packages.",
        "clienttype": 4,
        "webresourceid": ensure_icon(),
        # 0 = classic single-session shell. 1 is the multi-session (Customer
        # Service workspace) shell, whose session/tab rail eats vertical space
        # we want for the designer canvas.
        "navigationtype": 0,
    }
    if app:
        app_id = app["appmoduleid"]
        dv.patch(f"appmodules({app_id})", {
            "name": APP_NAME,
            "description": body["description"],
            "navigationtype": 0,
        }, solution=True)
        print("  ~ app", app_id)
    else:
        app_id = dv.new_id(dv.post("appmodules", body, solution=True))
        print("  + app", app_id)

    comps = [{"@odata.type": "Microsoft.Dynamics.CRM.sitemap", "sitemapid": sm_id}]
    for e in ENTITIES:
        md = dv.get(f"EntityDefinitions(LogicalName='{e}')?$select=MetadataId")
        comps.append({"@odata.type": "Microsoft.Dynamics.CRM.entity", "entityid": md["MetadataId"]})
    dv.post("AddAppComponents", {"AppId": app_id, "Components": comps})
    print("  + app components:", len(comps))

    dv.post("PublishXml", {"ParameterXml":
        f"<importexportxml><appmodules><appmodule>{app_id}</appmodule></appmodules>"
        f"<sitemaps><sitemap>{sm_id}</sitemap></sitemaps></importexportxml>"})
    print(f"\nApp URL: {dv.ORG}/main.aspx?appid={app_id}")
    print("done")


if __name__ == "__main__":
    main()
