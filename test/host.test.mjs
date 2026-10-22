/**
 * The HOST half's tests — the part the pure reducer cannot cover.
 *
 * `lib/state.js` decides which sessions earn a row; the host is what has to copy the ONE fact that
 * never appears in a session event (whether a row is a subagent — it is in the session HEADER), what
 * publishes `state.json` in the contract's schema 3 shape, what decides how a settled row is allowed
 * to go away, and what finds the window binary without naming this machine.
 *
 * This drives `apply()` with a stub context and reads the state FILE it publishes, because the file
 * is the contract between the plugin and the window. No test spawns the real window: the exe seam is
 * either switched off (`spawnWindow: false`) or pointed at a path that does not exist.
 *
 * Run: node test/host.test.mjs      (NOT `node --test test/` — see the harness note in AGENTS.md)
 */

import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";

// HERMETIC ON PURPOSE. The window lookup falls back to an INSTALLED release under
// `$DSH_HOME/state/agents-notch/window/<version>/`, so on a machine where the developer has actually
// installed one — which is the normal state of a machine that ships this — the "no window anywhere"
// tests would find it, spawn it, and fail. Measured: two of them did exactly that the first time a
// release was installed. A test may not depend on the developer's disk: point DSH_HOME at a temp
// directory that has nothing in it, and clear the env override that would win before the lookup.
process.env.DSH_HOME = mkdtempSync(join(tmpdir(), "agents-notch-home-"));
delete process.env.AGENTS_NOTCH_EXE;

import { apply, buildState, contextOf, nextSpawn, quitHold, settingsOf } from "../lib/host.js";
import { exeCandidates, findExe, installedExePath, missingExeFix, publishExePath } from "../lib/exe.js";
import { SETTLED_FALLBACK_MS, applyEvent, createRecord } from "../lib/state.js";

/** A harness context stub: one listing, one route registration, timers we can dispose. */
function stubContext({ entries, events = [], port = 0 }) {
	const disposers = [];
	const routes = [];
	const ctx = {
		on: () => {},
		effect: (fn) => {
			const dispose = fn();
			if (typeof dispose === "function") disposers.push(dispose);
			return dispose;
		},
		get: (name) => {
			if (name === "sessionQuery") {
				return {
					listSessions: async () => entries,
					readTitleSnapshots: async () => new Map(),
					readSession: async () => ({ events }),
				};
			}
			// No port by default: `finishedUrl()` then publishes nothing and no probe is attempted.
			if (name === "webServer") return port > 0 ? { port, protocol: "http:" } : undefined;
			return undefined;
		},
		webServer: { register: (route) => { routes.push(route); return () => {}; } },
	};
	return { ctx, disposers, routes };
}

/** Poll a predicate instead of sleeping for a guessed duration. */
async function until(predicate, { tries = 250, every = 20 } = {}) {
	for (let i = 0; i < tries; i += 1) {
		const answer = await predicate();
		if (answer) return answer;
		await new Promise((resolve) => setTimeout(resolve, every));
	}
	return null;
}

function readState(path) {
	try {
		return JSON.parse(readFileSync(path, "utf8"));
	} catch {
		return null;
	}
}

/** Start `apply()` on a temp state dir and hand back the directory, its disposers and a reader. */
function startHost({ entries = [], events = [], port = 0, config = {} } = {}) {
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-host-"));
	writeFileSync(join(dir, "manual.json"), "{}");
	const statePath = join(dir, "state.json");
	const { ctx, disposers, routes } = stubContext({ entries, events, port });
	apply(ctx, {
		stateDir: dir,
		spawnWindow: false,
		// NEVER fetch in a test: once the manifest names a repository, the auto-download would reach
		// GitHub from a unit test (measured: it did, and three "no window anywhere" tests started
		// failing because the fetch answered instead of the report). Off by default here; the one test
		// that wants the fetch path switches it on and injects nothing.
		autoDownload: false,
		routePath: "/agents-notch/state",
		finishedRoutePath: "/session-finished/api/status",
		manualPath: join(dir, "manual.json"),
		pollMs: 60_000,
		sweepMs: 60_000,
		report: false,
		// The dev build and the published build live under the package root. A checkout that has run
		// `dotnet build` therefore HAS a window binary, and the "nothing found" tests would spawn it
		// instead of reporting its absence (measured). Pin the lookup inside the temp dir.
		exeRoot: dir,
		...config,
	});
	return { dir, statePath, disposers, routes, state: () => readState(statePath) };
}

/** The board `apply()` publishes for one fixed set of entries, then shut down. */
async function boardOf(entries, config = {}) {
	const host = startHost({ entries, config });
	const state = await until(host.state);
	for (const dispose of host.disposers) dispose();
	assert.notEqual(state, null, "the host never published a state file");
	return state;
}

