using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Custom API spc_PrepareAgentTask. The read side of the agent turn.
    ///
    /// The Phase 4 cloud flow is deliberately thin: it cannot be trusted with prompt assembly,
    /// scope resolution or outcome wording, because all of that is configuration a business user
    /// edits in the designer. So the flow asks this API for one ready-to-send message and the
    /// schema name of the agent to send it to, and does nothing else but the HTTP hop.
    ///
    /// Also flips the task to Running so a second trigger firing cannot start a duplicate turn.
    /// </summary>
    public class PrepareAgentTask : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            var taskId = ProcessRuntime.ParseGuid(In(ctx, "TaskId"));
            if (taskId == Guid.Empty)
                throw new InvalidPluginExecutionException("TaskId is required.");

            try
            {
                Prepare(svc, trace, ctx, taskId);
            }
            catch (Exception ex)
            {
                // Never throw at the flow. A hard failure here should leave a readable reason on
                // the task rather than a red run with a stack trace in it.
                trace.Trace("PrepareAgentTask failed: " + ex);
                Fail(svc, taskId);
                ctx.OutputParameters["Ok"] = "false";
                ctx.OutputParameters["Error"] = ex.Message;
                ctx.OutputParameters["AgentSchemaName"] = "";
                ctx.OutputParameters["AgentName"] = "";
                ctx.OutputParameters["Message"] = "";
                ctx.OutputParameters["PromptSent"] = "";
                ctx.OutputParameters["ContextSent"] = "";
                ctx.OutputParameters["TimeoutMins"] = 5;
                ctx.OutputParameters["AgentTemplate"] = "";
                ctx.OutputParameters["Harness"] = "standard";
            }
        }

        private static void Prepare(IOrganizationService svc, ITracingService trace,
                                    IPluginExecutionContext ctx, Guid taskId)
        {
            var task = svc.Retrieve("task", taskId, new ColumnSet(
                "subject", "statecode", "regardingobjectid",
                P + "sourcetask", P + "agentstate", P + "availableoutcomes"));

            if (task.GetAttributeValue<OptionSetValue>("statecode") != null
                && task.GetAttributeValue<OptionSetValue>("statecode").Value != 0)
                throw new InvalidPluginExecutionException("Task is already closed.");

            var sourceTask = task.GetAttributeValue<EntityReference>(P + "sourcetask");
            if (sourceTask == null)
                throw new InvalidPluginExecutionException("Task has no template task behind it.");

            var regarding = task.GetAttributeValue<EntityReference>("regardingobjectid");
            var oppId = regarding != null && regarding.LogicalName == "opportunity"
                ? regarding.Id : Guid.Empty;
            if (oppId == Guid.Empty)
                throw new InvalidPluginExecutionException("Task is not regarding a case.");

            var tt = svc.Retrieve(P + "processtask", sourceTask.Id, new ColumnSet(
                P + "name", P + "agent", P + "agentprompt", P + "contextscope",
                P + "agentoutcomemode", P + "confidencethreshold", P + "autocomplete",
                P + "outputtarget", P + "agenttimeoutmins", P + "assigntype"));

            var agentRef = tt.GetAttributeValue<EntityReference>(P + "agent");
            if (agentRef == null)
                throw new InvalidPluginExecutionException("No agent is configured on this task.");

            var bot = svc.Retrieve("bot", agentRef.Id, new ColumnSet(
                "name", "schemaname", "statecode", "template"));
            var schema = bot.GetAttributeValue<string>("schemaname");
            if (string.IsNullOrWhiteSpace(schema))
                throw new InvalidPluginExecutionException("Agent has no schema name.");

            var scope = OpportunityContext.ParseScope(tt[P + "contextscope"]);
            var context = OpportunityContext.Build(svc, oppId, scope);

            var prompt = tt.GetAttributeValue<string>(P + "agentprompt") ?? "";
            var outcomeMode = Opt(tt, P + "agentoutcomemode", 1);
            var outcomes = ProcessRuntime.LoadOutcomes(svc, tt.Id);

            var message = Compose(task.GetAttributeValue<string>("subject"), prompt, context,
                                  outcomes, outcomeMode);

            // Claim the task before the flow makes the long call.
            svc.Update(new Entity("task", taskId)
            {
                [P + "agentstate"] = new OptionSetValue(ProcessRuntime.AgentStateRunning),
                [P + "agenterror"] = null,
            });

            trace.Trace("Prepared agent '{0}' for task {1}: prompt {2} chars, context {3} chars, {4} outcomes.",
                        schema, taskId, prompt.Length, context.Length, outcomes.Count);

            ctx.OutputParameters["Ok"] = "true";
            ctx.OutputParameters["Error"] = "";
            ctx.OutputParameters["AgentSchemaName"] = schema;
            ctx.OutputParameters["AgentName"] = bot.GetAttributeValue<string>("name") ?? schema;
            ctx.OutputParameters["Message"] = message;
            ctx.OutputParameters["PromptSent"] = prompt;
            ctx.OutputParameters["ContextSent"] = context;
            ctx.OutputParameters["TimeoutMins"] = tt.GetAttributeValue<int?>(P + "agenttimeoutmins") ?? 5;

            // Which harness the agent was built on decides how it can be reached at all.
            // Standard-harness agents answer the Copilot Studio connector; GitHub Copilot
            // harness agents refuse it outright and are only reachable through a Copilot Studio
            // workflow's agent node. The flow needs to know which before it picks a path, and
            // this plugin already holds the bot record, so it reports the fact rather than
            // making the flow look the agent up a second time.
            var template = bot.GetAttributeValue<string>("template") ?? "";
            var isCopilotHarness = template.StartsWith("cliagent", StringComparison.OrdinalIgnoreCase);
            ctx.OutputParameters["AgentTemplate"] = template;
            ctx.OutputParameters["Harness"] = isCopilotHarness ? "copilot" : "standard";
        }

        /// <summary>
        /// One self-contained message. Copilot Studio agents are conversational, so the response
        /// contract has to be stated in the turn itself - there is no system prompt to lean on.
        /// </summary>
        private static string Compose(string subject, string prompt, string context,
                                      List<Entity> outcomes, int outcomeMode)
        {
            var sb = new StringBuilder();
            sb.Append("You are completing a step in a bank's case handling process.\n\n");
            sb.Append("## Step\n").Append(subject ?? "(untitled)").Append("\n\n");

            sb.Append("## Your instructions\n")
              .Append(string.IsNullOrWhiteSpace(prompt) ? "Assess the case and report your findings." : prompt)
              .Append("\n\n");

            sb.Append("## Case context\n").Append(context).Append("\n\n");

            var wantsOutcome = outcomeMode != 3 && outcomes.Count > 0;
            if (wantsOutcome)
            {
                sb.Append("## Allowed outcomes\nChoose exactly one, copying the name verbatim:\n");
                foreach (var o in outcomes)
                {
                    sb.Append("- ").Append(o.GetAttributeValue<string>(P + "name"));
                    var desc = o.GetAttributeValue<string>(P + "description");
                    if (!string.IsNullOrWhiteSpace(desc)) sb.Append(" — ").Append(desc);
                    sb.Append("\n");
                }
                sb.Append("\n");
            }

            sb.Append("## Response format\n");
            sb.Append("Reply with a single JSON object and nothing else — no prose before or after, ");
            sb.Append("no markdown code fence.\n\n");
            sb.Append("{\n");
            if (wantsOutcome)
                sb.Append("  \"outcome\": \"<one of the allowed outcome names, verbatim>\",\n");
            sb.Append("  \"confidence\": <integer 0-100>,\n");
            sb.Append("  \"output\": \"<your findings, written for the case worker who reads this next>\"\n");
            sb.Append("}\n\n");
            sb.Append("Base every statement on the case context above. If the context does not support ");
            sb.Append("a confident answer, say so in \"output\" and lower \"confidence\" accordingly — ");
            sb.Append("do not guess to appear certain.");

            return sb.ToString();
        }

        private static void Fail(IOrganizationService svc, Guid taskId)
        {
            try
            {
                svc.Update(new Entity("task", taskId)
                {
                    [P + "agentstate"] = new OptionSetValue(ProcessRuntime.AgentStateFailed),
                });
            }
            catch { /* the caller already has the real error */ }
        }

        private static int Opt(Entity e, string field, int dflt)
        {
            var o = e == null ? null : e.GetAttributeValue<OptionSetValue>(field);
            return o == null ? dflt : o.Value;
        }

        private static string In(IPluginExecutionContext ctx, string key)
        {
            return ctx.InputParameters.Contains(key) ? ctx.InputParameters[key] as string : null;
        }
    }
}
