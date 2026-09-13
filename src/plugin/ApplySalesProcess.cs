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
    /// Fires on Create of opp. Finds the highest ranked published Case Process Template whose
    /// match rules are satisfied by the case, then applies it: SLA targets, business process flow and
    /// stage, stage linked tasks with resolved owners and task SLAs, and the required document checklist.
    /// </summary>
    public class ApplySalesProcess : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            if (!ctx.InputParameters.Contains("Target") || !(ctx.InputParameters["Target"] is Entity)) return;
            if (ctx.Depth > 2) return;

            // Two entry points. The deal itself is the obvious one, but a sales process is targeted
            // by what is being sold, and the product lines do not exist yet when the opportunity is
            // created. So a line landing on a deal that has no process yet is a second chance to
            // match. Everything downstream of here only ever sees an opportunity id.
            var oppId = ctx.PrimaryEntityId;
            if (ctx.PrimaryEntityName == "opportunityproduct")
            {
                var line = (Entity)ctx.InputParameters["Target"];
                var parent = line.GetAttributeValue<EntityReference>("opportunityid");
                if (parent == null)
                {
                    try
                    {
                        parent = svc.Retrieve("opportunityproduct", ctx.PrimaryEntityId,
                            new ColumnSet("opportunityid")).GetAttributeValue<EntityReference>("opportunityid");
                    }
                    catch (Exception) { return; }
                }
                if (parent == null) return;
                oppId = parent.Id;
            }
            else if (ctx.PrimaryEntityName != "opportunity") return;

            var started = DateTime.UtcNow;
            var log = new StringBuilder();

            {
                var opp = svc.Retrieve("opportunity", oppId, new ColumnSet(true));
                if (opp.Contains(P + "appliedtemplate"))
                {
                    trace.Trace("Template already applied, skipping.");
                    return;
                }

                var templates = LoadTemplates(svc);
                log.AppendLine("Evaluating " + templates.Count + " published template(s) in rank order.");

                Entity winner = null;
                foreach (var t in templates)
                {
                    var rules = LoadRules(svc, t.Id);
                    string why;
                    var ok = Evaluate(svc, opp, rules, out why);
                    log.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  rank {0,-4} {1,-40} => {2}  {3}",
                        t.GetAttributeValue<int>(P + "rank"),
                        t.GetAttributeValue<string>(P + "name"),
                        ok ? "MATCH" : "no match", why));
                    if (ok && winner == null) winner = t;
                }

                if (winner == null)
                {
                    log.AppendLine("No template matched. Deal left untouched.");
                    StampApplied(svc, oppId, null, 2, 0, 0, null, null, null, log, started);
                    return;
                }

                log.AppendLine("Winner: " + winner.GetAttributeValue<string>(P + "name"));

                var slaName = ApplySla(svc, opp, winner, log);
                string bpfName, stageName;
                ApplyBpf(svc, opp, winner, log, out bpfName, out stageName);
                var taskCount = GenerateTasks(svc, opp, winner, log);
                var docCount = GenerateDocuments(svc, opp, winner, log);

                var update = new Entity("opportunity", oppId);
                update[P + "appliedtemplate"] = new EntityReference(P + "salesprocesstemplate", winner.Id);
                update[P + "processappliedon"] = DateTime.UtcNow;
                update[P + "processsummary"] = string.Format(CultureInfo.InvariantCulture,
                    "{0} - {1} task(s), {2} document(s), SLA {3}",
                    winner.GetAttributeValue<string>(P + "name"), taskCount, docCount, slaName ?? "none");
                update[P + "taskstotal"] = taskCount;
                update[P + "tasksopen"] = taskCount;
                update[P + "docstotal"] = docCount;
                update[P + "docsreceived"] = 0;

                var setPriority = winner.GetAttributeValue<OptionSetValue>(P + "setopportunitypriority");
                if (setPriority != null && setPriority.Value > 0)
                    update["prioritycode"] = new OptionSetValue(setPriority.Value);

                svc.Update(update);

                var bump = new Entity(P + "salesprocesstemplate", winner.Id);
                bump[P + "appliedcount"] = winner.GetAttributeValue<int>(P + "appliedcount") + 1;
                svc.Update(bump);

                StampApplied(svc, oppId, winner, 1, taskCount, docCount, slaName, bpfName, stageName, log, started);
                trace.Trace(log.ToString());
            }
        }

        // ------------------------------------------------------------------ configuration load

        private static List<Entity> LoadTemplates(IOrganizationService svc)
        {
            var q = new QueryExpression(P + "salesprocesstemplate")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "rank", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "publishstatus", ConditionOperator.Equal, 2);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            var now = DateTime.UtcNow;
            var eff = new FilterExpression(LogicalOperator.Or);
            eff.AddCondition(P + "effectivefrom", ConditionOperator.Null);
            eff.AddCondition(P + "effectivefrom", ConditionOperator.OnOrBefore, now);
            q.Criteria.AddFilter(eff);

            var exp = new FilterExpression(LogicalOperator.Or);
            exp.AddCondition(P + "effectiveto", ConditionOperator.Null);
            exp.AddCondition(P + "effectiveto", ConditionOperator.OnOrAfter, now);
            q.Criteria.AddFilter(exp);

            return svc.RetrieveMultiple(q).Entities.ToList();
        }

        private static List<Entity> LoadRules(IOrganizationService svc, Guid templateId)
        {
            var q = new QueryExpression(P + "matchrule")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "sequence", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "template", ConditionOperator.Equal, templateId);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            return svc.RetrieveMultiple(q).Entities.ToList();
        }

        private static List<Entity> LoadTasks(IOrganizationService svc, Guid templateId)
        {
            var q = new QueryExpression(P + "processtask")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "sequence", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "template", ConditionOperator.Equal, templateId);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            return svc.RetrieveMultiple(q).Entities.ToList();
        }

        // ------------------------------------------------------------------ rule evaluation

        /// <summary>Conditions in the same group are ORed. Groups are ANDed.</summary>
        internal static bool Evaluate(IOrganizationService svc, Entity opp, List<Entity> rules,
                                      out string why)
        {
            why = string.Empty;
            if (rules.Count == 0) { why = "(no rules)"; return false; }

            var groups = rules.GroupBy(r => r.GetAttributeValue<int>(P + "groupnumber"));
            foreach (var g in groups)
            {
                var any = false;
                foreach (var r in g)
                {
                    if (Matches(svc, opp, r)) { any = true; break; }
                }
                if (!any)
                {
                    why = "failed on " + g.First().GetAttributeValue<string>(P + "attributelabel");
                    return false;
                }
            }
            return true;
        }

        private static bool Matches(IOrganizationService svc, Entity opp, Entity rule)
        {
            var attr = rule.GetAttributeValue<string>(P + "attributename");
            var op = rule.GetAttributeValue<OptionSetValue>(P + "operator");
            var raw = rule.GetAttributeValue<string>(P + "value") ?? string.Empty;
            if (string.IsNullOrEmpty(attr) || op == null) return false;

            var values = raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(v => v.Trim()).ToList();

            // A case carries one subject, so a rule about "what this is" is a single value. An
            // opportunity carries a basket of product lines instead, so the equivalent rule has to
            // mean "any line on this deal". That is a different shape of test and it gets its own
            // path rather than being bent into the scalar one.
            if (IsLineAttribute(attr))
                return MatchesAnyLine(svc, opp, attr, op.Value, raw, values);

            // "is under" / "is not under" walk a hierarchical lookup, so they need the reference
            // itself rather than the flattened string the other operators compare.
            if (op.Value == 11 || op.Value == 12)
            {
                var self = RefOf(svc, opp, attr);
                var under = self != null && values.Any(v => IsUnder(svc, self, v));
                return op.Value == 11 ? under : !under;
            }

            var actual = ValueOf(svc, opp, attr);

            switch (op.Value)
            {
                case 1: return Eq(actual, raw);
                case 2: return !Eq(actual, raw);
                case 3: return values.Any(v => Eq(actual, v));
                case 4: return !values.Any(v => Eq(actual, v));
                case 5: return actual != null &&
                               actual.IndexOf(raw, StringComparison.OrdinalIgnoreCase) >= 0;
                case 6: return actual != null &&
                               actual.StartsWith(raw, StringComparison.OrdinalIgnoreCase);
                case 7: return Num(actual) > Num(raw);
                case 8: return Num(actual) < Num(raw);
                case 9: return string.IsNullOrEmpty(actual);
                case 10: return !string.IsNullOrEmpty(actual);
                default: return false;
            }
        }

        // ------------------------------------------------------------------ product lines

        /// <summary>
        /// The prefix that marks a rule as being about the deal's product lines rather than the
        /// deal itself. "productlines.productid" is the one that matters - it is the sales
        /// equivalent of the case's subject - but the hop is written generally so a rule can reach
        /// any column on the line, for example "productlines.quantity".
        /// </summary>
        private const string LinePrefix = "productlines.";

        private static bool IsLineAttribute(string attr)
        {
            return (attr ?? string.Empty).StartsWith(LinePrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when ANY line on the deal satisfies the condition.
        ///
        /// The negative operators are the subtle ones. "is not under Business Lending" has to mean
        /// "no line on this deal is under Business Lending", not "some line isn't" - otherwise any
        /// mixed basket would satisfy both a rule and its opposite. So negatives are evaluated as
        /// the negation of the positive test over the whole basket, never line by line.
        /// </summary>
        private static bool MatchesAnyLine(IOrganizationService svc, Entity opp, string attr,
                                           int op, string raw, List<string> values)
        {
            var column = attr.Substring(LinePrefix.Length);
            var lines = LinesOf(svc, opp.Id, column);

            // Emptiness is about the basket itself, so it is answered before any line is read.
            if (op == 9) return lines.Count == 0;
            if (op == 10) return lines.Count > 0;
            if (lines.Count == 0) return false;

            switch (op)
            {
                case 11:
                case 12:
                {
                    var under = lines.Any(l =>
                    {
                        var r = l.Contains(column) ? l[column] as EntityReference : null;
                        return r != null && values.Any(v => IsUnder(svc, r, v));
                    });
                    return op == 11 ? under : !under;
                }

                case 1: return lines.Any(l => Eq(Flatten(l, column), raw));
                case 2: return !lines.Any(l => Eq(Flatten(l, column), raw));
                case 3: return lines.Any(l => values.Any(v => Eq(Flatten(l, column), v)));
                case 4: return !lines.Any(l => values.Any(v => Eq(Flatten(l, column), v)));
                case 5: return lines.Any(l => (Flatten(l, column) ?? "")
                            .IndexOf(raw, StringComparison.OrdinalIgnoreCase) >= 0);
                case 6: return lines.Any(l => (Flatten(l, column) ?? "")
                            .StartsWith(raw, StringComparison.OrdinalIgnoreCase));
                case 7: return lines.Any(l => Num(Flatten(l, column)) > Num(raw));
                case 8: return lines.Any(l => Num(Flatten(l, column)) < Num(raw));
                default: return false;
            }
        }

        /// <summary>
        /// The deal's product lines, loading only the column a rule asked about. productid is always
        /// fetched because a line that overrides the catalog ("write-in product") has no productid,
        /// and such a line can never sit under a product family however it is named.
        /// </summary>
        private static List<Entity> LinesOf(IOrganizationService svc, Guid oppId, string column)
        {
            var cols = new ColumnSet("productid");
            if (!string.Equals(column, "productid", StringComparison.OrdinalIgnoreCase))
                cols.AddColumn(column);

            var q = new QueryExpression("opportunityproduct") { ColumnSet = cols };
            q.Criteria.AddCondition("opportunityid", ConditionOperator.Equal, oppId);
            try { return svc.RetrieveMultiple(q).Entities.ToList(); }
            catch { return new List<Entity>(); }
        }

        // ------------------------------------------------------------------ hierarchy

        private const int MaxHierarchyDepth = 12;

        /// <summary>
        /// True when <paramref name="node"/> is the ancestor record itself or sits anywhere beneath it.
        /// Walking up from the case's own value is far cheaper than expanding the ancestor's subtree,
        /// because a subject tree is only a few levels deep but can be very wide.
        /// </summary>
        private static bool IsUnder(IOrganizationService svc, EntityReference node, string ancestorId)
        {
            Guid target;
            if (!Guid.TryParse((ancestorId ?? string.Empty).Trim(), out target)) return false;

            var parentAttr = ParentAttributeOf(svc, node.LogicalName);
            if (string.IsNullOrEmpty(parentAttr)) return node.Id == target;

            var seen = new HashSet<Guid>();
            var current = node.Id;
            for (var i = 0; i < MaxHierarchyDepth; i++)
            {
                if (current == target) return true;
                if (!seen.Add(current)) return false;      // defensive: cycles in the tree
                Entity row;
                try { row = svc.Retrieve(node.LogicalName, current, new ColumnSet(parentAttr)); }
                catch { return false; }
                var parent = row.GetAttributeValue<EntityReference>(parentAttr);
                if (parent == null) return false;
                current = parent.Id;
            }
            return false;
        }

        private static readonly Dictionary<string, string> ParentAttrCache =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "subject", "parentsubject" },
                { "account", "parentaccountid" },
                { "opportunity", "parentcaseid" },
                { "product", "parentproductid" },
                { "territory", "parentterritoryid" },
                { "businessunit", "parentbusinessunitid" },
                { "position", "parentpositionid" },
            };

        /// <summary>Self referential parent lookup for a hierarchical table, discovered once per entity.</summary>
        private static string ParentAttributeOf(IOrganizationService svc, string entity)
        {
            lock (ParentAttrCache)
            {
                string known;
                if (ParentAttrCache.TryGetValue(entity, out known)) return known;
            }

            string found = null;
            try
            {
                var req = new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest
                {
                    LogicalName = entity,
                    EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes,
                    RetrieveAsIfPublished = true
                };
                var resp = (Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)svc.Execute(req);
                foreach (var a in resp.EntityMetadata.Attributes)
                {
                    var lk = a as Microsoft.Xrm.Sdk.Metadata.LookupAttributeMetadata;
                    if (lk == null || lk.Targets == null || lk.Targets.Length != 1) continue;
                    if (!string.Equals(lk.Targets[0], entity, StringComparison.OrdinalIgnoreCase)) continue;
                    if (lk.AttributeOf != null) continue;
                    found = lk.LogicalName;
                    break;
                }
            }
            catch { /* fall through and cache the miss so we only pay for this once */ }

            lock (ParentAttrCache) { ParentAttrCache[entity] = found; }
            return found;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a ?? string.Empty, (b ?? string.Empty).Trim(),
                                 StringComparison.OrdinalIgnoreCase);
        }

        private static double Num(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : double.NaN;
        }

        /// <summary>
        /// Normalises a case attribute to a comparable string. Accepts a one hop path such as
        /// "customerid.spc_customersegment" so a rule can target the customer behind the case rather
        /// than a copy of that data denormalised onto the case itself.
        /// </summary>
        internal static string ValueOf(IOrganizationService svc, Entity e, string attr)
        {
            var dot = (attr ?? string.Empty).IndexOf('.');
            if (dot > 0)
            {
                var hop = attr.Substring(0, dot);
                var rest = attr.Substring(dot + 1);
                var related = Hop(svc, e, hop, rest);
                return related == null ? null : Flatten(related, rest);
            }
            return Flatten(e, attr);
        }

        /// <summary>Resolves the record on the far side of a lookup, loading only the column asked for.</summary>
        private static Entity Hop(IOrganizationService svc, Entity e, string lookup, string column)
        {
            if (!e.Contains(lookup)) return null;
            var er = e[lookup] as EntityReference;
            if (er == null) return null;
            try { return svc.Retrieve(er.LogicalName, er.Id, new ColumnSet(column)); }
            catch { return null; }   // column not present on this side of a polymorphic lookup
        }

        private static string Flatten(Entity e, string attr)
        {
            if (!e.Contains(attr)) return null;
            var v = e[attr];
            if (v == null) return null;
            var osv = v as OptionSetValue; if (osv != null) return osv.Value.ToString(CultureInfo.InvariantCulture);
            var er = v as EntityReference; if (er != null) return er.Id.ToString();
            var money = v as Money; if (money != null) return money.Value.ToString(CultureInfo.InvariantCulture);
            if (v is bool) return ((bool)v) ? "1" : "0";
            if (v is DateTime) return ((DateTime)v).ToString("o", CultureInfo.InvariantCulture);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        /// <summary>The raw reference behind an attribute path, needed by the hierarchy operators.</summary>
        private static EntityReference RefOf(IOrganizationService svc, Entity e, string attr)
        {
            var dot = (attr ?? string.Empty).IndexOf('.');
            if (dot <= 0) return e.Contains(attr) ? e[attr] as EntityReference : null;
            var related = Hop(svc, e, attr.Substring(0, dot), attr.Substring(dot + 1));
            if (related == null) return null;
            var rest = attr.Substring(dot + 1);
            return related.Contains(rest) ? related[rest] as EntityReference : null;
        }

        // ------------------------------------------------------------------ apply

        private static string ApplySla(IOrganizationService svc, Entity opp, Entity t, StringBuilder log)
        {
            var created = opp.Contains("createdon")
                ? opp.GetAttributeValue<DateTime>("createdon") : DateTime.UtcNow;

            var fr = t.GetAttributeValue<decimal?>(P + "qualificationhours");
            var res = t.GetAttributeValue<decimal?>(P + "closehours");

            var u = new Entity("opportunity", opp.Id);
            if (fr.HasValue && fr.Value > 0)
            {
                var due = created.AddHours((double)fr.Value);
                u[P + "qualificationdue"] = due;
                u[P + "qualificationwarn"] = created.AddHours((double)fr.Value * 0.75);
                u[P + "qualificationstatus"] = new OptionSetValue(1);
            }
            if (res.HasValue && res.Value > 0)
            {
                u[P + "closedue"] = created.AddHours((double)res.Value);
                u[P + "closewarn"] = created.AddHours((double)res.Value * 0.75);
                u[P + "closestatus"] = new OptionSetValue(1);
            }

            string slaName = null;
            var slaRef = t.GetAttributeValue<EntityReference>(P + "sla");
            if (slaRef != null)
            {
                var sla = svc.Retrieve("sla", slaRef.Id, new ColumnSet("statecode", "name"));
                slaName = sla.GetAttributeValue<string>("name");
                var slaState = sla.GetAttributeValue<OptionSetValue>("statecode");
                if (slaState != null && slaState.Value == 1)
                {
                    u["slaid"] = new EntityReference("sla", slaRef.Id);
                    log.AppendLine("  SLA record attached: " + slaName);
                }
                else
                {
                    log.AppendLine("  SLA '" + slaName + "' is not active, targets computed directly.");
                }
            }

            if (u.Attributes.Count > 0) svc.Update(u);
            log.AppendLine("  SLA targets set: first response " + fr + "h, resolution " + res + "h.");
            return slaName;
        }

        private static void ApplyBpf(IOrganizationService svc, Entity opp, Entity t,
                                     StringBuilder log, out string bpfName, out string stageName)
        {
            bpfName = t.GetAttributeValue<string>(P + "bpfname");
            stageName = t.GetAttributeValue<string>(P + "startstagename");
            var bpfId = t.GetAttributeValue<string>(P + "bpfid");
            var stageId = t.GetAttributeValue<string>(P + "startstageid");
            if (string.IsNullOrEmpty(bpfId)) { log.AppendLine("  No business process flow configured."); return; }

            Guid processGuid, stageGuid;
            if (!Guid.TryParse(bpfId, out processGuid))
            {
                log.AppendLine("  Business process flow id is not a valid guid, skipped.");
                return;
            }

            var u = new Entity("opportunity", opp.Id);
            u["processid"] = processGuid;
            var haveStage = Guid.TryParse(stageId, out stageGuid);
            if (haveStage) u["stageid"] = stageGuid;
            svc.Update(u);
            log.AppendLine("  Business process flow set to '" + bpfName + "' at stage '" + stageName + "'.");

            // Stamping processid/stageid on the case is not enough for the header to render: the
            // process instance row carries the active stage the UI actually reads.
            if (haveStage)
                ProcessRuntime.SyncInstance(svc, t.GetAttributeValue<string>(P + "bpfentityname"),
                                            opp.Id, processGuid, stageGuid, log);
        }

        /// <summary>
        /// Creates only the entry tasks of the graph. Everything downstream is created later by
        /// AdvanceProcess when an agent records an outcome on the task that precedes it.
        /// </summary>
        private static int GenerateTasks(IOrganizationService svc, Entity opp, Entity t, StringBuilder log)
        {
            var clock = opp.Contains("createdon")
                ? opp.GetAttributeValue<DateTime>("createdon") : DateTime.UtcNow;
            var all = ProcessRuntime.LoadTemplateTasks(svc, t.Id);
            var entry = ProcessRuntime.EntryTasks(svc, t.Id, all);

            foreach (var tt in entry)
                ProcessRuntime.Instantiate(svc, opp, tt, t.Id, clock, "Start");

            log.AppendLine("  Started " + entry.Count + " of " + all.Count
                           + " task(s); the rest unlock as outcomes are recorded.");
            return entry.Count;
        }

        private static int GenerateDocuments(IOrganizationService svc, Entity opp, Entity t, StringBuilder log)
        {
            var pkg = t.GetAttributeValue<EntityReference>(P + "documentpackage");
            if (pkg == null) { log.AppendLine("  No document package configured."); return 0; }

            var created = opp.Contains("createdon")
                ? opp.GetAttributeValue<DateTime>("createdon") : DateTime.UtcNow;

            var q = new QueryExpression(P + "documentitem")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "sequence", OrderType.Ascending) }
            };
            q.Criteria.AddCondition(P + "package", ConditionOperator.Equal, pkg.Id);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            var count = 0;
            foreach (var item in svc.RetrieveMultiple(q).Entities)
            {
                var d = new Entity(P + "opportunityrequireddocument");
                d[P + "name"] = item.GetAttributeValue<string>(P + "name");
                d[P + "opportunity"] = new EntityReference("opportunity", opp.Id);
                d[P + "packageitem"] = new EntityReference(P + "documentitem", item.Id);
                d[P + "sequence"] = item.GetAttributeValue<int>(P + "sequence");
                d[P + "mandatory"] = item.GetAttributeValue<bool>(P + "mandatory");
                d[P + "received"] = false;

                var resp = item.GetAttributeValue<OptionSetValue>(P + "responsible");
                if (resp != null) d[P + "responsible"] = new OptionSetValue(resp.Value);

                var due = item.GetAttributeValue<decimal?>(P + "duehours");
                if (due.HasValue && due.Value > 0) d[P + "duedate"] = created.AddHours((double)due.Value);

                var team = item.GetAttributeValue<EntityReference>(P + "ownerteam");
                if (team != null) d[P + "ownerteam"] = new EntityReference("team", team.Id);

                var url = item.GetAttributeValue<string>(P + "templateurl");
                if (!string.IsNullOrEmpty(url)) d[P + "templateurl"] = url;

                svc.Create(d);
                count++;
            }

            log.AppendLine("  Created " + count + " required document row(s) from package '" + pkg.Name + "'.");
            return count;
        }

        private static void StampApplied(IOrganizationService svc, Guid oppId, Entity template, int result,
                                         int tasks, int docs, string sla, string bpf, string stage,
                                         StringBuilder log, DateTime started)
        {
            var a = new Entity(P + "appliedprocess");
            a[P + "name"] = template == null
                ? "No template matched"
                : template.GetAttributeValue<string>(P + "name");
            a[P + "opportunity"] = new EntityReference("opportunity", oppId);
            if (template != null)
                a[P + "template"] = new EntityReference(P + "salesprocesstemplate", template.Id);
            a[P + "appliedon"] = DateTime.UtcNow;
            a[P + "result"] = new OptionSetValue(result);
            a[P + "tasksgenerated"] = tasks;
            a[P + "docsrequired"] = docs;
            if (sla != null) a[P + "slaapplied"] = sla;
            if (bpf != null) a[P + "bpfapplied"] = bpf;
            if (stage != null) a[P + "stageset"] = stage;
            a[P + "durationms"] = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            var text = log.ToString();
            a[P + "evaluationlog"] = text.Length > 90000 ? text.Substring(0, 90000) : text;
            svc.Create(a);
        }
    }
}
