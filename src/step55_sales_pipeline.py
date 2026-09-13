"""Step 55: seed a realistic sales pipeline across all six templates.

The end-to-end tests prove one deal works. A demo needs a pipeline that looks like a real
book: deals at different stages, across different products, some won, some lost, some early.

Every deal is created the way a banker would create it - bare, then a product line - so the
async line-driven match is what applies the process, exactly as in production.

Credit lifecycle deals have their information pack and financials staged before the walk
reaches the analytical tasks, otherwise the agent correctly refuses and the pipeline fills
up with parked tasks instead of progress.

Re-runnable: every deal on the demo price list is deleted and recreated. The price list is the
cleanup handle rather than a name prefix, so the deals can carry names that look real in a demo
without the seeder losing track of what it owns.
"""
import sys
import time

import dv

P = dv.PREFIX
PRICE_LIST = "SPC Demo Price List"

AGENT_STATE = {1: "Queued", 2: "Running", 3: "Succeeded",
               4: "Awaiting review", 5: "Failed", 6: "Skipped"}

FINANCIALS = """Audited financials received (unqualified opinion).

AED millions, three years:            FY2023   FY2024   FY2025
Revenue                                1,840    2,110    2,395
EBITDA                                   268      311      364
Net profit                               121      149      186
Total debt                               640      702      735
Shareholders funds                       890      988    1,131
Interest expense                          34       39       41

Derived: net leverage 1.65x, interest cover 8.9x, gearing 53%, EBITDA margin 15.2%.

Proposed covenants: net leverage not above 2.50x tested quarterly, interest cover not below
4.0x tested quarterly, minimum tangible net worth AED 900m, cap of AED 50m on additional
indebtedness without the bank's consent. Annual review each 31 March on audited accounts."""

# account, product leaf, deal name, AED value, close date, how far to walk, how it ends
DEALS = [
    ("Emirates Steel Industries PJSC", "Corporate Term Loan",
     "capex facility for a third rolling mill line", 60_000_000, "2026-12-31", 6, None),
    ("Arabian Ranches Developments LLC", "Commercial Mortgage",
     "investment mortgage on the Phase 4 retail podium", 85_000_000, "2027-02-28", 2, None),
    ("Gulf Petrochem Trading FZE", "Working Capital Facility",
     "revolving working capital line renewal", 40_000_000, "2026-11-15", 99, "won"),

    ("Al Futtaim Logistics LLC", "Letter of Credit",
     "import LC line for the Jebel Ali consolidation hub", 25_000_000, "2026-10-31", 3, None),
    ("Gulf Petrochem Trading FZE", "Supply Chain Finance",
     "supplier finance programme for 40 upstream vendors", 55_000_000, "2027-01-31", 1, None),

    ("Nakheel Hospitality Group", "Host to Host Payments",
     "host to host payments integration across 14 properties", 1_200_000, "2026-11-30", 4, None),
    ("Union Cooperative Society", "Payroll and WPS Services",
     "WPS payroll mandate for 3,400 staff", 750_000, "2026-10-15", 2, None),

    ("Al Futtaim Logistics LLC", "Corporate Credit Card Programme",
     "corporate card programme for the fleet and travel spend", 900_000, "2026-10-20", 99, "won"),

    ("Desert Rose Foodstuff Trading LLC", "SME Business Loan",
     "expansion loan for a second cold store", 3_500_000, "2026-11-10", 2, None),
    ("Mira Medical Supplies LLC", "SME Overdraft",
     # Walked partway and then lost: a process that runs to completion can only end on one of
     # its own outcomes, so a deal killed by the customer has to be stopped mid-flight.
     "overdraft to bridge ministry receivables", 1_800_000, "2026-10-05", 3, "lost"),

    ("First Gulf Capital Partners", "FX Forward",
     "12 month EUR/AED forward programme", 2_400_000, "2026-09-30", 3, None),
    ("Meridian Industrial Group", "Interest Rate Swap",
     "IRS to fix the floating leg of the 2024 term loan", 1_100_000, "2026-12-15", 1, None),
]

CREDIT_PRODUCTS = {"Corporate Term Loan", "Commercial Mortgage", "Working Capital Facility",
                   "Project Finance Facility", "Invoice Discounting"}