/** A real HTTP server standing in for `plugins/session-finished`. `reply` may change mid-test. */
async function finishedServer(reply = 200) {
	const seen = { status: reply, calls: 0 };
	const server = createServer((req, res) => {
		seen.calls += 1;
		res.writeHead(seen.status, { "content-type": "application/json" });
		res.end(seen.status === 200 ? JSON.stringify({ ok: true, finished: [], unfinished: [], manual: {} }) : JSON.stringify({ ok: false, error: "not mounted" }));
	});
	await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
	return { server, seen, port: server.address().port };
}

/**
 * Drive one registered route handler the way the harness's web server would.
 *
 * The routes are collected by the stub and were previously never called, so every branch of them —
 * 405, unknown key, reset, non-JSON, the clamp echo, and now the failed-write path — was verified by
 * hand only. A handler that throws synchronously rejects the promise instead of hanging the test.
 */
function callRoute(route, { method = "GET", body = "" } = {}) {
	return new Promise((resolve, reject) => {
		const handlers = new Map();
		const req = {
			method,
			setEncoding: () => {},
			on: (name, handler) => { handlers.set(name, handler); },
			destroy: () => {},
		};
		const res = {
			status: 0,
			writeHead(status) { this.status = status; },
			end(text) {
				try {
					resolve({ status: this.status, body: text ? JSON.parse(text) : null });
				} catch (error) {
					reject(error);
				}
			},
		};
		try {
			route.handler(req, res);
		} catch (error) {
			reject(error);
			return;
		}
		if (body !== "") handlers.get("data")?.(body);
		handlers.get("end")?.();
	});
}

test("a subagent's row carries its origin and its parent's id", async () => {
	const state = await boardOf([
		{ live: true, header: { id: "session-lead", cwd: "C:\\work" } },
		{
			live: true,
			header: {
				id: "4bd2f6ab-96d3-4808-a2fc-a1186848cfd9",
				cwd: "C:\\work",
				origin: "subagent",
				parentSession: "session-lead",
				delegationDepth: 1,
			},
		},
	]);

	const byId = new Map(state.sessions.map((row) => [row.id, row]));
	// A session earns no row until something happened to it, so both are absent here — what this
	// asserts is the PAYLOAD's shape, which is where the window reads the edge from.
	assert.equal(state.plugin, "dsh-agents-notch/host");
	assert.equal(state.schema, 3);
	// The denominator is NOT published by default any more: it is `null` unless the row config gives
	// a positive number, because a percentage against a guessed denominator is a lie (contract §1).
	assert.equal(state.context.budget, null);
	assert.equal(state.context.hotAt, 0.5);
	assert.equal(typeof state.sessions, "object");
	assert.equal(byId.size, 0);
});

test("a state.json from apply() is schema 3 — the contract's keys and nothing extra", async () => {
	// Field for field. If a key is added to or removed from the payload without the contract
	// changing, this test is the one that says so.
	const events = [
		{ type: "turn/start", data: { turn: 1 } },
		{ type: "session/title", data: { title: "Argon 0/4 on laptop — fixed" } },
		{ type: "tool/call", data: { name: "roblox_verify" } },
		{ type: "assistant/message", data: { usage: { inputTokens: 228, cacheReadTokens: 99_200, outputTokens: 90, totalTokens: 99_518 } } },
	];
	const entries = [
		{
			live: true,
			header: {
				id: "4bd2f6ab-96d3-4808-a2fc-a1186848cfd9",
				cwd: "C:\\work\\blindKnifeArgon",
				origin: "subagent",
				parentSession: "session-lead",
				delegationDepth: 1,
			},
		},
	];
	const host = startHost({ entries, events, config: { pollMs: 150, sweepMs: 120 } });
	const state = await until(() => {
		const published = host.state();
		return published !== null && published.sessions.length > 0 ? published : null;
	});
	for (const dispose of host.disposers) dispose();
	assert.notEqual(state, null, "the host never published a row");

	// --- the root -------------------------------------------------------------------------------
	assert.deepEqual(
		Object.keys(state).sort(),
		["context", "finishedUrl", "hostPid", "plugin", "retire", "schema", "sessions", "settings", "summary", "wallMs"],
		"the root carries the contract's keys — `finishedUrl` is the ✕'s route, published or null",
	);
	assert.equal(state.schema, 3);
	assert.equal(state.plugin, "dsh-agents-notch/host");
	assert.equal(typeof state.wallMs, "number");
	assert.ok(Math.abs(Date.now() - state.wallMs) < 60_000, "wallMs is the host clock at write time");
	assert.equal(state.hostPid, process.pid);
	assert.equal(state.retire, "route");
	// This stub has no web server, so there is no address to publish — and "not known" is null,
	// never an invented URL (contract #5).
	assert.equal(state.finishedUrl, null);

	// --- context ----------------------------------------------------------------------------------
	assert.deepEqual(Object.keys(state.context).sort(), ["budget", "hotAt"]);
	assert.equal(state.context.budget, null);
	assert.equal(state.context.hotAt, 0.5);

	// --- settings ---------------------------------------------------------------------------------
	assert.deepEqual(Object.keys(state.settings).sort(), ["anchor", "hideFullscreen", "maxRows", "motion", "opacity", "scale", "screen", "showDone", "showProject"]);
	assert.deepEqual(state.settings, { scale: 1, screen: "primary", anchor: "top-center", maxRows: 6, hideFullscreen: true, opacity: 1, motion: "auto", showProject: true, showDone: true });

	// --- summary ----------------------------------------------------------------------------------
	assert.deepEqual(Object.keys(state.summary).sort(), ["attention", "done", "error", "status", "total", "working"]);
	assert.deepEqual(state.summary, { status: "working", working: 1, attention: 0, done: 0, error: 0, total: 1 });

	// --- one row ----------------------------------------------------------------------------------
	const [row] = state.sessions;
	assert.deepEqual(
		Object.keys(row).sort(),
		["activity", "approval", "contextTokens", "cwd", "id", "identity", "origin", "parentSession", "project", "since", "status", "steps", "title"],
	);
	assert.equal(row.id, "4bd2f6ab-96d3-4808-a2fc-a1186848cfd9");
	assert.equal(row.title, "Argon 0/4 on laptop — fixed");
	assert.equal(row.project, "blindKnifeArgon");
	assert.equal(row.cwd, "C:\\work\\blindKnifeArgon");
	assert.equal(row.status, "working");
	assert.equal(typeof row.since, "number");
	assert.deepEqual(row.activity, { kind: "tool", label: "roblox_verify" });
	assert.equal(row.approval, null);
	assert.equal(row.steps, 1);
	assert.ok(["teal", "violet", "coral", "pink", "lime"].includes(row.identity));
	assert.equal(row.origin, "subagent");
	assert.equal(row.parentSession, "session-lead");
	assert.equal(row.contextTokens, 99_518);
});

