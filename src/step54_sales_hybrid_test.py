"""Step 54: hybrid end-to-end test of the corporate credit lifecycle.

13 tasks: 9 human, 4 handed to the Corporate Credit Analyst agent. The script completes ONLY
the human tasks. Every AI task is left entirely to the Phase 4 cloud flow, so if the flow,
the context builder, the agent or the write-back is broken the run stalls and fails rather
than quietly passing.

It also asserts the Copilot-harness refusal string never lands in an agent's output. That
refusal arrives as the agent's own reply, so the flow run goes green and the refusal gets
written onto the task as if it were a finding.

Re-runnable: the probe deal is deleted and recreated each time.
"""
import sys
import time

import dv

P = dv.PREFIX
TAG = "SPCHYBRID"
TEMPLATE = "Corporate Credit Facility - Full Lifecycle"
# Three AI tasks are configured to close themselves. The fourth writes the paper that goes to
# the credit committee and is deliberately configured to draft and park, so reaching
# "Awaiting review" with output attached is a PASS for that one, not a failure.
DRAFT_ONLY = "Write the credit application"
AUTO_COUNT = 3

AGENT_STATE = {1: "Queued", 2: "Running", 3: "Succeeded",
               4: "Awaiting review", 5: "Failed", 6: "Skipped"}
RUN_STATUS = {1: "Success", 2: "Failed", 3: "Timed out",
              4: "Rejected", 5: "Invalid response"}
REFUSAL = "doesn't support agents built with the GitHub Copilot harness"


def clean():
    for o in dv.get(f"opportunities?$select=opportunityid,name"
                    f"&$filter=startswith(name,'{TAG}')")["value"]:
        for t in dv.get(f"tasks?$select=activityid&$filter=_regardingobjectid_value eq "
                        f"{o['opportunityid']}")["value"]:
            dv.call("DELETE", f"tasks({t['activityid']})")
        dv.call("DELETE", f"opportunities({o['opportunityid']})")
        print("  - removed", o["name"])


def price_list():
    ex = dv.find_one("pricelevels", "name eq 'SPC Demo Price List'", "pricelevelid")
    if ex:
        return ex["pricelevelid"]
    return dv.new_id(dv.post("pricelevels",
                             {"name": "SPC Demo Price List", "begindate": "2026-01-01"}))


def open_tasks(oid):
    return dv.get(f"tasks?$select=activityid,subject,_{P}_sourcetask_value,{P}_agentstate,"
                  f"{P}_agentconfidence,{P}_agentoutput,{P}_agenterror,{P}_sladue"
                  f"&$filter=_regardingobjectid_value eq {oid} and statecode eq 0"
                  f"&$orderby=createdon asc")["value"]


def complete_human(t):
    """Take the default outcome. The outcome is a lookup, not a label."""
    src = t.get(f"_{P}_sourcetask_value")
    outs = dv.get(f"{P}_taskoutcomes?$select={P}_taskoutcomeid,{P}_name,{P}_isdefault,"
                  f"{P}_sequence&$filter=_{P}_task_value eq {src}"
                  f"&$orderby={P}_sequence asc")["value"] if src else []
    pick = next((o for o in outs if o.get(f"{P}_isdefault")), outs[0] if outs else None)
    if not pick:
        return None
    dv.patch(f"tasks({t['activityid']})", {
        f"{P}_selectedoutcome_Task@odata.bind":
            f"/{P}_taskoutcomes({pick[f'{P}_taskoutcomeid']})",
        f"{P}_outcomecomment": "Completed by the hybrid end to end test.",
        "statecode": 1, "statuscode": 5})
    return pick[f"{P}_name"]


FINANCIALS = """FY2025 audited financials received from Deloitte (unqualified opinion).

Group summary, AED millions, three years to 31 December:
                         FY2023    FY2024    FY2025
Revenue                   1,840     2,110     2,395
EBITDA                      268       311       364
Net profit                  121       149       186
Total debt                  640       702       735
Net debt                    540       585       602
Shareholders funds          890       988     1,131
Interest expense             34        39        41

Derived: net leverage 1.65x (FY2025, down from 2.02x FY2023); interest cover 8.9x;
gearing 53%; EBITDA margin 15.2% and rising for three consecutive years.

Facility requested: AED 60m amortising term loan, 7 years, 1 year grace, quarterly
repayments. Purpose is a third rolling mill line plus port handling upgrade; total
project cost AED 84m with AED 24m of sponsor equity already injected and evidenced.

Security offered: first ranking mortgage over the plant (independent valuation
commissioned, report due), corporate guarantee from the parent, and an assignment of
the offtake contract with the two largest buyers (62% of FY2025 revenue).

Proposed covenants: net leverage not above 2.50x tested quarterly, interest cover not
below 4.0x tested quarterly, minimum tangible net worth AED 900m, and a cap on
additional indebtedness of AED 50m without the bank's consent. Annual review each
31 March on receipt of audited accounts."""


