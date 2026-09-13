"""Creates business process flows on the case entity from a plain stage/field description.

Dataverse exposes no supported "create a BPF" message, so this assembles the two payloads the
process designer produces - `clientdata` (JSON) and `xaml` - from one shared plan so the ids,
labels and stage GUIDs in both always agree. Both are mandatory: posting a workflow of
category 4 without `xaml` fails with 0x80040203 "workflowstep".

Shapes were taken from the out of the box Phone to Case Process in this org.
"""
import json
import uuid

import dv

# Control class ids used by the process designer, keyed by the kind of column being placed.
CLASS = {
    "lookup":   "270BD3DB-D9AF-4782-9025-509E298DEC0A",
    "picklist": "3EF39988-22BB-4F0B-BBBE-64B5A3748AEE",
    "text":     "4273EDBD-AC1D-40D3-9FB2-095C621B552D",
    "memo":     "E0DECE4B-6FC8-4A8F-A065-082708572369",
    "datetime": "5B773807-9FB2-42DB-97C3-7A91EFF8ADFF",
    "money":    "533B9E00-756B-4312-95A0-DC888637AC78",
    "decimal":  "C3EFE0C3-0EC6-42BE-8349-CBD9079DFD8E",
    "boolean":  "67FAC785-CD58-4F9F-ABB3-4B7DDC6ED5ED",
}

# stagecategory option values that exist on every org.
CATEGORY = {"qualify": 0, "develop": 1, "propose": 2, "close": 3,
            "identify": 4, "research": 5, "resolve": 6}

_AQN = ("Microsoft.Crm.Workflow.Activities.{0}, Microsoft.Crm.Workflow, Version=6.0.0.0, "
        "Culture=neutral, PublicKeyToken=31bf3856ad364e35")

_XAML_HEAD = """<?xml version="1.0" encoding="utf-8"?>
<Activity x:Class="XrmWorkflow00000000000000000000000000000000" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" xmlns:mcwb="clr-namespace:Microsoft.Crm.Workflow.BusinessProcessFlowActivities;assembly=Microsoft.Crm.Workflow, Version=6.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mcwo="clr-namespace:Microsoft.Crm.Workflow.ObjectModel;assembly=Microsoft.Crm, Version=6.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mva="clr-namespace:Microsoft.VisualBasic.Activities;assembly=System.Activities, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mxs="clr-namespace:Microsoft.Xrm.Sdk;assembly=Microsoft.Xrm.Sdk, Version=6.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:mxswa="clr-namespace:Microsoft.Xrm.Sdk.Workflow.Activities;assembly=Microsoft.Xrm.Sdk.Workflow, Version=6.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35" xmlns:scg="clr-namespace:System.Collections.Generic;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" xmlns:sco="clr-namespace:System.Collections.ObjectModel;assembly=mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" xmlns:srs="clr-namespace:System.Runtime.Serialization;assembly=System.Runtime.Serialization, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" xmlns:this="clr-namespace:" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <x:Members>
    <x:Property Name="InputEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
    <x:Property Name="CreatedEntities" Type="InArgument(scg:IDictionary(x:String, mxs:Entity))" />
  </x:Members>
  <this:XrmWorkflow00000000000000000000000000000000.InputEntities>
    <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
  </this:XrmWorkflow00000000000000000000000000000000.InputEntities>
  <this:XrmWorkflow00000000000000000000000000000000.CreatedEntities>
    <InArgument x:TypeArguments="scg:IDictionary(x:String, mxs:Entity)" />
  </this:XrmWorkflow00000000000000000000000000000000.CreatedEntities>
  <mva:VisualBasic.Settings>Assembly references and imported namespaces for internal implementation</mva:VisualBasic.Settings>
  <mxswa:Workflow>
"""

_XAML_TAIL = """  </mxswa:Workflow>
</Activity>"""


def _esc(s):
    return (str(s).replace("&", "&amp;").replace("<", "&lt;")
            .replace(">", "&gt;").replace('"', "&quot;"))


