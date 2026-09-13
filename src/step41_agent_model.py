"""Step 41 (v2): data model for AI-agent-assigned process tasks.

Adds:
  - spc_assigntype option 7 = AI Agent on spc_processtask
  - agent configuration columns on spc_processtask (design time)
  - agent run-state columns on task (runtime)
  - spc_agentrun, the audit table for every agent invocation

Idempotent - safe to re-run.
"""
import dv
from dv import label

P = dv.PREFIX

# --- how much CRM context a configurer may hand to the agent -----------------
# Deliberately granular: a business user grants only what the task needs, and
# each option maps to a bounded query in the context builder.
CONTEXT_SCOPE = [
    (1, "Deal core fields"),
    (2, "Deal narrative"),
    (3, "Customer profile"),
    (4, "Other deals with this customer"),
    (5, "Notes on the deal"),
    (6, "Emails on the deal"),
    (7, "Process tasks and outcomes"),
    (8, "Required documents"),
    (9, "Clock status"),
    (10, "Product lines"),
]

OUTPUT_TARGET = [
    (1, "Agent output field only"),
    (2, "Task description"),
    (3, "Note on the deal"),
    (4, "Append to deal narrative"),
]

AGENT_STATE = [
    (1, "Queued"),
    (2, "Running"),
    (3, "Succeeded"),
    (4, "Awaiting review"),
    (5, "Failed"),
    (6, "Skipped"),
]

RUN_STATUS = [
    (1, "Success"),
    (2, "Failed"),
    (3, "Timed out"),
    (4, "Rejected by threshold"),
    (5, "Invalid response"),
]


def attr_exists(entity, logical):
    try:
        dv.get(f"EntityDefinitions(LogicalName='{entity}')"
               f"/Attributes(LogicalName='{logical}')?$select=LogicalName")
        return True
    except RuntimeError:
        return False


def add(entity, col):
    """Create one column. `col['name']` is the suffix after the publisher prefix."""
    ln = f"{P}_{col['name']}"
    if attr_exists(entity, ln):
        print(f"  = {entity}.{ln}")
        return
    t = col["type"]
    b = {"SchemaName": ln, "LogicalName": ln, "DisplayName": label(col["display"]),
         "RequiredLevel": {"Value": col.get("req", "None")}}
    if col.get("desc"):
        b["Description"] = label(col["desc"])
    if t == "str":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.StringAttributeMetadata"
        b["MaxLength"] = col.get("len", 200)
        b["FormatName"] = {"Value": "Text"}
    elif t == "memo":
        b["@odata.type"] = "Microsoft.Dynamics.CRM.MemoAttributeMetadata"
        b["MaxLength"] = col.get("len", 100000)
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
        b["MaxValue"] = col.get("max", 100)
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
        b["Format"] = "DateAndTime"
        b["DateTimeBehavior"] = {"Value": "UserLocal"}
    elif t in ("choice", "multichoice"):
        b["@odata.type"] = ("Microsoft.Dynamics.CRM.PicklistAttributeMetadata" if t == "choice"
                            else "Microsoft.Dynamics.CRM.MultiSelectPicklistAttributeMetadata")
        b["OptionSet"] = {"@odata.type": "Microsoft.Dynamics.CRM.OptionSetMetadata",
                          "OptionSetType": "Picklist", "IsGlobal": False,
                          "Name": f"{ln}_{entity}", "DisplayName": label(col["display"]),
                          "Options": [{"Value": v, "Label": label(l)} for v, l in col["options"]]}
        if "default" in col:
            b["DefaultFormValue"] = col["default"]
    else:
        raise ValueError(f"unknown column type {t}")
    dv.post(f"EntityDefinitions(LogicalName='{entity}')/Attributes", b, solution=True)
    print(f"  + {entity}.{ln}")


