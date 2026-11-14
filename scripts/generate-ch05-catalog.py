"""Generates samples/catalog/policy-catalog.json: about 200 one-paragraph summaries of Deductible
Dynamics' policies, procedures, and coverage rules (chapter 5). Everything is invented.

Run from the repository root: python3 scripts/generate-ch05-catalog.py
"""
import json
from pathlib import Path

OUT = Path("samples/catalog/policy-catalog.json")

# (department, number prefix, [(title, summary), ...]); numbers are assigned in order.
HAND_WRITTEN = [
    ("HR", "HR", 101, [
        ("Code of Conduct", "Sets the standards of honesty, respect, and professional behavior expected of every employee and contractor, and explains how to report a concern without retaliation."),
        ("Equal Employment Opportunity", "Prohibits discrimination in hiring, pay, promotion, and termination on the basis of any protected characteristic, and names HR as the place to raise a complaint."),
        ("Anti-Harassment", "Defines harassment, including sexual harassment, gives examples, and explains how complaints are investigated and kept confidential."),
        ("Paid Time Off", "Explains how vacation and personal days accrue each pay period, how much can carry over into the next year, and how to request time away from work."),
        ("Holidays", "Lists the eleven company holidays and explains holiday pay for people who are scheduled to work on one, such as the weekend clinical line."),
        ("Remote and Hybrid Work", "Explains who may work from home, how many days a week, the equipment the company provides, and the rules for working from another state."),
        ("Family and Medical Leave (FMLA)", "Describes up to twelve weeks of job-protected unpaid leave for a serious health condition, a new child, or caring for a family member, and how to apply."),
        ("Parental Leave", "Provides eight weeks of fully paid leave to birth, adoptive, and foster parents in the first year after the child arrives, on top of any disability benefits."),
        ("Bereavement Leave", "Gives up to five paid days off after the death of a close family member and three days for other relatives, with no documentation required."),
        ("Jury Duty and Witness Leave", "Pays regular wages for up to ten days of jury service and explains what to do when a court summons arrives."),
        ("Military Leave", "Protects the jobs of employees called to military service under USERRA and continues health coverage during the first thirty days."),
        ("Short-Term Disability", "Replaces sixty percent of pay for up to twenty-six weeks when an illness or injury keeps an employee from working, after a seven-day waiting period."),
        ("Workers' Compensation", "Explains what to do after an injury at work: report it the same day, get care, and file the claim with HR within twenty-four hours."),
        ("Overtime and Timekeeping", "Explains who is eligible for overtime, how to record hours accurately, and why working off the clock is never allowed."),
        ("Payroll Schedule and Pay Stubs", "Employees are paid every other Friday by direct deposit; explains how to view pay stubs and correct a payroll error."),
        ("Performance Reviews", "Describes the annual review cycle, the mid-year check-in, the five-point rating scale, and how ratings feed into merit increases."),
        ("Promotions and Internal Transfers", "Explains how to apply for an internal job, when to tell your manager, and the minimum time in role before transferring."),
        ("Hiring and Background Checks", "Describes the background, license, and exclusion-list checks completed before an offer is final, including for contractors with system access."),
        ("Employee Referral Bonus", "Pays a referral bonus when someone you refer is hired and stays ninety days; lists the roles that qualify for a higher bonus."),
        ("Tuition Assistance", "Reimburses up to a yearly limit for job-related courses and degrees, with a pay-back requirement if the employee leaves within a year."),
        ("Professional License Renewal", "The company pays renewal fees and continuing education costs for nursing, pharmacy, and coding credentials required for the job."),
        ("Dress Code", "Business casual in offices, scrubs or business casual for clinical staff on site, and badges visible at all times."),
        ("Drug-Free Workplace", "Prohibits working under the influence and explains when testing happens, such as after a workplace accident."),
        ("Workplace Violence Prevention", "Prohibits threats and weapons on company property and explains how to report a threat or an unsafe situation."),
        ("Employee Assistance Program", "Offers free, confidential counseling sessions and referrals for stress, family, financial, and legal concerns to employees and their households."),
        ("Health and Dental Benefits for Employees", "Summarizes the medical, dental, and vision plans available to employees, open enrollment, and when coverage starts for new hires."),
        ("Retirement Plan (401(k))", "Explains the company match, vesting schedule, automatic enrollment, and how to change contributions."),
        ("Resignation and Exit Process", "Asks for two weeks' notice, explains the final paycheck and PTO payout, and lists the equipment and badges to return on the last day."),
        ("Progressive Discipline", "Describes the steps from verbal warning to termination and the conduct that can skip steps, such as a privacy violation."),
        ("Personnel Files and Employee Records", "Explains what HR keeps in a personnel file, how long it is kept, and how employees can ask to see their own."),
        ("Flexible Work Schedules", "Allows compressed weeks and shifted hours with manager approval, as long as core hours from ten to three are covered."),
        ("Lactation Accommodation", "Provides private lactation rooms and paid break time for nursing parents, at every office."),
        ("Reasonable Accommodation", "Explains how to request a workplace accommodation for a disability or religious practice and how requests are kept confidential."),
    ]),
    ("Clinical", "CLN", 201, [
        ("Sepsis Screening and Escalation", "Gives the care management nurse line the screening questions for possible sepsis and the steps to escalate to emergency care immediately."),
        ("Nurse Advice Line Protocols", "Lists the symptom-based protocols nurses follow on member calls, when to direct a member to urgent care or the emergency room, and how to document the call."),
        ("Medication Reconciliation", "Describes how care managers compare a member's medication list across prescribers after a hospital discharge and flag interactions to the pharmacist."),
        ("Care Management Enrollment", "Explains which members are offered care management, such as those with heart failure or diabetes, and how outreach and consent work."),
        ("Transitions of Care After Discharge", "Requires a follow-up call within two business days of a hospital discharge to check medications, appointments, and home support."),
        ("Clinical Documentation Standards", "Sets the rules for clinical notes: timely, factual, signed, and never copied forward without review."),
        ("Medical Necessity Criteria", "Explains which nationally recognized criteria clinical reviewers use to decide whether a requested service is medically necessary."),
        ("Peer-to-Peer Review", "Lets an ordering physician discuss a denial with a plan medical director before or after a decision, and sets the scheduling time frames."),
        ("Utilization Review Time Frames", "Sets decision deadlines for standard and expedited prior authorization requests and for concurrent review of inpatient stays."),
        ("Behavioral Health Crisis Response", "Gives nurses the steps to follow when a member expresses thoughts of self-harm, including a warm transfer to the crisis line."),
        ("Maternity Care Management", "Describes outreach to pregnant members, high-risk pregnancy screening, and postpartum follow-up visits."),
        ("Chronic Kidney Disease Program", "Explains how members with advanced kidney disease are identified and connected to nephrology and dialysis planning."),
        ("Diabetes Management Program", "Covers glucose monitoring supplies, education sessions, and the A1c targets care managers track with members."),
        ("Heart Failure Program", "Describes daily weight monitoring, symptom check calls, and when to escalate a member with worsening heart failure."),
        ("Infection Control in Clinics", "Sets hand hygiene, cleaning, and personal protective equipment rules for the on-site employee clinics."),
        ("Clinical Staff Licensure Verification", "Requires primary-source verification of every nurse's and pharmacist's license at hire and before each expiration."),
        ("Interpreter Services", "Explains how to get a qualified medical interpreter for a member call in any language, at no cost to the member."),
        ("Advance Directives", "Explains how care managers talk with members about advance directives and where members can get the forms."),
        ("Home Health Coordination", "Describes how care managers arrange skilled nursing and therapy visits at home after a hospital stay."),
        ("Durable Medical Equipment Review", "Lists the clinical documentation needed to approve wheelchairs, hospital beds, oxygen, and CPAP machines."),
        ("Pharmacy and Therapeutics Committee", "Describes how the committee reviews drugs for the formulary, how often it meets, and how conflicts of interest are handled."),
        ("Opioid Prescribing Safety", "Explains the limits on first-fill opioid prescriptions, the morphine-equivalent dose threshold that triggers review, and naloxone outreach."),
        ("Specialty Pharmacy Management", "Covers high-cost specialty drugs, the network specialty pharmacy, and the clinical review required before the first fill."),
        ("Clinical Quality Measures (HEDIS)", "Describes how the plan measures preventive care and chronic condition quality, and how care gaps are sent to providers."),
        ("Member Health Risk Assessment", "Explains the annual health questionnaire, who receives it, and how answers route members into programs."),
        ("Telehealth Clinical Standards", "Sets the rules for video and phone visits through the plan's virtual care vendor, including when an in-person visit is required."),
        ("Emergency Room Follow-Up", "Requires outreach to members after an emergency room visit for a condition that could have been treated in primary care."),
        ("Preventive Care Reminders", "Describes reminder letters and calls for screenings such as mammograms, colonoscopies, and childhood immunizations."),
        ("Palliative Care Program", "Offers symptom management and care planning support to members with serious illness, alongside curative treatment."),
        ("Case Rounds and Clinical Escalation", "Describes the weekly case rounds where complex members are discussed with a medical director and pharmacist."),
        ("Clinical Incident Reporting", "Explains how to report an event that harmed or could have harmed a member, and how incidents are reviewed without blame."),
        ("Nurse Reviewer Training", "Lists the onboarding and annual training required before a nurse makes prior authorization recommendations."),
    ]),
    ("Claims", "CLM", 301, [
        ("Prior Authorization Overview", "Explains which services need approval before they are provided, who submits the request, and what happens when a request arrives without one."),
        ("Prior Authorization Intake", "Describes how faxed, portal, and phone requests are logged, checked for missing information, and routed to a nurse reviewer."),
        ("Urgent and Expedited Requests", "Explains when a request qualifies as expedited and the seventy-two-hour decision deadline that applies to it."),
        ("Retroactive Authorization", "Describes the narrow cases, such as emergencies and newborns, in which approval can be requested after the service was provided."),
        ("Claims Submission Requirements", "Lists the information a clean claim must include and the timely filing limit for in-network providers."),
        ("Claim Denials and Remark Codes", "Explains the most common denial reasons, the remark codes on the remittance, and what a provider can fix and resubmit."),
        ("Member Appeals", "Explains how a member appeals a denial, the sixty-day filing window, the review by a clinician not involved in the original decision, and the time frames."),
        ("Provider Disputes and Reconsiderations", "Describes how a provider disputes a payment amount and the difference between a reconsideration and an appeal."),
        ("External Review", "Explains a member's right to an independent external review after the internal appeal is exhausted."),
        ("Coordination of Benefits", "Explains how the plan decides which coverage pays first when a member has more than one plan, including Medicare."),
        ("Subrogation", "Describes how the plan recovers claim costs when another party, such as an auto insurer, is responsible for an injury."),
        ("Out-of-Network Claims", "Explains how out-of-network services are paid, balance billing protections, and when an exception makes them in-network."),
        ("No Surprises Act Compliance", "Describes the protections against surprise bills for emergency care and for out-of-network clinicians at in-network facilities."),
        ("Claims Payment Accuracy Audits", "Describes the monthly sample of paid claims audited for coding and payment errors, and how errors are corrected."),
        ("Duplicate Claim Detection", "Explains how duplicate and overlapping claims are flagged before payment."),
        ("Fraud, Waste, and Abuse Referrals", "Explains the warning signs of billing fraud and how claims staff refer a suspicious pattern to the Special Investigations Unit."),
        ("Overpayment Recovery", "Describes how the plan notifies a provider of an overpayment and the timeline to repay or dispute it."),
        ("Continuity of Care for New Members", "Allows new members in active treatment to keep seeing an out-of-network provider for up to ninety days."),
        ("Formulary Exception Requests", "Explains how a prescriber requests coverage of a drug that is not on the formulary, and the clinical information required."),
        ("Step Therapy", "Explains which drugs require trying a preferred drug first and how to request an exception when it is not appropriate."),
        ("Member Cost Sharing", "Explains deductibles, copays, coinsurance, and the out-of-pocket maximum, with worked examples."),
        ("Explanation of Benefits Statements", "Describes what appears on the EOB a member receives after a claim and how to read it."),
    ]),
    ("Compliance", "CMP", 401, [
        ("HIPAA Privacy Rule Overview", "Summarizes what protected health information is, the minimum necessary standard, and when PHI may be used or disclosed without authorization."),
        ("Minimum Necessary Standard", "Requires staff to use, request, and disclose only the PHI needed for a task, and gives examples for claims and care management."),
        ("Member Rights to Access Records", "Explains how a member requests a copy of their records, the thirty-day deadline, and the allowed fees."),
        ("Privacy Incident Reporting", "Requires every suspected privacy incident, such as a misdirected fax or email, to be reported to the Privacy Office within one business day."),
        ("Breach Notification", "Describes the risk assessment that decides whether an incident is a reportable breach and the notification deadlines for members and regulators."),
        ("Business Associate Agreements", "Requires a signed BAA before any vendor creates, receives, stores, or transmits PHI for the plan, and names who may sign one."),
        ("Security Awareness Training", "Requires annual privacy and security training for every workforce member, with completion tracked by Compliance."),
        ("Sanctions for Privacy Violations", "Describes the disciplinary consequences of privacy violations, from retraining to termination, based on intent and harm."),
        ("Records Retention Schedule", "Lists how long each type of record is kept, such as claims for ten years, and how records are destroyed at the end."),
        ("Secure Disposal of PHI", "Requires shredding of paper PHI in locked bins and certified destruction of drives and devices."),
        ("Fax Handling Procedure", "Requires confirming fax numbers, using the cover sheet, sending only necessary pages, and reporting misdirected faxes."),
        ("Email and Messaging with PHI", "Requires encryption for external email containing PHI and prohibits PHI in text messages and chat tools that are not approved."),
        ("De-identification of Data", "Explains the Safe Harbor and Expert Determination methods and who must approve a de-identified data set before it is shared."),
        ("Research Use of Member Data", "Requires Privacy Board approval before member data is used for research, and explains limited data sets and data use agreements."),
        ("Marketing and Member Communications", "Explains when marketing to members requires their authorization and which communications are exempt."),
        ("Conflicts of Interest", "Requires annual disclosure of outside interests and gifts that could influence business decisions."),
        ("Gifts and Entertainment", "Limits gifts from vendors and providers to a small value and prohibits cash or gift cards."),
        ("Compliance Hotline", "Describes the anonymous hotline for reporting fraud, privacy, and ethics concerns, and the no-retaliation guarantee."),
        ("Exclusion List Screening", "Requires monthly screening of employees, contractors, and providers against federal and state exclusion lists."),
        ("Regulatory Audits and Inquiries", "Explains how to respond to a regulator's request: route it to Compliance the same day and do not send documents directly."),
        ("Accounting of Disclosures", "Explains a member's right to a list of certain disclosures of their PHI and how Compliance tracks them."),
        ("Mental Health Parity Compliance", "Describes how the plan checks that limits on mental health and substance use benefits are no stricter than medical ones."),
        ("Language Access and Nondiscrimination", "Describes the notice of nondiscrimination, taglines in other languages, and free interpreter and translation services."),
        ("Policy Management", "Explains how policies are written, approved, numbered, reviewed every year, and retired."),
    ]),
    ("IT", "IT", 501, [
        ("Acceptable Use of Systems", "Sets the rules for using company laptops, email, and internet, including what personal use is allowed and what is monitored."),
        ("Password and Passphrase Standard", "Requires passphrases of at least fourteen characters and multi-factor sign-in, and explains how to reset a forgotten password."),
        ("Multi-Factor Authentication", "Requires multi-factor sign-in for every account, and explains how to set up the authenticator app and what to do with a lost phone."),
        ("Remote Access and VPN", "Explains how to connect to internal systems from home, why public Wi-Fi requires the VPN, and which systems are reachable remotely."),
        ("Bring Your Own Device", "Allows company email on personal phones only with the device management app installed and a screen lock enabled."),
        ("Laptop Encryption and Lost Devices", "Requires full-disk encryption on every laptop and immediate reporting of a lost or stolen device to the service desk."),
        ("Access Requests and Approvals", "Explains how to request access to an application, who approves it, and how access is removed when a role changes."),
        ("Quarterly Access Reviews", "Requires managers to review and confirm their team's application access every quarter."),
        ("Phishing Reporting", "Explains how to recognize a phishing email and report it with the button in Outlook, and what happens next."),
        ("Software Installation", "Allows only approved software from the company portal and explains how to request something new."),
        ("Generative AI Use", "Allows only approved AI tools, prohibits entering PHI or confidential data into consumer chatbots, and names PriorAuthBot as the approved internal assistant."),
        ("Data Classification", "Defines the four classification levels, Public, Internal, Confidential, and Restricted, with examples of each."),
        ("Backup and Recovery", "Describes how often systems are backed up, how long backups are kept, and how restores are tested."),
        ("Change Management", "Requires a reviewed and approved change ticket for production changes, with an emergency process for outages."),
        ("Incident Response", "Describes how security incidents are detected, triaged, contained, and reported, and who is on the response team."),
        ("Vendor Security Reviews", "Requires a security review before buying any software or service that will store company or member data."),
        ("Logging and Monitoring", "Describes which systems send logs to the security monitoring platform and how long logs are kept."),
        ("Vulnerability Management", "Sets patching deadlines by severity, such as fourteen days for critical vulnerabilities."),
        ("Service Desk Support", "Explains how to open a ticket, the support hours, and response targets by priority."),
        ("Printing and Scanning", "Requires secure release printing for documents with PHI and explains scan-to-email limits."),
        ("Collaboration Tools", "Lists the approved chat, meeting, and file-sharing tools and the rules for sharing files with outside parties."),
        ("Shared Mailboxes", "Explains how to request a shared mailbox and who owns access to it."),
        ("Developer Secure Coding Standard", "Requires code review, dependency scanning, and secrets in a vault rather than in source code."),
        ("Cloud Usage", "Requires all cloud resources to be created in company subscriptions with tagging, and prohibits personal cloud accounts for work data."),
    ]),
    ("AllStaff", "GEN", 601, [
        ("Business Travel", "Explains how to book travel through the company travel tool, the hotel and meal limits, and how to submit expenses within thirty days."),
        ("Expense Reimbursement", "Lists what can be expensed, the receipts required, and how quickly reimbursements are paid."),
        ("Corporate Credit Cards", "Explains who receives a corporate card, what it can be used for, and the monthly reconciliation deadline."),
        ("Office Access and Badges", "Requires badges to be worn and never shared, and explains how visitors are signed in and escorted."),
        ("Visitors in the Office", "Visitors sign in at reception, wear a visitor badge, and stay with their host, and may not enter areas where PHI is handled."),
        ("Emergency Evacuation", "Describes fire alarms, evacuation routes, and assembly points for each office."),
        ("Inclement Weather", "Explains how office closures are announced and when employees should work from home."),
        ("Social Media", "Explains what employees may and may not post about the company, members, and coworkers."),
        ("Media Inquiries", "Requires that all questions from reporters be sent to Communications without comment."),
        ("Charitable Giving and Volunteering", "Provides one paid volunteer day per year and a donation match up to a yearly limit."),
        ("Company Vehicles", "Lists who may drive a company vehicle, the insurance requirements, and how to report an accident."),
        ("Mail and Shipping", "Explains how to send overnight packages and certified mail, and how incoming mail with PHI is handled."),
        ("Purchasing and Approval Limits", "Lists who may approve purchases at each amount and when a purchase order is required."),
        ("Records Requests from Employees", "Explains how employees request employment verification letters and copies of tax forms."),
        ("Recycling and Sustainability", "Describes office recycling and the program to reduce printing."),
        ("Parking and Commuting", "Explains parking permits at each office and the pre-tax transit benefit."),
        ("Food in the Office", "Explains kitchen rules and the policy on catered meetings."),
        ("Employee Directory", "Explains how to update your title, phone, and photo in the directory."),
        ("Holiday Gifts to Members and Providers", "Prohibits gifts to members and limits gifts to providers to items of nominal value."),
        ("Company Events", "Covers conduct, alcohol, and expense rules for company-sponsored events."),
    ]),
]

