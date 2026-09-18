"""Step 52: end to end proof that a deal picks up its process from its product lines.

Creates a deal with no lines, adds a Corporate Term Loan line, and asserts the second
registration (opportunityproduct Create) is what fires the match engine. Then walks the
corporate credit lifecycle far enough to prove outcomes, stage advance and close-as-won.

Re-runnable: the probe deal is deleted and recreated each time.
"""
import sys
import time

import dv

P = dv.PREFIX
TAG = "SPCE2E"


def clean():
    for o in dv.get(f"opportunities?$select=opportunityid,name"
                    f"&$filter=startswith(name,'{TAG}')")["value"]:
        for t in dv.get(f"tasks?$select=activityid&$filter=_regardingobjectid_value eq "
                        f"{o['opportunityid']}")["value"]:
            dv.call("DELETE", f"tasks({t['activityid']})")
        dv.call("DELETE", f"opportunities({o['opportunityid']})")
        print("  - removed", o["name"])


def price_list():
    """A deal line needs a price list, and this org had none before the sales build."""
    ex = dv.find_one("pricelevels", "name eq 'SPC Demo Price List'", "pricelevelid")
    if ex:
        return ex["pricelevelid"]
    return dv.new_id(dv.post("pricelevels", {"name": "SPC Demo Price List", "begindate": "2026-01-01"}))


def wait_for(fn, secs=90, label=""):
    """The line-driven match step is registered async, so the answer arrives late."""
    end = time.time() + secs
    while time.time() < end:
        v = fn()
        if v:
            return v
        time.sleep(3)
    print("  !! timed out waiting for", label)
    return None


