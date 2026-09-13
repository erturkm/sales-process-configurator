namespace Spc.Plugins
{
    /// <summary>
    /// The strict JSON schema handed to Azure AI Foundry. It mirrors the blueprint shape that
    /// spc_AuthorProcess already parses, so a design can go straight into preview and commit.
    ///
    /// Strict structured output requires every property to appear in "required" and every object to
    /// set additionalProperties false, so optional fields are expressed as nullable type unions
    /// rather than by being left out.
    /// </summary>
    internal static class Schema
    {
        public const string Text = @"{
  ""type"": ""object"",
  ""additionalProperties"": false,
  ""required"": [""name"",""description"",""businessProcessFlow"",""startStage"",""documentPackage"",
                 ""sla"",""rank"",""publish"",""setOpportunityPriority"",""qualificationHours"",
                 ""closeHours"",""rules"",""tasks"",""assumptions""],
  ""properties"": {
    ""name"": { ""type"": ""string"", ""description"": ""Short business name for this sales process."" },
    ""description"": { ""type"": ""string"", ""description"": ""One or two sentences on what it covers and which procedure it came from."" },
    ""businessProcessFlow"": { ""type"": [""string"",""null""], ""description"": ""Exact name of a business process flow from the catalog, or null."" },
    ""startStage"": { ""type"": [""string"",""null""], ""description"": ""Exact stage name on that flow where a new deal starts."" },
    ""documentPackage"": { ""type"": [""string"",""null""], ""description"": ""Exact document package name from the catalog, or null."" },
    ""sla"": { ""type"": [""string"",""null""], ""description"": ""Exact SLA record name from the catalog, or null."" },
    ""rank"": { ""type"": ""integer"", ""description"": ""Match precedence 1-100. More specific processes take a lower number."" },
    ""publish"": { ""type"": ""boolean"", ""description"": ""Always false. A human publishes after reviewing."" },
    ""setOpportunityPriority"": { ""type"": ""string"", ""enum"": [""leave as is"",""high"",""normal"",""low""] },
    ""qualificationHours"": { ""type"": [""number"",""null""], ""description"": ""Hours allowed to qualify the deal."" },
    ""closeHours"": { ""type"": [""number"",""null""], ""description"": ""Hours allowed to reach a won or lost decision."" },

    ""rules"": {
      ""type"": ""array"",
      ""description"": ""Which opportunities this process applies to. Conditions sharing a group are OR'ed; groups are AND'ed."",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""attribute"",""label"",""operator"",""group"",""value""],
        ""properties"": {
          ""attribute"": { ""type"": ""string"", ""description"": ""Logical name on the opportunity, e.g. productlines.productid for what is being sold, customerid.spc_customersegment for the buyer, or a plain column such as estimatedvalue or opportunityratingcode. A productlines rule is true when ANY line on the deal satisfies it."" },
          ""label"": { ""type"": ""string"", ""description"": ""Human readable field name."" },
          ""operator"": { ""type"": ""string"", ""enum"": [""equals"",""does not equal"",""is any of"",""is none of"",""contains"",""begins with"",""greater than"",""less than"",""is empty"",""is not empty"",""is at or under"",""is not under""] },
          ""group"": { ""type"": ""integer"", ""description"": ""Group number. Same number means OR."" },
          ""value"": { ""type"": ""string"", ""description"": ""Value label. For 'is any of' separate with semicolons."" }
        }
      }
    },

    ""tasks"": {
      ""type"": ""array"",
      ""description"": ""Every step the procedure requires, in the order it states them."",
      ""items"": {
        ""type"": ""object"",
        ""additionalProperties"": false,
        ""required"": [""subject"",""stage"",""assignTo"",""assignee"",""dueHours"",""slaTargetHours"",
                       ""slaWarnPercent"",""blocksStage"",""mandatory"",""slaStartWhen"",""onBreach"",
                       ""instructions"",""agentName"",""agentPrompt"",""agentContext"",
                       ""agentOutcomeMode"",""autoComplete"",""confidenceThreshold"",""outputTarget""],
        ""properties"": {
          ""subject"": { ""type"": ""string"", ""description"": ""Imperative task title, e.g. 'Confirm the customer's borrowing requirement'."" },
          ""stage"": { ""type"": [""string"",""null""], ""description"": ""Exact stage name this task belongs to."" },
          ""assignTo"": { ""type"": ""string"", ""enum"": [""team"",""user"",""role"",""queue"",""manager of deal owner"",""deal owner"",""ai agent""] },
          ""assignee"": { ""type"": [""string"",""null""], ""description"": ""Exact team, user, role or queue name from the catalog. Null for deal owner, manager or ai agent."" },
          ""dueHours"": { ""type"": ""number"", ""description"": ""Working hours allowed. One business day is 8."" },
          ""slaTargetHours"": { ""type"": [""number"",""null""] },
          ""slaWarnPercent"": { ""type"": ""integer"", ""description"": ""Warn at this percent of the target. 75 is typical."" },
          ""blocksStage"": { ""type"": ""boolean"", ""description"": ""True for controls and approvals that must finish before the stage can close."" },
          ""mandatory"": { ""type"": ""boolean"" },
          ""slaStartWhen"": { ""type"": ""string"", ""enum"": [""deal created"",""stage entered"",""predecessor completed"",""task created""] },
          ""onBreach"": { ""type"": ""string"", ""enum"": [""do nothing"",""notify task owner"",""escalate to manager"",""escalate to queue"",""raise deal priority""] },
          ""instructions"": { ""type"": ""string"", ""description"": ""What the handler must actually do, taken from the procedure. Quote thresholds, amounts and form names exactly."" },

          ""agentName"": { ""type"": [""string"",""null""], ""description"": ""Exact published agent name. Only when assignTo is 'ai agent', else null."" },
          ""agentPrompt"": { ""type"": [""string"",""null""], ""description"": ""Instruction sent to the agent. State the allowed outcomes and ask for a confidence percentage."" },
          ""agentContext"": {
            ""type"": [""array"",""null""],
            ""description"": ""Which deal data the agent may read. Give it everything the task genuinely needs; withholding context makes the agent guess. Null unless assignTo is 'ai agent'."",
            ""items"": { ""type"": ""string"", ""enum"": [""deal fields"",""deal narrative"",""product lines"",""customer profile"",""other deals with this customer"",""notes on the deal"",""emails on the deal"",""tasks already done and their outcomes"",""required documents"",""clock status""] }
          },
          ""agentOutcomeMode"": { ""type"": [""string"",""null""], ""enum"": [""agent selects the outcome"",""agent proposes, a person selects"",""no outcome, output only"",null], ""description"": ""Default to 'agent selects the outcome'. Use 'agent proposes, a person selects' only where a named human must own the decision."" },
          ""autoComplete"": { ""type"": [""boolean"",""null""], ""description"": ""Default true. The agent closes the task and the process moves on without waiting for a person. Set false only where a human must confirm before the process advances."" },
          ""confidenceThreshold"": { ""type"": [""integer"",""null""], ""description"": ""0-100. Below this the task falls back to a human regardless of the settings above. This, not autoComplete, is the safety net."" },
          ""outputTarget"": { ""type"": [""string"",""null""], ""enum"": [""agent output field only"",""task description"",""note on the deal"",""append to deal narrative"",null] }
        }
      }
    },

    ""assumptions"": {
      ""type"": ""array"",
      ""description"": ""Anything you inferred, substituted or could not find in the procedure. Be candid: this is what the reviewer checks first."",
      ""items"": { ""type"": ""string"" }
    }
  }
}";
    }
}
