# Deductible Dynamics sample documents

Fictional policies, protocols, and procedures for Deductible Dynamics, the health plan in the book.
Every name, number, and rule here is invented. None of it is medical, legal, or HR advice.

Each file starts with front matter that stands in for the source system's access control list:

| Field | Meaning |
|---|---|
| `allowed-groups` | Entra ID groups that may see the document. Empty or missing means nobody (deny by default). |
| `sensitivity` | Public, Internal, Confidential, or Restricted |
| `phi` | Whether the document contains protected health information |

The groups are AllStaff (every employee), Clinical, Claims, HR, Compliance, and Contractors.
Contractors are not in AllStaff.

| Document | Allowed groups |
|---|---|
| code-of-conduct | AllStaff, Contractors |
| it-acceptable-use | AllStaff, Contractors |
| privacy-incident-reporting | AllStaff, Contractors |
| contractor-handbook | Contractors, HR |
| pto-policy | AllStaff |
| remote-work-policy | AllStaff |
| expense-reimbursement | AllStaff |
| sepsis-screening-protocol | Clinical |
| medication-reconciliation | Clinical |
| prior-auth-lumbar-mri | Clinical, Claims |
| claims-appeals-procedure | Claims, Compliance |
| workplace-investigations | HR |
| breach-risk-assessment | Compliance |
| incident-2026-031 | Compliance (contains synthetic PHI) |
| draft-layoff-plan | none, so it is never indexed |
