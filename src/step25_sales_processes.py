"""Step 25: corporate and business banking sales processes.

Six published templates, each targeted at a branch of the product tree rather than at a
single SKU, so a deal picks up its process from what is actually on the deal lines. The
flagship is the corporate credit lifecycle - origination through to post-disbursement
monitoring - which is the end to end demo process.

Re-runnable: every write is keyed on name.
"""
import dv
import bpfgen

P = dv.PREFIX
NOW = "2026-09-01T06:00:00Z"
CAL = "UAE Sun-Thu 08:00-18:00"

POS, NEU, NEG, END = 1, 2, 3, 4
TEAM_, USER_, ROLE_, QUEUE_, MGR_, OWNER_, AI_ = 1, 2, 3, 4, 5, 6, 7

# opportunity.prioritycode has exactly one option in this org ("Default Value"), so every
# template leaves it alone. Writing anything else throws at match time.
PRIO = 0

LINE_ATTR = "productlines.productid"
UNDER = 11  # "is at or under" - the product tree hierarchy operator

# ------------------------------------------------------------------ business process flows
BPF_CREDIT = [
    ("Origination", "qualify", [
        ("Borrower", "customerid", "lookup", True),
        ("Facility amount", "estimatedvalue", "money", True)]),
    ("Credit Assessment", "develop", [
        ("Analyst", "ownerid", "lookup", False),
        ("Credit summary", "description", "memo", False)]),
    ("Credit Approval", "develop", [
        ("Target sanction date", "estimatedclosedate", "datetime", False)]),
    ("Documentation", "propose", [
        ("Documentation notes", "description", "memo", False)]),
    ("Disbursement", "close", [
        ("Drawdown date", "estimatedclosedate", "datetime", False)]),
    ("Monitoring", "close", [
        ("Relationship manager", "ownerid", "lookup", False)]),
]

BPF_TRADE = [
    ("Enquiry", "qualify", [
        ("Applicant", "customerid", "lookup", True),
        ("Instrument value", "estimatedvalue", "money", True)]),
    ("Structuring", "develop", [
        ("Structurer", "ownerid", "lookup", False)]),
    ("Credit and Compliance", "develop", [
        ("Assessment notes", "description", "memo", False)]),
    ("Issuance", "propose", [
        ("Issue date", "estimatedclosedate", "datetime", False)]),
    ("Settlement", "close", [
        ("Settlement notes", "description", "memo", False)]),
]

BPF_MANDATE = [
    ("Discovery", "qualify", [
        ("Client", "customerid", "lookup", True),
        ("Mandate value", "estimatedvalue", "money", False)]),
    ("Solution Design", "develop", [
        ("Solution owner", "ownerid", "lookup", False),
        ("Design notes", "description", "memo", False)]),
    ("Pricing and Approval", "propose", [
        ("Target start date", "estimatedclosedate", "datetime", False)]),
    ("Implementation", "close", [
        ("Implementation notes", "description", "memo", False)]),
    ("Go Live", "close", [
        ("Service owner", "ownerid", "lookup", False)]),
]

BPF_SME = [
    ("Application", "qualify", [
        ("Business", "customerid", "lookup", True),
        ("Amount requested", "estimatedvalue", "money", True)]),
    ("Assessment", "develop", [
        ("Assessor", "ownerid", "lookup", False)]),
    ("Decision", "propose", [
        ("Decision notes", "description", "memo", False)]),
    ("Drawdown", "close", [
        ("Funding date", "estimatedclosedate", "datetime", False)]),
]

ORDER = {
    "credit": ["Origination", "Credit Assessment", "Credit Approval",
               "Documentation", "Disbursement", "Monitoring"],
    "trade": ["Enquiry", "Structuring", "Credit and Compliance", "Issuance", "Settlement"],
    "mandate": ["Discovery", "Solution Design", "Pricing and Approval",
                "Implementation", "Go Live"],
    "sme": ["Application", "Assessment", "Decision", "Drawdown"],
}

# ------------------------------------------------------------------------ document packages
# (name, sequence, mandatory, responsible, due hours, template file, owner team)
CUST, DEAL, TEAMR, THIRD = 1, 2, 3, 4

