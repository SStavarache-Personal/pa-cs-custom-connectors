const state = {
  connectors: [],
  tests: [],
  currentConnector: null,
  selectedTestId: null,
  swaggerUi: null,
};

const elements = {
  connectorSelect: document.getElementById("connectorSelect"),
  connectorMeta: document.getElementById("connectorMeta"),
  validationList: document.getElementById("validationList"),
  exampleList: document.getElementById("exampleList"),
  exampleSummary: document.getElementById("exampleSummary"),
  exampleRequest: document.getElementById("exampleRequest"),
  exampleExpectation: document.getElementById("exampleExpectation"),
  refreshButton: document.getElementById("refreshButton"),
  runSelectedExampleButton: document.getElementById("runSelectedExampleButton"),
  swaggerStatus: document.getElementById("swaggerStatus"),
  resultBadge: document.getElementById("resultBadge"),
  statusValue: document.getElementById("statusValue"),
  outboundCountValue: document.getElementById("outboundCountValue"),
  networkModeValue: document.getElementById("networkModeValue"),
  responseHeaders: document.getElementById("responseHeaders"),
  responseBody: document.getElementById("responseBody"),
  outboundLog: document.getElementById("outboundLog"),
  failureList: document.getElementById("failureList"),
};

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (!response.ok) {
    throw new Error(`Request failed: ${response.status} ${response.statusText}`);
  }

  return response.json();
}

function formatJson(value) {
  return JSON.stringify(value, null, 2);
}

function setBusy(isBusy) {
  elements.refreshButton.disabled = isBusy;
  elements.runSelectedExampleButton.disabled = isBusy || !state.selectedTestId;
}

function setResultBadge(text, kind) {
  elements.resultBadge.textContent = text;
  elements.resultBadge.className = `status-chip ${kind}`;
}

function setSwaggerStatus(text, kind) {
  elements.swaggerStatus.textContent = text;
  elements.swaggerStatus.className = `status-chip ${kind}`;
}

function renderIssues(issues) {
  elements.validationList.innerHTML = "";
  if (!issues || issues.length === 0) {
    elements.validationList.innerHTML = '<div class="issue">No validation issues detected.</div>';
    return;
  }

  for (const issue of issues) {
    const div = document.createElement("div");
    div.className = `issue ${issue.severity.toLowerCase()}`;
    div.textContent = `[${issue.severity}] ${issue.message}`;
    elements.validationList.appendChild(div);
  }
}

function renderFailures(failures) {
  elements.failureList.innerHTML = "";
  if (!failures || failures.length === 0) {
    return;
  }

  for (const failure of failures) {
    const div = document.createElement("div");
    div.className = "issue error";
    div.textContent = failure;
    elements.failureList.appendChild(div);
  }
}

function getModeLabel(test) {
  if (test.allowLiveNetwork && !test.includeInAutomatedRun) {
    return { text: "Local live", className: "local-live" };
  }

  if (test.includeInAutomatedRun) {
    return { text: "CI safe", className: "ci-safe" };
  }

  return { text: "Local only", className: "local-only" };
}

function renderConnectorMeta(connector) {
  elements.connectorMeta.innerHTML = `
    <div class="meta-line"><strong>${connector.title}</strong></div>
    <div class="meta-line">Original host: ${connector.host ?? "api.example.com"}</div>
    <div class="meta-line">Original base path: ${connector.basePath ?? "/"}</div>
    <div class="meta-line">Operations: ${connector.operations.length}</div>
    <div class="meta-line">Seeded examples: ${connector.tests.length}</div>
    <div class="meta-line">Workbench runtime: ${connector.runtimeBaseUrl}</div>
  `;

  renderIssues(connector.validation.issues);
}

function renderExampleCards() {
  elements.exampleList.innerHTML = "";

  if (state.tests.length === 0) {
    elements.exampleList.innerHTML = '<div class="issue warning">No seeded examples found for this connector yet.</div>';
    return;
  }

  for (const test of state.tests) {
    const mode = getModeLabel(test);
    const article = document.createElement("article");
    article.className = `example-card${test.id === state.selectedTestId ? " active" : ""}`;
    article.innerHTML = `
      <div class="example-title">${test.name}</div>
      <div class="example-meta">
        <span class="pill ${mode.className}">${mode.text}</span>
        <span class="pill local-only">${test.operationId}</span>
      </div>
      <div class="example-description">${test.description ?? "No description provided."}</div>
      <div class="example-actions">
        <button type="button" class="ghost-button small-button" data-action="select" data-test-id="${test.id}">Preview</button>
        <button type="button" class="accent-button small-button" data-action="run" data-test-id="${test.id}">Run</button>
      </div>
    `;
    elements.exampleList.appendChild(article);
  }
}

