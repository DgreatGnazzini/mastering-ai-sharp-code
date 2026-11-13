"""Generates chapter 4's sample files: a text PDF, a Word document, a scanned PDF, and a scanned
prior authorization intake form. Everything is fictional. Run from the repository root:

    python3 scripts/generate-ch04-samples.py

Needs reportlab, Pillow, and python-docx. The outputs are committed, so readers do not need to run this.
"""
import json
import random
from pathlib import Path

from docx import Document
from PIL import Image, ImageDraw, ImageFilter, ImageFont
from reportlab.lib.pagesizes import LETTER
from reportlab.lib.units import inch
from reportlab.pdfgen import canvas

random.seed(42)
FILES = Path("samples/files")
INTAKE = Path("samples/intake")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
FONT_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
FONT_MONO = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"


def meta(path, **fields):
    Path(str(path) + ".meta.json").write_text(json.dumps(fields, indent=2) + "\n")


# 1. A text PDF: two pages, real text a PDF library can read.
FORMULARY = [
    ("Formulary Exception Process", None),
    ("Purpose", "This procedure explains how a prescriber requests coverage for a drug that is not on the Deductible Dynamics formulary, or for a dose above a formulary limit."),
    ("Who may request", "The prescriber, the member, or the member's authorized representative may request an exception. Pharmacy technicians may submit a request on a prescriber's behalf."),
    ("Required documentation", "Every request needs a prescriber statement that the formulary alternatives were ineffective, not tolerated, or are contraindicated. Attach chart notes for at least one tried alternative."),
    ("Decision timelines", "Standard requests are decided within 72 hours of receipt. Expedited requests, when the prescriber states that waiting could seriously jeopardize the member's health, are decided within 24 hours."),
    ("If the exception is approved", "An approved exception lasts 12 months or for the length of the prescription, whichever is shorter. Renewal requires a new request with updated chart notes."),
    ("If the exception is denied", "The denial letter states the reason and the appeal rights. Members may appeal within 60 days under the Claims Appeals Procedure, CLM-220."),
]
c = canvas.Canvas(str(FILES / "formulary-exception-process.pdf"), pagesize=LETTER)
c.setTitle("Formulary Exception Process (PHR-310)")
y = 10 * inch
for i, (heading, body) in enumerate(FORMULARY):
    if i == 4:
        c.showPage()
        y = 10 * inch
    if body is None:
        c.setFont("Helvetica-Bold", 18)
        c.drawString(inch, y, heading)
        c.setFont("Helvetica", 9)
        c.drawString(inch, y - 16, "Policy PHR-310 | Pharmacy | Effective January 1, 2026")
        y -= 48
        continue
    c.setFont("Helvetica-Bold", 13)
    c.drawString(inch, y, heading)
    y -= 18
    c.setFont("Helvetica", 11)
    line = ""
    for word in body.split():
        if c.stringWidth(line + " " + word, "Helvetica", 11) > 6.5 * inch:
            c.drawString(inch, y, line)
            y -= 15
            line = word
        else:
            line = (line + " " + word).strip()
    c.drawString(inch, y, line)
    y -= 30
c.save()
meta(FILES / "formulary-exception-process.pdf", title="Formulary Exception Process", policy="PHR-310",
     department="Claims", allowedGroups=["Clinical", "Claims"], sensitivity="Confidential", phi=False)

# 2. A Word document with real heading styles.
doc = Document()
doc.add_heading("Business Travel Policy", level=1)
doc.add_paragraph("Policy FIN-118. Applies to all employees who travel for Deductible Dynamics business.")
doc.add_heading("Booking", level=2)
doc.add_paragraph("Book flights and hotels through the corporate travel portal at least 14 days before departure. Economy class is required for flights under six hours.")
doc.add_heading("Meals", level=2)
doc.add_paragraph("The daily meal allowance is $65 within the plan's service area and $80 elsewhere. Alcohol is not reimbursed.")
table = doc.add_table(rows=3, cols=2)
for row, (k, v) in zip(table.rows, [("Trip length", "Allowance"), ("Same day", "$25"), ("Overnight", "$65 or $80 a day")]):
    row.cells[0].text, row.cells[1].text = k, v
doc.add_heading("Mileage", level=2)
doc.add_paragraph("Personal vehicle use is reimbursed at the IRS standard mileage rate in effect on the date of travel, from your normal work location.")
doc.core_properties.title = "Business Travel Policy"
doc.save(FILES / "business-travel-policy.docx")
meta(FILES / "business-travel-policy.docx", title="Business Travel Policy", policy="FIN-118",
     department="AllStaff", allowedGroups=["AllStaff"], sensitivity="Internal", phi=False)


