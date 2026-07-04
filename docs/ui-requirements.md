# RFP Extractor — Monitoring UI (`rfpx serve`)
## Functional Requirements

> High-level, functionality-only requirements for the local monitoring interface.
> Visual/interaction design is intentionally **out of scope** for this document.
> Keywords **MUST / SHOULD / MAY** are used in the RFC-2119 sense.

---

### 1. Purpose

Provide a local, browser-based interface to launch a single questionnaire ingestion and
observe its **progress and results in real time**, and to **validate LLM connectivity**
before any document is processed. It makes the extraction pipeline observable and operable
without the command line.

### 2. Scope

| In scope | Out of scope |
|---|---|
| Single-document ingestion | Batch/multi-file queue |
| Preflight environment validation | Job history / archive |
| Real-time progress + streamed results | Answer authoring / fill-back |
| Result download + save-to-disk | Authentication / multi-user / remote hosting |

### 3. Actors

- **Operator** — a single local user running the tool on their own machine.
- **External dependencies** (called by the system, not users): the configured LLM provider and
  the selected document engine/conversion service.

---

## 4. Functional Requirements

### 4.1 Startup & Preflight Checks
- **FR-1.1** On launch, the UI MUST run a sequence of environment checks before ingestion begins.
- **FR-1.2** Checks MUST include, at minimum: runtime availability, document-engine readiness,
  LLM configuration presence, and **live LLM connectivity**.
- **FR-1.3** The connectivity check MUST perform a **real request** to the configured provider and
  confirm a response — not merely confirm that configuration values exist.
- **FR-1.4** Each check MUST report an outcome of **pass / warning / fail** plus a human-readable detail.
- **FR-1.5** If a configuration check fails, dependent checks (e.g. connectivity) MUST be reported as
  **skipped**, not executed.
- **FR-1.6** Results MUST appear **incrementally** as each check completes; the operator MUST NOT have
  to wait for all checks to see the first.
- **FR-1.7** If all checks pass the UI MAY proceed automatically; if any fails/warns the operator MUST
  be able to proceed manually.
- **FR-1.8** Checks MUST run against the provider/engine the server was started with and MUST name the
  correct provider/engine in their messages.

### 4.2 Job Configuration & Input
- **FR-2.1** The operator MUST be able to select **one** input document.
- **FR-2.2** Accepted input types MUST be Word (`.docx`), PDF (`.pdf`), and Excel (`.xlsx/.xlsm/.xls`).
- **FR-2.3** The operator MUST be able to set, per run: document engine, LLM provider, model,
  extraction strategy, render resolution, and concurrency.
- **FR-2.4** Configuration fields MUST default to the server's startup settings.
- **FR-2.5** Ingestion MUST NOT be startable until a valid input document is selected.

### 4.3 Ingestion Execution
- **FR-3.1** Submitting a job MUST upload the document and start **exactly one** ingestion using the
  selected configuration.
- **FR-3.2** Each job MUST be assigned a unique identifier.
- **FR-3.3** Ingestion MUST run asynchronously; the UI MUST remain responsive and begin streaming
  immediately.
- **FR-3.4** The pipeline invoked MUST be behaviourally identical to the command-line path (same
  engines, LLM handling, reconciliation, and resilience behaviour).

### 4.4 Real-Time Monitoring
- **FR-4.1** The UI MUST display the active job's configuration (file, engine, provider, model, strategy).
- **FR-4.2** The UI MUST show **per-leg progress** (vision pages, text chunks, grid sheets) with
  completed/total counts.
- **FR-4.3** Progress MUST update in real time as each unit (page/chunk/sheet) completes.
- **FR-4.4** The UI MUST present a chronological **activity log** of pipeline events, including retries
  and failures.
- **FR-4.5** Extracted questions MUST **stream in incrementally** as each unit completes — not only at
  the end of the run.
