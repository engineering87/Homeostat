// Homeostat dashboard: reads ./status and calls the control endpoints next to it.
// Every value from the server is rendered with textContent: condition names and messages can come from external alerts.
(() => {
  "use strict";
  const $ = (id) => document.getElementById(id);
  let timer = null;

  function el(tag, attrs, ...children) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (key === "class") node.className = value;
      else if (key === "title") node.title = value;
      else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
      else node.setAttribute(key, value);
    }
    for (const child of children) {
      if (child === null || child === undefined) continue;
      node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
  }

  function badge(text, tone) { return el("span", { class: `badge b-${tone}` }, text); }

  function ago(iso) {
    if (!iso) return "";
    const delta = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
    const seconds = Math.abs(delta);
    const span = seconds < 60 ? `${seconds}s` : seconds < 3600 ? `${Math.round(seconds / 60)}m`
      : seconds < 86400 ? `${Math.round(seconds / 3600)}h` : `${Math.round(seconds / 86400)}d`;
    return delta >= 0 ? `${span} ago` : `in ${span}`;
  }

  function time(iso) { return iso ? el("span", { title: new Date(iso).toLocaleString() }, ago(iso)) : ""; }

  function showError(message) {
    const banner = $("banner");
    banner.hidden = !message;
    banner.textContent = message || "";
  }

  async function post(path, button) {
    if (button) button.disabled = true;
    try {
      const response = await fetch(path, { method: "POST", credentials: "same-origin", headers: { "X-Requested-With": "homeostat-dashboard" } });
      if (response.status === 401 || response.status === 403) throw new Error("You are not allowed to do this.");
      if (response.status === 404) throw new Error("Not found: the request may have been answered already, or the control endpoints are not mapped.");
      if (!response.ok) throw new Error(`The server answered ${response.status}.`);
      await refresh();
    } catch (error) {
      showError(error.message);
    } finally {
      if (button) button.disabled = false;
    }
  }

  const tone = { Active: "bad", Pending: "warn", Inactive: "ok" };
  const outcomeTone = { Healed: "ok", ConditionCleared: "ok", DryRun: "info", NotHealed: "bad", Failed: "bad" };

  function renderConditions(conditions) {
    const body = $("conditions");
    body.replaceChildren();
    if (conditions.length === 0) {
      body.append(el("tr", {}, el("td", { colspan: "4", class: "empty" }, "No conditions.")));
      return;
    }
    const order = { Active: 0, Pending: 1, Inactive: 2 };
    for (const c of [...conditions].sort((a, b) => (order[a.status] ?? 3) - (order[b.status] ?? 3) || a.name.localeCompare(b.name))) {
      const status = el("td", {}, badge(c.status, tone[c.status] || "info"));
      if (c.suppressedBy) status.append(" ", el("span", { class: "small" }, `suppressed by ${c.suppressedBy}`));
      if (c.clearingSince) status.append(" ", el("span", { class: "small" }, "recovering"));
      body.append(el("tr", {},
        el("td", { class: "name" }, c.name),
        status,
        el("td", {}, time(c.activeSince || c.pendingSince)),
        el("td", {}, c.lastSeverity ? badge(c.lastSeverity, c.lastSeverity === "Healthy" ? "ok" : c.lastSeverity === "Degraded" ? "warn" : "bad") : "",
          " ", el("span", { class: "small" }, c.lastMessage || ""), " ", el("span", { class: "small" }, ago(c.lastSignalAt)))));
    }
  }

  function renderApprovals(approvals) {
    const box = $("approvals");
    box.replaceChildren();
    if (approvals.length === 0) {
      box.append(el("p", { class: "empty" }, "Nothing is waiting for approval."));
      return;
    }
    for (const a of approvals) {
      const id = encodeURIComponent(a.id);
      box.append(el("div", { class: "approval" },
        el("div", {},
          el("div", { class: "name" }, `${a.conditionName}: ${a.stepName}`),
          el("div", { class: "small" }, a.triggeringSignal?.message || ""),
          el("div", { class: "small" }, "Requested ", time(a.requestedAt), ", expires ", time(a.expiresAt))),
        el("div", { class: "buttons" },
          el("button", { class: "primary", onclick: (e) => post(`approvals/${id}/approve`, e.currentTarget) }, "Approve"),
          el("button", { class: "danger", onclick: (e) => post(`approvals/${id}/reject`, e.currentTarget) }, "Reject"))));
    }
  }

  function renderSwitches(switches) {
    const list = $("switches");
    list.replaceChildren();
    if (switches.length === 0) list.append(el("li", { class: "empty" }, "No switch is on."));
    for (const name of [...switches].sort()) {
      list.append(el("li", {}, el("span", {}, name),
        el("button", { onclick: (e) => post(`switches/${encodeURIComponent(name)}/off`, e.currentTarget) }, "Turn off")));
    }
  }

  function renderBudget(budget, storeError) {
    const box = $("budget");
    box.replaceChildren();
    if (!budget) {
      box.append(el("p", { class: "empty" }, storeError ? "Unavailable: the store cannot be read." : "Unavailable."));
      return;
    }
    const share = budget.limit > 0 ? Math.min(100, Math.round((budget.used / budget.limit) * 100)) : 0;
    const meter = el("div", { class: "meter", role: "meter", "aria-valuemin": "0", "aria-valuemax": String(budget.limit), "aria-valuenow": String(budget.used) }, el("div", {}));
    meter.firstChild.style.width = `${share}%`;
    box.append(el("div", {}, el("strong", {}, `${budget.used} / ${budget.limit}`), " actions used"), meter,
      el("p", { class: "small" }, `${budget.remaining} left in a ${Math.round(budget.windowSeconds / 60)}-minute window`));
  }

  function renderRuns(runs) {
    const list = $("runs");
    list.replaceChildren();
    if (runs.length === 0) list.append(el("li", { class: "empty" }, "No runs yet."));
    for (const r of runs) {
      const steps = el("ol", { class: "steps" });
      for (const s of r.steps) {
        steps.append(el("li", {}, `${s.name}: ${s.succeeded ? "done" : "failed"}`,
          s.verification ? `, ${s.verification}` : "", s.error ? ` (${s.error})` : "",
          s.undoSucceeded === null || s.undoSucceeded === undefined ? "" : `, undo ${s.undoSucceeded ? "done" : "failed"}`));
      }
      list.append(el("li", {},
        el("span", { class: "name" }, r.condition), " ", badge(r.outcome, outcomeTone[r.outcome] || "warn"), " ",
        el("span", { class: "small" }, time(r.completedAt), " on ", r.instance), steps));
    }
  }

  function renderHistory(events) {
    const list = $("history");
    list.replaceChildren();
    if (!events) { list.append(el("li", { class: "empty" }, "Unavailable.")); return; }
    if (events.length === 0) { list.append(el("li", { class: "empty" }, "No condition has changed state yet.")); return; }
    for (const e of events.slice(0, 50)) {
      list.append(el("li", {},
        badge(e.kind === "Activated" ? "Active" : "Cleared", e.kind === "Activated" ? "bad" : "ok"),
        el("span", { class: "name" }, e.condition),
        el("span", { class: "small" }, time(e.at), " on ", e.instance),
        e.message ? el("span", { class: "small" }, e.message) : null));
    }
  }

  function renderCoverage(report) {
    const box = $("coverage");
    box.replaceChildren();
    if (!report) { box.append(el("p", { class: "empty" }, "Unavailable.")); return; }
    const share = Math.round(report.stepCoverage * 100);
    const meter = el("div", { class: "meter", role: "meter", "aria-valuemin": "0", "aria-valuemax": "100", "aria-valuenow": String(share) }, el("div", {}));
    meter.firstChild.style.width = `${share}%`;
    box.append(el("div", {}, el("strong", {}, `${share}%`), " of plan steps completed at least once"), meter);
    if (report.truncated) box.append(el("p", { class: "small" }, "The history does not reach back 24 hours: older activity is not counted."));
    const list = el("ul", { class: "coverage-list" });
    for (const c of report.conditions.filter((x) => x.hasPlan)) {
      const outcomes = Object.entries(c.outcomes).map(([k, v]) => `${k} ${v}`).join(", ");
      const item = el("li", {}, el("span", { class: "name" }, c.name), " ",
        el("span", { class: "small" }, `${c.activations} activations, ${c.runs} runs${outcomes ? ` (${outcomes})` : ""}`));
      if (c.stepsFailed.length > 0) item.append(el("div", { class: "small" }, `Always failed: ${c.stepsFailed.join(", ")}`));
      if (c.stepsNeverRun.length > 0) item.append(el("div", { class: "small" }, `Never ran: ${c.stepsNeverRun.join(", ")}`));
      list.append(item);
    }
    if (list.childElementCount === 0) list.append(el("li", { class: "empty" }, "No conditions with a plan."));
    box.append(list);
  }

  // History and coverage change slowly and cost store reads: refreshed every 30 seconds, not every 5.
  let lastSlowRefresh = 0;

  async function getJson(path) {
    try {
      const response = await fetch(path, { credentials: "same-origin", headers: { Accept: "application/json" } });
      return response.ok ? await response.json() : null;
    } catch {
      return null;
    }
  }

  async function refreshSlow(force) {
    if (!force && Date.now() - lastSlowRefresh < 30000) return;
    lastSlowRefresh = Date.now();
    const [history, coverage] = await Promise.all([getJson("history"), getJson("coverage")]);
    renderHistory(history);
    renderCoverage(coverage);
  }

  function render(status) {
    document.title = `${status.application} · Homeostat`;
    $("app").textContent = status.application;
    $("instance").textContent = `Instance ${status.instance}`;
    const badges = $("badges");
    badges.replaceChildren();
    const active = status.conditions.filter((c) => c.status === "Active").length;
    badges.append(active > 0 ? badge(`${active} active`, "bad") : badge("All clear", "ok"));
    if (status.paused) badges.append(badge(status.pauseReason ? `Paused: ${status.pauseReason}` : "Paused", "warn"));
    if (status.dryRun) badges.append(badge("Dry-run", "info"));
    $("pause").hidden = status.paused;
    $("pause-for").hidden = status.paused;
    $("resume").hidden = !status.paused;
    showError(status.storeError ? `The store is unavailable, so Homeostat does not act: ${status.storeError}` : "");
    renderApprovals(status.pendingApprovals);
    renderConditions(status.conditions);
    renderSwitches(status.switches);
    renderBudget(status.budget, status.storeError);
    renderRuns(status.recentRuns);
    $("updated").textContent = `Updated ${new Date().toLocaleTimeString()}`;
  }

  async function refresh() {
    try {
      const response = await fetch("status", { credentials: "same-origin", headers: { Accept: "application/json" } });
      if (response.status === 401 || response.status === 403) throw new Error("You are not allowed to see the status.");
      if (!response.ok) throw new Error(`The status endpoint answered ${response.status}. Is MapHomeostatStatus mapped with the same prefix?`);
      render(await response.json());
      await refreshSlow(false);
    } catch (error) {
      showError(error.message);
    }
  }

  function schedule() {
    clearInterval(timer);
    if (!document.hidden) timer = setInterval(refresh, 5000);
  }

  $("pause").addEventListener("click", (e) => {
    const duration = $("pause-for").value;
    post(duration ? `pause?for=${encodeURIComponent(duration)}` : "pause", e.currentTarget);
  });
  $("resume").addEventListener("click", (e) => post("resume", e.currentTarget));
  $("switch-form").addEventListener("submit", (e) => {
    e.preventDefault();
    const name = $("switch-name").value.trim();
    if (!name) return;
    $("switch-name").value = "";
    post(`switches/${encodeURIComponent(name)}/on`, e.submitter);
  });
  document.addEventListener("visibilitychange", () => { schedule(); if (!document.hidden) refresh(); });

  refresh();
  schedule();
})();
