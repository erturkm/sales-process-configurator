"""Step 20: point the app at the SPC forms, and let go of Microsoft's.

Two halves, and they belong together:

1. Register the forms step19 created as app components. An app that lists no forms for an
   entity offers every form on that entity, and which one opens is then decided by form
   order and security roles -- so the SPC widgets were reachable only by luck. Naming the
   forms explicitly makes the SPC app open them every time.

2. Drop Microsoft's Opportunity and Task forms from the solution. This is the change that
   stops SPC overwriting other people's work: a system form is an atomic component, so
   while those forms were in the solution SPC shipped a whole copy of each and stamped it
   over whatever the target org had.

Removing a form from the solution does not delete it or touch its contents. The sections
SPC added to them in this org stay exactly where they are, as does anything CPC put there.
"""
import dv

P = dv.PREFIX

APP_UNIQUE = f"{P}_SalesProcessConfiguratorApp"
FORM_COMPONENT = 60

# Microsoft's forms, which SPC must no longer claim. Named rather than hardcoded by guid so
# this reads as an intent and still works in an org where the guids differ.
RELEASE = [
    ("opportunity", "Opportunity"),
    ("opportunity", "Information"),
    ("task", "Task"),
    ("task", "Information"),
    ("task", "Task for Interactive experience"),
]

# The forms SPC owns, created by step19.
OWNED = [
    ("opportunity", f"Opportunity ({P.upper()})"),
    ("task", f"Task ({P.upper()})"),
]


def form_id(entity, name):
    f = dv.find_one("systemforms",
                    f"objecttypecode eq '{entity}' and type eq 2 and name eq '{name}'",
                    "formid,name")
    return f["formid"] if f else None


def solution_components(unique_name):
    """The form components currently carried by the solution, as a set of lowercase guids."""
    sol = dv.find_one("solutions", f"uniquename eq '{unique_name}'", "solutionid")
    if not sol:
        return set()
    rows = dv.get("solutioncomponents?$select=objectid,componenttype&"
                  f"$filter=_solutionid_value eq {sol['solutionid']} and "
                  f"componenttype eq {FORM_COMPONENT}&$top=500")["value"]
    return {r["objectid"].lower() for r in rows}


def add_to_app():
    app = dv.find_one("appmodules", f"uniquename eq '{APP_UNIQUE}'", "appmoduleid,name")
    if not app:
        print(f"  ! app not found: {APP_UNIQUE} - run step11 first")
        return
    comps = []
    for entity, name in OWNED:
        fid = form_id(entity, name)
        if not fid:
            print(f"  ! missing {entity} form '{name}' - run step19 first")
            continue
        comps.append({"@odata.type": "Microsoft.Dynamics.CRM.systemform", "formid": fid})
        print(f"  + app form: {name}")
    if comps:
        dv.post("AddAppComponents", {"AppId": app["appmoduleid"], "Components": comps})
        dv.post("PublishXml", {"ParameterXml":
                               f"<importexportxml><appmodules><appmodule>{app['appmoduleid']}"
                               f"</appmodule></appmodules></importexportxml>"})


def release_oob():
    carried = solution_components(dv.SOLUTION)
    for entity, name in RELEASE:
        fid = form_id(entity, name)
        if not fid:
            print(f"  . no such form: {entity}/{name}")
            continue
        if fid.lower() not in carried:
            print(f"  . already released: {entity}/{name}")
            continue
        try:
            # The Web API spells this differently from the SDK: there is no ComponentId
            # parameter, it wants a solutioncomponent reference keyed by the object's own id.
            dv.post("RemoveSolutionComponent", {
                "SolutionComponent": {
                    "@odata.type": "Microsoft.Dynamics.CRM.solutioncomponent",
                    "solutioncomponentid": fid,
                },
                "ComponentType": FORM_COMPONENT,
                "SolutionUniqueName": dv.SOLUTION,
            })
            print(f"  - released {entity}/{name}  ({fid})")
        except RuntimeError as e:
            print(f"  ! could not release {entity}/{name} ->", str(e)[-240:])


def main():
    print("Registering SPC forms with the app")
    add_to_app()
    print("\nReleasing Microsoft's forms from the solution")
    release_oob()
    print("\nRemaining form components in the solution:")
    for fid in sorted(solution_components(dv.SOLUTION)):
        f = dv.get(f"systemforms({fid})?$select=name,objecttypecode")
        if f["objecttypecode"] in ("opportunity", "task"):
            print(f"   {f['objecttypecode']:12} {f['name']}")
    print("done")


if __name__ == "__main__":
    main()