test("the context budget is null published — and a positive one is published as given", async () => {
	// Rule 1 of the contract. `null` is not "0 tokens" and it is not a plausible 1,000,000: it means
	// the host does not know the route's window, and the window then shows the token count and no bar.
	assert.deepEqual(contextOf({}), { budget: null, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextBudget: null, contextHotAt: null }), { budget: null, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextBudget: 0 }), { budget: null, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextBudget: -5 }), { budget: null, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextBudget: "nonsense" }), { budget: null, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextBudget: 1_000_000 }), { budget: 1_000_000, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextBudget: "250000" }), { budget: 250_000, hotAt: 0.5 });
	// hotAt is the fraction where the bar turns amber; outside (0,1) it is not a fraction, so the
	// default is used rather than a clamped number nobody chose.
	assert.deepEqual(contextOf({ contextHotAt: 0.8 }), { budget: null, hotAt: 0.8 });
	assert.deepEqual(contextOf({ contextHotAt: 1.5 }), { budget: null, hotAt: 0.5 });
	assert.deepEqual(contextOf({ contextHotAt: 0 }), { budget: null, hotAt: 0.5 });

	// Through `apply()`, which is what the window actually reads.
	const withoutBudget = await boardOf([]);
	assert.equal(withoutBudget.context.budget, null);
	const withBudget = await boardOf([], { contextBudget: 250_000, contextHotAt: 0.8 });
	assert.deepEqual(withBudget.context, { budget: 250_000, hotAt: 0.8 });

	// And the pure builder, which a test can drive with records and a config alone.
	const record = createRecord("session-x", 1_000);
	applyEvent(record, { type: "turn/start" }, 1_000);
	const state = buildState({ records: [record], config: { contextBudget: 400_000 }, now: 2_000 });
	assert.equal(state.context.budget, 400_000);
	assert.equal(state.sessions.length, 1);
});

