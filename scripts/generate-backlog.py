#!/usr/bin/env python3
"""
Regenerates docs/BACKLOG.md from the live GitHub issues.

    ./scripts/generate-backlog.sh

Run it whenever issues are added, retitled, closed or re-scoped. Hand-editing
BACKLOG.md works until someone forgets, and then the map quietly stops matching
the territory — which is worse than having no map.

Modules come from the `module:` label on each issue, so adding a module means
adding a label and a row in ORDER below.
"""
import json, re, sys, os

REPO = "https://github.com/innovateavitech/trips-agent"
issues = json.load(open(sys.argv[1] if len(sys.argv) > 1 else '/tmp/issues.json'))

# Module display order — infrastructure first, then the money path, then surfaces.
ORDER = [
    ("platform-foundation", "Platform Foundation", "Scaffold, CI, database, messaging, assets, audit. Everything else sits on this."),
    ("multi-tenancy", "Multi-Tenancy", "Agencies, the sub-agent hierarchy, and the isolation that keeps one agency's data away from another's."),
    ("identity-access", "Identity & Access", "Authentication, tokens, permissions, password flows."),
    ("agency-onboarding", "Agency Onboarding", "Registration through KYB verification to a live, transacting account."),
    ("wallet-ledger", "Wallet & Ledger", "The double-entry ledger and the agent wallet it backs."),
    ("payments", "Payments", "Paystack, webhooks, refunds, payouts, reconciliation."),
    ("pricing-markup", "Pricing & Markup", "How a net rate becomes a sell price, and how that price is frozen."),
    ("flight-bus-booking", "Flight & Bus Booking", "The Trips Africa integration: search, price confirmation, ticketing, reconciliation."),
    ("checkout-orders", "Checkout & Orders", "Cart, the checkout saga, fulfilment status, and the resolution queue."),
    ("documents", "Documents", "Branded invoices and vouchers, with gapless numbering."),
    ("notifications", "Notifications", "Email and SMS delivery, templates, scheduled reminders."),
    ("agent-console", "Agent Console", "The shell every agent-facing screen lives inside."),
    ("product-catalog", "Product Catalog", "Agent-authored tours, packages and visas."),
    ("group-tours", "Group Tours", "Fixed-date departures, deposits, installments, waitlists."),
    ("storefront", "Storefront", "The site builder, custom domains, and the public branded site."),
    ("crm", "CRM", "Customers, leads, quotes, pipeline, follow-ups."),
    ("sub-agent-network", "Sub-Agent Network", "Agencies beneath agencies: scopes, allowances, consolidated reporting."),
    ("subscriptions-billing", "Subscriptions & Billing", "Tiers, entitlements, recurring billing, dunning."),
    ("admin-console", "Admin Console", "Trips' own back-office."),
    ("analytics-reporting", "Analytics & Reporting", "Read models, rollups, reports, exports."),
    ("loyalty-reviews", "Loyalty & Reviews", "Retention features."),
    ("security", "Security", "Hardening, rate limiting, PII handling, load testing."),
]

by_slug = {}
for i in issues:
    names = [l['name'] for l in i['labels']]
    slug = next((n.split(':',1)[1] for n in names if n.startswith('module:')), None)
    i['_slug'] = slug
    i['_labels'] = names
    m = re.search(r'\*\*Depends on:\*\*\s*(.+)', i.get('body') or '')
    i['_deps'] = re.findall(r'#(\d+)', m.group(1)) if m else []
    by_slug.setdefault(slug, []).append(i)

state = {i['number']: i['state'] for i in issues}

def badge(i):
    b = []
    if 'epic' in i['_labels']: b.append('🧩')
    if 'good-first-issue' in i['_labels']: b.append('🟢')
    if 'blocked' in i['_labels']: b.append('🚫')
    if i['number'] in (11,12,22,29): b.append('⚠️')
    if i['number'] in (36,37,42,43): b.append('🔴')
    return ' '.join(b)

def short(i):
    t = i['title']
    return t.split(': ', 1)[1] if ': ' in t else t

out = []
w = out.append

