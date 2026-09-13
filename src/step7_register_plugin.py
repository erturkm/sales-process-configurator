"""Step 7: register the plugin assembly, types and SDK message processing steps."""
import base64, hashlib, os, subprocess, sys
import dv

HERE = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(HERE, "plugin", "bin", "Release", "SpcPlugins.dll")
ASM_NAME = "SpcPlugins"

# public key token from the signing key
PUBLIC_KEY_TOKEN = None


def compute_identity():
    """Read the assembly identity with ildasm-free reflection using dotnet + a tiny helper."""
    global PUBLIC_KEY_TOKEN
    script = os.path.join(HERE, "plugin", "identity.csx")
    # simplest reliable route: ask Dataverse to parse it. We only need name/version/culture/token,
    # and Dataverse recomputes them server side when Content is supplied.
    return None


def register_assembly():
    content = base64.b64encode(open(DLL, "rb").read()).decode()
    existing = dv.find_one("pluginassemblies", f"name eq '{ASM_NAME}'", "pluginassemblyid,version")
    body = {
        "content": content,
        "name": ASM_NAME,
        "isolationmode": 2,          # sandbox
        "sourcetype": 0,             # database
        "description": "Sales Process Configurator engine: applies process templates on opportunity create.",
    }
    if existing:
        dv.patch(f"pluginassemblies({existing['pluginassemblyid']})", {"content": content}, solution=True)
        print("  ~ assembly updated")
        return existing["pluginassemblyid"]
    aid = dv.new_id(dv.post("pluginassemblies", body, solution=True))
    print("  + assembly registered", aid)
    return aid


def register_type(asm_id, typename, friendly):
    ex = dv.find_one("plugintypes", f"typename eq '{typename}'", "plugintypeid")
    if ex:
        print("  exists type", typename)
        return ex["plugintypeid"]
    # plugintype.friendlyname carries a uniqueness constraint across the whole org, and the case
    # configurator already owns the unadorned names, so every sales type is namespaced.
    disp = friendly if friendly.startswith("SPC ") else "SPC " + friendly
    tid = dv.new_id(dv.post("plugintypes", {
        "typename": typename,
        "friendlyname": disp,
        "name": disp,
        "pluginassemblyid@odata.bind": f"/pluginassemblies({asm_id})",
    }, solution=True))
    print("  + type", typename)
    return tid


def message_id(name):
    r = dv.find_one("sdkmessages", f"name eq '{name}'", "sdkmessageid")
    return r["sdkmessageid"]


def filter_id(msg_id, entity):
    r = dv.find_one("sdkmessagefilters",
                    f"_sdkmessageid_value eq {msg_id} and primaryobjecttypecode eq '{entity}'",
                    "sdkmessagefilterid")
    return r["sdkmessagefilterid"] if r else None


def register_step(plugin_type_id, message, entity, stage, mode, name, rank=1, filtering=None):
    """stage: 20 pre-operation, 40 post-operation. mode: 0 sync, 1 async."""
    ex = dv.find_one("sdkmessageprocessingsteps", f"name eq '{name}'", "sdkmessageprocessingstepid")
    if ex:
        print("  exists step", name)
        return ex["sdkmessageprocessingstepid"]
    mid = message_id(message)
    fid = filter_id(mid, entity)
    body = {
        "name": name,
        "description": name,
        "mode": mode,
        "rank": rank,
        "stage": stage,
        "supporteddeployment": 0,
        "invocationsource": 0,
        "plugintypeid@odata.bind": f"/plugintypes({plugin_type_id})",
        "sdkmessageid@odata.bind": f"/sdkmessages({mid})",
    }
    if fid:
        body["sdkmessagefilterid@odata.bind"] = f"/sdkmessagefilters({fid})"
    if filtering:
        body["filteringattributes"] = filtering
    sid = dv.new_id(dv.post("sdkmessageprocessingsteps", body, solution=True))
    dv.patch(f"sdkmessageprocessingsteps({sid})", {"statecode": 0, "statuscode": 1})
    print("  + step", name)
    return sid


if __name__ == "__main__":
    if not os.path.exists(DLL):
        sys.exit("Build the plugin first: dotnet build -c Release")
    print("Assembly")
    asm = register_assembly()

    print("Types")
    t_apply = register_type(asm, "Spc.Plugins.ApplySalesProcess", "Apply Sales Process")
    t_roll = register_type(asm, "Spc.Plugins.UpdateOpportunityRollups", "Update Opportunity Rollups")

    print("Steps")
    # Post-operation on create, so the product lines written by whatever created the deal are
    # readable. This is the one real ordering difference from the case engine: a case carries its
    # subject in the create payload, but a deal's product lines are separate records that only
    # exist after the opportunity does. Anything that writes lines must therefore either write
    # them first or re-apply, which is what SPC: Re-apply on line change below covers.
    register_step(t_apply, "Create", "opportunity", 40, 0,
                  "SPC: Apply sales process on deal create", rank=10)
    # A deal is very often saved bare and then filled with product lines, so the first pass finds
    # nothing to match on. Re-running when the first line lands is what makes the product-family
    # targeting usable in practice rather than only on perfectly formed records.
    register_step(t_apply, "Create", "opportunityproduct", 40, 1,
                  "SPC: Re-apply sales process when a product line is added", rank=10)
    register_step(t_roll, "Update", "task", 40, 1,
                  "SPC: Roll up task progress to deal", rank=20,
                  filtering="statecode,statuscode")
    register_step(t_roll, "Update", "spc_opportunityrequireddocument", 40, 1,
                  "SPC: Roll up document progress to deal", rank=20,
                  filtering="spc_received")
    dv.publish_all()
    print("done")