# Coverage rules for specific services. These carry CPT and HCPCS codes, which semantic search
# handles poorly: the setup for hybrid search in chapter 8.
SERVICES = [
    ("MRI of the lumbar spine", "72148", "after six weeks of documented conservative treatment, unless red-flag symptoms are present"),
    ("MRI of the brain", "70553", "for new neurological deficits, suspected tumor, or seizures not explained by prior imaging"),
    ("CT of the abdomen and pelvis", "74177", "when ultrasound is not diagnostic or not appropriate for the suspected condition"),
    ("PET scan for cancer staging", "78815", "for initial staging and restaging of cancers where it changes management"),
    ("Knee arthroscopy", "29881", "for mechanical symptoms with imaging-confirmed meniscal tear after failed physical therapy"),
    ("Total knee replacement", "27447", "for advanced arthritis with functional limitation after conservative treatment"),
    ("Total hip replacement", "27130", "for advanced arthritis or avascular necrosis with documented functional limitation"),
    ("Lumbar spinal fusion", "22612", "for instability or deformity with neurological findings, after a shared decision-making visit"),
    ("Bariatric surgery (sleeve gastrectomy)", "43775", "for adults meeting body mass index criteria who completed a supervised weight management program"),
    ("Sleep study in a lab", "95810", "when a home sleep test is not appropriate or was inconclusive"),
    ("Home sleep apnea test", "95806", "for adults with suspected moderate to severe obstructive sleep apnea"),
    ("CPAP device", "E0601", "after a sleep study confirms obstructive sleep apnea; continued coverage requires adherence data"),
    ("Outpatient physical therapy beyond 20 visits", "97110", "with documented functional progress toward measurable goals"),
    ("Speech therapy", "92507", "for speech or swallowing disorders with a treatment plan and measurable goals"),
    ("Genetic testing for hereditary cancer (BRCA1/BRCA2)", "81162", "when personal or family history meets national testing criteria and results will change care"),
    ("Upper endoscopy", "43239", "for persistent symptoms, alarm features, or surveillance of known conditions"),
    ("Colonoscopy (diagnostic)", "45378", "for symptoms or abnormal screening results; screening colonoscopy needs no authorization"),
    ("Cardiac catheterization", "93458", "for suspected coronary disease with positive or inconclusive noninvasive testing"),
    ("Echocardiogram (transthoracic)", "93306", "for new symptoms, murmurs, or changes in known heart disease"),
    ("Cardiac stress test with imaging", "78452", "when a standard exercise test is not possible or not diagnostic"),
    ("Spinal cord stimulator trial", "63650", "for chronic pain after failed conservative and surgical treatment, with a psychological evaluation"),
    ("Epidural steroid injection", "62323", "for radicular pain with imaging correlation, limited to a set number per year"),
    ("Radiofrequency ablation of spinal nerves", "64635", "after two positive diagnostic medial branch blocks"),
    ("Cataract surgery", "66984", "when vision loss limits daily activities and cannot be corrected with glasses"),
    ("Hysterectomy (laparoscopic)", "58571", "after documented failure or contraindication of less invasive treatment"),
    ("Septoplasty", "30520", "for nasal obstruction with documented failure of medical therapy"),
    ("Tonsillectomy for adults", "42826", "for recurrent tonsillitis meeting frequency criteria or for obstructive symptoms"),
    ("Varicose vein ablation", "36475", "for symptomatic venous reflux after a trial of compression stockings"),
    ("Hyperbaric oxygen therapy", "99183", "for specific approved indications such as non-healing diabetic foot ulcers"),
    ("Inpatient rehabilitation admission", "None", "when the member needs and can tolerate intensive therapy three hours a day"),
    ("Skilled nursing facility admission", "None", "after a qualifying need for daily skilled care that cannot be provided at home"),
    ("Home infusion therapy", "99601", "for intravenous drugs that can be safely given at home with nursing support"),
    ("Proton beam therapy", "77520", "for tumors near critical structures where the dose benefit over standard radiation is documented"),
    ("Insulin pump", "E0784", "for members with diabetes on multiple daily injections who meet monitoring criteria"),
    ("Continuous glucose monitor", "A9276", "for members on insulin or with documented problematic hypoglycemia"),
    ("Power wheelchair", "K0823", "when a manual wheelchair or scooter cannot meet the member's mobility needs at home"),
    ("Applied behavior analysis for autism", "97153", "with a diagnosis, a treatment plan, and progress reviews every six months"),
    ("Transcranial magnetic stimulation for depression", "90867", "after at least two failed antidepressant trials and psychotherapy"),
    ("Residential substance use treatment", "H0018", "when withdrawal risk or relapse history makes outpatient care unsafe"),
    ("Gender-affirming surgery", "None", "with documentation from treating clinicians that national criteria are met"),
]