def _plan(stages):
    """One numbered plan that both the JSON and the XAML are rendered from."""
    n = [1]

    def nxt():
        n[0] += 1
        return n[0]

    out = []
    for sname, cat, fields in stages:
        steps = []
        for flabel, fname, kind, required in fields:
            ctrl_i = nxt()
            step_i = nxt()
            steps.append({
                "ctrl_i": ctrl_i, "step_i": step_i, "label": flabel,
                "field": fname, "cls": CLASS[kind], "required": bool(required),
                "guid": str(uuid.uuid4()),
            })
        out.append({"i": nxt(), "name": sname, "cat": CATEGORY[cat],
                    "guid": str(uuid.uuid4()), "steps": steps})
    return out, n[0] + 1


def _clientdata(name, description, entity, plan, last_index, workflow_id):
    def label(text, guid):
        return {"list": [{"labelId": guid, "languageCode": 1033, "description": text}]}

    stage_steps = []
    for st in plan:
        steps = []
        for s in st["steps"]:
            steps.append({
                "__class": "StepStep:#Microsoft.Crm.Workflow.ObjectModel",
                "id": "StepStep" + str(s["step_i"]), "description": s["field"], "name": None,
                "stepLabels": label(s["label"], s["guid"]),
                "steps": {"list": [{
                    "__class": "ControlStep:#Microsoft.Crm.Workflow.ObjectModel",
                    "id": "ControlStep" + str(s["ctrl_i"]), "description": "",
                    "name": "Step_" + str(s["ctrl_i"]), "stepLabels": {"list": []},
                    "controlId": s["field"], "classId": s["cls"], "dataFieldName": s["field"],
                    "systemStepType": "IdentifyContact", "isSystemControl": False,
                    "parameters": "", "controlDisplayName": s["field"],
                    "isUnbound": False, "controlType": "0",
                }]},
                "stepStepId": s["guid"],
                "isProcessRequired": s["required"], "isHidden": False,
            })
        stage_steps.append({
            "__class": "StageStep:#Microsoft.Crm.Workflow.ObjectModel",
            "id": "StageStep" + str(st["i"]), "description": st["name"],
            "name": "Step_" + str(st["i"]),
            "stepLabels": label(st["name"], st["guid"]),
            "steps": {"list": steps},
            "stageId": st["guid"], "nextStageId": None, "stageCategory": str(st["cat"]),
        })

    return json.dumps({
        "__class": "WorkflowStep:#Microsoft.Crm.Workflow.ObjectModel",
        "id": "WorkflowStep0", "description": description, "name": "Step_0",
        "stepLabels": {"list": []},
        "steps": {"list": [{
            "__class": "EntityStep:#Microsoft.Crm.Workflow.ObjectModel",
            "id": "EntityStep1", "description": entity, "name": "Step_1",
            "stepLabels": {"list": []}, "steps": {"list": stage_steps},
            "relationshipName": None, "attributeName": None, "isClosedLoop": False,
        }]},
        "primaryEntityName": entity, "nextStepIndex": str(last_index),
        "isCrmUIWorkflow": True, "category": "4", "businessProcessType": "0", "mode": "0",
        "title": name, "workflowEntityId": workflow_id,
        "formId": None, "argumentsArray": [], "variables": [], "inputs": [],
    })