PACKAGES = {
    "Corporate Credit File": [
        ("Audited financial statements - last 3 years", 1, True, CUST, 72, "", "Credit Origination"),
        ("Management accounts - latest quarter", 2, True, CUST, 72, "", "Credit Origination"),
        ("Trade licence and memorandum of association", 3, True, CUST, 48, "", "Legal and Documentation"),
        ("Board resolution to borrow", 4, True, CUST, 120, "", "Legal and Documentation"),
        ("Cash flow projections", 5, True, CUST, 96, "", "Credit Risk"),
        ("Security and collateral valuation report", 6, True, THIRD, 240, "", "Credit Risk"),
        ("Credit bureau report (Al Etihad)", 7, True, TEAMR, 24, "", "Credit Risk"),
        ("Group structure and beneficial ownership", 8, True, CUST, 72, "", "Compliance and Financial Crime"),
        ("Sanctions and adverse media screening result", 9, True, TEAMR, 24, "", "Compliance and Financial Crime"),
        ("Signed facility offer letter", 10, True, CUST, 168, "", "Legal and Documentation"),
        ("Executed security documents", 11, True, CUST, 240, "", "Legal and Documentation"),
        ("Disbursement instruction", 12, True, CUST, 48, "", "Loan Operations"),
    ],
    "Trade Finance Documentation": [
        ("Application form for the instrument", 1, True, CUST, 24, "", "Trade Operations"),
        ("Underlying commercial contract or purchase order", 2, True, CUST, 48, "", "Trade Operations"),
        ("Proforma invoice", 3, True, CUST, 48, "", "Trade Operations"),
        ("Shipping and transport documents", 4, False, CUST, 168, "", "Trade Operations"),
        ("Counterparty and country sanctions screening", 5, True, TEAMR, 24, "", "Compliance and Financial Crime"),
        ("Trade facility limit confirmation", 6, True, TEAMR, 24, "", "Credit Risk"),
        ("Signed indemnity and counter-guarantee", 7, True, CUST, 72, "", "Legal and Documentation"),
    ],
    "Cash Management Onboarding Pack": [
        ("Completed mandate and account opening form", 1, True, CUST, 72, "", "Onboarding Operations"),
        ("Authorised signatory list", 2, True, CUST, 72, "", "Onboarding Operations"),
        ("Board resolution for banking mandate", 3, True, CUST, 120, "", "Legal and Documentation"),
        ("Host to host connectivity specification", 4, False, DEAL, 168, "", "Digital Banking Support"),
        ("WPS employee file format sample", 5, False, CUST, 168, "", "Onboarding Operations"),
        ("Signed pricing schedule", 6, True, CUST, 120, "", "Deal Desk"),
        ("KYC refresh confirmation", 7, True, TEAMR, 48, "", "Compliance and Financial Crime"),
    ],
    "SME Credit Pack": [
        ("Trade licence", 1, True, CUST, 24, "", "SME Sales"),
        ("Bank statements - last 12 months", 2, True, CUST, 48, "", "Credit Risk"),
        ("VAT returns - last 4 quarters", 3, True, CUST, 72, "", "Credit Risk"),
        ("Owner personal guarantee", 4, True, CUST, 120, "", "Legal and Documentation"),
        ("Credit bureau report (Al Etihad)", 5, True, TEAMR, 24, "", "Credit Risk"),
        ("Signed offer letter", 6, True, CUST, 120, "", "Legal and Documentation"),
    ],
    "Treasury Suitability Pack": [
        ("Client suitability and appropriateness assessment", 1, True, TEAMR, 24, "", "Treasury Sales"),
        ("ISDA master agreement", 2, True, CUST, 240, "", "Legal and Documentation"),
        ("Credit support annex", 3, False, CUST, 240, "", "Legal and Documentation"),
        ("Risk disclosure acknowledgement", 4, True, CUST, 48, "", "Compliance and Financial Crime"),
        ("Dealing mandate and authorised dealers", 5, True, CUST, 72, "", "Treasury Sales"),
    ],
    "Card Programme Pack": [
        ("Card programme application", 1, True, CUST, 48, "", "Corporate Coverage"),
        ("Cardholder list and limits schedule", 2, True, CUST, 96, "", "Card Operations"),
        ("Corporate liability agreement", 3, True, CUST, 120, "", "Legal and Documentation"),
        ("Expense policy for programme controls", 4, False, CUST, 168, "", "Card Operations"),
        ("Credit limit approval", 5, True, TEAMR, 48, "", "Credit Risk"),
    ],
}

