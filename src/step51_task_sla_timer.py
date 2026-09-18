"""Step 51: put the Modern SLA Timer PCF on the Task form, above the outcome picker.

The case form already shows live SLA countdowns through
mcsla_ModernSlaTimer.ModernSlaTimerControl bound to a slakpiinstance subgrid
(step 23).  Process tasks carry their own deadline in spc_sladue / spc_slawarn,
but those are plain columns: the form showed a date, not a clock.

Rather than build a second, look-alike widget, this reuses the same control.
The task entity is SLA enabled and the platform already ships the
slakpiinstance_task relationship, so a task can own real SLA KPI Instance rows.
The engine mirrors each task deadline into one such row and the PCF renders it
exactly as it does on the case.

Two notes on the mirror row:
  * applicablefromvalue is computed by the platform and rejects a value on
    create.  The control falls back to createdon, which is the moment the task
    was generated - the correct start of the countdown anyway.
  * status is left at 0 (In Progress).  The control moves it to Nearing Breach
    and Breached on its own clock, so nothing needs to poll.

Usage: python3 step51_task_sla_timer.py
"""
import re
import uuid

import dv

P = dv.PREFIX
PCF = "mcsla_ModernSlaTimer.ModernSlaTimerControl"
SUBGRID_CLASSID = "{E7A81278-8635-4d9e-8D4D-59480B391C5B}"
VIEW_NAME = "Sales Task SLA Timer Source"
SECTION = "spc_tasksla_sec"
# The case configurator puts its own timer on these same shared task forms, under
# "Task SLA Timer Source" / control id TaskSlaTimerGrid. Both names here must stay
# distinct from those: this script patches the view into the SPC solution and strips
# its own section before re-adding it, so a shared name would hijack the case view
# and a shared control id would delete the case timer's binding.
GRID_ID = "SpcTaskSlaTimerGrid"

ATTRS = [
    "name", "status", "failuretime", "warningtime", "succeededon",
    "applicablefromvalue", "pausedon", "terminalstatereached",
    "createdon", "slakpiinstanceid", "regarding",
]


def ensure_view():
    """A slakpiinstance view for the subgrid. Scoped by the relationship, not by filter."""
    existing = dv.find_one(
        "savedqueries",
        f"name eq '{VIEW_NAME}' and returnedtypecode eq 'slakpiinstance'",
        "savedqueryid,name")
    fetch = ("<fetch version='1.0' output-format='xml-platform' mapping='logical'>"
             "<entity name='slakpiinstance'>"
             + "".join(f"<attribute name='{a}' />" for a in ATTRS)
             + "<order attribute='createdon' descending='false' />"
             "</entity></fetch>")
    layout = ("<grid name='resultset' object='9752' jump='name' select='1' icon='1' preview='1'>"
              "<row name='result' id='slakpiinstanceid'>"
              "<cell name='name' width='150' /><cell name='status' width='120' />"
              "<cell name='failuretime' width='125' /><cell name='warningtime' width='125' />"
              "</row></grid>")
    body = {"name": VIEW_NAME, "fetchxml": fetch, "layoutxml": layout}
    if existing:
        vid = existing["savedqueryid"]
        dv.patch(f"savedqueries({vid})", body, solution=True)
        print("  ~ view", vid)
    else:
        body |= {"returnedtypecode": "slakpiinstance", "querytype": 0,
                 "description": "Feeds the Modern SLA Timer PCF on the sales task form."}
        vid = dv.new_id(dv.post("savedqueries", body, solution=True))
        print("  + view", vid)
    return vid


def grid_params(view_id):
    return (f"<ViewId>{{{view_id.upper()}}}</ViewId><IsUserView>false</IsUserView>"
            "<RelationshipName>slakpiinstance_task</RelationshipName>"
            "<TargetEntityType>slakpiinstance</TargetEntityType>"
            "<AutoExpand>Fixed</AutoExpand><EnableQuickFind>false</EnableQuickFind>"
            "<EnableViewPicker>false</EnableViewPicker><EnableJumpBar>false</EnableJumpBar>"
            "<ChartGridMode>Grid</ChartGridMode><VisualizationId /><IsUserChart>false</IsUserChart>"
            "<EnableChartPicker>false</EnableChartPicker><RecordsPerPage>10</RecordsPerPage>"
            "<EnableContextualActions>false</EnableContextualActions>")


def section_xml(view_id, uid):
    return (
        f'<section name="{SECTION}" id="{{{uuid.uuid4()}}}" IsUserDefined="0" locklevel="0" '
        'showlabel="true" showbar="false" columns="1" labelwidth="115" '
        'celllabelalignment="Left" celllabelposition="Left">'
        '<labels><label description="SLA" languagecode="1033" /></labels>'
        '<rows>'
        f'<row><cell id="{{{uuid.uuid4()}}}" showlabel="false" rowspan="5" colspan="1" auto="false">'
        '<labels><label description="SLA" languagecode="1033" /></labels>'
        f'<control id="{GRID_ID}" classid="{SUBGRID_CLASSID}" '
        f'indicationOfSubgrid="true" uniqueid="{uid}">'
        f'<parameters>{grid_params(view_id)}</parameters>'
        '</control></cell></row>'
        '<row /><row /><row /><row />'
        '</rows></section>'
    )


