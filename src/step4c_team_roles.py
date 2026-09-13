"""Step 4c: give the demo teams a security role so they can own cases, tasks and activities.

Teams created through the Web API get no roles at all (privilegeCount=0), which makes the case
form fail with "Team Roles Error ... is missing prvReadActivity" the moment a team owns anything.
System Administrator is granted here deliberately: this is a demo org and these teams need to
read and write every activity type the case timeline touches.
"""
import dv

BU = dv.get("businessunits?$select=businessunitid"
            "&$filter=parentbusinessunitid eq null")["value"][0]["businessunitid"]

ROLE = "System Administrator"

def owner_teams():
    """Every non-default owner team in the org.

    Discovered rather than listed, so teams added by later seeding steps are
    picked up automatically instead of silently going role-less.
    """
    rows = dv.get("teams?$select=teamid,name,isdefault,_businessunitid_value"
                  "&$filter=teamtype eq 0&$top=200")["value"]
    return [t for t in rows if not t.get("isdefault")]


def main():
    roles = {r["name"]: r["roleid"] for r in dv.get(
        f"roles?$select=roleid,name&$filter=_businessunitid_value eq {BU}")["value"]}
    if ROLE not in roles:
        raise SystemExit(f"Role {ROLE!r} not found in the root business unit. "
                         "Available: " + str(sorted(roles)[:40]))
    role_id = roles[ROLE]
    print("Using role:", ROLE)

    for t in owner_teams():
        name = t["name"]
        # A team can only hold roles from its own business unit, so pull it into the root BU.
        if t.get("_businessunitid_value") != BU:
            dv.patch(f"teams({t['teamid']})",
                     {"businessunitid@odata.bind": f"/businessunits({BU})"})
            print("    moved to root BU:", name)

        have = {r["name"] for r in
                dv.get(f"teams({t['teamid']})/teamroles_association?$select=name")["value"]}
        if ROLE in have:
            print("  = already has role:", name)
            continue
        dv.post(f"teams({t['teamid']})/teamroles_association/$ref",
                {"@odata.id": f"{dv.API}/roles({role_id})"})
        print("  + role granted:", name)

    print("\nVerifying")
    bad = []
    for t in owner_teams():
        name = t["name"]
        have = [r["name"] for r in
                dv.get(f"teams({t['teamid']})/teamroles_association?$select=name")["value"]]
        ok = ROLE in have
        if not ok:
            bad.append(name)
        print(f"  {'OK ' if ok else '!! '}{name}: {have or 'NO ROLES'}")
    print("\nRESULT:", "ALL TEAMS HAVE THE ROLE" if not bad else "MISSING: " + str(bad))


if __name__ == "__main__":
    main()
