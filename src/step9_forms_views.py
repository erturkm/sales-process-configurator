"""Step 9: generate main forms and list views for the configurator tables."""
import uuid, html
import dv

P = dv.PREFIX

CLASSID = {
    "String": "{4273EDBD-AC1D-40D3-9FB2-095C621B552D}",
    "Memo": "{E0DECE4B-6FC8-4A8F-A065-082708572369}",
    "Integer": "{C6D124CA-7EDA-4A60-AEA9-7FB8D318B68F}",
    "Decimal": "{C3EFE0C3-0EC6-42BE-8349-CBD9079DFD8E}",
    "Double": "{C3EFE0C3-0EC6-42BE-8349-CBD9079DFD8E}",
    "Money": "{533B9E00-756B-4312-95A0-DC888637AC78}",
    "Picklist": "{3EF39988-22BB-4F0B-BBBE-64B5A3748AEE}",
    "State": "{5D68B988-0661-4DB2-BC3E-17598AD3BE6C}",
    "Status": "{3EF39988-22BB-4F0B-BBBE-64B5A3748AEE}",
    "Boolean": "{B0C6723A-8503-4FD7-BB28-C8A06AC933C2}",
    "DateTime": "{5B773807-9FB2-42DB-97C3-7A91EFF8ADFF}",
    "Lookup": "{270BD3DB-D9AF-4782-9025-509E298DEC0A}",
    "Customer": "{270BD3DB-D9AF-4782-9025-509E298DEC0A}",
    "Owner": "{270BD3DB-D9AF-4782-9025-509E298DEC0A}",
    "Uniqueidentifier": "{4273EDBD-AC1D-40D3-9FB2-095C621B552D}",
}
SUBGRID = "{E7A81278-8635-4D9E-8D4D-59480B391C5B}"


def g():
    return "{" + str(uuid.uuid4()) + "}"


def meta(entity):
    a = dv.get(f"EntityDefinitions(LogicalName='{entity}')/Attributes"
               f"?$select=LogicalName,AttributeType,DisplayName,IsValidForForm")["value"]
    out = {}
    for x in a:
        dn = x.get("DisplayName") or {}
        lbls = dn.get("LocalizedLabels") or []
        out[x["LogicalName"]] = {
            "type": x["AttributeType"],
            "label": lbls[0]["Label"] if lbls else x["LogicalName"],
            "form": x.get("IsValidForForm", False),
        }
    return out


def cell(entity_meta, field, colspan=1):
    m = entity_meta.get(field)
    if not m or not m["form"]:
        return ""
    cid = CLASSID.get(m["type"], CLASSID["String"])
    rowspan = ' rowspan="4"' if m["type"] == "Memo" else ""
    return (f'<cell id="{g()}" showlabel="true" locklevel="0"{rowspan}>'
            f'<labels><label description="{html.escape(m["label"])}" languagecode="1033" /></labels>'
            f'<control id="{field}" classid="{cid}" datafieldname="{field}" disabled="false" />'
            f'</cell>')


def grid_cell(name, label, relationship, target, view_id, rowspan=12):
    return (f'<cell id="{g()}" showlabel="true" rowspan="{rowspan}" colspan="2" locklevel="0">'
            f'<labels><label description="{html.escape(label)}" languagecode="1033" /></labels>'
            f'<control id="{name}" classid="{SUBGRID}" indicationOfSubgrid="true" uniqueid="{g()}">'
            f'<parameters>'
            f'<ViewId>{view_id}</ViewId>'
            f'<IsUserView>false</IsUserView>'
            f'<RelationshipName>{relationship}</RelationshipName>'
            f'<TargetEntityType>{target}</TargetEntityType>'
            f'<AutoExpand>Fixed</AutoExpand>'
            f'<RecordsPerPage>10</RecordsPerPage>'
            f'<EnableQuickFind>false</EnableQuickFind>'
            f'<EnableJumpBar>false</EnableJumpBar>'
            f'<EnableViewPicker>true</EnableViewPicker>'
            f'</parameters></control></cell>')


