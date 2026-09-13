using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Custom API spc_CompleteAgentTask. The single write-back point for an AI agent turn.
    ///
    /// It records the run, stores the output, and then decides one of two things:
    ///   - auto-complete is on and confidence clears the threshold -> close the task with the
    ///     chosen outcome, which lets the existing AdvanceProcess plug-in move the graph on
    ///     exactly as if a human had clicked it;
    ///   - otherwise -> park the task in "Awaiting review" with the draft attached.
    ///
    /// The graph logic is deliberately not duplicated here. An agent is just another actor
    /// that picks an outcome.
    /// </summary>
    public class CompleteAgentTask : IPlugin
    {
        private const string P = "spc_";

        // spc_runstatus on spc_agentrun
        private const int RunSuccess = 1;
        private const int RunFailed = 2;
        private const int RunTimedOut = 3;
        private const int RunRejected = 4;
        private const int RunInvalid = 5;

        // spc_outputtarget on spc_processtask
        private const int TargetFieldOnly = 1;
        private const int TargetTaskDescription = 2;
        private const int TargetCaseNote = 3;
        private const int TargetCaseDescription = 4;

        // spc_agentoutcomemode on spc_processtask
        private const int OutcomeHumanSelects = 1;
        private const int OutcomeAgentSelects = 2;
        private const int OutcomeNone = 3;

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            var taskId = ProcessRuntime.ParseGuid(In(ctx, "TaskId"));
            if (taskId == Guid.Empty)
                throw new InvalidPluginExecutionException("TaskId is required.");

            var output = In(ctx, "Output") ?? "";
            var outcomeName = In(ctx, "OutcomeName");
            var rawResponse = In(ctx, "RawResponse");
            var errorText = In(ctx, "Error");
            var conversationId = In(ctx, "ConversationId");
            var promptSent = In(ctx, "PromptSent");
            var contextSent = In(ctx, "ContextSent");
            var confidence = Dec(ctx, "Confidence");
            var latency = Int(ctx, "LatencyMs");

            // The cloud flow forwards the agent's reply verbatim rather than trying to pull the
            // JSON apart in workflow expressions, which are unreadable and fail silently. Explicit
            // parameters still win, so step44's simulated runs behave exactly as before.
            var agentText = In(ctx, "AgentText");
            if (!string.IsNullOrWhiteSpace(agentText))
            {
                string pOutcome, pOutput;
                decimal? pConf;
                ParseAgentText(agentText, out pOutcome, out pConf, out pOutput);
                if (string.IsNullOrWhiteSpace(outcomeName)) outcomeName = pOutcome;
                if (!confidence.HasValue) confidence = pConf;
                if (string.IsNullOrWhiteSpace(output)) output = pOutput ?? agentText;
                if (string.IsNullOrWhiteSpace(rawResponse)) rawResponse = agentText;
            }

            var task = svc.Retrieve("task", taskId, new ColumnSet(
                "subject", "statecode", "description", "regardingobjectid",
                P + "sourcetask", P + "agentattempts", P + "agentstate"));

            var sourceTask = task.GetAttributeValue<EntityReference>(P + "sourcetask");
            var regarding = task.GetAttributeValue<EntityReference>("regardingobjectid");
            Guid oppId = regarding != null && regarding.LogicalName == "opportunity"
                ? regarding.Id : Guid.Empty;

            Entity tt = null;
            if (sourceTask != null)
            {
                tt = svc.Retrieve(P + "processtask", sourceTask.Id, new ColumnSet(
                    P + "name", P + "agent", P + "autocomplete", P + "confidencethreshold",
                    P + "outputtarget", P + "agentoutcomemode"));
            }

            var attempt = (task.GetAttributeValue<int?>(P + "agentattempts") ?? 0) + 1;
            var failed = !string.IsNullOrWhiteSpace(errorText);

            // ---------------------------------------------------------- decide
            var autoComplete = tt != null && tt.GetAttributeValue<bool>(P + "autocomplete");
            var threshold = tt == null ? 0 : (tt.GetAttributeValue<int?>(P + "confidencethreshold") ?? 0);
            var outcomeMode = OptionOr(tt, P + "agentoutcomemode", OutcomeHumanSelects);

            Entity chosenOutcome = null;
            if (!failed && sourceTask != null && outcomeMode != OutcomeNone
                && !string.IsNullOrWhiteSpace(outcomeName))
            {
                chosenOutcome = MatchOutcome(svc, sourceTask.Id, outcomeName);
                if (chosenOutcome == null) trace.Trace("Outcome '" + outcomeName + "' did not match.");
            }

            var confident = !confidence.HasValue || confidence.Value >= threshold;
            var canComplete = !failed
                              && autoComplete
                              && confident
                              && outcomeMode == OutcomeAgentSelects
                              && chosenOutcome != null;

            var runStatus = RunSuccess;
            if (failed) runStatus = RunFailed;
            else if (outcomeMode != OutcomeNone && !string.IsNullOrWhiteSpace(outcomeName)
                     && chosenOutcome == null) runStatus = RunInvalid;
            else if (autoComplete && !confident) runStatus = RunRejected;

            // ---------------------------------------------------------- write task state
            var upd = new Entity("task", taskId);
            upd[P + "agentattempts"] = attempt;
            upd[P + "agentrunon"] = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(output)) upd[P + "agentoutput"] = Cap(output, 100000);
            if (confidence.HasValue) upd[P + "agentconfidence"] = confidence.Value;
            upd[P + "agenterror"] = failed ? ProcessRuntime.Truncate(errorText, 400) : null;
            upd[P + "agentstate"] = new OptionSetValue(
                failed ? ProcessRuntime.AgentStateFailed
                       : canComplete ? ProcessRuntime.AgentStateSucceeded
                                     : ProcessRuntime.AgentStateReview);

            // The output target is what a business user actually sees on the task.
            var target = OptionOr(tt, P + "outputtarget", TargetFieldOnly);
            if (!failed && !string.IsNullOrWhiteSpace(output) && target == TargetTaskDescription)
            {
                var existing = task.GetAttributeValue<string>("description") ?? "";
                var stamp = "--- AI agent "
                            + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                            + " UTC ---";
                upd["description"] = CapToAttribute(svc, "task", "description",
                    (string.IsNullOrWhiteSpace(existing) ? "" : existing + "\n\n")
                    + stamp + "\n" + output);
            }

            if (canComplete && chosenOutcome != null)
            {
                upd[P + "selectedoutcome"] = new EntityReference(P + "taskoutcome", chosenOutcome.Id);
                upd[P + "outcomelabel"] = chosenOutcome.GetAttributeValue<string>(P + "name");
                upd[P + "outcomecomment"] = ProcessRuntime.Truncate(
                    "Completed by AI agent" + (confidence.HasValue
                        ? " (confidence " + confidence.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%)"
                        : ""), 400);
                // statecode 1 / statuscode 5 = Completed. This is what AdvanceProcess listens for,
                // so the graph moves on through the existing path rather than a parallel one.
                upd["statecode"] = new OptionSetValue(1);
                upd["statuscode"] = new OptionSetValue(5);
            }
            svc.Update(upd);

            // ---------------------------------------------------------- side outputs
            if (!failed && !string.IsNullOrWhiteSpace(output) && oppId != Guid.Empty)
            {
                if (target == TargetCaseNote) CreateNote(svc, oppId, task, output);
                else if (target == TargetCaseDescription) AppendCaseDescription(svc, oppId, output);
            }

            // ---------------------------------------------------------- audit
            var runId = LogRun(svc, task, tt, oppId, taskId, attempt, runStatus, promptSent,
                               contextSent, rawResponse, outcomeName, confidence, latency,
                               errorText, conversationId, canComplete, In(ctx, "RequestedBy"));

            ctx.OutputParameters["AgentRunId"] = runId.ToString();
            ctx.OutputParameters["Completed"] = canComplete ? "true" : "false";
            ctx.OutputParameters["State"] = failed ? "Failed" : canComplete ? "Succeeded" : "Awaiting review";
            ctx.OutputParameters["Reason"] = Reason(failed, autoComplete, confident, outcomeMode,
                                                    chosenOutcome, outcomeName, threshold);
        }

        // ------------------------------------------------------------------ helpers

        private static string Reason(bool failed, bool autoComplete, bool confident, int outcomeMode,
                                     Entity chosen, string outcomeName, int threshold)
        {
            if (failed) return "Agent call failed; task held for the fallback team.";
            if (outcomeMode == OutcomeNone) return "Output recorded; this task takes no outcome.";
            if (!autoComplete) return "Auto-complete is off; a human confirms the outcome.";
            if (!confident) return "Confidence below the " + threshold + "% threshold; held for review.";
            if (outcomeMode == OutcomeHumanSelects) return "Agent proposed an outcome; a human selects it.";
            if (chosen == null)
                return string.IsNullOrWhiteSpace(outcomeName)
                    ? "Agent returned no outcome; held for review."
                    : "Agent returned an unknown outcome '" + outcomeName + "'; held for review.";
            return "Completed automatically by the agent.";
        }

        /// <summary>Matches on the outcome name, case and whitespace insensitive, then falls
        /// back to a contains match so a chatty model is not punished for wording.</summary>
        private static Entity MatchOutcome(IOrganizationService svc, Guid sourceTaskId, string name)
        {
            var outcomes = ProcessRuntime.LoadOutcomes(svc, sourceTaskId);
            if (outcomes.Count == 0) return null;
            var want = Norm(name);
            var exact = outcomes.FirstOrDefault(
                o => Norm(o.GetAttributeValue<string>(P + "name")) == want);
            if (exact != null) return exact;
            return outcomes.FirstOrDefault(o =>
            {
                var n = Norm(o.GetAttributeValue<string>(P + "name"));
                return n.Length > 0 && (want.Contains(n) || n.Contains(want));
            });
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        private static void CreateNote(IOrganizationService svc, Guid oppId, Entity task, string output)
        {
            var note = new Entity("annotation");
            note["subject"] = ProcessRuntime.Truncate(
                "AI agent: " + task.GetAttributeValue<string>("subject"), 200);
            note["notetext"] = Cap(output, 100000);
            note["objectid"] = new EntityReference("opportunity", oppId);
            note["objecttypecode"] = "opportunity";
            svc.Create(note);
        }

        private static void AppendCaseDescription(IOrganizationService svc, Guid oppId, string output)
        {
            var c = svc.Retrieve("opportunity", oppId, new ColumnSet("description"));
            var existing = c.GetAttributeValue<string>("description") ?? "";
            var upd = new Entity("opportunity", oppId);
            upd["description"] = CapToAttribute(svc, "opportunity", "description",
                (string.IsNullOrWhiteSpace(existing) ? "" : existing + "\n\n")
                + "--- AI agent "
                + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                + " UTC ---\n" + output);
            svc.Update(upd);
        }

        private static Guid LogRun(IOrganizationService svc, Entity task, Entity tt, Guid oppId,
                                   Guid taskId, int attempt, int runStatus, string prompt,
                                   string context, string raw, string outcomeName, decimal? confidence,
                                   int? latency, string error, string conversationId,
                                   bool autoCompleted, string requestedBy)
        {
            var run = new Entity(P + "agentrun");
            run[P + "name"] = ProcessRuntime.Truncate(
                (task.GetAttributeValue<string>("subject") ?? "Agent run") + " - attempt " + attempt, 300);
            run[P + "task"] = new EntityReference("task", taskId);
            if (oppId != Guid.Empty) run[P + "opportunity"] = new EntityReference("opportunity", oppId);
            if (tt != null)
            {
                run[P + "sourcetask"] = new EntityReference(P + "processtask", tt.Id);
                var agent = tt.GetAttributeValue<EntityReference>(P + "agent");
                if (agent != null)
                {
                    run[P + "agent"] = new EntityReference("bot", agent.Id);
                    try
                    {
                        var bot = svc.Retrieve("bot", agent.Id, new ColumnSet("schemaname"));
                        run[P + "agentschemaname"] = bot.GetAttributeValue<string>("schemaname");
                    }
                    catch (Exception) { /* the name is a convenience, not a requirement */ }
                }
            }
            run[P + "attempt"] = attempt;
            run[P + "runstatus"] = new OptionSetValue(runStatus);
            run[P + "promptsent"] = Cap(prompt, 100000);
            run[P + "contextsent"] = Cap(context, 100000);
            run[P + "rawresponse"] = Cap(raw, 100000);
            run[P + "parsedoutcome"] = ProcessRuntime.Truncate(outcomeName, 200);
            if (confidence.HasValue) run[P + "confidence"] = confidence.Value;
            if (latency.HasValue) run[P + "latencyms"] = latency.Value;
            run[P + "contextchars"] = string.IsNullOrEmpty(context) ? 0 : context.Length;
            run[P + "errordetail"] = Cap(error, 4000);
            run[P + "conversationid"] = ProcessRuntime.Truncate(conversationId, 200);
            run[P + "autocompleted"] = autoCompleted;
            // The flow authenticates as a shared service account, so attribution to the real
            // business user has to be carried explicitly or it is lost.
            run[P + "requestedbyname"] = ProcessRuntime.Truncate(requestedBy, 200);
            return svc.Create(run);
        }

        private static string In(IPluginExecutionContext ctx, string key)
        {
            return ctx.InputParameters.Contains(key) ? ctx.InputParameters[key] as string : null;
        }

        /// <summary>
        /// Pulls outcome/confidence/output out of an agent reply. Agents wrap JSON in prose or a
        /// code fence often enough that scanning for the outermost braces is more reliable than
        /// trusting the instruction, and a reply with no JSON at all still has to survive as text.
        /// </summary>
        internal static void ParseAgentText(string text, out string outcome,
                                            out decimal? confidence, out string output)
        {
            outcome = null;
            confidence = null;
            output = null;
            if (string.IsNullOrWhiteSpace(text)) return;

            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start) return;

            Dictionary<string, object> d;
            try { d = Json.Obj(Json.Parse(text.Substring(start, end - start + 1))); }
            catch { return; }
            if (d == null) return;

            outcome = First(d, "outcome", "Outcome", "outcomeName", "OutcomeName");
            output = First(d, "output", "Output", "findings", "summary", "text");

            var c = First(d, "confidence", "Confidence", "confidenceScore");
            if (!string.IsNullOrWhiteSpace(c))
            {
                decimal v;
                if (decimal.TryParse(c, NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                {
                    if (v > 0 && v <= 1) v = v * 100m;
                    confidence = Math.Max(0m, Math.Min(100m, v));
                }
            }
        }

        private static string First(Dictionary<string, object> d, params string[] keys)
        {
            foreach (var k in keys)
            {
                if (!d.ContainsKey(k)) continue;
                var v = d[k];
                if (v == null) continue;
                var s = v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return null;
        }

        private static decimal? Dec(IPluginExecutionContext ctx, string key)
        {
            var s = In(ctx, key);
            if (string.IsNullOrWhiteSpace(s)) return null;
            decimal d;
            if (!decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d)) return null;
            // Accept either 0-1 or 0-100 so a model returning 0.82 is not read as 0.82%.
            if (d > 0 && d <= 1) d = d * 100m;
            if (d < 0) d = 0;
            if (d > 100) d = 100;
            return d;
        }

        private static int? Int(IPluginExecutionContext ctx, string key)
        {
            var s = In(ctx, key);
            int v;
            return int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v) ? v : (int?)null;
        }

        private static int OptionOr(Entity e, string attr, int fallback)
        {
            if (e == null) return fallback;
            var o = e.GetAttributeValue<OptionSetValue>(attr);
            return o == null ? fallback : o.Value;
        }

        private static string Cap(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static readonly Dictionary<string, int> MaxLenCache = new Dictionary<string, int>();

        /// <summary>
        /// Cap text to whatever the target column actually allows.
        /// A hardcoded limit is a trap here: task.description is 2,000 characters, and an agent
        /// asked to draft a credit application will comfortably exceed that. The platform then
        /// rejects the whole update with 0x80044331, which fails the flow AFTER the agent has
        /// already done the expensive work, so the run is lost rather than just trimmed.
        /// When trimming is needed the TAIL is kept, because the newest agent output is the part
        /// a business user is looking for.
        /// </summary>
        private static string CapToAttribute(IOrganizationService svc, string entity,
                                             string attribute, string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var key = entity + "." + attribute;
            int max;
            if (!MaxLenCache.TryGetValue(key, out max))
            {
                max = 2000;
                try
                {
                    var res = (RetrieveAttributeResponse)svc.Execute(new RetrieveAttributeRequest
                    {
                        EntityLogicalName = entity,
                        LogicalName = attribute,
                        RetrieveAsIfPublished = true,
                    });
                    var sm = res.AttributeMetadata as StringAttributeMetadata;
                    if (sm != null && sm.MaxLength.HasValue) max = sm.MaxLength.Value;
                    else
                    {
                        var mm = res.AttributeMetadata as MemoAttributeMetadata;
                        if (mm != null && mm.MaxLength.HasValue) max = mm.MaxLength.Value;
                    }
                }
                catch (Exception)
                {
                    // Metadata read failed; 2,000 is the safe floor for both targets we write to.
                }
                MaxLenCache[key] = max;
            }

            if (text.Length <= max) return text;
            const string Marker = "[earlier content trimmed to fit the field]\n\n";
            var keep = max - Marker.Length;
            if (keep < 1) return text.Substring(text.Length - max);
            return Marker + text.Substring(text.Length - keep);
        }
    }
}