- **FR-4.6** Each streamed question MUST show its text and provenance (originating leg, section,
  answer type).
- **FR-4.7** A running **count** of discovered questions MUST be displayed and kept current.
- **FR-4.8** All real-time updates MUST be **pushed** from server to browser (no client polling).

### 4.5 Results & Persistence
- **FR-5.1** On completion the UI MUST display summary metrics: total questions, agreed (multi-leg)
  count, needs-review count, and elapsed time.
- **FR-5.2** Any warnings raised during the run MUST be surfaced to the operator.
- **FR-5.3** The operator MUST be able to **download** each of the four result artifacts individually
  (document schema, questions, review queue, reconciliation report).
- **FR-5.4** The operator MUST be able to **persist all four artifacts to a local folder** ("save to disk").
- **FR-5.5** The save action MUST provide a default destination and MUST let the operator edit it before saving.
- **FR-5.6** The save action MUST confirm success (including the final path written) or report the failure reason.

### 4.6 Provider & Engine Flexibility
- **FR-6.1** The UI MUST support multiple LLM providers (enterprise gateway, Azure OpenAI, OpenAI),
  selectable per run.
- **FR-6.2** The UI MUST support multiple document engines, selectable per run.
- **FR-6.3** Provider and engine selection MUST be **independent** — any combination is permitted.
- **FR-6.4** Missing provider/engine prerequisites MUST be reported by preflight checks with
  **actionable remediation** text.

### 4.7 Error Handling
- **FR-7.1** An upload with no file (or an empty file) MUST be rejected with a message.
- **FR-7.2** A fatal ingestion error MUST be surfaced to the operator **without terminating the UI**.
- **FR-7.3** Transient per-unit failures MUST NOT abort the run; they MUST be visible in the log and,
  if permanent for that unit, reflected in the run's warnings.
- **FR-7.4** Requests referencing an unknown or unfinished job MUST fail safely (not-found), never
  return partial/incorrect data.

---

## 5. Operational Constraints (functional)

- **OC-1** The UI MUST be **self-contained** — no external network dependency for its own assets.
- **OC-2** The server MUST bind to the **local machine only**.
- **OC-3** The server MUST bind its port **before** opening the browser and MUST fail clearly if the
  port is unavailable (never silently attach to a pre-existing instance).
- **OC-4** Scope is **single-user, single active session**; concurrent jobs are not required.
- **OC-5** Uploaded inputs and in-memory results are **ephemeral** — not retained unless the operator saves them.

## 6. Assumptions & Dependencies

- A reachable LLM provider is available and its credentials are configured in the environment **before**
  the server is started (a running server does not pick up newly-set credentials).
- The selected document engine's prerequisite (local licence or conversion service) is available.

---

## 7. Acceptance Criteria (traceable)

| # | Criterion | Requirements |
|---|---|---|
| AC-1 | Launcher runs all four checks incrementally; connectivity performs a real provider round-trip | FR-1.1–1.6 |
| AC-2 | A run cannot start without a selected, supported input file | FR-2.1, FR-2.2, FR-2.5 |
| AC-3 | Progress, activity log, and questions all update live during the run (server-pushed) | FR-4.2–4.8 |
| AC-4 | Questions appear before the run finishes (streamed, not batched) | FR-4.5 |
| AC-5 | Completion shows summary + warnings; artifacts are downloadable and can be saved to a chosen folder | FR-5.1–5.6 |
| AC-6 | Any engine × any provider combination is selectable and validated | FR-6.1–6.4 |
| AC-7 | Failures (upload, fatal, per-unit, unknown job) are handled without crashing the UI | FR-7.1–7.4 |
| AC-8 | Port already in use produces a clear error and no misleading UI | OC-3 |

## 8. Out of Scope / Future Candidates

Batch / multi-file queue · job history & comparison · pause/cancel mid-run · answer fill-back ·
authentication & multi-user · remote/hosted deployment · result diffing across runs.
