"""Step 49 (Phase 4): the cloud flow that actually runs an AI agent task.

ProcessRuntime parks AI-assigned tasks as Queued and spc_CompleteAgentTask knows how to write a
turn back and advance the graph. This is the missing hop between them.

The flow is deliberately thin. It does not assemble prompts, resolve context scopes or interpret
outcomes - all of that is configuration a business user edits in the designer, so it lives in
spc_PrepareAgentTask and spc_CompleteAgentTask where it can be tested. The flow only:

    trigger on a Queued AI task
      -> spc_PrepareAgentTask   (claims the task, returns the agent + one ready message)
      -> ExecuteCopilotAsyncV2  (the long call a sandboxed plug-in cannot make)
      -> spc_CompleteAgentTask  (parses the reply, audits, completes or parks for review)

Re-runnable: upserts the connection references and the workflow row by name.
"""
import json
import os
import subprocess
import urllib.parse
import urllib.request

import dv


def environment_id():
    """The Power Platform environment id for whatever DATAVERSE_URL points at.

    This used to be a hard-coded literal, which silently bound every install to the
    environment it was first authored in: a second org would look up its connections
    in someone else's environment and either find none or wire the flow to the wrong
    tenant's connections. The organization row carries the id, so ask the org itself.
    Override with SPC_ENV_ID if you need to pin it.
    """
    pinned = os.environ.get("SPC_ENV_ID")
    if pinned:
        return pinned
    env = None
    try:
        env = dv.get("RetrieveCurrentOrganization(AccessType='Default')")
    except Exception:
        pass
    if env:
        detail = env.get("Detail") or {}
        for key in ("EnvironmentId", "environmentId"):
            if detail.get(key):
                return detail[key]
    raise SystemExit(
        "Could not determine the environment id for %s.\n"
        "  Set it explicitly:  export SPC_ENV_ID=\"<guid>\"\n"
        "  (Power Platform admin center > your environment > Environment ID)"
        % (dv.ORG or "the target org"))


ENV_ID = environment_id()
FLOW_NAME = "SPC - Run AI agent task"

DV_API = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"
MCS_API = "/providers/Microsoft.PowerApps/apis/shared_microsoftcopilotstudio"
AGN_API = "/providers/Microsoft.PowerApps/apis/shared_agentnode"

REFS = [
    ("spc_dataverse", "SPC Dataverse", DV_API, "shared_commondataserviceforapps"),
    ("spc_copilotstudio", "SPC Copilot Studio", MCS_API, "shared_microsoftcopilotstudio"),
    ("spc_agentnode", "SPC Agent Node", AGN_API, "shared_agentnode"),
]


# --------------------------------------------------------------------------- connections

def powerapps_token():
    out = subprocess.run(
        ["az", "account", "get-access-token", "--resource", "https://service.powerapps.com/",
         "--query", "accessToken", "-o", "tsv"],
        capture_output=True, text=True, check=True)
    return out.stdout.strip()


def find_connection(token, api_name):
    """Newest Connected connection for this connector in the target environment."""
    flt = urllib.parse.quote("environment eq '%s'" % ENV_ID)
    url = (f"https://api.powerapps.com/providers/Microsoft.PowerApps/apis/{api_name}"
           f"/connections?api-version=2016-11-01&$filter={flt}")
    req = urllib.request.Request(url, headers={"Authorization": f"Bearer {token}"})
    with urllib.request.urlopen(req, timeout=60) as r:
        rows = json.load(r).get("value", [])
    ok = [c for c in rows
          if any(s.get("status") == "Connected" for s in c["properties"].get("statuses", []))]
    if not ok:
        raise SystemExit(f"No connected '{api_name}' connection in the environment. "
                         "Create one in make.powerapps.com first.")
    ok.sort(key=lambda c: c["properties"].get("createdTime", ""), reverse=True)
    return ok[0]["name"]