def _xaml(entity, plan):
    o = [_XAML_HEAD]
    o.append('    <mxswa:ActivityReference AssemblyQualifiedName="%s" DisplayName="EntityStep1: %s">\n'
             % (_AQN.format("EntityComposite"), _esc(entity)))
    o.append('      <mxswa:ActivityReference.Properties>\n')
    o.append('        <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />\n')
    o.append('        <sco:Collection x:TypeArguments="Activity" x:Key="Activities">\n')

    for st in plan:
        o.append('          <mxswa:ActivityReference AssemblyQualifiedName="%s" DisplayName="StageStep%d: %s">\n'
                 % (_AQN.format("StageComposite"), st["i"], _esc(st["name"])))
        o.append('            <mxswa:ActivityReference.Properties>\n')
        o.append('              <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />\n')
        o.append('              <sco:Collection x:TypeArguments="Activity" x:Key="Activities">\n')
        for s in st["steps"]:
            g = s["guid"].upper()
            o.append('                <mxswa:ActivityReference AssemblyQualifiedName="%s" DisplayName="StepStep%d: %s">\n'
                     % (_AQN.format("StepComposite"), s["step_i"], _esc(s["field"])))
            o.append('                  <mxswa:ActivityReference.Properties>\n')
            o.append('                    <sco:Collection x:TypeArguments="Variable" x:Key="Variables" />\n')
            o.append('                    <sco:Collection x:TypeArguments="Activity" x:Key="Activities">\n')
            o.append('                      <Sequence DisplayName="ControlStep%d">\n' % s["ctrl_i"])
            o.append('                        <mcwb:Control ClassId="%s" ControlDisplayName="%s" ControlId="%s" '
                     'DataFieldName="%s" IsSystemControl="False" Parameters="" SystemStepType="0" />\n'
                     % (s["cls"], _esc(s["field"]), _esc(s["field"]), _esc(s["field"])))
            o.append('                      </Sequence>\n')
            o.append('                    </sco:Collection>\n')
            o.append('                    <sco:Collection x:TypeArguments="mcwo:StepLabel" x:Key="StepLabels">\n')
            o.append('                      <mcwo:StepLabel Description="%s" LabelId="%s" LanguageCode="1033" />\n'
                     % (_esc(s["label"]), g))
            o.append('                    </sco:Collection>\n')
            o.append('                    <x:String x:Key="ProcessStepId">%s</x:String>\n' % g)
            o.append('                    <x:Boolean x:Key="IsProcessRequired">%s</x:Boolean>\n'
                     % ("True" if s["required"] else "False"))
            o.append('                  </mxswa:ActivityReference.Properties>\n')
            o.append('                </mxswa:ActivityReference>\n')
        o.append('              </sco:Collection>\n')
        o.append('              <sco:Collection x:TypeArguments="mcwo:StepLabel" x:Key="StepLabels">\n')
        o.append('                <mcwo:StepLabel Description="%s" LabelId="%s" LanguageCode="1033" />\n'
                 % (_esc(st["name"]), st["guid"].upper()))
        o.append('              </sco:Collection>\n')
        o.append('              <x:String x:Key="StageId">%s</x:String>\n' % st["guid"].upper())
        o.append('              <x:String x:Key="StageCategory">%d</x:String>\n' % st["cat"])
        o.append('            </mxswa:ActivityReference.Properties>\n')
        o.append('          </mxswa:ActivityReference>\n')

    o.append('        </sco:Collection>\n')
    o.append('        <x:Null x:Key="RelationshipName" />\n')
    o.append('        <x:Null x:Key="AttributeName" />\n')
    o.append('        <x:Boolean x:Key="IsClosedLoop">False</x:Boolean>\n')
    o.append('      </mxswa:ActivityReference.Properties>\n')
    o.append('    </mxswa:ActivityReference>\n')
    o.append(_XAML_TAIL)
    return "".join(o)


def upsert_bpf(name, unique, description, stages, entity="incident"):
    """Creates the flow if it is missing, then activates it. Returns (workflowid, {stage: id})."""
    ex = dv.find_one("workflows", f"uniquename eq '{unique}'", "workflowid,statecode")
    if ex:
        wid = ex["workflowid"]
        print(f"  = bpf exists: {name}")
    else:
        wid = str(uuid.uuid4())
        plan, last = _plan(stages)
        dv.post("workflows", {
            "workflowid": wid, "name": name, "uniquename": unique, "description": description,
            "category": 4, "type": 1, "mode": 0, "scope": 4,
            "primaryentity": entity, "ondemand": False, "istransacted": True,
            "businessprocesstype": 0, "languagecode": 1033,
            "clientdata": _clientdata(name, description, entity, plan, last, wid),
            "xaml": _xaml(entity, plan),
        })
        print(f"  + bpf created: {name}")

    if dv.get(f"workflows({wid})?$select=statecode")["statecode"] != 1:
        dv.patch(f"workflows({wid})", {"statecode": 1, "statuscode": 2})
        print("    activated")

    rows = dv.get("processstages?$select=processstageid,stagename,stagecategory"
                  f"&$filter=_processid_value eq {wid}")["value"]
    return wid, {r["stagename"]: r["processstageid"] for r in rows}
