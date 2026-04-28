# AI-Powered IT Operational Insights for Azure — Just Ask in Plain Language

[![LinkedIn](https://img.shields.io/badge/Built%20by-Erik%20K%20Ipsen-0077B5?style=flat&logo=linkedin&logoColor=white)](https://www.linkedin.com/in/erik-k-ipsen/)

This project gives companies a fully customizable AI assistant — powered by Claude or GitHub Copilot — that connects directly to Azure. Ask questions in plain language, get real answers from real data. No expertise needed. Built on a controlled, secure foundation that the company owns and manages.

- [Built on an Official Microsoft Template](#built-on-an-official-microsoft-template)
- [What Problem Does This Solve?](#what-problem-does-this-solve)
- [Who Is This For?](#who-is-this-for)
- [Security: Data Stays in Company Control](#security-data-stays-in-company-control)
- [Tools Already in Place](#what-can-it-do-today)
- [Ideas for Customized Azure Tools](#build-custom-azure-tools--made-for-the-companys-needs)
- [How Does It Work?](#how-does-it-work-the-short-version)

---

## Built on an Official Microsoft Template

This project extends the official Microsoft Azure open-source sample [mcp-sdk-functions-hosting-dotnet](https://github.com/Azure-Samples/mcp-sdk-functions-hosting-dotnet), which demonstrates how to host an AI tool server (called an MCP server) on Azure's serverless platform. That foundation has been extended with enterprise-grade authentication, real Azure integrations, and tools that are useful for day-to-day operations.

---

## What Problem Does This Solve?

Most teams already have a lot of tools: Azure Portal, monitoring dashboards, log systems, alerting. The problem is that getting answers out of those tools takes time and expertise. It requires knowing where to look, how to navigate, and often how to write complex queries just to answer a simple question.

This project connects an AI assistant directly to an Azure environment — so instead of clicking through dashboards, just ask.

> *"How is our production app performing right now?"*
> *"Were there any errors in the payment service in the last 24 hours?"*
> *"What services are running in our staging environment?"*

The AI answers with real data, pulled live from the Azure infrastructure, using the permissions of the person who asked.

---

## Who Is This For?

This template is a great fit for:

- **Software companies and consulting firms** on Azure — keeping developers and teams in flow by letting AI extract operational insights and troubleshoot incidents, without leaving the code editor or switching between client environments
- **Manufacturing and enterprise companies** that use Azure for internal systems and want team leads or operations staff to access data without needing Azure expertise

---

## Security: Data Stays in Company Control

This is one of the most important things to understand about this project. It is completely **self-hosted** — running inside the company's own Azure subscription — and secured using Microsoft's enterprise identity system (Microsoft Entra ID, the same system that powers Microsoft 365 and Teams login).

Here is what that means in plain terms:

- **Only company employees can use it.** The server requires users to log in with their company Microsoft account before anything happens. No anonymous access, ever.

- **Each person sees only what they are allowed to see.** The AI does not have a powerful admin account of its own. It acts on behalf of the logged-in user — using that person's own Azure permissions. If someone cannot see a resource in Azure Portal, the AI cannot see it either.

- **Data never leaves the Azure environment.** Queries go to the Azure resources and come back to the user's screen. They do not pass through any external service.

- **No passwords stored anywhere.** Authentication is handled entirely through short-lived security tokens — the same technology used across all Microsoft enterprise products.

Think of it this way: the AI assistant carries the same keycard as the person using it. It can only open the doors that person is allowed to open.

### How the login flow works (in simple terms)

When a developer opens their AI assistant and asks a question:

1. The server asks them to log in with their company Microsoft account (just like logging into Teams or Outlook)
2. Once logged in, their identity is confirmed and their question is processed
3. The AI queries Azure on their behalf, using only their own access rights
4. The answer comes back directly to their screen

This is an industry-standard security pattern called "On-Behalf-Of" — the AI never acts with more authority than the person who asked.

---

## Tools Already in Place

### Application Insights Report Tool

This is the standout tool. Every application running in Azure generates a continuous stream of monitoring data: how many requests came in, how fast the app responded, what errors occurred, which parts of the app are most used. Reading this data normally means logging into Azure Portal, navigating to the right screen, and knowing how to write database queries like this:

```
AppRequests
| where TimeGenerated > ago(24h)
| summarize total = count(), failed = countif(Success == false),
    avgDuration = avg(DurationMs)
```

With this tool, anyone can simply ask:

> *"Give me a report on the payment service for the last 24 hours."*

And get back a plain-language answer like:

> *"The application received 45,230 requests with a 99.7% success rate and an average response time of 142 ms. There were 12 exceptions, mostly database timeouts. Availability tests are passing at 100%."*

**What the report covers:**

| What it measures | What it tells you |
|---|---|
| Request volume and success rate | Is the app healthy? How many users are hitting it? |
| Response times (average, P95, P99) | Is the app fast? Are there slowdowns under load? |
| Errors and exceptions | What is breaking, and how often? |
| External dependencies | Is the database or a third-party API the bottleneck? |
| Page views | Which parts of the app are most used? |
| Availability tests | Is the app reachable from the internet? |

**Who benefits:**

- **Developers** can easily let AI extract operational insights and troubleshoot incidents without leaving their coding environment
- **Team leads** can check service health without needing Azure Portal access
- **On-call engineers** can quickly assess an incident by writing prompts (not queries)
- **Project managers** get operational insights and stay informed on incidents in plain language, without needing technical expertise

> The tool supports both English and Danish responses — just ask in the language you prefer.

---

### Infrastructure Overview Tools

These tools give anyone a plain-language view of what is running in Azure:

- **List resource groups** — *"What environments do we have?"* Returns all resource groups the account can see, grouped by Azure region, with tags so results can be filtered by team or environment.
- **List services in a resource group** — *"What is running in our production environment?"* Returns all Azure resources (databases, servers, storage, APIs, etc.) inside a given environment.

---

## Ideas for Customized Azure Tools

The tools above are just the beginning. A development team can build new tools that connect to any Azure service or internal system. Here are some ideas focused on IT operations:

### Cost Monitoring
> *"What did we spend on Azure last month, broken down by team?"*

Connect to Azure Cost Management and let team leads check cloud spending in plain language — no Excel exports, no waiting for the monthly report.

### Active Alerts Summary
> *"Are there any open alerts in our production environment right now?"*

Pull all active Azure Monitor alerts and present them as a readable summary. Ideal for morning stand-ups or on-call handovers.

### Deployment Status
> *"What version of the API is running in production? When was it last deployed?"*

Query deployment history and container versions to give instant answers about what is live, and when it was last changed.

### Incident Investigation Assistant
> *"There is a spike in errors in the checkout service. What changed in the last two hours?"*

During an incident, automatically gather relevant context: recent deployments, error spikes, slow external dependencies, and unusual traffic patterns — all in one answer, in seconds.

### Security Posture Summary
> *"Do we have any critical security recommendations from Microsoft Defender?"*

Pull from Microsoft Defender for Cloud and summarize open security findings by severity. Give security-conscious managers a weekly health check without requiring them to log into another portal.

### Database Performance Insights
> *"Is our Azure SQL database performing well this week?"*

Surface query performance, CPU usage, slow queries, and connection counts from Azure SQL or Cosmos DB in plain language — useful for both developers and operations teams.

### Capacity Planning
> *"Which of our services is getting close to its limits?"*

Aggregate CPU, memory, and request metrics across all services and flag anything approaching its configured maximum — before it becomes a problem.

### Log Search in Plain Language
> *"Were there any failed login attempts in the last hour?"*

Let developers and operations staff search application logs by just describing what they are looking for. The AI translates the question into a database query behind the scenes.

---

## How Does It Work? (The Short Version)

The server runs as a small program inside the company's Azure subscription. When someone asks it a question from their AI assistant (Claude Code or GitHub Copilot in VS Code):

1. The server confirms the user is logged in with their company account
2. It calls the relevant Azure APIs using that person's own permissions
3. It returns a structured answer that the AI formats into plain language

The server is built on an open standard called **MCP (Model Context Protocol)**, which lets AI assistants connect to external tools and data sources. Hosting it on **Azure Functions** means there is no dedicated server to manage or pay for when idle — costs are usage-based.

---

## Technical README

The original technical README with setup instructions, deployment steps, and architecture details is available in [docs/readme-original.md](docs/readme-original.md).

