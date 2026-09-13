"""Step 12: outcome graph schema.

Adds the spc_taskoutcome table, canvas layout columns on spc_processtask, and the runtime
columns on task that record which outcome an agent picked.
"""
import dv
from dv import label
from step5_runtime_columns import add

P = dv.PREFIX

SENTIMENT = [(1, "Positive"), (2, "Neutral"), (3, "Negative"), (4, "Terminal")]


def create_table():
    ln = f"{P}_taskoutcome"
    try:
        dv.get(f"EntityDefinitions(LogicalName='{ln}')?$select=LogicalName")
        print("  exists", ln)
        return
    except RuntimeError:
        pass
    body = {
        "@odata.type": "Microsoft.Dynamics.CRM.EntityMetadata",
        "SchemaName": ln,
        "LogicalName": ln,
        "DisplayName": label("Task Outcome"),
        "DisplayCollectionName": label("Task Outcomes"),
        "Description": label("A selectable result of a process task that determines which task comes next."),
        "OwnershipType": "UserOwned",
        "IsActivity": False,
        "HasNotes": False,
        "HasActivities": False,
        "Attributes": [{
            "@odata.type": "Microsoft.Dynamics.CRM.StringAttributeMetadata",
            "SchemaName": f"{P}_name",
            "LogicalName": f"{P}_name",
            "DisplayName": label("Outcome"),
            "MaxLength": 200,
            "IsPrimaryName": True,
            "RequiredLevel": {"Value": "ApplicationRequired"},
            "FormatName": {"Value": "Text"},
        }],
    }
    dv.post("EntityDefinitions", body, solution=True)
    print("  + table", ln)


OUTCOME_COLS = [
    dict(name="sequence", display="Sequence", type="int"),
    dict(name="description", display="Guidance", type="str", len=500),
    dict(name="sentiment", display="Sentiment", type="choice", options=SENTIMENT, default=2),
    dict(name="advancestage", display="Advance Business Process Stage", type="bool",
         true="Advance", false="Stay"),
    dict(name="targetstagename", display="Jump To Stage", type="str"),
    # A deal does not "resolve" - it is won or lost, and which one it is changes the record's
    # state, so this has to carry the direction rather than a yes/no.
    dict(name="closeopportunity", display="Close The Deal", type="choice",
         options=[(0, "Leave open"), (1, "Close as won"), (2, "Close as lost")], default=0),
    dict(name="setopportunitypriority", display="Set Deal Priority", type="choice",
         options=[(0, "Leave as is"), (1, "High"), (2, "Normal"), (3, "Low")], default=0),
    dict(name="requirecomment", display="Require A Comment", type="bool", true="Required", false="Optional"),
    dict(name="isdefault", display="Default Outcome", type="bool", true="Default", false="No"),
]

TASK_COLS = [
    dict(name="isstart", display="Entry Task", type="bool", true="Entry", false="No"),
    dict(name="posx", display="Canvas X", type="int"),
    dict(name="posy", display="Canvas Y", type="int"),
    dict(name="nodecolor", display="Node Colour", type="str", len=20),
]

RUNTIME_TASK_COLS = [
    dict(name="outcomelabel", display="Outcome", type="str", len=200),
    dict(name="outcomecomment", display="Outcome Comment", type="str", len=1000),
    dict(name="availableoutcomes", display="Available Outcomes", type="str", len=4000),
    dict(name="branchpath", display="Branch Path", type="str", len=1000),
]


def relationships():
    rels = [
        # (child entity, lookup logical name, display, parent entity, nav collection)
        (f"{P}_taskoutcome", f"{P}_task", "Process Task", f"{P}_processtask"),
        (f"{P}_taskoutcome", f"{P}_nexttask", "Next Task", f"{P}_processtask"),
        (f"{P}_taskoutcome", f"{P}_template", "Sales Process Template", f"{P}_salesprocesstemplate"),
    ]
    for child, lookup, disp, parent in rels:
        schema = f"{parent}_{child}_{lookup.split('_', 1)[1]}"
        try:
            dv.get(f"RelationshipDefinitions(SchemaName='{schema}')?$select=SchemaName")
            print("  exists rel", schema)
            continue
        except RuntimeError:
            pass
        body = {
            "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
            "SchemaName": schema,
            "ReferencedEntity": parent,
            "ReferencingEntity": child,
            "CascadeConfiguration": {"Assign": "NoCascade", "Delete": "RemoveLink",
                                     "Merge": "NoCascade", "Reparent": "NoCascade",
                                     "Share": "NoCascade", "Unshare": "NoCascade"},
            "Lookup": {
                "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata",
                "SchemaName": lookup, "LogicalName": lookup,
                "DisplayName": label(disp), "RequiredLevel": {"Value": "None"},
            },
            "AssociatedMenuConfiguration": {
                "Behavior": "UseCollectionName", "Group": "Details", "Order": 10000,
            },
        }
        dv.post("RelationshipDefinitions", body, solution=True)
        print("  + rel", schema)


def runtime_outcome_lookup():
    schema = f"{P}_taskoutcome_task_selectedoutcome"
    try:
        dv.get(f"RelationshipDefinitions(SchemaName='{schema}')?$select=SchemaName")
        print("  exists rel", schema)
        return
    except RuntimeError:
        pass
    dv.post("RelationshipDefinitions", {
        "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
        "SchemaName": schema,
        "ReferencedEntity": f"{P}_taskoutcome",
        "ReferencingEntity": "task",
        "CascadeConfiguration": {"Assign": "NoCascade", "Delete": "RemoveLink", "Merge": "NoCascade",
                                 "Reparent": "NoCascade", "Share": "NoCascade", "Unshare": "NoCascade"},
        "Lookup": {
            "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata",
            "SchemaName": f"{P}_selectedoutcome", "LogicalName": f"{P}_selectedoutcome",
            "DisplayName": label("Selected Outcome"), "RequiredLevel": {"Value": "None"},
        },
        "AssociatedMenuConfiguration": {"Behavior": "DoNotDisplay", "Group": "Details", "Order": 10000},
    }, solution=True)
    print("  + rel", schema)


def main():
    print("Table")
    create_table()
    print("Outcome columns")
    for c in OUTCOME_COLS:
        add(f"{P}_taskoutcome", c)
    print("Task template columns")
    for c in TASK_COLS:
        add(f"{P}_processtask", c)
    print("Runtime task columns")
    for c in RUNTIME_TASK_COLS:
        add("task", c)
    print("Relationships")
    relationships()
    runtime_outcome_lookup()
    dv.publish_all()
    print("done")


if __name__ == "__main__":
    main()
