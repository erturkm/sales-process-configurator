"""Step 2: create the Sales Process Configurator tables and their non-lookup columns."""
import dv
from dv import label

P = dv.PREFIX


def entity(schema, disp, plural, desc, primary_max=200):
    ln = f"{P}_{schema}"
    try:
        dv.get(f"EntityDefinitions(LogicalName='{ln}')?$select=LogicalName")
        print("  exists", ln)
        return
    except RuntimeError:
        pass
    body = {
        "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
        "SchemaName": ln, "LogicalName": ln,
        "DisplayName": label(disp), "DisplayCollectionName": label(plural),
        "Description": label(desc),
        "OwnershipType": "UserOwned", "IsActivity": False, "HasNotes": True, "HasActivities": False,
        "Attributes": [{
            "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
            "SchemaName": f"{P}_name", "LogicalName": f"{P}_name",
            "DisplayName": label("Name"), "IsPrimaryName": True,
            "RequiredLevel": {"Value": "ApplicationRequired"},
            "MaxLength": primary_max, "FormatName": {"Value": "Text"},
        }],
    }
    dv.post("EntityDefinitions", body, solution=True)
    print("  created", ln)


def add(entity_schema, col):
    ln = f"{P}_{col['name']}"
    ent = f"{P}_{entity_schema}"
    try:
        dv.get(f"EntityDefinitions(LogicalName='{ent}')/Attributes(LogicalName='{ln}')?$select=LogicalName")
        return
    except RuntimeError:
        pass
    t = col["type"]
    b = {"SchemaName": ln, "LogicalName": ln, "DisplayName": label(col["display"]),
         "RequiredLevel": {"Value": col.get("req", "None")}}
    if col.get("desc"):
        b["Description"] = label(col["desc"])
    if t == "str":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
        b["MaxLength"] = col.get("len", 200)
        b["FormatName"] = {"Value": col.get("format", "Text")}
    elif t == "memo":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
        b["MaxLength"] = col.get("len", 4000)
        b["Format"] = "TextArea"
    elif t == "int":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
        b["MinValue"] = col.get("min", 0)
        b["MaxValue"] = col.get("max", 1000000)
        b["Format"] = "None"
    elif t == "dec":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.DecimalAttributeMetadata"
        b["Precision"] = col.get("prec", 2)
        b["MinValue"] = col.get("min", 0)
        b["MaxValue"] = col.get("max", 1000000)
    elif t == "bool":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.BooleanAttributeMetadata"
        b["DefaultValue"] = col.get("default", False)
        b["OptionSet"] = {
            "@odata.type": "Microsoft.Dynamics.CRM.BooleanOptionSetMetadata",
            "TrueOption": {"Value": 1, "Label": label(col.get("true", "Yes"))},
            "FalseOption": {"Value": 0, "Label": label(col.get("false", "No"))},
        }
    elif t == "datetime":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
        b["Format"] = col.get("format", "DateAndTime")
        b["DateTimeBehavior"] = {"Value": "UserLocal"}
    elif t == "choice":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
        opts = [{"Value": v, "Label": label(l)} for v, l in col["options"]]
        b["OptionSet"] = {
            "@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata",
            "OptionSetType": "Picklist", "IsGlobal": False,
            "Name": f"{ent}_{col['name']}", "DisplayName": label(col["display"]),
            "Options": opts,
        }
        if "default" in col:
            b["DefaultFormValue"] = col["default"]
    dv.post(f"EntityDefinitions(LogicalName='{ent}')/Attributes", b, solution=True)
    print("    +", ln)


# ---------------------------------------------------------------- tables
TABLES = [
    ("salesprocesstemplate", "Sales Process Template", "Sales Process Templates",
     "A declarative blueprint describing which deals it targets and what to apply to them."),
    ("matchrule", "Process Match Rule", "Process Match Rules",
     "A single condition used to decide whether a template applies to an opportunity. An attribute of the form productlines.<column> tests the deal's product lines and is true when any line matches."),
    ("processtask", "Process Task Template", "Process Task Templates",
     "An ordered task to generate on the deal, linked to a business process stage, with an owner and a task SLA."),
    ("documentpackage", "Document Package", "Document Packages",
     "A reusable bundle of documents required for a sales process."),
    ("documentitem", "Document Package Item", "Document Package Items",
     "A single required document inside a document package."),
    ("appliedprocess", "Applied Process", "Applied Processes",
     "Runtime stamp recording which template was applied to a deal and what it generated."),
    ("opportunityrequireddocument", "Deal Required Document", "Deal Required Documents",
     "Runtime checklist row for a document required on a specific deal."),
]