test("settings are clamped to the contract's ranges, and a bad value falls back silently", () => {
	// The window clamps too, but the host must not rely on that: these are the human's settings and
	// a typo in a config file has to cost a default, never an exception.
	assert.deepEqual(settingsOf({}), { scale: 1, screen: "primary", anchor: "top-center", maxRows: 6, hideFullscreen: true, opacity: 1, motion: "auto", showProject: true, showDone: true });
	assert.deepEqual(
		settingsOf({ scale: 5, screen: "nope", anchor: "middle", maxRows: 99, hideFullscreen: "yes" }),
		{ scale: 2, screen: "primary", anchor: "top-center", maxRows: 10, hideFullscreen: true, opacity: 1, motion: "auto", showProject: true, showDone: true },
	);
	assert.deepEqual(
		settingsOf({ scale: 0.1, screen: 0, anchor: "top-left", maxRows: 1, hideFullscreen: false }),
		{ scale: 0.75, screen: "primary", anchor: "top-left", maxRows: 3, hideFullscreen: false, opacity: 1, motion: "auto", showProject: true, showDone: true },
	);
	// In range: passed through, not normalised to the default.
	assert.deepEqual(
		settingsOf({ scale: 1.25, screen: "cursor", anchor: "top-right", maxRows: 4, hideFullscreen: false, opacity: 1, motion: "auto", showProject: true, showDone: true }),
		{ scale: 1.25, screen: "cursor", anchor: "top-right", maxRows: 4, hideFullscreen: false, opacity: 1, motion: "auto", showProject: true, showDone: true },
	);
	// Displays are 1-based, as Windows numbers them; a digit string is a display, "0" is not.
	assert.equal(settingsOf({ screen: "2" }).screen, "2");
	assert.equal(settingsOf({ screen: 2 }).screen, "2");
	assert.equal(settingsOf({ screen: "0" }).screen, "primary");
	assert.equal(settingsOf({ screen: -1 }).screen, "primary");
	// `maxRows` is a row count, so it is a whole number before it is a range.
	assert.equal(settingsOf({ maxRows: 4.6 }).maxRows, 5);
	assert.equal(settingsOf({ maxRows: "7" }).maxRows, 7);
	assert.equal(settingsOf({ maxRows: null }).maxRows, 6);
	// The four appearance knobs, clamped the same way — a typo costs a default, never an exception.
	assert.equal(settingsOf({ opacity: 0.05 }).opacity, 0.35);
	assert.equal(settingsOf({ opacity: 4 }).opacity, 1);
	assert.equal(settingsOf({ opacity: 0.6 }).opacity, 0.6);
	assert.equal(settingsOf({ opacity: "nope" }).opacity, 1);
	assert.equal(settingsOf({ motion: "sometimes" }).motion, "auto");
	assert.equal(settingsOf({ motion: "OFF" }).motion, "off");
	assert.equal(settingsOf({ showProject: "no" }).showProject, true);
	assert.equal(settingsOf({ showDone: false }).showDone, false);
	// A missing/blank value is absent, not zero.
	assert.equal(settingsOf({ scale: "" }).scale, 1);
	assert.equal(settingsOf({ scale: null }).scale, 1);

	// `maxRows` is not decoration: it is the board's cap too, so the published setting and the board
	// can never disagree. Driven through the pure builder — records and a config, no harness.
	const many = [];
	for (let i = 0; i < 12; i += 1) {
		const record = createRecord(`session-${i}`, 1_000);
		applyEvent(record, { type: "turn/start" }, 1_000);
		many.push(record);
	}
	assert.equal(buildState({ records: many, config: {}, now: 2_000 }).sessions.length, 6);
	assert.equal(buildState({ records: many, config: { maxRows: 3 }, now: 2_000 }).sessions.length, 3);
	assert.equal(buildState({ records: many, config: { maxRows: 99 }, now: 2_000 }).sessions.length, 10, "and it is clamped to 10, not trusted");
});

test("the ✕'s route is published when the host knows its port, and null when it does not", async () => {
	// `lib/host-v6.js` published this and a later cleanup dropped it from the payload, while the
	// window kept reading it (desktop/Program.cs, `FinishedUrl`). The symptom was silent: every ✕
	// took the manual.json fallback instead of the route, so `plugins/session-finished` kept its own
	// in-memory copy and the app's sidebar tick could disagree with the overlay.
	assert.equal((await boardOf([])).finishedUrl, null, "no port is `not known`, never an invented URL");

	const host = startHost({ entries: [], port: 45123 });
	const state = await until(host.state);
	for (const dispose of host.disposers) dispose();
	assert.equal(state.finishedUrl, "http://127.0.0.1:45123/session-finished/api/status");
});

test("a settings write that fails answers 500 instead of throwing out of the harness", async () => {
	// The route is the only place a request changes anything, and it used to call `writeStore`
	// bare: on Windows the rename throws while anything holds the target open (the same lock the
	// state publisher retries around), or when the disk is full — inside an `async` HTTP callback,
	// which is an uncaughtException in the harness process. One click in Settings could take the
	// app down.
	const host = startHost({ entries: [] });
	const route = host.routes.find((candidate) => candidate.path === "/agents-notch/settings");
	assert.notEqual(route, undefined, "the settings route was never registered");

	// A directory where the settings file belongs: the tmp file writes, the rename cannot land.
	mkdirSync(join(host.dir, "settings.json"));

	const write = await callRoute(route, { method: "POST", body: JSON.stringify({ opacity: 0.5 }) });
	assert.equal(write.status, 500, "a failed write is not a success");
	assert.equal(write.body.ok, false);
	assert.match(write.body.error, /could not write/, "and it names what failed");

	// Reset goes through the same door, and may not report "ok" for a file it could not clear.
	const reset = await callRoute(route, { method: "POST", body: JSON.stringify({ reset: true }) });
	assert.equal(reset.status, 500);
	assert.equal(reset.body.ok, false);

	// The host is still serving: the failure was contained, not a crash.
	const after = await callRoute(route, { method: "GET" });
	for (const dispose of host.disposers) dispose();
	assert.equal(after.status, 200, "the route still answers after a failed write");
	assert.equal(after.body.settings.opacity, 1, "and nothing was stored");
});