def scan(img, angle):
    """Make a clean render look like a fax: grayscale, slight rotation, speckle, and blur."""
    img = img.convert("L").rotate(angle, resample=Image.BICUBIC, expand=False, fillcolor=255)
    px = img.load()
    for _ in range(img.width * img.height // 400):
        x, y = random.randrange(img.width), random.randrange(img.height)
        px[x, y] = random.choice([0, 60, 200])
    return img.filter(ImageFilter.GaussianBlur(0.6))


# 3. A scanned PDF: the same kind of policy, but only as an image of a page. No text layer.
W, H = 1700, 2200  # 200 dpi letter
page = Image.new("L", (W, H), 255)
d = ImageDraw.Draw(page)
title, body_font, head_font = ImageFont.truetype(FONT_BOLD, 52), ImageFont.truetype(FONT, 34), ImageFont.truetype(FONT_BOLD, 38)
d.text((150, 150), "Fax Handling Procedure", font=title, fill=0)
d.text((150, 225), "Procedure CMP-412 | Compliance | Revised March 2026", font=ImageFont.truetype(FONT, 26), fill=0)
y = 330
SECTIONS = [
    ("Before you send", ["Confirm the fax number with the recipient by phone", "for every first-time recipient. Use the cover sheet", "with the confidentiality notice on every fax."]),
    ("Minimum necessary", ["Send only the pages the recipient needs for the", "stated purpose. Never fax a full chart."]),
    ("Misdirected faxes", ["If a fax with PHI goes to the wrong number, report it", "to the Privacy Office within 1 business day using", "the privacy incident form. Ask the recipient to", "destroy it and confirm in writing."]),
]
for heading, lines in SECTIONS:
    d.text((150, y), heading, font=head_font, fill=0)
    y += 60
    for line in lines:
        d.text((150, y), line, font=body_font, fill=0)
        y += 48
    y += 40
# The words on the page, so the live OCR test can measure how many came back.
truth = ["# Fax Handling Procedure", "", "Procedure CMP-412 | Compliance | Revised March 2026"]
for heading, lines in SECTIONS:
    truth += ["", f"## {heading}", "", " ".join(lines)]
(FILES / "fax-handling-procedure-scanned.expected.txt").write_text("\n".join(truth) + "\n")
scanned = scan(page, -1.2)
c = canvas.Canvas(str(FILES / "fax-handling-procedure-scanned.pdf"), pagesize=LETTER)
tmp = FILES / "_scan.png"
scanned.save(tmp)
c.drawImage(str(tmp), 0, 0, width=8.5 * inch, height=11 * inch)
c.save()
tmp.unlink()
meta(FILES / "fax-handling-procedure-scanned.pdf", title="Fax Handling Procedure", policy="CMP-412",
     department="Compliance", allowedGroups=["AllStaff"], sensitivity="Internal", phi=False)

# 4. A scanned prior authorization intake form, filled in. All data is invented.
FORM = {
    "memberName": "Jordan A. Whitaker",
    "memberId": "DD-5820317",
    "dateOfBirth": "1978-09-22",
    "requestedService": "MRI lumbar spine without contrast",
    "cptCode": "72148",
    "icd10Code": "M54.16",
    "orderingProviderName": "Dr. Lena Park",
    "orderingProviderNpi": "1457392060",
    "urgent": False,
}
form = Image.new("L", (W, 1500), 255)
d = ImageDraw.Draw(form)
d.text((120, 90), "DEDUCTIBLE DYNAMICS", font=ImageFont.truetype(FONT_BOLD, 44), fill=0)
d.text((120, 150), "Prior Authorization Request - Outpatient Imaging", font=ImageFont.truetype(FONT, 34), fill=0)
d.line((120, 210, W - 120, 210), fill=0, width=3)
label, hand = ImageFont.truetype(FONT, 28), ImageFont.truetype(FONT_MONO, 34)
rows = [
    ("Member name", FORM["memberName"]),
    ("Member ID", FORM["memberId"]),
    ("Date of birth", "09/22/1978"),
    ("Requested service", FORM["requestedService"]),
    ("CPT code", FORM["cptCode"]),
    ("Diagnosis (ICD-10)", FORM["icd10Code"]),
    ("Ordering provider", f'{FORM["orderingProviderName"]}, NPI {FORM["orderingProviderNpi"]}'),
]
y = 260
for k, v in rows:
    d.text((120, y), k, font=label, fill=0)
    d.line((520, y + 42, W - 120, y + 42), fill=0, width=2)
    d.text((540, y), v, font=hand, fill=0)
    y += 110
d.text((120, y), "Urgent request?", font=label, fill=0)
for i, opt in enumerate(["Yes", "No"]):
    x = 540 + i * 220
    d.rectangle((x, y + 4, x + 32, y + 36), outline=0, width=3)
    d.text((x + 50, y), opt, font=label, fill=0)
d.line((760 + 4, y + 8, 760 + 28, y + 32), fill=0, width=4)
d.line((760 + 28, y + 8, 760 + 4, y + 32), fill=0, width=4)
scan(form, 0.8).save(INTAKE / "intake-form-001.png", optimize=True)
(INTAKE / "intake-form-001.expected.json").write_text(json.dumps(FORM, indent=2) + "\n")
print("done")
