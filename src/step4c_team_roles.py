"""Step 4c: give the demo teams a security role so they can own tasks and activities.

Teams created through the Web API get no roles at all (privilegeCount=0). The moment such a team
is made the owner of a task the platform rejects the assignment, because it validates that the new
owner can read the entity, and a task is an activity so the check runs across every activity type:

    Read Privilege Check For Owner failed ... Principal team (Id=..., privilegeCount=0),
    is missing prvReadActivity privilege ... for entity 'quoteclose'

That surfaces as a process that applies its template but silently generates no tasks.

Only the teams this accelerator creates are touched. An earlier version granted the role to every
non-default owner team it could find, which in a real environment means the owner teams that belong
to first-party solutions, application users and flows -- handing System Administrator to two dozen
service principals. Scope is now an explicit list, and anything unrecognised is left alone.
"""
import dv

# The teams step24 seeds. Kept in step with that list deliberately: a team not created here has
# not been reasoned about, and must not be granted anything.
SPC_TEAMS = [
    "Corporate Coverage",
    "SME Sales",
    "Credit Risk",
    "Credit Administration",
    "Trade Operations",
    "Treasury Sales",
    "Legal and Documentation",
    "Compliance and Financial Crime",
    "Deal Desk",
]

BU = dv.get("businessunits?$select=businessunitid"
            "&$filter=parentbusinessunitid eq null")["value"][0]["businessunitid"]

ROLE = "System Administrator"

def owner_teams():
    """The accelerator's own owner teams, and nothing else."""
    rows = dv.get("teams?$select=teamid,name,isdefault,_businessunitid_value"
                  "&$filter=teamtype eq 0&$top=500")["value"]
    by_name = {t["name"]: t for t in rows if not t.get("isdefault")}
    found, missing = [], []
    for name in SPC_TEAMS:
        (found if name in by_name else missing).append(by_name.get(name, name))
    if missing:
        print("  !! not found (run step24 first):", ", ".join(missing))
    return found


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