test("the edge reaches the row once the session is working", async () => {
	/** A log the backstop replays: one turn in flight. */
	const events = [{ type: "turn/start", data: { turn: 1 } }];
	const entries = [
		{
			live: true,
			header: {
				id: "4bd2f6ab-96d3-4808-a2fc-a1186848cfd9",
				cwd: "C:\\work",
				origin: "subagent",
				parentSession: "session-lead",
				delegationDepth: 1,
			},
		},
		{ live: true, header: { id: "session-lead", cwd: "C:\\work" } },
	];

	const host = startHost({ entries, events, config: { pollMs: 150, sweepMs: 120 } });
	// The first sweep races the first listing, so it finds nothing to replay; a short sweep interval
	// is what lets the second one do the work inside a test's patience. The TICK is what writes the
	// file — a sweep only fills memory — so the poll interval is short too.
	const state = await until(() => {
		const published = host.state();
		return published?.sessions.some((row) => row.id === "4bd2f6ab-96d3-4808-a2fc-a1186848cfd9") ? published : null;
	});
	for (const dispose of host.disposers) dispose();

	assert.notEqual(state, null, "no row for the working subagent");
	const sub = state.sessions.find((row) => row.id === "4bd2f6ab-96d3-4808-a2fc-a1186848cfd9");
	assert.equal(sub.origin, "subagent");
	assert.equal(sub.parentSession, "session-lead");
	assert.equal(sub.status, "working");
});

test("a session that is working again has its stale retirement cleared", async () => {
	// The bug the human reported: retire a row, use that session again, and its finish is swallowed
	// — `snapshot` drops a settled row that is closed out, so the row leaves the board instead of
	// turning into the tab's `✓ 1`. Working again is not finished, so the mark is cleared through
	// the documented route (the same write the ✕ makes), not edited behind the plugin's back.
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-host-"));
	const manualFile = join(dir, "manual.json");
	writeFileSync(manualFile, JSON.stringify({ "session-retired": "finished" }));

	const posts = [];
	const server = createServer((req, res) => {
		let body = "";
		req.on("data", (chunk) => { body += chunk; });
		req.on("end", () => {
			if (req.method === "POST") {
				posts.push(JSON.parse(body || "{}"));
				writeFileSync(manualFile, "{}");   // the real route persists the clear
			}
			res.writeHead(200, { "content-type": "application/json" });
			res.end(JSON.stringify({ ok: true, finished: [], unfinished: [], manual: {} }));
		});
	});
	await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
	const port = server.address().port;

	const events = [{ type: "turn/start", data: { turn: 1 } }];
	const { ctx, disposers } = stubContext({ entries: [{ live: true, header: { id: "session-retired", cwd: "C:\\work" } }], events, port });

	apply(ctx, {
		stateDir: dir, spawnWindow: false, routePath: "/agents-notch/state",
		finishedRoutePath: "/session-finished/api/status", manualPath: manualFile,
		pollMs: 150, sweepMs: 120, report: false,
	});

	const state = await until(() => {
		const published = readState(join(dir, "state.json"));
		return published?.sessions.some((row) => row.id === "session-retired") ? published : null;
	});
	for (const dispose of disposers) dispose();
	await new Promise((resolve) => server.close(resolve));

	assert.deepEqual(posts, [{ sessionId: "session-retired", state: "none" }]);
	assert.notEqual(state, null, "the row never came back after its stale mark was cleared");
	assert.equal(state.sessions.find((row) => row.id === "session-retired").status, "working");
});

test("retire flips to `timer` after three failed probes, and back after one success", async () => {
	// `plugins/session-finished` is optional on a stranger's machine. When its route does not answer,
	// the host must say so in the payload rather than let the board never empty — and it must stop
	// saying so the moment the route answers again.
	const { server, seen, port } = await finishedServer(404);
	const events = [{ type: "turn/start", data: { turn: 1 } }];
	const host = startHost({
		entries: [{ live: true, header: { id: "session-live", cwd: "C:\\work" } }],
		events,
		port,
		config: { pollMs: 60, sweepMs: 40, finishedPollMs: 10 },
	});

	const asTimer = await until(() => {
		const published = host.state();
		return published?.retire === "timer" ? published : null;
	});
	assert.notEqual(asTimer, null, "three 404s in a row must flip the payload to `timer`");
	assert.ok(seen.calls >= 3, `expected at least 3 probes, saw ${seen.calls}`);

	// One answer puts the board back on the verdict, without a restart.
	seen.status = 200;
	const asRoute = await until(() => {
		const published = host.state();
		return published?.retire === "route" ? published : null;
	});
	for (const dispose of host.disposers) dispose();
	await new Promise((resolve) => server.close(resolve));
	assert.notEqual(asRoute, null, "one successful probe must flip `retire` back to `route`");
});

