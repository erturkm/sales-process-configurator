using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Assembles the CRM context handed to an AI agent.
    ///
    /// Three rules drive the design:
    ///   1. Never dump raw columns. Lookups and option sets are resolved to human labels,
    ///      because a model reasons over "Premier customer, Fraud Dispute" and not over
    ///      a pair of GUIDs.
    ///   2. Only what was configured. The scope mask mirrors spc_contextscope, so a
    ///      business user grants exactly the slices a task needs.
    ///   3. Nothing sensitive leaves Dataverse. Emirates ID, IBAN, card numbers and
    ///      similar identifiers are masked on the way out.
    ///
    /// Used by the cloud flow (via spc_BuildOpportunityContext) and reusable anywhere else
    /// a prompt needs grounding.
    /// </summary>
    internal static class OpportunityContext
    {
        private const string P = "spc_";

        // Mirrors the spc_contextscope option set in step41_agent_model.py.
        public const int ScopeDealCore = 1;
        public const int ScopeDealNarrative = 2;
        public const int ScopeCustomer = 3;
        public const int ScopeCustomerHistory = 4;
        public const int ScopeNotes = 5;
        public const int ScopeEmails = 6;
        public const int ScopeTasks = 7;
        public const int ScopeDocuments = 8;
        public const int ScopeSla = 9;
        public const int ScopeProductLines = 10;

        private const int MaxNotes = 10;
        private const int MaxEmails = 8;
        private const int MaxHistory = 10;
        private const int MaxLines = 25;
        private const int MaxBodyChars = 2000;
        private const int DefaultBudgetChars = 24000;

        /// <summary>Builds the context block. Returns markdown, which models follow more
        /// reliably than JSON and which a human can read in the audit record.</summary>
        public static string Build(IOrganizationService svc, Guid oppId, ISet<int> scope,
                                   int budgetChars = DefaultBudgetChars)
        {
            if (scope == null || scope.Count == 0)
                scope = new HashSet<int> { ScopeDealCore, ScopeDealNarrative, ScopeProductLines };

            var sb = new StringBuilder();
            var opp = svc.Retrieve("opportunity", oppId, new ColumnSet(
                "name", "description", "createdon", "prioritycode", "statuscode", "statecode",
                "estimatedvalue", "estimatedclosedate", "closeprobability", "totalamount",
                "budgetstatus", "purchasetimeframe", "purchaseprocess", "need",
                "opportunityratingcode", "msdyn_forecastcategory", "salesstage", "stepname",
                "currentsituation", "customerneed", "proposedsolution", "qualificationcomments",
                "customerid", "ownerid",
                P + "processsummary",
                P + "qualificationdue", P + "qualificationstatus",
                P + "closedue", P + "closestatus",
                P + "tasksopen", P + "taskstotal", P + "docsreceived", P + "docstotal"));

            if (scope.Contains(ScopeDealCore)) AppendDeal(svc, sb, opp);
            if (scope.Contains(ScopeProductLines)) AppendProductLines(svc, sb, oppId);
            if (scope.Contains(ScopeDealNarrative)) AppendNarrative(sb, opp);
            if (scope.Contains(ScopeSla)) AppendSla(svc, sb, opp);
            if (scope.Contains(ScopeCustomer)) AppendCustomer(svc, sb, opp);
            if (scope.Contains(ScopeCustomerHistory)) AppendHistory(svc, sb, opp, oppId);
            if (scope.Contains(ScopeTasks)) AppendTasks(svc, sb, oppId);
            if (scope.Contains(ScopeDocuments)) AppendDocuments(svc, sb, oppId);
            if (scope.Contains(ScopeNotes)) AppendNotes(svc, sb, oppId);
            if (scope.Contains(ScopeEmails)) AppendEmails(svc, sb, oppId);

            var text = Redact(sb.ToString());
            if (text.Length > budgetChars)
                text = text.Substring(0, budgetChars) + "\n\n[context truncated to fit budget]";
            return text;
        }

        public static ISet<int> ParseScope(object raw)
        {
            var set = new HashSet<int>();
            var col = raw as OptionSetValueCollection;
            if (col != null)
            {
                foreach (var o in col) set.Add(o.Value);
                return set;
            }
            var s = raw as string;
            if (!string.IsNullOrWhiteSpace(s))
            {
                foreach (var part in s.Split(','))
                {
                    int v;
                    if (int.TryParse(part.Trim(), out v)) set.Add(v);
                }
            }
            return set;
        }

        // ---------------------------------------------------------------- sections

        private static void AppendDeal(IOrganizationService svc, StringBuilder sb, Entity c)
        {
            sb.AppendLine("## Opportunity");
            Line(sb, "Name", c.GetAttributeValue<string>("name"));
            Money(sb, "Estimated value", c.GetAttributeValue<Money>("estimatedvalue"));
            var closeOn = c.GetAttributeValue<DateTime?>("estimatedclosedate");
            if (closeOn.HasValue)
                Line(sb, "Estimated close date",
                     closeOn.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                     + " (" + Due(closeOn.Value) + ")");
            var prob = c.GetAttributeValue<int?>("closeprobability");
            if (prob.HasValue) Line(sb, "Probability", prob.Value + "%");
            Line(sb, "Sales stage", c.GetAttributeValue<string>("stepname"));
            Line(sb, "Status", OptionLabel(svc, "opportunity", "statuscode", c));
            Line(sb, "Forecast category", OptionLabel(svc, "opportunity", "msdyn_forecastcategory", c));
            Line(sb, "Rating", OptionLabel(svc, "opportunity", "opportunityratingcode", c));
            Line(sb, "Budget status", OptionLabel(svc, "opportunity", "budgetstatus", c));
            Line(sb, "Purchase timeframe", OptionLabel(svc, "opportunity", "purchasetimeframe", c));
            Line(sb, "Purchase process", OptionLabel(svc, "opportunity", "purchaseprocess", c));
            Line(sb, "Priority", OptionLabel(svc, "opportunity", "prioritycode", c));
            // Customer segment deliberately lives on the account/contact, not here, for the same
            // reason the case build moved it off the case: it is a property of the buyer, not of
            // the deal. It is emitted by AppendCustomer.
            var created = c.GetAttributeValue<DateTime?>("createdon");
            if (created.HasValue)
            {
                Line(sb, "Created", created.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC");
                Line(sb, "Deal age", Age(created.Value));
            }
            var owner = c.GetAttributeValue<EntityReference>("ownerid");
            if (owner != null) Line(sb, "Owner", owner.Name);
            Line(sb, "Applied process", c.GetAttributeValue<string>(P + "processsummary"));
            sb.AppendLine();
        }

        /// <summary>
        /// What is actually being sold. This is the sales equivalent of the case's subject, and it
        /// is what the targeting rules key on, so an agent reasoning about a deal needs it as much
        /// as it needs the value. The product family path is spelled out for the same reason it is
        /// on a case subject: "Business Lending &gt; Working Capital &gt; Invoice Finance" tells a
        /// model far more than the leaf name on its own.
        /// </summary>
        private static void AppendProductLines(IOrganizationService svc, StringBuilder sb, Guid oppId)
        {
            var q = new QueryExpression("opportunityproduct")
            {
                ColumnSet = new ColumnSet("productid", "productname", "quantity",
                                          "priceperunit", "extendedamount", "isproductoverridden"),
                TopCount = MaxLines,
                Orders = { new OrderExpression("extendedamount", OrderType.Descending) },
            };
            q.Criteria.AddCondition("opportunityid", ConditionOperator.Equal, oppId);

            EntityCollection rows;
            try { rows = svc.RetrieveMultiple(q); }
            catch { return; }
            if (rows.Entities.Count == 0) return;

            sb.AppendLine("## Products on this deal");
            foreach (var r in rows.Entities)
            {
                var prod = r.GetAttributeValue<EntityReference>("productid");
                var name = prod != null ? prod.Name : r.GetAttributeValue<string>("productname");
                var qty = r.GetAttributeValue<decimal?>("quantity");
                var amt = r.GetAttributeValue<Money>("extendedamount");

                sb.Append("- ").Append(Clean(name, 120));
                var path = ProductPath(svc, prod);
                if (!string.IsNullOrEmpty(path)) sb.Append(" | family: ").Append(path);
                if (qty.HasValue)
                    sb.Append(" | qty ").Append(qty.Value.ToString("0.##", CultureInfo.InvariantCulture));
                if (amt != null)
                    sb.Append(" | ").Append(amt.Value.ToString("N0", CultureInfo.InvariantCulture));
                // A write-in line has no catalog product behind it, so no family and no targeting.
                if (prod == null) sb.Append(" | write-in, not in the catalogue");
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        private static void AppendNarrative(StringBuilder sb, Entity c)
        {
            var blocks = new[]
            {
                new[] { "Description", c.GetAttributeValue<string>("description") },
                new[] { "Current situation", c.GetAttributeValue<string>("currentsituation") },
                new[] { "Customer need", c.GetAttributeValue<string>("customerneed") },
                new[] { "Proposed solution", c.GetAttributeValue<string>("proposedsolution") },
                new[] { "Qualification comments", c.GetAttributeValue<string>("qualificationcomments") },
            };
            if (blocks.All(b => string.IsNullOrWhiteSpace(b[1]))) return;

            sb.AppendLine("## Deal narrative");
            foreach (var b in blocks)
            {
                if (string.IsNullOrWhiteSpace(b[1])) continue;
                sb.AppendLine("### " + b[0]);
                sb.AppendLine(Clean(b[1], MaxBodyChars));
            }
            sb.AppendLine();
        }

        private static void AppendSla(IOrganizationService svc, StringBuilder sb, Entity c)
        {
            var qualDue = c.GetAttributeValue<DateTime?>(P + "qualificationdue");
            var closeDue = c.GetAttributeValue<DateTime?>(P + "closedue");
            if (!qualDue.HasValue && !closeDue.HasValue) return;
            sb.AppendLine("## Deal clocks");
            if (qualDue.HasValue)
                Line(sb, "Qualification due", Due(qualDue.Value) + " ("
                     + OptionLabel(svc, "opportunity", P + "qualificationstatus", c) + ")");
            if (closeDue.HasValue)
                Line(sb, "Close due", Due(closeDue.Value) + " ("
                     + OptionLabel(svc, "opportunity", P + "closestatus", c) + ")");
            sb.AppendLine();
        }

        private static void AppendCustomer(IOrganizationService svc, StringBuilder sb, Entity c)
        {
            var cust = c.GetAttributeValue<EntityReference>("customerid");
            if (cust == null) return;
            sb.AppendLine("## Customer");
            Line(sb, "Name", cust.Name);
            Line(sb, "Record type", cust.LogicalName == "account" ? "Organisation" : "Individual");
            try
            {
                if (cust.LogicalName == "contact")
                {
                    var e = svc.Retrieve("contact", cust.Id, new ColumnSet(
                        "jobtitle", "address1_city", "address1_country", "createdon",
                        "preferredcontactmethodcode", "parentcustomerid", P + "customersegment"));
                    Line(sb, "Segment", OptionLabel(svc, "contact", P + "customersegment", e));
                    Line(sb, "Job title", e.GetAttributeValue<string>("jobtitle"));
                    Line(sb, "City", e.GetAttributeValue<string>("address1_city"));
                    Line(sb, "Country", e.GetAttributeValue<string>("address1_country"));
                    var parent = e.GetAttributeValue<EntityReference>("parentcustomerid");
                    if (parent != null) Line(sb, "Related organisation", parent.Name);
                    var since = e.GetAttributeValue<DateTime?>("createdon");
                    if (since.HasValue) Line(sb, "Customer since",
                        since.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
                else
                {
                    var e = svc.Retrieve("account", cust.Id, new ColumnSet(
                        "industrycode", "address1_city", "address1_country",
                        "numberofemployees", "createdon", P + "customersegment"));
                    Line(sb, "Segment", OptionLabel(svc, "account", P + "customersegment", e));
                    Line(sb, "Industry", OptionLabel(svc, "account", "industrycode", e));
                    Line(sb, "City", e.GetAttributeValue<string>("address1_city"));
                    Line(sb, "Country", e.GetAttributeValue<string>("address1_country"));
                    var staff = e.GetAttributeValue<int?>("numberofemployees");
                    if (staff.HasValue) Line(sb, "Employees", staff.Value.ToString(CultureInfo.InvariantCulture));
                    var opened = e.GetAttributeValue<DateTime?>("createdon");
                    if (opened.HasValue) Line(sb, "Customer since",
                        opened.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception)
            {
                // Missing privileges on the customer record must not fail the whole build.
                Line(sb, "Detail", "not available to the calling identity");
            }
            sb.AppendLine();
        }

        private static void AppendHistory(IOrganizationService svc, StringBuilder sb,
                                          Entity c, Guid oppId)
        {
            var cust = c.GetAttributeValue<EntityReference>("customerid");
            if (cust == null) return;
            var q = new QueryExpression("opportunity")
            {
                ColumnSet = new ColumnSet("name", "createdon", "statuscode", "statecode",
                                          "estimatedvalue", "actualvalue"),
                TopCount = MaxHistory,
                Orders = { new OrderExpression("createdon", OrderType.Descending) },
            };
            q.Criteria.AddCondition("customerid", ConditionOperator.Equal, cust.Id);
            q.Criteria.AddCondition("opportunityid", ConditionOperator.NotEqual, oppId);
            var rows = svc.RetrieveMultiple(q).Entities;
            if (rows.Count == 0) return;

            // Won, lost and still-open deals all matter here, and for different reasons: the wins
            // show what this customer buys, the losses show what they walked away from, and the
            // open ones are what this deal is competing with for the same budget.
            sb.AppendLine("## Other deals with this customer");
            foreach (var r in rows)
            {
                var when = r.GetAttributeValue<DateTime?>("createdon");
                var state = r.GetAttributeValue<OptionSetValue>("statecode");
                var won = state != null && state.Value == 1;
                var amount = won
                    ? r.GetAttributeValue<Money>("actualvalue")
                    : r.GetAttributeValue<Money>("estimatedvalue");
                sb.AppendLine("- " + (when.HasValue
                        ? when.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "?")
                    + " | " + Clean(r.GetAttributeValue<string>("name"), 120)
                    + " | " + OptionLabel(svc, "opportunity", "statuscode", r)
                    + (amount == null ? ""
                        : " | " + amount.Value.ToString("N0", CultureInfo.InvariantCulture)));
            }
            sb.AppendLine();
        }

        private static void AppendTasks(IOrganizationService svc, StringBuilder sb, Guid oppId)
        {
            var q = new QueryExpression("task")
            {
                ColumnSet = new ColumnSet("subject", "statuscode", "statecode", "description",
                                          P + "sequence", P + "stagename", P + "outcomelabel",
                                          P + "slastatus", P + "agentstate"),
                Orders = { new OrderExpression(P + "sequence", OrderType.Ascending) },
                TopCount = 50,
            };
            q.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, oppId);
            var rows = svc.RetrieveMultiple(q).Entities;
            if (rows.Count == 0) return;

            sb.AppendLine("## Process tasks");
            foreach (var t in rows)
            {
                var state = t.GetAttributeValue<OptionSetValue>("statecode");
                var done = state != null && state.Value == 1;
                sb.Append("- [").Append(done ? "x" : " ").Append("] ")
                  .Append(t.GetAttributeValue<string>("subject"));
                var stage = t.GetAttributeValue<string>(P + "stagename");
                if (!string.IsNullOrEmpty(stage)) sb.Append(" (stage: ").Append(stage).Append(")");
                var outcome = t.GetAttributeValue<string>(P + "outcomelabel");
                if (!string.IsNullOrEmpty(outcome)) sb.Append(" -> ").Append(outcome);
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        private static void AppendDocuments(IOrganizationService svc, StringBuilder sb, Guid oppId)
        {
            var q = new QueryExpression(P + "opportunityrequireddocument")
            {
                ColumnSet = new ColumnSet(P + "name", P + "received", P + "mandatory"),
                TopCount = 50,
            };
            q.Criteria.AddCondition(P + "opportunity", ConditionOperator.Equal, oppId);
            EntityCollection rows;
            try { rows = svc.RetrieveMultiple(q); }
            catch (Exception) { return; }
            if (rows.Entities.Count == 0) return;

            sb.AppendLine("## Required documents");
            foreach (var d in rows.Entities)
            {
                var got = d.GetAttributeValue<bool>(P + "received");
                var mand = d.GetAttributeValue<bool>(P + "mandatory");
                sb.Append("- [").Append(got ? "x" : " ").Append("] ")
                  .Append(d.GetAttributeValue<string>(P + "name"))
                  .Append(mand ? " (mandatory)" : " (optional)")
                  .AppendLine();
            }
            sb.AppendLine();
        }

        private static void AppendNotes(IOrganizationService svc, StringBuilder sb, Guid oppId)
        {
            var q = new QueryExpression("annotation")
            {
                ColumnSet = new ColumnSet("subject", "notetext", "createdon", "createdby"),
                TopCount = MaxNotes,
                Orders = { new OrderExpression("createdon", OrderType.Descending) },
            };
            q.Criteria.AddCondition("objectid", ConditionOperator.Equal, oppId);
            var rows = svc.RetrieveMultiple(q).Entities;
            if (rows.Count == 0) return;

            sb.AppendLine("## Notes (most recent first)");
            foreach (var n in rows)
            {
                var when = n.GetAttributeValue<DateTime?>("createdon");
                var who = n.GetAttributeValue<EntityReference>("createdby");
                sb.Append("### ")
                  .Append(when.HasValue ? when.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "?")
                  .Append(who != null ? " - " + who.Name : "")
                  .AppendLine();
                var subject = n.GetAttributeValue<string>("subject");
                if (!string.IsNullOrWhiteSpace(subject)) sb.AppendLine("**" + subject + "**");
                sb.AppendLine(Clean(n.GetAttributeValue<string>("notetext"), MaxBodyChars));
            }
            sb.AppendLine();
        }

        private static void AppendEmails(IOrganizationService svc, StringBuilder sb, Guid oppId)
        {
            var q = new QueryExpression("email")
            {
                ColumnSet = new ColumnSet("subject", "description", "createdon",
                                          "directioncode", "senton"),
                TopCount = MaxEmails,
                Orders = { new OrderExpression("createdon", OrderType.Descending) },
            };
            q.Criteria.AddCondition("regardingobjectid", ConditionOperator.Equal, oppId);
            var rows = svc.RetrieveMultiple(q).Entities;
            if (rows.Count == 0) return;

            sb.AppendLine("## Email correspondence (most recent first)");
            foreach (var e in rows)
            {
                var when = e.GetAttributeValue<DateTime?>("createdon");
                var outbound = e.GetAttributeValue<bool>("directioncode");
                sb.Append("### ").Append(outbound ? "Outbound" : "Inbound").Append(" - ")
                  .Append(when.HasValue ? when.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "?")
                  .AppendLine();
                sb.AppendLine("**" + Clean(e.GetAttributeValue<string>("subject"), 200) + "**");
                sb.AppendLine(Clean(e.GetAttributeValue<string>("description"), MaxBodyChars));
            }
            sb.AppendLine();
        }

        // ---------------------------------------------------------------- helpers

        private static void Line(StringBuilder sb, string k, string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return;
            sb.Append("- ").Append(k).Append(": ").AppendLine(v);
        }

        private static void Money(StringBuilder sb, string k, Money v)
        {
            if (v == null) return;
            Line(sb, k, v.Value.ToString("N0", CultureInfo.InvariantCulture));
        }

        private static string Age(DateTime createdUtc)
        {
            var span = DateTime.UtcNow - createdUtc;
            if (span.TotalDays >= 1) return ((int)span.TotalDays) + " day(s)";
            if (span.TotalHours >= 1) return ((int)span.TotalHours) + " hour(s)";
            return Math.Max(0, (int)span.TotalMinutes) + " minute(s)";
        }

        private static string Due(DateTime dueUtc)
        {
            var s = dueUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
            var span = dueUtc - DateTime.UtcNow;
            if (span.TotalMinutes < 0) return s + " (OVERDUE by " + Age(dueUtc) + ")";
            if (span.TotalHours < 24) return s + " (in " + ((int)span.TotalHours) + "h "
                                             + (span.Minutes) + "m)";
            return s + " (in " + ((int)span.TotalDays) + " day(s))";
        }

        /// <summary>Full subject path, so "Cards &gt; Card Disputes &gt; Unauthorised
        /// Transaction" reads as a hierarchy rather than a bare leaf.</summary>
        /// <summary>
        /// The product family path, walked up from the leaf. A deal targeted at "Business Lending"
        /// matches on an ancestor the line itself never names, so the path is what makes the
        /// targeting legible to a human reading the agent's context.
        /// </summary>
        private static string ProductPath(IOrganizationService svc, EntityReference product)
        {
            if (product == null) return null;
            var parts = new List<string>();
            var id = product.Id;
            for (var hop = 0; hop < 6 && id != Guid.Empty; hop++)
            {
                Entity s;
                try { s = svc.Retrieve("product", id, new ColumnSet("name", "parentproductid")); }
                catch (Exception) { break; }
                parts.Insert(0, s.GetAttributeValue<string>("name"));
                var parent = s.GetAttributeValue<EntityReference>("parentproductid");
                if (parent == null) break;
                id = parent.Id;
            }
            // The leaf is already printed by the caller, so only the ancestry is useful here.
            if (parts.Count > 1) parts.RemoveAt(parts.Count - 1);
            return string.Join(" > ", parts);
        }

        private static readonly Dictionary<string, Dictionary<int, string>> OptionCache =
            new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Resolves an option set value to its label. Cached per execution so a
        /// list of 10 cases does not trigger 10 metadata round trips.</summary>
        private static string OptionLabel(IOrganizationService svc, string entity,
                                          string attribute, Entity row)
        {
            var osv = row.GetAttributeValue<OptionSetValue>(attribute);
            if (osv == null) return null;
            var key = entity + "." + attribute;
            Dictionary<int, string> map;
            if (!OptionCache.TryGetValue(key, out map))
            {
                map = new Dictionary<int, string>();
                try
                {
                    var req = new RetrieveAttributeRequest
                    {
                        EntityLogicalName = entity,
                        LogicalName = attribute,
                        RetrieveAsIfPublished = true,
                    };
                    var resp = (RetrieveAttributeResponse)svc.Execute(req);
                    var meta = resp.AttributeMetadata as EnumAttributeMetadata;
                    if (meta != null)
                    {
                        foreach (var o in meta.OptionSet.Options)
                        {
                            if (o.Value.HasValue && o.Label != null && o.Label.UserLocalizedLabel != null)
                                map[o.Value.Value] = o.Label.UserLocalizedLabel.Label;
                        }
                    }
                }
                catch (Exception) { /* fall back to the raw value below */ }
                OptionCache[key] = map;
            }
            string labelText;
            return map.TryGetValue(osv.Value, out labelText)
                ? labelText
                : osv.Value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = Regex.Replace(s, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "</p>", "\n", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, "<[^>]+>", " ");
            s = s.Replace("&nbsp;", " ").Replace("&amp;", "&")
                 .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                 .Replace("&#39;", "'");
            s = Regex.Replace(s, "[ \\t]+", " ");
            s = Regex.Replace(s, "\n{3,}", "\n\n");
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max) + " ..." : s;
        }

        // Order matters: the longest and most specific patterns run first, so a card
        // number is not partially consumed by the generic long-digit rule.
        private static readonly Tuple<Regex, string>[] Redactions =
        {
            // IBAN, e.g. AE07 0331 2345 6789 0123 456
            Tuple.Create(new Regex(@"\b[A-Z]{2}\d{2}[ ]?(?:[A-Za-z0-9]{4}[ ]?){2,7}[A-Za-z0-9]{1,4}\b"),
                         "[IBAN REDACTED]"),
            // International phone. Must run before the card and account rules, otherwise a
            // signature block phone gets mislabelled as an account number.
            Tuple.Create(new Regex(@"(?<![\d\w])\+\d[\d \-().]{7,17}\d\b"), "[PHONE REDACTED]"),
            // Payment card, 13-19 digits with optional separators
            Tuple.Create(new Regex(@"\b(?:\d[ -]?){13,19}\b"), "[CARD REDACTED]"),
            // Emirates ID 784-YYYY-NNNNNNN-C
            Tuple.Create(new Regex(@"\b784[- ]?\d{4}[- ]?\d{7}[- ]?\d\b"), "[EMIRATES ID REDACTED]"),
            // Long bare digit runs that are almost certainly an account number
            Tuple.Create(new Regex(@"\b\d{9,}\b"), "[ACCOUNT NUMBER REDACTED]"),
            Tuple.Create(new Regex(@"\b[A-PR-WY][1-9]\d\s?\d{4}[1-9]\b"), "[PASSPORT REDACTED]"),
        };

        /// <summary>Masks identifiers that must not leave Dataverse. Applied last so it
        /// covers every section, including free text pasted into notes.</summary>
        public static string Redact(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            foreach (var r in Redactions) s = r.Item1.Replace(s, r.Item2);
            return s;
        }
    }

    /// <summary>
    /// Custom API spc_BuildOpportunityContext. Exposes the context builder so the cloud flow can
    /// assemble grounding data without embedding any query logic in the flow itself.
    /// </summary>
    public class BuildOpportunityContext : IPlugin
    {
        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);

            var oppId = ProcessRuntime.ParseGuid(
                ctx.InputParameters.Contains("OpportunityId") ? (string)ctx.InputParameters["OpportunityId"] : null);
            if (oppId == Guid.Empty)
                throw new InvalidPluginExecutionException("OpportunityId is required.");

            var scopeRaw = ctx.InputParameters.Contains("Scope")
                ? (string)ctx.InputParameters["Scope"] : null;
            var budget = ctx.InputParameters.Contains("BudgetChars")
                ? Convert.ToInt32(ctx.InputParameters["BudgetChars"]) : 24000;
            if (budget <= 0) budget = 24000;

            var text = OpportunityContext.Build(svc, oppId, OpportunityContext.ParseScope(scopeRaw), budget);
            ctx.OutputParameters["Context"] = text;
            ctx.OutputParameters["Length"] = text.Length;
        }
    }
}