def section(title, cells, columns=2):
    rows = ""
    if columns == 1:
        for c in cells:
            rows += f"<row>{c}</row>"
    else:
        buf = []
        for c in cells:
            if 'classid="{E7A81278' in c or 'rowspan="4"' in c:
                if buf:
                    rows += "<row>" + "".join(buf) + "</row>"; buf = []
                rows += f"<row>{c}</row>"
                continue
            buf.append(c)
            if len(buf) == 2:
                rows += "<row>" + "".join(buf) + "</row>"; buf = []
        if buf:
            rows += "<row>" + "".join(buf) + "</row>"
    colattr = "111" if columns == 3 else ("11" if columns == 2 else "1")
    return (f'<section name="sec_{uuid.uuid4().hex[:8]}" id="{g()}" IsUserDefined="0" locklevel="0" '
            f'showlabel="true" showbar="false" columns="{colattr}" labelwidth="140" celllabelalignment="Left" '
            f'celllabelposition="Left">'
            f'<labels><label description="{html.escape(title)}" languagecode="1033" /></labels>'
            f'<rows>{rows}</rows></section>')


def tab(title, sections, expanded=True):
    return (f'<tab name="tab_{uuid.uuid4().hex[:8]}" id="{g()}" IsUserDefined="0" locklevel="0" '
            f'showlabel="true" expanded="{"true" if expanded else "false"}" verticallayout="true">'
            f'<labels><label description="{html.escape(title)}" languagecode="1033" /></labels>'
            f'<columns><column width="100%"><sections>{"".join(sections)}</sections></column></columns>'
            f'</tab>')


def form_xml(tabs):
    return ('<form><tabs>' + "".join(tabs) + '</tabs>'
            '<header id="' + g() + '" columns="111"><rows /></header>'
            '<footer id="' + g() + '" columns="111"><rows /></footer>'
            '</form>')


def set_main_form(entity, tabs, form_name):
    forms = dv.get(f"systemforms?$select=formid,name,type&$filter=objecttypecode eq '{entity}' and type eq 2")["value"]
    if not forms:
        print("  no main form for", entity); return
    fid = forms[0]["formid"]
    dv.patch(f"systemforms({fid})", {"formxml": form_xml(tabs), "name": form_name}, solution=True)
    print("  ~ form", entity)


def set_view(entity, view_name, columns, widths=None, order=None, filt=None):
    v = dv.find_one("savedqueries", f"returnedtypecode eq '{entity}' and name eq '{view_name}'",
                    "savedqueryid,name,fetchxml,layoutxml")
    if not v:
        print("  no view", entity, view_name); return
    id_field = entity + "id"
    attrs = "".join(f'<attribute name="{c}" />' for c in columns)
    orderxml = f'<order attribute="{order}" descending="false" />' if order else ""
    filterxml = filt or '<filter type="and"><condition attribute="statecode" operator="eq" value="0" /></filter>'
    fetch = (f'<fetch version="1.0" output-format="xml-platform" mapping="logical" no-lock="false">'
             f'<entity name="{entity}"><attribute name="{id_field}" />{attrs}{orderxml}{filterxml}'
             f'</entity></fetch>')
    widths = widths or [150] * len(columns)
    cells = "".join(f'<cell name="{c}" width="{w}" />' for c, w in zip(columns, widths))
    layout = (f'<grid name="resultset" object="1" jump="{columns[0]}" select="1" icon="1" preview="1">'
              f'<row name="result" id="{id_field}">{cells}</row></grid>')
    dv.patch(f"savedqueries({v['savedqueryid']})", {"fetchxml": fetch, "layoutxml": layout}, solution=True)
    print("  ~ view", entity, view_name)
    return v["savedqueryid"]


def view_id(entity, name):
    v = dv.find_one("savedqueries", f"returnedtypecode eq '{entity}' and name eq '{name}'", "savedqueryid")
    return "{" + v["savedqueryid"] + "}" if v else "{00000000-0000-0000-0000-000000000000}"


def rel_name(referencing, lookup):
    r = dv.get(f"EntityDefinitions(LogicalName='{referencing}')/ManyToOneRelationships"
               f"?$select=SchemaName,ReferencingAttribute")["value"]
    for x in r:
        if x["ReferencingAttribute"] == lookup:
            return x["SchemaName"]
    return ""