test("a settled row ages out under `timer` but never under `route`", async () => {
	// The same board, the same settled row, two different verdicts about the close-out route. Under
	// `route` the row is the human's to dismiss, however long it waits; under `timer` it goes after
	// `finishedFallbackMs`, which is the fallback that stops the board filling up.
	const settled = [
		{ type: "turn/start", data: { turn: 1 } },
		// The harness's real shape: an OBJECT reason, not a string.
		{ type: "turn/end", data: { turn: 1, reason: { kind: "completed" } } },
	];
	const entries = [{ live: true, header: { id: "session-settled", cwd: "C:\\work" } }];
	const config = { pollMs: 60, sweepMs: 40, finishedPollMs: 10, finishedFallbackMs: 1 };
	const rowOf = (published) => published?.sessions.find((row) => row.id === "session-settled") ?? null;

	// (a) the route answers: the row stays, and `retire` stays `route`.
	const alive = await finishedServer(200);
	const routeHost = startHost({ entries, events: settled, port: alive.port, config });
	const seenRoute = await until(() => rowOf(routeHost.state()));
	assert.notEqual(seenRoute, null, "a settled row must be on the board");
	assert.equal(seenRoute.status, "done");
	// Long enough for several probes on a route that answers.
	await new Promise((resolve) => setTimeout(resolve, 400));
	const still = routeHost.state();
	assert.equal(still.retire, "route");
	assert.notEqual(rowOf(still), null, "under `route` a settled row must NOT age out");
	for (const dispose of routeHost.disposers) dispose();
	await new Promise((resolve) => alive.server.close(resolve));

	// (b) the route does not answer: after three failures the row ages out.
	const dead = await finishedServer(404);
	const timerHost = startHost({ entries, events: settled, port: dead.port, config });
	const firstSeen = await until(() => rowOf(timerHost.state()));
	assert.notEqual(firstSeen, null, "the row must be on the board before the fallback engages");
	const gone = await until(() => {
		const published = timerHost.state();
		return published?.retire === "timer" && rowOf(published) === null ? published : null;
	});
	for (const dispose of timerHost.disposers) dispose();
	await new Promise((resolve) => dead.server.close(resolve));
	assert.notEqual(gone, null, "under `timer` a settled row older than the fallback must age out");
	assert.equal(gone.summary.total, 0);
});

test("a host with no web server yet does not blame `session-finished` for it", async () => {
	// No port is this host's own not-ready state, not evidence that the close-out plugin is absent.
	// If it counted, the first second of every boot would publish `retire: "timer"` and a settled row
	// older than the fallback would flicker off a board whose verdict is in fact available.
	const settled = [
		{ type: "turn/start", data: { turn: 1 } },
		{ type: "turn/end", data: { turn: 1, reason: { kind: "completed" } } },
	];
	const host = startHost({
		entries: [{ live: true, header: { id: "session-settled", cwd: "C:\\work" } }],
		events: settled,
		port: 0,
		config: { pollMs: 60, sweepMs: 40, finishedPollMs: 10, finishedFallbackMs: 1 },
	});
	await new Promise((resolve) => setTimeout(resolve, 400));   // many ticks, no port
	const state = host.state();
	for (const dispose of host.disposers) dispose();
	assert.equal(state.retire, "route");
	assert.equal(state.sessions.length, 1, "the row must still be there — nothing was retired");
});

test("the window is found in the contract's order, each step on its own", () => {
	const root = "C:\\package";
	const home = "C:\\home\\.dsh";
	const version = "1.2.3";
	const dev = join(root, "desktop", "bin", "Release", "net9.0-windows", "AgentsNotch.exe");
	const published = join(root, "dist", "win-x64", "AgentsNotch.exe");
	const installed = installedExePath({ dshHome: home, version });
	assert.equal(installed, join(home, "state", "agents-notch", "window", "1.2.3", "AgentsNotch.exe"));
	assert.equal(publishExePath(root), published);

	// The order itself, with no filesystem involved.
	assert.deepEqual(
		exeCandidates({ exePath: "C:\\a\\AgentsNotch.exe", env: { AGENTS_NOTCH_EXE: "C:\\b\\AgentsNotch.exe" }, dshHome: home, version, root }),
		[
			{ source: "config", path: "C:\\a\\AgentsNotch.exe" },
			{ source: "env", path: "C:\\b\\AgentsNotch.exe" },
			{ source: "installed", path: installed },
			{ source: "dev", path: dev },
			{ source: "publish", path: published },
		],
	);
	// a. the row config wins over everything.
	assert.equal(findExe({ exePath: "C:\\a.exe", env: { AGENTS_NOTCH_EXE: "C:\\b.exe" }, dshHome: home, version, root, exists: () => true }).path, "C:\\a.exe");
	// b. the environment variable, when the row says nothing.
	assert.equal(findExe({ exePath: "", env: { AGENTS_NOTCH_EXE: "C:\\b.exe" }, dshHome: home, version, root, exists: () => true }).path, "C:\\b.exe");
	// c. the installed release, when both overrides are empty.
	const viaInstalled = findExe({ exePath: "  ", env: {}, dshHome: home, version, root, exists: (path) => path === installed });
	assert.deepEqual(viaInstalled, {
		path: installed,
		source: "installed",
		tried: [
			{ source: "installed", path: installed },
		],
	});
	// d. the developer build, when nothing else is there.
	const viaDev = findExe({ env: {}, dshHome: home, version, root, exists: (path) => path === dev });
	assert.equal(viaDev.path, dev);
	assert.equal(viaDev.source, "dev");
	assert.deepEqual(viaDev.tried.map((candidate) => candidate.source), ["installed", "dev"]);
	// e. the PUBLISHED build (`npm run window:publish` -> dist/win-x64) is found too, on its own.
	const viaPublished = findExe({ env: {}, dshHome: home, version, root, exists: (path) => path === published });
	assert.equal(viaPublished.path, published);
	assert.equal(viaPublished.source, "publish");
	assert.deepEqual(viaPublished.tried.map((candidate) => candidate.source), ["installed", "dev", "publish"]);
	// ...and it LOSES to the installed release when both are there: an installation is the one the
	// installer owns, so the published copy is a developer fallback and never shadows it.
	const both = findExe({ env: {}, dshHome: home, version, root, exists: (path) => path === installed || path === published });
	assert.equal(both.path, installed);
	assert.equal(both.source, "installed");
	// nothing found: the host keeps running and the report names every path it looked at.
	const none = findExe({ exePath: "C:\\a.exe", env: { AGENTS_NOTCH_EXE: "C:\\b.exe" }, dshHome: home, version, root, exists: () => false });
	assert.equal(none.path, null);
	assert.equal(none.source, "");
	assert.equal(none.tried.length, 5);
	const fix = missingExeFix(none);
	assert.ok(fix.includes(installed), "the fix must name the installed path");
	assert.ok(fix.includes("dotnet build desktop/AgentsNotch.csproj -c Release"), "and the build command");
	assert.ok(fix.includes("npm run window:publish"), "and the publish command");
	assert.ok(fix.includes("AGENTS_NOTCH_EXE"), "and both overrides");
	assert.ok(fix.includes("C:\\a.exe") && fix.includes("C:\\b.exe"), "and every path it tried");
	// A filesystem that cannot answer is not a filesystem that says yes.
	assert.equal(findExe({ env: {}, dshHome: home, version, root, exists: () => { throw new Error("no"); } }).path, null);
});

