"""Step 53: turn four tasks of the corporate credit lifecycle into AI agent tasks.

The split is deliberate and is the point of the demo: the agent does the analysis and the
drafting, and every task that commits the bank's money or goes to the customer in writing
stays with a person. Qualification, sanction, the offer letter, security execution and
disbursement are all left human.

Everything goes through spc_SaveProcessGraph rather than patching rows, so this doubles as
a round-trip test of the exact path the designer uses.
"""
import json
import sys

import dv

TEMPLATE = "Corporate Credit Facility - Full Lifecycle"
AGENT = "Corporate Credit Analyst"

# scope ids: 1 deal core, 2 narrative, 3 customer, 4 other deals, 5 notes,
#            7 tasks and outcomes, 8 required documents, 9 clock, 10 product lines
WIRE = [
    dict(
        task="Collect the credit information pack",
        scope="1,3,7,8,10",
        threshold=70,
        fallback="Credit Origination",
        prompt=(
            "Check whether the credit information pack for this borrowing request is complete "
            "enough for credit analysis to begin.\n\n"
            "The information pack for THIS stage is only these items:\n"
            "  audited financial statements, management accounts, trade licence and memorandum "
            "of association, board resolution to borrow, cash flow projections, credit bureau "
            "report, and group structure and beneficial ownership.\n"
            "The required documents list on the deal is the whole credit file and also contains "
            "items that are produced LATER in the lifecycle - the security and collateral "
            "valuation, the signed offer letter, the executed security documents and the "
            "disbursement instruction. Those are not part of this pack. Ignore them entirely; "
            "they are supposed to be outstanding at this point.\n\n"
            '- Choose "Pack complete" when every item in the list above has been received.\n'
            '- Choose "Customer did not supply" when one or more of them is still outstanding, '
            "and name exactly which.\n\n"
            "Do not assume a document exists because the deal has progressed. If the checklist "
            "shows an item as not received, it is not received."
        ),
    ),
    dict(
        task="Spread the financials and build the credit model",
        scope="1,2,3,4,7,8,10",
        threshold=85,
        fallback="Credit Risk",
        prompt=(
            "Spread the borrower's financials and give a first view on whether they support the "
            "facility being requested.\n\n"
            "Comment on leverage, interest cover and the trend across the periods you were given, "
            "in AED. State the requested amount and tenor you are assessing against.\n"
            '- Choose "Financials support the request" when the numbers comfortably carry the ask.\n'
            '- Choose "Weak - restructure the ask" when the borrower is bankable but the amount, '
            "tenor or structure needs to change.\n"
            '- Choose "Financials do not support any facility" only when no sensible structure works.\n\n'
            "The threshold on this task is 85 because it feeds the credit application. If the "
            "audited financials are missing or the periods are inconsistent, say so and keep "
            "confidence below 70 so a credit analyst picks it up."
        ),
    ),
    dict(
        task="Write the credit application",
        scope="1,2,3,4,5,7,8,10",
        threshold=75,
        fallback="Credit Risk",
        # The one AI task that deliberately does NOT close itself. This is the paper that goes to
        # the credit committee, so the pattern here is "AI drafts, human confirms": the agent
        # writes the narrative and parks the task for Credit Risk to read, edit and submit.
        auto=False,
        mode=1,   # agent proposes, human selects the outcome
        prompt=(
            "Draft the credit application narrative for the sanctioning committee.\n\n"
            "Cover, in this order: the borrower and its group, the purpose and structure of the "
            "request, the financial assessment already recorded on this deal, security and "
            "collateral, the key risks, and the mitigants. Reference the outcomes of the earlier "
            "tasks rather than re-deriving them.\n\n"
            "Write it as prose a credit officer can lift into the paper, not as bullet fragments. "
            "Put the draft in the summary field.\n"
            '- The only outcome is "Application ready for sanction". Choose it when you have '
            "produced a usable draft; if key inputs are missing, still say so in summary and keep "
            "confidence below 70 so a human writes it instead.\n\n"
            "Calibration for this task specifically: you are being scored on whether you produced "
            "a complete draft, not on whether the credit decision is certain - that decision is "
            "the committee's and is a later task. If you covered all six sections from the "
            "supplied context and your missing list is empty, that is 85 or above. Reserve "
            "anything below 70 for the case where you genuinely could not write the paper."
        ),
    ),
    dict(
        task="Schedule covenant testing and review date",
        scope="1,2,5,7,8,10",
        threshold=70,
        fallback="Portfolio Monitoring",
        prompt=(
            "Propose the post-disbursement monitoring calendar for this facility.\n\n"
            "Based on the facility type, tenor and any covenants recorded on this deal, set out "
            "the covenant testing frequency, the first test date, the annual review date and who "
            "owns each. Where a covenant was amended at sanction, use the amended terms.\n"
            '- The only outcome is "Monitoring calendar set".\n\n'
            "The sanctioned covenant package, the tenor and the repayment profile are in the deal "
            "narrative on this file. Use them.\n"
            "If no covenants are recorded anywhere on the deal, say so explicitly and keep "
            "confidence below 70 rather than inventing a schedule."
        ),
    ),
]