if __name__ == "__main__":
    M = {t: meta(P + "_" + t) for t in
         ["salesprocesstemplate", "matchrule", "processtask", "documentpackage",
          "documentitem", "appliedprocess", "opportunityrequireddocument"]}

    # ---------------------------------------------------------------- views first (needed by subgrids)
    print("Views")
    set_view(P + "_salesprocesstemplate", "Active Sales Process Templates",
             [f"{P}_name", f"{P}_rank", f"{P}_publishstatus", f"{P}_bpfname", f"{P}_startstagename",
              f"{P}_sla", f"{P}_documentpackage", f"{P}_appliedcount"],
             [260, 60, 90, 190, 120, 170, 180, 90], order=f"{P}_rank")
    set_view(P + "_matchrule", "Active Process Match Rules",
             [f"{P}_name", f"{P}_template", f"{P}_groupnumber", f"{P}_sequence",
              f"{P}_attributelabel", f"{P}_operator", f"{P}_valuelabel"],
             [240, 200, 60, 60, 150, 110, 150], order=f"{P}_sequence")
    set_view(P + "_processtask", "Active Process Task Templates",
             [f"{P}_name", f"{P}_template", f"{P}_stagename", f"{P}_sequence", f"{P}_assigntype",
              f"{P}_team", f"{P}_duehours", f"{P}_slatargethours", f"{P}_onbreach"],
             [250, 190, 130, 60, 110, 160, 80, 110, 140], order=f"{P}_sequence")
    set_view(P + "_documentpackage", "Active Document Packages",
             [f"{P}_name", f"{P}_description", f"{P}_active"], [260, 400, 80])
    set_view(P + "_documentitem", "Active Document Package Items",
             [f"{P}_name", f"{P}_package", f"{P}_sequence", f"{P}_mandatory",
              f"{P}_responsible", f"{P}_ownerteam", f"{P}_duehours"],
             [250, 200, 60, 90, 120, 170, 80], order=f"{P}_sequence")
    set_view(P + "_appliedprocess", "Active Applied Processes",
             [f"{P}_name", f"{P}_opportunity", f"{P}_template", f"{P}_appliedon", f"{P}_result",
              f"{P}_tasksgenerated", f"{P}_docsrequired", f"{P}_slaapplied", f"{P}_durationms"],
             [220, 220, 200, 150, 100, 90, 90, 160, 90],
             order=f"{P}_appliedon")
    set_view(P + "_opportunityrequireddocument", "Active Deal Required Documents",
             [f"{P}_name", f"{P}_opportunity", f"{P}_mandatory", f"{P}_received",
              f"{P}_duedate", f"{P}_responsible", f"{P}_ownerteam"],
             [240, 220, 90, 90, 150, 120, 170], order=f"{P}_sequence")

    RULE_VIEW = view_id(P + "_matchrule", "Active Process Match Rules")
    TASK_VIEW = view_id(P + "_processtask", "Active Process Task Templates")
    ITEM_VIEW = view_id(P + "_documentitem", "Active Document Package Items")
    APPLIED_VIEW = view_id(P + "_appliedprocess", "Active Applied Processes")

    REL_RULE = rel_name(P + "_matchrule", f"{P}_template")
    REL_TASK = rel_name(P + "_processtask", f"{P}_template")
    REL_ITEM = rel_name(P + "_documentitem", f"{P}_package")
    REL_APPLIED = rel_name(P + "_appliedprocess", f"{P}_template")

    print("Forms")
    m = M["salesprocesstemplate"]
    set_main_form(P + "_salesprocesstemplate", [
        tab("General", [
            section("Template", [cell(m, f"{P}_name"), cell(m, f"{P}_rank"),
                                 cell(m, f"{P}_publishstatus"), cell(m, f"{P}_matchlogic"),
                                 cell(m, f"{P}_effectivefrom"), cell(m, f"{P}_effectiveto"),
                                 cell(m, f"{P}_appliedcount"), cell(m, "ownerid")]),
            section("Description", [cell(m, f"{P}_description")], columns=1),
        ]),
        tab("Match Rules", [
            section("Which cases does this template target?",
                    [grid_cell("rules_grid", "Match rules", REL_RULE, P + "_matchrule", RULE_VIEW, 14)],
                    columns=1),
        ], expanded=False),
        tab("Process and Tasks", [
            section("Business process flow", [
                cell(m, f"{P}_bpfname"), cell(m, f"{P}_startstagename"),
                cell(m, f"{P}_bpfid"), cell(m, f"{P}_startstageid"),
                cell(m, f"{P}_bpfentityname"), cell(m, f"{P}_setopportunitypriority"),
            ]),
            section("Task plan",
                    [grid_cell("tasks_grid", "Stage linked tasks", REL_TASK, P + "_processtask", TASK_VIEW, 14)],
                    columns=1),
        ], expanded=False),
        tab("SLA and Documents", [
            section("Case SLA", [cell(m, f"{P}_sla"), cell(m, f"{P}_entitlement"),
                                 cell(m, f"{P}_qualificationhours"), cell(m, f"{P}_closehours")]),
            section("Document package", [cell(m, f"{P}_documentpackage")]),
            section("Notes", [cell(m, f"{P}_notes")], columns=1),
        ], expanded=False),
        tab("History", [
            section("Where this template has been applied",
                    [grid_cell("applied_grid", "Applied processes", REL_APPLIED,
                               P + "_appliedprocess", APPLIED_VIEW, 12)], columns=1),
        ], expanded=False),
    ], "Sales Process Template")

    m = M["matchrule"]
    set_main_form(P + "_matchrule", [tab("General", [
        section("Condition", [cell(m, f"{P}_name"), cell(m, f"{P}_template"),
                              cell(m, f"{P}_groupnumber"), cell(m, f"{P}_sequence")]),
        section("Test", [cell(m, f"{P}_attributelabel"), cell(m, f"{P}_attributename"),
                         cell(m, f"{P}_operator"), cell(m, f"{P}_value"),
                         cell(m, f"{P}_valuelabel")]),
    ])], "Process Match Rule")

    m = M["processtask"]
    set_main_form(P + "_processtask", [tab("General", [
        section("Task", [cell(m, f"{P}_name"), cell(m, f"{P}_template"),
                         cell(m, f"{P}_mandatory"), cell(m, f"{P}_blocksstage")]),
        section("Instructions", [cell(m, f"{P}_description")], columns=1),
        section("Placement", [cell(m, f"{P}_stagename"), cell(m, f"{P}_sequence"),
                              cell(m, f"{P}_predecessor"), cell(m, f"{P}_stageid")]),
        section("Ownership", [cell(m, f"{P}_assigntype"), cell(m, f"{P}_team"),
                              cell(m, f"{P}_user"), cell(m, f"{P}_rolename"),
                              cell(m, f"{P}_queue"), cell(m, f"{P}_fallbackteam")]),
        section("Task SLA", [cell(m, f"{P}_duehours"), cell(m, f"{P}_slastartwhen"),
                             cell(m, f"{P}_slatargethours"), cell(m, f"{P}_slawarnpercent"),
                             cell(m, f"{P}_onbreach"), cell(m, f"{P}_calendarname"),
                             cell(m, f"{P}_pauseonwaiting")]),
    ])], "Process Task Template")

    m = M["documentpackage"]
    set_main_form(P + "_documentpackage", [tab("General", [
        section("Package", [cell(m, f"{P}_name"), cell(m, f"{P}_active")]),
        section("Description", [cell(m, f"{P}_description")], columns=1),
        section("Documents", [grid_cell("items_grid", "Documents in this package", REL_ITEM,
                                        P + "_documentitem", ITEM_VIEW, 14)], columns=1),
    ])], "Document Package")

    m = M["documentitem"]
    set_main_form(P + "_documentitem", [tab("General", [
        section("Document", [cell(m, f"{P}_name"), cell(m, f"{P}_package"),
                             cell(m, f"{P}_sequence"), cell(m, f"{P}_mandatory")]),
        section("Description", [cell(m, f"{P}_description")], columns=1),
        section("Ownership and timing", [cell(m, f"{P}_responsible"), cell(m, f"{P}_ownerteam"),
                                         cell(m, f"{P}_duehours")]),
        section("Template", [cell(m, f"{P}_templatefile"), cell(m, f"{P}_templateurl")]),
    ])], "Document Package Item")

    m = M["appliedprocess"]
    set_main_form(P + "_appliedprocess", [tab("General", [
        section("Result", [cell(m, f"{P}_name"), cell(m, f"{P}_opportunity"),
                           cell(m, f"{P}_template"), cell(m, f"{P}_appliedon"),
                           cell(m, f"{P}_result"), cell(m, f"{P}_durationms")]),
        section("What was applied", [cell(m, f"{P}_bpfapplied"), cell(m, f"{P}_stageset"),
                                     cell(m, f"{P}_slaapplied"), cell(m, f"{P}_tasksgenerated"),
                                     cell(m, f"{P}_docsrequired")]),
        section("Evaluation log", [cell(m, f"{P}_evaluationlog")], columns=1),
    ])], "Applied Process")

    m = M["opportunityrequireddocument"]
    set_main_form(P + "_opportunityrequireddocument", [tab("General", [
        section("Document", [cell(m, f"{P}_name"), cell(m, f"{P}_opportunity"),
                             cell(m, f"{P}_packageitem"), cell(m, f"{P}_sequence")]),
        section("Status", [cell(m, f"{P}_mandatory"), cell(m, f"{P}_received"),
                           cell(m, f"{P}_receivedon"), cell(m, f"{P}_duedate")]),
        section("Ownership", [cell(m, f"{P}_responsible"), cell(m, f"{P}_ownerteam"),
                              cell(m, f"{P}_templateurl")]),
    ])], "Case Required Document")

    dv.publish_all()
    print("done")
