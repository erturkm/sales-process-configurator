"""Step 13: register the outcome engine plugin type, the advance step and the graph custom APIs."""
import dv
from dv import label
from step7_register_plugin import register_assembly, register_type, register_step

P = dv.PREFIX

# name, display, plugin type, bound entity ("" = global), request params, response props
APIS = [
    ("GetProcessCatalog", "Get Process Catalog", "Spc.Plugins.GetProcessCatalog", "",
     [], [("Catalog", 14)]),
    ("AuthorProcess", "Author Process", "Spc.Plugins.AuthorProcess", "",
     [("ProcessJson", 14, True), ("Mode", 10, False)],
     [("Summary", 14), ("TemplateId", 10), ("WarningCount", 5)]),
    ("GetProcessGraph", "Get Process Graph", "Spc.Plugins.GetProcessGraph", "",
     [("TemplateId", 10, True)], [("Graph", 14)]),
    ("SaveProcessGraph", "Save Process Graph", "Spc.Plugins.SaveProcessGraph", "",
     [("Graph", 14, True)], [("TemplateId", 10), ("Summary", 14)]),
    ("TestMatchRules", "Test Match Rules", "Spc.Plugins.TestMatchRules", "",
     [("Rules", 14, True)], [("Result", 14)]),
    ("GetOpportunityView", "Get Opportunity View", "Spc.Plugins.GetOpportunityView", "",
     [("OpportunityId", 10, True)], [("View", 14)]),
]

# Dataverse custom API type codes: 5 = Integer, 10 = String, 14 = StringArray? -> actually
# 0 Boolean, 1 DateTime, 2 Decimal, 3 Entity, 4 EntityCollection, 5 EntityReference, 6 Float,
# 7 Integer, 8 Money, 9 Picklist, 10 String, 11 StringArray, 12 Guid
TYPE_INT = 7
TYPE_STR = 10


def norm(t):
    """Map the shorthand used above onto real Dataverse custom API parameter types."""
    return TYPE_INT if t == 5 else TYPE_STR


def upsert_api(uniquename, display, plugin_type_id, bound, req, resp):
    name = f"{P}_{uniquename}"
    ex = dv.find_one("customapis", f"uniquename eq '{name}'", "customapiid,uniquename")
    body = {
        "uniquename": name,
        "name": name,
        "displayname": display,
        "description": display,
        "bindingtype": 0,
        "boundentitylogicalname": bound or None,
        "isfunction": False,
        "isprivate": False,
        "allowedcustomprocessingsteptype": 0,
        "executeprivilegename": None,
        "PluginTypeId@odata.bind": f"/plugintypes({plugin_type_id})",
    }
    if ex:
        api_id = ex["customapiid"]
        dv.patch(f"customapis({api_id})", {
            "displayname": display,
            "PluginTypeId@odata.bind": f"/plugintypes({plugin_type_id})"}, solution=True)
        print("  ~ api", name)
    else:
        api_id = dv.new_id(dv.post("customapis", body, solution=True))
        print("  + api", name)

    have_req = {r["uniquename"] for r in dv.get(
        f"customapirequestparameters?$select=uniquename&$filter=_customapiid_value eq {api_id}")["value"]}
    for i, (pname, ptype, required) in enumerate(req):
        if pname in have_req:
            continue
        dv.post("customapirequestparameters", {
            "uniquename": pname, "name": pname, "displayname": pname,
            "type": norm(ptype), "isoptional": not required,
            "CustomAPIId@odata.bind": f"/customapis({api_id})",
        }, solution=True)
        print("    + req", pname)

    have_resp = {r["uniquename"] for r in dv.get(
        f"customapiresponseproperties?$select=uniquename&$filter=_customapiid_value eq {api_id}")["value"]}
    for pname, ptype in resp:
        if pname in have_resp:
            continue
        dv.post("customapiresponseproperties", {
            "uniquename": pname, "name": pname, "displayname": pname,
            "type": norm(ptype),
            "CustomAPIId@odata.bind": f"/customapis({api_id})",
        }, solution=True)
        print("    + resp", pname)
    return api_id


def main():
    print("Assembly")
    asm = register_assembly()

    print("Types")
    types = {}
    for _, disp, typename, _, _, _ in APIS:
        types[typename] = register_type(asm, typename, disp)
    t_advance = register_type(asm, "Spc.Plugins.AdvanceProcess", "Advance Process On Outcome")

    print("Custom APIs")
    for uniquename, disp, typename, bound, req, resp in APIS:
        upsert_api(uniquename, disp, types[typename], bound, req, resp)

    print("Steps")
    register_step(t_advance, "Update", "task", 40, 0,
                  "SPC: Advance process on task outcome", rank=15,
                  filtering="statecode,statuscode,spc_selectedoutcome,spc_outcomelabel")

    dv.publish_all()
    print("done")


if __name__ == "__main__":
    main()