def stage_the_file(oid):
    """Receive the information pack and put the numbers on the deal.

    Without this the agent has nothing to analyse, correctly says so, and every task lands in
    Awaiting review - which tests the guard rail but never the happy path.
    """
    docs = dv.get(f"{P}_opportunityrequireddocuments?$select={P}_opportunityrequireddocumentid,"
                  f"{P}_name,{P}_mandatory&$filter=_{P}_opportunity_value eq {oid}")["value"]
    got = 0
    for d in docs:
        # The independent valuation is deliberately left outstanding: it is what the human
        # security task is for, and it keeps one honest gap on the file.
        if "valuation" in (d.get(f"{P}_name") or "").lower():
            continue
        dv.patch(f"{P}_opportunityrequireddocuments({d[f'{P}_opportunityrequireddocumentid']})",
                 {f"{P}_received": True, f"{P}_receivedon": "2026-09-10T09:00:00Z"})
        got += 1
    print(f"  information pack: {got} of {len(docs)} documents received "
          f"(valuation deliberately left outstanding)")

    dv.patch(f"opportunities({oid})", {
        "currentsituation": FINANCIALS,
        "customerneed": ("Funding for a third rolling mill line and port handling upgrade, "
                         "drawn in four tranches against certified progress."),
        "proposedsolution": ("AED 60m amortising term loan, 7 years with a 1 year grace period, "
                             "secured on the plant and guaranteed by the parent."),
        "qualificationcomments": ("Existing relationship since 2018. No arrears on the current "
                                  "AED 35m working capital line. Group exposure after this "
                                  "facility would be AED 95m against an internal single name "
                                  "limit of AED 150m.")})
    print("  financials, structure and proposed covenants written to the deal")



