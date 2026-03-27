require("./telemetry");

const express = require("express");
const axios = require("axios");
const winston = require("winston");
const { trace, metrics, SpanStatusCode } = require("@opentelemetry/api");
const { DefaultAzureCredential } = require("@azure/identity");
const { BlobServiceClient } = require("@azure/storage-blob");
const { SecretClient } = require("@azure/keyvault-secrets");

const app = express();
const port = Number(process.env.PORT || "8080");
const tracer = trace.getTracer("ZavaDemo.Server");
const meter = metrics.getMeter("ZavaDemo.Server");
const requestCounter = meter.createCounter("server.demo.requests");
const durationHistogram = meter.createHistogram("server.demo.duration", { unit: "ms" });
const logger = winston.createLogger({
  level: "info",
  format: winston.format.combine(
    winston.format.timestamp(),
    winston.format.errors({ stack: true }),
    winston.format.json()
  ),
  defaultMeta: {
    service: process.env.OTEL_SERVICE_NAME || "zava-demo-server",
  },
  transports: [new winston.transports.Console()],
});

const credential = new DefaultAzureCredential();
const blobServiceClient = process.env.STORAGE_ACCOUNT_URL
  ? new BlobServiceClient(process.env.STORAGE_ACCOUNT_URL, credential)
  : null;
const keyVaultSecretName = process.env.KEYVAULT_SECRET_NAME || "demo-config";
const secretClient = process.env.KEYVAULT_URI
  ? new SecretClient(process.env.KEYVAULT_URI, credential)
  : null;
const uuidUrl = process.env.EXTERNAL_UUID_URL || "https://httpbin.org/uuid";
const todoUrl = process.env.EXTERNAL_TODO_URL || "https://jsonplaceholder.typicode.com/todos/1";

app.use(express.json());

app.get("/healthz", (_req, res) => {
  res.json({ ok: true, service: process.env.OTEL_SERVICE_NAME || "zava-demo-server" });
});

app.get("/api/demo", async (req, res) => {
  const simulateFailure = req.query.simulateFailure === "true";
  const triggeredDependencies = [];
  const startedAt = Date.now();
  requestCounter.add(1, { route: "/api/demo", simulateFailure });

  await tracer.startActiveSpan("server.orchestrate-demo", async (span) => {
    span.setAttribute("demo.simulate_failure", simulateFailure);
    span.addEvent("demo.request.accepted");

    try {
      logger.info("Demo request started", { simulateFailure });

      const [uuidResponse, todoResponse, storageResult, secretResult] = await Promise.all([
        axios.get(uuidUrl, { timeout: 5000, headers: { "User-Agent": "zava-demo-server" } }),
        axios.get(todoUrl, { timeout: 5000, headers: { "User-Agent": "zava-demo-server" } }),
        readStorage(triggeredDependencies),
        readSecret(triggeredDependencies),
      ]);

      triggeredDependencies.push("httpbin.org", "jsonplaceholder.typicode.com");

      if (simulateFailure) {
        throw new Error("Simulated downstream processing error from server.");
      }

      const durationMs = Date.now() - startedAt;
      durationHistogram.record(durationMs, { route: "/api/demo", success: true });
      span.setStatus({ code: SpanStatusCode.OK });
      span.addEvent("demo.completed", { dependencyCount: triggeredDependencies.length });

      res.json({
        message: "Server demo completed.",
        dependencyCount: triggeredDependencies.length,
        triggeredDependencies,
        durationMs,
        upstreamSamples: {
          uuid: uuidResponse.data.uuid || null,
          todoTitle: todoResponse.data.title || null,
        },
        storage: storageResult,
        keyVault: secretResult,
      });
    } catch (error) {
      const durationMs = Date.now() - startedAt;
      durationHistogram.record(durationMs, { route: "/api/demo", success: false });
      span.recordException(error);
      span.setStatus({ code: SpanStatusCode.ERROR, message: error.message });

      logger.error("Demo request failed", {
        message: error.message,
        stack: error.stack,
      });

      res.status(500).json({
        message: error.message,
        dependencyCount: triggeredDependencies.length,
        triggeredDependencies,
        durationMs,
      });
    } finally {
      span.end();
    }
  });
});

app.listen(port, () => {
  logger.info("Server listening", { port });
});

async function readStorage(triggeredDependencies) {
  if (!blobServiceClient) {
    return {
      enabled: false,
      reason: "STORAGE_ACCOUNT_URL not configured",
    };
  }

  try {
    const iterator = blobServiceClient.listContainers().byPage({ maxPageSize: 1 });
    const page = await iterator.next();
    const container = page.value.containerItems?.[0]?.name || null;

    triggeredDependencies.push("azure-storage-blob");

    return {
      enabled: true,
      sampleContainer: container,
    };
  } catch (error) {
    logger.warn("Storage dependency call failed", { message: error.message });
    return {
      enabled: true,
      error: error.message,
    };
  }
}

async function readSecret(triggeredDependencies) {
  if (!secretClient) {
    return {
      enabled: false,
      reason: "KEYVAULT_URI not configured",
    };
  }

  try {
    const secret = await secretClient.getSecret(keyVaultSecretName);
    triggeredDependencies.push("azure-key-vault");

    return {
      enabled: true,
      name: secret.name,
      valueLength: secret.value ? secret.value.length : 0,
    };
  } catch (error) {
    logger.warn("Key Vault dependency call failed", { message: error.message });
    return {
      enabled: true,
      error: error.message,
    };
  }
}