def clean():
    """Delete the deals this seeder owns.

    Ownership is claimed by the demo price list, which nothing else in the solution uses.
    Matching on that rather than a name prefix keeps the deals presentable while still making
    the seeder safely re-runnable. If the price list does not exist yet there is nothing to
    clean, and we must not fall back to a broader filter that could hit real deals.
    """
    pl = dv.find_one("pricelevels", f"name eq '{PRICE_LIST}'", "pricelevelid")
    if not pl:
        print("  no demo price list yet; nothing to clean")
        return
    n = 0
    for o in dv.get(f"opportunities?$select=opportunityid,name"
                    f"&$filter=_pricelevelid_value eq {pl['pricelevelid']}")["value"]:
        for t in dv.get(f"tasks?$select=activityid&$filter=_regardingobjectid_value eq "
                        f"{o['opportunityid']}")["value"]:
            dv.call("DELETE", f"tasks({t['activityid']})")
        dv.call("DELETE", f"opportunities({o['opportunityid']})")
        n += 1
    print(f"  removed {n} previous pipeline deal(s)")


def price_list():
    ex = dv.find_one("pricelevels", f"name eq '{PRICE_LIST}'", "pricelevelid")
    if ex:
        return ex["pricelevelid"]
    return dv.new_id(dv.post("pricelevels",
                             {"name": PRICE_LIST, "begindate": "2026-01-01"}))


def stage_credit_file(oid, value, blurb):
    """Receive the information pack and put the numbers on the file.

    The requested amount, tenor and purpose have to be on the file too, not just the accounts.
    Spreading financials with no idea what is being asked for is not an analysis a credit
    officer could sign, and the agent correctly refuses to pretend otherwise.
    """
    docs = dv.get(f"{P}_opportunityrequireddocuments?$select="
                  f"{P}_opportunityrequireddocumentid,{P}_name"
                  f"&$filter=_{P}_opportunity_value eq {oid}")["value"]
    for d in docs:
        if "valuation" in (d.get(f"{P}_name") or "").lower():
            continue
        dv.patch(f"{P}_opportunityrequireddocuments"
                 f"({d[f'{P}_opportunityrequireddocumentid']})",
                 {f"{P}_received": True, f"{P}_receivedon": "2026-09-08T09:00:00Z"})
    ask = ("Facility requested: AED {:,.0f}, amortising, 7 years with a 1 year grace period "
           "and quarterly repayments. Purpose: {}.\nSecurity offered: first ranking charge over "
           "the financed assets plus a corporate guarantee from the parent. The independent "
           "valuation has been commissioned and the report is awaited."
           ).format(value, blurb)
    dv.patch(f"opportunities({oid})", {"currentsituation": FINANCIALS + "\n\n" + ask})


def walk(oid, steps, label):
    """Complete human tasks. AI tasks are left to the flow; we only wait for them."""
    done = 0
    guard = 0
    stall = time.time() + 300
    while done < steps and guard < 60:
        guard += 1
        ts = dv.get(f"tasks?$select=activityid,subject,_{P}_sourcetask_value,{P}_agentstate"
                    f"&$filter=_regardingobjectid_value eq {oid} and statecode eq 0"
                    f"&$orderby=createdon asc")["value"]
        if not ts:
            break
        t = ts[0]
        state = t.get(f"{P}_agentstate")
        if state in (1, 2):
            if time.time() > stall:
                print(f"      ! {label}: agent stuck on '{t['subject']}'")
                break
            time.sleep(5)
            continue
        if state in (4, 5):
            # Drafted and parked, or failed. A pipeline is allowed to contain both; that is
            # what the review queue looks like in real life. Leave it open and stop here.
            print(f"      · {t['subject'][:44]} left {AGENT_STATE[state].lower()} "
                  f"(this is the human's queue)")
            break
        src = t.get(f"_{P}_sourcetask_value")
        outs = dv.get(f"{P}_taskoutcomes?$select={P}_taskoutcomeid,{P}_name,{P}_isdefault,"
                      f"{P}_sequence&$filter=_{P}_task_value eq {src}"
                      f"&$orderby={P}_sequence asc")["value"] if src else []
        pick = next((o for o in outs if o.get(f"{P}_isdefault")), outs[0] if outs else None)
        if not pick:
            break
        dv.patch(f"tasks({t['activityid']})", {
            f"{P}_selectedoutcome_Task@odata.bind":
                f"/{P}_taskoutcomes({pick[f'{P}_taskoutcomeid']})",
            f"{P}_outcomecomment": "Completed as part of the demo pipeline.",
            "statecode": 1, "statuscode": 5})
        done += 1
        stall = time.time() + 300
        time.sleep(1)
    return done


