"""Step 24: the sales foundation - product catalogue, teams, price list, customers.

The product tree is the backbone of everything else here: a sales process is targeted by what
is being sold, so the families in this tree are the vocabulary a business user writes rules in.
It is deliberately three levels deep, because two is not enough to show why "is at or under"
beats naming every SKU.

Idempotent. Re-running matches on name and leaves existing records alone.
"""
import dv

P = dv.PREFIX

# (name, [children]) - nesting is the point, not decoration
TREE = [
    ("Corporate Banking", [
        ("Business Lending", [
            ("Working Capital Facility", []),
            ("Corporate Term Loan", []),
            ("Commercial Mortgage", []),
            ("Invoice Discounting", []),
            ("Project Finance Facility", []),
        ]),
        ("Trade Finance", [
            ("Letter of Credit", []),
            ("Bank Guarantee", []),
            ("Documentary Collection", []),
            ("Supply Chain Finance", []),
        ]),
        ("Cash Management", [
            ("Corporate Current Account", []),
            ("Payroll and WPS Services", []),
            ("Host to Host Payments", []),
            ("Liquidity Management", []),
        ]),
        ("Treasury and Markets", [
            ("FX Forward", []),
            ("Interest Rate Swap", []),
        ]),
        ("Corporate Cards", [
            ("Corporate Credit Card Programme", []),
            ("Purchasing Card", []),
        ]),
    ]),
    ("Business Banking", [
        ("SME Lending", [
            ("SME Business Loan", []),
            ("SME Overdraft", []),
            ("Equipment Finance", []),
        ]),
        ("Merchant Services", [
            ("POS Merchant Acquiring", []),
            ("Online Payment Gateway", []),
        ]),
    ]),
]

TEAMS = [
    ("Corporate Coverage", "Relationship managers who own the customer and the deal."),
    ("SME Sales", "Business banking sales for smaller corporates."),
    ("Credit Risk", "Underwrites and rates the borrower. Owns the credit decision."),
    ("Credit Administration", "Builds facility documents and books limits after approval."),
    ("Trade Operations", "Issues and services letters of credit and guarantees."),
    ("Treasury Sales", "Prices and books FX and rates products."),
    ("Legal and Documentation", "Drafts and reviews the facility agreement and security."),
    ("Compliance and Financial Crime", "KYC, sanctions and financial crime clearance."),
    ("Deal Desk", "Pricing approval, exception handling and deal governance."),
]

ACCOUNTS = [
    # (name, segment, revenue AED, industry, city)
    ("Al Futtaim Logistics LLC", 1, 1_850_000_000, "Transportation", "Dubai"),
    ("Emirates Steel Industries PJSC", 1, 4_200_000_000, "Manufacturing", "Abu Dhabi"),
    ("Gulf Petrochem Trading FZE", 1, 2_600_000_000, "Distribution", "Sharjah"),
    ("Arabian Ranches Developments LLC", 2, 640_000_000, "Real Estate", "Dubai"),
    ("Nakheel Hospitality Group", 2, 410_000_000, "Accounting", "Dubai"),
    ("Desert Rose Foodstuff Trading LLC", 3, 78_000_000, "Retail", "Dubai"),
    ("Mira Medical Supplies LLC", 3, 44_000_000, "Wholesale", "Abu Dhabi"),
    ("Falcon Technical Services LLC", 3, 31_000_000, "Service", "Sharjah"),
    ("Union Cooperative Society", 5, 980_000_000, "Retail", "Dubai"),
    ("First Gulf Capital Partners", 4, 320_000_000, "Financial", "Dubai"),
]

CONTACTS = [
    ("Rashid", "Al Mansoori", "Group Treasurer", "Al Futtaim Logistics LLC"),
    ("Priya", "Nair", "Chief Financial Officer", "Emirates Steel Industries PJSC"),
    ("Omar", "Haddad", "Finance Director", "Gulf Petrochem Trading FZE"),
    ("Sarah", "Whitfield", "Head of Finance", "Arabian Ranches Developments LLC"),
    ("Vikram", "Shetty", "Financial Controller", "Nakheel Hospitality Group"),
    ("Layla", "Hassan", "Managing Director", "Desert Rose Foodstuff Trading LLC"),
    ("Ahmed", "Siddiqui", "Owner", "Mira Medical Supplies LLC"),
    ("Grace", "Mutiso", "General Manager", "Falcon Technical Services LLC"),
    ("Khalid", "Al Suwaidi", "Chief Executive", "Union Cooperative Society"),
    ("Daniel", "Okafor", "Head of Corporate Finance", "First Gulf Capital Partners"),
]


