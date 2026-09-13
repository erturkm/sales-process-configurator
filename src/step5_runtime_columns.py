"""Step 5: runtime columns on opportunity, task, account and contact.

These are the columns the engine writes at run time, as opposed to the configuration
tables in step 2. Everything is spc_ prefixed, so the case configurator's cpc_ columns
on the shared `task` table are untouched and the two solutions can run side by side.

The SLA pair is qualification / close rather than first response / resolution: a deal's
two clocks are "how long until we know whether this is real" and "how long until we know
whether we won it".
"""
import dv
from dv import label

P = dv.PREFIX
SLA_STATUS = [(1, "In progress"), (2, "Nearing breach"), (3, "Met"), (4, "Breached"), (5, "Paused")]
SEGMENTS = [(1, "Large Corporate"), (2, "Mid Market"), (3, "SME"), (4, "Financial Institution"),
            (5, "Government and Public Sector")]


def add(entity, col):
    ln = f"{P}_{col['name']}"
    try:
        dv.get(f"EntityDefinitions(LogicalName='{entity}')/Attributes(LogicalName='{ln}')?$select=LogicalName")
        print("  exists", entity, ln)
        return
    except RuntimeError:
        pass
    t = col["type"]
    b = {"SchemaName": ln, "LogicalName": ln, "DisplayName": label(col["display"]),
         "RequiredLevel": {"Value": "None"}}
    if t == "datetime":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.DateTimeAttributeMetadata"
        b["Format"] = "DateAndTime"
        b["DateTimeBehavior"] = {"Value": "UserLocal"}
    elif t == "str":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
        b["MaxLength"] = col.get("len", 200)
        b["FormatName"] = {"Value": "Text"}
    elif t == "int":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.IntegerAttributeMetadata"
        b["MinValue"] = 0; b["MaxValue"] = 1000000; b["Format"] = "None"
    elif t == "bool":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.BooleanAttributeMetadata"
        b["DefaultValue"] = False
        b["OptionSet"] = {"@odata.type": "Microsoft.Dynamics.CRM.BooleanOptionSetMetadata",
                          "TrueOption": {"Value": 1, "Label": label(col.get("true", "Yes"))},
                          "FalseOption": {"Value": 0, "Label": label(col.get("false", "No"))}}
    elif t == "choice":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.PicklistAttributeMetadata"
        b["OptionSet"] = {"@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata",
                          "OptionSetType": "Picklist", "IsGlobal": False,
                          "Name": f"{ln}_{entity}", "DisplayName": label(col["display"]),
                          "Options": [{"Value": v, "Label": label(l)} for v, l in col["options"]]}
        if "default" in col:
            b["DefaultFormValue"] = col["default"]
    dv.post(f"EntityDefinitions(LogicalName='{entity}')/Attributes", b, solution=True)
    print("  +", entity, ln)


OPPORTUNITY = [
    dict(name="qualificationdue", display="Qualification Due", type="datetime"),
    dict(name="qualificationwarn", display="Qualification Warning At", type="datetime"),
    dict(name="qualificationstatus", display="Qualification SLA Status", type="choice",
         options=SLA_STATUS, default=1),
    dict(name="closedue", display="Close Decision Due", type="datetime"),
    dict(name="closewarn", display="Close Decision Warning At", type="datetime"),
    dict(name="closestatus", display="Close SLA Status", type="choice",
         options=SLA_STATUS, default=1),
    dict(name="processappliedon", display="Process Applied On", type="datetime"),
    dict(name="processsummary", display="Applied Process Summary", type="str", len=400),
    dict(name="tasksopen", display="Open Process Tasks", type="int"),
    dict(name="taskstotal", display="Total Process Tasks", type="int"),
    dict(name="docsreceived", display="Documents Received", type="int"),
    dict(name="docstotal", display="Documents Required", type="int"),
]

# Deliberately spc_ prefixed even though cpc_customersegment already exists on these
# tables. Sharing the column would couple the two solutions, and the sales view of a
# customer is not the service view: a bank segments a borrower by turnover and exposure,
# not by the card product they happen to hold.
CUSTOMER = [
    dict(name="customersegment", display="Sales Segment", type="choice", options=SEGMENTS),
]

TASK = [
    dict(name="sequence", display="Sequence", type="int"),
    dict(name="stagename", display="Business Process Stage", type="str"),
    dict(name="blocksstage", display="Blocks Stage Completion", type="bool",
         true="Blocks", false="Does not block"),
    dict(name="mandatory", display="Mandatory", type="bool", true="Mandatory", false="Optional"),
    dict(name="sladue", display="Task SLA Due", type="datetime"),
    dict(name="slawarn", display="Task SLA Warning At", type="datetime"),
    dict(name="slastatus", display="Task SLA Status", type="choice", options=SLA_STATUS, default=1),
    dict(name="onbreach", display="On Breach", type="choice",
         options=[(1, "Do nothing"), (2, "Notify task owner"), (3, "Escalate to manager"),
                  (4, "Escalate to queue"), (5, "Raise deal priority")], default=1),
    dict(name="assignedteamname", display="Assigned Team", type="str"),
]


if __name__ == "__main__":
    print("Opportunity columns"); [add("opportunity", c) for c in OPPORTUNITY]
    print("Customer columns")
    for e in ("account", "contact"):
        [add(e, c) for c in CUSTOMER]
    print("Task columns"); [add("task", c) for c in TASK]
    dv.publish_all()
    print("done")
