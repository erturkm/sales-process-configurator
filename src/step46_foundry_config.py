"""Create Dataverse environment variables holding the Foundry connection for spc_DesignProcess.

Nothing about the connection is hardcoded: this repository is public, and the tenant, app
registration and secret are all deployment specific. Configure it with environment variables:

    export SPC_FOUNDRY_ENDPOINT="https://<your-resource>.openai.azure.com"
    export SPC_FOUNDRY_DEPLOYMENT="gpt-4.1"
    export SPC_FOUNDRY_API_VERSION="2024-12-01-preview"
    export SPC_FOUNDRY_TENANT_ID="<tenant guid>"
    export SPC_FOUNDRY_CLIENT_ID="<app registration id>"
    export SPC_FOUNDRY_CLIENT_SECRET="<client secret>"

If the Case Process Configurator happens to be installed and configured in the same environment,
run with --borrow to reuse its values so the secret never has to be re-surfaced. That is a
convenience only; this solution does not depend on the other one.
"""
import os
import sys

import dv

BORROW = "--borrow" in sys.argv

VARS = [
    ("spc_FoundryEndpoint",     "Foundry endpoint",    "SPC_FOUNDRY_ENDPOINT",      "cpc_FoundryEndpoint"),
    ("spc_FoundryDeployment",   "Foundry deployment",  "SPC_FOUNDRY_DEPLOYMENT",    "cpc_FoundryDeployment"),
    ("spc_FoundryApiVersion",   "Foundry api-version", "SPC_FOUNDRY_API_VERSION",   "cpc_FoundryApiVersion"),
    ("spc_FoundryTenantId",     "Entra tenant id",     "SPC_FOUNDRY_TENANT_ID",     "cpc_FoundryTenantId"),
    ("spc_FoundryClientId",     "Entra client id",     "SPC_FOUNDRY_CLIENT_ID",     "cpc_FoundryClientId"),
    ("spc_FoundryClientSecret", "Entra client secret", "SPC_FOUNDRY_CLIENT_SECRET", "cpc_FoundryClientSecret"),
]


def read_value(schema):
    """Read the current value of an environment variable, falling back to its default."""
    d = dv.get(f"environmentvariabledefinitions?$select=environmentvariabledefinitionid,"
               f"defaultvalue&$filter=schemaname eq '{schema}'")["value"]
    if not d:
        return None
    did = d[0]["environmentvariabledefinitionid"]
    v = dv.get(f"environmentvariablevalues?$select=value&$filter="
               f"_environmentvariabledefinitionid_value eq {did}")["value"]
    return (v[0]["value"] if v else None) or d[0].get("defaultvalue") or None


def resolve(env_var, borrow_from):
    v = os.environ.get(env_var, "").strip()
    if v:
        return v
    if BORROW:
        v = read_value(borrow_from)
        if v:
            return v
        raise SystemExit(f"--borrow was requested but {borrow_from} has no value here.")
    raise SystemExit(
        f"{env_var} is not set. Set the SPC_FOUNDRY_* environment variables (see the module\n"
        f"docstring), or pass --borrow to reuse the Case Process Configurator's configuration.")


def main():
    for schema, label, env_var, borrow_from in VARS:
        value = resolve(env_var, borrow_from)

        existing = dv.get(f"environmentvariabledefinitions?$select=environmentvariabledefinitionid"
                          f"&$filter=schemaname eq '{schema}'")["value"]
        if existing:
            defid = existing[0]["environmentvariabledefinitionid"]
            print(f"  = {schema} (definition exists)")
        else:
            # The DEFINITION belongs in the solution so an importer is prompted for it.
            r = dv.post("environmentvariabledefinitions", {
                "schemaname": schema,
                "displayname": label,
                "type": 100000000,          # String
                "isrequired": False,
            }, solution=True)
            defid = dv.new_id(r)
            print(f"  + {schema}")

        vals = dv.get(f"environmentvariablevalues?$select=environmentvariablevalueid"
                      f"&$filter=_environmentvariabledefinitionid_value eq {defid}")["value"]
        # The VALUE is deliberately NOT added to the solution. Values are exported with the
        # solution, so adding one here would carry the client secret into every zip.
        if vals:
            dv.patch(f"environmentvariablevalues({vals[0]['environmentvariablevalueid']})",
                     {"value": value})
            print("      value updated")
        else:
            dv.post("environmentvariablevalues", {
                "value": value,
                "EnvironmentVariableDefinitionId@odata.bind":
                    f"/environmentvariabledefinitions({defid})",
            })
            print("      value set")

    print("\ndone. The secret is stored in Dataverse, not in source,\n"
          "     and the value records are deliberately NOT solution components\n"
          "     so they can never be carried out in an exported zip.")


if __name__ == "__main__":
    main()
