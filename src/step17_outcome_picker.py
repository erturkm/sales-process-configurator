"""Step 17: runtime outcome picker web resource on the Task form.

Places the outcome picker plus the runtime columns the engine stamps (branch path, chosen
outcome, comment) directly on the main tab of the OOB task forms, in the right-hand column.
"""
import base64
import os
import re
import uuid

import dv

P = dv.PREFIX
WR_NAME = f"{P}_outcome.html"
HERE = os.path.dirname(os.path.abspath(__file__))
HTML_PATH = os.path.join(HERE, "webresources", "outcome.html")

TAB_NAME = "spc_outcome_tab"


def upload():
    content = base64.b64encode(open(HTML_PATH, "rb").read()).decode()
    wr = dv.find_one("webresourceset", f"name eq '{WR_NAME}'", "webresourceid,name")
    if wr:
        dv.patch(f"webresourceset({wr['webresourceid']})", {"content": content}, solution=True)
        print("  ~ web resource", wr["webresourceid"])
        wid = wr["webresourceid"]
    else:
        wid = dv.new_id(dv.post("webresourceset", {
            "name": WR_NAME,
            "displayname": "Sales Process Outcome Picker",
            "description": "Renders the configured outcome buttons for a process task.",
            "webresourcetype": 1,
            "content": content,
        }, solution=True))
        print("  + web resource", wid)
    dv.post("PublishXml", {"ParameterXml":
        f"<importexportxml><webresources><webresource>{wid}</webresource>"
        f"</webresources></importexportxml>"})
    return wid


SECTIONS = (
    '<section name="spc_outcome_sec" id="{secid}" IsUserDefined="0" locklevel="0" '
    'showlabel="true" showbar="false" columns="1" labelwidth="115" '
    'celllabelalignment="Left" celllabelposition="Left">'
    '<labels><label description="Sales process" languagecode="1033" /></labels>'
    '<rows>'
    '<row><cell id="{cell1}" showlabel="false" rowspan="12" colspan="1" auto="false">'
    '<labels><label description="Outcome" languagecode="1033" /></labels>'
    '<control id="WebResource_spcoutcome" classid="{{9FDF5F91-88B1-47f4-AD53-C11EFC01A01D}}">'
    '<parameters><Url>{wr}</Url><PassParameters>true</PassParameters>'
    '<ShowOnMobileClient>false</ShowOnMobileClient><Security>false</Security>'
    '<Scrolling>auto</Scrolling><Border>false</Border>'
    '<WebResourceId>{{{wid}}}</WebResourceId></parameters>'
    '</control></cell></row>'
    '</rows></section>'
    '<section name="spc_journal_sec" id="{secid2}" IsUserDefined="0" locklevel="0" '
    'showlabel="true" showbar="false" columns="11" labelwidth="115" '
    'celllabelalignment="Left" celllabelposition="Left">'
    '<labels><label description="Process journal" languagecode="1033" /></labels>'
    '<rows>{jrows}</rows></section>'
)

JCELL = (
    '<row><cell id="{cellid}" showlabel="true">'
    '<labels><label description="{label}" languagecode="1033" /></labels>'
    '<control id="{field}" classid="{classid}" datafieldname="{field}" disabled="true" />'
    '</cell></row>'
)

TEXT_CLASS = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}"
LOOKUP_CLASS = "{270BD3DB-D9AF-4782-9025-509E298DEC0A}"

JOURNAL = [
    (f"{P}_branchpath", "Branch path", TEXT_CLASS),
    (f"{P}_outcomelabel", "Recorded outcome", TEXT_CLASS),
    (f"{P}_outcomecomment", "Outcome comment", TEXT_CLASS),
    (f"{P}_selectedoutcome", "Outcome record", LOOKUP_CLASS),
]


def sections_xml(wid):
    jrows = "".join(
        JCELL.format(cellid="{" + str(uuid.uuid4()) + "}", label=lbl, field=f, classid=c)
        for f, lbl, c in JOURNAL
    )
    return SECTIONS.format(
        secid="{" + str(uuid.uuid4()) + "}",
        secid2="{" + str(uuid.uuid4()) + "}",
        cell1="{" + str(uuid.uuid4()) + "}",
        wr=WR_NAME, wid=wid.upper(), jrows=jrows,
    )


def strip_old(xml):
    """Remove the legacy standalone tab and any previously injected sections."""
    xml = re.sub(rf'<tab name="{TAB_NAME}".*?</tab>', "", xml, flags=re.S)
    for sec in ("spc_outcome_sec", "spc_journal_sec"):
        xml = re.sub(rf'<section name="{sec}".*?</section>', "", xml, flags=re.S)
    return xml


def inject(xml, wid):
    """Drop the sections into the last column of the form's first tab.

    Multi-column tabs get them at the top of the right-hand column (that is the
    empty rail on the OOB task form).  Single-column tabs get them appended at
    the bottom, so nothing is pushed off screen.
    """
    xml = strip_old(xml)
    start = xml.find("<tabs>")
    if start < 0:
        return None
    end = xml.find("</tab>", start)
    if end < 0:
        return None
    tab = xml[start:end]

    cols_open = tab.find("<columns>")
    cols_close = tab.rfind("</columns>")
    if cols_open < 0 or cols_close < 0:
        return None
    cols = tab[cols_open + len("<columns>"):cols_close]
    n_cols = cols.count("<column ")
    if n_cols == 0:
        return None

    secs = sections_xml(wid)
    last = cols.rfind("<sections>")
    if last < 0:
        return None
    if n_cols > 1:
        cols = cols[:last + len("<sections>")] + secs + cols[last + len("<sections>"):]
    else:
        shut = cols.rfind("</sections>")
        cols = cols[:shut] + secs + cols[shut:]

    if n_cols == 2:
        cols = widen(cols)

    tab = tab[:cols_open + len("<columns>")] + cols + tab[cols_close:]
    return xml[:start] + tab + xml[end:]


def widen(cols):
    """Give the right-hand rail at least 40% so the picker is not cramped."""
    widths = re.findall(r'<column width="(\d+)%"', cols)
    if len(widths) != 2 or int(widths[1]) >= 40:
        return cols
    cols = cols.replace(f'<column width="{widths[0]}%"', '<column width="60%"', 1)
    head, sep, tail = cols.rpartition(f'<column width="{widths[1]}%"')
    return head + '<column width="40%"' + tail


def add_panel(wid):
    forms = dv.get(
        "systemforms?$select=formid,name,formxml,type&"
        "$filter=objecttypecode eq 'task' and type eq 2"
    )["value"]
    if not forms:
        print("  ! no task main form found")
        return
    for f in forms:
        xml = inject(f["formxml"], wid)
        if xml is None:
            print("  ! could not locate a target column in", f["name"])
            continue
        try:
            dv.patch(f"systemforms({f['formid']})", {"formxml": xml}, solution=True)
            print("  ~ panel placed on main tab:", f["name"])
        except RuntimeError as e:
            print("  ! failed on", f["name"], "->", str(e)[-260:])


def main():
    wid = upload()
    add_panel(wid)
    dv.post("PublishXml", {"ParameterXml":
        "<importexportxml><entities><entity>task</entity></entities>"
        f"<webresources><webresource>{wid}</webresource></webresources></importexportxml>"})
    print("  + published")
    print("done")


if __name__ == "__main__":
    main()