def main():
    print("Cleaning previous probe deals")
    clean()

    acct = dv.find_one("accounts", "name eq 'Emirates Steel Industries PJSC'", "accountid")
    prod = dv.find_one("products", "name eq 'Corporate Term Loan'",
                       "productid,_defaultuomid_value")
    pl = price_list()

    print("Creating the deal and adding a Corporate Term Loan line")
    oid = dv.new_id(dv.post("opportunities", {
        "name": f"{TAG} - AED 60m expansion facility",
        "customerid_account@odata.bind": f"/accounts({acct['accountid']})",
        "estimatedvalue": 60000000, "estimatedclosedate": "2026-12-31",
        "pricelevelid@odata.bind": f"/pricelevels({pl})",
        "description": (
            "Seven year amortising term loan of AED 60m to fund a third rolling mill line and "
            "the associated port handling upgrade. Borrower is the flagship of a five company "
            "group. Audited FY2025 financials, the board resolution and the trade licence have "
            "been received; the independent valuation of the plant is still outstanding.")}))

    fails = []
    tid = dv.find_one(f"{P}_salesprocesstemplates", f"{P}_name eq '{TEMPLATE}'",
                      f"{P}_salesprocesstemplateid")[f"{P}_salesprocesstemplateid"]

    dv.post("opportunityproducts", {
        "opportunityid@odata.bind": f"/opportunities({oid})",
        "productid@odata.bind": f"/products({prod['productid']})",
        "uomid@odata.bind": f"/uoms({prod['_defaultuomid_value']})",
        "quantity": 1, "priceperunit": 60000000, "isproductoverridden": False})

    end = time.time() + 150
    applied = None
    while time.time() < end and not applied:
        applied = dv.get(f"opportunities({oid})?$select=_{P}_appliedtemplate_value") \
            .get(f"_{P}_appliedtemplate_value")
        if not applied:
            time.sleep(4)
    if (applied or "").lower() != tid.lower():
        print("RESULT: FAIL -> the match engine did not apply the credit lifecycle")
        return 1
    print("  template applied")

    # A real credit deal has its information pack in before financial spreading starts. If the
    # documents are left outstanding the agent is right to refuse, and the test proves nothing
    # about the happy path - so the pack is received here, exactly as an origination officer
    # would have done it, and the financial detail the analyst needs is put on the file.
    stage_the_file(oid)

    # ---- walk ---------------------------------------------------------------
    # Human tasks are completed here. AI tasks are NOT touched: the flow owns them.
    print("\nWalking the lifecycle. Humans are completed here, agents are left to the flow.")
    seen_ai = {}
    step = 0
    stall = time.time() + 420
    while step < 40:
        ts = open_tasks(oid)
        if not ts:
            break
        t = ts[0]
        state = t.get(f"{P}_agentstate")
        if state:
            # An AI task. Wait for the flow; never complete it from here.
            key = t["activityid"]
            if seen_ai.get(key) != state:
                print(f"      {t['subject'][:52]:52} agent -> {AGENT_STATE.get(state, state)}")
                seen_ai[key] = state
            if state in (4, 5):
                label = AGENT_STATE[state]
                expected = t["subject"] == DRAFT_ONLY and state == 4
                if expected:
                    drafted = (t.get(f"{P}_agentoutput") or "")
                    print(f"      {t['subject'][:52]:52} drafted {len(drafted)} chars, "
                          f"parked for a human as designed")
                    if len(drafted) < 200:
                        fails.append(f"'{t['subject']}' parked for review with no usable draft")
                else:
                    fails.append(f"agent parked task '{t['subject']}' as {label}"
                                 + (f": {t.get(f'{P}_agenterror')}" if t.get(f"{P}_agenterror") else ""))
                    print(f"  !! {t['subject']} -> {label}; completing it by hand to keep walking")
                complete_human(t)
                stall = time.time() + 420
                continue
            if time.time() > stall:
                fails.append(f"agent never finished '{t['subject']}' "
                             f"(stuck at {AGENT_STATE.get(state, state)})")
                break
            time.sleep(6)
            continue

        step += 1
        name = complete_human(t)
        if not name:
            fails.append("task without outcomes: " + t["subject"])
            break
        print(f"  {step:2}. {t['subject'][:56]:56} -> {name}")
        stall = time.time() + 420
        time.sleep(1)

    # ---- what the agents actually did ---------------------------------------
    print("\nAgent runs on this deal")
    runs = dv.get(f"{P}_agentruns?$select={P}_name,{P}_runstatus,{P}_confidence,{P}_latencyms,"
                  f"{P}_parsedoutcome,{P}_rawresponse,{P}_errordetail,createdon"
                  f"&$filter=_{P}_opportunity_value eq {oid}&$orderby=createdon asc")["value"]
    if not runs:
        fails.append("no agent run rows were written")
    for r in runs:
        print(f"  - {(r.get(f'{P}_name') or '')[:46]:46} "
              f"{RUN_STATUS.get(r.get(f'{P}_runstatus'), '?'):16} "
              f"conf={r.get(f'{P}_confidence')} "
              f"{r.get(f'{P}_latencyms')}ms -> {r.get(f'{P}_parsedoutcome')}")
        body = (r.get(f"{P}_rawresponse") or "") + (r.get(f"{P}_errordetail") or "")
        if REFUSAL in body:
            fails.append("a harness refusal was written back as if it were agent output")

    # The drafting task also reports Success - it did its job - but it does not close itself,
    # so it must not be counted towards the autonomy number.
    ai_done = [r for r in runs if r.get(f"{P}_runstatus") == 1
               and not (r.get(f"{P}_name") or "").startswith(DRAFT_ONLY)]
    print(f"\n  {len(ai_done)} of {AUTO_COUNT} self-closing AI tasks completed without a human")
    print(f"  1 AI task drafted and handed to a person by design ({DRAFT_ONLY})")
    if len(ai_done) < AUTO_COUNT:
        fails.append(f"only {len(ai_done)} of {AUTO_COUNT} AI tasks were agent-completed")
    if len(runs) < AUTO_COUNT + 1:
        fails.append(f"only {len(runs)} of {AUTO_COUNT + 1} AI tasks produced a run at all")

    # ---- SLA countdowns -----------------------------------------------------
    all_tasks = dv.get(f"tasks?$select=activityid,subject,{P}_sladue"
                       f"&$filter=_regardingobjectid_value eq {oid}")["value"]
    with_sla = [t for t in all_tasks if t.get(f"{P}_sladue")]
    kpi = dv.get(f"slakpiinstances?$select=slakpiinstanceid,status&$filter="
                 + " or ".join(f"_regarding_value eq {t['activityid']}" for t in all_tasks)
                 )["value"] if all_tasks else []
    print(f"  {len(with_sla)}/{len(all_tasks)} tasks carry a deadline, "
          f"{len(kpi)} live SLA timer row(s) for the widget")
    if not with_sla:
        fails.append("no task carries an SLA deadline")
    if not kpi:
        fails.append("no slakpiinstance rows - the SLA widget would render empty")

    final = dv.get(f"opportunities({oid})?$select=statecode,statuscode,{P}_tasksopen,"
                   f"{P}_taskstotal,{P}_docstotal,{P}_processsummary")
    print("\n  summary   :", (final.get(f"{P}_processsummary") or "")[:260])
    print("  statecode :", final["statecode"], "(0 open, 1 won, 2 lost)")
    print("  tasks     :", final.get(f"{P}_tasksopen"), "open of",
          final.get(f"{P}_taskstotal"), "| docs:", final.get(f"{P}_docstotal"))
    if final["statecode"] != 1:
        fails.append("deal did not close as won")

    print("\nRESULT:", "PASS" if not fails else "FAIL -> " + "; ".join(fails))
    print("deal kept as evidence:", oid)
    return 0 if not fails else 1


if __name__ == "__main__":
    sys.exit(main())
