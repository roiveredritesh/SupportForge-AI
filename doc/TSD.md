### **Technical Specification & Design Document (TSD)**  
**SupportForge AI – Plug-and-Play Support Agent Pipeline**  
**Version:** 1.0 MVP  
**Target Completion:** **7 Days** (July 24 – July 31, 2026)

---

#### **1. Architecture Overview**

- **Frontend**: React 18 + TypeScript
- **Backend**: .NET 8/9 + ASP.NET Core Web API (IIS)
- **Agent Engine**: Microsoft Agent Framework
- **Vector DB**: Chroma (Local) ↔ Pinecone (Production)
- **Deployment**: Single EC2 Windows instance + IIS

---

#### **2. Detailed Tech Stack**

| Layer            | Technology                              | Version / Notes                     |
|------------------|-----------------------------------------|-------------------------------------|
| Frontend         | React + TypeScript + Vite + Tailwind + shadcn/ui | Modern, fast |
| Backend          | .NET 8/9 + ASP.NET Core                 | Hosted on IIS |
| Agents           | Microsoft Agent Framework               | Latest 2026 version |
| Vector Store     | Chroma (local) / Pinecone (prod)        | Abstracted |
| Git              | LibGit2Sharp                            | Code ingestion |
| Auth             | Microsoft Entra ID                      | JWT |
| Monitoring       | Application Insights + OpenTelemetry    | - |

---

#### **3. Solution Folder Structure**

```
SupportForge.AI/
├── frontend/                          # React App
│   ├── src/
│   │   ├── components/
│   │   ├── pages/
│   │   ├── hooks/
│   │   └── lib/
│   └── vite.config.ts
│
├── backend/
│   ├── SupportForge.Api/              # Web API
│   ├── SupportForge.Core/             # Domain & Services
│   ├── SupportForge.Agents/           # Agent definitions & workflows
│   ├── SupportForge.Ingestion/        # Background worker
│   ├── SupportForge.VectorStore/      # Chroma + Pinecone
│   └── SupportForge.Common/           # Shared models
│
└── docs/
```

---

#### **4. UI / Frontend Specification (Detailed)**

**Framework**: React 18 + TypeScript + Vite

**Key Pages & Features**

1. **Login / Auth Page** (Entra ID)
2. **Dashboard**
   - Project selector (multi-project)
   - Quick search bar
   - Recent queries + feedback summary
   - System health (freshness score)

3. **Main Query Interface** (Most Important)
   - Large textarea for question
   - Drag & drop + paste screenshot support
   - Optional file/code snippet upload
   - “Ask Agent” button
   - Real-time status: “Triage → Searching KB → Analyzing Code → Generating Response”
   - Results Panel:
     - Formatted answer (Markdown)
     - Sources with clickable links (repo files, Confluence pages)
     - Confidence + freshness badge
     - Action buttons: Copy, Edit, Send to Customer, Not Helpful, Escalate

4. **Admin / Configuration Page**
   - Add/Edit Project
   - GitHub repos management
   - KB sources configuration
   - Ingestion status & manual trigger

**UI/UX Requirements**
- Clean, modern, support-friendly interface
- Fast loading (< 1s for UI)
- Responsive (works well on laptop)
- Dark/Light mode
- Loading skeletons + progress indicators
- Error boundaries with retry

**State Management**: Zustand  
**API Client**: TanStack Query (React Query)  
**Styling**: Tailwind + shadcn/ui components

---

#### **5. 7-Day MVP Execution Plan (Including Full UI)**

**Day 1: Foundation**
- Create .NET solution + React Vite app
- Setup IIS hosting + basic API endpoint
- Implement `IVectorStoreService` (Chroma)
- Basic project config system

**Day 2: Agent Core + Simple Workflow**
- Setup Microsoft Agent Framework
- Build Triage + Drafter agents
- Basic end-to-end query API

**Day 3: Ingestion & Tools**
- Ingestion background service
- Documents + one more KB connector
- GitHub code ingestion (basic)
- KB Search + Code Search tools

**Day 4: Vision + Advanced Agents**
- Screenshot analysis tool
- Full multi-agent workflow
- Freshness metadata

**Day 5: React UI – Core Query Flow**
- Dashboard + Query page
- Image upload + preview
- Results display with citations
- Connect to backend

**Day 6: Polish & Admin**
- Admin config pages
- Feedback system
- Loading states, error handling
- Token tracking

**Day 7: Testing, Deployment & Handover**
- End-to-end testing with real project data
- Deploy to EC2 + IIS (backend + frontend)
- Configuration guide + runbook
- Demo + knowledge transfer

---

#### **6. API Endpoints (MVP)**

- `POST /api/chat/query` – Main agent query (supports multipart for images)
- `GET /api/projects` – List projects
- `POST /api/projects` – Create/update project config
- `POST /api/ingestion/trigger` – Manual reindex

---

#### **7. Risks & Mitigation for 1-Week Delivery**

- Focus only on **core query loop** + basic connectors.
- Use simple connectors first (Documents + GitHub).
- Prioritize working UI over perfect admin UI.
- Daily testing to catch issues early.

**Realistic Outcome by End of Week**:
A working system where support users can:
- Select a project
- Ask questions + upload screenshots
- Get useful answers with sources
- Give feedback

Admin can add basic project configs.

---
