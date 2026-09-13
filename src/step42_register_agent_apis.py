"""Step 42 (v2): register the AI agent plugin types and custom APIs.

Re-run after any `dotnet build -c Release` in plugin/.

Registers:
  spc_BuildOpportunityContext  assemble grounded CRM context for a prompt
  spc_CompleteAgentTask  write an agent turn back and advance the process
  spc_GetAgentCatalog    published, non-system agents for the designer picker
"""
import dv
from step7_register_plugin import register_assembly, register_type
from step13_register_apis import upsert_api

P = dv.PREFIX

APIS = [
    ("BuildOpportunityContext", "Build Opportunity Context", "Spc.Plugins.BuildOpportunityContext", "",
     [("OpportunityId", 10, True), ("Scope", 10, False), ("BudgetChars", 5, False)],
     [("Context", 10), ("Length", 5)]),

    ("CompleteAgentTask", "Complete Agent Task", "Spc.Plugins.CompleteAgentTask", "",
     [("TaskId", 10, True), ("Output", 10, False), ("OutcomeName", 10, False),
      ("Confidence", 10, False), ("RawResponse", 10, False), ("PromptSent", 10, False),
      ("ContextSent", 10, False), ("LatencyMs", 10, False), ("Error", 10, False),
      ("ConversationId", 10, False), ("RequestedBy", 10, False), ("AgentText", 10, False)],
     [("AgentRunId", 10), ("Completed", 10), ("State", 10), ("Reason", 10)]),

    ("PrepareAgentTask", "Prepare Agent Task", "Spc.Plugins.PrepareAgentTask", "",
     [("TaskId", 10, True)],
     [("Ok", 10), ("Error", 10), ("AgentSchemaName", 10), ("AgentName", 10),
      ("Message", 10), ("PromptSent", 10), ("ContextSent", 10), ("TimeoutMins", 5),
      ("AgentTemplate", 10), ("Harness", 10)]),

    ("GetAgentCatalog", "Get Agent Catalog", "Spc.Plugins.GetAgentCatalog", "",
     [("IncludeSystem", 10, False), ("IncludeUnpublished", 10, False)],
     [("Catalog", 10)]),
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
