"""Step 60: export the solution as a zip that installs into a vanilla org.

Why this exists
---------------
The sales and case configurators were authored in the same org, and both layer their
own sections onto the three shared task system forms (Information, Task, Task for
Interactive experience).  In that org this is correct and wanted: a task belongs to a
case or to a deal, each panel shows only in its own context, and nothing collides.

A system form, however, is an atomic solution component.  It exports whole - there is
no way to say "export only my sections".  So a straight ExportSolution of the sales
solution produces task forms that still carry the case sections:

    cpc_outcome_sec     -> cpc_outcome.html                     (web resource)
    cpc_journal_sec     -> cpc_branchpath, cpc_outcomelabel,
                           cpc_outcomecomment, cpc_selectedoutcome  (columns)
                        -> cpc_taskoutcome_task_selectedoutcome     (relationship)
    cpc_tasksla_sec     -> Task SLA Timer Source                 (case-owned view)

None of those exist in a vanilla org, so the import fails with a missing dependency for
each one, multiplied by the three forms.

This strips the case sections out of the exported form XML.  It touches only the zip.
The org is left exactly as it is, so the case configurator keeps working, and the case
solution's own export still carries its sections and re-adds them on import.  Install
both into the same org and both sets of panels come back.

The external Modern SLA Timer PCF is deliberately NOT bundled - it has its own
repository and licence.  It stays a documented prerequisite:
https://github.com/moliveirapinto/modern-sla-timer-pcf

Usage:
    python3 step60_package.py                 # both zips into ./dist
    python3 step60_package.py --version 1.1.0.0
"""
import argparse
import base64
import io
import os
import re
import shutil
import zipfile

import dv

# Sections owned by the case configurator. Anything matching is removed from the
# exported task forms, together with the controlDescription its control points at.
FOREIGN_SECTION = re.compile(r"^cpc_")

DIST = os.path.join(os.path.dirname(os.path.abspath(__file__)), "dist")
PCF_REPO = "https://github.com/moliveirapinto/modern-sla-timer-pcf"


# ------------------------------------------------------------------ form surgery

def strip_foreign_sections(form_xml):
    """Remove every foreign-prefixed section and its control binding from one form.

    Returns (xml, [section names removed]).  Sections are matched by name so the
    removal is explicit rather than positional, and each section's control uniqueid
    is read out before deletion so the matching controlDescription goes with it -
    a controlDescription left pointing at a control that no longer exists is itself
    an import error.
    """
    removed = []
    # Sections come in two shapes: a normal <section ...>...</section> and an empty
    # self-closing <section ... />.  Matching only the first shape lets the pattern
    # run straight past a self-closing section and swallow the next real one, which
    # is how a foreign section survived the strip on the first attempt.
    pattern = re.compile(r'<section name="([^"]+)"(?:[^>]*/>|[^>]*>.*?</section>)', re.S)
    for m in list(pattern.finditer(form_xml)):
        name = m.group(1)
        if not FOREIGN_SECTION.match(name):
            continue
        block = m.group(0)
        uid = re.search(r'uniqueid="(\{[^"]+\})"', block)
        form_xml = form_xml.replace(block, "")
        if uid:
            form_xml = re.sub(
                r'<controlDescription forControl="%s">.*?</controlDescription>'
                % re.escape(uid.group(1)), "", form_xml, flags=re.S)
        removed.append(name)
    return form_xml, removed


def clean_customizations(xml):
    """Apply the form surgery across the whole customizations file.

    This deliberately does not try to carve the document into <form> blocks first.
    Form XML is embedded, quoted and nested inconsistently enough that block matching
    silently missed a form on the first attempt; section names and control uniqueids
    are unique across the document, so matching them directly is both simpler and
    exact.
    """
    xml, removed = strip_foreign_sections(xml)
    report = {}
    for r in removed:
        report[r] = report.get(r, 0) + 1
    return xml, report


def audit(xml):
    """Anything still referencing the foreign prefix would fail on import."""
    hits = {}
    for tok in re.findall(r"cpc_[A-Za-z0-9_.]*", xml):
        hits[tok] = hits.get(tok, 0) + 1
    return hits