w("# Backlog")
w("")
w("Every issue needed to build the platform, grouped by **module**.")
w(f"Live board: **[{REPO.split('//')[1]}/issues]({REPO}/issues)**")
w("")
w("This file is the map. GitHub is the source of truth for status — if the two disagree, believe GitHub.")
w("")
w("Issues are titled `Module: What it is`, and carry a `module:` label so you can filter to one area:")
w("")
w("```bash")
w('gh issue list --label "module:wallet-ledger"')
w('gh issue list --label "good-first-issue"')
w('gh issue list --milestone "M1 — Tenanted spine + live ticketing"')
w("```")
w("")
w("---")
w("")
w("## How to pick something up")
w("")
w("1. Find an unassigned issue whose dependencies are **closed**")
w("2. Assign it to yourself, so nobody duplicates your work")
w("3. Branch, build, PR — see [WORKING_WITH_CLAUDE.md](WORKING_WITH_CLAUDE.md) if you are using")
w("   Claude Code, or [CONTRIBUTING.md §4](../CONTRIBUTING.md#4-the-everyday-workflow) if not")
w("")
w("New here? Start with a **[`good-first-issue`](%s/labels/good-first-issue)**." % REPO)
w("These are deliberately self-contained and never touch payments, tenancy or the supplier")
w("integration — the three places where a well-meaning mistake costs money or leaks data.")
w("")
w("**Do not start an issue whose dependencies are still open.** You will build against something")
w("that does not exist yet and have to redo it.")
w("")
w("### Legend")
w("")
w("| | |")
w("|---|---|")
w("| 🟢 | `good-first-issue` — safe and self-contained |")
w("| 🧩 | Epic — break it into smaller issues first, and post the breakdown as a comment |")
w("| ⚠️ | Touches tenancy or financial invariants. Get it reviewed carefully |")
w("| 🔴 | On the money path. **Read [ADR-0003](adr/0003-never-retry-ticket-issuance.md) first** |")
w("| 🚫 | Blocked — waiting on a client decision |")
w("")
w("---")
w("")
w("## Modules at a glance")
w("")
w("| Module | Issues | Milestones |")
w("|---|---|---|")
for slug, name, _ in ORDER:
    items = by_slug.get(slug, [])
    if not items: continue
    ms = sorted({(i['milestone'] or {}).get('title','')[:2] for i in items if i['milestone']})
    w(f"| [{name}](#{name.lower().replace(' ','-').replace('&','').replace('--','-')}) | {len(items)} | {', '.join(ms)} |")
w("")
w("---")
w("")

for slug, name, blurb in ORDER:
    items = sorted(by_slug.get(slug, []), key=lambda x: x['number'])
    if not items: continue
    w(f"## {name}")
    w("")
    w(blurb)
    w("")
    w("| # | Issue | Depends on | Status |")
    w("|---|---|---|---|")
    for i in items:
        deps = ', '.join(f"#{d}" for d in i['_deps']) or '—'
        st = '✅ done' if i['state'] == 'CLOSED' else 'open'
        b = badge(i)
        title = short(i) + (f" {b}" if b else "")
        w(f"| [#{i['number']}]({REPO}/issues/{i['number']}) | {title} | {deps} | {st} |")
    w("")

w("---")
w("")
w("## Build order")
w("")
w("Modules are for finding things. **Dependencies decide what you can actually start.**")
w("")
w("Milestone 1 in rough order — each group needs the one above it:")
w("")
w("1. **Platform Foundation** — #2 #3 #4 #5 #6 #7 #8 #9")
w("2. **Multi-Tenancy** — #10 #11 #12, and **Identity & Access** #13 #14 #15 #16 #17")
w("3. **Agency Onboarding** — #18 #19 #20, plus **Platform Foundation** #21")
w("4. **Wallet & Ledger** #22 #23 #26 #27 and **Payments** #24 #25")
w("5. **Pricing & Markup** #28 #29")
w("6. **Platform Foundation** #30 #31 — the async backbone")
w("7. **Flight & Bus Booking** #32 → #33 #34 → #35 → #36 → #37 #38, plus #39 #40")
w("8. **Checkout & Orders** #41 → #42 → #43 #44")
w("9. **Notifications** #45, **Documents** #46 #47")
w("10. **Agent Console** #48, then the screens across modules: #49 #50 #51 #52 #53 #54 #55")
w("")
w("**M1 is done when:** a real ticket is issued against Trips Africa staging; a forced failure")
w("proves the reversal works; a tampered hash blocks issuance; concurrent submits produce exactly")
w("one ticket; and `wallet.balance = SUM(ledger entries)` holds after a randomised soak test.")
w("")
w("---")
w("")
w("## Blocked on client answers")
w("")
w("27 open questions are listed in [§7 of the plan](ARCHITECTURE_AND_DELIVERY_PLAN.md). Five must")
w("be answered before Milestone 2 starts, because they change money flows rather than screens:")
w("")
w("1. Do agents hold their own Trips Africa credentials, or does the platform transact as one merchant?")
w("2. Who is the merchant of record for traveller payments?")
w("3. **Who fronts the money between checkout and ticketing?** The card settles tomorrow; the")
w("   ticket must issue now.")
w("4. Is the platform fee added to the traveller's price, or taken from the agent's margin?")
w("5. How do flight cancellations work? The supplier documents bus cancellation but not flight.")
w("")
w("If an issue you are working on runs into one of these, **stop and flag it** rather than guessing.")
w("Guessing on a money question costs more to unwind than the delay costs to wait.")

open('docs/BACKLOG.md','w').write('\n'.join(out) + '\n')
print("wrote docs/BACKLOG.md")
print("modules rendered:", sum(1 for s,_,_ in ORDER if by_slug.get(s)))
print("issues rendered:", sum(len(by_slug.get(s,[])) for s,_,_ in ORDER))