# Policies that also exist as full documents in samples/ keep the same number everywhere.
KNOWN_NUMBERS = {
    "Code of Conduct": "GEN-001",
    "Paid Time Off": "HR-104",
    "Remote and Hybrid Work": "HR-120",
    "Expense Reimbursement": "FIN-220",
    "Business Travel": "FIN-118",
    "Acceptable Use of Systems": "IT-210",
    "Medication Reconciliation": "CLN-305",
    "Sepsis Screening and Escalation": "CLN-410",
    "Member Appeals": "CLM-620",
    "Privacy Incident Reporting": "CMP-110",
    "Breach Notification": "CMP-220",
    "Fax Handling Procedure": "CMP-412",
    "Formulary Exception Requests": "PHR-310",
    "MRI of the lumbar spine": "UM-512",
}


def main():
    entries = []
    taken = set(KNOWN_NUMBERS.values())
    for department, prefix, start, items in HAND_WRITTEN:
        number = start
        for title, summary in items:
            policy = KNOWN_NUMBERS.get(title)
            if policy is None:
                while f"{prefix}-{number}" in taken:
                    number += 1
                policy = f"{prefix}-{number}"
                number += 1
            taken.add(policy)
            entries.append({
                "id": policy.lower(),
                "policy": policy,
                "title": title,
                "department": department,
                "summary": summary,
            })

    for i, (service, code, criteria) in enumerate(SERVICES):
        number = 700 + i
        policy = KNOWN_NUMBERS.get(service, f"COV-{number}")
        code_text = "" if code == "None" else f" (CPT {code})" if code.isdigit() else f" (HCPCS {code})"
        entries.append({
            "id": policy.lower(),
            "policy": policy,
            "title": f"Coverage criteria: {service}",
            "department": "Claims",
            "summary": f"Prior authorization is required for {service.lower() if not service[0:3].isupper() else service}{code_text}. It is approved {criteria}.",
        })

    assert len({e["id"] for e in entries}) == len(entries), "duplicate policy numbers"
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(entries, indent=2) + "\n")
    print(f"{len(entries)} entries")


main()