# ------------------------------------------------------------------------------- templates
TEMPLATES = [

# ------------------------------------------------------------------------------------- 1
{
 "name": "Corporate Credit Facility - Full Lifecycle", "rank": 10, "status": 2,
 "desc": "The end to end corporate credit journey for any lending product: origination and "
         "mandate, financial spreading and credit assessment, delegated or committee sanction, "
         "legal documentation and security perfection, drawdown, and the first post-disbursement "
         "monitoring cycle. Fires on any deal with a Business Lending product on it.",
 "bpf": "credit", "startstage": "Origination",
 "package": "Corporate Credit File", "qual": 48, "close": 1440,
 "rules": [(1, 1, LINE_ATTR, "Product", UNDER, "", "Business Lending")],
 "tasks": [
  (1, "Qualify the borrowing request", "Origination", TEAM_, "Corporate Coverage",
   24, 1, 24, 75, 2, True, True,
   "Confirm who is borrowing, how much, for what purpose and over what tenor. A facility "
   "request without a purpose and a repayment source is not yet a deal."),
  (2, "Run KYC and sanctions screening", "Origination", TEAM_, "Compliance and Financial Crime",
   48, 3, 48, 75, 3, True, True,
   "Screen the borrower, its group and its beneficial owners. Nothing may progress to credit "
   "assessment while a screening hit is open."),
  (3, "Collect the credit information pack", "Origination", TEAM_, "Credit Origination",
   96, 3, 72, 80, 2, True, True,
   "Chase the financial statements, management accounts and projections. Log what is still "
   "outstanding rather than waiting silently."),
  (4, "Spread the financials and build the credit model", "Credit Assessment", TEAM_, "Credit Risk",
   72, 3, 48, 80, 2, True, True,
   "Normalise three years of accounts, compute leverage, interest cover and working capital "
   "cycle, and stress the projections. Flag any restatement or auditor qualification."),
  (5, "Assess security and collateral", "Credit Assessment", TEAM_, "Credit Risk",
   96, 3, 72, 80, 2, True, True,
   "Value the proposed security, check it is unencumbered and registrable, and compute the "
   "loan to value against policy."),
  (6, "Write the credit application", "Credit Assessment", TEAM_, "Credit Risk",
   72, 3, 48, 80, 2, True, True,
   "Produce the credit paper with a clear recommendation, the proposed covenants and the "
   "conditions precedent. State the downside case explicitly."),
  (7, "Obtain sanction", "Credit Approval", TEAM_, "Credit Committee",
   120, 2, 96, 85, 3, True, True,
   "Route to the approving authority for the exposure band. Record the conditions attached to "
   "the sanction, not just the decision."),
  (8, "Issue the facility offer letter", "Documentation", TEAM_, "Legal and Documentation",
   48, 3, 48, 80, 2, True, True,
   "Draft the offer on sanctioned terms. Any deviation from the sanction has to go back to the "
   "approver, however small it looks."),
  (9, "Execute security and perfect the charge", "Documentation", TEAM_, "Legal and Documentation",
   240, 3, 168, 80, 3, True, True,
   "Get the security documents signed and registered. An unperfected charge is not security, "
   "it is paperwork."),
  (10, "Confirm conditions precedent are satisfied", "Documentation", TEAM_, "Credit Administration",
   96, 3, 72, 85, 3, True, True,
   "Tick off every condition precedent against evidence on file. This is the last gate before "
   "money can move."),
  (11, "Set up the facility limit and disburse", "Disbursement", TEAM_, "Loan Operations",
   48, 3, 24, 85, 3, True, True,
   "Load the limit, the pricing and the repayment schedule, then release the drawdown against "
   "the customer's instruction."),
  (12, "Schedule covenant testing and review date", "Monitoring", TEAM_, "Portfolio Monitoring",
   72, 3, 72, 75, 2, False, True,
   "Diarise the financial covenant tests, the information undertakings and the annual review. "
   "The facility is only as good as the monitoring behind it."),
  (13, "Hand over to the relationship manager", "Monitoring", OWNER_, "",
   48, 3, 48, 75, 2, False, True,
   "Walk the relationship manager through the sanction conditions and the monitoring calendar, "
   "then close the origination file."),
 ],
 "graph": [
  ("Qualify the borrowing request", "Request is bankable", POS,
   "Run KYC and sanctions screening", ["default"],
   "Purpose, amount, tenor and repayment source all stack up."),
  ("Qualify the borrowing request", "Outside credit appetite", END,
   None, ["lost", "comment"],
   "Sector, tenor or structure sits outside current appetite. Decline early and say why."),

  ("Run KYC and sanctions screening", "Screening clear", POS,
   "Collect the credit information pack", ["default", "advance"],
   "No hits, or hits cleared with evidence on file."),
  ("Run KYC and sanctions screening", "Screening hit - cannot proceed", END,
   None, ["lost", "comment"],
   "A sanctions or adverse media hit that cannot be cleared. Stop and escalate to MLRO."),

  ("Collect the credit information pack", "Pack complete", POS,
   "Spread the financials and build the credit model", ["default", "advance"],
   "Everything the credit team needs is on file."),
  ("Collect the credit information pack", "Customer did not supply", END,
   None, ["lost", "comment"],
   "Two chases and no financials. Close as lost so the pipeline stays honest."),

  ("Spread the financials and build the credit model", "Financials support the request", POS,
   "Assess security and collateral", ["default"],
   "Leverage and cover ratios are inside policy on the base case."),
  ("Spread the financials and build the credit model", "Weak - restructure the ask", NEU,
   "Assess security and collateral", ["comment"],
   "Serviceable only at a lower amount or a longer tenor. Reshape before it goes to credit."),
  ("Spread the financials and build the credit model", "Financials do not support any facility", END,
   None, ["lost", "comment"],
   "No repayment capacity on any reasonable structure."),

  ("Assess security and collateral", "Security acceptable", POS,
   "Write the credit application", ["default"],
   "Valuation, title and loan to value are all within policy."),
  ("Assess security and collateral", "Unsecured - proceed on covenants", NEU,
   "Write the credit application", ["comment"],
   "No tangible security. The case has to be carried by covenants and cash flow."),

  ("Write the credit application", "Application ready for sanction", POS,
   "Obtain sanction", ["default", "advance"],
   "Recommendation, covenants and conditions precedent are all documented."),

  ("Obtain sanction", "Approved as recommended", POS,
   "Issue the facility offer letter", ["default", "advance"],
   "Sanctioned on the proposed terms."),
  ("Obtain sanction", "Approved with amended terms", NEU,
   "Issue the facility offer letter", ["advance", "comment"],
   "Approved at a different amount, tenor or price. The offer must follow the sanction, not "
   "the application."),
  ("Obtain sanction", "Declined", END,
   None, ["lost", "comment"],
   "Committee declined. Record the reason so the relationship manager can have the "
   "conversation properly."),

  ("Issue the facility offer letter", "Offer accepted", POS,
   "Execute security and perfect the charge", ["default"],
   "Signed acceptance is on file within the validity period."),
  ("Issue the facility offer letter", "Offer lapsed or declined", END,
   None, ["lost", "comment"],
   "The customer walked away or let the offer expire."),

  ("Execute security and perfect the charge", "Security perfected", POS,
   "Confirm conditions precedent are satisfied", ["default"],
   "Documents signed, registered and filed."),
  ("Execute security and perfect the charge", "Registration blocked", NEG,
   "Confirm conditions precedent are satisfied", ["comment"],
   "A title defect or prior charge is holding registration up. Credit must decide whether to "
   "allow drawdown against an undertaking."),

  ("Confirm conditions precedent are satisfied", "All conditions met", POS,
   "Set up the facility limit and disburse", ["default", "advance"],
   "Every condition precedent has evidence behind it."),
  ("Confirm conditions precedent are satisfied", "Waiver required", NEU,
   "Set up the facility limit and disburse", ["advance", "comment"],
   "Proceeding on an approved waiver. The waiver and its expiry go on the file."),

  ("Set up the facility limit and disburse", "Funds released", POS,
   "Schedule covenant testing and review date", ["default", "advance"],
   "Limit loaded and drawdown settled to the customer's instruction."),

  ("Schedule covenant testing and review date", "Monitoring calendar set", POS,
   "Hand over to the relationship manager", ["default"],
   "Covenant tests, information undertakings and the review date are all diarised."),

  ("Hand over to the relationship manager", "Facility live and handed over", POS,
   None, ["won", "advance"],
   "The deal is done. Close it as won and let portfolio monitoring take it from here."),
 ],
},

# ------------------------------------------------------------------------------------- 2
{
 "name": "Trade Finance Facility", "rank": 12, "status": 2,
 "desc": "Issuance of a letter of credit, bank guarantee, documentary collection or supply "
         "chain finance line, from enquiry through structuring, credit and sanctions clearance, "
         "issuance and settlement.",
 "bpf": "trade", "startstage": "Enquiry",
 "package": "Trade Finance Documentation", "qual": 24, "close": 720,
 "rules": [(1, 1, LINE_ATTR, "Product", UNDER, "", "Trade Finance")],
 "tasks": [
  (1, "Take the instrument enquiry", "Enquiry", TEAM_, "Trade Operations",
   8, 1, 8, 75, 2, True, True,
   "Capture the instrument type, amount, currency, tenor, beneficiary and country. The country "
   "and the beneficiary drive everything that follows."),
  (2, "Screen counterparty and country risk", "Enquiry", TEAM_, "Compliance and Financial Crime",
   24, 3, 24, 75, 3, True, True,
   "Screen the beneficiary, the vessel and the route. Sanctioned goods or routes end the deal "
   "regardless of the customer relationship."),
  (3, "Structure the instrument and confirm wording", "Structuring", TEAM_, "Trade Operations",
   48, 3, 24, 80, 2, True, True,
   "Agree the wording with the customer and, where needed, with the advising bank. Ambiguous "
   "wording becomes a discrepancy later."),
  (4, "Confirm trade limit availability", "Credit and Compliance", TEAM_, "Credit Risk",
   24, 3, 24, 80, 3, True, True,
   "Check headroom on the trade line and the tenor cap. If there is no headroom, this is a "
   "credit application, not an issuance."),
  (5, "Obtain the indemnity and counter-guarantee", "Credit and Compliance", TEAM_, "Legal and Documentation",
   72, 3, 48, 80, 2, True, True,
   "Get the customer's indemnity signed by authorised signatories before anything is issued in "
   "the bank's name."),
  (6, "Issue the instrument", "Issuance", TEAM_, "Trade Operations",
   24, 3, 8, 85, 3, True, True,
   "Transmit and confirm receipt with the advising bank. Book the contingent liability the "
   "same day."),
  (7, "Handle presentation and settle", "Settlement", TEAM_, "Trade Operations",
   120, 3, 120, 80, 2, False, True,
   "Examine documents against the terms, raise discrepancies inside the banking days allowed, "
   "and settle or reimburse."),
 ],
 "graph": [
  ("Take the instrument enquiry", "Enquiry is workable", POS,
   "Screen counterparty and country risk", ["default"],
   "Instrument, amount and beneficiary are all clear."),
  ("Take the instrument enquiry", "Not a product we issue", END,
   None, ["lost", "comment"],
   "The structure asked for is not in the product set."),

  ("Screen counterparty and country risk", "Screening clear", POS,
   "Structure the instrument and confirm wording", ["default", "advance"],
   "Beneficiary, route and goods all clear."),
  ("Screen counterparty and country risk", "Sanctions exposure", END,
   None, ["lost", "comment"],
   "Sanctioned country, party or cargo. Decline and report internally."),

  ("Structure the instrument and confirm wording", "Wording agreed", POS,
   "Confirm trade limit availability", ["default", "advance"],
   "Both sides have signed off the operative wording."),

  ("Confirm trade limit availability", "Limit available", POS,
   "Obtain the indemnity and counter-guarantee", ["default"],
   "Headroom exists on the trade line for the amount and tenor."),
  ("Confirm trade limit availability", "No headroom - needs a credit application", END,
   None, ["comment"],
   "Send this back through the corporate credit lifecycle to get the line increased."),

  ("Obtain the indemnity and counter-guarantee", "Indemnity executed", POS,
   "Issue the instrument", ["default", "advance"],
   "Signed by authorised signatories and verified."),

  ("Issue the instrument", "Instrument issued", POS,
   "Handle presentation and settle", ["default", "advance"],
   "Transmitted, acknowledged and booked."),

  ("Handle presentation and settle", "Settled without discrepancy", POS,
   None, ["won", "advance"],
   "Documents complied and the instrument settled cleanly."),
  ("Handle presentation and settle", "Settled after discrepancy waiver", NEU,
   None, ["won", "comment"],
   "Discrepancies were raised and the applicant waived them."),
  ("Handle presentation and settle", "Expired unutilised", END,
   None, ["lost", "comment"],
   "The instrument lapsed without presentation. Release the contingent liability."),
 ],
},

# ------------------------------------------------------------------------------------- 3
{
 "name": "Cash Management Mandate", "rank": 14, "status": 2,
 "desc": "Winning and implementing a corporate cash management mandate: accounts, payroll and "
         "WPS, host to host connectivity and liquidity structures, through discovery, solution "
         "design, pricing approval and go live.",
 "bpf": "mandate", "startstage": "Discovery",
 "package": "Cash Management Onboarding Pack", "qual": 48, "close": 1080,
 "rules": [(1, 1, LINE_ATTR, "Product", UNDER, "", "Cash Management")],
 "tasks": [
  (1, "Map the client's current cash cycle", "Discovery", TEAM_, "Corporate Coverage",
   72, 1, 48, 75, 2, True, True,
   "Understand where the money actually sits and moves today: collection points, payment runs, "
   "payroll dates and idle balances."),
  (2, "Design the account and liquidity structure", "Solution Design", TEAM_, "Corporate Coverage",
   96, 3, 72, 80, 2, True, True,
   "Propose the account hierarchy, sweeping or pooling, and the payment channels. Keep it as "
   "simple as the client's treasury can actually run."),
  (3, "Scope host to host and file formats", "Solution Design", TEAM_, "Digital Banking Support",
   96, 3, 72, 80, 2, False, True,
   "Agree the file formats, the connectivity method and the test plan with the client's ERP "
   "team. Format surprises are what delay go live."),
  (4, "Price the mandate and get Deal Desk approval", "Pricing and Approval", TEAM_, "Deal Desk",
   72, 3, 48, 80, 2, True, True,
   "Build the fee schedule against expected volumes and float, and get it approved before it "
   "goes to the client."),
  (5, "Complete KYC refresh and mandate documentation", "Implementation", TEAM_, "Onboarding Operations",
   120, 3, 96, 80, 2, True, True,
   "Refresh KYC, collect the board resolution and register the authorised signatories."),
  (6, "Implement and test the channel", "Implementation", TEAM_, "Digital Banking Support",
   168, 3, 120, 80, 2, True, True,
   "Configure the channel, run penny tests and a full payroll dry run before any live file."),
  (7, "Confirm go live and first payment run", "Go Live", TEAM_, "Onboarding Operations",
   72, 3, 48, 85, 2, True, True,
   "Watch the first live run end to end. Nothing proves an implementation like a clean first "
   "payroll."),
 ],
 "graph": [
  ("Map the client's current cash cycle", "Opportunity confirmed", POS,
   "Design the account and liquidity structure", ["default", "advance"],
   "There is a real pain point and a budget owner."),
  ("Map the client's current cash cycle", "No appetite to change banks", END,
   None, ["lost", "comment"],
   "The client is happy where they are for now."),

  ("Design the account and liquidity structure", "Structure agreed", POS,
   "Scope host to host and file formats", ["default"],
   "The client's treasury has accepted the proposed structure."),

  ("Scope host to host and file formats", "Connectivity scoped", POS,
   "Price the mandate and get Deal Desk approval", ["default", "advance"],
   "Formats, method and test plan are agreed in writing."),
  ("Scope host to host and file formats", "Manual channel only", NEU,
   "Price the mandate and get Deal Desk approval", ["advance", "comment"],
   "The client will use the portal rather than host to host. Price accordingly."),

  ("Price the mandate and get Deal Desk approval", "Pricing approved and accepted", POS,
   "Complete KYC refresh and mandate documentation", ["default", "advance"],
   "Deal Desk approved it and the client signed the schedule."),
  ("Price the mandate and get Deal Desk approval", "Lost on price", END,
   None, ["lost", "comment"],
   "A competitor came in below the floor. Record the gap for the next review."),

  ("Complete KYC refresh and mandate documentation", "Documentation complete", POS,
   "Implement and test the channel", ["default"],
   "Mandate, signatories and KYC are all in order."),

  ("Implement and test the channel", "Testing signed off", POS,
   "Confirm go live and first payment run", ["default", "advance"],
   "Penny tests and the payroll dry run both passed."),
  ("Implement and test the channel", "Client ERP not ready", NEU,
   "Confirm go live and first payment run", ["comment"],
   "Go live slips to the client's ERP timetable. Keep the deal open and re-diarise."),

  ("Confirm go live and first payment run", "Mandate live", POS,
   None, ["won", "advance"],
   "First live run completed clean. The mandate is won."),
 ],
},

# ------------------------------------------------------------------------------------- 4
{
 "name": "Corporate Card Programme", "rank": 16, "status": 2,
 "desc": "Setting up a corporate credit card or purchasing card programme: scoping the "
         "cardholder population and controls, approving the programme limit, issuing cards and "
         "rolling out to the client's staff.",
 "bpf": "mandate", "startstage": "Discovery",
 "package": "Card Programme Pack", "qual": 24, "close": 720,
 "rules": [(1, 1, LINE_ATTR, "Product", UNDER, "", "Corporate Cards")],
 "tasks": [
  (1, "Scope the cardholder population and controls", "Discovery", TEAM_, "Corporate Coverage",
   48, 1, 48, 75, 2, True, True,
   "How many cards, what merchant category controls, what per-transaction and monthly limits, "
   "and who approves expenses."),
  (2, "Approve the programme credit limit", "Solution Design", TEAM_, "Credit Risk",
   72, 3, 48, 80, 3, True, True,
   "Assess the aggregate programme limit as an unsecured exposure, not as a card application."),
  (3, "Agree pricing and rebate schedule", "Pricing and Approval", TEAM_, "Deal Desk",
   48, 3, 48, 80, 2, True, True,
   "Interchange rebate, annual fees and FX mark-up. Model it against the client's expected "
   "spend, not against a standard tariff."),
  (4, "Collect cardholder data and issue cards", "Implementation", TEAM_, "Card Operations",
   120, 3, 96, 80, 2, True, True,
   "Load the cardholder schedule, run the embossing file and track delivery."),
  (5, "Run the cardholder onboarding session", "Go Live", TEAM_, "Corporate Coverage",
   72, 3, 48, 75, 2, False, True,
   "Walk the client's staff through activation, controls and expense submission. Programmes "
   "die quietly when cardholders never activate."),
 ],
 "graph": [
  ("Scope the cardholder population and controls", "Programme scoped", POS,
   "Approve the programme credit limit", ["default", "advance"],
   "Population, limits and controls are all agreed."),
  ("Scope the cardholder population and controls", "Too small to be viable", END,
   None, ["lost", "comment"],
   "A handful of cards will not carry the programme overhead."),

  ("Approve the programme credit limit", "Limit approved", POS,
   "Agree pricing and rebate schedule", ["default", "advance"],
   "The aggregate limit is sanctioned."),
  ("Approve the programme credit limit", "Declined on credit", END,
   None, ["lost", "comment"],
   "The unsecured exposure is outside appetite for this name."),

  ("Agree pricing and rebate schedule", "Pricing accepted", POS,
   "Collect cardholder data and issue cards", ["default", "advance"],
   "The client has signed the schedule."),

  ("Collect cardholder data and issue cards", "Cards issued", POS,
   "Run the cardholder onboarding session", ["default", "advance"],
   "Cards embossed and delivered."),

  ("Run the cardholder onboarding session", "Programme live", POS,
   None, ["won", "advance"],
   "Cardholders are activated and spending. Close as won."),
 ],
},

# ------------------------------------------------------------------------------------- 5
{
 "name": "SME Lending Application", "rank": 18, "status": 2,
 "desc": "A faster, lighter credit journey for small and medium business borrowing - business "
         "loans, overdrafts and equipment finance - scored from bureau data and bank statements "
         "rather than a full credit paper.",
 "bpf": "sme", "startstage": "Application",
 "package": "SME Credit Pack", "qual": 8, "close": 360,
 "rules": [(1, 1, LINE_ATTR, "Product", UNDER, "", "SME Lending")],
 "tasks": [
  (1, "Take the application and check eligibility", "Application", TEAM_, "SME Sales",
   8, 1, 8, 75, 2, True, True,
   "Trading history, turnover and sector against the SME policy grid. Most declines belong "
   "here, not three days later."),
  (2, "Pull bureau and score the application", "Assessment", TEAM_, "Credit Risk",
   24, 3, 24, 80, 2, True, True,
   "Pull the Al Etihad report, score the file and check the statements for returned cheques "
   "and bounced direct debits."),
  (3, "Decision the application", "Decision", TEAM_, "Credit Risk",
   24, 3, 24, 80, 3, True, True,
   "Approve, decline, or approve at a reduced amount. Give a reason the relationship manager "
   "can repeat to the customer."),
  (4, "Issue and collect the signed offer", "Decision", TEAM_, "Legal and Documentation",
   72, 3, 48, 80, 2, True, True,
   "Send the offer with the personal guarantee, and chase the signature."),
  (5, "Fund the facility", "Drawdown", TEAM_, "Loan Operations",
   24, 3, 24, 85, 3, True, True,
   "Load the limit and release the funds to the business account."),
 ],
 "graph": [
  ("Take the application and check eligibility", "Eligible", POS,
   "Pull bureau and score the application", ["default", "advance"],
   "Inside the policy grid on trading history, turnover and sector."),
  ("Take the application and check eligibility", "Outside policy", END,
   None, ["lost", "comment"],
   "Fails a hard policy rule. Decline now and explain it clearly."),

  ("Pull bureau and score the application", "Score passes", POS,
   "Decision the application", ["default", "advance"],
   "Bureau and conduct data support the request."),
  ("Pull bureau and score the application", "Adverse bureau", END,
   None, ["lost", "comment"],
   "Defaults or judgements on the bureau file."),

  ("Decision the application", "Approved as requested", POS,
   "Issue and collect the signed offer", ["default", "advance"],
   "Approved at the amount and tenor applied for."),
  ("Decision the application", "Approved at a reduced amount", NEU,
   "Issue and collect the signed offer", ["advance", "comment"],
   "A smaller facility is serviceable. Go back to the customer before issuing."),
  ("Decision the application", "Declined", END,
   None, ["lost", "comment"],
   "Credit declined. Record the reason code."),

  ("Issue and collect the signed offer", "Offer signed", POS,
   "Fund the facility", ["default", "advance"],
   "Signed offer and guarantee are on file."),
  ("Issue and collect the signed offer", "Customer did not sign", END,
   None, ["lost", "comment"],
   "The offer lapsed unsigned."),

  ("Fund the facility", "Funded", POS,
   None, ["won", "advance"],
   "Funds released. Close as won."),
 ],
},

# ------------------------------------------------------------------------------------- 6
{
 "name": "Treasury Markets Deal", "rank": 20, "status": 2,
 "desc": "Hedging deals - FX forwards and interest rate swaps - where the real work is "
         "suitability, documentation and disclosure rather than credit. Runs the ISDA and CSA "
         "path before any dealing mandate is accepted.",
 "bpf": "sme", "startstage": "Application",
 "package": "Treasury Suitability Pack", "qual": 8, "close": 480,
 "rules": [(1, 1, LINE_ATTR, "Product", UNDER, "", "Treasury and Markets")],
 "tasks": [
  (1, "Capture the hedging requirement", "Application", TEAM_, "Treasury Sales",
   8, 1, 8, 75, 2, True, True,
   "What exposure is being hedged, in what currency, over what horizon. A hedge without an "
   "underlying exposure is speculation and we do not sell it."),
  (2, "Complete the suitability assessment", "Assessment", TEAM_, "Treasury Sales",
   24, 3, 24, 80, 3, True, True,
   "Assess the client's knowledge, experience and capacity to bear loss, and record it. This "
   "is the document a regulator will ask for first."),
  (3, "Confirm pre-settlement risk limit", "Assessment", TEAM_, "Credit Risk",
   24, 3, 24, 80, 3, True, True,
   "Derivatives consume credit limit through mark to market. Confirm the PSR line before "
   "quoting."),
  (4, "Execute ISDA and credit support annex", "Decision", TEAM_, "Legal and Documentation",
   240, 3, 168, 80, 2, True, True,
   "Negotiate and execute the master agreement. Nothing can be dealt under a draft ISDA."),
  (5, "Accept the dealing mandate and price", "Drawdown", TEAM_, "Treasury Sales",
   24, 3, 8, 85, 2, True, True,
   "Verify the caller against the dealing mandate, quote, and confirm the trade in writing the "
   "same day."),
 ],
 "graph": [
  ("Capture the hedging requirement", "Genuine exposure to hedge", POS,
   "Complete the suitability assessment", ["default", "advance"],
   "There is an underlying commercial exposure behind the request."),
  ("Capture the hedging requirement", "No underlying exposure", END,
   None, ["lost", "comment"],
   "Speculative. Decline and record why."),

  ("Complete the suitability assessment", "Client is suitable", POS,
   "Confirm pre-settlement risk limit", ["default"],
   "Knowledge, experience and loss capacity all documented."),
  ("Complete the suitability assessment", "Not suitable for derivatives", END,
   None, ["lost", "comment"],
   "Offer a simpler product instead and record the assessment."),

  ("Confirm pre-settlement risk limit", "PSR limit available", POS,
   "Execute ISDA and credit support annex", ["default", "advance"],
   "Headroom confirmed for the notional and tenor."),
  ("Confirm pre-settlement risk limit", "No PSR limit", END,
   None, ["lost", "comment"],
   "Needs a credit application before any derivative can be dealt."),

  ("Execute ISDA and credit support annex", "ISDA executed", POS,
   "Accept the dealing mandate and price", ["default", "advance"],
   "Master agreement and, where required, the CSA are both signed."),
  ("Execute ISDA and credit support annex", "Negotiation stalled", END,
   None, ["lost", "comment"],
   "The client will not accept the credit terms. Park the deal."),

  ("Accept the dealing mandate and price", "Trade executed", POS,
   None, ["won", "advance"],
   "Dealt and confirmed. Close as won."),
 ],
},
]