def uom():
    """Products need a unit group and a default unit before they can be created."""
    ug = dv.find_one("uomschedules", "name eq 'Default Unit'", "uomscheduleid")
    if not ug:
        ug = dv.find_one("uomschedules", None, "uomscheduleid,name")
        if not ug:
            ug = {"uomscheduleid": dv.new_id(dv.post("uomschedules", {
                "name": "Default Unit", "baseuomname": "Primary Unit"}))}
    u = dv.find_one("uoms", f"_uomscheduleid_value eq {ug['uomscheduleid']}", "uomid")
    return ug["uomscheduleid"], u["uomid"]


def ensure_product(name, parent_id, ug, u, is_family):
    ex = dv.find_one("products", f"name eq '{name.replace(chr(39), chr(39) * 2)}'",
                     "productid,name,statecode")
    if ex:
        return ex["productid"]
    body = {
        "name": name,
        "productnumber": "SPC-" + name.replace(" ", "-").upper()[:40],
        # 3 = product family, 1 = product. Only a family can parent anything, which is what
        # makes the tree real rather than a naming convention.
        # 1 Product, 2 Product Family, 3 Product BUNDLE. Getting this wrong gives
        # "You can only select a product family as the parent", which reads like a
        # problem with the parent when it is really the parent being a bundle.
        "productstructure": 2 if is_family else 1,
    }
    # Dataverse demands a unit schedule even on a family, which is odd but not negotiable.
    body["defaultuomscheduleid@odata.bind"] = f"/uomschedules({ug})"
    body["defaultuomid@odata.bind"] = f"/uoms({u})"
    if not is_family:
        body["price"] = 0
    if parent_id:
        body["parentproductid@odata.bind"] = f"/products({parent_id})"
    pid = dv.new_id(dv.post("products", body, solution=False))
    print("  +", ("family " if is_family else "product ") + name)
    return pid


def publish(pid, name):
    """A draft family cannot be given children, so each one is activated on the way down."""
    # PublishProduct is not exposed on the Web API at all - neither bound nor unbound - so a
    # product is activated by writing its state directly. A family stays in Draft otherwise,
    # and a Draft family cannot be given children.
    try:
        dv.patch(f"products({pid})", {"statecode": 0, "statuscode": 1})
    except RuntimeError as e:
        print("  ! activate", name, str(e)[-160:])


def walk(nodes, parent, ug, u):
    for name, kids in nodes:
        pid = ensure_product(name, parent, ug, u, bool(kids))
        publish(pid, name)
        if kids:
            walk(kids, pid, ug, u)


def publish_products():
    """A draft product is invisible to a deal, so every leaf has to be activated."""
    rows = dv.get("products?$select=productid,name,statecode,productstructure")["value"]
    for p in rows:
        if p["statecode"] == 0 or not p["name"]:
            continue
        publish(p["productid"], p["name"])


def ensure_team(name, desc, buid):
    ex = dv.find_one("teams", f"name eq '{name}'", "teamid")
    if ex:
        return ex["teamid"]
    tid = dv.new_id(dv.post("teams", {
        "name": name, "description": desc, "teamtype": 0,
        "businessunitid@odata.bind": f"/businessunits({buid})"}))
    print("  + team", name)
    return tid


def ensure_accounts():
    ids = {}
    for name, seg, rev, industry, city in ACCOUNTS:
        esc = name.replace("'", "''")
        ex = dv.find_one("accounts", f"name eq '{esc}'", "accountid")
        if ex:
            ids[name] = ex["accountid"]
            dv.patch(f"accounts({ex['accountid']})", {f"{P}_customersegment": seg})
            continue
        ids[name] = dv.new_id(dv.post("accounts", {
            "name": name, "revenue": rev, "address1_city": city,
            "customertypecode": 3, f"{P}_customersegment": seg,
        }))
        print("  + account", name)
    return ids


def ensure_contacts(accounts):
    for first, last, title, acct in CONTACTS:
        full = f"{first} {last}"
        ex = dv.find_one("contacts", f"fullname eq '{full}'", "contactid")
        aid = accounts.get(acct)
        if ex:
            continue
        dv.post("contacts", {
            "firstname": first, "lastname": last, "jobtitle": title,
            "parentcustomerid_account@odata.bind": f"/accounts({aid})",
            f"{P}_customersegment": dict((a[0], a[1]) for a in
                                         [(x[0], x[1]) for x in ACCOUNTS]).get(acct, 3),
        })
        print("  + contact", full)


def main():
    ug, u = uom()
    print("Product catalogue")
    walk(TREE, None, ug, u)
    print("Publishing products")
    publish_products()

    print("Teams")
    bu = dv.get("businessunits?$select=businessunitid&$filter=parentbusinessunitid eq null")["value"][0]
    for name, desc in TEAMS:
        ensure_team(name, desc, bu["businessunitid"])

    print("Accounts")
    accounts = ensure_accounts()
    print("Contacts")
    ensure_contacts(accounts)
    print("done")


if __name__ == "__main__":
    main()
