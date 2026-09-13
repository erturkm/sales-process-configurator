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
    /// Custom API spc_GetOpportunityView. One call that feeds both runtime widgets on the case summary
    /// tab: the process progress rail (applied template, business process flow stages with the
    /// live one flagged, and the task timeline) and the required document checklist.
    /// </summary>
    public class GetOpportunityView : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);

            var raw = ctx.InputParameters.Contains("OpportunityId") ? (string)ctx.InputParameters["OpportunityId"] : null;
            var oppId = ProcessRuntime.ParseGuid(raw);
            if (oppId == Guid.Empty)
                throw new InvalidPluginExecutionException("OpportunityId is required.");

            var opp = svc.Retrieve("opportunity", oppId, new ColumnSet(
                "name", "estimatedvalue", "estimatedclosedate", "closeprobability", "statecode",
                "stageid", P + "appliedtemplate", P + "processappliedon",
                P + "processsummary", P + "qualificationdue", P + "qualificationstatus",
                P + "closedue", P + "closestatus",
                P + "tasksopen", P + "taskstotal", P + "docsreceived", P + "docstotal"));

            var sb = new StringBuilder();
            sb.Append("{").Append(Json.Q("deal")).Append(":{");
            sb.Append(Json.Q("id")).Append(":").Append(Json.Q(oppId.ToString()));
            Field(sb, "title", opp.GetAttributeValue<string>("name"));
            var est = opp.GetAttributeValue<Money>("estimatedvalue");
            Field(sb, "estimatedValue", est == null
                ? null : est.Value.ToString("N0", CultureInfo.InvariantCulture));
            DateField(sb, "estimatedCloseDate", opp.GetAttributeValue<DateTime?>("estimatedclosedate"));
            NumField(sb, "probability", opp.GetAttributeValue<int?>("closeprobability"));
            Field(sb, "summary", opp.GetAttributeValue<string>(P + "processsummary"));
            var tpl = opp.GetAttributeValue<EntityReference>(P + "appliedtemplate");
            Field(sb, "templateId", tpl == null ? null : tpl.Id.ToString());
            Field(sb, "templateName", tpl == null ? null : tpl.Name);
            DateField(sb, "appliedOn", opp.GetAttributeValue<DateTime?>(P + "processappliedon"));
            DateField(sb, "qualificationDue", opp.GetAttributeValue<DateTime?>(P + "qualificationdue"));
            DateField(sb, "closeDue", opp.GetAttributeValue<DateTime?>(P + "closedue"));
            NumField(sb, "qualificationStatus", Opt(opp, P + "qualificationstatus"));
            NumField(sb, "closeStatus", Opt(opp, P + "closestatus"));
            NumField(sb, "tasksOpen", opp.GetAttributeValue<int?>(P + "tasksopen"));
            NumField(sb, "tasksTotal", opp.GetAttributeValue<int?>(P + "taskstotal"));
            NumField(sb, "docsReceived", opp.GetAttributeValue<int?>(P + "docsreceived"));
            NumField(sb, "docsTotal", opp.GetAttributeValue<int?>(P + "docstotal"));
            sb.Append("}");

            AppendStages(sb, svc, oppId, tpl, opp.GetAttributeValue<Guid?>("stageid") ?? Guid.Empty);
            AppendTasks(sb, svc, oppId);
            AppendDocuments(sb, svc, oppId);

            sb.Append("}");
            ctx.OutputParameters["View"] = sb.ToString();
        }

        // ------------------------------------------------------------------ stages

        private static void AppendStages(StringBuilder sb, IOrganizationService svc, Guid oppId,
                                         EntityReference tpl, Guid stageId)
        {
            string bpfName = null, bpfEntity = null, current = null;
            var processId = Guid.Empty;

            if (tpl != null)
            {
                try
                {
                    var t = svc.Retrieve(P + "salesprocesstemplate", tpl.Id,
                        new ColumnSet(P + "bpfid", P + "bpfname", P + "bpfentityname"));
                    processId = ProcessRuntime.ParseGuid(t.GetAttributeValue<string>(P + "bpfid"));
                    bpfName = t.GetAttributeValue<string>(P + "bpfname");
                    bpfEntity = t.GetAttributeValue<string>(P + "bpfentityname");
                }
                catch (Exception) { }
            }

            current = CurrentStageName(svc, oppId, bpfEntity, stageId);

            sb.Append(",").Append(Json.Q("bpf")).Append(":{");
            sb.Append(Json.Q("name")).Append(":").Append(Json.Q(bpfName));
            Field(sb, "currentStage", current);
            sb.Append(",").Append(Json.Q("stages")).Append(":[");
            if (processId != Guid.Empty)
            {
                var first = true;
                foreach (var s in ProcessRuntime.OrderedStages(svc, processId))
                {
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{").Append(Json.Q("name")).Append(":")
                      .Append(Json.Q(s.GetAttributeValue<string>("stagename"))).Append("}");
                }
            }
            sb.Append("]}");
        }

        /// <summary>
        /// The engine stamps opp.stageid when it moves the flow, and the business process flow
        /// instance mirrors it. Prefer the case column, fall back to the instance row.
        /// </summary>
        private static string CurrentStageName(IOrganizationService svc, Guid oppId,
                                               string bpfEntity, Guid stageId)
        {
            if (stageId == Guid.Empty && !string.IsNullOrEmpty(bpfEntity))
            {
                try
                {
                    var q = new QueryExpression(bpfEntity)
                    {
                        ColumnSet = new ColumnSet("activestageid"),
                        TopCount = 1
                    };
                    q.Criteria.AddCondition("opportunityid", ConditionOperator.Equal, oppId);
                    var rows = svc.RetrieveMultiple(q).Entities;
                    if (rows.Count > 0)
                    {
                        var stage = rows[0].GetAttributeValue<EntityReference>("activestageid");
                        if (stage != null)
                        {
                            if (!string.IsNullOrEmpty(stage.Name)) return stage.Name;
                            stageId = stage.Id;
                        }
                    }
                }
                catch (Exception) { }
            }
            if (stageId == Guid.Empty) return null;
            try
            {
                var s = svc.Retrieve("processstage", stageId, new ColumnSet("stagename"));
                return s.GetAttributeValue<string>("stagename");
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------ tasks

        private static void AppendTasks(StringBuilder sb, IOrganizationService svc, Guid oppId)
        {
            var q = new QueryExpression("task")
            {
                ColumnSet = new ColumnSet("activityid", "subject", "statecode", "statuscode",
                    "scheduledend", "ownerid", "createdon", "description",
                    P + "sequence", P + "stagename", P + "sladue", P + "slastatus",
                    P + "assignedteamname", P + "outcomelabel", P + "branchpath",
                    P + "mandatory", P + "availableoutcomes"),
                Orders =
                {
                    new OrderExpression("statecode", OrderType.Ascending),
                    new OrderExpression(P + "sequence", OrderType.Ascending),
                    new OrderExpression("createdon", OrderType.Ascending)
                }
            };
            q.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, oppId);
            q.Criteria.AddCondition(P + "sourcetemplate", ConditionOperator.NotNull);

            sb.Append(",").Append(Json.Q("tasks")).Append(":[");
            var first = true;
            foreach (var t in svc.RetrieveMultiple(q).Entities)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(t.Id.ToString()));
                Field(sb, "subject", t.GetAttributeValue<string>("subject"));
                Field(sb, "stageName", t.GetAttributeValue<string>(P + "stagename"));
                Field(sb, "owner", OwnerLabel(t));
                Field(sb, "outcome", t.GetAttributeValue<string>(P + "outcomelabel"));
                Field(sb, "branchPath", t.GetAttributeValue<string>(P + "branchpath"));
                NumField(sb, "sequence", t.GetAttributeValue<int?>(P + "sequence"));
                NumField(sb, "state", Opt(t, "statecode"));
                NumField(sb, "slaStatus", Opt(t, P + "slastatus"));
                DateField(sb, "due", t.GetAttributeValue<DateTime?>(P + "sladue")
                                     ?? t.GetAttributeValue<DateTime?>("scheduledend"));
                BoolField(sb, "mandatory", t.GetAttributeValue<bool>(P + "mandatory"));
                NumField(sb, "outcomeCount", OutcomeCount(t.GetAttributeValue<string>(P + "availableoutcomes")));
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static string OwnerLabel(Entity t)
        {
            var named = t.GetAttributeValue<string>(P + "assignedteamname");
            if (!string.IsNullOrEmpty(named)) return named;
            var owner = t.GetAttributeValue<EntityReference>("ownerid");
            return owner == null ? null : owner.Name;
        }

        private static int OutcomeCount(string serialised)
        {
            if (string.IsNullOrEmpty(serialised)) return 0;
            try { return Json.Arr(Json.Parse(serialised)).Count; }
            catch (Exception) { return 0; }
        }

        // ------------------------------------------------------------------ documents

        private static void AppendDocuments(StringBuilder sb, IOrganizationService svc, Guid oppId)
        {
            var q = new QueryExpression(P + "opportunityrequireddocument")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "sequence", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "opportunity", ConditionOperator.Equal, oppId);
            var docs = svc.RetrieveMultiple(q).Entities.ToList();

            var files = Attachments(svc, oppId);

            sb.Append(",").Append(Json.Q("documents")).Append(":[");
            var first = true;
            foreach (var d in docs)
            {
                if (!first) sb.Append(",");
                first = false;
                var name = d.GetAttributeValue<string>(P + "name");
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(d.Id.ToString()));
                Field(sb, "name", name);
                Field(sb, "owner", OwnerOf(d));
                Field(sb, "templateUrl", d.GetAttributeValue<string>(P + "templateurl"));
                NumField(sb, "sequence", d.GetAttributeValue<int?>(P + "sequence"));
                BoolField(sb, "mandatory", d.GetAttributeValue<bool>(P + "mandatory"));
                BoolField(sb, "received", d.GetAttributeValue<bool>(P + "received"));
                DateField(sb, "due", d.GetAttributeValue<DateTime?>(P + "duedate"));
                DateField(sb, "receivedOn", d.GetAttributeValue<DateTime?>(P + "receivedon"));
                AppendFiles(sb, files, name);
                sb.Append("}");
            }
            sb.Append("]");
        }

        private static string OwnerOf(Entity d)
        {
            var team = d.GetAttributeValue<EntityReference>(P + "ownerteam");
            if (team != null) return team.Name;
            var resp = d.GetAttributeValue<OptionSetValue>(P + "responsible");
            if (resp == null) return null;
            switch (resp.Value)
            {
                case 1: return "Customer";
                case 2: return "Agent";
                case 3: return "Third party";
                default: return null;
            }
        }

        /// <summary>All notes on the case that carry a file, keyed for cheap prefix matching.</summary>
        private static List<Entity> Attachments(IOrganizationService svc, Guid oppId)
        {
            var q = new QueryExpression("annotation")
            {
                ColumnSet = new ColumnSet("annotationid", "subject", "filename", "filesize",
                                          "createdon", "mimetype"),
                Orders = { new OrderExpression("createdon", OrderType.Descending) }
            };
            q.Criteria.AddCondition("objectid", ConditionOperator.Equal, oppId);
            q.Criteria.AddCondition("isdocument", ConditionOperator.Equal, true);
            try { return svc.RetrieveMultiple(q).Entities.ToList(); }
            catch (Exception) { return new List<Entity>(); }
        }

        /// <summary>
        /// The upload widget stamps the note subject with "[requirement name]" so files can be
        /// tied back to the checklist row without another custom table.
        /// </summary>
        private static void AppendFiles(StringBuilder sb, List<Entity> files, string requirement)
        {
            var tag = "[" + (requirement ?? "") + "]";
            sb.Append(",").Append(Json.Q("files")).Append(":[");
            var first = true;
            foreach (var f in files)
            {
                var subject = f.GetAttributeValue<string>("subject") ?? "";
                if (string.IsNullOrEmpty(requirement) ||
                    subject.IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(f.Id.ToString()));
                Field(sb, "fileName", f.GetAttributeValue<string>("filename"));
                Field(sb, "mimeType", f.GetAttributeValue<string>("mimetype"));
                NumField(sb, "size", f.GetAttributeValue<int?>("filesize"));
                DateField(sb, "uploadedOn", f.GetAttributeValue<DateTime?>("createdon"));
                sb.Append("}");
            }
            sb.Append("]");
        }

        // ------------------------------------------------------------------ writers

        private static int? Opt(Entity e, string attr)
        {
            var v = e.GetAttributeValue<OptionSetValue>(attr);
            return v == null ? (int?)null : v.Value;
        }

        private static void Field(StringBuilder sb, string name, string value)
        {
            sb.Append(",").Append(Json.Q(name)).Append(":");
            if (value == null) sb.Append("null"); else sb.Append(Json.Q(value));
        }

        private static void NumField(StringBuilder sb, string name, int? value)
        {
            sb.Append(",").Append(Json.Q(name)).Append(":");
            sb.Append(value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "null");
        }

        private static void BoolField(StringBuilder sb, string name, bool value)
        {
            sb.Append(",").Append(Json.Q(name)).Append(":").Append(value ? "true" : "false");
        }

        private static void DateField(StringBuilder sb, string name, DateTime? value)
        {
            sb.Append(",").Append(Json.Q(name)).Append(":");
            if (!value.HasValue) sb.Append("null");
            else sb.Append(Json.Q(value.Value.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
        }
    }
}