def upsert_ref(logical, display, connector_id, connection_id):
    q = ("connectionreferences?$select=connectionreferenceid"
         f"&$filter=connectionreferencelogicalname eq '{logical}'")
    rows = dv.get(q)["value"]
    body = {
        "connectionreferencedisplayname": display,
        "connectorid": connector_id,
        "connectionid": connection_id,
    }
    if rows:
        rid = rows[0]["connectionreferenceid"]
        dv.patch(f"connectionreferences({rid})", body)
        print(f"  ~ {logical}")
    else:
        body["connectionreferencelogicalname"] = logical
        dv.post("connectionreferences", body, solution=True)
        print(f"  + {logical}")


# --------------------------------------------------------------------------- definition

def clientdata():
    task_id = "@triggerOutputs()?['body/activityid']"
    prep = "outputs('Prepare_the_agent_turn')?['body/%s']"

    def unbound(name, params, run_after):
        return {
            "runAfter": run_after,
            "type": "OpenApiConnection",
            "inputs": {
                "host": {"connectionName": "shared_commondataserviceforapps",
                         "operationId": "PerformUnboundAction", "apiId": DV_API},
                "parameters": dict({"actionName": name}, **params),
                "authentication": "@parameters('$authentication')",
            },
        }

    # Everything the audit row needs, shared by the success and failure write-backs.
    def provenance(conv="@variables('conversationId')"):
        return {
            "item/ConversationId": conv,
            "item/PromptSent": "@" + prep % "PromptSent",
            "item/ContextSent": "@" + prep % "ContextSent",
            "item/LatencyMs": "@string(div(sub(ticks(utcNow()), variables('startTicks')), 10000))",
            "item/RequestedBy": "Cloud flow: " + FLOW_NAME,
        }

    definition = {
        "$schema": "https://schema.management.azure.com/providers/Microsoft.Logic/schemas/"
                   "2016-06-01/workflowdefinition.json#",
        "contentVersion": "1.0.0.0",
        "parameters": {
            "$connections": {"defaultValue": {}, "type": "Object"},
            "$authentication": {"defaultValue": {}, "type": "SecureObject"},
        },
        "triggers": {
            "When_an_AI_task_is_queued": {
                "type": "OpenApiConnectionWebhook",
                "inputs": {
                    "host": {"connectionName": "shared_commondataserviceforapps",
                             "operationId": "SubscribeWebhookTrigger", "apiId": DV_API},
                    "parameters": {
                        "subscriptionRequest/message": 3,          # create or update
                        "subscriptionRequest/entityname": "task",
                        "subscriptionRequest/scope": 4,            # organization
                        # The single trigger signal. spc_agentqueuedon is written only when a turn
                        # is genuinely requested, never by this flow, so the flow's own state
                        # writes cannot wake it. Filtering on spc_agentstate instead costs three
                        # wasted runs per task (Running, then the completion write, then its echo).
                        "subscriptionRequest/filteringattributes": "spc_agentqueuedon",
                    },
                    "authentication": "@parameters('$authentication')",
                },
            }
        },
        "actions": {
            # InitializeVariable cannot be nested inside a condition, so the clock starts first.
            "Start_the_clock": {
                "runAfter": {},
                "type": "InitializeVariable",
                "inputs": {"variables": [
                    {"name": "startTicks", "type": "integer", "value": "@ticks(utcNow())"}]},
            },
            "New_conversation_id": {
                "runAfter": {"Start_the_clock": ["Succeeded"]},
                "type": "InitializeVariable",
                "inputs": {"variables": [
                    {"name": "conversationId", "type": "string", "value": "@{guid()}"}]},
            },

            # Belt and braces. The trigger attribute should mean only requested turns arrive, but
            # a task can be closed or taken over by a human between the request and the delivery,
            # so the state is checked again before anything is spent on the agent.
            "Is_a_queued_AI_task": {
                "runAfter": {"New_conversation_id": ["Succeeded"]},
                "type": "If",
                "expression": {"equals": [
                    "@int(coalesce(triggerOutputs()?['body/spc_agentstate'], 0))", 1]},
                "actions": {

            # Claims the task (Queued -> Running) and hands back one ready-to-send message.
            "Prepare_the_agent_turn": unbound(
                "spc_PrepareAgentTask", {"item/TaskId": task_id}, {}),

            # Prepare reports its own problems rather than throwing, so the usual path is a
            # value check. This handler is only for the custom API itself falling over.
            "Record_preparation_crash": unbound(
                "spc_CompleteAgentTask",
                {"item/TaskId": task_id,
                 "item/Error": "Could not prepare the agent turn. See the flow run for details.",
                 "item/RequestedBy": "Cloud flow: " + FLOW_NAME},
                {"Prepare_the_agent_turn": ["Failed", "TimedOut"]}),

            "Is_the_turn_ready": {
                "runAfter": {"Prepare_the_agent_turn": ["Succeeded"]},
                "type": "If",
                "expression": {"equals": ["@" + prep % "Ok", "true"]},
                "actions": {

                    # How an agent is reachable depends on the harness it was built on.
                    # A GitHub Copilot harness agent (bot.template starts 'cliagent') refuses the
                    # Copilot Studio connector outright - it answers with a flat "this action
                    # doesn't support agents built with the GitHub Copilot harness" AS THE AGENT'S
                    # OWN REPLY, so the connector reports success and the run looks healthy while
                    # nothing happened. The Entra direct-invoke conversations API gives the same
                    # refusal. The only route that works is the agent node connector, which is
                    # what a Copilot Studio workflow's Agent node is underneath. Standard agents
                    # keep the original path untouched.
                    "Which_harness": {
                        "runAfter": {},
                        "type": "If",
                        "expression": {"equals": ["@" + prep % "Harness", "copilot"]},
                        "actions": {
                            # agentId is a plain string here, so the agent stays configuration -
                            # the flow never changes when an agent is added.
                            "Run_the_agent_node": {
                                "runAfter": {},
                                "type": "OpenApiConnection",
                                "inputs": {
                                    "host": {"connectionName": "shared_agentnode",
                                             "operationId": "InvokeAgent", "apiId": AGN_API},
                                    "parameters": {
                                        "body/agentId": "@" + prep % "AgentSchemaName",
                                        "body/prompt": "@" + prep % "Message",
                                    },
                                    "authentication": "@parameters('$authentication')",
                                },
                            },
                            "Write_the_harness_turn_back": unbound(
                                "spc_CompleteAgentTask",
                                dict({"item/TaskId": task_id,
                                      "item/AgentText":
                                          "@string(coalesce(body('Run_the_agent_node')?['result'], ''))"},
                                     **provenance(
                                         "@string(coalesce("
                                         "body('Run_the_agent_node')?['conversationId'], "
                                         "variables('conversationId')))")),
                                {"Run_the_agent_node": ["Succeeded"]}),

                            "Record_harness_failure": unbound(
                                "spc_CompleteAgentTask",
                                dict({"item/TaskId": task_id,
                                      "item/Error": "The Copilot harness agent did not complete. "
                                                    "Check the flow run for the agent node error."},
                                     **provenance()),
                                {"Run_the_agent_node": ["Failed", "TimedOut", "Skipped"]}),
                        },
                        "else": {"actions": {
                            # The long call. This is the whole reason Phase 4 is a flow and not a
                            # plug-in: an agent turn runs well past the Dataverse sandbox ceiling.
                            "Run_the_agent": {
                                "runAfter": {},
                                "type": "OpenApiConnectionWebhook",
                                "inputs": {
                                    "host": {"connectionName": "shared_microsoftcopilotstudio",
                                             "operationId": "ExecuteCopilotAsyncV2",
                                             "apiId": MCS_API},
                                    "parameters": {
                                        "Copilot": "@" + prep % "AgentSchemaName",
                                        "body/message": "@" + prep % "Message",
                                        "x-ms-conversation-id": "@variables('conversationId')",
                                    },
                                    "authentication": "@parameters('$authentication')",
                                },
                            },
                            # Agents interleave typing and trace activities, so the last activity
                            # is not reliably the answer. Keep only messages that carry text.
                            "Keep_only_message_activities": {
                                "runAfter": {"Run_the_agent": ["Succeeded"]},
                                "type": "Query",
                                "inputs": {
                                    "from": "@coalesce(body('Run_the_agent')?['activities'], "
                                            "json('[]'))",
                                    "where": "@and(equals(item()?['type'], 'message'), "
                                             "not(empty(coalesce(item()?['text'], ''))))",
                                },
                            },
                            "Agent_reply": {
                                "runAfter": {"Keep_only_message_activities": ["Succeeded"]},
                                "type": "Compose",
                                "inputs": "@if(empty(body('Keep_only_message_activities')), '', "
                                          "last(body('Keep_only_message_activities'))?['text'])",
                            },
                            "Write_the_turn_back": unbound(
                                "spc_CompleteAgentTask",
                                dict({"item/TaskId": task_id,
                                      "item/AgentText": "@string(outputs('Agent_reply'))"},
                                     **provenance()),
                                {"Agent_reply": ["Succeeded"]}),

                            "Record_agent_failure": unbound(
                                "spc_CompleteAgentTask",
                                dict({"item/TaskId": task_id,
                                      "item/Error": "The agent call did not complete. "
                                                    "Check the flow run for the connector error."},
                                     **provenance()),
                                {"Run_the_agent": ["Failed", "TimedOut", "Skipped"]}),
                        }},
                    },
                },
                "else": {
                    "actions": {
                        "Record_preparation_failure": unbound(
                            "spc_CompleteAgentTask",
                            {"item/TaskId": task_id,
                             "item/Error": "@" + prep % "Error",
                             "item/RequestedBy": "Cloud flow: " + FLOW_NAME},
                            {}),
                    }
                },
            },
                },
                "else": {"actions": {}},
            },
        },
    }

    return json.dumps({
        "properties": {
            "connectionReferences": {
                "shared_commondataserviceforapps": {
                    "runtimeSource": "embedded",
                    "connection": {"connectionReferenceLogicalName": "spc_dataverse"},
                    "api": {"name": "shared_commondataserviceforapps"},
                },
                "shared_microsoftcopilotstudio": {
                    "runtimeSource": "embedded",
                    "connection": {"connectionReferenceLogicalName": "spc_copilotstudio"},
                    "api": {"name": "shared_microsoftcopilotstudio"},
                },
                "shared_agentnode": {
                    "runtimeSource": "embedded",
                    "connection": {"connectionReferenceLogicalName": "spc_agentnode"},
                    "api": {"name": "shared_agentnode"},
                },
            },
            "definition": definition,
            "templateName": "",
        },
        "schemaVersion": "1.0.0.0",
    })


