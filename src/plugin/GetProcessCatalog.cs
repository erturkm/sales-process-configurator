using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Custom API spc_GetProcessCatalog. Returns a JSON description of everything the Process Copilot
    /// is allowed to reference: business process flows and their stages, teams, queues, SLAs, document
    /// packages, targetable case attributes with their option values, and the supported enumerations.
    /// </summary>
    public class GetProcessCatalog : IPlugin
    {
        private const string P = "spc_";

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);

            var sb = new StringBuilder();
            sb.Append("{");

            sb.Append(Json.Q("businessProcessFlows")).Append(":[");
            var bpfs = svc.RetrieveMultiple(new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet("workflowid", "name", "uniquename", "primaryentity"),
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("category", ConditionOperator.Equal, 4),
                        new ConditionExpression("statecode", ConditionOperator.Equal, 1),
                        new ConditionExpression("type", ConditionOperator.Equal, 1),
                        new ConditionExpression("primaryentity", ConditionOperator.Equal, "opportunity")
                    }
                }
            });
            var firstB = true;
            foreach (var b in bpfs.Entities)
            {
                if (!firstB) sb.Append(",");
                firstB = false;
                sb.Append("{").Append(Json.Q("name")).Append(":")
                  .Append(Json.Q(b.GetAttributeValue<string>("name")))
                  .Append(",").Append(Json.Q("id")).Append(":").Append(Json.Q(b.Id.ToString()))
                  .Append(",").Append(Json.Q("entity")).Append(":")
                  .Append(Json.Q(b.GetAttributeValue<string>("primaryentity")))
                  .Append(",").Append(Json.Q("stages")).Append(":[");
                var firstS = true;
                foreach (var s in ProcessRuntime.OrderedStages(svc, b.Id))
                {
                    if (!firstS) sb.Append(",");
                    firstS = false;
                    sb.Append("{").Append(Json.Q("name")).Append(":")
                      .Append(Json.Q(s.GetAttributeValue<string>("stagename")))
                      .Append(",").Append(Json.Q("id")).Append(":").Append(Json.Q(s.Id.ToString()))
                      .Append("}");
                }
                sb.Append("]}");
            }
            sb.Append("]");

            AppendNameList(sb, svc, "teams", "team", "name", "teamid",
                new ConditionExpression("teamtype", ConditionOperator.Equal, 0));
            AppendNameList(sb, svc, "queues", "queue", "name", "queueid",
                new ConditionExpression("queueviewtype", ConditionOperator.Equal, 0));
            AppendNameList(sb, svc, "slas", "sla", "name", "slaid", null);
            AppendNameList(sb, svc, "documentPackages", P + "documentpackage", P + "name",
                P + "documentpackageid", new ConditionExpression("statecode", ConditionOperator.Equal, 0));
            AppendSecurityRoles(sb, svc);
            AppendAgents(sb, svc);
            AppendTemplates(sb, svc);
            AppendCaseAttributes(sb, svc);
            AppendRelatedAttributes(sb, svc);
            AppendLineAttributes(sb, svc);
            AppendProductTree(sb, svc);

            sb.Append(",").Append(Json.Q("operators")).Append(":[")
              .Append("{\"value\":1,\"label\":\"is\"},{\"value\":2,\"label\":\"is not\"},")
              .Append("{\"value\":3,\"label\":\"is any of\"},{\"value\":4,\"label\":\"is none of\"},")
              .Append("{\"value\":5,\"label\":\"contains\"},{\"value\":6,\"label\":\"begins with\"},")
              .Append("{\"value\":7,\"label\":\"is greater than\"},{\"value\":8,\"label\":\"is less than\"},")
              .Append("{\"value\":9,\"label\":\"is empty\"},{\"value\":10,\"label\":\"is not empty\"},")
              .Append("{\"value\":11,\"label\":\"is at or under\",\"hierarchical\":true},")
              .Append("{\"value\":12,\"label\":\"is not under\",\"hierarchical\":true}]");
            sb.Append(",").Append(Json.Q("assignTypes")).Append(":")
              .Append("[\"team\",\"user\",\"role\",\"queue\",\"manager of deal owner\",\"deal owner\"]");
            sb.Append(",").Append(Json.Q("matchLogic")).Append(":")
              .Append("\"Rules that share a groupNumber are OR'ed together; separate groups are AND'ed. ")
              .Append("Templates are evaluated in ascending rank order and the first match wins. ")
              .Append("An attribute may be a plain opportunity column, a one hop path such as ")
              .Append("customerid.spc_customersegment to target the customer behind the deal, or ")
              .Append("productlines.productid to target what is being sold. A deal carries several ")
              .Append("product lines, so a productlines rule is true when ANY line satisfies it, and ")
              .Append("a negative one is true only when NO line does. The product catalogue is a ")
              .Append("tree, so prefer 'is at or under' over 'is' to catch every product beneath a ")
              .Append("family rather than naming each SKU.\"");

            sb.Append("}");

            ctx.OutputParameters["Catalog"] = sb.ToString();
        }

        /// <summary>
        /// Enough detail about every saved process for the designer's open dialog to render a useful
        /// card: rank, status, mapped flow and how much is actually configured inside it.
        /// </summary>
        private static void AppendTemplates(StringBuilder sb, IOrganizationService svc)
        {
            var q = new QueryExpression(P + "salesprocesstemplate")
            {
                ColumnSet = new ColumnSet(true),
                Orders = { new OrderExpression(P + "rank", OrderType.Ascending),
                           new OrderExpression(P + "name", OrderType.Ascending) }
            };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var templates = svc.RetrieveMultiple(q).Entities;

            var taskCounts = CountBy(svc, P + "processtask", P + "template");
            var ruleCounts = CountBy(svc, P + "matchrule", P + "template");

            sb.Append(",").Append(Json.Q("existingTemplates")).Append(":[");
            var first = true;
            foreach (var t in templates)
            {
                if (!first) sb.Append(",");
                first = false;
                var st = t.GetAttributeValue<OptionSetValue>(P + "publishstatus");
                var pkg = t.GetAttributeValue<EntityReference>(P + "documentpackage");
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(t.Id.ToString()))
                  .Append(",").Append(Json.Q("name")).Append(":")
                  .Append(Json.Q(t.GetAttributeValue<string>(P + "name")))
                  .Append(",").Append(Json.Q("description")).Append(":")
                  .Append(Json.Q(t.GetAttributeValue<string>(P + "description") ?? ""))
                  .Append(",").Append(Json.Q("rank")).Append(":")
                  .Append((t.GetAttributeValue<int?>(P + "rank") ?? 50).ToString(CultureInfo.InvariantCulture))
                  .Append(",").Append(Json.Q("publishStatus")).Append(":")
                  .Append((st == null ? 1 : st.Value).ToString(CultureInfo.InvariantCulture))
                  .Append(",").Append(Json.Q("bpfName")).Append(":")
                  .Append(Json.Q(t.GetAttributeValue<string>(P + "bpfname") ?? ""))
                  .Append(",").Append(Json.Q("packageName")).Append(":")
                  .Append(Json.Q(pkg == null ? "" : pkg.Name ?? ""))
                  .Append(",").Append(Json.Q("appliedCount")).Append(":")
                  .Append((t.GetAttributeValue<int?>(P + "appliedcount") ?? 0).ToString(CultureInfo.InvariantCulture))
                  .Append(",").Append(Json.Q("taskCount")).Append(":").Append(Count(taskCounts, t.Id))
                  .Append(",").Append(Json.Q("ruleCount")).Append(":").Append(Count(ruleCounts, t.Id))
                  .Append("}");
            }
            sb.Append("]");
        }

        private static string Count(Dictionary<Guid, int> d, Guid id)
        {
            int n;
            return (d.TryGetValue(id, out n) ? n : 0).ToString(CultureInfo.InvariantCulture);
        }

        private static Dictionary<Guid, int> CountBy(IOrganizationService svc, string entity, string lookup)
        {
            var map = new Dictionary<Guid, int>();
            var q = new QueryExpression(entity) { ColumnSet = new ColumnSet(lookup) };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            foreach (var e in svc.RetrieveMultiple(q).Entities)
            {
                var r = e.GetAttributeValue<EntityReference>(lookup);
                if (r == null) continue;
                int n;
                map[r.Id] = map.TryGetValue(r.Id, out n) ? n + 1 : 1;
            }
            return map;
        }

        /// <summary>
        /// Agents offered in the designer picker. Only published, non-system agents: the
        /// environment carries dozens of first-party msdyn_* agents that a business user must
        /// never pick, and an unpublished agent fails at runtime with an opaque 404.
        /// </summary>
        private static void AppendAgents(StringBuilder sb, IOrganizationService svc)
        {
            var q = new QueryExpression("bot")
            {
                ColumnSet = new ColumnSet("name", "botid", "schemaname", "publishedon"),
                Orders = { new OrderExpression("name", OrderType.Ascending) },
                TopCount = 500,
            };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            sb.Append(",").Append(Json.Q("agents")).Append(":[");
            var first = true;
            foreach (var b in svc.RetrieveMultiple(q).Entities)
            {
                var schema = b.GetAttributeValue<string>("schemaname") ?? "";
                if (!b.Contains("publishedon")) continue;
                if (schema.StartsWith("msdyn_", StringComparison.OrdinalIgnoreCase)
                    || schema.StartsWith("msfp_", StringComparison.OrdinalIgnoreCase)
                    || schema.StartsWith("mspp_", StringComparison.OrdinalIgnoreCase)
                    || schema.StartsWith("msgpt_", StringComparison.OrdinalIgnoreCase)) continue;

                if (!first) sb.Append(",");
                first = false;
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(b.Id.ToString()))
                  .Append(",").Append(Json.Q("name")).Append(":")
                  .Append(Json.Q(b.GetAttributeValue<string>("name")))
                  .Append(",").Append(Json.Q("schemaName")).Append(":").Append(Json.Q(schema))
                  .Append("}");
            }
            sb.Append("]");
        }

        private static void AppendSecurityRoles(StringBuilder sb, IOrganizationService svc)
        {            var q = new QueryExpression("role") { ColumnSet = new ColumnSet("name", "roleid") };
            q.Criteria.AddCondition("parentroleid", ConditionOperator.Null);
            q.Orders.Add(new OrderExpression("name", OrderType.Ascending));
            sb.Append(",").Append(Json.Q("roles")).Append(":[");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var first = true;
            foreach (var e in svc.RetrieveMultiple(q).Entities)
            {
                var n = e.GetAttributeValue<string>("name");
                if (string.IsNullOrEmpty(n) || !seen.Add(n)) continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{").Append(Json.Q("name")).Append(":").Append(Json.Q(n))
                  .Append(",").Append(Json.Q("id")).Append(":").Append(Json.Q(e.Id.ToString()))
                  .Append("}");
            }
            sb.Append("]");
        }

        /// <summary>
        /// Every case field a business user could sensibly filter on, read from live metadata so the
        /// rule builder offers the org's own custom columns without anyone editing this plug-in.
        /// Choice fields carry their options inline so the value picker never has to guess.
        /// </summary>
        private static void AppendCaseAttributes(StringBuilder sb, IOrganizationService svc)
        {
            var rows = UsefulAttributes(svc, "opportunity");
            var targets = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
            sb.Append(",").Append(Json.Q("caseAttributes")).Append(":[");
            var first = true;
            foreach (var a in rows)
            {
                if (!first) sb.Append(",");
                first = false;
                AppendAttribute(sb, svc, a, targets, a.LogicalName,
                    a.DisplayName.UserLocalizedLabel.Label, null);
            }
            sb.Append("]");
        }

        /// <summary>
        /// Attributes reachable one hop from the case. Rules address these with a dotted path such as
        /// customerid.spc_customersegment, which lets a process target a property of the customer
        /// (segment, tier, risk rating) instead of requiring that value to be copied onto every case.
        /// </summary>
        private static void AppendRelatedAttributes(StringBuilder sb, IOrganizationService svc)
        {
            var hops = new[]
            {
                new { Path = "customerid", Label = "Customer", Entities = new[] { "contact", "account" } },
                new { Path = "primarycontactid", Label = "Contact", Entities = new[] { "contact" } },
                new { Path = "ownerid", Label = "Owner", Entities = new[] { "systemuser" } },
            };

            var targets = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
            sb.Append(",").Append(Json.Q("relatedAttributes")).Append(":[");
            var first = true;
            foreach (var hop in hops)
            {
                // A polymorphic lookup exposes the union of its targets. Where both sides define the
                // same column (segment on contact and on account) it is listed once.
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entity in hop.Entities)
                {
                    foreach (var a in UsefulAttributes(svc, entity))
                    {
                        var path = hop.Path + "." + a.LogicalName;
                        if (!seen.Add(path)) continue;
                        if (!first) sb.Append(",");
                        first = false;
                        AppendAttribute(sb, svc, a, targets, path,
                            hop.Label + " \u203a " + a.DisplayName.UserLocalizedLabel.Label, entity);
                    }
                }
            }
            sb.Append("]");
        }

        /// <summary>
        /// The product family tree, flattened with a parent pointer and a path so the rule builder can
        /// render it without a second round trip. This is what a sales process targets: the same
        /// shape as a case subject tree, so a rule on a family should normally use "is at or under"
        /// to catch every product beneath it rather than naming each SKU.
        /// </summary>
        private static void AppendProductTree(StringBuilder sb, IOrganizationService svc)
        {
            var q = new QueryExpression("product")
            {
                ColumnSet = new ColumnSet("productid", "name", "parentproductid", "productstructure"),
                Orders = { new OrderExpression("name", OrderType.Ascending) }
            };
            q.Criteria.AddCondition("statecode", ConditionOperator.NotEqual, 2);   // not retired
            var rows = svc.RetrieveMultiple(q).Entities;

            var byId = new Dictionary<Guid, Entity>();
            foreach (var s in rows) byId[s.Id] = s;

            sb.Append(",").Append(Json.Q("products")).Append(":[");
            var first = true;
            foreach (var s in rows)
            {
                if (!first) sb.Append(",");
                first = false;
                var parent = s.GetAttributeValue<EntityReference>("parentproductid");

                var names = new List<string>();
                var depth = 0;
                var walk = s;
                var guard = new HashSet<Guid>();
                while (walk != null && guard.Add(walk.Id) && depth < 12)
                {
                    names.Insert(0, walk.GetAttributeValue<string>("name") ?? "");
                    var pr = walk.GetAttributeValue<EntityReference>("parentproductid");
                    if (pr == null) break;
                    Entity next;
                    if (!byId.TryGetValue(pr.Id, out next)) break;
                    walk = next;
                    depth++;
                }

                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(s.Id.ToString()))
                  .Append(",").Append(Json.Q("title")).Append(":")
                  .Append(Json.Q(s.GetAttributeValue<string>("name") ?? ""))
                  .Append(",").Append(Json.Q("parentId")).Append(":")
                  .Append(parent == null ? "null" : Json.Q(parent.Id.ToString()))
                  .Append(",").Append(Json.Q("depth")).Append(":")
                  .Append(depth.ToString(CultureInfo.InvariantCulture))
                  .Append(",").Append(Json.Q("path")).Append(":")
                  .Append(Json.Q(string.Join(" \u203a ", names.ToArray())))
                  .Append("}");
            }
            sb.Append("]");
        }

        /// <summary>Advanced-find visible, labelled attributes with a value editor we can render.</summary>
        private static List<Microsoft.Xrm.Sdk.Metadata.AttributeMetadata> UsefulAttributes(
            IOrganizationService svc, string entity)
        {
            var rows = new List<Microsoft.Xrm.Sdk.Metadata.AttributeMetadata>();
            Microsoft.Xrm.Sdk.Metadata.EntityMetadata md;
            try
            {
                var req = new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest
                {
                    LogicalName = entity,
                    EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Attributes,
                    RetrieveAsIfPublished = true
                };
                md = ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)svc.Execute(req)).EntityMetadata;
            }
            catch (Exception) { return rows; }

            foreach (var a in md.Attributes)
            {
                if (a.IsValidForAdvancedFind == null || a.IsValidForAdvancedFind.Value == false) continue;
                if (a.AttributeOf != null) continue;
                if (a.DisplayName == null || a.DisplayName.UserLocalizedLabel == null) continue;
                if (string.IsNullOrEmpty(a.DisplayName.UserLocalizedLabel.Label)) continue;
                if (Kind(a) == null) continue;
                rows.Add(a);
            }
            rows.Sort((x, y) => string.Compare(x.DisplayName.UserLocalizedLabel.Label,
                y.DisplayName.UserLocalizedLabel.Label, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        /// <summary>Emits one attribute descriptor, shared by the case and related attribute lists.</summary>
        /// <summary>Tables that parent themselves, so a rule on a node can mean "and everything under it".</summary>
        private static bool IsTreeEntity(string entity)
        {
            return string.Equals(entity, "product", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity, "account", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entity, "subject", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The product lines on the deal, addressed as productlines.&lt;column&gt;. These are not
        /// opportunity columns at all, which is exactly why they have to be published deliberately:
        /// a case has one subject, a deal has a basket, so these rules read "any line on this deal"
        /// and their negatives read "no line on this deal".
        /// </summary>
        private static void AppendLineAttributes(StringBuilder sb, IOrganizationService svc)
        {
            var rows = UsefulAttributes(svc, "opportunityproduct");
            var targets = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
            sb.Append(",").Append(Json.Q("lineAttributes")).Append(":[");
            var first = true;
            foreach (var a in rows)
            {
                if (!first) sb.Append(",");
                first = false;
                AppendAttribute(sb, svc, a, targets, "productlines." + a.LogicalName,
                    "Product line \u203a " + a.DisplayName.UserLocalizedLabel.Label,
                    "opportunityproduct");
            }
            sb.Append("]");
        }

        private static void AppendAttribute(StringBuilder sb, IOrganizationService svc,
            Microsoft.Xrm.Sdk.Metadata.AttributeMetadata a, Dictionary<string, TargetInfo> targets,
            string logicalName, string label, string ownerEntity)
        {
            sb.Append("{").Append(Json.Q("logicalName")).Append(":").Append(Json.Q(logicalName))
              .Append(",").Append(Json.Q("label")).Append(":").Append(Json.Q(label))
              .Append(",").Append(Json.Q("kind")).Append(":").Append(Json.Q(Kind(a)))
              .Append(",").Append(Json.Q("custom")).Append(":")
              .Append(a.IsCustomAttribute.GetValueOrDefault() ? "true" : "false");
            if (ownerEntity != null)
                sb.Append(",").Append(Json.Q("ownerEntity")).Append(":").Append(Json.Q(ownerEntity));
            sb.Append(",").Append(Json.Q("targets")).Append(":[");
            var lk = a as Microsoft.Xrm.Sdk.Metadata.LookupAttributeMetadata;
            var hierarchical = false;
            if (lk != null && lk.Targets != null)
                for (var i = 0; i < lk.Targets.Length; i++)
                {
                    if (i > 0) sb.Append(",");
                    AppendTarget(sb, svc, lk.Targets[i], targets);
                    if (ownerEntity != null &&
                        string.Equals(lk.Targets[i], ownerEntity, StringComparison.OrdinalIgnoreCase))
                        hierarchical = true;
                }
            // A lookup into a self-parented table is a tree, which is what unlocks the
            // "is at or under" operator in the rule builder. Product is the important one here:
            // it is to a sales process what subject is to a case process.
            if (!hierarchical && lk != null && lk.Targets != null && lk.Targets.Length == 1)
                hierarchical = IsTreeEntity(lk.Targets[0]);
            sb.Append("],").Append(Json.Q("hierarchical")).Append(":")
              .Append(hierarchical ? "true" : "false")
              .Append(",").Append(Json.Q("options")).Append(":[");
            var en = a as Microsoft.Xrm.Sdk.Metadata.EnumAttributeMetadata;
            if (en != null && en.OptionSet != null)
            {
                var n = 0;
                foreach (var o in en.OptionSet.Options)
                {
                    if (n++ > 0) sb.Append(",");
                    var lbl = o.Label != null && o.Label.UserLocalizedLabel != null
                        ? o.Label.UserLocalizedLabel.Label : "";
                    sb.Append("{").Append(Json.Q("value")).Append(":")
                      .Append(o.Value.GetValueOrDefault().ToString(CultureInfo.InvariantCulture))
                      .Append(",").Append(Json.Q("label")).Append(":").Append(Json.Q(lbl)).Append("}");
                }
            }
            var bl = a as Microsoft.Xrm.Sdk.Metadata.BooleanAttributeMetadata;
            if (bl != null && bl.OptionSet != null)
            {
                sb.Append("{").Append(Json.Q("value")).Append(":1,").Append(Json.Q("label")).Append(":")
                  .Append(Json.Q(LabelOf(bl.OptionSet.TrueOption, "Yes"))).Append("},");
                sb.Append("{").Append(Json.Q("value")).Append(":0,").Append(Json.Q("label")).Append(":")
                  .Append(Json.Q(LabelOf(bl.OptionSet.FalseOption, "No"))).Append("}");
            }
            sb.Append("]}");
        }

        /// <summary>
        /// Lookup targets carry the collection name and primary name column so the rule builder can
        /// offer a searchable list of the real records instead of asking makers to type a GUID.
        /// </summary>
        private static void AppendTarget(StringBuilder sb, IOrganizationService svc, string entity,
                                         Dictionary<string, TargetInfo> cache)
        {
            string set = null, nameAttr = "name", idAttr = entity + "id", label = entity;
            TargetInfo info;
            if (!cache.TryGetValue(entity, out info))
            {
                try
                {
                    var r = new Microsoft.Xrm.Sdk.Messages.RetrieveEntityRequest
                    {
                        LogicalName = entity,
                        EntityFilters = Microsoft.Xrm.Sdk.Metadata.EntityFilters.Entity,
                        RetrieveAsIfPublished = true
                    };
                    var m = ((Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse)svc.Execute(r)).EntityMetadata;
                    set = m.EntitySetName;
                    nameAttr = m.PrimaryNameAttribute ?? "name";
                    idAttr = m.PrimaryIdAttribute ?? (entity + "id");
                    if (m.DisplayName != null && m.DisplayName.UserLocalizedLabel != null
                        && !string.IsNullOrEmpty(m.DisplayName.UserLocalizedLabel.Label))
                        label = m.DisplayName.UserLocalizedLabel.Label;
                }
                catch (Exception) { }
                info = new TargetInfo { Set = set, NameAttr = nameAttr, IdAttr = idAttr, Label = label };
                cache[entity] = info;
            }

            sb.Append("{").Append(Json.Q("entity")).Append(":").Append(Json.Q(entity))
              .Append(",").Append(Json.Q("set")).Append(":").Append(Json.Q(info.Set))
              .Append(",").Append(Json.Q("nameAttr")).Append(":").Append(Json.Q(info.NameAttr))
              .Append(",").Append(Json.Q("idAttr")).Append(":").Append(Json.Q(info.IdAttr))
              .Append(",").Append(Json.Q("label")).Append(":").Append(Json.Q(info.Label))
              .Append("}");
        }

        private sealed class TargetInfo
        {
            public string Set;
            public string NameAttr;
            public string IdAttr;
            public string Label;
        }

        private static string LabelOf(Microsoft.Xrm.Sdk.Metadata.OptionMetadata o, string dflt)
        {
            if (o == null || o.Label == null || o.Label.UserLocalizedLabel == null) return dflt;
            var l = o.Label.UserLocalizedLabel.Label;
            return string.IsNullOrEmpty(l) ? dflt : l;
        }

        /// <summary>Maps a Dataverse attribute type onto the value editor the rule builder should show.</summary>
        private static string Kind(Microsoft.Xrm.Sdk.Metadata.AttributeMetadata a)
        {
            switch (a.AttributeType.GetValueOrDefault())
            {
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Picklist:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.State:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Status:
                    return "choice";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Boolean:
                    return "boolean";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Customer:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Lookup:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Owner:
                    return "lookup";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Integer:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Decimal:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Double:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Money:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.BigInt:
                    return "number";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.DateTime:
                    return "datetime";
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.String:
                case Microsoft.Xrm.Sdk.Metadata.AttributeTypeCode.Memo:
                    return "text";
                default:
                    return null;
            }
        }

        private static void AppendNameList(StringBuilder sb, IOrganizationService svc, string label,
            string entity, string nameField, string idField, ConditionExpression filter)
        {
            var q = new QueryExpression(entity) { ColumnSet = new ColumnSet(nameField, idField) };
            if (filter != null) q.Criteria.Conditions.Add(filter);
            if (entity == "queue") q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            q.Orders.Add(new OrderExpression(nameField, OrderType.Ascending));
            var res = svc.RetrieveMultiple(q);
            sb.Append(",").Append(Json.Q(label)).Append(":[");
            var first = true;
            foreach (var e in res.Entities)
            {
                var n = e.GetAttributeValue<string>(nameField);
                if (string.IsNullOrEmpty(n)) continue;
                if (entity == "team" && IsSystemTeamName(n)) continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{").Append(Json.Q("name")).Append(":").Append(Json.Q(n))
                  .Append(",").Append(Json.Q("id")).Append(":").Append(Json.Q(e.Id.ToString()))
                  .Append("}");
            }
            sb.Append("]");
        }

        /// <summary>
        /// Application user teams are named like "f16a6f32e38b4fca8f2bd906895d07db_1". They are noise
        /// in a maker picker, so they are filtered out of the catalog.
        /// </summary>
        internal static bool IsSystemTeamName(string n)
        {
            var us = n.LastIndexOf('_');
            if (us != 32 || us + 1 >= n.Length) return false;
            for (var i = 0; i < 32; i++)
            {
                var c = char.ToLowerInvariant(n[i]);
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            for (var i = us + 1; i < n.Length; i++)
                if (n[i] < '0' || n[i] > '9') return false;
            return true;
        }

    }
}
