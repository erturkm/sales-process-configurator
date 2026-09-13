using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Fires after a process task is completed. Reads the outcome the agent selected, applies the
    /// outcome's side effects (stage advance, case priority, case resolution) and instantiates the
    /// task the outcome points at. This is what makes the process a graph rather than a checklist.
    /// </summary>
    public class AdvanceProcess : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            if (ctx.PrimaryEntityName != "task") return;
            if (ctx.Depth > 3) return;

            var task = svc.Retrieve("task", ctx.PrimaryEntityId, new ColumnSet(
                "subject", "statecode", "regardingobjectid", P + "sourcetask", P + "sourcetemplate",
                P + "selectedoutcome", P + "outcomelabel", P + "branchpath", P + "stagename"));

            // Only interested in completed tasks that came from a process template.
            var state = task.GetAttributeValue<OptionSetValue>("statecode");
            if (state == null || state.Value != 1) return;

            // The countdown stops the moment the task is done, whoever closed it - a human in the
            // form or the agent through spc_CompleteAgentTask. Doing it here rather than in each
            // caller keeps a single place responsible for it.
            ProcessRuntime.CloseSlaTimer(svc, ctx.PrimaryEntityId, DateTime.UtcNow);

            var sourceTask = task.GetAttributeValue<EntityReference>(P + "sourcetask");
            var sourceTemplate = task.GetAttributeValue<EntityReference>(P + "sourcetemplate");
            var regarding = task.GetAttributeValue<EntityReference>("regardingobjectid");
            if (sourceTask == null || sourceTemplate == null || regarding == null
                || regarding.LogicalName != "opportunity") return;

            var log = new StringBuilder();
            log.AppendLine("Task '" + task.GetAttributeValue<string>("subject") + "' completed.");

            var outcomes = ProcessRuntime.LoadOutcomes(svc, sourceTask.Id);
            if (outcomes.Count == 0)
            {
                trace.Trace("No outcomes configured; nothing to advance.");
                return;
            }

            // Work out which outcome applies: explicit lookup, then label match, then the default.
            Entity chosen = null;
            var sel = task.GetAttributeValue<EntityReference>(P + "selectedoutcome");
            if (sel != null) chosen = outcomes.FirstOrDefault(o => o.Id == sel.Id);

            if (chosen == null)
            {
                var lbl = task.GetAttributeValue<string>(P + "outcomelabel");
                if (!string.IsNullOrEmpty(lbl))
                    chosen = outcomes.FirstOrDefault(o => string.Equals(
                        o.GetAttributeValue<string>(P + "name"), lbl, StringComparison.OrdinalIgnoreCase));
            }
            if (chosen == null)
                chosen = outcomes.FirstOrDefault(o => o.GetAttributeValue<bool>(P + "isdefault"));
            if (chosen == null) chosen = outcomes[0];

            var outcomeName = chosen.GetAttributeValue<string>(P + "name");
            log.AppendLine("Outcome recorded: " + outcomeName + ".");

            // Keep the denormalised label in step so the timeline reads well without a join.
            if (sel == null || task.GetAttributeValue<string>(P + "outcomelabel") != outcomeName)
            {
                var stamp = new Entity("task", task.Id);
                stamp[P + "selectedoutcome"] = new EntityReference(P + "taskoutcome", chosen.Id);
                stamp[P + "outcomelabel"] = outcomeName;
                svc.Update(stamp);
            }

            var opp = svc.Retrieve("opportunity", regarding.Id, new ColumnSet(true));
            var template = svc.Retrieve(P + "salesprocesstemplate", sourceTemplate.Id, new ColumnSet(true));

            ApplyOutcomeEffects(svc, opp, template, task, chosen, log);

            // Spawn whatever comes next.
            var next = chosen.GetAttributeValue<EntityReference>(P + "nexttask");
            var spawned = 0;
            if (next != null)
            {
                var def = svc.Retrieve(P + "processtask", next.Id, new ColumnSet(true));
                var openAlready = HasOpenInstance(svc, opp.Id, next.Id);
                if (openAlready)
                {
                    log.AppendLine("Next task '" + def.GetAttributeValue<string>(P + "name")
                                   + "' is already open; not duplicated.");
                }
                else
                {
                    var path = task.GetAttributeValue<string>(P + "branchpath");
                    path = string.IsNullOrEmpty(path)
                        ? outcomeName
                        : path + " > " + outcomeName;
                    ProcessRuntime.Instantiate(svc, opp, def, template.Id, DateTime.UtcNow, path);
                    spawned = 1;
                    log.AppendLine("Started next task: " + def.GetAttributeValue<string>(P + "name") + ".");
                }
            }
            else
            {
                log.AppendLine("This outcome ends its branch - no follow up task configured.");
            }

            AppendJournal(svc, opp.Id, template.Id, log.ToString());
            trace.Trace("AdvanceProcess: outcome={0} spawned={1}", outcomeName, spawned);
        }

        private static void ApplyOutcomeEffects(IOrganizationService svc, Entity opp, Entity template,
                                                Entity task, Entity outcome, StringBuilder log)
        {
            var update = new Entity("opportunity", opp.Id);
            var dirty = false;

            var pr = outcome.GetAttributeValue<OptionSetValue>(P + "setopportunitypriority");
            if (pr != null && pr.Value > 0)
            {
                update["prioritycode"] = new OptionSetValue(pr.Value);
                dirty = true;
                log.AppendLine("Case priority set by the outcome.");
            }

            if (dirty) svc.Update(update);

            var processId = ProcessRuntime.ParseGuid(template.GetAttributeValue<string>(P + "bpfid"));
            var bpfEntity = template.GetAttributeValue<string>(P + "bpfentityname");

            var jump = outcome.GetAttributeValue<string>(P + "targetstagename");
            if (!string.IsNullOrEmpty(jump))
            {
                ProcessRuntime.MoveStage(svc, opp.Id, bpfEntity, processId, jump, log);
            }
            else if (outcome.GetAttributeValue<bool>(P + "advancestage"))
            {
                var current = task.GetAttributeValue<string>(P + "stagename");
                var nextStage = ProcessRuntime.NextStageName(svc, processId, current);
                if (!string.IsNullOrEmpty(nextStage))
                    ProcessRuntime.MoveStage(svc, opp.Id, bpfEntity, processId, nextStage, log);
            }

            var close = outcome.GetAttributeValue<OptionSetValue>(P + "closeopportunity");
            if (close != null && close.Value > 0)
            {
                CancelOpenTasks(svc, opp.Id, task.Id, log);
                CloseOpportunity(svc, opp.Id, close.Value,
                                 outcome.GetAttributeValue<string>(P + "name"), log);
            }
        }

        private static bool HasOpenInstance(IOrganizationService svc, Guid oppId, Guid processTaskId)
        {
            var q = new QueryExpression("task") { ColumnSet = new ColumnSet("activityid"), TopCount = 1 };
            q.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, oppId);
            q.Criteria.AddCondition(P + "sourcetask", ConditionOperator.Equal, processTaskId);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            return svc.RetrieveMultiple(q).Entities.Count > 0;
        }

        private static void CancelOpenTasks(IOrganizationService svc, Guid oppId, Guid exceptTaskId,
                                            StringBuilder log)
        {
            var q = new QueryExpression("task") { ColumnSet = new ColumnSet("activityid", "subject") };
            q.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, oppId);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            q.Criteria.AddCondition(P + "sourcetask", ConditionOperator.NotNull);
            var open = svc.RetrieveMultiple(q).Entities;
            var n = 0;
            foreach (var t in open)
            {
                if (t.Id == exceptTaskId) continue;
                var cancel = new Entity("task", t.Id);
                cancel["statecode"] = new OptionSetValue(2);
                cancel["statuscode"] = new OptionSetValue(6);
                svc.Update(cancel);
                n++;
            }
            if (n > 0) log.AppendLine("Cancelled " + n + " task(s) still open on the abandoned branches.");
        }

        /// <summary>
        /// Closes the deal the way Sales expects. An opportunity does not "resolve" like a case:
        /// it is won or lost, and either way the platform wants an opportunityclose activity
        /// carrying the value and the date, because that activity is what the sales reports read.
        /// </summary>
        /// <param name="how">1 won, 2 lost. Anything else leaves the deal open.</param>
        private static void CloseOpportunity(IOrganizationService svc, Guid oppId, int how,
                                             string reason, StringBuilder log)
        {
            if (how != 1 && how != 2) return;

            var opp = svc.Retrieve("opportunity", oppId,
                                   new ColumnSet("statecode", "estimatedvalue", "actualvalue"));
            var st = opp.GetAttributeValue<OptionSetValue>("statecode");
            if (st != null && st.Value != 0)
            {
                log.AppendLine("Opportunity is not open; close skipped.");
                return;
            }

            // Win takes the actual value if someone set one, otherwise the estimate stands as the
            // booked number. A loss books zero regardless of what was forecast.
            var value = how == 1
                ? (opp.GetAttributeValue<Money>("actualvalue")
                   ?? opp.GetAttributeValue<Money>("estimatedvalue")
                   ?? new Money(0m))
                : new Money(0m);

            var closure = new Entity("opportunityclose")
            {
                ["subject"] = (how == 1 ? "Won by process outcome: " : "Lost by process outcome: ")
                              + (reason ?? ""),
                ["opportunityid"] = new EntityReference("opportunity", oppId),
                ["actualend"] = DateTime.UtcNow,
                ["actualrevenue"] = value,
            };

            if (how == 1)
            {
                svc.Execute(new Microsoft.Crm.Sdk.Messages.WinOpportunityRequest
                {
                    OpportunityClose = closure,
                    Status = new OptionSetValue(3),      // Won
                });
                log.AppendLine("Opportunity closed as won by the outcome.");
            }
            else
            {
                svc.Execute(new Microsoft.Crm.Sdk.Messages.LoseOpportunityRequest
                {
                    OpportunityClose = closure,
                    Status = new OptionSetValue(4),      // Canceled / lost
                });
                log.AppendLine("Opportunity closed as lost by the outcome.");
            }
        }

        /// <summary>Appends the branch decision to the applied process record so the path is auditable.</summary>
        private static void AppendJournal(IOrganizationService svc, Guid oppId, Guid templateId, string entry)
        {
            var q = new QueryExpression(P + "appliedprocess")
            {
                ColumnSet = new ColumnSet(P + "appliedprocessid", P + "evaluationlog"),
                TopCount = 1,
                Orders = { new OrderExpression("createdon", OrderType.Descending) }
            };
            q.Criteria.AddCondition(P + "opportunity", ConditionOperator.Equal, oppId);
            var found = svc.RetrieveMultiple(q).Entities;
            if (found.Count == 0) return;

            var existing = found[0].GetAttributeValue<string>(P + "evaluationlog") ?? "";
            var stamped = existing + Environment.NewLine
                          + "[" + DateTime.UtcNow.ToString("u") + "] " + entry;

            var upd = new Entity(P + "appliedprocess", found[0].Id);
            upd[P + "evaluationlog"] = ProcessRuntime.Truncate(stamped, 100000);
            svc.Update(upd);
        }
    }
}
