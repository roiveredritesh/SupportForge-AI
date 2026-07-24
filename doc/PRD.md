### **Product Requirements Document (PRD)**

**Product Name:** SupportForge AI  
**Description:** Plug-and-Play Multi-Project AI Support Agent Pipeline  
**Version:** 1.0 (MVP)  
**Date:** July 24, 2026  

---

#### **1. Executive Summary**

SupportForge AI is an internal **plug-and-play AI platform** that helps support teams quickly resolve customer queries involving product features, code issues, and error screenshots across multiple projects.

It features a reusable agent pipeline, automated knowledge/code ingestion, and a user-friendly interface for support staff.

---

#### **2. Objectives**

- Reduce average ticket resolution time by 40-60%.
- High adoption by support team.
- Easy onboarding of new projects.
- Cost-efficient operation.
- Excellent user experience for support staff.

---

#### **3. User Personas**

- **Support Engineer** (Primary User): Daily user who interacts with the AI to get fast answers.
- **Support Lead / Admin**: Manages projects, monitors usage, reviews feedback.
- **Engineering Contributor**: Occasionally helps with KB and code indexing.

---

#### **4. Scope**

**In Scope (MVP)**
- Multi-agent pipeline using Microsoft Agent Framework.
- Plug-and-play configuration system.
- Multi-source KB ingestion + Multi-GitHub repo code indexing.
- Screenshot analysis.
- Freshness management.
- Vector DB abstraction (**Chroma** local ↔ **Pinecone** production).
- **React-based UI** for support users.
- Deployment on **EC2 + IIS**.

---

#### **5. UI / User Interaction (New Detailed Section)**

**Frontend Technology**: **React** (with TypeScript)

**Primary Interaction Channels** (MVP):

1. **Main Web Application** (React + Vite)
   - Internal web app hosted on the same EC2/IIS (or via API).
   - Primary interface for support team.

2. **Future Slack/Teams Integration** (Phase 1.5)

**Key Screens & Flows**

**a. Dashboard (Home)**
- Project selector (dropdown for multi-project support).
- Quick search bar.
- Recent queries & answers.
- Usage stats and freshness score.

**b. New Query Page (Core Interaction)**
- Text input area.
- Screenshot / Image upload (drag & drop + paste support).
- Optional: Attach code snippet or ticket link.
- “Analyze” button → Shows loading with progress steps (Triage → Research → Analysis → Drafting).
- Results Screen:
  - Clean, well-formatted answer.
  - Cited sources (KB + Code with file/repo links).
  - Confidence score + freshness indicator.
  - “Copy Response”, “Edit Draft”, “Mark as Useful/Not Useful”, “Escalate to Engineer” buttons.
  - One-click “Send to Customer” (with optional edits).

**c. Project Management (Admin View)**
- Add/Edit Project.
- Configure GitHub repos.
- Add KB sources (Confluence, Documents, APIs, etc.).
- View ingestion status and last sync time.
- Trigger manual re-index.

**d. Analytics / Monitoring**
- Token usage per project.
- Most common queries.
- Feedback trends.
- Staleness alerts.

**UI/UX Principles**
- Clean, fast, minimalistic.
- Strong mobile responsiveness (support may use it on laptops).
- Dark mode support.
- Keyboard shortcuts for power users.
- Clear error messages with retry options.
- Accessibility compliant.

**Tech Stack for UI**
- React 18 + TypeScript
- Tailwind CSS or shadcn/ui
- TanStack Query (for data fetching)
- Zustand or Context for state
- Axios for API calls

---

#### **6. Backend & Core Features**

**Vector Database Strategy**
- Local: **Chroma**
- Production: **Pinecone**
- Abstracted via `IVectorStoreService`

**Agent Pipeline**
- Triage, KB Researcher, Code Analyzer, Vision Analyzer, Drafter, Coordinator.

**Automation**
- Ingestion pipeline for KB & Code (webhooks + scheduled).
- Incremental updates.

**Freshness Management**
- GitHub webhooks, scheduled jobs, metadata tracking.

---

#### **7. Non-Functional Requirements**

- **Performance**: End-to-end response time < 8-12 seconds for most queries.
- **Security**: Entra ID authentication, role-based access (Support vs Admin), secure storage of credentials.
- **Hosting**: EC2 Windows + IIS (backend) + React (served statically or via API).
- **Scalability**: Support 10+ projects initially.
- **Monitoring**: Application Insights + OpenTelemetry.

---

#### **8. Risks & Mitigations**

- Stale data → Automated freshness + warnings.
- Poor UX adoption → Intuitive React UI with fast feedback loop.
- Token cost → Optimized retrieval + model tiering.
- Multi-repo complexity → Rich metadata + cross-repo tools.

---

#### **9. Roadmap**

**MVP (4–6 weeks)**
- Core agent pipeline.
- React UI with main query + results flow.
- Chroma (local) + Pinecone (prod) support.
- 1–2 pilot projects live.

**Phase 2**
- Slack/Teams bot integration.
- Advanced admin dashboard.
- More KB connectors.

---
