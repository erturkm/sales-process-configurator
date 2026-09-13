"""Step 56 (Phase 8): end to end test of spc_DesignProcess from an uploaded .docx.

Uploads the procedure as an annotation, asks the Copilot to design a sales process from it,
and reports what came back. Deletes the note afterwards unless --keep is passed.
"""
import base64, json, os, sys, time, urllib.request
import dv


def foundry(prep):
    """Call Azure AI Foundry the way the designer web resource does."""
    url = (prep["Endpoint"] + "/openai/deployments/" + prep["Deployment"]
           + "/chat/completions?api-version=" + prep["ApiVersion"])
    body = {
        "messages": [
            {"role": "system", "content": prep["SystemPrompt"]},
            {"role": "user", "content": prep["UserPrompt"]},
        ],
        "max_completion_tokens": 32000,
        "response_format": {"type": "json_schema", "json_schema": {
            "name": "sales_process", "strict": True,
            "schema": json.loads(prep["SchemaJson"])}},
    }
    req = urllib.request.Request(url, data=json.dumps(body).encode(), headers={
        "Authorization": "Bearer " + prep["Token"], "Content-Type": "application/json"})
    res = json.loads(urllib.request.urlopen(req, timeout=600).read())
    choice = res["choices"][0]
    content = (choice.get("message") or {}).get("content")
    if not content:
        raise RuntimeError(f"empty design, finish_reason={choice.get('finish_reason')}")
    return content

DOC = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                   "demo", "RAKBANK_SLS-TRD-021_Trade_Finance_Origination.docx")


def main():
    keep = "--keep" in sys.argv
    raw = open(DOC, "rb").read()
    print(f"uploading {os.path.basename(DOC)} ({len(raw):,} bytes)")

    note = dv.post("annotations", {
        "subject": "SOP upload (design test)",
        "filename": os.path.basename(DOC),
        "mimetype": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "documentbody": base64.b64encode(raw).decode(),
    })
    nid = dv.new_id(note)
    print("  annotation:", nid)

    try:
        t0 = time.time()
        prep = dv.post("spc_DesignProcess", {
            "AnnotationId": nid,
            "Instructions": "This is for RAKBANK wholesale trade finance in the UAE.",
            "Mode": "prepare",
        })
        print(f"  prepared in {time.time() - t0:.1f}s using {prep.get('Model')}")
        print("  ", prep.get("Notes"))
        print(f"   system prompt {len(prep['SystemPrompt']):,} chars,"
              f" user prompt {len(prep['UserPrompt']):,} chars")

        # This is exactly what the designer does in the browser.
        t1 = time.time()
        design = foundry(prep)
        print(f"  designed in {time.time() - t1:.1f}s")
        # A design pass costs ten minutes. Never let a later failure throw it away.
        out = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                           "demo", "last_design.json")
        open(out, "w").write(design)
        print("  saved", out)
        r = {"ProcessJson": design, "Model": prep.get("Model")}

        d = json.loads(design)
        print()
        print("=" * 78)
        print(f"NAME  : {d.get('name')}")
        print(f"DESC  : {d.get('description')}")
        print(f"BPF   : {d.get('businessProcessFlow')}  start={d.get('startStage')}")
        print(f"RANK  : {d.get('rank')}   priority={d.get('setOpportunityPriority')}   publish={d.get('publish')}")
        print(f"SLA   : qualification={d.get('qualificationHours')}h  close={d.get('closeHours')}h")
        print(f"PKG   : {d.get('documentPackage')}")

        print(f"\nRULES ({len(d.get('rules', []))})")
        for x in d.get("rules", []):
            print(f"  [g{x.get('group')}] {x.get('label')} {x.get('operator')} {x.get('value')!r}")

        tasks = d.get("tasks", [])
        ai = [t for t in tasks if t.get("assignTo") == "ai agent"]
        print(f"\nTASKS ({len(tasks)} total, {len(ai)} AI, {len(tasks) - len(ai)} human)")
        for i, t in enumerate(tasks, 1):
            who = t.get("assignTo")
            tag = "AI " if who == "ai agent" else "   "
            target = t.get("agentName") if who == "ai agent" else (t.get("assignee") or who)
            block = "!" if t.get("blocksStage") else " "
            print(f" {tag}{i:2}.{block} [{t.get('stage')}] {t.get('subject')}")
            print(f"        -> {target}   due {t.get('dueHours')}h   breach: {t.get('onBreach')}")
            if who == "ai agent":
                print(f"        mode={t.get('agentOutcomeMode')} auto={t.get('autoComplete')} "
                      f"conf>={t.get('confidenceThreshold')} out={t.get('outputTarget')}")
                print(f"        ctx={t.get('agentContext')}")
                print(f"        prompt: {(t.get('agentPrompt') or '')[:150]}")

        print(f"\nASSUMPTIONS ({len(d.get('assumptions', []))})")
        for a in d.get("assumptions", []):
            print("  *", a[:220])

        print("\n" + "=" * 78)
        print("now rendering through spc_AuthorProcess preview ...")
        pv = dv.post("spc_AuthorProcess", {"ProcessJson": r["ProcessJson"], "Mode": "preview"})
        summary = pv.get("Summary") or pv.get("Preview") or json.dumps(pv)[:400]
        print(summary[:2500])
        warn = pv.get("Warnings")
        if warn:
            print("\nWARNINGS FROM PREVIEW:")
            print(warn[:1500])
    except Exception as e:
        print("\nFAILED:", e)
        raise
    finally:
        if keep:
            print(f"\n(note {nid} kept)")
        else:
            dv.call("DELETE", f"annotations({nid})")
            print(f"\n(note {nid} deleted)")


if __name__ == "__main__":
    main()