def pick(seq, name):
    for x in seq:
        if x.get("name") == name:
            return x
    return None


def main():
    tpl = dv.find_one("spc_salesprocesstemplates", f"spc_name eq '{TEMPLATE}'",
                      "spc_salesprocesstemplateid,spc_name")
    if not tpl:
        sys.exit(f"Template '{TEMPLATE}' not found.")
    tid = tpl["spc_salesprocesstemplateid"]

    agent = dv.find_one("bots", f"name eq '{AGENT}'",
                        "botid,name,schemaname,publishedon,template")
    if not agent:
        sys.exit(f"Agent '{AGENT}' not found.")
    if not agent.get("publishedon"):
        sys.exit(f"Agent '{AGENT}' is not published; the runtime endpoint would 404.")
    harness = "copilot" if str(agent.get("template") or "").startswith("cliagent") else "standard"
    print(f"agent   : {agent['name']} ({agent['schemaname']}) harness={harness}")

    graph = json.loads(dv.post("spc_GetProcessGraph", {"TemplateId": tid})["Graph"])

    for w in WIRE:
        node = pick(graph["nodes"], w["task"])
        if not node:
            sys.exit(f"Task '{w['task']}' not found in the template.")
        outs = [o["label"] for o in graph["outcomes"] if o["from"] == node["id"]]
        print(f"\n  AI  {w['task']}")
        print(f"      outcomes  {outs}")
        node["assignType"] = 7
        node["agent"] = {"id": agent["botid"], "name": agent["name"]}
        node["agentPrompt"] = w["prompt"]
        node["contextScope"] = w["scope"]
        node["outputTarget"] = 2        # append to the task description
        node["agentOutcomeMode"] = w.get("mode", 2)
        node["autoComplete"] = w.get("auto", True)
        node["confidenceThreshold"] = w["threshold"]
        node["agentTimeoutMins"] = 8
        node["fallbackTeam"] = w["fallback"]

    payload = {
        "template": dict(graph["template"], id=tid),
        "nodes": graph["nodes"],
        "outcomes": graph["outcomes"],
        "matchRules": graph.get("matchRules", []),
        "documents": graph.get("documents", []),
    }
    res = dv.post("spc_SaveProcessGraph", {"Graph": json.dumps(payload)})
    print("\nsaved   :", (res.get("Summary") or "")[:300])

    back = json.loads(dv.post("spc_GetProcessGraph", {"TemplateId": tid})["Graph"])
    bad = []
    for w in WIRE:
        n = pick(back["nodes"], w["task"])
        got = {
            "assignType": n["assignType"],
            "agent": (n.get("agent") or {}).get("name"),
            "contextScope": n.get("contextScope"),
            "outputTarget": n.get("outputTarget"),
            "agentOutcomeMode": n.get("agentOutcomeMode"),
            "autoComplete": n.get("autoComplete"),
            "confidenceThreshold": n.get("confidenceThreshold"),
            "agentTimeoutMins": n.get("agentTimeoutMins"),
            "fallbackTeam": (n.get("fallbackTeam") or {}).get("name"),
            "promptChars": len(n.get("agentPrompt") or ""),
        }
        print(f"\nreadback {w['task']}\n  {json.dumps(got)}")
        if got["assignType"] != 7: bad.append(f"{w['task']}/assignType")
        if got["agent"] != AGENT: bad.append(f"{w['task']}/agent")
        if got["contextScope"] != w["scope"]: bad.append(f"{w['task']}/contextScope")
        if got["fallbackTeam"] != w["fallback"]: bad.append(f"{w['task']}/fallbackTeam")
        if got["promptChars"] == 0: bad.append(f"{w['task']}/agentPrompt")
        if got["confidenceThreshold"] != w["threshold"]: bad.append(f"{w['task']}/threshold")
        if got["autoComplete"] is not w.get("auto", True): bad.append(f"{w['task']}/autoComplete")
        if got["agentOutcomeMode"] != w.get("mode", 2): bad.append(f"{w['task']}/agentOutcomeMode")

    human = [n["name"] for n in back["nodes"] if n.get("assignType") != 7]
    print(f"\nstill human ({len(human)}): " + "; ".join(human))

    if bad:
        sys.exit("ROUND TRIP FAILED for: " + ", ".join(bad))
    auto = [w["task"] for w in WIRE if w.get("auto", True)]
    drafts = [w["task"] for w in WIRE if not w.get("auto", True)]
    print(f"\nround trip OK on all {len(WIRE)} tasks")
    print(f"  closes itself above threshold ({len(auto)}): " + "; ".join(auto))
    print(f"  always parked for a human ({len(drafts)}): " + "; ".join(drafts))


if __name__ == "__main__":
    main()