def lookup(referenced, referencing, suffix, display, desc=None):
    ln = f"{P}_{suffix}"
    if attr_exists(referencing, ln):
        print(f"  = {referencing}.{ln} -> {referenced}")
        return
    rel = f"{P}_{referenced}_{referencing}_{suffix}".replace("spc_spc_", "spc_")[:100]
    lk = {"@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata",
          "SchemaName": ln, "LogicalName": ln, "DisplayName": label(display),
          "RequiredLevel": {"Value": "None"}}
    if desc:
        lk["Description"] = label(desc)
    dv.post("RelationshipDefinitions", {
        "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
        "SchemaName": rel,
        "ReferencedEntity": referenced,
        "ReferencingEntity": referencing,
        "CascadeConfiguration": {"Assign": "NoCascade", "Delete": "RemoveLink",
                                 "Merge": "NoCascade", "Reparent": "NoCascade",
                                 "Share": "NoCascade", "Unshare": "NoCascade"},
        "Lookup": lk,
    }, solution=True)
    print(f"  + {referencing}.{ln} -> {referenced}")


def ensure_assigntype_option():
    """Add 7 = AI Agent to the existing spc_assigntype picklist."""
    meta = dv.get(f"EntityDefinitions(LogicalName='{P}_processtask')"
                  f"/Attributes(LogicalName='{P}_assigntype')"
                  "/Microsoft.Dynamics.CRM.PicklistAttributeMetadata?$expand=OptionSet")
    existing = {o["Value"]: o["Label"]["UserLocalizedLabel"]["Label"]
                for o in meta["OptionSet"]["Options"]}
    if 7 in existing:
        print(f"  = spc_assigntype 7 = {existing[7]}")
        return
    dv.post("InsertOptionValue", {
        "EntityLogicalName": f"{P}_processtask",
        "AttributeLogicalName": f"{P}_assigntype",
        "Value": 7,
        "Label": label("AI Agent"),
        "SolutionUniqueName": dv.SOLUTION,
    })
    print("  + spc_assigntype 7 = AI Agent")


def ensure_agentrun_table():
    ln = f"{P}_agentrun"
    try:
        dv.get(f"EntityDefinitions(LogicalName='{ln}')?$select=LogicalName")
        print(f"  = {ln}")
        return
    except RuntimeError:
        pass
    dv.post("EntityDefinitions", {
        "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
        "SchemaName": ln, "LogicalName": ln,
        "DisplayName": label("Agent Run"),
        "DisplayCollectionName": label("Agent Runs"),
        "Description": label("One AI agent invocation: what was sent, what came back, "
                             "how long it took and what was done with the answer."),
        "OwnershipType": "UserOwned", "IsActivity": False,
        "HasNotes": False, "HasActivities": False,
        "Attributes": [{
            "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
            "SchemaName": f"{P}_name", "LogicalName": f"{P}_name",
            "DisplayName": label("Name"), "IsPrimaryName": True,
            "RequiredLevel": {"Value": "ApplicationRequired"},
            "MaxLength": 300, "FormatName": {"Value": "Text"},
        }],
    }, solution=True)
    print(f"  + {ln}")


# --- design-time configuration, on the task template -------------------------
PROCESSTASK = [
    dict(name="agentprompt", type="memo", len=8000, display="Agent Prompt",
         desc="The instruction sent to the agent for this task. The agent supplies "
              "capability (knowledge, tools); this prompt supplies the ask."),
    dict(name="contextscope", type="multichoice", options=CONTEXT_SCOPE,
         display="CRM Context To Share",
         desc="Which slices of CRM data are assembled and sent with the prompt."),
    dict(name="outputtarget", type="choice", options=OUTPUT_TARGET, default=1,
         display="Write Output To"),
    dict(name="autocomplete", type="bool", default=False,
         true="Complete automatically", false="Hold for human review",
         display="Auto-Complete Task",
         desc="If off, the agent drafts and a human confirms the outcome."),
    dict(name="confidencethreshold", type="int", min=0, max=100, display="Confidence Threshold %",
         desc="Below this, the task is held for review even when auto-complete is on."),
    dict(name="agenttimeoutmins", type="int", min=1, max=120, display="Agent Timeout (minutes)"),
    dict(name="agentoutcomemode", type="choice", default=1, display="Outcome Selection",
         options=[(1, "Agent proposes, human selects"),
                  (2, "Agent selects from configured outcomes"),
                  (3, "No outcome, output only")]),
]