test("a window that keeps dying is retried slowly, never given up on for good", () => {
	// Three quick deaths are retried at 15 s; the fourth used to stop the supervisor until the harness
	// restarted. Measured on this machine: a developer killing the window to render stills tripped it.
	let respawns = 0;
	const delays = [];
	for (let i = 0; i < 6; i++) {
		const next = nextSpawn({ lived: 2_000, respawns });
		respawns = next.respawns;
		delays.push(next.gaveUp ? "slow" : next.delayMs);
	}
	assert.deepEqual(delays, [15_000, 15_000, 15_000, "slow", "slow", "slow"]);
	assert.equal(nextSpawn({ lived: 2_000, respawns: 3 }).delayMs, 5 * 60 * 1000);

	// A window that stayed up a minute was healthy: the count starts again.
	assert.deepEqual(nextSpawn({ lived: 60_000, respawns: 9 }), { respawns: 0, delayMs: 15_000, gaveUp: false });
});

test("a tray-quit suppresses the spawn for an hour, and not after it", async () => {
	// The window writes `quit.json` beside the state file when the human picks *Quit overlay*, and
	// the host must not resurrect it — otherwise the supervisor fights the human once a minute.
	const now = 1_790_000_000_000;
	assert.equal(quitHold({ quitWallMs: now - 60_000, bootedAt: now - 120_000, now }), true, "an hour-old quit, newer than boot, holds");
	assert.equal(quitHold({ quitWallMs: now - 60_000, bootedAt: now - 120_000, now: now + 3_600_001 }), false, "after an hour it no longer holds");
	assert.equal(quitHold({ quitWallMs: now - 200_000, bootedAt: now - 120_000, now }), false, "a quit from before this boot is history");
	assert.equal(quitHold({ quitWallMs: null, bootedAt: 0, now }), false);
	assert.equal(quitHold({}), false);

	// Through `apply()`: the marker actually stops the spawn, and it is logged exactly once.
	// The exe path does not exist, so the DISCRIMINATOR is clean — a host that ignored the marker
	// would report `window-missing` instead.
	//
	// The marker is written a few ms AHEAD of this process's clock, because that is what a real quit
	// looks like from here: the WINDOW wrote it, while this host was already up (the contract's rule
	// is "newer than the host's boot"). Writing the file before `apply()` with a timestamp in the
	// past would test the opposite case — a quit that predates this process, which must NOT hold.
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-quit-"));
	writeFileSync(join(dir, "quit.json"), JSON.stringify({ wallMs: Date.now() + 5_000 }));
	const quiet = startHost({ config: { stateDir: dir, spawnWindow: true, exePath: join(dir, "no-such-window.exe"), report: true } });
	const events = await until(async () => {
		if (!existsSync(join(dir, "host-report.jsonl"))) return null;
		return readFileSync(join(dir, "host-report.jsonl"), "utf8").trim().split("\n").filter(Boolean).map((line) => JSON.parse(line));
	});
	for (const dispose of quiet.disposers) dispose();
	assert.notEqual(events, null, "the host wrote no report at all");
	const names = events.map((event) => event.event);
	assert.equal(names.filter((name) => name === "window-quit").length, 1, `expected one window-quit, saw ${names.join(",")}`);
	assert.equal(names.includes("window-missing"), false, "a quit must be honoured BEFORE the exe is looked for");
	assert.equal(names.includes("window-started"), false);

	// An hour later the same marker is history and the host goes back to looking for the window.
	const stale = mkdtempSync(join(tmpdir(), "agents-notch-quit-old-"));
	writeFileSync(join(stale, "quit.json"), JSON.stringify({ wallMs: Date.now() - 3_700_000 }));
	const awake = startHost({ config: { stateDir: stale, spawnWindow: true, exePath: join(stale, "no-such-window.exe"), report: true } });
	const later = await until(async () => {
		if (!existsSync(join(stale, "host-report.jsonl"))) return null;
		return readFileSync(join(stale, "host-report.jsonl"), "utf8").trim().split("\n").filter(Boolean).map((line) => JSON.parse(line));
	});
	for (const dispose of awake.disposers) dispose();
	const laterNames = (later ?? []).map((event) => event.event);
	assert.equal(laterNames.includes("window-quit"), false, "an hour-old quit must not suppress anything");
	assert.equal(laterNames.includes("window-missing"), true, "and the host must be looking for the window again");
});

