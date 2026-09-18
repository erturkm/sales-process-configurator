# Installing the Sales Process Configurator

> [!WARNING]
> **This is a demonstration accelerator. It is not built, reviewed or tested for production use, and is provided "as is" with no warranty and no support.** Deploy only to a disposable trial, developer or sandbox environment — never to production, and never to an environment holding real personal, regulated or business-critical data. Please read the full [DISCLAIMER](DISCLAIMER.md) before you begin.

---

## Prerequisites

| Requirement | Notes |
|---|---|
| A Dataverse environment | A **disposable** trial, developer or sandbox environment. A Power Platform developer environment is free and ideal. |
| Dynamics 365 Sales | The Opportunity table, product catalog and sales business process flows must be present. |
| **Modern SLA Timer PCF** | **Install this first.** The task form binds its deal clock to this control, so the solution import fails without it. See below. |
| System Administrator role | Required to register a plug-in assembly, create custom APIs and edit the Opportunity form. |
| Python 3.9+ | Only needed to build from source, or to run the post-import configuration steps. |
| Azure CLI (`az`) | Used for authentication by the build scripts. Sign in with `az login` first. |
| .NET SDK 6.0+ | Only needed if you intend to rebuild the plug-in assembly from C# source. |

### Modern SLA Timer PCF

The countdown clock on each process task is not our control. It is the **Modern SLA Timer** PCF by
Marcelo Oliveira Pinto, used unmodified and under its own licence:

**https://github.com/moliveirapinto/modern-sla-timer-pcf**

It is deliberately *not* bundled into this solution, so that it stays on its own release cadence and
its licence is never restated by us. Install it first — download the solution zip from that
repository's [releases](https://github.com/moliveirapinto/modern-sla-timer-pcf/releases) and import
it before importing this one.

Skipping it produces exactly this import error:

```
Solution manifest import: FAILURE ... The missing dependencies are:
  <Required type="66" schemaName="mcsla_ModernSlaTimer.ModernSlaTimerControl" ... />
  <Dependent type="60" displayName="Task" ... />
```

Optional, and only if you want the Copilot authoring features:

| Requirement | Notes |
|---|---|
| Azure AI Foundry deployment | A chat-completions deployment that supports structured JSON output. |
| An Entra app registration | Client credentials with access to that Foundry resource. |

> [!NOTE]
> The accelerator installs and runs perfectly well **without** the Foundry connection. You simply
> author processes in the visual designer instead of describing them to Copilot.

---

## Option A — import the packaged solution (recommended)