def main():
    print("Cleaning previous probe deals")
    clean()

    acct = dv.find_one("accounts", "name eq 'Emirates Steel Industries PJSC'", "accountid")
    prod = dv.find_one("products", "name eq 'Corporate Term Loan'", "productid,defaultuomid,_defaultuomid_value")
    pl = price_list()

    print("Creating the deal with NO lines")
    oid = dv.new_id(dv.post("opportunities", {
        "name": f"{TAG} - AED 45m capex facility",
        "customerid_account@odata.bind": f"/accounts({acct['accountid']})",
        "estimatedvalue": 45000000, "estimatedclosedate": "2026-11-30",
        "pricelevelid@odata.bind": f"/pricelevels({pl})",
        "description": "Five year amortising term loan to fund a second rolling mill line."}))
    bare = dv.get(f"opportunities({oid})?$select=_{P}_appliedtemplate_value,{P}_taskstotal")
    print("  applied template while bare:",
          bare.get(f"_{P}_appliedtemplate_value") or "none (correct)")

    print("Adding a Corporate Term Loan line")
    dv.post("opportunityproducts", {
        "opportunityid@odata.bind": f"/opportunities({oid})",
        "productid@odata.bind": f"/products({prod['productid']})",
        "uomid@odata.bind": f"/uoms({prod['_defaultuomid_value']})",
        "quantity": 1, "priceperunit": 45000000, "isproductoverridden": False})

    got = wait_for(lambda: dv.get(
        f"opportunities({oid})?$select=_{P}_appliedtemplate_value")
        .get(f"_{P}_appliedtemplate_value"),
        120, "the match engine")
    o = dv.get(f"opportunities({oid})?$select=_{P}_appliedtemplate_value,{P}_taskstotal,{P}_docstotal,"
               f"{P}_qualificationdue,{P}_closedue,{P}_processappliedon,{P}_processsummary")
    print("  template :", o.get(f"_{P}_appliedtemplate_value"))
    print("  summary  :", (o.get(f"{P}_processsummary") or "")[:160])
    print("  applied  :", o.get(f"{P}_processappliedon"))
    print("  tasks    :", o.get(f"{P}_taskstotal"), " docs:", o.get(f"{P}_docstotal"))
    print("  qual due :", o.get(f"{P}_qualificationdue"))
    print("  close due:", o.get(f"{P}_closedue"))

    fails = []
    want = dv.find_one(f"{P}_salesprocesstemplates",
                       f"{P}_name eq 'Corporate Credit Facility - Full Lifecycle'",
                       f"{P}_salesprocesstemplateid")[f"{P}_salesprocesstemplateid"]
    if (o.get(f"_{P}_appliedtemplate_value") or "").lower() != want.lower():
        fails.append("wrong or missing template")
    if not o.get(f"{P}_qualificationdue") or not o.get(f"{P}_closedue"):
        fails.append("no deal level SLA")

    tasks = dv.get(f"tasks?$select=activityid,subject,{P}_sladue,{P}_availableoutcomes,"
                   f"statecode,_ownerid_value&$filter=_regardingobjectid_value eq {oid}"
                   f"&$orderby=createdon asc")["value"]
    print(f"  {len(tasks)} task(s) generated")
    for t in tasks:
        print("    -", t["subject"], "| SLA due", t.get(f"{P}_sladue"))
    if not tasks:
        fails.append("no tasks generated")
    elif not any(t.get(f"{P}_sladue") for t in tasks):
        fails.append("no task carries an SLA deadline")

    docs = dv.get(f"{P}_opportunityrequireddocuments?$select={P}_name"
                  f"&$filter=_{P}_opportunity_value eq {oid}")["value"]
    print(f"  {len(docs)} required document(s)")
    if not docs:
        fails.append("no required documents")

    # Walk the whole lifecycle by always taking the default outcome. The outcome is a
    # LOOKUP, not a label, so it has to be resolved against the source process task.
    print("\nWalking the lifecycle on default outcomes")
    for step in range(25):
        open_tasks = dv.get(f"tasks?$select=activityid,subject,_{P}_sourcetask_value"
                            f"&$filter=_regardingobjectid_value eq {oid} and statecode eq 0"
                            f"&$orderby=createdon asc")["value"]
        if not open_tasks:
            break
        t = open_tasks[0]
        src = t.get(f"_{P}_sourcetask_value")
        outs = dv.get(f"{P}_taskoutcomes?$select={P}_taskoutcomeid,{P}_name,{P}_isdefault,"
                      f"{P}_sequence&$filter=_{P}_task_value eq {src}"
                      f"&$orderby={P}_sequence asc")["value"] if src else []
        pick = next((o for o in outs if o.get(f"{P}_isdefault")), outs[0] if outs else None)
        if not pick:
            print("  !! no outcomes on", t["subject"])
            fails.append("task without outcomes: " + t["subject"])
            break
        dv.patch(f"tasks({t['activityid']})", {
            f"{P}_selectedoutcome_Task@odata.bind":
                f"/{P}_taskoutcomes({pick[f'{P}_taskoutcomeid']})",
            f"{P}_outcomecomment": "Automated end to end walk.",
            "statecode": 1, "statuscode": 5})
        print(f"  {step + 1:2}. {t['subject'][:56]:56} -> {pick[f'{P}_name']}")
        time.sleep(1)

    # The rollups are maintained by an async step, so reading them the instant the last
    # outcome is saved reports the count from one task ago. Wait for the deal to settle
    # rather than reporting a failure that is really a race in the test.
    final = wait_for(lambda: (lambda d: d if d["statecode"] != 0 and
                              (d.get(f"{P}_tasksopen") or 0) == 0 else None)(
        dv.get(f"opportunities({oid})?$select=statecode,statuscode,"
               f"{P}_tasksopen,{P}_taskstotal,{P}_processsummary")),
        60, "the deal rollups to settle") or dv.get(
        f"opportunities({oid})?$select=statecode,statuscode,"
        f"{P}_tasksopen,{P}_taskstotal,{P}_processsummary")
    print("\n  summary     :", (final.get(f"{P}_processsummary") or "")[:300])
    print("  statecode   :", final["statecode"], "(0 open, 1 won, 2 lost)")
    print("  tasks       :", final.get(f"{P}_tasksopen"), "open of", final.get(f"{P}_taskstotal"))
    if final["statecode"] != 1:
        fails.append("deal did not close as won")
    done = dv.get(f"tasks?$select=activityid,statecode&$filter=_regardingobjectid_value eq {oid}")["value"]
    print("  actual      :", sum(1 for t in done if t["statecode"] == 0), "open of", len(done))
    if any(t["statecode"] == 0 for t in done):
        fails.append("tasks left open after the deal closed")

    print("\nRESULT:", "PASS" if not fails else "FAIL -> " + "; ".join(fails))
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
