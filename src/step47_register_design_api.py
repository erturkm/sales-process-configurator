"""Step 47 (Phase 8): register the Process Copilot design API.

Re-run after any `dotnet build -c Release` in plugin/.

Registers:
  spc_DesignProcess  read an uploaded SOP and design a case process with Azure AI Foundry

Parameter types: 10 = String, 5 = Integer.
"""
import dv
from step7_register_plugin import register_assembly, register_type
from step13_register_apis import upsert_api

APIS = [
    ("DesignProcess", "Design Process From Document", "Spc.Plugins.DesignProcess", "",
     [("AnnotationId", 10, False), ("SopText", 10, False), ("Instructions", 10, False),
      ("Mode", 10, False)],
     [("ProcessJson", 10), ("Notes", 10), ("SourceName", 10), ("Model", 10),
      ("Endpoint", 10), ("Deployment", 10), ("ApiVersion", 10), ("Token", 10),
      ("SystemPrompt", 10), ("UserPrompt", 10), ("SchemaJson", 10)]),
]


def main():
    print("Assembly")
    asm = register_assembly()

    print("Types")
    types = {}
    for _, disp, typename, _, _, _ in APIS:
        types[typename] = register_type(asm, typename, disp)

    print("Custom APIs")
    for uniquename, disp, typename, bound, req, resp in APIS:
        upsert_api(uniquename, disp, types[typename], bound, req, resp)

    print("Publishing ...")
    dv.publish_all()
    print("done")


if __name__ == "__main__":
    main()