test("a missing window is reported once a minute with the exact fix, and the board keeps serving", async () => {
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-missing-"));
	const host = startHost({
		config: { stateDir: dir, spawnWindow: true, exePath: join(dir, "no-such-window.exe"), pollMs: 40, report: true },
	});
	// Several ticks' worth of time: the state file must keep being written while the exe is absent,
	// and the report must not fill up with one line per poll.
	const served = await until(async () => {
		const published = readState(join(dir, "state.json"));
		return published !== null ? published : null;
	});
	await new Promise((resolve) => setTimeout(resolve, 300));
	const again = readState(join(dir, "state.json"));
	for (const dispose of host.disposers) dispose();

	assert.notEqual(served, null, "a host with no window must still serve state");
	assert.ok(again.wallMs > served.wallMs, "and keep refreshing wallMs — the window's liveness rule needs it");
	const lines = readFileSync(join(dir, "host-report.jsonl"), "utf8").trim().split("\n").filter(Boolean).map((line) => JSON.parse(line));
	const missing = lines.filter((line) => line.event === "window-missing");
	assert.equal(missing.length, 1, `expected exactly one window-missing, saw ${missing.length}`);
	assert.ok(typeof missing[0].fix === "string" && missing[0].fix.includes("AgentsNotch.exe"));
	assert.ok(Array.isArray(missing[0].tried));
});

test("`settings.showDone: false` takes finished rows off the board, and the count with them", () => {
	// Only the host computes both the rows and the summary, so the filter belongs here: a filter in
	// the window would let the tab's ✓ count disagree with the rows it is summarising.
	const finished = createRecord("session-finished");
	applyEvent(finished, { type: "turn/end", data: { reason: { kind: "completed" } } });
	const working = createRecord("session-working");
	working.status = "working";            // the reducer sets this from turn/step events

	const all = buildState({ records: [finished, working], config: {} });
	assert.equal(all.sessions.length, 2);
	assert.equal(all.summary.done, 1);

	const shown = buildState({ records: [finished, working], config: { showDone: false } });
	assert.deepEqual(shown.sessions.map((row) => row.id), ["session-working"]);
	assert.equal(shown.summary.done, 0, "the tab's count must agree with the rows it shows");
	assert.equal(shown.summary.total, 1);
	assert.equal(shown.settings.showDone, false);
});

test("with autoDownload off the host never reaches for the network, and still prints the by-hand fix", async () => {
	// The manifest DOES name a repository now, so the only thing standing between a unit test and a
	// 65 MB download is this switch. `startHost` sets it false; this asserts the report says so.
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-no-fetch-"));
	const host = startHost({
		config: { stateDir: dir, spawnWindow: true, exePath: join(dir, "no-such-window.exe"), exeRoot: dir, pollMs: 40, report: true, autoDownload: false },
	});
	const events = await until(async () => {
		if (!existsSync(join(dir, "host-report.jsonl"))) return null;
		const lines = readFileSync(join(dir, "host-report.jsonl"), "utf8").trim().split("\n").filter(Boolean).map((line) => JSON.parse(line));
		return lines.some((event) => event.event === "window-missing") ? lines : null;
	});
	for (const dispose of host.disposers) dispose();
	assert.notEqual(events, null, "the host never got as far as reporting the missing window");
	const names = events.map((event) => event.event);
	assert.equal(names.some((name) => name.startsWith("window-fetch")), false, `no fetch may happen: ${names.join(",")}`);
	const missing = events.find((event) => event.event === "window-missing");
	assert.equal(missing.autoDownload, false);
	assert.match(missing.fix, /install-window\.ps1/);
});