# --------------------------------------------------------------------------- flow

def upsert_flow(data):
    q = ("workflows?$select=workflowid,statecode"
         f"&$filter=category eq 5 and name eq '{FLOW_NAME}'")
    rows = dv.get(q)["value"]
    body = {"name": FLOW_NAME, "clientdata": data}
    if rows:
        wid = rows[0]["workflowid"]
        dv.patch(f"workflows({wid})", body)
        print(f"  ~ flow {wid}")
    else:
        body.update({"category": 5, "type": 1, "primaryentity": "none",
                     "description": "Runs Sales Process Configurator AI agent tasks."})
        dv.post("workflows", body, solution=True)
        wid = dv.get(q)["value"][0]["workflowid"]
        print(f"  + flow {wid}")
    return wid


def activate(wid, on=True):
    dv.patch(f"workflows({wid})", {"statecode": 1 if on else 0, "statuscode": 2 if on else 1})
    print("  activated" if on else "  deactivated")


def main():
    print("Connections")
    token = powerapps_token()
    conns = {api: find_connection(token, api) for _, _, _, api in REFS}
    for api, name in conns.items():
        print(f"  {api} -> {name}")

    print("Connection references")
    for logical, display, connector_id, api in REFS:
        upsert_ref(logical, display, connector_id, conns[api])

    print("Flow")
    # Deactivating first forces the webhook subscription to be torn down and rebuilt, otherwise a
    # stale callbackregistration (wrong filter, wrong attributes) survives the definition update.
    existing = dv.get("workflows?$select=workflowid&$filter=category eq 5 and "
                      f"name eq '{FLOW_NAME}'")["value"]
    if existing:
        try:
            activate(existing[0]["workflowid"], False)
        except RuntimeError as e:
            print("  (already off)", str(e)[:80])

    wid = upsert_flow(clientdata())

    print("Activate")
    try:
        activate(wid, True)
    except RuntimeError as e:
        print("  activation failed:\n ", str(e)[:600])
        raise

    print("done")


if __name__ == "__main__":
    main()