def lose(oid):
    """A deal that dies mid-process. Cancel the open tasks first so it does not look stuck."""
    if dv.get(f"opportunities({oid})?$select=statecode")["statecode"] != 0:
        return  # the process already closed it on one of its own outcomes
    for t in dv.get(f"tasks?$select=activityid&$filter=_regardingobjectid_value eq {oid} "
                    f"and statecode eq 0")["value"]:
        dv.patch(f"tasks({t['activityid']})", {"statecode": 2, "statuscode": 6})
    dv.post("LoseOpportunity", {
        "Status": 4,
        "OpportunityClose": {
            "subject": "Customer went with an incumbent bank",
            "description": "Pricing matched but the incumbent held the primary operating account.",
            "opportunityid@odata.bind": f"/opportunities({oid})",
            "@odata.type": "Microsoft.Dynamics.CRM.opportunityclose"}})


def main():
    print("Cleaning previous pipeline")
    clean()
    pl = price_list()

    made = []
    for acct_name, prod_name, blurb, value, close, steps, ending in DEALS:
        acct = dv.find_one("accounts", f"name eq '{acct_name}'", "accountid")
        prod = dv.find_one("products", f"name eq '{prod_name}'",
                           "productid,_defaultuomid_value")
        if not acct or not prod:
            print(f"  !! skipped {acct_name} / {prod_name} - not found")
            continue

        name = f"{acct_name.split()[0]} {blurb}"
        oid = dv.new_id(dv.post("opportunities", {
            "name": name[:300],
            "customerid_account@odata.bind": f"/accounts({acct['accountid']})",
            "estimatedvalue": value,
            "estimatedclosedate": close,
            "pricelevelid@odata.bind": f"/pricelevels({pl})",
            "description": blurb[0].upper() + blurb[1:] + "."}))
        dv.post("opportunityproducts", {
            "opportunityid@odata.bind": f"/opportunities({oid})",
            "productid@odata.bind": f"/products({prod['productid']})",
            "uomid@odata.bind": f"/uoms({prod['_defaultuomid_value']})",
            "quantity": 1, "priceperunit": value, "isproductoverridden": False})

        end = time.time() + 150
        tid = None
        while time.time() < end and not tid:
            tid = dv.get(f"opportunities({oid})?$select=_{P}_appliedtemplate_value") \
                .get(f"_{P}_appliedtemplate_value")
            if not tid:
                time.sleep(4)
        if not tid:
            print(f"  !! {prod_name}: no process matched")
            continue
        tname = dv.get(f"{P}_salesprocesstemplates({tid})?$select={P}_name")[f"{P}_name"]

        if prod_name in CREDIT_PRODUCTS and steps > 2:
            stage_credit_file(oid, value, blurb)

        n = walk(oid, steps, prod_name)
        if ending == "lost":
            lose(oid)
        st = dv.get(f"opportunities({oid})?$select=statecode,{P}_taskstotal")
        status = {0: "open", 1: "WON", 2: "LOST"}[st["statecode"]]
        print(f"  {prod_name[:32]:32} {tname[:34]:34} {n:2} step(s)  {status}")
        made.append(oid)

    print(f"\n{len(made)} deal(s) seeded")

    runs = dv.get(f"{P}_agentruns?$select={P}_name,{P}_runstatus,{P}_confidence"
                  f"&$orderby=createdon desc&$top=20")["value"]
    print(f"{len([r for r in runs if r.get(f'{P}_runstatus') == 1])} recent successful agent run(s) "
          f"visible in the audit table")
    return 0


if __name__ == "__main__":
    sys.exit(main())