def control_description(view_id, uid):
    """The PCF binding lives at form level, keyed by the cell control's uniqueid."""
    ff = "".join(
        f'<customControl formFactor="{n}" name="{PCF}"><parameters>'
        f'<data-set name="dataSetGrid_1"><ViewId>{{{view_id.upper()}}}</ViewId>'
        "<IsUserView>false</IsUserView>"
        "<RelationshipName>slakpiinstance_task</RelationshipName>"
        "<TargetEntityType>slakpiinstance</TargetEntityType></data-set>"
        '<Update_Frequency static="true" type="Enum">10</Update_Frequency>'
        '<EnableNegativeTimer static="true" type="Enum">1</EnableNegativeTimer>'
        "</parameters></customControl>"
        for n in (0, 1, 2)
    )
    return (f'<controlDescription forControl="{uid}">'
            f'<customControl id="{SUBGRID_CLASSID}">'
            f"<parameters>{grid_params(view_id)}</parameters></customControl>"
            f"{ff}</controlDescription>")


def inject(xml, view_id):
    """Place the timer immediately above the outcome picker.

    Every task form carries the spc_outcome_sec section, and that is the anchor:
    inserting directly before it puts the countdown at the top of the same rail
    the agent uses to close the task, whatever column layout the form happens to
    have.

    These task forms are shared with the case configurator, which layers its own
    timer on them. Only this script's own section and its own controlDescription
    are removed, matched by the uniqueid carried inside the section, so a re-run
    never disturbs the case sections sitting alongside.
    """
    mine = re.search(rf'<section name="{SECTION}".*?</section>', xml, flags=re.S)
    if mine:
        uid = re.search(r'uniqueid="(\{[^"]+\})"', mine.group(0))
        xml = xml.replace(mine.group(0), "")
        if uid:
            xml = re.sub(
                r'<controlDescription forControl="%s">.*?</controlDescription>'
                % re.escape(uid.group(1)), "", xml, flags=re.S)

    anchor = xml.find('<section name="spc_outcome_sec"')
    if anchor < 0:
        return None

    uid = "{" + str(uuid.uuid4()).upper() + "}"
    xml = xml[:anchor] + section_xml(view_id, uid) + xml[anchor:]

    desc = control_description(view_id, uid)
    xml = re.sub(r"<controlDescriptions\s*/>", "<controlDescriptions></controlDescriptions>", xml)
    if "<controlDescriptions>" in xml:
        xml = xml.replace("<controlDescriptions>", "<controlDescriptions>" + desc, 1)
    else:
        xml = xml.replace("</form>",
                          f"<controlDescriptions>{desc}</controlDescriptions></form>", 1)
    return xml


def add_to_forms(view_id):
    forms = dv.get("systemforms?$select=formid,name,formxml,type&"
                   "$filter=objecttypecode eq 'task' and type eq 2")["value"]
    for f in forms:
        xml = inject(f["formxml"], view_id)
        if xml is None:
            print("  ! no target column in", f["name"])
            continue
        try:
            dv.patch(f"systemforms({f['formid']})", {"formxml": xml}, solution=True)
            print("  ~ timer placed on:", f["name"])
        except RuntimeError as e:
            print("  ! failed on", f["name"], "->", str(e)[-240:])


def backfill():
    """Give tasks that already carry a deadline their mirror row, so the demo has history."""
    tasks = dv.get("tasks?$select=activityid,subject,spc_sladue,spc_slawarn,statecode,"
                   "spc_slastatus&$filter=spc_sladue ne null&$top=500")["value"]
    made = skipped = 0
    for t in tasks:
        have = dv.get("slakpiinstances?$select=slakpiinstanceid&"
                      f"$filter=_regarding_value eq {t['activityid']}")["value"]
        if have:
            skipped += 1
            continue
        body = {
            "name": "Task SLA",
            "regarding_task@odata.bind": f"/tasks({t['activityid']})",
            "failuretime": t["spc_sladue"],
            "status": 4 if t["statecode"] == 1 else 0,
        }
        if t.get("spc_slawarn"):
            body["warningtime"] = t["spc_slawarn"]
        if t["statecode"] == 1:
            body["succeededon"] = t["spc_sladue"]
            body["terminalstatereached"] = True
        try:
            dv.post("slakpiinstances", body)
            made += 1
        except RuntimeError as e:
            print("  ! backfill failed for", t["subject"][:40], "->", str(e)[-160:])
    print(f"  backfilled {made} task(s), {skipped} already had a timer")


def main():
    view_id = ensure_view()
    add_to_forms(view_id)
    dv.post("PublishXml", {"ParameterXml":
        "<importexportxml><entities><entity>task</entity></entities></importexportxml>"})
    print("  published")
    backfill()
    print("done")


if __name__ == "__main__":
    main()
