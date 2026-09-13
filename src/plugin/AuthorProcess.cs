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
    /// Custom API spc_AuthorProcess. Takes a JSON description of a case process blueprint produced by
    /// the Process Copilot and either renders a human readable preview (Mode = preview) or writes the
    /// template, its match rules and its stage linked task plan to Dataverse (Mode = commit).
    /// </summary>
    public class AuthorProcess : IPlugin
    {
        private const string P = "spc_";

        private static readonly Dictionary<string, int> Operators = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "equals", 1 }, { "eq", 1 }, { "is", 1 },
            { "does not equal", 2 }, { "not equals", 2 }, { "ne", 2 },
            { "is any of", 3 }, { "in", 3 }, { "any of", 3 },
            { "is none of", 4 }, { "not in", 4 },
            { "contains", 5 },
            { "begins with", 6 }, { "starts with", 6 },
            { "greater than", 7 }, { "gt", 7 },
            { "less than", 8 }, { "lt", 8 },
            { "is empty", 9 }, { "empty", 9 },
            { "is not empty", 10 }, { "not empty", 10 },
            { "is at or under", 11 }, { "at or under", 11 }, { "is under", 11 }, { "under", 11 },
            { "is not under", 12 }, { "not under", 12 }
        };

        private static readonly Dictionary<string, int> AssignTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "team", 1 }, { "user", 2 }, { "security role", 3 }, { "role", 3 }, { "queue", 4 },
            { "manager of deal owner", 5 }, { "manager of case owner", 5 }, { "manager", 5 },
            { "deal owner", 6 }, { "case owner", 6 }, { "owner", 6 }, { "account manager", 6 },
            { "ai agent", 7 }, { "agent", 7 }, { "ai", 7 }
        };

        /// <summary>Context scope labels the designer shows, mapped to the stored option values.</summary>
        private static readonly Dictionary<string, int> ContextScopes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "deal fields", 1 }, { "deal core fields", 1 }, { "opportunity fields", 1 }, { "case fields", 1 },
            { "deal narrative", 2 }, { "deal description", 2 }, { "case description", 2 },
            { "customer profile", 3 }, { "customer details", 3 }, { "customer fields", 3 },
            { "other deals with this customer", 4 }, { "customer deal history", 4 }, { "related deals", 4 },
            { "notes on the deal", 5 }, { "deal notes", 5 }, { "notes", 5 },
            { "emails on the deal", 6 }, { "emails", 6 },
            { "tasks already done and their outcomes", 7 }, { "process tasks and outcomes", 7 }, { "tasks", 7 },
            { "required documents", 8 }, { "documents", 8 }, { "attachments", 8 },
            { "clock status", 9 }, { "sla status", 9 }, { "sla", 9 },
            { "product lines", 10 }, { "products", 10 }, { "what is being sold", 10 }, { "line items", 10 }
        };

        private static readonly Dictionary<string, int> OutcomeModes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "agent proposes, a person selects", 1 }, { "agent proposes, human selects", 1 },
            { "recommends", 1 }, { "recommend", 1 },
            { "agent selects the outcome", 2 }, { "agent selects from configured outcomes", 2 },
            { "decides", 2 }, { "decide", 2 },
            { "no outcome, output only", 3 }, { "output only", 3 }
        };

        private static readonly Dictionary<string, int> OutputTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "agent output field only", 1 }, { "agent output field", 1 },
            { "task description", 2 },
            { "note on the deal", 3 }, { "deal note", 3 }, { "note on the case", 3 },
            { "append to deal narrative", 4 }, { "append to case description", 4 }
        };

        private static readonly Dictionary<string, int> SlaStart = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "deal created", 1 }, { "case created", 1 }, { "stage entered", 2 }, { "predecessor completed", 3 }, { "task created", 4 }
        };

        private static readonly Dictionary<string, int> OnBreach = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "do nothing", 1 }, { "notify task owner", 2 }, { "notify", 2 }, { "escalate to manager", 3 },
            { "escalate", 3 }, { "escalate to queue", 4 }, { "raise deal priority", 5 }, { "raise case priority", 5 }
        };

        private static readonly Dictionary<string, int> Priorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "leave as is", 0 }, { "none", 0 }, { "high", 1 }, { "normal", 2 }, { "medium", 2 }, { "low", 3 }
        };

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            var raw = ctx.InputParameters.Contains("ProcessJson") ? (string)ctx.InputParameters["ProcessJson"] : null;
            var mode = ctx.InputParameters.Contains("Mode") ? (string)ctx.InputParameters["Mode"] : "commit";
            if (string.IsNullOrEmpty(mode)) mode = "commit";
            var commit = mode.Equals("commit", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidPluginExecutionException("ProcessJson is required.");

            raw = raw.Trim();
            if (raw.StartsWith("```", StringComparison.Ordinal))
            {
                var nl = raw.IndexOf('\n');
                if (nl > 0) raw = raw.Substring(nl + 1);
                var fence = raw.LastIndexOf("```", StringComparison.Ordinal);
                if (fence >= 0) raw = raw.Substring(0, fence);
                raw = raw.Trim();
            }

            var root = Json.Obj(Json.Parse(raw));
            if (root == null) throw new InvalidPluginExecutionException("ProcessJson could not be parsed as JSON.");

            var name = Json.Str(root, "name");
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidPluginExecutionException("The process needs a name.");

            var warnings = new List<string>();
            var sum = new StringBuilder();

            // ---------------- resolve business process flow + stages ----------------
            var bpfName = Json.Str(root, "businessProcessFlow") ?? Json.Str(root, "bpf");
            Entity bpf = null;
            var stages = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(bpfName))
            {
                bpf = FindBpf(svc, bpfName);
                if (bpf == null) warnings.Add("Business process flow '" + bpfName + "' was not found and was skipped.");
                else
                {
                    foreach (var s in svc.RetrieveMultiple(new QueryExpression("processstage")
                    {
                        ColumnSet = new ColumnSet("processstageid", "stagename"),
                        Criteria = { Conditions = { new ConditionExpression("processid", ConditionOperator.Equal, bpf.Id) } }
                    }).Entities)
                    {
                        var sn = s.GetAttributeValue<string>("stagename");
                        if (!string.IsNullOrEmpty(sn)) stages[sn] = s.Id;
                    }
                }
            }

            var startStage = Json.Str(root, "startStage") ?? Json.Str(root, "startstage");
            Guid startStageId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(startStage) && !stages.TryGetValue(startStage, out startStageId))
                warnings.Add("Stage '" + startStage + "' does not exist on that business process flow.");

            // ---------------- resolve document package + sla ----------------
            var pkgName = Json.Str(root, "documentPackage") ?? Json.Str(root, "package");
            Guid pkgId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(pkgName))
            {
                pkgId = FindByName(svc, P + "documentpackage", P + "name", pkgName);
                if (pkgId == Guid.Empty) warnings.Add("Document package '" + pkgName + "' was not found.");
            }

            var slaName = Json.Str(root, "sla");
            Guid slaId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(slaName))
            {
                slaId = FindByName(svc, "sla", "name", slaName);
                if (slaId == Guid.Empty) warnings.Add("SLA '" + slaName + "' was not found.");
            }

            var rank = Json.Int(root, "rank") ?? 50;
            var publish = Json.Bool(root, "publish") ?? false;
            var priority = MapEnum(Priorities, Json.Str(root, "setOpportunityPriority") ?? Json.Str(root, "priority"), 0);
            var qualHours = Num(root, "qualificationHours");
            var closeHrs = Num(root, "closeHours");
            var description = Json.Str(root, "description", "");

            // ---------------- build header summary ----------------
            sum.AppendLine("**" + name + "**  (rank " + rank + ", " + (publish ? "Published" : "Draft") + ")");
            if (!string.IsNullOrEmpty(description)) sum.AppendLine(description);
            sum.AppendLine();
            sum.AppendLine("| Setting | Value |");
            sum.AppendLine("| --- | --- |");
            sum.AppendLine("| Business process flow | " + (bpf != null ? bpf.GetAttributeValue<string>("name") : "_not set_") + " |");
            sum.AppendLine("| Start stage | " + (string.IsNullOrEmpty(startStage) ? "_not set_" : startStage) + " |");
            sum.AppendLine("| First response target | " + (qualHours.HasValue ? qualHours.Value.ToString("0.##", CultureInfo.InvariantCulture) + " h" : "_not set_") + " |");
            sum.AppendLine("| Resolution target | " + (closeHrs.HasValue ? closeHrs.Value.ToString("0.##", CultureInfo.InvariantCulture) + " h" : "_not set_") + " |");
            sum.AppendLine("| Case priority | " + PriorityLabel(priority) + " |");
            sum.AppendLine("| SLA record | " + (slaId != Guid.Empty ? slaName : "_none_") + " |");
            sum.AppendLine("| Document package | " + (pkgId != Guid.Empty ? pkgName : "_none_") + " |");
            sum.AppendLine();

            // ---------------- rules ----------------
            var ruleRows = new List<Entity>();
            var rules = Json.Arr(Json.Get(root, "rules") ?? Json.Get(root, "matchRules"));
            var seq = 0;
            sum.AppendLine("**Targeting** (conditions in the same group are OR'ed, groups are AND'ed)");
            sum.AppendLine();
            sum.AppendLine("| # | Group | Attribute | Operator | Value |");
            sum.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var r in rules)
            {
                var ro = Json.Obj(r);
                if (ro == null) continue;
                seq++;
                var attr = Json.Str(ro, "attribute") ?? Json.Str(ro, "attributeName");
                var label = Json.Str(ro, "label") ?? AttributeLabel(svc, attr);
                var op = MapEnum(Operators, Json.Str(ro, "operator", "equals"), 1);
                var group = Json.Int(ro, "group") ?? Json.Int(ro, "groupNumber") ?? seq;
                var valueLabel = Json.Str(ro, "value", "");
                var stored = ResolveOptionValues(svc, attr, valueLabel, warnings);

                var e = new Entity(P + "matchrule");
                e[P + "name"] = name + " :: " + label;
                e[P + "sequence"] = seq;
                e[P + "groupnumber"] = group;
                e[P + "attributename"] = attr;
                e[P + "attributelabel"] = label;
                e[P + "operator"] = new OptionSetValue(op);
                e[P + "value"] = stored;
                e[P + "valuelabel"] = valueLabel;
                ruleRows.Add(e);

                sum.AppendLine("| " + seq + " | " + group + " | " + label + " | "
                               + OperatorLabel(op) + " | " + valueLabel + " |");
            }
            if (seq == 0) sum.AppendLine("| _no conditions - this template would match every case_ | | | | |");
            sum.AppendLine();

            // ---------------- tasks ----------------
            var taskRows = new List<Entity>();
            var tasks = Json.Arr(Json.Get(root, "tasks"));
            sum.AppendLine("**Task plan**");
            sum.AppendLine();
            sum.AppendLine("| # | Stage | Task | Assigned to | Due | Task SLA | Blocks stage |");
            sum.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
            var tseq = 0;
            foreach (var t in tasks)
            {
                var to = Json.Obj(t);
                if (to == null) continue;
                tseq++;
                var subject = Json.Str(to, "subject") ?? Json.Str(to, "name") ?? ("Task " + tseq);
                var stage = Json.Str(to, "stage") ?? Json.Str(to, "stageName") ?? startStage;
                var atype = MapEnum(AssignTypes, Json.Str(to, "assignTo") ?? Json.Str(to, "assignType", "team"), 1);
                var assignee = Json.Str(to, "team") ?? Json.Str(to, "user") ?? Json.Str(to, "queue")
                               ?? Json.Str(to, "role") ?? Json.Str(to, "assignee");
                var due = Num(to, "dueHours") ?? 24;
                var slaTarget = Num(to, "slaTargetHours");
                var warnPct = Json.Int(to, "slaWarnPercent") ?? 75;
                var blocks = Json.Bool(to, "blocksStage") ?? true;
                var mandatory = Json.Bool(to, "mandatory") ?? true;
                var startWhen = MapEnum(SlaStart, Json.Str(to, "slaStartWhen", "stage entered"), 2);
                var breach = MapEnum(OnBreach, Json.Str(to, "onBreach", "notify task owner"), 2);

                var e = new Entity(P + "processtask");
                e[P + "name"] = subject;
                e[P + "sequence"] = tseq;
                e[P + "description"] = Json.Str(to, "instructions", "");
                e[P + "assigntype"] = new OptionSetValue(atype);
                e[P + "duehours"] = new decimal(due);
                e[P + "slastartwhen"] = new OptionSetValue(startWhen);
                e[P + "slawarnpercent"] = warnPct;
                e[P + "onbreach"] = new OptionSetValue(breach);
                e[P + "blocksstage"] = blocks;
                e[P + "mandatory"] = mandatory;
                e[P + "pauseonwaiting"] = true;
                if (slaTarget.HasValue) e[P + "slatargethours"] = new decimal(slaTarget.Value);
                if (!string.IsNullOrEmpty(stage))
                {
                    e[P + "stagename"] = stage;
                    Guid sid;
                    if (stages.TryGetValue(stage, out sid)) e[P + "stageid"] = sid.ToString();
                    else warnings.Add("Task '" + subject + "' references unknown stage '" + stage + "'.");
                }

                var assignedLabel = AssignLabel(atype, assignee);
                if (atype == ProcessRuntime.AssignTypeAiAgent)
                {
                    assignedLabel = SetAgent(svc, e, to, subject, warnings);
                }
                else if (atype == 1 && !string.IsNullOrWhiteSpace(assignee))
                {
                    var id = FindByName(svc, "team", "name", assignee);
                    if (id == Guid.Empty) warnings.Add("Team '" + assignee + "' was not found for task '" + subject + "'.");
                    else e[P + "team"] = new EntityReference("team", id);
                }
                else if (atype == 2 && !string.IsNullOrWhiteSpace(assignee))
                {
                    var id = FindByName(svc, "systemuser", "fullname", assignee);
                    if (id == Guid.Empty) warnings.Add("User '" + assignee + "' was not found for task '" + subject + "'.");
                    else e[P + "user"] = new EntityReference("systemuser", id);
                }
                else if (atype == 3 && !string.IsNullOrWhiteSpace(assignee))
                {
                    e[P + "rolename"] = assignee;
                }
                else if (atype == 4 && !string.IsNullOrWhiteSpace(assignee))
                {
                    var id = FindByName(svc, "queue", "name", assignee);
                    if (id == Guid.Empty) warnings.Add("Queue '" + assignee + "' was not found for task '" + subject + "'.");
                    else e[P + "queue"] = new EntityReference("queue", id);
                }
                taskRows.Add(e);

                sum.AppendLine("| " + tseq + " | " + (stage ?? "-") + " | " + subject + " | " + assignedLabel
                               + " | " + due.ToString("0.##", CultureInfo.InvariantCulture) + " h | "
                               + (slaTarget.HasValue ? slaTarget.Value.ToString("0.##", CultureInfo.InvariantCulture) + " h" : "-")
                               + " | " + (blocks ? "Yes" : "No") + " |");
            }
            if (tseq == 0) sum.AppendLine("| _no tasks defined_ | | | | | | |");

            if (warnings.Count > 0)
            {
                sum.AppendLine();
                sum.AppendLine("**Warnings**");
                foreach (var w in warnings.Distinct()) sum.AppendLine("- " + w);
            }

            // ---------------- commit ----------------
            Guid templateId = Guid.Empty;
            if (commit)
            {
                var existing = FindByName(svc, P + "salesprocesstemplate", P + "name", name);
                var tpl = new Entity(P + "salesprocesstemplate");
                tpl[P + "name"] = name;
                tpl[P + "rank"] = rank;
                tpl[P + "publishstatus"] = new OptionSetValue(publish ? 2 : 1);
                tpl[P + "description"] = description;
                tpl[P + "matchlogic"] = new OptionSetValue(3);
                tpl[P + "setopportunitypriority"] = new OptionSetValue(priority);
                tpl[P + "effectivefrom"] = DateTime.UtcNow.Date;
                if (qualHours.HasValue) tpl[P + "qualificationhours"] = new decimal(qualHours.Value);
                if (closeHrs.HasValue) tpl[P + "closehours"] = new decimal(closeHrs.Value);
                if (bpf != null)
                {
                    tpl[P + "bpfid"] = bpf.Id.ToString();
                    tpl[P + "bpfname"] = bpf.GetAttributeValue<string>("name");
                    tpl[P + "bpfentityname"] = bpf.GetAttributeValue<string>("uniquename");
                }
                if (startStageId != Guid.Empty)
                {
                    tpl[P + "startstageid"] = startStageId.ToString();
                    tpl[P + "startstagename"] = startStage;
                }
                if (pkgId != Guid.Empty) tpl[P + "documentpackage"] = new EntityReference(P + "documentpackage", pkgId);
                if (slaId != Guid.Empty) tpl[P + "sla"] = new EntityReference("sla", slaId);

                if (existing != Guid.Empty)
                {
                    templateId = existing;
                    tpl.Id = existing;
                    svc.Update(tpl);
                    DeleteChildren(svc, P + "matchrule", P + "matchruleid", P + "template", existing);
                    DeleteChildren(svc, P + "processtask", P + "processtaskid", P + "template", existing);
                }
                else
                {
                    tpl[P + "appliedcount"] = 0;
                    templateId = svc.Create(tpl);
                }

                foreach (var r in ruleRows)
                {
                    r[P + "template"] = new EntityReference(P + "salesprocesstemplate", templateId);
                    svc.Create(r);
                }

                Guid prev = Guid.Empty;
                foreach (var t in taskRows)
                {
                    t[P + "template"] = new EntityReference(P + "salesprocesstemplate", templateId);
                    if (prev != Guid.Empty) t[P + "predecessor"] = new EntityReference(P + "processtask", prev);
                    prev = svc.Create(t);
                }

                sum.AppendLine();
                sum.AppendLine(existing != Guid.Empty
                    ? "Saved. The existing template was updated with " + ruleRows.Count + " condition(s) and " + taskRows.Count + " task(s)."
                    : "Saved. Created the template with " + ruleRows.Count + " condition(s) and " + taskRows.Count + " task(s).");
                if (publish) sum.AppendLine("It is **Published** and will be applied to matching cases from now on.");
                else sum.AppendLine("It is saved as **Draft**. Ask me to publish it when you are happy with it.");
            }
            else
            {
                sum.AppendLine();
                sum.AppendLine("This is a preview - nothing has been saved yet.");
            }

            trace.Trace("AuthorProcess mode={0} template={1}", mode, templateId);
            ctx.OutputParameters["Summary"] = sum.ToString();
            ctx.OutputParameters["TemplateId"] = templateId == Guid.Empty ? "" : templateId.ToString();
            ctx.OutputParameters["WarningCount"] = warnings.Distinct().Count();
        }

        // ------------------------------------------------------------------ helpers

        private static double? Num(Dictionary<string, object> d, string key)
        {
            var v = Json.Get(d, key);
            if (v == null) return null;
            if (v is double) return (double)v;
            double x;
            if (v is string && double.TryParse((string)v, NumberStyles.Any, CultureInfo.InvariantCulture, out x)) return x;
            return null;
        }

        private static int MapEnum(Dictionary<string, int> map, string key, int dflt)
        {
            if (string.IsNullOrWhiteSpace(key)) return dflt;
            int v;
            if (map.TryGetValue(key.Trim(), out v)) return v;
            if (int.TryParse(key, out v)) return v;
            return dflt;
        }

        private static string PriorityLabel(int v)
        {
            return v == 1 ? "High" : v == 2 ? "Normal" : v == 3 ? "Low" : "Leave as is";
        }

        private static string OperatorLabel(int v)
        {
            switch (v)
            {
                case 1: return "equals";
                case 2: return "does not equal";
                case 3: return "is any of";
                case 4: return "is none of";
                case 5: return "contains";
                case 6: return "begins with";
                case 7: return "greater than";
                case 8: return "less than";
                case 9: return "is empty";
                default: return "is not empty";
            }
        }

        private static string AssignLabel(int atype, string assignee)
        {
            switch (atype)
            {
                case 1: return "Team: " + (assignee ?? "?");
                case 2: return "User: " + (assignee ?? "?");
                case 3: return "Role: " + (assignee ?? "?");
                case 4: return "Queue: " + (assignee ?? "?");
                case 5: return "Manager of case owner";
                default: return "Case owner";
            }
        }

        private static Entity FindBpf(IOrganizationService svc, string name)
        {
            var q = new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet("workflowid", "name", "uniquename", "primaryentity"),
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("category", ConditionOperator.Equal, 4),
                        new ConditionExpression("type", ConditionOperator.Equal, 1),
                        new ConditionExpression("statecode", ConditionOperator.Equal, 1),
                        new ConditionExpression("name", ConditionOperator.Equal, name)
                    }
                }
            };
            var r = svc.RetrieveMultiple(q);
            if (r.Entities.Count > 0) return r.Entities[0];

            q.Criteria.Conditions[3] = new ConditionExpression("name", ConditionOperator.Like, "%" + name + "%");
            r = svc.RetrieveMultiple(q);
            return r.Entities.Count > 0 ? r.Entities[0] : null;
        }

        private static Guid FindByName(IOrganizationService svc, string entity, string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Guid.Empty;
            var q = new QueryExpression(entity)
            {
                ColumnSet = new ColumnSet(field),
                TopCount = 1,
                Criteria = { Conditions = { new ConditionExpression(field, ConditionOperator.Equal, value.Trim()) } }
            };
            var r = svc.RetrieveMultiple(q);
            if (r.Entities.Count > 0) return r.Entities[0].Id;

            q.Criteria.Conditions[0] = new ConditionExpression(field, ConditionOperator.Like, "%" + value.Trim() + "%");
            r = svc.RetrieveMultiple(q);
            return r.Entities.Count > 0 ? r.Entities[0].Id : Guid.Empty;
        }

        private static void DeleteChildren(IOrganizationService svc, string entity, string idField,
            string lookup, Guid parent)
        {
            var r = svc.RetrieveMultiple(new QueryExpression(entity)
            {
                ColumnSet = new ColumnSet(idField),
                Criteria = { Conditions = { new ConditionExpression(lookup, ConditionOperator.Equal, parent) } }
            });
            foreach (var e in r.Entities) svc.Delete(entity, e.Id);
        }

        /// <summary>
        /// Resolves attribute metadata, including the one hop dotted paths the match engine
        /// supports such as "customerid.spc_customersegment". Returns null when the attribute
        /// cannot be found so the caller can warn instead of failing the whole design.
        /// </summary>
        private static AttributeMetadata GetAttr(IOrganizationService svc, string attr)
        {
            if (string.IsNullOrWhiteSpace(attr)) return null;

            var entity = "opportunity";
            var name = attr.Trim();

            var dot = name.IndexOf('.');
            if (dot > 0)
            {
                var hop = name.Substring(0, dot);
                name = name.Substring(dot + 1);

                var lookup = Retrieve(svc, "opportunity", hop) as LookupAttributeMetadata;
                if (lookup == null || lookup.Targets == null || lookup.Targets.Length == 0)
                    return null;

                // customerid points at both account and contact. The segment column lives on
                // account in this model, so prefer a target that actually has the attribute.
                foreach (var target in lookup.Targets)
                {
                    var md = Retrieve(svc, target, name);
                    if (md != null) return md;
                }
                return null;
            }

            return Retrieve(svc, entity, name);
        }

        private static AttributeMetadata Retrieve(IOrganizationService svc, string entity, string attr)
        {
            try
            {
                var resp = (RetrieveAttributeResponse)svc.Execute(new RetrieveAttributeRequest
                {
                    EntityLogicalName = entity,
                    LogicalName = attr,
                    RetrieveAsIfPublished = true
                });
                return resp.AttributeMetadata;
            }
            catch
            {
                return null;
            }
        }

        private static string AttributeLabel(IOrganizationService svc, string attr)
        {
            if (string.IsNullOrEmpty(attr)) return "";
            var md = GetAttr(svc, attr);
            return md != null && md.DisplayName != null && md.DisplayName.UserLocalizedLabel != null
                ? md.DisplayName.UserLocalizedLabel.Label : attr;
        }

        /// <summary>
        /// Applies the AI agent configuration a designed task carries. Mirrors SaveProcessGraph so a
        /// process authored by the Copilot behaves identically to one drawn by hand.
        /// </summary>
        private static string SetAgent(IOrganizationService svc, Entity e,
            Dictionary<string, object> to, string subject, List<string> warnings)
        {
            var agentName = Json.Str(to, "agentName") ?? Json.Str(to, "agent");
            var label = "AI agent";

            if (string.IsNullOrWhiteSpace(agentName))
            {
                warnings.Add("Task '" + subject + "' is assigned to an AI agent but names no agent.");
            }
            else
            {
                var id = FindByName(svc, "bot", "name", agentName);
                if (id == Guid.Empty)
                    warnings.Add("Agent '" + agentName + "' was not found for task '" + subject + "'.");
                else
                {
                    e[P + "agent"] = new EntityReference("bot", id);
                    label = "AI agent: " + agentName;
                }
            }

            var prompt = Json.Str(to, "agentPrompt", "");
            if (string.IsNullOrWhiteSpace(prompt))
                warnings.Add("Task '" + subject + "' has no agent prompt, so the agent gets no instruction.");
            e[P + "agentprompt"] = prompt;

            var mode = MapEnum(OutcomeModes, Json.Str(to, "agentOutcomeMode", "agent selects the outcome"), 2);
            var auto = Json.Bool(to, "autoComplete") ?? true;
            if (auto && mode == 1)
            {
                // Auto complete is inert when a person still has to pick the outcome. Say so
                // rather than silently producing a task that looks automated but always waits.
                warnings.Add("Task '" + subject + "' has auto complete on but a person still "
                             + "selects the outcome, so it will wait for them. Auto complete was "
                             + "turned off.");
                auto = false;
            }
            e[P + "agentoutcomemode"] = new OptionSetValue(mode);
            e[P + "autocomplete"] = auto;
            e[P + "outputtarget"] = new OptionSetValue(
                MapEnum(OutputTargets, Json.Str(to, "outputTarget", "task description"), 2));

            var conf = Json.Int(to, "confidenceThreshold") ?? 70;
            e[P + "confidencethreshold"] = conf < 0 ? 0 : (conf > 100 ? 100 : conf);
            var timeout = Json.Int(to, "agentTimeoutMins") ?? 5;
            e[P + "agenttimeoutmins"] = timeout < 1 ? 1 : (timeout > 120 ? 120 : timeout);

            var scope = new OptionSetValueCollection();
            var seen = new HashSet<int>();
            foreach (var c in Json.Arr(Json.Get(to, "agentContext")))
            {
                var s = c as string;
                if (string.IsNullOrWhiteSpace(s)) continue;
                int v;
                if (!ContextScopes.TryGetValue(s.Trim(), out v))
                {
                    warnings.Add("Unknown agent context '" + s + "' on task '" + subject + "'.");
                    continue;
                }
                if (seen.Add(v)) scope.Add(new OptionSetValue(v));
            }
            if (scope.Count == 0)
            {
                // An agent with no context can only guess.
                scope.Add(new OptionSetValue(1));
                warnings.Add("Task '" + subject + "' gave the agent no deal context, so deal fields "
                             + "were added.");
            }
            e[P + "contextscope"] = scope;

            return label;
        }

        /// <summary>Turns human option labels into the stored integer values the rule engine compares against.</summary>
        private static string ResolveOptionValues(IOrganizationService svc, string attr, string value,
            List<string> warnings)
        {
            if (string.IsNullOrEmpty(attr) || string.IsNullOrEmpty(value)) return value ?? "";
            var md = GetAttr(svc, attr) as EnumAttributeMetadata;
            if (md == null) return value;

            var parts = value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var outv = new List<string>();
            foreach (var raw in parts)
            {
                var p = raw.Trim();
                int already;
                if (int.TryParse(p, out already)) { outv.Add(already.ToString(CultureInfo.InvariantCulture)); continue; }

                var hit = md.OptionSet.Options.FirstOrDefault(o =>
                    o.Label != null && o.Label.UserLocalizedLabel != null &&
                    string.Equals(o.Label.UserLocalizedLabel.Label, p, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                    hit = md.OptionSet.Options.FirstOrDefault(o =>
                        o.Label != null && o.Label.UserLocalizedLabel != null &&
                        o.Label.UserLocalizedLabel.Label.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

                if (hit != null) outv.Add(hit.Value.GetValueOrDefault().ToString(CultureInfo.InvariantCulture));
                else
                {
                    warnings.Add("'" + p + "' is not a valid choice for " + attr + ".");
                    outv.Add(p);
                }
            }
            return string.Join(";", outv.ToArray());
        }
    }
}
