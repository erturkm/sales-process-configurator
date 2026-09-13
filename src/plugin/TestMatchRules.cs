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
    /// Custom API spc_TestMatchRules. Takes the targeting filter the maker is drawing and answers the
    /// only question they actually care about: which of my real cases would this have caught? Returns
    /// a plain English summary, the equivalent FetchXML, a match count and a handful of sample cases.
    /// </summary>
    public class TestMatchRules : IPlugin
    {
        private const string P = "spc_";
        private const int SampleSize = 8;

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);

            var raw = ctx.InputParameters.Contains("Rules") ? (string)ctx.InputParameters["Rules"] : null;
            var rules = ParseRules(raw);

            var query = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet("name", "estimatedvalue", "createdon", "statecode"),
                TopCount = 200,
                // A deal with four qualifying product lines is still one deal. Without this the
                // join multiplies the row out and the preview reports a match count that is really
                // a line count.
                Distinct = true,
                Orders = { new OrderExpression("createdon", OrderType.Descending) }
            };
            query.Criteria.FilterOperator = LogicalOperator.And;

            var unsupported = new List<string>();
            var links = new Dictionary<string, LinkEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in rules.GroupBy(r => r.Group).OrderBy(g => g.Key))
            {
                var f = new FilterExpression(LogicalOperator.Or);
                foreach (var r in group)
                {
                    var c = Condition(r);
                    if (c == null) { unsupported.Add(r.Label); continue; }

                    // A dotted rule targets the record behind a lookup. A hierarchy rule needs the
                    // same join for a different reason: Dataverse only accepts "under" against the
                    // primary key of the hierarchical table, never against the lookup on the deal.
                    if (IsLineRule(r))
                    {
                        // A negative test over a collection cannot be expressed as an inner join:
                        // joining would find deals that have SOME line that is not under X, when
                        // the rule means deals where NO line is under X. Rather than show a count
                        // that is quietly wrong, the preview says it cannot draw this one. The
                        // runtime evaluates it correctly over the whole basket.
                        if (IsNegative(r.Operator)) { unsupported.Add(r.Label); continue; }

                        var lineLink = LineLinkFor(query, links, r);
                        if (lineLink == null) { unsupported.Add(r.Label); continue; }
                        c.EntityName = lineLink.EntityAlias;
                        f.AddCondition(c);
                        continue;
                    }

                    var hop = HopOf(r);
                    if (hop != null)
                    {
                        var link = LinkFor(query, links, hop, r);
                        if (link == null) { unsupported.Add(r.Label); continue; }
                        c.EntityName = link.EntityAlias;
                    }
                    f.AddCondition(c);
                }
                if (f.Conditions.Count > 0) query.Criteria.AddFilter(f);
            }

            var sb = new StringBuilder();
            sb.Append("{").Append(Json.Q("summary")).Append(":").Append(Json.Q(Summary(rules)));
            sb.Append(",").Append(Json.Q("fetchXml")).Append(":").Append(Json.Q(Fetch(rules)));
            sb.Append(",").Append(Json.Q("unsupported")).Append(":[");
            for (var i = 0; i < unsupported.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(Json.Q(unsupported[i]));
            }
            sb.Append("]");

            if (rules.Count == 0)
            {
                sb.Append(",").Append(Json.Q("matchCount")).Append(":0")
                  .Append(",").Append(Json.Q("capped")).Append(":false")
                  .Append(",").Append(Json.Q("samples")).Append(":[]}");
                ctx.OutputParameters["Result"] = sb.ToString();
                return;
            }

            var found = svc.RetrieveMultiple(query).Entities;
            sb.Append(",").Append(Json.Q("matchCount")).Append(":")
              .Append(found.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append(",").Append(Json.Q("capped")).Append(":")
              .Append(found.Count >= query.TopCount.Value ? "true" : "false");
            sb.Append(",").Append(Json.Q("samples")).Append(":[");
            for (var i = 0; i < Math.Min(SampleSize, found.Count); i++)
            {
                if (i > 0) sb.Append(",");
                var e = found[i];
                var created = e.GetAttributeValue<DateTime?>("createdon");
                var value = e.GetAttributeValue<Money>("estimatedvalue");
                sb.Append("{").Append(Json.Q("id")).Append(":").Append(Json.Q(e.Id.ToString()))
                  .Append(",").Append(Json.Q("title")).Append(":")
                  .Append(Json.Q(e.GetAttributeValue<string>("name") ?? "(unnamed)"))
                  .Append(",").Append(Json.Q("number")).Append(":")
                  .Append(Json.Q(value == null ? ""
                      : value.Value.ToString("N0", CultureInfo.InvariantCulture)))
                  .Append(",").Append(Json.Q("createdOn")).Append(":")
                  .Append(Json.Q(created.HasValue
                      ? created.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : ""))
                  .Append("}");
            }
            sb.Append("]}");

            ctx.OutputParameters["Result"] = sb.ToString();
        }

        // ------------------------------------------------------------------ model

        private class Rule
        {
            public string Attribute;
            public string Label;
            public int Operator;
            public string Value;
            public string ValueLabel;
            public int Group;
        }

        private static List<Rule> ParseRules(string raw)
        {
            var list = new List<Rule>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            var arr = Json.Arr(Json.Parse(raw));
            foreach (var item in arr)
            {
                var d = Json.Obj(item);
                if (d == null) continue;
                var attr = Json.Str(d, "attribute", "");
                if (string.IsNullOrWhiteSpace(attr)) continue;
                list.Add(new Rule
                {
                    Attribute = attr,
                    Label = Json.Str(d, "attributeLabel", attr),
                    Operator = Json.Int(d, "operator") ?? 1,
                    Value = Json.Str(d, "value", ""),
                    ValueLabel = Json.Str(d, "valueLabel", ""),
                    Group = Json.Int(d, "group") ?? 1
                });
            }
            return list;
        }

        // ------------------------------------------------------------------ translation

        private static ConditionExpression Condition(Rule r)
        {
            var values = Split(r.Value);
            // Once a rule is bound to its join, the condition names the column on the far table,
            // not the path (or lookup) used to get there.
            var attr = ColumnOf(r);
            switch (r.Operator)
            {
                case 1:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.Equal, values[0]);
                case 2:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.NotEqual, values[0]);
                case 3:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.In, values.Cast<object>().ToArray());
                case 4:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.NotIn, values.Cast<object>().ToArray());
                case 5:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.Like, "%" + r.Value + "%");
                case 6:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.Like, r.Value + "%");
                case 7:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.GreaterThan, values[0]);
                case 8:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.LessThan, values[0]);
                case 9:
                    return new ConditionExpression(attr, ConditionOperator.Null);
                case 10:
                    return new ConditionExpression(attr, ConditionOperator.NotNull);
                case 11:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.UnderOrEqual,
                                                  values.Cast<object>().ToArray());
                case 12:
                    return values.Count == 0 ? null
                        : new ConditionExpression(attr, ConditionOperator.NotUnder,
                                                  values.Cast<object>().ToArray());
                default:
                    return null;
            }
        }

        private static List<string> Split(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return new List<string>();
            return v.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        // ------------------------------------------------------------------ product lines

        private const string LinePrefix = "productlines.";

        private static bool IsLineRule(Rule r)
        {
            return (r.Attribute ?? string.Empty)
                   .StartsWith(LinePrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Operators whose meaning over a collection is "no line matches".</summary>
        private static bool IsNegative(int op) { return op == 2 || op == 4 || op == 9 || op == 12; }

        /// <summary>
        /// Joins the deal to its product lines, and on to the product itself when the rule walks the
        /// product family tree - "under" is only legal against the hierarchical table's own primary
        /// key, so the lookup on the line is not enough.
        /// </summary>
        private static LinkEntity LineLinkFor(QueryExpression query,
                                              Dictionary<string, LinkEntity> cache, Rule r)
        {
            var column = r.Attribute.Substring(LinePrefix.Length);
            var hierarchy = IsHierarchyOp(r.Operator);
            var key = hierarchy ? "productlines#product" : "productlines";

            LinkEntity existing;
            if (cache.TryGetValue(key, out existing)) return existing;

            LinkEntity lines;
            if (!cache.TryGetValue("productlines", out lines))
            {
                lines = new LinkEntity("opportunity", "opportunityproduct",
                                       "opportunityid", "opportunityid", JoinOperator.Inner)
                {
                    EntityAlias = "line"
                };
                query.LinkEntities.Add(lines);
                cache["productlines"] = lines;
            }
            if (!hierarchy) return lines;

            var prod = lines.AddLink("product", column, "productid", JoinOperator.Inner);
            prod.EntityAlias = "lineproduct";
            cache[key] = prod;
            return prod;
        }

        // ------------------------------------------------------------------ related record joins

        private static bool IsHierarchyOp(int op) { return op == 11 || op == 12; }

        /// <summary>
        /// The lookup a rule has to traverse, or null when it can be evaluated on the case itself.
        /// Dotted rules name their hop; hierarchy rules imply one, because "under" is only legal
        /// against the primary key of the hierarchical table.
        /// </summary>
        private static string HopOf(Rule r)
        {
            var dot = r.Attribute.IndexOf('.');
            if (dot > 0) return r.Attribute.Substring(0, dot);
            return IsHierarchyOp(r.Operator) ? r.Attribute : null;
        }

        /// <summary>The column a condition names once its rule has been bound to a join.</summary>
        private static string ColumnOf(Rule r)
        {
            var dot = r.Attribute.IndexOf('.');
            if (dot > 0) return r.Attribute.Substring(dot + 1);
            if (IsHierarchyOp(r.Operator))
            {
                var target = TargetFor(r.Attribute, "");
                return target == null ? r.Attribute : target + "id";
            }
            return r.Attribute;
        }

        /// <summary>
        /// Adds (or reuses) the join a rule needs. A polymorphic lookup such as customerid can point
        /// at more than one table, and a query can only join one of them, so the preview joins the
        /// target that actually carries the column, preferring contact when both do. Runtime matching
        /// has no such limit because it reads whichever record the case really points at.
        /// </summary>
        private static LinkEntity LinkFor(QueryExpression query, Dictionary<string, LinkEntity> cache,
                                          string hop, Rule r)
        {
            LinkEntity existing;
            if (cache.TryGetValue(hop, out existing)) return existing;

            var dot = r.Attribute.IndexOf('.');
            var column = dot > 0 ? r.Attribute.Substring(dot + 1) : "";
            var target = TargetFor(hop, column);
            if (target == null) return null;

            var link = new LinkEntity("opportunity", target, hop, target + "id", JoinOperator.Inner)
            {
                EntityAlias = "rel_" + hop
            };
            query.LinkEntities.Add(link);
            cache[hop] = link;
            return link;
        }

        private static string TargetFor(string hop, string column)
        {
            // The rule builder only offers hops off the case that resolve to a customer or a user,
            // so a short static map keeps this off the metadata service on every preview call.
            switch (hop.ToLowerInvariant())
            {
                case "customerid":
                case "primarycontactid":
                case "responsiblecontactid":
                    return column.StartsWith("account", StringComparison.OrdinalIgnoreCase)
                        ? "account" : "contact";
                case "parentaccountid":
                case "accountid":
                    return "account";
                case "contactid":
                    return "contact";
                case "ownerid":
                    return "systemuser";
                case "subjectid":
                    return "subject";
                default:
                    return null;
            }
        }

        /// <summary>Builds the same filter as FetchXML so a maker can see and reuse the query.</summary>
        private static string Fetch(List<Rule> rules)
        {
            var sb = new StringBuilder();
            sb.Append("<fetch top=\"50\"><entity name=\"opportunity\">");
            sb.Append("<attribute name=\"name\" /><attribute name=\"estimatedvalue\" />");
            sb.Append("<filter type=\"and\">");
            foreach (var g in rules.GroupBy(r => r.Group).OrderBy(g => g.Key))
            {
                var direct = g.Where(x => !IsLineRule(x) && HopOf(x) == null).ToList();
                if (direct.Count == 0) continue;   // group is entirely joins, emitted below
                sb.Append("<filter type=\"or\">");
                foreach (var r in direct) AppendCondition(sb, r);
                sb.Append("</filter>");
            }
            sb.Append("</filter>");

            AppendLineLinks(sb, rules);

            // Joined rules are grouped by the lookup they traverse so each hop is emitted once.
            foreach (var hop in rules.Where(r => !IsLineRule(r) && HopOf(r) != null)
                                     .GroupBy(HopOf, StringComparer.OrdinalIgnoreCase))
            {
                var sample = hop.First();
                var dot = sample.Attribute.IndexOf('.');
                var target = TargetFor(hop.Key, dot > 0 ? sample.Attribute.Substring(dot + 1) : "");
                if (target == null) continue;
                sb.Append("<link-entity name=\"").Append(Xml(target))
                  .Append("\" from=\"").Append(Xml(target)).Append("id\" to=\"").Append(Xml(hop.Key))
                  .Append("\" alias=\"rel_").Append(Xml(hop.Key)).Append("\" link-type=\"inner\">");
                foreach (var byGroup in hop.GroupBy(r => r.Group).OrderBy(x => x.Key))
                {
                    sb.Append("<filter type=\"or\">");
                    foreach (var r in byGroup) AppendCondition(sb, r);
                    sb.Append("</filter>");
                }
                sb.Append("</link-entity>");
            }

            sb.Append("</entity></fetch>");
            return sb.ToString();
        }

        /// <summary>
        /// Emits the product line join. Hierarchy rules need a second hop on to the product itself,
        /// so they sit in a nested link; everything else is tested on the line row. Negative rules
        /// are left out for the same reason the query builder drops them - an inner join cannot
        /// express "no line matches".
        /// </summary>
        private static void AppendLineLinks(StringBuilder sb, List<Rule> rules)
        {
            var line = rules.Where(r => IsLineRule(r) && !IsNegative(r.Operator)).ToList();
            if (line.Count == 0) return;

            sb.Append("<link-entity name=\"opportunityproduct\" from=\"opportunityid\" ")
              .Append("to=\"opportunityid\" alias=\"line\" link-type=\"inner\">");

            var flat = line.Where(r => !IsHierarchyOp(r.Operator)).ToList();
            foreach (var g in flat.GroupBy(r => r.Group).OrderBy(x => x.Key))
            {
                sb.Append("<filter type=\"or\">");
                foreach (var r in g) AppendCondition(sb, r);
                sb.Append("</filter>");
            }

            var tree = line.Where(r => IsHierarchyOp(r.Operator)).ToList();
            if (tree.Count > 0)
            {
                sb.Append("<link-entity name=\"product\" from=\"productid\" to=\"productid\" ")
                  .Append("alias=\"lineproduct\" link-type=\"inner\">");
                foreach (var g in tree.GroupBy(r => r.Group).OrderBy(x => x.Key))
                {
                    sb.Append("<filter type=\"or\">");
                    foreach (var r in g) AppendCondition(sb, r);
                    sb.Append("</filter>");
                }
                sb.Append("</link-entity>");
            }

            sb.Append("</link-entity>");
        }

        private static void AppendCondition(StringBuilder sb, Rule r)
        {
            sb.Append("<condition attribute=\"").Append(Xml(ColumnOf(r)))
              .Append("\" operator=\"").Append(FetchOp(r.Operator)).Append("\"");
            var values = Split(r.Value);
            if (r.Operator == 9 || r.Operator == 10) { sb.Append(" />"); return; }
            if (r.Operator == 3 || r.Operator == 4 || IsHierarchyOp(r.Operator))
            {
                sb.Append(">");
                foreach (var v in values) sb.Append("<value>").Append(Xml(v)).Append("</value>");
                sb.Append("</condition>");
                return;
            }
            var single = r.Value;
            if (r.Operator == 5) single = "%" + single + "%";
            if (r.Operator == 6) single = single + "%";
            sb.Append(" value=\"").Append(Xml(single)).Append("\" />");
        }

        private static string FetchOp(int op)
        {
            switch (op)
            {
                case 1: return "eq";
                case 2: return "ne";
                case 3: return "in";
                case 4: return "not-in";
                case 5: return "like";
                case 6: return "like";
                case 7: return "gt";
                case 8: return "lt";
                case 9: return "null";
                case 10: return "not-null";
                case 11: return "under-or-equal";
                case 12: return "not-under";
                default: return "eq";
            }
        }

        private static string Xml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string Summary(List<Rule> rules)
        {
            if (rules.Count == 0)
                return "No conditions yet, so this process would never be selected automatically.";
            var parts = new List<string>();
            foreach (var g in rules.GroupBy(r => r.Group).OrderBy(g => g.Key))
            {
                var any = g.Select(r => r.Label + " " + SaveProcessGraph.OperatorWord(r.Operator)
                    + Tail(r)).ToList();
                parts.Add(any.Count == 1 ? any[0] : "(" + string.Join(" or ", any.ToArray()) + ")");
            }
            return "Apply to a new case when " + string.Join(" and ", parts.ToArray()) + ".";
        }

        private static string Tail(Rule r)
        {
            if (r.Operator == 9 || r.Operator == 10) return "";
            var v = string.IsNullOrWhiteSpace(r.ValueLabel) ? r.Value : r.ValueLabel;
            return " " + v;
        }
    }
}