# -------------------------------------------------------------------------------- writers
def upsert_package(name, items, teams):
    pkg = dv.find_one(f"{P}_documentpackages", f"{P}_name eq '{name}'", f"{P}_documentpackageid")
    if pkg:
        pid = pkg[f"{P}_documentpackageid"]
    else:
        pid = dv.new_id(dv.post(f"{P}_documentpackages", {
            f"{P}_name": name, f"{P}_active": True,
            f"{P}_description": f"Documents required for the {name.lower()}."}))
        print("  + package", name)
    have = {r[f"{P}_name"] for r in dv.get(
        f"{P}_documentitems?$select={P}_name&$filter=_{P}_package_value eq {pid}")["value"]}
    for iname, seq, mand, resp, due, tmpl, team in items:
        if iname in have:
            continue
        body = {f"{P}_name": iname, f"{P}_sequence": seq, f"{P}_mandatory": mand,
                f"{P}_responsible": resp, f"{P}_duehours": due,
                f"{P}_package@odata.bind": f"/{P}_documentpackages({pid})"}
        if tmpl:
            body[f"{P}_templatefile"] = tmpl
        if team in teams:
            body[f"{P}_ownerteam@odata.bind"] = f"/teams({teams[team]})"
        dv.post(f"{P}_documentitems", body)
    return pid


