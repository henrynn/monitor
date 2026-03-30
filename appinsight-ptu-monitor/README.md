# appinsight-ptu-monitor

Azure OpenAI **PTU (Provisioned Throughput Unit)** utilization monitoring and proactive traffic routing — built on top of [appinsight-zavademo](../appinsight-zavademo).

## What This Adds

While `appinsight-zavademo` demonstrates general App Insights + OpenTelemetry observability, this module focuses specifically on **Azure OpenAI PTU production operations**:

| Capability | appinsight-zavademo | appinsight-ptu-monitor |
|------------|:---:|:---:|
| App Insights + OTel integration | ✅ | ✅ (same stack) |
| Custom metrics → App Insights | demo metrics | PTU utilization %, TTFT, E2E, 429 count |
| Live Metrics (real-time) | ✅ | ✅ |
| AOAI proxy with header capture | — | ✅ `x-ratelimit-remaining-tokens` |
| Proactive PTU → PAYGO routing | — | ✅ (threshold-based) |
| APIM policy for production | — | ✅ (XML ready) |
| Azure Monitor alert rules | — | ✅ (Bicep template) |
| KQL dashboard queries | — | ✅ (7 queries) |

## Architecture

```
┌──────────┐  POST /api/chat  ┌──────────────────────────┐
│  Client  │ ───────────────> │   PTU Monitor Server      │
│          │ <─────────────── │   (Node.js + OTel)        │
└──────────┘   JSON response  │                           │
                              │  if util < 95% ──> PTU    │
                              │  if util >= 95% ─> PAYGO  │
                              └──────┬──────────┬─────────┘
                                     │          │
                              ┌──────▼──┐ ┌────▼─────┐
                              │  PTU    │ │  PAYGO   │
                              │ Deploy  │ │  Deploy  │
                              └─────────┘ └──────────┘
                                     │
                              ┌──────▼──────────┐
                              │ Application     │
                              │ Insights        │
                              │ (Live Metrics + │
                              │  custom metrics)│
                              └─────────────────┘
```

## Shared Infrastructure with appinsight-zavademo

This module reuses the same OpenTelemetry patterns:

- **`telemetry.js`**: Same `useAzureMonitor()` initialization with `enableLiveMetrics: true`
- **`@azure/monitor-opentelemetry`**: Same Azure Monitor distro
- **Application Insights**: Can share the same instance or use a dedicated one
- **Bicep**: `ptu-monitoring/infra/ptu-monitoring.bicep` deploys additional resources (APIM, alerts) on top of the shared platform

## Quick Start

```bash
cd appinsight-ptu-monitor/server
npm install

# Single endpoint (monitoring only)
PTU_ENDPOINT=https://YOUR_ENDPOINT.openai.azure.com \
PTU_API_KEY=YOUR_KEY \
AOAI_DEPLOYMENT=gpt-5.4-nano \
APPLICATIONINSIGHTS_CONNECTION_STRING="InstrumentationKey=xxx;..." \
npm start

# Dual endpoint (monitoring + proactive routing)
PTU_ENDPOINT=https://YOUR_PTU.openai.azure.com \
PTU_API_KEY=YOUR_PTU_KEY \
PAYGO_ENDPOINT=https://YOUR_PAYGO.openai.azure.com \
PAYGO_API_KEY=YOUR_PAYGO_KEY \
ROUTING_THRESHOLD=95 \
APPLICATIONINSIGHTS_CONNECTION_STRING="InstrumentationKey=xxx;..." \
npm start
```

### Test

```bash
# Single request
curl -X POST http://localhost:8080/api/chat \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"Hello"}]}'

# PTU status
curl http://localhost:8080/api/status

# Stress test
curl -X POST http://localhost:8080/api/stress \
  -H "Content-Type: application/json" \
  -d '{"concurrency":10,"total":50}'
```

## API Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/api/chat` | Proxy AOAI request with monitoring + routing |
| `GET` | `/api/status` | Current PTU utilization + routing decision |
| `POST` | `/api/stress` | Concurrent stress test |
| `POST` | `/api/simulate` | Set utilization manually (testing) |
| `GET` | `/healthz` | Health check + config |

## Custom Metrics (App Insights)

| Metric | Type | Description |
|--------|------|-------------|
| `ptu.utilization_pct` | Histogram | TPM utilization percentage |
| `ptu.ttft_ms` | Histogram | Time to first token |
| `ptu.e2e_ms` | Histogram | End-to-end latency |
| `ptu.http429_count` | Counter | Throttled requests |
| `ptu.routing_decision` | Counter | PTU vs PAYGO decisions |

## Production Deployment (ptu-monitoring/)

For production, use Azure Monitor + APIM instead of the Node.js proxy:

| File | Purpose |
|------|---------|
| `ptu-monitoring/infra/ptu-monitoring.bicep` | Deploys: Log Analytics + Diagnostic Settings + 3 Alert Rules + APIM + Backends |
| `ptu-monitoring/apim-policy-ptu-routing.xml` | APIM policy: proactive routing + 429 retry + emit-metric |
| `ptu-monitoring/kql-queries.kql` | 7 KQL queries for dashboards |
| `ptu-monitoring/deploy.sh` | One-command deployment |

## Validation Results

Tested against a real Azure OpenAI PAYGO deployment:

| Validation | Result |
|------------|:------:|
| Azure Monitor platform metrics (8 metrics) | ✅ All confirmed |
| Rate-limit headers in streaming mode | ✅ 100% availability (300+ requests) |
| KQL AzureDiagnostics queries | ✅ Queryable in Log Analytics |
| Alert rule deployment + Action Group | ✅ Deployed and active |
| Application Insights Live Metrics | ✅ Real-time < 1s latency |
| Routing logic (PTU → PAYGO switch) | ✅ 6/6 E2E tests passed |

## Credits

Built on [appinsight-zavademo](../appinsight-zavademo) by Xuebing Bai — reusing the OpenTelemetry + Azure Monitor integration patterns.
