"""Step 3: create lookup columns via one-to-many relationships."""
import dv
from dv import label

P = dv.PREFIX

# (referenced table, referencing table, lookup schema suffix, lookup display, required, nav name)
RELS = [
    # config hierarchy
    ("spc_salesprocesstemplate", "spc_matchrule", "template", "Sales Process Template", "ApplicationRequired"),
    ("spc_salesprocesstemplate", "spc_processtask", "template", "Sales Process Template", "ApplicationRequired"),
    ("spc_documentpackage", "spc_documentitem", "package", "Document Package", "ApplicationRequired"),
    ("spc_documentpackage", "spc_salesprocesstemplate", "documentpackage", "Document Package", "None"),
    ("spc_processtask", "spc_processtask", "predecessor", "Predecessor Task", "None"),
    # ownership targets on the task template
    ("team", "spc_processtask", "team", "Team", "None"),
    ("systemuser", "spc_processtask", "user", "User", "None"),
    ("queue", "spc_processtask", "queue", "Queue", "None"),
    ("team", "spc_processtask", "fallbackteam", "Fallback Team", "None"),
    ("team", "spc_documentitem", "ownerteam", "Owning Team", "None"),
    # SLA on the template
    ("sla", "spc_salesprocesstemplate", "sla", "SLA", "None"),
    ("entitlement", "spc_salesprocesstemplate", "entitlement", "Entitlement", "None"),
    # runtime
    ("opportunity", "spc_appliedprocess", "opportunity", "Opportunity", "ApplicationRequired"),
    ("spc_salesprocesstemplate", "spc_appliedprocess", "template", "Sales Process Template", "None"),
    ("opportunity", "spc_opportunityrequireddocument", "opportunity", "Opportunity", "ApplicationRequired"),
    ("spc_documentitem", "spc_opportunityrequireddocument", "packageitem", "Document Package Item", "None"),
    ("team", "spc_opportunityrequireddocument", "ownerteam", "Owning Team", "None"),
    # link generated tasks back to their template row
    ("spc_processtask", "task", "sourcetask", "Source Process Task", "None"),
    ("spc_salesprocesstemplate", "task", "sourcetemplate", "Source Sales Process Template", "None"),
    ("spc_salesprocesstemplate", "opportunity", "appliedtemplate", "Applied Sales Process Template", "None"),
    # headline product on the deal. SPC owns this rather than reusing another publisher's column so
    # the package never carries a dependency the target org does not have.
    ("product", "opportunity", "product", "Product", "None"),
]


def make(referenced, referencing, suffix, display, req):
    lookup = f"{P}_{suffix}" if not suffix.startswith(P) else suffix
    rel = f"{P}_{referenced}_{referencing}_{suffix}".replace("spc_spc_", "spc_")
    if len(rel) > 100:
        rel = rel[:100]
    try:
        dv.get(f"EntityDefinitions(LogicalName='{referencing}')/Attributes(LogicalName='{lookup}')?$select=LogicalName")
        print("  exists", referencing, lookup)
        return
    except RuntimeError:
        pass
    body = {
        "@odata.type": "Microsoft.Dynamics.CRM.OneToManyRelationshipMetadata",
        "SchemaName": rel,
        "ReferencedEntity": referenced,
        "ReferencingEntity": referencing,
        "CascadeConfiguration": {"Assign": "NoCascade", "Delete": "RemoveLink", "Merge": "NoCascade",
                                 "Reparent": "NoCascade", "Share": "NoCascade", "Unshare": "NoCascade"},
        "Lookup": {
            "@odata.type": "Microsoft.Dynamics.CRM.LookupAttributeMetadata",
            "SchemaName": lookup, "LogicalName": lookup,
            "DisplayName": label(display),
            "RequiredLevel": {"Value": req},
        },
        "AssociatedMenuConfiguration": {
            "Behavior": "UseCollectionName", "Group": "Details", "Order": 10000, "IsCustomizable": True,
        },
    }
    if referencing.startswith(P) and referenced.startswith(P):
        body["CascadeConfiguration"]["Delete"] = "Cascade" if suffix in ("template", "package") else "RemoveLink"
    dv.post("RelationshipDefinitions", body, solution=True)
    print("  +", referencing, lookup, "->", referenced)


if __name__ == "__main__":
    for r in RELS:
        make(*r)
    dv.publish_all()
    print("done")