function renderSelectedExample() {
  const test = state.tests.find(item => item.id === state.selectedTestId) ?? null;
  elements.runSelectedExampleButton.disabled = !test;

  if (!test) {
    elements.exampleSummary.innerHTML = '<div class="meta-line">Select a seeded example to inspect its request and expected outcome.</div>';
    elements.exampleRequest.value = "";
    elements.exampleExpectation.value = "";
    return;
  }

  const mode = getModeLabel(test);
  elements.exampleSummary.innerHTML = `
    <div class="meta-line"><strong>${test.name}</strong></div>
    <div class="meta-line">Operation: ${test.operationId}</div>
    <div class="meta-line">Request: ${test.method} ${test.relativePath}${test.queryString ? `?${test.queryString}` : ""}</div>
    <div class="meta-line">Mode: ${mode.text}</div>
  `;

  elements.exampleRequest.value = formatJson({
    method: test.method,
    relativePath: test.relativePath,
    queryString: test.queryString,
    includeInAutomatedRun: test.includeInAutomatedRun,
    allowLiveNetwork: test.allowLiveNetwork,
    headers: test.headers,
    requestBody: test.requestBody ? JSON.parse(test.requestBody) : null,
    outboundStubs: test.outboundStubs,
  });

  elements.exampleExpectation.value = formatJson({
    statusCode: test.expected.statusCode,
    bodyJson: test.expected.bodyJson ? JSON.parse(test.expected.bodyJson) : null,
    jsonPathEquals: test.expected.jsonPathEquals,
    headerEquals: test.expected.headerEquals,
    bodyTextContains: test.expected.bodyTextContains,
    outboundRequestCount: test.expected.outboundRequestCount,
  });
}

function updateResult(invocation, passed, failures) {
  elements.statusValue.textContent = `${invocation.statusCode} ${invocation.reasonPhrase}`;
  elements.outboundCountValue.textContent = String(invocation.outboundRequests.length);
  elements.networkModeValue.textContent = invocation.usedLiveNetwork ? "Live HTTP" : "Stubbed";
  elements.responseHeaders.value = formatJson(invocation.headers);
  elements.responseBody.value = invocation.bodyText;
  elements.outboundLog.value = formatJson(invocation.outboundRequests);
  renderFailures(failures);

  if (passed === null) {
    setResultBadge("Request completed", "neutral");
  } else if (passed) {
    setResultBadge("Example passed", "success");
  } else {
    setResultBadge("Example failed", "error");
  }
}

function mountSwaggerUi(connector) {
  const swaggerMount = document.getElementById("swagger-ui");
  swaggerMount.innerHTML = "";
  setSwaggerStatus("Loading spec", "neutral");

  state.swaggerUi = SwaggerUIBundle({
    url: connector.specUrl,
    dom_id: "#swagger-ui",
    deepLinking: true,
    displayOperationId: true,
    displayRequestDuration: true,
    defaultModelRendering: "example",
    defaultModelsExpandDepth: 1,
    docExpansion: "list",
    filter: true,
    tryItOutEnabled: true,
    operationsSorter: "alpha",
    tagsSorter: "alpha",
    validatorUrl: null,
    persistAuthorization: true,
    requestSnippetsEnabled: true,
    requestSnippets: {
      generators: {
        curl_bash: { title: "cURL (bash)", syntax: "bash" },
        curl_powershell: { title: "cURL (PowerShell)", syntax: "powershell" },
      },
      defaultExpanded: true,
    },
    presets: [
      SwaggerUIBundle.presets.apis,
      SwaggerUIStandalonePreset,
    ],
    layout: "StandaloneLayout",
    onComplete: () => setSwaggerStatus("Spec ready", "success"),
    onFailure: () => setSwaggerStatus("Spec failed to load", "error"),
  });
}

async function loadConnectorTests(connectorName) {
  state.tests = await fetchJson(`/api/connectors/${encodeURIComponent(connectorName)}/tests`);
  state.selectedTestId = state.tests[0]?.id ?? null;
  renderExampleCards();
  renderSelectedExample();
}

async function onConnectorChanged() {
  const connector = state.connectors.find(item => item.name === elements.connectorSelect.value);
  if (!connector) {
    return;
  }

  state.currentConnector = connector;
  renderConnectorMeta(connector);
  await loadConnectorTests(connector.name);
  mountSwaggerUi(connector);
}

async function refreshConnectors() {
  setBusy(true);
  try {
    state.connectors = await fetchJson("/api/connectors");
    elements.connectorSelect.innerHTML = "";

    for (const connector of state.connectors) {
      const option = document.createElement("option");
      option.value = connector.name;
      option.textContent = connector.name;
      elements.connectorSelect.appendChild(option);
    }

    await onConnectorChanged();
  } finally {
    setBusy(false);
  }
}

async function runExample(testId) {
  if (!state.currentConnector || !testId) {
    return;
  }

  setBusy(true);
  try {
    setResultBadge("Running example", "neutral");
    renderFailures([]);

    const result = await fetchJson(`/api/connectors/${encodeURIComponent(state.currentConnector.name)}/tests/${encodeURIComponent(testId)}/run`, {
      method: "POST",
    });

    updateResult(result.invocation, result.passed, result.failures ?? []);
  } catch (error) {
    setResultBadge("Example failed", "error");
    renderFailures([error.message]);
  } finally {
    setBusy(false);
  }
}

elements.connectorSelect.addEventListener("change", onConnectorChanged);
elements.refreshButton.addEventListener("click", refreshConnectors);
elements.runSelectedExampleButton.addEventListener("click", () => runExample(state.selectedTestId));
elements.exampleList.addEventListener("click", event => {
  const button = event.target.closest("button[data-action]");
  if (!button) {
    return;
  }

  const testId = button.dataset.testId;
  if (!testId) {
    return;
  }

  state.selectedTestId = testId;
  renderExampleCards();
  renderSelectedExample();

  if (button.dataset.action === "run") {
    runExample(testId);
  }
});

refreshConnectors().catch(error => {
  setSwaggerStatus("Initialization failed", "error");
  setResultBadge("Initialization failed", "error");
  renderFailures([error.message]);
});