const fs = require("fs");
const { Document, Packer, Paragraph, TextRun, HeadingLevel, AlignmentType,
        LevelFormat, PageOrientation } = require("docx");

const H1 = t => new Paragraph({ heading: HeadingLevel.HEADING_1, children: [new TextRun(t)] });
const H2 = t => new Paragraph({ heading: HeadingLevel.HEADING_2, children: [new TextRun(t)] });
const P  = t => new Paragraph({ children: [new TextRun(t)] });
const B  = t => new Paragraph({ numbering: { reference: "bullets", level: 0 },
                                children: [new TextRun(t)] });

const doc = new Document({
  styles: {
    default: { document: { run: { font: "Arial", size: 22 } } },
    paragraphStyles: [
      { id: "Heading1", name: "Heading 1", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 30, bold: true, font: "Arial" },
        paragraph: { spacing: { before: 280, after: 160 }, outlineLevel: 0 } },
      { id: "Heading2", name: "Heading 2", basedOn: "Normal", next: "Normal", quickFormat: true,
        run: { size: 25, bold: true, font: "Arial" },
        paragraph: { spacing: { before: 200, after: 120 }, outlineLevel: 1 } },
    ],
  },
  numbering: { config: [{ reference: "bullets", levels: [{ level: 0, format: LevelFormat.BULLET,
    text: "\u2022", alignment: AlignmentType.LEFT,
    style: { paragraph: { indent: { left: 720, hanging: 360 } } } }] }] },
  sections: [{
    properties: { page: { size: { width: 11906, height: 16838 },
                          margin: { top: 1440, right: 1440, bottom: 1440, left: 1440 } } },
    children: [
      new Paragraph({ alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: "RAKBANK \u2014 Wholesale Banking", bold: true, size: 26 })] }),
      new Paragraph({ alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: "SLS-TRD-021  Trade Finance Facility Origination Procedure",
                                 bold: true, size: 34 })] }),
      new Paragraph({ alignment: AlignmentType.CENTER,
        children: [new TextRun({ text: "Version 4.2  \u00b7  Effective 1 July 2026  \u00b7  Owner: Head of Trade Finance Sales",
                                 italics: true, size: 19 })] }),

      H1("1. Purpose and scope"),
      P("This procedure governs how the bank originates, assesses and books a trade finance facility for a corporate or mid-market customer in the United Arab Emirates. It applies to every opportunity where the customer is seeking a documentary letter of credit line, an import or export collection line, a bank guarantee or standby letter of credit line, an invoice or receivables discounting line, or a supply chain finance programme."),
      P("It does not apply to unsecured working capital term lending, which is governed by SLS-CRD-014, nor to retail or personal banking products. Where a customer requests both a trade line and a term facility, the two are originated separately and only the credit approval is combined."),
      P("Typical facility sizes run from AED 2 million to AED 250 million. Anything above AED 250 million is escalated to Group Credit Committee and follows the same steps with an additional approval layer."),

      H1("2. Roles"),
      B("Trade Sales \u2014 owns the customer relationship and the commercial conversation. Qualifies the request and is accountable for the deal until it is booked."),
      B("Trade Product \u2014 structures the facility, sets pricing against the product tariff, and confirms the operational feasibility of what has been promised."),
      B("Credit Risk \u2014 independent assessment of the borrower and of the trade cycle. Owns the credit recommendation."),
      B("Compliance and Financial Crime \u2014 KYC, sanctions, dual-use goods and counterparty country screening."),
      B("Legal and Documentation \u2014 facility agreement, security documentation and conditions precedent."),
      B("Trade Operations \u2014 limit loading, facility activation and post-booking servicing."),

      H1("3. Stages of the process"),
      P("A trade finance opportunity moves through six stages. A deal may not advance to the next stage until every blocking step in the current stage is complete."),
      B("Origination \u2014 the request is captured, qualified and screened."),
      B("Credit Assessment \u2014 the borrower, the trade cycle and the counterparties are analysed."),
      B("Credit Approval \u2014 the recommendation is put to the appropriate approval authority."),
      B("Documentation \u2014 the offer is issued, accepted and documented."),
      B("Disbursement \u2014 limits are loaded and the facility is activated."),
      B("Monitoring \u2014 utilisation, covenants and the annual review date are set up."),

      H1("4. Origination"),
      H2("4.1 Qualify the trade request"),
      P("Trade Sales must establish, within two working days of the request being logged, what the customer actually trades, with whom, in which countries, on what payment terms, and what the annual turnover of that trade flow is. A request that cannot describe the underlying trade cycle is not a trade finance request and must be redirected. The qualification is recorded on the opportunity and the deal is either taken forward or closed as not bankable."),
      H2("4.2 KYC and sanctions screening"),
      P("Compliance and Financial Crime screen the borrower, its beneficial owners, its declared counterparties and the countries in the trade corridor. Screening covers UN, OFAC, EU, UK and UAE local lists. Any hit, and any exposure to a comprehensively sanctioned jurisdiction, stops the deal until it is cleared in writing by the Head of Financial Crime. This step blocks the stage \u2014 nothing proceeds without it."),
      H2("4.3 Assemble the trade information pack"),
      P("The following are required before credit assessment can begin:"),
      B("Audited financial statements for the last three years"),
      B("Management accounts for the latest quarter"),
      B("Trade licence and memorandum of association"),
      B("Board resolution to borrow"),
      B("A schedule of the last twelve months of trade transactions, showing counterparty, country, goods and value"),
      B("Copies of three representative contracts or proforma invoices"),
      B("Bank statements for the last six months from all banks"),
      B("Existing facility letters from other banks, if any"),
      P("Collating this pack, chasing the missing items and checking each document for completeness and currency is routine, high-volume work. It is a good candidate to be prepared automatically, with the relationship manager confirming the result. Where a mandatory item is missing the pack is not complete and the deal cannot move on."),

      H1("5. Credit Assessment"),
      H2("5.1 Analyse the trade cycle"),
      P("The bank must understand how long the customer's cash is tied up: from placing the order, through shipment and customs, to receiving payment. The facility tenor must match that cycle. A 90 day LC line against a 180 day cash conversion cycle will fail. This analysis draws on the transaction schedule, the contracts and the bank statements, and produces a stated cycle length in days with the evidence for it."),
      H2("5.2 Spread the financials"),
      P("Credit Risk spreads the last three years of audited accounts and the latest management accounts into the bank's standard template, and computes leverage, interest cover, current ratio, working capital and the trend in each. The output is a recommendation on whether the financials support the requested limit. This is analytical work against a defined template and is well suited to being drafted in advance of review."),
      H2("5.3 Assess counterparty and country risk"),
      P("Each material counterparty and each corridor country is assessed for payment risk, political risk and transferability. Where a counterparty accounts for more than 25 per cent of the trade flow, it must be named and assessed individually."),
      H2("5.4 Confirm security and collateral"),
      P("Trade facilities are typically secured on the goods, on the receivable, or by cash margin. Credit Risk confirms what security is available, how it is perfected, and what margin is required. Any decision to lend unsecured is a human decision and is taken by the Credit Risk officer, never delegated."),

      H1("6. Credit Approval"),
      H2("6.1 Write the credit application"),
      P("A single credit application paper is prepared bringing together the borrower, the trade cycle, the financial analysis, the counterparty assessment, the proposed structure, pricing, security and the recommendation. The paper may be drafted in advance, but it is always reviewed, amended and signed off by a named credit officer before it goes anywhere. The bank does not put an unreviewed paper in front of its approvers."),
      H2("6.2 Obtain approval"),
      P("The application goes to the appropriate authority by size: up to AED 25 million, the Head of Credit Risk; up to AED 100 million, the Credit Committee; above that, Group Credit Committee. The approval decision is a human decision. Target turnaround is five working days for committee cases."),

      H1("7. Documentation"),
      H2("7.1 Issue the offer letter"),
      P("Legal and Documentation prepare the facility offer letter reflecting the approved terms exactly, including any conditions imposed by the approver. The letter is sent to the customer by the relationship manager, who talks the customer through it. Anything that goes to the customer over the bank's name is issued by a person."),
      H2("7.2 Execute documentation and security"),
      P("Facility agreement, trade-specific annexes, guarantees and security documents are executed, and security is registered where registration is required. Legal confirm the documentation is complete and enforceable."),
      H2("7.3 Satisfy conditions precedent"),
      P("Every condition precedent must be evidenced and signed off before the limit can be loaded. A checklist is maintained and each item is individually cleared."),

      H1("8. Disbursement"),
      H2("8.1 Load the limits"),
      P("Trade Operations load the approved sub-limits into the core system: LC sight, LC usance, guarantees, discounting, each with its own cap and tenor. The aggregate cap must be respected. The loading is checked by a second operations officer."),
      H2("8.2 Activate and hand over"),
      P("The facility is activated, the customer and the trade desk are notified, and the relationship manager hands the customer over to day-to-day servicing, confirming with the customer that they know how to draw on the line."),

      H1("9. Monitoring"),
      H2("9.1 Set up utilisation and covenant monitoring"),
      P("Utilisation reporting, covenant test dates, the expiry date of each sub-limit and the annual review date are all diarised at the point of booking. Setting these up is mechanical and follows directly from the approved terms, so it can be prepared automatically and confirmed."),
      H2("9.2 Annual review"),
      P("Every trade facility is reviewed at least annually, and immediately on any covenant breach, adverse counterparty news or material change in the trade pattern."),

      H1("10. Service standards"),
      B("Qualification decision: 2 working days from the request being logged"),
      B("Information pack complete: 10 working days from qualification"),
      B("Credit recommendation: 5 working days from a complete pack"),
      B("Approval: 5 working days for committee cases, 2 for delegated authority"),
      B("Offer letter issued: 3 working days from approval"),
      B("Limits loaded and facility live: 3 working days from conditions precedent being satisfied"),
      B("End to end target from qualification to live facility: 30 working days"),

      H1("11. Automation policy"),
      P("The bank is comfortable using automated assistance for assembling information, analysing documents, drafting analysis and setting up monitoring, provided the output is evidenced and can be checked. The limit is authority, not capability."),
      P("Four things are never delegated: the credit decision itself, any decision to lend unsecured or to waive security, anything issued to the customer over the bank's name, and the loading of a limit into the core banking system. Everything else may be prepared automatically and confirmed by the responsible team."),
      P("Where an automated step is not confident, or where a mandatory input is missing, it must say so and hand the work to the responsible team rather than guess."),
    ],
  }],
});

Packer.toBuffer(doc).then(b => {
  fs.writeFileSync(__dirname + "/RAKBANK_SLS-TRD-021_Trade_Finance_Origination.docx", b);
  console.log("written", b.length, "bytes");
});
