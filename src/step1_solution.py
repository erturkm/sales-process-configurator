"""Step 1: create publisher cpc and unmanaged solution SalesProcessConfigurator."""
import dv

pub = dv.find_one("publishers", "uniquename eq 'spcpublisher'", "publisherid,uniquename,customizationprefix")
if pub:
    print("publisher exists", pub["publisherid"], pub["customizationprefix"])
else:
    r = dv.post("publishers", {
        "uniquename": "spcpublisher",
        "friendlyname": "Sales Process Configurator",
        "description": "Publisher for the Sales Process Configurator solution",
        "customizationprefix": "spc",
        "customizationoptionvalueprefix": 74310,
    })
    pub = {"publisherid": dv.new_id(r)}
    print("publisher created", pub["publisherid"])

sol = dv.find_one("solutions", f"uniquename eq '{dv.SOLUTION}'", "solutionid,uniquename,version")
if sol:
    print("solution exists", sol["solutionid"])
else:
    r = dv.post("solutions", {
        "uniquename": dv.SOLUTION,
        "friendlyname": "Sales Process Configurator",
        "description": "Declarative sales process blueprints for opportunities: product-line match rules, BPF and stage-linked tasks with owners and task SLAs, document packages.",
        "version": "1.0.0.0",
        "publisherid@odata.bind": f"/publishers({pub['publisherid']})",
    })
    print("solution created", dv.new_id(r))