def upsert_template(t, ctx):
    bpf, stages = ctx["bpfs"][t["bpf"]]
    body = {
        f"{P}_name": t["name"], f"{P}_rank": t["rank"], f"{P}_publishstatus": t["status"],
        f"{P}_description": t["desc"], f"{P}_matchlogic": 3,
        f"{P}_bpfid": bpf["id"], f"{P}_bpfname": bpf["name"], f"{P}_bpfentityname": bpf["unique"],
        f"{P}_startstageid": stages[t["startstage"]], f"{P}_startstagename": t["startstage"],
        f"{P}_qualificationhours": t["qual"], f"{P}_closehours": t["close"],
        f"{P}_setopportunitypriority": PRIO, f"{P}_effectivefrom": NOW,
        f"{P}_documentpackage@odata.bind":
            f"/{P}_documentpackages({ctx['pkgs'][t['package']]})",
    }
    ex = dv.find_one(f"{P}_salesprocesstemplates", f"{P}_name eq '{t['name']}'",
                     f"{P}_salesprocesstemplateid")
    if ex:
        tid = ex[f"{P}_salesprocesstemplateid"]
        dv.patch(f"{P}_salesprocesstemplates({tid})", body)
        print("  ~ template", t["name"])
    else:
        body[f"{P}_appliedcount"] = 0
        tid = dv.new_id(dv.post(f"{P}_salesprocesstemplates", body))
        print("  + template", t["name"])

    have = {r[f"{P}_name"] for r in dv.get(
        f"{P}_matchrules?$select={P}_name&$filter=_{P}_template_value eq {tid}")["value"]}
    for seq, grp, attr, lbl, op, val, vlbl in t["rules"]:
        rn = f"{t['name']} :: {lbl} {seq}"
        if rn in have:
            continue
        # Product rules are written by family name; resolve to the record id here so the
        # seeding data stays readable.
        if attr == LINE_ATTR and not val:
            pr = ctx["products"].get(vlbl)
            if not pr:
                print("    ! product not found:", vlbl)
                continue
            val = pr
        dv.post(f"{P}_matchrules", {
            f"{P}_name": rn, f"{P}_sequence": seq, f"{P}_groupnumber": grp,
            f"{P}_attributename": attr, f"{P}_attributelabel": lbl,
            f"{P}_operator": op, f"{P}_value": val, f"{P}_valuelabel": vlbl,
            f"{P}_template@odata.bind": f"/{P}_salesprocesstemplates({tid})"})

    have_tasks = {r[f"{P}_name"]: r[f"{P}_processtaskid"] for r in dv.get(
        f"{P}_processtasks?$select={P}_name,{P}_processtaskid"
        f"&$filter=_{P}_template_value eq {tid}")["value"]}
    fallback = ctx["teams"].get("Deal Desk")
    prev = None
    for (seq, tname, stage, atype, assignee, due, slastart, slatarget,
         warn, breach, blocks, mand, instr) in t["tasks"]:
        if tname in have_tasks:
            prev = have_tasks[tname]
            continue
        b = {
            f"{P}_name": tname, f"{P}_sequence": seq, f"{P}_description": instr,
            f"{P}_stagename": stage, f"{P}_stageid": stages.get(stage, ""),
            f"{P}_assigntype": atype, f"{P}_duehours": due,
            f"{P}_slastartwhen": slastart, f"{P}_slatargethours": slatarget,
            f"{P}_slawarnpercent": warn, f"{P}_onbreach": breach,
            f"{P}_blocksstage": blocks, f"{P}_mandatory": mand,
            f"{P}_calendarname": CAL, f"{P}_pauseonwaiting": True,
            f"{P}_template@odata.bind": f"/{P}_salesprocesstemplates({tid})",
        }
        if atype == TEAM_ and assignee in ctx["teams"]:
            b[f"{P}_team@odata.bind"] = f"/teams({ctx['teams'][assignee]})"
        elif atype == QUEUE_ and assignee in ctx["queues"]:
            b[f"{P}_queue@odata.bind"] = f"/queues({ctx['queues'][assignee]})"
        elif atype == ROLE_ and assignee:
            b[f"{P}_rolename"] = assignee
        if fallback and atype in (TEAM_, QUEUE_, AI_):
            b[f"{P}_fallbackteam@odata.bind"] = f"/teams({fallback})"
        if prev:
            b[f"{P}_predecessor@odata.bind"] = f"/{P}_processtasks({prev})"
        prev = dv.new_id(dv.post(f"{P}_processtasks", b))
    return tid


