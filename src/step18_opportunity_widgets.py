"""Step 18: runtime widgets on the Opportunity summary tab.

Uploads the process progress rail and the required-document checklist, then splices both into
the right-hand column of the Summary tab of the opportunity main form.
"""
import base64
import os
import re
import uuid

import dv

P = dv.PREFIX
HERE = os.path.dirname(os.path.abspath(__file__))

WIDGETS = [
    (f"{P}_dealprocess.html", "dealprocess.html", "Sales Process Progress",
     "Visual stage rail and task timeline for the applied sales process."),
    (f"{P}_dealdocs.html", "dealdocs.html", "Deal Required Documents",
     "Checklist of required documents with drag and drop upload."),
    (f"{P}_dealpanel.html", "dealpanel.html", "Sales Process Panel",
     "Tabbed shell hosting the process rail and the document checklist."),
]

# The two runtime widgets now live inside one tabbed panel rather than stacked sections, so
# the documents checklist is reachable without scrolling past the whole task timeline.
SEC_PANEL = "spc_summary_panel"
SEC_PROCESS = "spc_summary_process"
SEC_DOCS = "spc_summary_docs"
TARGET_FORMS = ["Opportunity", "Information"]

IFRAME_CLASS = "{9FDF5F91-88B1-47f4-AD53-C11EFC01A01D}"
PICKLIST_CLASS = "{3EF39988-22BB-4f0b-BBBE-64B5A3748AEE}"


def upload(name, filename, label, description):
    content = base64.b64encode(open(os.path.join(HERE, "webresources", filename), "rb").read()).decode()
    wr = dv.find_one("webresourceset", f"name eq '{name}'", "webresourceid")
    if wr:
        wid = wr["webresourceid"]
        dv.patch(f"webresourceset({wid})", {"content": content}, solution=True)
        print("  ~ web resource", name)
    else:
        wid = dv.new_id(dv.post("webresourceset", {
            "name": name, "displayname": label, "description": description,
            "webresourcetype": 1, "content": content,
        }, solution=True))
        print("  + web resource", name)
    dv.post("PublishXml", {"ParameterXml":
        f"<importexportxml><webresources><webresource>{wid}</webresource>"
        f"</webresources></importexportxml>"})
    return wid


def gid():
    return "{" + str(uuid.uuid4()) + "}"


def widget_section(sec_name, label, control_id, wr_name, wid, rows=14):
    return (
        f'<section name="{sec_name}" id="{gid()}" IsUserDefined="0" locklevel="0" '
        f'showlabel="true" showbar="false" columns="1" labelwidth="115" '
        f'celllabelalignment="Left" celllabelposition="Left">'
        f'<labels><label description="{label}" languagecode="1033" /></labels>'
        f'<rows><row><cell id="{gid()}" showlabel="false" rowspan="{rows}" colspan="1" auto="false">'
        f'<labels><label description="{label}" languagecode="1033" /></labels>'
        f'<control id="{control_id}" classid="{IFRAME_CLASS}">'
        f'<parameters><Url>{wr_name}</Url><PassParameters>true</PassParameters>'
        f'<ShowOnMobileClient>false</ShowOnMobileClient><Security>false</Security>'
        f'<Scrolling>auto</Scrolling><Border>false</Border>'
        f'<WebResourceId>{{{wid.upper()}}}</WebResourceId></parameters>'
        f'</control></cell></row></rows></section>'
    )


def field_rows():
    """The two clocks, tagged so the splice stays idempotent."""
    out = []
    for field, label in ((f"{P}_qualificationstatus", "Qualification SLA"),
                         (f"{P}_closestatus", "Close Decision SLA")):
        out.append(
            f'<row><cell id="{gid()}" showlabel="true" locklevel="0">'
            f'<labels><label description="{label}" languagecode="1033" /></labels>'
            f'<control id="{field}" classid="{PICKLIST_CLASS}" datafieldname="{field}" '
            f'disabled="false" /></cell></row>'
        )
    return "".join(out)


