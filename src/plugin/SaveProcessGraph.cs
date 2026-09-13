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
    /// Custom API spc_SaveProcessGraph. Takes the whole canvas back as JSON and reconciles it against
    /// Dataverse. Nodes and edges carry client side ids for anything newly drawn, so the save is a
    /// single atomic round trip rather than a chatty per-record sync.
    /// </summary>
    public class SaveProcessGraph : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            var raw = ctx.InputParameters.Contains("Graph") ? (string)ctx.InputParameters["Graph"] : null;
            if (string.IsNullOrWhiteSpace(raw))
                throw new InvalidPluginExecutionException("Graph is required.");

            var root = Json.Obj(Json.Parse(raw));
            if (root == null) throw new InvalidPluginExecutionException("Graph could not be parsed.");

            var header = Json.Obj(Json.Get(root, "template"));
            if (header == null) throw new InvalidPluginExecutionException("Graph.template is required.");

            var templateId = ProcessRuntime.ParseGuid(Json.Str(header, "id"));
            var name = Json.Str(header, "name");
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidPluginExecutionException("The process needs a name.");

            // ---------------- header ----------------
            var tpl = new Entity(P + "salesprocesstemplate");
            tpl[P + "name"] = name;
            tpl[P + "description"] = Json.Str(header, "description", "");
            tpl[P + "rank"] = Json.Int(header, "rank") ?? 50;
            tpl[P + "publishstatus"] = new OptionSetValue(Json.Int(header, "publishStatus") ?? 1);
            tpl[P + "setopportunitypriority"] = new OptionSetValue(Json.Int(header, "setOpportunityPriority") ?? 0);
            tpl[P + "matchlogic"] = new OptionSetValue(3);

            var fr = Dec(header, "qualificationHours");
            if (fr.HasValue) tpl[P + "qualificationhours"] = fr.Value;
            var rh = Dec(header, "closeHours");
            if (rh.HasValue) tpl[P + "closehours"] = rh.Value;

            var bpfName = Json.Str(header, "bpfName");
            Guid processId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(bpfName))
            {
                var bpf = FindBpf(svc, bpfName);
                if (bpf != null)
                {
                    processId = bpf.Id;
                    tpl[P + "bpfid"] = bpf.Id.ToString();
                    tpl[P + "bpfname"] = bpf.GetAttributeValue<string>("name");
                    tpl[P + "bpfentityname"] = bpf.GetAttributeValue<string>("uniquename");
                }
            }

            var stages = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            if (processId != Guid.Empty)
            {
                var sq = new QueryExpression("processstage")
                {
                    ColumnSet = new ColumnSet("processstageid", "stagename")
                };
                sq.Criteria.AddCondition("processid", ConditionOperator.Equal, processId);
                foreach (var s in svc.RetrieveMultiple(sq).Entities)
                {
                    var sn = s.GetAttributeValue<string>("stagename");
                    if (!string.IsNullOrEmpty(sn)) stages[sn] = s.Id;
                }
            }

            var startStage = Json.Str(header, "startStageName");
            if (!string.IsNullOrWhiteSpace(startStage))
            {
                tpl[P + "startstagename"] = startStage;
                Guid sid;
                if (stages.TryGetValue(startStage, out sid)) tpl[P + "startstageid"] = sid.ToString();
            }

            var slaName = NameOf(Json.Get(header, "sla"));
            tpl[P + "sla"] = null;
            if (!string.IsNullOrWhiteSpace(slaName))
            {
                var id = FindByName(svc, "sla", "name", slaName);
                if (id != Guid.Empty) tpl[P + "sla"] = new EntityReference("sla", id);
            }
            var pkgRef = SavePackage(svc, Json.Obj(Json.Get(root, "package")), header);
            if (pkgRef != null) tpl[P + "documentpackage"] = pkgRef;
            else
            {
                var pkgName = NameOf(Json.Get(header, "documentPackage"));
                if (!string.IsNullOrWhiteSpace(pkgName))
                {
                    var id = FindByName(svc, P + "documentpackage", P + "name", pkgName);
                    if (id != Guid.Empty) tpl[P + "documentpackage"] = new EntityReference(P + "documentpackage", id);
                }
                else tpl[P + "documentpackage"] = null;
            }

            if (templateId != Guid.Empty)
            {
                tpl.Id = templateId;
                svc.Update(tpl);
            }
            else
            {
                tpl[P + "appliedcount"] = 0;
                tpl[P + "effectivefrom"] = DateTime.UtcNow.Date;
                templateId = svc.Create(tpl);
            }

            // ---------------- nodes ----------------
            var existingTasks = ProcessRuntime.LoadTemplateTasks(svc, templateId)
                                              .ToDictionary(e => e.Id, e => e);
            var nodes = Json.Arr(Json.Get(root, "nodes"));
            var clientToServer = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var keptTasks = new HashSet<Guid>();
            var seq = 0;

            foreach (var raw2 in nodes)
            {
                var n = Json.Obj(raw2);
                if (n == null) continue;
                seq++;
                var clientId = Json.Str(n, "id", "");
                var serverId = ProcessRuntime.ParseGuid(clientId);

                var e = new Entity(P + "processtask");
                e[P + "name"] = Json.Str(n, "name", "Untitled task");
                e[P + "description"] = Json.Str(n, "instructions", "");
                e[P + "sequence"] = Json.Int(n, "sequence") ?? seq;
                e[P + "assigntype"] = new OptionSetValue(Json.Int(n, "assignType") ?? 1);
                e[P + "slastartwhen"] = new OptionSetValue(Json.Int(n, "slaStartWhen") ?? 2);
                e[P + "onbreach"] = new OptionSetValue(Json.Int(n, "onBreach") ?? 2);
                e[P + "slawarnpercent"] = Json.Int(n, "slaWarnPercent") ?? 75;
                e[P + "blocksstage"] = Json.Bool(n, "blocksStage") ?? true;
                e[P + "mandatory"] = Json.Bool(n, "mandatory") ?? true;
                e[P + "isstart"] = Json.Bool(n, "isStart") ?? false;
                e[P + "pauseonwaiting"] = true;
                e[P + "posx"] = Json.Int(n, "x") ?? 0;
                e[P + "posy"] = Json.Int(n, "y") ?? 0;
                e[P + "nodecolor"] = Json.Str(n, "color", "");
                e[P + "template"] = new EntityReference(P + "salesprocesstemplate", templateId);

                var dueH = Dec(n, "dueHours");
                if (dueH.HasValue) e[P + "duehours"] = dueH.Value;
                var slaH = Dec(n, "slaTargetHours");
                if (slaH.HasValue) e[P + "slatargethours"] = slaH.Value;

                var stageName = Json.Str(n, "stageName", "");
                if (!string.IsNullOrEmpty(stageName))
                {
                    e[P + "stagename"] = stageName;
                    Guid sid;
                    if (stages.TryGetValue(stageName, out sid)) e[P + "stageid"] = sid.ToString();
                }

                SetOwner(svc, e, n);
                SetAgentConfig(svc, e, n);

                Guid id;
                if (serverId != Guid.Empty && existingTasks.ContainsKey(serverId))
                {
                    e.Id = serverId;
                    svc.Update(e);
                    id = serverId;
                }
                else
                {
                    id = svc.Create(e);
                }
                clientToServer[clientId] = id;
                keptTasks.Add(id);
            }

            foreach (var gone in existingTasks.Keys.Where(k => !keptTasks.Contains(k)).ToList())
                svc.Delete(P + "processtask", gone);

            // ---------------- edges ----------------
            var existingOutcomes = ProcessRuntime.LoadTemplateOutcomes(svc, templateId)
                                                 .ToDictionary(e => e.Id, e => e);
            var edges = Json.Arr(Json.Get(root, "outcomes"));
            var keptOutcomes = new HashSet<Guid>();
            var oseq = 0;

            foreach (var raw3 in edges)
            {
                var o = Json.Obj(raw3);
                if (o == null) continue;
                oseq++;

                var fromKey = Json.Str(o, "from", "");
                Guid fromId;
                if (!clientToServer.TryGetValue(fromKey, out fromId)) continue;

                var e = new Entity(P + "taskoutcome");
                e[P + "name"] = Json.Str(o, "label", "Outcome " + oseq);
                e[P + "description"] = Json.Str(o, "guidance", "");
                e[P + "sequence"] = Json.Int(o, "sequence") ?? oseq;
                e[P + "sentiment"] = new OptionSetValue(Json.Int(o, "sentiment") ?? 2);
                e[P + "advancestage"] = Json.Bool(o, "advanceStage") ?? false;
                e[P + "closeopportunity"] = new OptionSetValue(Json.Int(o, "closeOpportunity") ?? 0);
                e[P + "requirecomment"] = Json.Bool(o, "requireComment") ?? false;
                e[P + "isdefault"] = Json.Bool(o, "isDefault") ?? false;
                e[P + "setopportunitypriority"] = new OptionSetValue(Json.Int(o, "setOpportunityPriority") ?? 0);
                e[P + "targetstagename"] = Json.Str(o, "targetStageName", "");
                e[P + "task"] = new EntityReference(P + "processtask", fromId);
                e[P + "template"] = new EntityReference(P + "salesprocesstemplate", templateId);

                var toKey = Json.Str(o, "to", "");
                Guid toId;
                if (!string.IsNullOrEmpty(toKey) && clientToServer.TryGetValue(toKey, out toId))
                    e[P + "nexttask"] = new EntityReference(P + "processtask", toId);

                var oid = ProcessRuntime.ParseGuid(Json.Str(o, "id", ""));
                if (oid != Guid.Empty && existingOutcomes.ContainsKey(oid))
                {
                    e.Id = oid;
                    svc.Update(e);
                    keptOutcomes.Add(oid);
                }
                else
                {
                    keptOutcomes.Add(svc.Create(e));
                }
            }

            foreach (var gone in existingOutcomes.Keys.Where(k => !keptOutcomes.Contains(k)).ToList())
                svc.Delete(P + "taskoutcome", gone);

            var ruleCount = SaveRules(svc, templateId, Json.Arr(Json.Get(root, "matchRules")));

            trace.Trace("SaveProcessGraph template={0} nodes={1} edges={2} rules={3}",
                templateId, keptTasks.Count, keptOutcomes.Count, ruleCount);

            ctx.OutputParameters["TemplateId"] = templateId.ToString();
            ctx.OutputParameters["Summary"] = "Saved " + keptTasks.Count + " task(s), "
                                              + keptOutcomes.Count + " outcome(s) and "
                                              + ruleCount + " targeting condition(s).";
        }

        /// <summary>
        /// Replaces the template's targeting filter with whatever the rule builder posted. Rules are
        /// cheap rows with no children, so a reconcile-and-delete-orphans pass keeps the canvas as the
        /// single source of truth.
        /// </summary>
        private static int SaveRules(IOrganizationService svc, Guid templateId, List<object> rules)
        {
            var q = new QueryExpression(P + "matchrule") { ColumnSet = new ColumnSet(P + "matchruleid") };
            q.Criteria.AddCondition(P + "template", ConditionOperator.Equal, templateId);
            var existing = svc.RetrieveMultiple(q).Entities.Select(e => e.Id).ToList();
            var kept = new HashSet<Guid>();
            var seq = 0;

            foreach (var rawRule in rules)
            {
                var r = Json.Obj(rawRule);
                if (r == null) continue;
                var attr = Json.Str(r, "attribute", "");
                if (string.IsNullOrWhiteSpace(attr)) continue;
                seq++;

                var e = new Entity(P + "matchrule");
                e[P + "name"] = Truncate(Json.Str(r, "attributeLabel", attr) + " "
                    + OperatorWord(Json.Int(r, "operator") ?? 1) + " "
                    + Json.Str(r, "valueLabel", Json.Str(r, "value", "")), 100);
                e[P + "attributename"] = attr;
                e[P + "attributelabel"] = Json.Str(r, "attributeLabel", attr);
                e[P + "operator"] = new OptionSetValue(Json.Int(r, "operator") ?? 1);
                e[P + "value"] = Json.Str(r, "value", "");
                e[P + "valuelabel"] = Json.Str(r, "valueLabel", "");
                e[P + "groupnumber"] = Json.Int(r, "group") ?? 1;
                e[P + "sequence"] = Json.Int(r, "sequence") ?? seq;
                e[P + "template"] = new EntityReference(P + "salesprocesstemplate", templateId);

                var id = ProcessRuntime.ParseGuid(Json.Str(r, "id", ""));
                if (id != Guid.Empty && existing.Contains(id))
                {
                    e.Id = id;
                    svc.Update(e);
                    kept.Add(id);
                }
                else kept.Add(svc.Create(e));
            }

            foreach (var gone in existing.Where(k => !kept.Contains(k)).ToList())
                svc.Delete(P + "matchrule", gone);

            return kept.Count;
        }

        /// <summary>
        /// Creates or updates the document package edited inline on the canvas and reconciles its
        /// items. Returns the reference to stamp on the template, or null when no package is attached.
        /// </summary>
        private static EntityReference SavePackage(IOrganizationService svc,
            Dictionary<string, object> pkg, Dictionary<string, object> header)
        {
            if (pkg == null) return null;
            var name = Json.Str(pkg, "name", "");
            if (string.IsNullOrWhiteSpace(name)) return null;

            var pkgId = ProcessRuntime.ParseGuid(Json.Str(pkg, "id", ""));
            var e = new Entity(P + "documentpackage");
            e[P + "name"] = Truncate(name, 100);
            e[P + "description"] = Json.Str(pkg, "description", "");
            e[P + "active"] = Json.Bool(pkg, "active") ?? true;

            if (pkgId != Guid.Empty)
            {
                e.Id = pkgId;
                svc.Update(e);
            }
            else
            {
                pkgId = FindByName(svc, P + "documentpackage", P + "name", name);
                if (pkgId != Guid.Empty) { e.Id = pkgId; svc.Update(e); }
                else pkgId = svc.Create(e);
            }

            var q = new QueryExpression(P + "documentitem") { ColumnSet = new ColumnSet(P + "documentitemid") };
            q.Criteria.AddCondition(P + "package", ConditionOperator.Equal, pkgId);
            var existing = svc.RetrieveMultiple(q).Entities.Select(x => x.Id).ToList();
            var kept = new HashSet<Guid>();
            var seq = 0;

            foreach (var rawItem in Json.Arr(Json.Get(pkg, "items")))
            {
                var i = Json.Obj(rawItem);
                if (i == null) continue;
                var iname = Json.Str(i, "name", "");
                if (string.IsNullOrWhiteSpace(iname)) continue;
                seq++;

                var d = new Entity(P + "documentitem");
                d[P + "name"] = Truncate(iname, 100);
                d[P + "description"] = Json.Str(i, "description", "");
                d[P + "sequence"] = Json.Int(i, "sequence") ?? seq;
                d[P + "mandatory"] = Json.Bool(i, "mandatory") ?? true;
                d[P + "responsible"] = new OptionSetValue(Json.Int(i, "responsible") ?? 1);
                d[P + "templateurl"] = Json.Str(i, "templateUrl", "");
                d[P + "package"] = new EntityReference(P + "documentpackage", pkgId);
                var due = Dec(i, "dueHours");
                if (due.HasValue) d[P + "duehours"] = due.Value;

                var id = ProcessRuntime.ParseGuid(Json.Str(i, "id", ""));
                if (id != Guid.Empty && existing.Contains(id))
                {
                    d.Id = id;
                    svc.Update(d);
                    kept.Add(id);
                }
                else kept.Add(svc.Create(d));
            }

            foreach (var gone in existing.Where(k => !kept.Contains(k)).ToList())
                svc.Delete(P + "documentitem", gone);

            return new EntityReference(P + "documentpackage", pkgId);
        }

        internal static string OperatorWord(int op)
        {
            switch (op)
            {
                case 1: return "is";
                case 2: return "is not";
                case 3: return "is any of";
                case 4: return "is none of";
                case 5: return "contains";
                case 6: return "begins with";
                case 7: return "is greater than";
                case 8: return "is less than";
                case 9: return "is empty";
                case 10: return "is not empty";
                case 11: return "is at or under";
                case 12: return "is not under";
                default: return "is";
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        private static void SetOwner(IOrganizationService svc, Entity e, Dictionary<string, object> n)
        {
            var atype = Json.Int(n, "assignType") ?? 1;
            var teamName = NameOf(Json.Get(n, "team"));
            var userName = NameOf(Json.Get(n, "user"));
            var queueName = NameOf(Json.Get(n, "queue"));
            var fallback = NameOf(Json.Get(n, "fallbackTeam"));

            // Clear every owner slot first so switching assign type on the canvas does not leave a
            // stale lookup behind that the runtime would still resolve against.
            e[P + "team"] = null;
            e[P + "user"] = null;
            e[P + "queue"] = null;
            e[P + "rolename"] = null;
            e[P + "fallbackteam"] = null;

            if (atype == 1 && !string.IsNullOrWhiteSpace(teamName))
            {
                var id = FindByName(svc, "team", "name", teamName);
                if (id != Guid.Empty) e[P + "team"] = new EntityReference("team", id);
            }
            else if (atype == 2 && !string.IsNullOrWhiteSpace(userName))
            {
                var id = FindByName(svc, "systemuser", "fullname", userName);
                if (id != Guid.Empty) e[P + "user"] = new EntityReference("systemuser", id);
            }
            else if (atype == 3)
            {
                e[P + "rolename"] = Json.Str(n, "roleName", "");
            }
            else if (atype == 4 && !string.IsNullOrWhiteSpace(queueName))
            {
                var id = FindByName(svc, "queue", "name", queueName);
                if (id != Guid.Empty) e[P + "queue"] = new EntityReference("queue", id);
            }

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                var id = FindByName(svc, "team", "name", fallback);
                if (id != Guid.Empty) e[P + "fallbackteam"] = new EntityReference("team", id);
            }
        }

        /// <summary>
        /// Persists the AI agent block. Everything is cleared first so a task demoted from
        /// "AI Agent" back to a human assign type does not keep a live agent binding that the
        /// runtime would still act on.
        /// </summary>
        private static void SetAgentConfig(IOrganizationService svc, Entity e, Dictionary<string, object> n)
        {
            e[P + "agent"] = null;
            e[P + "agentprompt"] = null;
            e[P + "contextscope"] = null;
            e[P + "outputtarget"] = null;
            e[P + "agentoutcomemode"] = null;
            e[P + "autocomplete"] = false;
            e[P + "confidencethreshold"] = null;
            e[P + "agenttimeoutmins"] = null;

            if ((Json.Int(n, "assignType") ?? 1) != ProcessRuntime.AssignTypeAiAgent) return;

            var agentName = NameOf(Json.Get(n, "agent"));
            var agentId = ProcessRuntime.ParseGuid(IdOf(Json.Get(n, "agent")));
            if (agentId == Guid.Empty && !string.IsNullOrWhiteSpace(agentName))
                agentId = FindByName(svc, "bot", "name", agentName);
            if (agentId != Guid.Empty) e[P + "agent"] = new EntityReference("bot", agentId);

            e[P + "agentprompt"] = Json.Str(n, "agentPrompt", "");
            e[P + "outputtarget"] = new OptionSetValue(Json.Int(n, "outputTarget") ?? 2);
            e[P + "agentoutcomemode"] = new OptionSetValue(Json.Int(n, "agentOutcomeMode") ?? 2);
            e[P + "autocomplete"] = Json.Bool(n, "autoComplete") ?? true;
            e[P + "confidencethreshold"] = Clamp(Json.Int(n, "confidenceThreshold") ?? 70, 0, 100);
            e[P + "agenttimeoutmins"] = Clamp(Json.Int(n, "agentTimeoutMins") ?? 5, 1, 120);

            var scope = ParseScope(Json.Str(n, "contextScope", ""));
            if (scope.Count > 0) e[P + "contextscope"] = scope;
        }

        private static OptionSetValueCollection ParseScope(string csv)
        {
            var col = new OptionSetValueCollection();
            if (string.IsNullOrWhiteSpace(csv)) return col;
            var seen = new HashSet<int>();
            foreach (var part in csv.Split(','))
            {
                int v;
                if (!int.TryParse(part.Trim(), out v)) continue;
                if (seen.Add(v)) col.Add(new OptionSetValue(v));
            }
            return col;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }

        private static string IdOf(object o)
        {
            var d = Json.Obj(o);
            return d == null ? null : Json.Str(d, "id");
        }

        private static string NameOf(object o)
        {
            if (o == null) return null;
            var s = o as string;
            if (s != null) return s;
            var d = Json.Obj(o);
            return d == null ? null : Json.Str(d, "name");
        }

        private static decimal? Dec(Dictionary<string, object> d, string key)
        {
            var v = Json.Get(d, key);
            if (v == null) return null;
            if (v is double) return (decimal)(double)v;
            decimal x;
            if (v is string && decimal.TryParse((string)v, NumberStyles.Any, CultureInfo.InvariantCulture, out x))
                return x;
            return null;
        }

        private static Entity FindBpf(IOrganizationService svc, string name)
        {
            var q = new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet("workflowid", "name", "uniquename"),
                TopCount = 1,
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
            return r.Entities.Count > 0 ? r.Entities[0].Id : Guid.Empty;
        }
    }
}