# --- runtime state, on the generated task ------------------------------------
TASK = [
    dict(name="agentstate", type="choice", options=AGENT_STATE, display="Agent State"),
    dict(name="agentoutput", type="memo", len=100000, display="Agent Output"),
    dict(name="agentconfidence", type="dec", prec=2, min=0, max=100, display="Agent Confidence %"),
    dict(name="agentrunon", type="datetime", display="Agent Ran On"),
    dict(name="agentattempts", type="int", min=0, max=100, display="Agent Attempts"),
    dict(name="agenterror", type="str", len=400, display="Agent Error"),
    # The one and only trigger signal for the Phase 4 cloud flow. Written when a turn is
    # genuinely requested and never by the flow itself, so the flow's own state writes
    # (Running, Succeeded, Awaiting review) cannot wake it. Registering the trigger on
    # spc_agentstate instead costs three wasted runs per task.
    dict(name="agentqueuedon", type="datetime", display="Agent Turn Requested On"),
]

# --- the audit record --------------------------------------------------------
AGENTRUN = [
    dict(name="promptsent", type="memo", len=100000, display="Prompt Sent"),
    dict(name="contextsent", type="memo", len=100000, display="Context Sent"),
    dict(name="rawresponse", type="memo", len=100000, display="Raw Response"),
    dict(name="parsedoutcome", type="str", len=200, display="Parsed Outcome"),
    dict(name="confidence", type="dec", prec=2, min=0, max=100, display="Confidence %"),
    dict(name="latencyms", type="int", min=0, max=100000000, display="Latency (ms)"),
    dict(name="contextchars", type="int", min=0, max=100000000, display="Context Size (chars)"),
    dict(name="attempt", type="int", min=0, max=100, display="Attempt"),
    dict(name="runstatus", type="choice", options=RUN_STATUS, display="Run Status"),
    dict(name="errordetail", type="memo", len=4000, display="Error Detail"),
    dict(name="agentschemaname", type="str", len=200, display="Agent Schema Name"),
    dict(name="conversationid", type="str", len=200, display="Conversation Id"),
    dict(name="requestedbyname", type="str", len=200, display="Requested By",
         desc="The business user the run was performed for. The connection itself "
              "authenticates as a shared service account, so this preserves attribution."),
    dict(name="autocompleted", type="bool", default=False,
         true="Auto-completed", false="Held for review", display="Auto-Completed"),
]


def main():
    print("Phase 1: AI agent data model\n")

    print("Assign type")
    ensure_assigntype_option()

    print("\nAgent run table")
    ensure_agentrun_table()

    print("\nTask template - agent configuration")
    for c in PROCESSTASK:
        add(f"{P}_processtask", c)
    lookup("bot", f"{P}_processtask", "agent", "Copilot Studio Agent",
           "The published agent invoked for this task.")

    print("\nGenerated task - agent runtime state")
    for c in TASK:
        add("task", c)

    print("\nAgent run - audit columns")
    for c in AGENTRUN:
        add(f"{P}_agentrun", c)
    lookup("opportunity", f"{P}_agentrun", "opportunity", "Opportunity")
    lookup("task", f"{P}_agentrun", "task", "Task")
    lookup("bot", f"{P}_agentrun", "agent", "Copilot Studio Agent")
    lookup(f"{P}_processtask", f"{P}_agentrun", "sourcetask", "Source Process Task")

    print("\nPublishing ...")
    dv.publish_all()
    print("Done.")


if __name__ == "__main__":
    main()
