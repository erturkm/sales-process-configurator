using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Spc.Plugins
{
    /// <summary>
    /// Custom API spc_DesignProcess. Reads an uploaded standard operating procedure and asks an
    /// Azure AI Foundry model to design a case process from it. Returns a blueprint in exactly the
    /// shape spc_AuthorProcess already understands, so the existing preview and commit path is reused.
    ///
    /// Inputs : AnnotationId (note holding the document) or SopText, plus optional Instructions.
    /// Outputs: ProcessJson, Notes, SourceName, Model.
    ///
    /// The model is given the real teams, queues, stages, subjects, SLAs, document packages and
    /// published agents in this environment, and is told to choose only from those lists. That is
    /// what stops it inventing names that fail to resolve on commit.
    /// </summary>
    public class DesignProcess : IPlugin
    {
        private const string P = "spc_";
        private const int SopBudget = 90000;

        public void Execute(IServiceProvider sp)
        {
            var ctx = (IPluginExecutionContext)sp.GetService(typeof(IPluginExecutionContext));
            var factory = (IOrganizationServiceFactory)sp.GetService(typeof(IOrganizationServiceFactory));
            var svc = factory.CreateOrganizationService(ctx.UserId);
            var trace = (ITracingService)sp.GetService(typeof(ITracingService));

            var annotationId = In(ctx, "AnnotationId");
            var sopText = In(ctx, "SopText");
            var instructions = In(ctx, "Instructions");
            var mode = In(ctx, "Mode");
            if (string.IsNullOrWhiteSpace(mode)) mode = "run";
            var prepareOnly = mode.Equals("prepare", StringComparison.OrdinalIgnoreCase);

            var sourceName = "pasted text";
            var kind = "text";

            if (!string.IsNullOrWhiteSpace(annotationId))
            {
                Guid id;
                if (!Guid.TryParse(annotationId.Trim(), out id))
                    throw new InvalidPluginExecutionException("AnnotationId is not a valid id.");

                var note = svc.Retrieve("annotation", id,
                    new ColumnSet("documentbody", "filename", "mimetype", "subject"));
                var body = note.GetAttributeValue<string>("documentbody");
                if (string.IsNullOrEmpty(body))
                    throw new InvalidPluginExecutionException(
                        "That note has no file attached. Upload the procedure document again.");

                sourceName = note.GetAttributeValue<string>("filename") ?? "document";
                var bytes = Convert.FromBase64String(body);
                trace.Trace("sop: {0}, {1} bytes", sourceName, bytes.Length);
                sopText = SopText.Extract(bytes, sourceName, out kind);
            }

            if (string.IsNullOrWhiteSpace(sopText))
                throw new InvalidPluginExecutionException(
                    "Upload a procedure document or paste the procedure text.");

            bool trimmed;
            sopText = SopText.Budget(SopText.Tidy(sopText), SopBudget, out trimmed);
            trace.Trace("extracted {0} chars as {1} (trimmed={2})", sopText.Length, kind, trimmed);

            var cfg = Config(svc);
            var catalog = Catalog(svc, trace);

            var system = SystemPrompt(catalog);
            var user = UserPrompt(sopText, instructions, sourceName, trimmed);

            var notes = new StringBuilder();
            notes.Append("Read ").Append(sopText.Length.ToString("N0"))
                 .Append(" characters from ").Append(sourceName)
                 .Append(" (").Append(kind).Append(").");
            if (trimmed) notes.Append(" The document was long, so the middle was omitted.");

            ctx.OutputParameters["Notes"] = notes.ToString();
            ctx.OutputParameters["SourceName"] = sourceName;
            ctx.OutputParameters["Model"] = cfg.Deployment;

            if (prepareOnly)
            {
                // The designer calls Azure AI Foundry itself. A reasoning model routinely needs
                // more than the two minutes a sandboxed plugin is allowed, and the browser has no
                // such limit, so the plugin hands over the prompt and a short lived token instead
                // of making the call. The client secret never leaves Dataverse.
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ctx.OutputParameters["Endpoint"] = cfg.Endpoint.TrimEnd('/');
                ctx.OutputParameters["Deployment"] = cfg.Deployment;
                ctx.OutputParameters["ApiVersion"] = cfg.ApiVersion;
                ctx.OutputParameters["Token"] = Token(cfg);
                ctx.OutputParameters["SystemPrompt"] = system;
                ctx.OutputParameters["UserPrompt"] = user;
                ctx.OutputParameters["SchemaJson"] = Schema.Text;
                ctx.OutputParameters["ProcessJson"] = "";
                return;
            }

            ctx.OutputParameters["ProcessJson"] = Foundry(cfg, system, user, trace);
            ctx.OutputParameters["Endpoint"] = "";
            ctx.OutputParameters["Deployment"] = cfg.Deployment;
            ctx.OutputParameters["ApiVersion"] = cfg.ApiVersion;
            ctx.OutputParameters["Token"] = "";
            ctx.OutputParameters["SystemPrompt"] = "";
            ctx.OutputParameters["UserPrompt"] = "";
            ctx.OutputParameters["SchemaJson"] = "";
        }

        private static string In(IPluginExecutionContext c, string k)
        {
            return c.InputParameters.Contains(k) ? c.InputParameters[k] as string : null;
        }

        // ---------------- configuration ----------------

        private class Cfg
        {
            public string Endpoint, Deployment, ApiVersion, TenantId, ClientId, ClientSecret;
        }

        private static Cfg Config(IOrganizationService svc)
        {
            var c = new Cfg
            {
                Endpoint = EnvVar(svc, "spc_FoundryEndpoint"),
                Deployment = EnvVar(svc, "spc_FoundryDeployment"),
                ApiVersion = EnvVar(svc, "spc_FoundryApiVersion"),
                TenantId = EnvVar(svc, "spc_FoundryTenantId"),
                ClientId = EnvVar(svc, "spc_FoundryClientId"),
                ClientSecret = EnvVar(svc, "spc_FoundryClientSecret"),
            };
            if (string.IsNullOrWhiteSpace(c.Endpoint) || string.IsNullOrWhiteSpace(c.Deployment)
                || string.IsNullOrWhiteSpace(c.ClientId) || string.IsNullOrWhiteSpace(c.ClientSecret))
                throw new InvalidPluginExecutionException(
                    "The Process Copilot is not configured. Set the spc_Foundry* environment variables.");
            if (string.IsNullOrWhiteSpace(c.ApiVersion)) c.ApiVersion = "2024-12-01-preview";
            return c;
        }

        private static string EnvVar(IOrganizationService svc, string schemaName)
        {
            var q = new QueryExpression("environmentvariabledefinition")
            {
                ColumnSet = new ColumnSet("environmentvariabledefinitionid", "defaultvalue"),
                Criteria = { Conditions = { new ConditionExpression("schemaname", ConditionOperator.Equal, schemaName) } },
                TopCount = 1
            };
            var def = svc.RetrieveMultiple(q).Entities;
            if (def.Count == 0) return null;

            var vq = new QueryExpression("environmentvariablevalue")
            {
                ColumnSet = new ColumnSet("value"),
                Criteria = { Conditions = { new ConditionExpression(
                    "environmentvariabledefinitionid", ConditionOperator.Equal, def[0].Id) } },
                TopCount = 1
            };
            var val = svc.RetrieveMultiple(vq).Entities;
            if (val.Count > 0)
            {
                var v = val[0].GetAttributeValue<string>("value");
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return def[0].GetAttributeValue<string>("defaultvalue");
        }

        // ---------------- what actually exists in this environment ----------------

        /// <summary>
        /// The product catalogue as a tree. Indentation is the point: the model has to be able to
        /// see that "Working Capital Facility" sits under "Business Lending" in order to write a
        /// family rule instead of enumerating every SKU.
        /// </summary>
        private static void AppendProducts(IOrganizationService svc, StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("## Product catalogue (use productlines.productid with \"is at or under\")");
            var rows = svc.RetrieveMultiple(new QueryExpression("product")
            {
                ColumnSet = new ColumnSet("productid", "name", "parentproductid"),
                Criteria = { Conditions = { new ConditionExpression("statecode", ConditionOperator.NotEqual, 2) } },
                Orders = { new OrderExpression("name", OrderType.Ascending) }
            }).Entities;
            if (rows.Count == 0) { sb.AppendLine("- (none)"); return; }

            var kids = new Dictionary<Guid, List<Entity>>();
            var roots = new List<Entity>();
            var known = new HashSet<Guid>();
            foreach (var r in rows) known.Add(r.Id);
            foreach (var r in rows)
            {
                var p = r.GetAttributeValue<EntityReference>("parentproductid");
                if (p == null || !known.Contains(p.Id)) { roots.Add(r); continue; }
                List<Entity> list;
                if (!kids.TryGetValue(p.Id, out list)) { list = new List<Entity>(); kids[p.Id] = list; }
                list.Add(r);
            }
            foreach (var r in roots) Walk(sb, kids, r, 0);
        }

        private static void Walk(StringBuilder sb, Dictionary<Guid, List<Entity>> kids, Entity node, int depth)
        {
            if (depth > 6) return;
            sb.Append(new string(' ', depth * 2)).Append("- ")
              .AppendLine(node.GetAttributeValue<string>("name"));
            List<Entity> list;
            if (!kids.TryGetValue(node.Id, out list)) return;
            foreach (var c in list) Walk(sb, kids, c, depth + 1);
        }

        private static string Catalog(IOrganizationService svc, ITracingService trace)
        {
            var sb = new StringBuilder();

            sb.AppendLine("## Business process flows available for Opportunity");
            var bpfs = svc.RetrieveMultiple(new QueryExpression("workflow")
            {
                ColumnSet = new ColumnSet("workflowid", "name", "primaryentity"),
                Criteria =
                {
                    Conditions =
                    {
                        new ConditionExpression("category", ConditionOperator.Equal, 4),
                        new ConditionExpression("type", ConditionOperator.Equal, 1),
                        new ConditionExpression("primaryentity", ConditionOperator.Equal, "opportunity"),
                        new ConditionExpression("statecode", ConditionOperator.Equal, 1)
                    }
                }
            }).Entities;
            foreach (var b in bpfs)
            {
                sb.Append("- \"").Append(b.GetAttributeValue<string>("name")).Append("\" stages: ");
                var st = svc.RetrieveMultiple(new QueryExpression("processstage")
                {
                    ColumnSet = new ColumnSet("stagename"),
                    Criteria = { Conditions = { new ConditionExpression("processid", ConditionOperator.Equal, b.Id) } }
                }).Entities;
                var names = new List<string>();
                foreach (var s in st)
                {
                    var n = s.GetAttributeValue<string>("stagename");
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
                sb.AppendLine(names.Count == 0 ? "(none)" : string.Join(" -> ", names.ToArray()));
            }
            if (bpfs.Count == 0) sb.AppendLine("- (none activated)");

            List(svc, sb, "## Teams (use for assignTo \"team\")", "team", "name",
                 new ConditionExpression("teamtype", ConditionOperator.Equal, 0), 200, Noise);
            List(svc, sb, "## Queues (use for assignTo \"queue\")", "queue", "name", null, 40, null);
            List(svc, sb, "## SLA records", "sla", "name", null, 25, null);
            List(svc, sb, "## Document packages", P + "documentpackage", P + "name", null, 40, null);
            AppendProducts(svc, sb);

            sb.AppendLine();
            sb.AppendLine("## Published AI agents (use for assignTo \"ai agent\")");
            try
            {
                // Read the whole table: the Microsoft first party agents heavily outnumber the
                // customer built ones, so a TopCount here would hide exactly the agents we want.
                var bots = svc.RetrieveMultiple(new QueryExpression("bot")
                {
                    ColumnSet = new ColumnSet("name", "schemaname")
                }).Entities;

                var shown = 0;
                foreach (var b in bots)
                {
                    var name = b.GetAttributeValue<string>("name");
                    var schema = b.GetAttributeValue<string>("schemaname") ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (IsSystemAgent(schema, name)) continue;
                    sb.Append("- \"").Append(name).Append("\"\n");
                    shown++;
                }
                if (shown == 0)
                    sb.AppendLine("- (none published - do not use assignTo \"ai agent\")");
            }
            catch (Exception ex)
            {
                trace.Trace("bot read failed: {0}", ex.Message);
                sb.AppendLine("- (unavailable - do not use assignTo \"ai agent\")");
            }

            return sb.ToString();
        }

        /// <summary>Microsoft ships dozens of first party agents. They are not ours to orchestrate.</summary>
        private static bool IsSystemAgent(string schema, string name)
        {
            if (schema.StartsWith("msdyn_", StringComparison.OrdinalIgnoreCase)) return true;
            if (schema.StartsWith("oc_", StringComparison.OrdinalIgnoreCase)) return true;
            return name.TrimStart().StartsWith("[Internal]", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Dataverse auto-creates owner teams named after an application id, like
        /// "f16a6f32e38b4fca8f2bd906895d07db_1". They are noise in a picker and the model must not
        /// assign work to them.
        /// </summary>
        private static bool Noise(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var us = name.LastIndexOf('_');
            var head = us > 0 ? name.Substring(0, us) : name;
            if (head.Length == 32)
            {
                var hex = true;
                foreach (var c in head)
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    { hex = false; break; }
                if (hex) return true;
            }
            return false;
        }

        private static void List(IOrganizationService svc, StringBuilder sb, string heading,
                                 string entity, string attr, ConditionExpression extra, int top,
                                 Func<string, bool> skip)
        {
            sb.AppendLine();
            sb.AppendLine(heading);
            try
            {
                var q = new QueryExpression(entity)
                {
                    ColumnSet = new ColumnSet(attr),
                    TopCount = top
                };
                if (extra != null) q.Criteria.Conditions.Add(extra);
                var rows = svc.RetrieveMultiple(q).Entities;
                var shown = 0;
                foreach (var r in rows)
                {
                    var v = r.GetAttributeValue<string>(attr);
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (skip != null && skip(v)) continue;
                    sb.Append("- \"").Append(v).Append("\"\n");
                    shown++;
                }
                if (shown == 0) sb.AppendLine("- (none)");
            }
            catch
            {
                sb.AppendLine("- (unavailable)");
            }
        }

        // ---------------- prompts ----------------

        private static string SystemPrompt(string catalog)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a Dynamics 365 Sales process designer for a bank.");
            sb.AppendLine("You convert a written sales procedure or playbook into a sales process blueprint");
            sb.AppendLine("that runs on an opportunity.");
            sb.AppendLine();
            sb.AppendLine("Rules you must follow:");
            sb.AppendLine("1. Use ONLY names that appear in the environment catalog below. If the procedure");
            sb.AppendLine("   names a team or stage that does not exist, pick the closest one that does and");
            sb.AppendLine("   record the substitution in assumptions. Never invent a name.");
            sb.AppendLine("2. Follow the procedure closely. Every mandatory step, control, approval,");
            sb.AppendLine("   hand-off and deadline in the document becomes a task. Preserve the order.");
            sb.AppendLine("   A task is one unit of work that one owner completes and signs off in one");
            sb.AppendLine("   sitting. Do not split the data capture inside a single step into separate");
            sb.AppendLine("   tasks: 'record the amount, merchant and date' is ONE task, not three.");
            sb.AppendLine("   Equally, do not merge a check, an approval and an action into one task.");
            sb.AppendLine("3. Put the real deadline in dueHours. Convert stated targets: one business day");
            sb.AppendLine("   is 8, two business days 16, a calendar day 24, five business days 40.");
            sb.AppendLine("   If the procedure gives no deadline, choose a sensible one and say so.");
            sb.AppendLine("4. instructions on each task must tell the handler what to actually do, drawn");
            sb.AppendLine("   from the procedure text. Quote thresholds, amounts and form names exactly.");
            sb.AppendLine("5. Use the AI agents listed in the catalog. If an agent is listed, assign it");
            sb.AppendLine("   the steps it genuinely suits: reading, summarising, classifying, checking");
            sb.AppendLine("   completeness, drafting correspondence. A process where a capable agent is");
            sb.AppendLine("   listed but every task is human is a worse answer, not a safer one.");
            sb.AppendLine("6. The limit is authority, not capability. Any step that moves money, grants");
            sb.AppendLine("   credit, accepts a loss, closes a regulated complaint, or is the final");
            sb.AppendLine("   decision must go to a human even when an agent could draft it. Where a");
            sb.AppendLine("   procedure names an approver by seniority, that step is always human.");
            sb.AppendLine("   A good pattern is an agent task that assesses and closes its own step,");
            sb.AppendLine("   followed by a human task only where a person must own the decision.");
            sb.AppendLine("7. For an AI agent task set agentName, agentPrompt, agentContext,");
            sb.AppendLine("   agentOutcomeMode and confidenceThreshold.");
            sb.AppendLine("   Default to \"agent selects the outcome\" with autoComplete true: the agent");
            sb.AppendLine("   finishes its own task and the process moves on. That is the point of giving");
            sb.AppendLine("   it the work. The confidence threshold is the safety net, not a human queue.");
            sb.AppendLine("   Use \"agent proposes, a person selects\" only for the steps in rule 6 where a");
            sb.AppendLine("   named human must own the decision, or where the procedure requires a");
            sb.AppendLine("   signature, an approval or a regulatory sign-off.");
            sb.AppendLine("   Set the threshold by consequence: 60-75 for routine reading and summarising,");
            sb.AppendLine("   85-95 where a wrong call is expensive or reaches the customer.");
            sb.AppendLine("   agentPrompt must name the allowed outcomes and ask for a confidence figure.");
            sb.AppendLine("   Give agentContext everything the task genuinely needs. An agent starved of");
            sb.AppendLine("   context guesses, which is worse than one that reads too much.");
            sb.AppendLine("8. blocksStage true means the stage cannot finish until the task is done. Use it");
            sb.AppendLine("   for controls and approvals, not for courtesy steps.");
            sb.AppendLine("9. Match rules decide which deals get this process. The primary targeting field is");
            sb.AppendLine("   productlines.productid - what the customer is actually buying - used with");
            sb.AppendLine("   \"is at or under\" against a product family so every product beneath it is");
            sb.AppendLine("   caught. A deal carries several lines, so such a rule is true when ANY line");
            sb.AppendLine("   matches and its negative is true only when NO line does. Qualify further");
            sb.AppendLine("   with the customer segment (customerid.spc_customersegment) or deal size");
            sb.AppendLine("   (estimatedvalue). Conditions in one group are OR'ed, groups are AND'ed.");
            sb.AppendLine();
            sb.AppendLine("Return JSON only, matching the supplied schema.");
            sb.AppendLine();
            sb.AppendLine("# Environment catalog");
            sb.Append(catalog);
            return sb.ToString();
        }

        private static string UserPrompt(string sop, string instructions, string source, bool trimmed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Design a sales process from the procedure below.");
            sb.Append("Source document: ").Append(source).AppendLine();
            if (trimmed)
                sb.AppendLine("Note: the document was truncated in the middle to fit. Work with what is here.");
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                sb.AppendLine();
                sb.AppendLine("Additional instructions from the business user (these take priority):");
                sb.AppendLine(instructions.Trim());
            }
            sb.AppendLine();
            sb.AppendLine("----- BEGIN PROCEDURE -----");
            sb.AppendLine(sop);
            sb.AppendLine("----- END PROCEDURE -----");
            return sb.ToString();
        }

        // ---------------- foundry ----------------

        private static string Foundry(Cfg cfg, string system, string user, ITracingService trace)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var token = Token(cfg);

            var url = cfg.Endpoint.TrimEnd('/') + "/openai/deployments/" + cfg.Deployment
                      + "/chat/completions?api-version=" + cfg.ApiVersion;

            var body = "{\"messages\":["
                     + "{\"role\":\"system\",\"content\":" + Q(system) + "},"
                     + "{\"role\":\"user\",\"content\":" + Q(user) + "}],"
                     + "\"max_completion_tokens\":32000,"
                     + "\"response_format\":{\"type\":\"json_schema\",\"json_schema\":"
                     + "{\"name\":\"sales_process\",\"strict\":true,\"schema\":" + Schema.Text + "}}}";

            var raw = Post(url, body, "Bearer " + token, trace);

            var root = Json.Obj(Json.Parse(raw));
            var choices = Json.Arr(Json.Get(root, "choices"));
            if (choices.Count == 0)
                throw new InvalidPluginExecutionException("The model returned no content. " + Snip(raw));

            var first = Json.Obj(choices[0]);
            var finish = Json.Str(first, "finish_reason");
            var msg = Json.Obj(Json.Get(first, "message"));
            var content = msg == null ? null : Json.Str(msg, "content");

            if (string.IsNullOrWhiteSpace(content))
            {
                if (string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidPluginExecutionException(
                        "The model ran out of output budget before it finished the design. "
                        + "Try a shorter procedure or ask for fewer stages.");
                throw new InvalidPluginExecutionException(
                    "The model returned an empty design (finish_reason=" + finish + ").");
            }
            trace.Trace("design {0} chars, finish={1}", content.Length, finish);
            return content;
        }

        private static string Token(Cfg cfg)
        {
            var form = "client_id=" + Uri.EscapeDataString(cfg.ClientId)
                     + "&client_secret=" + Uri.EscapeDataString(cfg.ClientSecret)
                     + "&grant_type=client_credentials"
                     + "&scope=" + Uri.EscapeDataString("https://cognitiveservices.azure.com/.default");

            var url = "https://login.microsoftonline.com/" + cfg.TenantId + "/oauth2/v2.0/token";
            var raw = Post(url, form, null, null, "application/x-www-form-urlencoded");
            var t = Json.Str(Json.Obj(Json.Parse(raw)), "access_token");
            if (string.IsNullOrEmpty(t))
                throw new InvalidPluginExecutionException(
                    "Could not authenticate to Azure AI Foundry. " + Snip(raw));
            return t;
        }

        private static string Post(string url, string body, string auth, ITracingService trace,
                                   string contentType = "application/json")
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = contentType;
            req.Timeout = 110000;
            req.ReadWriteTimeout = 110000;
            if (!string.IsNullOrEmpty(auth)) req.Headers["Authorization"] = auth;

            var data = new UTF8Encoding(false).GetBytes(body);
            req.ContentLength = data.Length;
            using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return r.ReadToEnd();
            }
            catch (WebException ex)
            {
                var detail = "";
                if (ex.Response != null)
                    using (var r = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        detail = r.ReadToEnd();
                if (trace != null) trace.Trace("http error: {0}", Snip(detail));
                var hint = string.IsNullOrWhiteSpace(detail)
                    ? "The call timed out. A sandboxed plugin gets two minutes, which a reasoning "
                      + "model designing a long procedure can exceed. Use Mode=prepare so the "
                      + "designer makes the call instead."
                    : Snip(detail);
                throw new InvalidPluginExecutionException(
                    "The Process Copilot could not reach Azure AI Foundry. " + hint);
            }
        }

        private static string Snip(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length > 400 ? s.Substring(0, 400) : s;
        }

        /// <summary>JSON string literal, escaped for the request body.</summary>
        private static string Q(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 32);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ' || c == '\u2028' || c == '\u2029')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