def clean_manifest(xml, customizations):
    """Drop stale entries from solution.xml's <MissingDependencies> block.

    The platform computes this block while exporting, i.e. against the org, where the
    task forms still carry the case sections.  It is written into the zip as a manifest
    and the importer trusts it over the actual content: leaving it in place fails the
    import with the very dependencies this script has just removed, even though nothing
    references them any more.

    An entry is dropped only when the required component is genuinely no longer
    referenced by the cleaned customizations.  The test is deliberately exact rather
    than a substring scan: an id is checked as a whole GUID, and a bare name is checked
    on identifier boundaries.  Substring matching gets this wrong in both directions -
    it kept the case solution's "Task SLA Timer Source" alive purely because the sales
    view is called "Sales Task SLA Timer Source", and it dropped real platform columns
    whose names happen to appear inside longer ones.

    A genuinely missing dependency therefore still surfaces at import time rather than
    being silently hidden: the external SLA timer PCF, for instance, stays in the
    manifest because the sales forms really do bind to it.
    """
    removed, kept = [], []

    def one(m):
        block = m.group(0)
        req = re.search(r"<Required[^>]*>", block)
        if not req:
            return block
        name = re.search(r'schemaName="([^"]+)"', req.group(0))
        rid = re.search(r'id="\{([^}]+)\}"', req.group(0))
        label = name.group(1) if name else (rid.group(1) if rid else "?")

        if rid:
            # An id is unambiguous, so trust it alone. Names are ambiguous by
            # construction here, because the sales components are named after the
            # case ones they were forked from.
            referenced = rid.group(1).upper() in customizations.upper()
        elif name:
            referenced = re.search(
                r"(?<![A-Za-z0-9_.])%s(?![A-Za-z0-9_.])" % re.escape(name.group(1)),
                customizations) is not None
        else:
            referenced = True

        (kept if referenced else removed).append(label)
        return block if referenced else ""

    xml = re.sub(r"<MissingDependency>.*?</MissingDependency>", one, xml, flags=re.S)
    xml = re.sub(r"<MissingDependencies>\s*</MissingDependencies>", "", xml)
    return xml, removed, kept


# ------------------------------------------------------------------ export / repack

def export(managed):
    kind = "managed" if managed else "unmanaged"
    print(f"  exporting {kind} ...", flush=True)
    r = dv.post("ExportSolution", {
        "SolutionName": dv.SOLUTION,
        "Managed": managed,
        "ExportAutoNumberingSettings": False,
        "ExportCalendarSettings": False,
        "ExportCustomizationSettings": False,
        "ExportEmailTrackingSettings": False,
        "ExportGeneralSettings": False,
        "ExportMarketingSettings": False,
        "ExportOutlookSynchronizationSettings": False,
        "ExportRelationshipRoles": False,
        "ExportIsvConfig": False,
        "ExportSales": False,
    })
    return base64.b64decode(r["ExportSolutionFile"])


def repack(raw, out_path):
    """Clean customizations first, then judge the manifest against the result."""
    src = zipfile.ZipFile(io.BytesIO(raw))
    files = {i.filename: src.read(i.filename) for i in src.infolist()}
    report, leftover, removed, kept = {}, {}, [], []

    for name in files:
        if name.endswith("customizations.xml"):
            text, report = clean_customizations(files[name].decode("utf-8-sig"))
            leftover = audit(text)
            files[name] = text.encode("utf-8")
            cleaned = text
            break
    else:
        raise SystemExit("no customizations.xml in the exported solution")

    for name in files:
        if name.endswith("solution.xml"):
            text, removed, kept = clean_manifest(
                files[name].decode("utf-8-sig"), cleaned)
            files[name] = text.encode("utf-8")

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with zipfile.ZipFile(out_path, "w", zipfile.ZIP_DEFLATED) as dst:
        for item in src.infolist():
            dst.writestr(item, files[item.filename])
    return report, leftover, removed, kept


def build(managed, version):
    raw = export(managed)
    name = "%s_%s%s.zip" % (dv.SOLUTION, version.replace(".", "_"),
                            "_managed" if managed else "")
    out = os.path.join(DIST, name)
    report, leftover, removed, kept = repack(raw, out)

    for sec, n in sorted(report.items()):
        print(f"    - stripped {sec} from {n} form(s)")
    if removed:
        print(f"    - cleared {len(removed)} stale manifest entr(ies): "
              + ", ".join(sorted(set(removed))))
    if kept:
        print(f"    - real prerequisite(s) left in the manifest: "
              + ", ".join(sorted(set(kept))))
    if leftover:
        print("    ! STILL REFERENCED, import would fail:")
        for tok, n in sorted(leftover.items()):
            print(f"        {tok} x{n}")
        raise SystemExit("aborting: foreign references survived the strip")
    print(f"    clean -> {out} ({os.path.getsize(out):,} bytes)")
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default=None,
                    help="solution version to stamp before export, e.g. 1.1.0.0")
    args = ap.parse_args()

    if args.version:
        sol = dv.find_one("solutions", f"uniquename eq '{dv.SOLUTION}'",
                          "solutionid,version")
        dv.patch(f"solutions({sol['solutionid']})", {"version": args.version})
        print(f"  version {sol['version']} -> {args.version}")
        version = args.version
    else:
        sol = dv.find_one("solutions", f"uniquename eq '{dv.SOLUTION}'",
                          "solutionid,version")
        version = sol["version"]
        print(f"  version {version}")

    shutil.rmtree(DIST, ignore_errors=True)
    build(False, version)
    build(True, version)
    print("\n  Reminder: the Modern SLA Timer PCF is a prerequisite, install it first:")
    print("   ", PCF_REPO)
    print("done")


if __name__ == "__main__":
    main()