# ------------------------------------------------------------- outcome graph + layout
SUB_W, ROWS_PER_LANE, STAGE_PAD, STAGE_GAP = 268, 3, 30, 18
ROW_H, ROW_Y0, LANE_STAGGER = 215, 200, 0.3

WON, LOST = 1, 2


def layout(tasks, stage_names):
    by_stage = {}
    for t in sorted(tasks.values(), key=lambda r: r[f"{P}_sequence"] or 0):
        by_stage.setdefault(t.get(f"{P}_stagename") or (stage_names[0] if stage_names else ""),
                            []).append(t)
    left = 0
    for name in stage_names:
        nodes = by_stage.get(name, [])
        lanes = max(1, -(-len(nodes) // ROWS_PER_LANE))
        width = lanes * SUB_W + 2 * STAGE_PAD
        for i, t in enumerate(nodes):
            lane, row = divmod(i, ROWS_PER_LANE)
            x = left + STAGE_PAD + lane * SUB_W + SUB_W / 2
            y = ROW_Y0 + (row + (lane % 2) * LANE_STAGGER) * ROW_H
            dv.patch(f"{P}_processtasks({t[f'{P}_processtaskid']})",
                     {f"{P}_posx": int(x), f"{P}_posy": int(y)})
        left += width + STAGE_GAP


def apply_graph(tid, spec, stage_names):
    tasks = {r[f"{P}_name"]: r for r in dv.get(
        f"{P}_processtasks?$select={P}_name,{P}_processtaskid,{P}_stagename,{P}_sequence"
        f"&$filter=_{P}_template_value eq {tid}")["value"]}

    missing = {n for e in spec for n in (e[0], e[3]) if n and n not in tasks}
    if missing:
        print("    ! unknown task(s):", missing)

    for o in dv.get(f"{P}_taskoutcomes?$select={P}_taskoutcomeid"
                    f"&$filter=_{P}_template_value eq {tid}")["value"]:
        dv.call("DELETE", f"{P}_taskoutcomes({o[f'{P}_taskoutcomeid']})")

    entry = spec[0][0]
    for name, t in tasks.items():
        dv.patch(f"{P}_processtasks({t[f'{P}_processtaskid']})", {f"{P}_isstart": name == entry})

    seq = 0
    for frm, lbl, sent, to, flags, guidance in spec:
        if frm not in tasks:
            continue
        seq += 1
        # A deal is never simply "resolved" - it is won or it is lost, and the outcome has
        # to say which so AdvanceProcess can issue the right close request.
        close = WON if "won" in flags else (LOST if "lost" in flags else 0)
        b = {
            f"{P}_name": lbl, f"{P}_description": guidance, f"{P}_sequence": seq,
            f"{P}_sentiment": sent,
            f"{P}_advancestage": "advance" in flags,
            f"{P}_closeopportunity": close,
            f"{P}_requirecomment": "comment" in flags, f"{P}_isdefault": "default" in flags,
            f"{P}_setopportunitypriority": PRIO,
            f"{P}_task@odata.bind": f"/{P}_processtasks({tasks[frm][f'{P}_processtaskid']})",
            f"{P}_template@odata.bind": f"/{P}_salesprocesstemplates({tid})",
        }
        jump = next((f.split(":", 1)[1] for f in flags if f.startswith("stage:")), None)
        if jump:
            b[f"{P}_targetstagename"] = jump
        if to and to in tasks:
            b[f"{P}_nexttask@odata.bind"] = f"/{P}_processtasks({tasks[to][f'{P}_processtaskid']})"
        dv.post(f"{P}_taskoutcomes", b)

    layout(tasks, stage_names)
    print(f"    {seq} outcome(s), {len(tasks)} task(s)")


def main():
    print("Business process flows")
    cr_id, cr_stages = bpfgen.upsert_bpf(
        "Corporate Credit Lifecycle", f"{P}_corporatecreditlifecycle",
        "Corporate lending from origination through credit assessment, sanction, documentation "
        "and disbursement to post-disbursement monitoring.",
        BPF_CREDIT, entity="opportunity")
    tr_id, tr_stages = bpfgen.upsert_bpf(
        "Trade Finance Deal", f"{P}_tradefinancedeal",
        "Trade instrument issuance from enquiry through structuring and clearance to settlement.",
        BPF_TRADE, entity="opportunity")
    mn_id, mn_stages = bpfgen.upsert_bpf(
        "Cash Management Mandate", f"{P}_cashmanagementmandate",
        "Winning and implementing a corporate cash management mandate.",
        BPF_MANDATE, entity="opportunity")
    sm_id, sm_stages = bpfgen.upsert_bpf(
        "SME Deal Journey", f"{P}_smedealjourney",
        "A light four stage journey for SME lending and other fast-moving business deals.",
        BPF_SME, entity="opportunity")

    ctx = {
        "teams": {t["name"]: t["teamid"] for t in
                  dv.get("teams?$select=teamid,name&$top=500")["value"]},
        "queues": {q["name"]: q["queueid"] for q in
                   dv.get("queues?$select=queueid,name&$top=500")["value"]},
        "products": {p["name"]: p["productid"] for p in
                     dv.get("products?$select=productid,name&$top=500")["value"]},
        "bpfs": {
            "credit": ({"id": cr_id, "name": "Corporate Credit Lifecycle",
                        "unique": f"{P}_corporatecreditlifecycle"}, cr_stages),
            "trade": ({"id": tr_id, "name": "Trade Finance Deal",
                       "unique": f"{P}_tradefinancedeal"}, tr_stages),
            "mandate": ({"id": mn_id, "name": "Cash Management Mandate",
                         "unique": f"{P}_cashmanagementmandate"}, mn_stages),
            "sme": ({"id": sm_id, "name": "SME Deal Journey",
                     "unique": f"{P}_smedealjourney"}, sm_stages),
        },
    }

    print("Document packages")
    ctx["pkgs"] = {p[f"{P}_name"]: p[f"{P}_documentpackageid"] for p in dv.get(
        f"{P}_documentpackages?$select={P}_documentpackageid,{P}_name&$top=500")["value"]}
    for n, items in PACKAGES.items():
        ctx["pkgs"][n] = upsert_package(n, items, ctx["teams"])

    print("Templates")
    for t in TEMPLATES:
        tid = upsert_template(t, ctx)
        apply_graph(tid, t["graph"], ORDER[t["bpf"]])
    print("done")


if __name__ == "__main__":
    main()
