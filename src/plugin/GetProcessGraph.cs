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
    /// Custom API spc_GetProcessGraph. Returns everything the visual designer needs for one template
    /// in a single call: header settings, stage swimlanes from the mapped business process flow, task
    /// nodes with their layout coordinates and ownership, and the outcome edges between them.
    /// </summary>
    public class GetProcessGraph : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);

            var idRaw = ctx.InputParameters.Contains("TemplateId") ? (string)ctx.InputParameters["TemplateId"] : null;
            var templateId = ProcessRuntime.ParseGuid(idRaw);
            if (templateId == Guid.Empty)
                throw new InvalidPluginExecutionException("TemplateId is required.");

            var t = svc.Retrieve(P + "salesprocesstemplate", templateId, new ColumnSet(true));
            var sb = new StringBuilder();
            sb.Append("{").Append(Json.Q("template")).Append(":{");
            sb.Append(Json.Q("id")).Append(":").Append(Json.Q(templateId.ToString()));
            Field(sb, "name", t.GetAttributeValue<string>(P + "name"));
            Field(sb, "description", t.GetAttributeValue<string>(P + "description"));
            NumField(sb, "rank", t.GetAttributeValue<int?>(P + "rank"));
            var status = t.GetAttributeValue<OptionSetValue>(P + "publishstatus");
            NumField(sb, "publishStatus", status == null ? 1 : status.Value);
            var prio = t.GetAttributeValue<OptionSetValue>(P + "setopportunitypriority");
            NumField(sb, "setOpportunityPriority", prio == null ? 0 : prio.Value);
            DecField(sb, "qualificationHours", t.GetAttributeValue<decimal?>(P + "qualificationhours"));
            DecField(sb, "closeHours", t.GetAttributeValue<decimal?>(P + "closehours"));
            Field(sb, "bpfId", t.GetAttributeValue<string>(P + "bpfid"));
            Field(sb, "bpfName", t.GetAttributeValue<string>(P + "bpfname"));
            Field(sb, "bpfEntityName", t.GetAttributeValue<string>(P + "bpfentityname"));
            Field(sb, "startStageName", t.GetAttributeValue<string>(P + "startstagename"));
            RefField(sb, "sla", t.GetAttributeValue<EntityReference>(P + "sla"));
            RefField(sb, "documentPackage", t.GetAttributeValue<EntityReference>(P + "documentpackage"));
            sb.Append("}");

            // ---------------- stages ----------------
            sb.Append(",").Append(Json.Q("stages")).Append(":[");
            var processId = ProcessRuntime.ParseGuid(t.GetAttributeValue<string>(P + "bpfid"));
            if (processId != Guid.Empty)
            {
                var first = true;
                foreach (var s in ProcessRuntime.OrderedStages(svc, processId))
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(s.Id.ToString()))
                      .Append(",").Append(Json.Q("name")).Append(":")
                      .Append(Json.Q(s.GetAttributeValue<string>("stagename"))).Append("}");
                }
            }
            sb.Append("]");

            // ---------------- nodes ----------------
            var tasks = ProcessRuntime.LoadTemplateTasks(svc, templateId);
            sb.Append(",").Append(Json.Q("nodes")).Append(":[");
            var firstN = true;
            foreach (var n in tasks)
            {
                if (!firstN) sb.Append(",");
                firstN = false;
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(n.Id.ToString()));
                Field(sb, "name", n.GetAttributeValue<string>(P + "name"));
                Field(sb, "instructions", n.GetAttributeValue<string>(P + "description"));
                Field(sb, "stageName", n.GetAttributeValue<string>(P + "stagename"));
                NumField(sb, "sequence", n.GetAttributeValue<int?>(P + "sequence"));
                var at = n.GetAttributeValue<OptionSetValue>(P + "assigntype");
                NumField(sb, "assignType", at == null ? 1 : at.Value);
                RefField(sb, "team", n.GetAttributeValue<EntityReference>(P + "team"));
                RefField(sb, "user", n.GetAttributeValue<EntityReference>(P + "user"));
                RefField(sb, "queue", n.GetAttributeValue<EntityReference>(P + "queue"));
                RefField(sb, "fallbackTeam", n.GetAttributeValue<EntityReference>(P + "fallbackteam"));
                Field(sb, "roleName", n.GetAttributeValue<string>(P + "rolename"));
                DecField(sb, "dueHours", n.GetAttributeValue<decimal?>(P + "duehours"));
                DecField(sb, "slaTargetHours", n.GetAttributeValue<decimal?>(P + "slatargethours"));
                NumField(sb, "slaWarnPercent", n.GetAttributeValue<int?>(P + "slawarnpercent"));
                var sw = n.GetAttributeValue<OptionSetValue>(P + "slastartwhen");
                NumField(sb, "slaStartWhen", sw == null ? 2 : sw.Value);
                var ob = n.GetAttributeValue<OptionSetValue>(P + "onbreach");
                NumField(sb, "onBreach", ob == null ? 2 : ob.Value);
                BoolField(sb, "blocksStage", n.GetAttributeValue<bool>(P + "blocksstage"));
                BoolField(sb, "mandatory", n.GetAttributeValue<bool>(P + "mandatory"));
                BoolField(sb, "isStart", n.GetAttributeValue<bool>(P + "isstart"));
                NumField(sb, "x", n.GetAttributeValue<int?>(P + "posx"));
                NumField(sb, "y", n.GetAttributeValue<int?>(P + "posy"));
                Field(sb, "color", n.GetAttributeValue<string>(P + "nodecolor"));
                // AI agent configuration
                RefField(sb, "agent", n.GetAttributeValue<EntityReference>(P + "agent"));
                Field(sb, "agentPrompt", n.GetAttributeValue<string>(P + "agentprompt"));
                Field(sb, "contextScope", MultiCsv(n.GetAttributeValue<OptionSetValueCollection>(P + "contextscope")));
                var ot = n.GetAttributeValue<OptionSetValue>(P + "outputtarget");
                NumField(sb, "outputTarget", ot == null ? 2 : ot.Value);
                var om = n.GetAttributeValue<OptionSetValue>(P + "agentoutcomemode");
                NumField(sb, "agentOutcomeMode", om == null ? 1 : om.Value);
                BoolField(sb, "autoComplete", n.GetAttributeValue<bool>(P + "autocomplete"));
                NumField(sb, "confidenceThreshold", n.GetAttributeValue<int?>(P + "confidencethreshold"));
                NumField(sb, "agentTimeoutMins", n.GetAttributeValue<int?>(P + "agenttimeoutmins"));
                sb.Append("}");
            }
            sb.Append("]");

            // ---------------- edges ----------------
            var outcomes = ProcessRuntime.LoadTemplateOutcomes(svc, templateId);
            sb.Append(",").Append(Json.Q("outcomes")).Append(":[");
            var firstO = true;
            foreach (var o in outcomes.OrderBy(x => x.GetAttributeValue<int?>(P + "sequence") ?? 0))
            {
                if (!firstO) sb.Append(",");
                firstO = false;
                var from = o.GetAttributeValue<EntityReference>(P + "task");
                var to = o.GetAttributeValue<EntityReference>(P + "nexttask");
                var sent = o.GetAttributeValue<OptionSetValue>(P + "sentiment");
                var pr = o.GetAttributeValue<OptionSetValue>(P + "setopportunitypriority");
                var close = o.GetAttributeValue<OptionSetValue>(P + "closeopportunity");
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(o.Id.ToString()));
                Field(sb, "label", o.GetAttributeValue<string>(P + "name"));
                Field(sb, "guidance", o.GetAttributeValue<string>(P + "description"));
                Field(sb, "from", from == null ? "" : from.Id.ToString());
                Field(sb, "to", to == null ? "" : to.Id.ToString());
                NumField(sb, "sentiment", sent == null ? 2 : sent.Value);
                NumField(sb, "sequence", o.GetAttributeValue<int?>(P + "sequence"));
                BoolField(sb, "advanceStage", o.GetAttributeValue<bool>(P + "advancestage"));
                Field(sb, "targetStageName", o.GetAttributeValue<string>(P + "targetstagename"));
                NumField(sb, "closeOpportunity", close == null ? 0 : close.Value);
                BoolField(sb, "requireComment", o.GetAttributeValue<bool>(P + "requirecomment"));
                BoolField(sb, "isDefault", o.GetAttributeValue<bool>(P + "isdefault"));
                NumField(sb, "setOpportunityPriority", pr == null ? 0 : pr.Value);
                sb.Append("}");
            }
            sb.Append("]");

            AppendMatchRules(sb, svc, templateId);
            AppendPackage(sb, svc, t.GetAttributeValue<EntityReference>(P + "documentpackage"));

            sb.Append("}");

            ctx.OutputParameters["Graph"] = sb.ToString();
        }

        /// <summary>
        /// The targeting filter. Rules sharing a group number are OR'ed; groups are AND'ed, which is
        /// what the designer renders as "any of these" cards stacked inside "all of these".
        /// </summary>
        private static void AppendMatchRules(StringBuilder sb, IOrganizationService svc, Guid templateId)
        {
            var q = new QueryExpression(P + "matchrule")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "groupnumber", OrderType.Ascending),
                           new OrderExpression(P + "sequence", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "template", ConditionOperator.Equal, templateId);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            sb.Append(",").Append(Json.Q("matchRules")).Append(":[");
            var first = true;
            foreach (var r in svc.RetrieveMultiple(q).Entities)
            {
                if (!first) sb.Append(",");
                first = false;
                var op = r.GetAttributeValue<OptionSetValue>(P + "operator");
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(r.Id.ToString()));
                Field(sb, "attribute", r.GetAttributeValue<string>(P + "attributename"));
                Field(sb, "attributeLabel", r.GetAttributeValue<string>(P + "attributelabel"));
                NumField(sb, "operator", op == null ? 1 : op.Value);
                Field(sb, "value", r.GetAttributeValue<string>(P + "value"));
                Field(sb, "valueLabel", r.GetAttributeValue<string>(P + "valuelabel"));
                NumField(sb, "group", r.GetAttributeValue<int?>(P + "groupnumber") ?? 1);
                NumField(sb, "sequence", r.GetAttributeValue<int?>(P + "sequence"));
                sb.Append("}");
            }
            sb.Append("]");
        }

        /// <summary>
        /// The linked document package with its items inlined, so the designer can edit the checklist
        /// without a second round trip or a second form.
        /// </summary>
        private static void AppendPackage(StringBuilder sb, IOrganizationService svc, EntityReference pkgRef)
        {
            sb.Append(",").Append(Json.Q("package")).Append(":");
            if (pkgRef == null) { sb.Append("null"); return; }

            Entity pkg;
            try { pkg = svc.Retrieve(P + "documentpackage", pkgRef.Id, new ColumnSet(true)); }
            catch (Exception) { sb.Append("null"); return; }

            sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(pkg.Id.ToString()));
            Field(sb, "name", pkg.GetAttributeValue<string>(P + "name"));
            Field(sb, "description", pkg.GetAttributeValue<string>(P + "description"));
            BoolField(sb, "active", pkg.GetAttributeValue<bool>(P + "active"));

            var q = new QueryExpression(P + "documentitem")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "sequence", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "package", ConditionOperator.Equal, pkg.Id);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            sb.Append(",").Append(Json.Q("items")).Append(":[");
            var first = true;
            foreach (var i in svc.RetrieveMultiple(q).Entities)
            {
                if (!first) sb.Append(",");
                first = false;
                var resp = i.GetAttributeValue<OptionSetValue>(P + "responsible");
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(i.Id.ToString()));
                Field(sb, "name", i.GetAttributeValue<string>(P + "name"));
                Field(sb, "description", i.GetAttributeValue<string>(P + "description"));
                NumField(sb, "sequence", i.GetAttributeValue<int?>(P + "sequence"));
                BoolField(sb, "mandatory", i.GetAttributeValue<bool>(P + "mandatory"));
                NumField(sb, "responsible", resp == null ? 1 : resp.Value);
                DecField(sb, "dueHours", i.GetAttributeValue<decimal?>(P + "duehours"));
                Field(sb, "templateUrl", i.GetAttributeValue<string>(P + "templateurl"));
                sb.Append("}");
            }
            sb.Append("]}");
        }

        internal static void Field(StringBuilder sb, string k, string v)
        {
            sb.Append(",").Append(Json.Q(k)).Append(":").Append(Json.Q(v ?? ""));
        }

        internal static void NumField(StringBuilder sb, string k, int? v)
        {
            sb.Append(",").Append(Json.Q(k)).Append(":").Append(v.HasValue
                ? v.Value.ToString(CultureInfo.InvariantCulture) : "null");
        }

        internal static void DecField(StringBuilder sb, string k, decimal? v)
        {
            sb.Append(",").Append(Json.Q(k)).Append(":").Append(v.HasValue
                ? v.Value.ToString("0.####", CultureInfo.InvariantCulture) : "null");
        }

        internal static void BoolField(StringBuilder sb, string k, bool v)
        {
            sb.Append(",").Append(Json.Q(k)).Append(":").Append(v ? "true" : "false");
        }

        internal static void RefField(StringBuilder sb, string k, EntityReference r)
        {
            sb.Append(",").Append(Json.Q(k)).Append(":");
            if (r == null) { sb.Append("null"); return; }
            sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(r.Id.ToString()))
              .Append(",").Append(Json.Q("name")).Append(":").Append(Json.Q(r.Name ?? "")).Append("}");
        }

        /// <summary>Multiselect choices travel as a plain csv of option values, which keeps the
        /// designer, the flow and the context builder speaking the same simple format.</summary>
        internal static string MultiCsv(OptionSetValueCollection c)
        {
            if (c == null || c.Count == 0) return "";
            var parts = new List<string>();
            foreach (var o in c) parts.Add(o.Value.ToString());
            return string.Join(",", parts.ToArray());
        }
    }
}
