using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Keeps the case level progress counters in step when a process task is completed
    /// or a required document is marked as received.
    /// </summary>
    public class UpdateOpportunityRollups : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));
            if (ctx.Depth > 3) return;

            {
                Guid oppId = Guid.Empty;

                if (ctx.PrimaryEntityName == "task")
                {
                    var task = svc.Retrieve("task", ctx.PrimaryEntityId,
                        new ColumnSet("regardingobjectid", "statecode", P + "sladue", P + "slastatus"));
                    var regarding = task.GetAttributeValue<EntityReference>("regardingobjectid");
                    if (regarding == null || regarding.LogicalName != "opportunity") return;
                    oppId = regarding.Id;

                    var state = task.GetAttributeValue<OptionSetValue>("statecode");
                    if (state != null && state.Value == 1)
                    {
                        var due = task.GetAttributeValue<DateTime?>(P + "sladue");
                        var status = due.HasValue && DateTime.UtcNow > due.Value ? 4 : 3;
                        svc.Update(new Entity("task", task.Id) { [P + "slastatus"] = new OptionSetValue(status) });
                    }
                }
                else if (ctx.PrimaryEntityName == P + "opportunityrequireddocument")
                {
                    var doc = svc.Retrieve(P + "opportunityrequireddocument", ctx.PrimaryEntityId,
                        new ColumnSet(P + "opportunity", P + "received", P + "receivedon"));
                    var c = doc.GetAttributeValue<EntityReference>(P + "opportunity");
                    if (c == null) return;
                    oppId = c.Id;

                    if (doc.GetAttributeValue<bool>(P + "received") &&
                        doc.GetAttributeValue<DateTime?>(P + "receivedon") == null)
                    {
                        svc.Update(new Entity(P + "opportunityrequireddocument", doc.Id)
                        { [P + "receivedon"] = DateTime.UtcNow });
                    }
                }

                if (oppId == Guid.Empty) return;
                Rollup(svc, oppId);
                trace.Trace("Rollup complete for case " + oppId);
            }
        }

        private static void Rollup(IOrganizationService svc, Guid oppId)
        {
            var tq = new QueryExpression("task") { ColumnSet = new ColumnSet("statecode") };
            tq.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, oppId);
            tq.Criteria.AddCondition(P + "sourcetask", ConditionOperator.NotNull);
            var tasks = svc.RetrieveMultiple(tq).Entities;

            var dq = new QueryExpression(P + "opportunityrequireddocument") { ColumnSet = new ColumnSet(P + "received") };
            dq.Criteria.AddCondition(P + "opportunity", ConditionOperator.Equal, oppId);
            dq.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var docs = svc.RetrieveMultiple(dq).Entities;

            var open = tasks.Count(t =>
            {
                var s = t.GetAttributeValue<OptionSetValue>("statecode");
                return s == null || s.Value == 0;
            });
            var received = docs.Count(d => d.GetAttributeValue<bool>(P + "received"));

            svc.Update(new Entity("opportunity", oppId)
            {
                [P + "taskstotal"] = tasks.Count,
                [P + "tasksopen"] = open,
                [P + "docstotal"] = docs.Count,
                [P + "docsreceived"] = received,
            });
        }
    }
}