ASSIGN = [(1, "Team"), (2, "User"), (3, "Security Role"), (4, "Queue"),
          (5, "Manager of deal owner"), (6, "Deal owner")]
OPERATORS = [(1, "Equals"), (2, "Does not equal"), (3, "Is any of"), (4, "Is none of"),
             (5, "Contains"), (6, "Begins with"), (7, "Greater than"), (8, "Less than"),
             (9, "Is empty"), (10, "Is not empty"),
             (11, "Is at or under"), (12, "Is not under")]

COLUMNS = {
"salesprocesstemplate": [
    dict(name="description", display="Description", type="memo"),
    dict(name="rank", display="Rank", type="int", req="ApplicationRequired", min=1, max=1000,
         desc="Lower rank wins when several templates match the same deal."),
    dict(name="publishstatus", display="Status", type="choice", req="ApplicationRequired",
         options=[(1, "Draft"), (2, "Published"), (3, "Retired")], default=1),
    dict(name="effectivefrom", display="Effective From", type="datetime"),
    dict(name="effectiveto", display="Effective To", type="datetime"),
    dict(name="matchlogic", display="Match Logic", type="choice",
         options=[(1, "All conditions (AND)"), (2, "Any condition (OR)"), (3, "Grouped")], default=1),
    dict(name="bpfid", display="Business Process Flow Id", type="str", len=100,
         desc="workflowid of the out of the box business process flow to apply."),
    dict(name="bpfname", display="Business Process Flow", type="str", len=200),
    dict(name="bpfentityname", display="BPF Entity Name", type="str", len=100,
         desc="Logical name of the business process flow entity, e.g. opportunitysalesprocess."),
    dict(name="startstageid", display="Start Stage Id", type="str", len=100),
    dict(name="startstagename", display="Start At Stage", type="str", len=200),
    dict(name="qualificationhours", display="Qualification Target (hours)", type="dec", prec=2, max=10000),
    dict(name="closehours", display="Close Decision Target (hours)", type="dec", prec=2, max=10000),
    dict(name="setopportunitypriority", display="Set Deal Priority", type="choice",
         options=[(0, "Leave as is"), (1, "High"), (2, "Normal"), (3, "Low")], default=0),
    dict(name="appliedcount", display="Times Applied", type="int", max=1000000),
    dict(name="notes", display="Configuration Notes", type="memo"),
],
"matchrule": [
    dict(name="sequence", display="Sequence", type="int", req="ApplicationRequired", min=1, max=1000),
    dict(name="groupnumber", display="Group", type="int", min=1, max=100,
         desc="Conditions in the same group are ORed together; groups are ANDed."),
    dict(name="attributename", display="Opportunity Field", type="str", len=100, req="ApplicationRequired",
         desc="Logical name of the field on the opportunity, a one hop path such as customerid.spc_customersegment, or productlines.<column> to test the deal's product lines."),
    dict(name="attributelabel", display="Opportunity Field Label", type="str", len=200),
    dict(name="operator", display="Operator", type="choice", req="ApplicationRequired",
         options=OPERATORS, default=1),
    dict(name="value", display="Value", type="str", len=2000,
         desc="Comparison value. For Is any of / Is none of use a semicolon separated list."),
    dict(name="valuelabel", display="Value Label", type="str", len=2000),
],
"processtask": [
    dict(name="sequence", display="Sequence", type="int", req="ApplicationRequired", min=1, max=1000),
    dict(name="description", display="Instructions", type="memo"),
    dict(name="stagename", display="Business Process Stage", type="str", len=200,
         desc="Stage of the selected business process flow that this task belongs to."),
    dict(name="stageid", display="Stage Id", type="str", len=100),
    dict(name="assigntype", display="Assign To", type="choice", req="ApplicationRequired",
         options=ASSIGN, default=1),
    dict(name="rolename", display="Security Role Name", type="str", len=200),
    dict(name="blocksstage", display="Blocks Stage Completion", type="bool", true="Blocks", false="Does not block"),
    dict(name="mandatory", display="Mandatory", type="bool", default=True, true="Mandatory", false="Optional"),
    dict(name="duehours", display="Due In (hours)", type="dec", prec=2, max=10000),
    dict(name="slastartwhen", display="SLA Starts When", type="choice",
         options=[(1, "Deal created"), (2, "Stage entered"), (3, "Predecessor completed"), (4, "Task created")],
         default=1),
    dict(name="slatargethours", display="Task SLA Target (hours)", type="dec", prec=2, max=10000),
    dict(name="slawarnpercent", display="Warn At (%)", type="int", min=1, max=100),
    dict(name="onbreach", display="On Breach", type="choice",
         options=[(1, "Do nothing"), (2, "Notify task owner"), (3, "Escalate to manager"),
                  (4, "Escalate to queue"), (5, "Raise deal priority")], default=1),
    dict(name="calendarname", display="Business Hours Calendar", type="str", len=200),
    dict(name="pauseonwaiting", display="Pause On Waiting For Customer", type="bool",
         true="Pause", false="Keep running"),
],
"documentpackage": [
    dict(name="description", display="Description", type="memo"),
    dict(name="active", display="Active", type="bool", default=True),
],
"documentitem": [
    dict(name="sequence", display="Sequence", type="int", req="ApplicationRequired", min=1, max=1000),
    dict(name="description", display="Description", type="memo"),
    dict(name="mandatory", display="Mandatory", type="bool", default=True, true="Mandatory", false="Optional"),
    dict(name="responsible", display="Responsible", type="choice",
         options=[(1, "Customer"), (2, "Deal owner"), (3, "Team"), (4, "Third party")], default=1),
    dict(name="duehours", display="Due In (hours)", type="dec", prec=2, max=10000),
    dict(name="templatefile", display="Template File", type="str", len=300),
    dict(name="templateurl", display="Template URL", type="str", len=500, format="Url"),
],
"appliedprocess": [
    dict(name="appliedon", display="Applied On", type="datetime"),
    dict(name="tasksgenerated", display="Tasks Generated", type="int", max=10000),
    dict(name="docsrequired", display="Documents Required", type="int", max=10000),
    dict(name="slaapplied", display="SLA Applied", type="str", len=200),
    dict(name="bpfapplied", display="Business Process Applied", type="str", len=200),
    dict(name="stageset", display="Stage Set", type="str", len=200),
    dict(name="result", display="Result", type="choice",
         options=[(1, "Applied"), (2, "No template matched"), (3, "Partially applied"), (4, "Failed")], default=1),
    dict(name="evaluationlog", display="Evaluation Log", type="memo", len=100000),
    dict(name="durationms", display="Apply Duration (ms)", type="int", max=1000000),
],
"opportunityrequireddocument": [
    dict(name="sequence", display="Sequence", type="int", min=1, max=1000),
    dict(name="mandatory", display="Mandatory", type="bool", default=True, true="Mandatory", false="Optional"),
    dict(name="received", display="Received", type="bool", true="Received", false="Outstanding"),
    dict(name="receivedon", display="Received On", type="datetime"),
    dict(name="duedate", display="Due Date", type="datetime"),
    dict(name="responsible", display="Responsible", type="choice",
         options=[(1, "Customer"), (2, "Deal owner"), (3, "Team"), (4, "Third party")], default=1),
    dict(name="templateurl", display="Template URL", type="str", len=500, format="Url"),
],
}

if __name__ == "__main__":
    for schema, disp, plural, desc in TABLES:
        print(disp)
        entity(schema, disp, plural, desc)
    for schema, cols in COLUMNS.items():
        print("columns for", schema)
        for c in cols:
            add(schema, c)
    dv.publish_all()
    print("done")