1. Download `SalesProcessConfigurator_1_1_0_0_managed.zip` from the
   [latest release](https://github.com/erturkm/sales-process-configurator/releases/latest).

2. **Import the [Modern SLA Timer PCF](https://github.com/moliveirapinto/modern-sla-timer-pcf)
   first** if you have not already — see Prerequisites above. The import fails without it.

3. In the **Power Platform admin centre** (or `make.powerapps.com`), select your target
   environment, go to **Solutions → Import solution**, and choose the downloaded file.

4. When prompted for **environment variable values**, you may leave them all blank. They hold the
   Azure AI Foundry connection and are only needed for Copilot authoring. They are intentionally
   shipped **without values** so that no credential can ever travel inside a solution file.

5. Complete the import and wait for publishing to finish. A first-time import takes roughly
   15 minutes.

6. Run the post-import configuration below.

### Post-import configuration

The solution carries the components; these steps create the sample teams, product-line data,
document packages and demo processes that make the accelerator demonstrable.

```bash
git clone https://github.com/erturkm/sales-process-configurator.git
cd sales-process-configurator/src

az login
export DATAVERSE_URL="https://yourorg.crm.dynamics.com"

python3 step4c_team_roles.py        # teams and security roles
python3 step24_sales_foundation.py  # product lines, document packages, sample accounts
python3 step25_sales_processes.py   # demo sales processes
python3 step55_sales_pipeline.py    # a sample pipeline of deals
```

To enable Copilot authoring, also configure the Foundry connection:

```bash
export SPC_FOUNDRY_ENDPOINT="https://<your-resource>.openai.azure.com"
export SPC_FOUNDRY_DEPLOYMENT="gpt-4.1"
export SPC_FOUNDRY_API_VERSION="2024-12-01-preview"
export SPC_FOUNDRY_TENANT_ID="<tenant guid>"
export SPC_FOUNDRY_CLIENT_ID="<app registration id>"
export SPC_FOUNDRY_CLIENT_SECRET="<client secret>"

python3 step46_foundry_config.py
```

> [!IMPORTANT]
> `step46_foundry_config.py` writes the secret into a Dataverse environment variable **value**.
> Values are deliberately *not* solution components, so re-exporting the solution will never carry
> your credential out of the environment. Do not add them to the solution manually.

---

## Option B — build from source

This rebuilds every component from scratch against your environment. Each step is idempotent and
can be safely re-run.

```bash
git clone https://github.com/erturkm/sales-process-configurator.git
cd sales-process-configurator/src

az login
export DATAVERSE_URL="https://yourorg.crm.dynamics.com"
```

Run the steps in order:

```bash
# Foundation
python3 step1_solution.py            # solution and publisher
python3 step2_tables.py              # custom tables
python3 step3_relationships.py       # relationships
python3 step4c_team_roles.py         # teams and security roles
python3 step5_runtime_columns.py     # runtime columns on opportunity and task

# Engine
python3 step7_register_plugin.py     # plug-in assembly and steps
python3 step13_register_apis.py      # custom APIs
python3 step12_outcome_schema.py     # outcome schema

# Experience
python3 step9_forms_views.py         # forms and views
python3 step11_app.py                # model-driven app
python3 step16_designer.py           # visual process designer
python3 step17_outcome_picker.py     # outcome picker on the task form
python3 step18_opportunity_widgets.py# deal panel, process and documents widgets
python3 step22_workload.py           # my work dashboard
python3 step51_task_sla_timer.py     # SLA timer on task records

# AI agents
python3 step41_agent_model.py        # agent configuration model
python3 step42_register_agent_apis.py
python3 step49_agent_flow.py
python3 step53_wire_sales_agents.py

# Demo content
python3 step24_sales_foundation.py   # product lines, document packages, accounts
python3 step25_sales_processes.py    # demo sales processes
python3 step55_sales_pipeline.py     # sample pipeline
```

Optional — Copilot authoring (see the environment variables above):

```bash
python3 step46_foundry_config.py
python3 step47_register_design_api.py
```

### Rebuilding the plug-in assembly

Only needed if you change the C# source:

```bash
cd src/plugin
dotnet build -c Release
cd ..
python3 step7_register_plugin.py       # re-register the assembly
python3 step13_register_apis.py        # re-point the custom APIs
python3 step47_register_design_api.py  # if you use the design Copilot
```

---

## Verification

```bash
python3 step52_sales_e2e.py            # end-to-end engine test
python3 step54_sales_hybrid_test.py    # human plus AI agent path
```

Then check in the app:

1. Open the **Sales Process Configurator** app.
2. Go to **Sales Processes** and open one — confirm match rules, tasks and documents are present.
3. Open the **Process Designer** and confirm the task graph renders with its stage lanes.
4. Create an opportunity with a product line covered by a template, save it, and confirm:
   - the deal clock appears with qualification and close dials,
   - the sales process tab lists the first tasks with owners,
   - the required documents tab lists the expected documents,
   - the business process flow is set to the configured starting stage.

---

## Post-installation notes

**Shared artefacts.** The installer edits the Opportunity main form and the app site map. If you
have your own customisations on the Opportunity form, review them after import.

**Coexistence with the Case Process Configurator.** The two solutions are fully isolated and can be
installed side by side. They both add columns to the shared `task` table, but each is prefixed
(`spc_` and `cpc_`) and each widget ignores tasks belonging to the other.

**No SLA on opportunity.** Dataverse does not permit platform SLAs on the Opportunity table
(`IsSLAEnabled` is false and cannot be set on a system table via the API). The accelerator
therefore renders its own deal clock from the engine's qualification and close columns. Task-level
SLAs *are* platform SLAs, because the `task` table is SLA-enabled.

---

## Troubleshooting

| Symptom | Cause and fix |
|---|---|
| Import fails on the plug-in assembly | The environment must allow sandboxed plug-in registration. Use a developer or sandbox environment. |
| No process applies to a new deal | The template must be **active**, and its match rules must resolve against the product lines actually on the deal. Use **Test rules** in the designer. |
| Deal clock shows nothing | The deal has no applied process. Confirm a template matched, and that the plug-in step on Opportunity create is registered and enabled. |
| Copilot authoring returns an error | The Foundry environment variable **values** are blank or wrong. Re-run `step46_foundry_config.py` with the environment variables set. |
| `DATAVERSE_URL is not set` | Export it in the same shell before running any step. |
| Publish lock / `0x80071151` | Another solution operation is running in the environment. The scripts retry automatically; if it persists, wait and re-run. |

---

## Uninstall

For a managed install, delete the **Sales Process Configurator** solution from
**Solutions** in the admin centre. This removes the tables, plug-in, custom APIs, web resources and
app.

Sample data created by the demo scripts (accounts, opportunities, teams) is **not** removed by
uninstalling, because it lives in standard tables. Delete it manually, or simply discard the
environment — which is the recommended approach for a demonstration accelerator.
