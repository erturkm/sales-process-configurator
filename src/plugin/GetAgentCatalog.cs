using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Custom API spc_GetAgentCatalog. Feeds the agent picker in the process designer.
    ///
    /// Filtering matters here: a Customer Service environment ships dozens of first-party
    /// msdyn_* agents that a business user must never pick, and an unpublished agent fails
    /// at runtime with an opaque 404. Both are excluded by default.
    /// </summary>
    public class GetAgentCatalog : IPlugin
    {
        private static readonly string[] SystemPrefixes =
        {
            "msdyn_", "msfp_", "mspp_", "msdynce_", "oc_", "msgpt_",
        };

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);

            var includeSystem = ctx.InputParameters.Contains("IncludeSystem")
                                && string.Equals(ctx.InputParameters["IncludeSystem"] as string,
                                                 "true", StringComparison.OrdinalIgnoreCase);
            var includeUnpublished = ctx.InputParameters.Contains("IncludeUnpublished")
                                && string.Equals(ctx.InputParameters["IncludeUnpublished"] as string,
                                                 "true", StringComparison.OrdinalIgnoreCase);

            var q = new QueryExpression("bot")
            {
                ColumnSet = new ColumnSet("botid", "name", "schemaname", "publishedon",
                                          "statecode", "authenticationmode"),
                Orders = { new OrderExpression("name", OrderType.Ascending) },
                TopCount = 500,
            };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

            var rows = svc.RetrieveMultiple(q).Entities;
            var sb = new StringBuilder();
            sb.Append("{").Append(Json.Q("agents")).Append(":[");
            var first = true;
            var hidden = 0;

            foreach (var b in rows)
            {
                var schema = b.GetAttributeValue<string>("schemaname") ?? "";
                var published = b.GetAttributeValue<DateTime?>("publishedon");
                var isSystem = SystemPrefixes.Any(
                    p => schema.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                if ((isSystem && !includeSystem) || (!published.HasValue && !includeUnpublished))
                {
                    hidden++;
                    continue;
                }

                if (!first) sb.Append(",");
                first = false;
                var auth = b.GetAttributeValue<OptionSetValue>("authenticationmode");
                sb.Append("{")
                  .Append(Json.Q("id")).Append(":").Append(Json.Q(b.Id.ToString())).Append(",")
                  .Append(Json.Q("name")).Append(":").Append(Json.Q(b.GetAttributeValue<string>("name"))).Append(",")
                  .Append(Json.Q("schemaName")).Append(":").Append(Json.Q(schema)).Append(",")
                  .Append(Json.Q("system")).Append(":").Append(isSystem ? "true" : "false").Append(",")
                  .Append(Json.Q("published")).Append(":").Append(published.HasValue ? "true" : "false").Append(",")
                  .Append(Json.Q("authMode")).Append(":").Append(auth == null ? "null" : auth.Value.ToString())
                  .Append("}");
            }

            sb.Append("],").Append(Json.Q("hidden")).Append(":").Append(hidden)
              .Append(",").Append(Json.Q("total")).Append(":").Append(rows.Count)
              .Append("}");

            ctx.OutputParameters["Catalog"] = sb.ToString();
        }
    }
}
