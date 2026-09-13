"""Step 16: publish the visual process designer web resource and surface it in the app.

Uploads webresources/designer.html as an HTML web resource, embeds it as a full width
"Visual designer" tab on the case process template main form, and adds a standalone
"Process Designer" nav item that opens a blank canvas.
"""
import base64
import os
import re
import uuid

import dv

P = dv.PREFIX
WR_NAME = f"{P}_designer.html"
WR_DISPLAY = "Sales Process Visual Designer"
WR_DESCRIPTION = "Drag and drop designer for outcome driven sales process graphs."
HERE = os.path.dirname(os.path.abspath(__file__))
HTML_PATH = os.path.join(HERE, "webresources", "designer.html")

APP_UNIQUE = f"{P}_SalesProcessConfiguratorApp"
SITEMAP_NAME = f"{P}_spcsitemap"
TEMPLATE_ENTITY = f"{P}_salesprocesstemplate"


def upload():
    content = base64.b64encode(open(HTML_PATH, "rb").read()).decode()
    wr = dv.find_one("webresourceset", f"name eq '{WR_NAME}'", "webresourceid,name")
    if wr:
        dv.patch(f"webresourceset({wr['webresourceid']})",
                 {"content": content, "displayname": WR_DISPLAY, "description": WR_DESCRIPTION},
                 solution=True)
        print("  ~ web resource", wr["webresourceid"])
        return wr["webresourceid"]
    wid = dv.new_id(dv.post("webresourceset", {
        "name": WR_NAME,
        "displayname": WR_DISPLAY,
        "description": WR_DESCRIPTION,
        "webresourcetype": 1,
        "content": content,
    }, solution=True))
    print("  + web resource", wid)
    return wid


DESIGNER_TAB = (
    '<tab name="spc_designer_tab" id="{tabid}" IsUserDefined="0" locklevel="0" '
    'showlabel="true" expanded="true" verticallayout="true">'
    '<labels><label description="Visual designer" languagecode="1033" /></labels>'
    '<columns><column width="100%">'
    '<sections>'
    '<section name="spc_designer_sec" id="{secid}" IsUserDefined="0" locklevel="0" '
    'showlabel="false" showbar="false" columns="1" labelwidth="115" celllabelalignment="Left" '
    'celllabelposition="Left">'
    '<labels><label description="Visual designer" languagecode="1033" /></labels>'
    '<rows>{rows}</rows>'
    '</section>'
    '</sections>'
    '</column></columns></tab>'
)

WR_CELL = (
    '<row><cell id="{cellid}" showlabel="false" rowspan="20" colspan="1" auto="false">'
    '<labels><label description="Visual designer" languagecode="1033" /></labels>'
    '<control id="WebResource_cpcdesigner" classid="{{9FDF5F91-88B1-47f4-AD53-C11EFC01A01D}}">'
    '<parameters>'
    '<Url>{wr}</Url>'
    '<PassParameters>true</PassParameters>'
    '<ShowOnMobileClient>false</ShowOnMobileClient>'
    '<Security>false</Security>'
    '<Scrolling>auto</Scrolling>'
    '<Border>false</Border>'
    '<WebResourceId>{{{wid}}}</WebResourceId>'
    '</parameters>'
    '</control></cell></row>'
)


def designer_tab_xml(wid):
    rows = WR_CELL.format(cellid="{" + str(uuid.uuid4()) + "}", wr=WR_NAME, wid=wid.upper())
    return DESIGNER_TAB.format(tabid="{" + str(uuid.uuid4()) + "}",
                               secid="{" + str(uuid.uuid4()) + "}", rows=rows)


def add_tab_to_form(wid):
    """Splice a full width designer tab into the template main form."""
    forms = dv.get(
        "systemforms?$select=formid,name,formxml,type&"
        f"$filter=objecttypecode eq '{TEMPLATE_ENTITY}' and type eq 2"
    )["value"]
    if not forms:
        print("  ! no main form found for", TEMPLATE_ENTITY)
        return
    for f in forms:
        xml = f["formxml"]
        if "spc_designer_tab" in xml:
            xml = re.sub(r'<tab name="spc_designer_tab".*?</tab>', "", xml, flags=re.S)
        if "<tabs>" not in xml:
            print("  ! form has no <tabs>:", f["name"])
            continue
        xml = xml.replace("</tabs>", designer_tab_xml(wid) + "</tabs>", 1)
        dv.patch(f"systemforms({f['formid']})", {"formxml": xml}, solution=True)
        print("  ~ form tab added:", f["name"])


def add_sitemap_item(app_id):
    sm = dv.find_one("sitemaps", f"sitemapname eq '{SITEMAP_NAME}'",
                     "sitemapid,sitemapname,sitemapxml")
    if not sm:
        print("  ! sitemap not found; run step11 first")
        return None
    xml = sm["sitemapxml"]
    if "spc_sub_designer" in xml:
        xml = re.sub(r'<SubArea Id="spc_sub_designer".*?</SubArea>', "", xml, flags=re.S)
    sub = (
        '<SubArea Id="spc_sub_designer" '
        f'Url="$webresource:{WR_NAME}" '
        'Client="All,Web" AvailableOffline="false" PassParams="false">'
        '<Titles><Title LCID="1033" Title="Process Designer" /></Titles>'
        '</SubArea>'
    )
    # first group of the Configuration area
    m = re.search(r'(<Area Id="area_Configuration".*?<Group [^>]*>.*?)(</Group>)', xml, flags=re.S)
    if not m:
        print("  ! could not find Configuration group in sitemap")
        return sm["sitemapid"]
    xml = xml[:m.end(1)] + sub + xml[m.end(1):]
    dv.patch(f"sitemaps({sm['sitemapid']})", {"sitemapxml": xml}, solution=True)
    print("  ~ sitemap subarea added")
    return sm["sitemapid"]


def main():
    wid = upload()
    dv.post("PublishXml", {"ParameterXml":
        f"<importexportxml><webresources><webresource>{wid}</webresource>"
        f"</webresources></importexportxml>"})
    print("  + web resource published")

    app = dv.find_one("appmodules", f"uniquename eq '{APP_UNIQUE}'", "appmoduleid,uniquename")
    app_id = app["appmoduleid"] if app else None

    sm_id = add_sitemap_item(app_id) if app_id else None
    add_tab_to_form(wid)

    # Web resources are not addable as explicit app components; the sitemap URL reference
    # and the form control dependency are enough for them to be included.

    parts = f"<webresources><webresource>{wid}</webresource></webresources>"
    parts += f"<entities><entity>{TEMPLATE_ENTITY}</entity></entities>"
    if app_id:
        parts += f"<appmodules><appmodule>{app_id}</appmodule></appmodules>"
    if sm_id:
        parts += f"<sitemaps><sitemap>{sm_id}</sitemap></sitemaps>"
    dv.post("PublishXml", {"ParameterXml": f"<importexportxml>{parts}</importexportxml>"})
    print("  + published")

    print(f"\nStandalone designer: {dv.ORG}/WebResources/{WR_NAME}")
    if app_id:
        print(f"App: {dv.ORG}/main.aspx?appid={app_id}")
    print("done")


if __name__ == "__main__":
    main()