def strip(tab):
    """Idempotency: drop anything a previous run of this step put on the tab."""
    for sec in (SEC_PANEL, SEC_PROCESS, SEC_DOCS):
        tab = re.sub(rf'<section name="{sec}".*?</section>', "", tab, flags=re.S)
    for field in (f"{P}_qualificationstatus", f"{P}_closestatus"):
        tab = re.sub(rf'<row>(?:(?!</row>).)*?datafieldname="{field}"(?:(?!</row>).)*?</row>',
                     "", tab, flags=re.S)
    return tab


"""Nothing is stripped from the opportunity form: unlike the case form it is already lean,
and the fields it does carry (estimated value, close date, rating) are all ones a seller reads."""
DROP_FIELDS = ()


def drop_oob_fields(tab):
    """Remove the unused out of the box lookups, and any section left empty as a result."""
    removed = []
    for field in DROP_FIELDS:
        pattern = rf'<row>(?:(?!</row>).)*?datafieldname="{field}"(?:(?!</row>).)*?</row>'
        tab, n = re.subn(pattern, "", tab, flags=re.S)
        if n:
            removed.append(field)
    tab = re.sub(r'<section\b(?:(?!</section>).)*?<rows\s*/>(?:(?!</section>).)*?</section>',
                 "", tab, flags=re.S)
    tab = re.sub(r'<section\b(?:(?!</section>).)*?<rows>\s*</rows>(?:(?!</section>).)*?</section>',
                 "", tab, flags=re.S)
    return tab, removed


def summary_tab_bounds(xml):
    """Locate the Summary tab, falling back to the first tab on forms that name it differently."""
    for pattern in (r'<tab name="Summary"', r'<tab name="general"', r'<tab '):
        m = re.search(pattern, xml)
        if m:
            end = xml.find("</tab>", m.start())
            if end > 0:
                return m.start(), end
    return None


def add_fields(tab):
    """Drop the two SLA picklists at the end of the first section on the tab."""
    m = re.search(r'<section name="[Ss]ummary[^"]*".*?</section>', tab, flags=re.S)
    if not m:
        m = re.search(r'<section [^>]*>.*?</section>', tab, flags=re.S)
        if not m:
            return tab, False
    sec = m.group(0)
    i = sec.rindex("</rows>")
    return tab[:m.start()] + sec[:i] + field_rows() + sec[i:] + tab[m.end():], True


def add_widgets(tab, ids):
    """Right-hand column of the tab, above whatever is already there."""
    o = tab.find("<columns>")
    c = tab.rfind("</columns>")
    if o < 0 or c < 0:
        return tab, False
    cols = tab[o + len("<columns>"):c]
    last = cols.rfind("<sections>")
    if last < 0:
        return tab, False
    secs = widget_section(SEC_PANEL, "Sales process", "WebResource_spcpanel",
                          WIDGETS[2][0], ids[2], rows=28)
    cols = cols[:last + len("<sections>")] + secs + cols[last + len("<sections>"):]
    return tab[:o + len("<columns>")] + cols + tab[c:], True


def main():
    ids = [upload(*w) for w in WIDGETS]

    forms = dv.get("systemforms?$select=formid,name,formxml"
                   "&$filter=objecttypecode eq 'opportunity' and type eq 2")["value"]
    for f in forms:
        if f["name"] not in TARGET_FORMS:
            continue
        xml = f["formxml"]
        bounds = summary_tab_bounds(xml)
        if not bounds:
            print("  ! no summary tab on", f["name"])
            continue
        start, end = bounds
        tab = strip(xml[start:end])
        tab, dropped = drop_oob_fields(tab)
        tab, ok_f = add_fields(tab)
        tab, ok_w = add_widgets(tab, ids)
        if not ok_w:
            print("  ! no column to host widgets on", f["name"])
            continue
        try:
            dv.patch(f"systemforms({f['formid']})", {"formxml": xml[:start] + tab + xml[end:]},
                     solution=True)
            print(f"  ~ summary tab updated: {f['name']} (fields={ok_f}, "
                  f"dropped={','.join(dropped) or 'none'})")
        except RuntimeError as e:
            print("  ! failed on", f["name"], "->", str(e)[-300:])

    dv.publish_all()
    print("done")


if __name__ == "__main__":
    main()
