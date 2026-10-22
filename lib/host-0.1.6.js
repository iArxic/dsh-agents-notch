/**
 * Agents Notch — the host half of the always-on-top notch.
 *
 * It owns the FACTS and nothing else: it watches the session feed, keeps one small record per
 * session, and writes the board's state to a file a separate .NET window polls. The window is a
 * separate process because a DSH plugin cannot create one (the host runs with
 * `ELECTRON_RUN_AS_NODE`, and the Electron shell has no topmost surface), so this plugin spawns and
 * supervises it.
 *
 * `docs/CONTRACT.md` (schema 3) is the whole interface: exactly the keys it lists, no more. The
 * state FILE is the contract, not the HTTP route — the harness server binds an ephemeral port
 * (`--port 0`), so the window would have to discover the port from a file anyway; the route is
 * there for debugging and for a client half to read.
 *
 * WHICH SESSIONS EARN A ROW is `./state.js`'s job, and it is pure. This file adds the things that
 * need a live harness:
 *
 *   1. THE FEED. `session/event` with `{ global: true }` — without the option the listener only
 *      hears sessions entered through its own context, and the symptom is silence, not an error.
 *   2. THE TICK. A finished or failed session keeps its row until the human closes it out, and the
 *      thing that knows that is `plugins/session-finished` — the sidebar's ✓. Its verdict is asked
 *      for over HTTP on the port this app is already serving (`ctx.webServer.port`). `manual.json`
 *      is read as well and the human's manual mark wins in both directions.
 *   3. THE FALLBACK. That plugin is optional on a stranger's machine. With it absent, the verdict is
 *      never answered, settled rows never retire and the board fills up with history until the
 *      agents that are working fall off it. So the failures are counted: three in a row with no
 *      success and the published `retire` becomes `"timer"`, and settled rows older than
 *      `finishedFallbackMs` (15 min) are dropped as if closed out. One success puts it back to
 *      `"route"` and resets the counter. The window says so in its tooltip — the board never
 *      silently pretends a timer is the human's answer.
 *   4. LATENCY. An event wakes the writer immediately (coalesced, 120 ms); the timer is only the
 *      heartbeat (`wallMs`) that proves the host is alive. The window compares it to its own clock:
 *      more than ~4 s stale means the host is gone, and the tab must say so rather than show the
 *      last happy frame.
 *
 * What this half must NOT do, all of it measured the hard way (`docs/INTERNALS.md` §9):
 *
 *   - Default `context.budget` to a plausible number. The route's window is a property of the model
 *     route, not of a session, and a percentage against a guessed denominator is a lie that looks
 *     like information. No budget ⇒ `null` ⇒ the window shows the token count and no bar.
 *   - Let a settled row expire on a timer while the close-out verdict is AVAILABLE.
 *   - Resurrect a window the human dismissed from the tray (`quit.json`), which is why the marker
 *     is read before every spawn.
 *   - Name this machine in any path. The window is found relative to the package (see `./exe.js`).
 *
 * @module dsh-agents-notch/host
 */

import { appendFileSync, existsSync, mkdirSync, readFileSync, renameSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { spawn } from "node:child_process";

import { STATUS, applyEvent, createRecord, isBoilerplate, projectOf, retiredRows, snapshot, summarise } from "./state.js";
import { PACKAGE_ROOT, defaultStateDir, dshHome, findExe, installedExePath, missingExeFix, packageRepository, packageVersion } from "./exe.js";
import { downloadExe, releaseAssetUrl } from "./fetch-exe.js";
import {
	ANCHORS as SETTINGS_ANCHORS,
	DEFAULTS,
	MOTIONS as SETTINGS_MOTIONS,
	SETTINGS_KEYS,
	clampSettings,
	clearStore,
	mergeSettings,
	readStore,
	settingsFileIn,
	writeStore,
} from "./settings-store.js";

/** Stable Loader identity. Do NOT add an `export default`: the Loader prefers `exports.default`. */
export const name = "agents-notch";

/** `sessionQuery` reads the sessions and their events; `webServer` serves the state for debugging. */
export const inject = ["sessionQuery", "webServer"];

/** The payload's identity. Bumped only with the contract. */
const PLUGIN = "dsh-agents-notch/host";
const SCHEMA = 3;

/** One hour: how long a tray-quit keeps this host from resurrecting the window. */
const QUIT_HOLD_MS = 60 * 60 * 1000;

/** A missing exe is usually a rebuild in progress, so retry often but say so at most once a minute. */
const MISSING_RETRY_MS = 15 * 1000;
const MISSING_REPORT_MS = 60 * 1000;

/** The contract's supervision policy: respawn after 15 s; after 3 quick deaths, see `nextSpawn`. */
const RESPAWN_MS = 15 * 1000;
const MAX_RESPAWNS = 3;
/** A window that stayed up this long was healthy; the next death starts the count again. */
const HEALTHY_MS = 60 * 1000;
/** After the quick tries run out, keep trying this slowly rather than never again. */
const GIVEUP_RETRY_MS = 5 * 60 * 1000;

/** Consecutive failed close-out probes before the board admits the verdict is not coming. */
const RETIRE_FAILURES = 3;

/** A config number, or the fallback when it is absent or not a number. `null` and `""` are absent. */
function numberOr(value, fallback) {
	if (value === null || value === undefined || value === "") return fallback;
	const number = Number(value);
	return Number.isFinite(number) ? number : fallback;
}

/**
 * `context` — the denominator the window turns a token count into a percentage with.
 *
 * `budget` is `null` unless the row config gives a POSITIVE number. It deliberately has no default:
 * §1 of the contract, and the README says the same. `hotAt` is the fraction where the bar turns
 * amber; outside the open interval (0,1) it falls back to 0.5 rather than being clamped to a number
 * nobody chose.
 *
 * @param {Record<string, any>} [config]
 * @returns {{ budget: number | null, hotAt: number }}
 */
export function contextOf(config = {}) {
	const budget = numberOr(config.contextBudget, Number.NaN);
	const hotAt = numberOr(config.contextHotAt, 0.5);
	return {
		budget: Number.isFinite(budget) && budget > 0 ? budget : null,
		hotAt: Number.isFinite(hotAt) && hotAt > 0 && hotAt < 1 ? hotAt : 0.5,
	};
}

/** The three anchors, in the contract's order — the first is the default. */
// ANCHORS/MOTIONS/screenOf/motionOf and the clamps live in settings-store.js: the Settings pane writes
// through the same rules as a hand-edited row, so the two doors cannot drift apart.
const ANCHORS = SETTINGS_ANCHORS;

/** `motion`: follow Windows (`auto`), animate (`on`), or draw the settled pose and nothing else. */
const MOTIONS = SETTINGS_MOTIONS;



/**
 * `settings` — the human's, from the plugin row's config. The window applies them and remembers
 * nothing on disk (contract, rule 4). An invalid value falls back to the default SILENTLY: a typo in
 * a config file must not take a plugin down.
 *
 * @param {Record<string, any>} [config]
 * @returns {{ scale: number, screen: string, anchor: string, maxRows: number, hideFullscreen: boolean,
 *             opacity: number, motion: string, showProject: boolean, showDone: boolean }}
 */
export function settingsOf(config = {}) {
	return clampSettings(config, DEFAULTS);
}

/**
 * How long a settled row may sit on the board under `retire: "timer"`. The host uses it to build
 * the retired set; the window never does.
 *
 * @param {Record<string, any>} [config]
 * @returns {number}
 */
export function fallbackMsOf(config = {}) {
	const ms = numberOr(config.finishedFallbackMs, 900_000);
	return ms >= 0 ? ms : 900_000;
}

/**
 * Does a tray-quit still forbid spawning the window?
 *
 * The marker is `{"wallMs": <ms>}` written by the window when the human picks *Quit overlay*. Two
 * clauses, both needed: it must be NEWER than this host's boot (a quit from before this process
 * started is history — restarting the app is how you get the notch back), and less than an hour old
 * (after that the human's dismissal has expired). A marker absurdly in the future is ignored too:
 * that is a clock that jumped, not a quit.
 *
 * @param {{ quitWallMs?: unknown, bootedAt?: number, now?: number, holdMs?: number }} [options]
 * @returns {boolean}
 */
export function quitHold({ quitWallMs, bootedAt = 0, now = Date.now(), holdMs = QUIT_HOLD_MS } = {}) {
	const wallMs = numberOr(quitWallMs, Number.NaN);
	if (!Number.isFinite(wallMs) || wallMs <= 0) return false;
	if (wallMs <= Number(bootedAt)) return false;
	if (wallMs > now + holdMs) return false;
	return now - wallMs < holdMs;
}

/**
 * What the supervisor does after the window dies: how long to wait, and the new count of quick deaths.
 *
 * A window that lived past `HEALTHY_MS` resets the count. Past `MAX_RESPAWNS` quick deaths the retry
 * slows to `GIVEUP_RETRY_MS` instead of stopping: a permanent give-up left the notch dead until the
 * harness restarted, and the quick deaths that tripped it on this machine were a developer killing the
 * window to render stills — nothing wrong with the window at all.
 *
 * @param {{ lived: number, respawns: number }} options
 * @returns {{ respawns: number, delayMs: number, gaveUp: boolean }}
 */
export function nextSpawn({ lived, respawns }) {
	if (lived >= HEALTHY_MS) return { respawns: 0, delayMs: RESPAWN_MS, gaveUp: false };
	const count = respawns + 1;
	if (count > MAX_RESPAWNS) return { respawns: count, delayMs: GIVEUP_RETRY_MS, gaveUp: true };
	return { respawns: count, delayMs: RESPAWN_MS, gaveUp: false };
}

/**
 * The payload the window polls — schema 3, exactly `docs/CONTRACT.md`'s keys and nothing extra.
 *
 * Pure on purpose: a test can hand it records and a config and read the board without a harness, a
 * clock, a filesystem or a child process. `apply()` calls it with its live records; nothing else
 * about the shape lives anywhere.
 *
 * @param {{ records?: Iterable<Record<string, any>>, finished?: Set<string>, retire?: "route" | "timer",
 *           config?: Record<string, any>, now?: number, hostPid?: number,
 *           finishedUrl?: string | null }} [options]
 * @returns {Record<string, any>}
 */
export function buildState({ records = [], finished = new Set(), retire = "route", config = {}, pane = {}, now = Date.now(), hostPid = process.pid, finishedUrl = null } = {}) {
	// Materialise once: `records` may be a Map's LIVE iterator, and it is walked twice.
	const list = Array.isArray(records) ? records : [...records];
	// The pane's layer is clamped ON TOP of the row's, so a click in Settings beats the YAML and
	// nothing else changes.
	const settings = mergeSettings(settingsOf(config), pane);
	const context = contextOf(config);
	const mode = retire === "timer" ? "timer" : "route";
	const retired = retiredRows(list, {
		finished,
		retire: mode,
		now,
		fallbackMs: fallbackMsOf(config),
	});
	const rows = snapshot(settings.showDone ? list : list.filter((record) => record.status !== STATUS.DONE), {
		maxRows: settings.maxRows,
		finished: retired,
	});
	return {
		schema: SCHEMA,
		plugin: PLUGIN,
		wallMs: now,
		hostPid,
		retire: mode,
		// The close-out route's absolute URL, or null while this host has no port yet. The window
		// POSTs here when the human presses the ✕, so the mark goes through the route's own validated
		// write and `plugins/session-finished` agrees immediately. Null means "not known here" — never
		// an invented address — and the window then falls back to writing the same mark into the
		// human's own `manual.json`.
		finishedUrl,
		context,
		settings,
		summary: summarise(rows),
		sessions: rows,
	};
}

/**
 * The host half.
 *
 * @param {import("cordis").Context} ctx
 * @param {{ stateDir?: string, routePath?: string, exePath?: string, spawnWindow?: boolean,
 *           pollMs?: number, sweepMs?: number, scale?: number, screen?: string, anchor?: string,
 *           maxRows?: number, hideFullscreen?: boolean,
 *           finishedRoutePath?: string, finishedPollMs?: number, finishedFallbackMs?: number,
 *           manualPath?: string, report?: boolean,
 *           contextBudget?: number | null, contextHotAt?: number }} [config]
 */
export function apply(ctx, config = {}) {
	const bootedAt = Date.now();

	const stateDir = typeof config.stateDir === "string" && config.stateDir.trim() !== "" ? config.stateDir : defaultStateDir(process.env);
	const statePath = join(stateDir, "state.json");
	// The Settings pane's own layer. Beside state.json on purpose: it is machine state a click wrote,
	// not configuration a human maintains.
	const panePath = settingsFileIn(stateDir);
	let paneCache = { at: -1, settings: {} };
	/**
	 * The pane's layer, re-read when the file changes. Cached by mtime + size: a tick runs twice a
	 * second and re-parsing a tiny file is cheap, but doing it twice a second for hours is not nothing.
	 */
	function paneOf() {
		try {
			const info = statSync(panePath);
			if (info.mtimeMs !== paneCache.at || info.size !== paneCache.size) {
				paneCache = { at: info.mtimeMs, size: info.size, settings: readStore(panePath) };
			}
			return paneCache.settings;
		} catch {
			paneCache = { at: -1, settings: {} };
			return paneCache.settings;
		}
	}
	/** What the window will actually use: the row's settings with the pane's layer on top. */
	function currentSettings() {
		return mergeSettings(settingsOf(config), paneOf());
	}
	const reportPath = join(stateDir, "host-report.jsonl");
	const quitPath = join(stateDir, "quit.json");
	const routePath = config.routePath ?? "/agents-notch/state";
	// The Settings pane's read/write door. A separate path from the state route: one is a read-only
	// publication, the other is the only place in this plugin where a POST changes anything.
	const settingsPath = config.settingsPath ?? "/agents-notch/settings";
	const spawnWindow = config.spawnWindow !== false;
	const pollMs = config.pollMs ?? 340;
	const sweepMs = config.sweepMs ?? 10000;
	const settings = settingsOf(config);
	const fallbackMs = fallbackMsOf(config);

	/** Where `plugins/session-finished` answers "is this session closed out?" */
	const finishedRoutePath = config.finishedRoutePath ?? "/session-finished/api/status";
	/** How often that answer is re-asked, while there is something settled to ask about. */
	const finishedPollMs = config.finishedPollMs ?? 1000;
	/** The human's own marks; also the fallback when the other plugin is not mounted. */
	const manualPath = config.manualPath ?? join(dshHome(process.env), "state", "session-finished", "manual.json");

	/**
	 * The absolute URL the window POSTs to when the human removes a row.
	 *
	 * The window is a separate process with no `ctx`, so it cannot discover the port itself. The
	 * tick route is the only WRITE in this feature and it lives on the host's own web server, so
	 * the host publishes its own address rather than letting the window guess a port, read another
	 * plugin's file, or keep a second "dismissed" state that could disagree.
	 */
	function finishedUrl() {
		const server = ctx.get("webServer");
		const port = Number(server?.port);
		if (!Number.isInteger(port) || port <= 0) return null;
		const protocol = typeof server?.protocol === "string" && server.protocol !== "" ? server.protocol : "http:";
		return `${protocol}//127.0.0.1:${port}${finishedRoutePath}`;
	}

	/** sessionId -> record. A session only appears here once something happened to it. */
	const records = new Map();
	/** sessionId -> ms of the last backstop replay, so the log is not re-read every sweep. */
	const sweptAt = new Map();
	/** Ids whose stale retirement mark has already been handed to `session-finished`. */
	const askedToClear = new Set();
	/** The ids the last listing reported as attached. */
	let attached = new Set();
	/** The ids `session-finished` says are closed out. A positive answer is kept until one says otherwise. */
	const closedOut = new Set();
	/** The human's marks, re-read on every build: the tick must not wait for a network round trip. */
	let manual = new Map();
	let askedKey = "";
	let askedAt = 0;
	let child = null;
	let childStartedAt = 0;
	let respawns = 0;
	let spawnTimer = null;
	let quitLoggedFor = null;
	let lastMissingAt = 0;
	/** One fetch attempt per process: a missing release must not become a download loop. */
	let downloadTried = false;
	let stopped = false;
	let writeTimer = null;
	let lastWriteAt = 0;

	/**
	 * How the close-out verdict is arriving — the published `retire`.
	 *
	 * `failures` counts CONSECUTIVE probes that did not produce an answer (no port, a non-2xx, a
	 * timeout, a body that is not JSON). Three in a row with no success since the last one and the
	 * board stops waiting: `retire` becomes `"timer"` and settled rows age out. One success puts it
	 * back and resets the count, in whichever direction it was.
	 */
	let finishedFailures = 0;
	let reportedRetire = "route";

	function retireMode() {
		return finishedFailures >= RETIRE_FAILURES ? "timer" : "route";
	}

	function report(event, extra = {}) {
		if (config.report === false) return;
		try {
			mkdirSync(stateDir, { recursive: true });
			appendFileSync(reportPath, `${JSON.stringify({ at: new Date().toISOString(), event, ...extra })}\n`);
		} catch {
			// A report that cannot be written must not take the plugin down.
		}
	}

	function idOf(session) {
		const id = session?.id ?? session?.header?.id;
		return typeof id === "string" && id !== "" ? id : null;
	}

	// The live feed. `{ global: true }` is load-bearing: `session/event` is scope-filtered, and
	// without it a listener only hears the sessions entered through its own context.
	ctx.on(
		"session/event",
		(session, event) => {
			const id = idOf(session);
			if (id === null) return;
			let record = records.get(id);
			if (record === undefined) {
				record = createRecord(id);
				records.set(id, record);
			}
			applyEvent(record, event);
			// The turn just ended, or a question just arrived: the board is stale by definition.
			scheduleWrite();
		},
		{ global: true }
	);

	/** The human's own marks: `{ "<sessionId>": "finished" | "unfinished" }`. */
	function readManual() {
		try {
			if (!existsSync(manualPath)) return new Map();
			const parsed = JSON.parse(readFileSync(manualPath, "utf8"));
			return parsed !== null && typeof parsed === "object" ? new Map(Object.entries(parsed)) : new Map();
		} catch {
			// A torn file is "no marks", never "everything is finished".
			return new Map();
		}
	}

	/** A probe that produced no answer. The transition into `"timer"` is reported exactly once. */
	function noteProbeFailure(reason) {
		const was = finishedFailures;
		finishedFailures += 1;
		if (was === 0) report("finished-unavailable", { reason });
		if (finishedFailures >= RETIRE_FAILURES && reportedRetire !== "timer") {
			reportedRetire = "timer";
			report("retire-fallback", { failures: finishedFailures, reason, fallbackMs });
		}
	}

	/** An answer. It puts the board back on the verdict and zeroes the count — in either direction. */
	function noteProbeSuccess() {
		const wasTimer = reportedRetire === "timer";
		finishedFailures = 0;
		reportedRetire = "route";
		if (wasTimer) report("retire-route", {});
	}

	/**
	 * Ask `plugins/session-finished` which of these sessions the human (or a close-out command) has
	 * finished. The route is the documented contract; `ctx.webServer.port` is the port this host is
	 * already serving on, so there is nothing to discover and no file to guess at.
	 *
	 * A failed probe keeps the answer already held (and `manual.json` still carries the human's
	 * ticks), so a missing plugin degrades to "nothing auto-clears" — for three probes, and then to
	 * the labelled timer fallback rather than to a board that never empties.
	 */
	async function refreshClosedOut(ids) {
		if (ids.length === 0) return;
		const key = ids.slice().sort().join(",");
		const now = Date.now();
		if (key === askedKey && now - askedAt < finishedPollMs) return;
		askedKey = key;
		askedAt = now;

		const base = finishedUrl();
		if (base === null) {
			// No port yet is THIS host not being ready, not the other plugin being absent. Counting it
			// would publish `retire: "timer"` for the first second of a boot and make a settled row
			// flicker off the board, so it is neither a success nor a failure.
			return;
		}
		const url = `${base}?ids=${encodeURIComponent(key)}`;
		let response;
		try {
			response = await fetch(url, { signal: AbortSignal.timeout(2000) });
		} catch (error) {
			const reason = String(error?.message ?? error).slice(0, 200);
			report("finished-failed", { reason });
			noteProbeFailure(reason);
			return;
		}
		if (!response.ok) {
			noteProbeFailure(`http-${response.status}`);
			return;
		}
		let body;
		try {
			body = await response.json();
		} catch (error) {
			const reason = String(error?.message ?? error).slice(0, 200);
			report("finished-failed", { reason });
			noteProbeFailure(reason);
			return;
		}
		noteProbeSuccess();
		const wanted = new Set(ids);
		const finished = Array.isArray(body?.finished) ? body.finished : [];
		const unfinished = Array.isArray(body?.unfinished) ? body.unfinished : [];
		for (const id of finished) if (wanted.has(id)) closedOut.add(id);
		// A negative is a statement about the log this time, not a permanent fact.
		for (const id of unfinished) if (wanted.has(id)) closedOut.delete(id);
		// The human's own marks beat the detector, so an explicit "unfinished" clears ours too.
		for (const [id, mark] of Object.entries(body?.manual ?? {})) {
			if (mark === "unfinished") closedOut.delete(id);
		}
	}

	/** Is this session closed out — by the human's tick, or by a close-out command in its log? */
	function isClosedOut(id) {
		const mark = manual.get(id);
		if (mark === "finished") return true;
		if (mark === "unfinished") return false;
		return closedOut.has(id);
	}

	/**
	 * The harness's own titles, if it has them. The shape of this API is not documented on this
	 * machine, so it is probed rather than assumed: a Map, a plain object and an array of records
	 * are all accepted, and a missing method is simply not used.
	 */
	async function harnessTitles(ids) {
		const query = ctx.get("sessionQuery");
		if (query === undefined || typeof query.readTitleSnapshots !== "function") return new Map();
		try {
			const raw = await query.readTitleSnapshots(ids);
			const titles = new Map();
			if (raw instanceof Map) {
				for (const [id, title] of raw) if (typeof title === "string") titles.set(id, title);
			} else if (Array.isArray(raw)) {
				for (const entry of raw) {
					const id = entry?.id ?? entry?.sessionId;
					const title = entry?.title;
					if (typeof id === "string" && typeof title === "string") titles.set(id, title);
				}
			} else if (raw !== null && typeof raw === "object") {
				for (const [id, title] of Object.entries(raw)) if (typeof title === "string") titles.set(id, title);
			}
			return titles;
		} catch (error) {
			report("titles-failed", { reason: String(error?.message ?? error).slice(0, 200) });
			return new Map();
		}
	}

	/**
	 * The backstop: a session that was already running when this plugin mounted has no live events
	 * in this process, so its log is replayed once to recover its title and its state.
	 */
	async function sweep() {
		const query = ctx.get("sessionQuery");
		if (query === undefined) return;
		const ids = [...attached];
		const titles = await harnessTitles(ids);
		for (const id of ids) {
			const record = records.get(id);
			const last = sweptAt.get(id) ?? 0;
			const needs = record === undefined || (record.title === "" && Date.now() - last > sweepMs);
			if (!needs) continue;
			sweptAt.set(id, Date.now());
			try {
				const snapshotOfSession = await query.readSession(id);
				const events = Array.isArray(snapshotOfSession?.events) ? snapshotOfSession.events : [];
				const fresh = createRecord(id);
				for (const event of events) applyEvent(fresh, event);
				const known = records.get(id);
				if (known === undefined) records.set(id, fresh);
				else {
					// The log is the older truth, but a live event may be newer: keep the newer status.
					if (known.lastEventAt <= fresh.lastEventAt) {
						known.title = known.title || fresh.title;
						if (known.status === STATUS.IDLE) {
							known.status = fresh.status;
							known.since = fresh.since;
							known.activity = fresh.activity;
							known.approval = fresh.approval;
							// The step count and the context size are properties of the turn the log
							// replayed, and an IDLE record has none of its own to protect — dropping
							// them here published `steps: 0` / `contextTokens: 0` for a session that
							// had demonstrably answered (caught by the schema-3 contract test).
							known.steps = fresh.steps;
							known.contextTokens = fresh.contextTokens;
						}
					}
				}
				// The harness's own title fills an empty one — a title from a `session/title` event
				// or from the human's own first line always wins over it.
				const target = records.get(id);
				const harnessTitle = titles.get(id);
				if (target !== undefined && target.title === "" && typeof harnessTitle === "string" && harnessTitle.trim() !== "" && !isBoilerplate(harnessTitle)) {
					target.title = harnessTitle.trim();
				}
			} catch (error) {
				report("sweep-failed", { sessionId: id, reason: String(error?.message ?? error).slice(0, 200) });
			}
		}
	}

	/** The attached sessions, with their working directories and who delegated them. */
	async function refreshAttached() {
		const query = ctx.get("sessionQuery");
		if (query === undefined) return;
		try {
			const list = await query.listSessions();
			const next = new Set();
			for (const entry of list) {
				const id = entry?.header?.id;
				if (typeof id !== "string" || id === "") continue;
				if (entry.live !== true) continue;
				next.add(id);
				let record = records.get(id);
				if (record === undefined) {
					record = createRecord(id);
					records.set(id, record);
				}
				const cwd = entry?.header?.cwd;
				if (typeof cwd === "string" && cwd !== "" && record.cwd !== cwd) {
					record.cwd = cwd;
					record.project = projectOf(cwd);
				}
				// Delegation lives in the session HEADER and nowhere else: no session event carries
				// it. Measured on two real logs (2026-10-21) — a subagent has
				// `origin: "subagent"` and `parentSession: "<id>"`, while a session opened in the app
				// has neither key at all, only `delegationDepth: 0`. So an empty string is the
				// honest answer for "top-level", not a field that failed to arrive.
				const origin = entry?.header?.origin;
				const parent = entry?.header?.parentSession;
				if (typeof origin === "string" && record.origin !== origin) record.origin = origin;
				if (typeof parent === "string" && record.parentSession !== parent) record.parentSession = parent;
			}
			attached = next;
			// Drop the records of sessions that are gone and had nothing to show. A settled row is
			// NOT dropped here: it is waiting for the human to close it out, and that is the point.
			const now = Date.now();
			for (const [id, record] of records) {
				if (next.has(id)) continue;
				if (record.status === STATUS.IDLE && now - record.lastEventAt > 60000) records.delete(id);
			}
		} catch (error) {
			// One unreadable log fails the listing for the whole corpus. Report it and keep the
			// previous answer: "I do not know" must not read as "nothing is running".
			report("list-failed", { reason: String(error?.message ?? error).slice(0, 200) });
		}
	}

	/** The payload, from the live records. `buildState` above is the pure half that shapes it. */
	function currentState() {
		manual = readManual();
		const closed = new Set();
		for (const id of records.keys()) if (isClosedOut(id)) closed.add(id);
		return buildState({
			records: records.values(),
			finished: closed,
			retire: retireMode(),
			config,
			pane: paneOf(),
			now: Date.now(),
			hostPid: process.pid,
			// The window has no `ctx` and no way to discover the port, so the address it needs is
			// published. Null until the web server is up; the window then falls back to the file.
			finishedUrl: finishedUrl(),
		});
	}

	/** Block for a few ms. Only ever used inside the retry below, never on a hot path. */
	function sleepSync(ms) {
		Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
	}

	/**
	 * Publish the board atomically.
	 *
	 * The rename is what makes the window never read half a file, and on Windows it FAILS while
	 * anything else holds the target open — measured as `EPERM` every ~30 s, with the window polling
	 * every 150 ms and again, more rarely, after the window was fixed to share deletes. So the lock
	 * is intermittent and partly external (an indexer or a sync client), not something this process
	 * can avoid. A failed publish is one stale frame — the next tick rewrites it — but retrying a few
	 * times stops a healthy board from reporting itself broken.
	 */
	function writeState(state) {
		try {
			mkdirSync(stateDir, { recursive: true });
			const temp = `${statePath}.tmp`;
			writeFileSync(temp, JSON.stringify(state));
			let lastError;
			for (let attempt = 0; attempt < 4; attempt += 1) {
				try {
					renameSync(temp, statePath);
					return;
				} catch (error) {
					lastError = error;
					sleepSync(4);
				}
			}
			throw lastError;
		} catch (error) {
			report("write-failed", { reason: String(error?.message ?? error).slice(0, 200) });
		}
	}

	/**
	 * Write now if the board has been quiet, otherwise soon. Coalesced so a burst of tool calls does
	 * not turn into a burst of disk writes, and leading-edge so the FIRST change of a quiet period —
	 * the one the human is waiting for — is not delayed at all.
	 */
	function flush() {
		if (writeTimer !== null) {
			clearTimeout(writeTimer);
			writeTimer = null;
		}
		lastWriteAt = Date.now();
		writeState(currentState());
	}

	function scheduleWrite() {
		if (stopped) return;
		const since = Date.now() - lastWriteAt;
		if (since >= 120) {
			flush();
			return;
		}
		if (writeTimer !== null) return;
		writeTimer = setTimeout(() => {
			writeTimer = null;
			flush();
		}, 120 - since);
	}

	/**
	 * A session that was RETIRED and is working again has a new finish in its future, and the old
	 * mark would swallow it.
	 *
	 * Measured 2026-10-21 (the human: "as you finished i didnt get a finished at the top notch"):
	 * a session whose row had been retired — the overlay's ✕ or the sidebar's ✓, both of which write
	 * `state/session-finished/manual.json` — kept that mark forever. `snapshot` drops a settled row
	 * that is closed out, so while the session worked its row was back (live work ignores the mark),
	 * and the instant it finished the row VANISHED instead of turning into `✓ 1` on the tab. The
	 * board could never tell that human about that session again.
	 *
	 * The rule is the human's own: working again is not finished. So the mark is cleared through the
	 * documented route (`state: "none"` — the same write the ✕ makes) rather than edited behind that
	 * plugin's back, and both stores keep agreeing.
	 */
	async function clearStaleMarks() {
		const base = finishedUrl();
		if (base === null) return;
		for (const record of records.values()) {
			if (record.status !== STATUS.WORKING) {
				askedToClear.delete(record.id);   // a fresh finish may be retired again later
				continue;
			}
			if (manual.get(record.id) !== "finished" || askedToClear.has(record.id)) continue;
			askedToClear.add(record.id);
			try {
				const response = await fetch(base, {
					method: "POST",
					headers: { "content-type": "application/json" },
					body: JSON.stringify({ sessionId: record.id, state: "none" }),
					signal: AbortSignal.timeout(2000),
				});
				if (!response.ok) continue;
				closedOut.delete(record.id);
				manual.delete(record.id);
				report("mark-cleared", { sessionId: record.id });
			} catch (error) {
				report("mark-clear-failed", { sessionId: record.id, reason: String(error?.message ?? error).slice(0, 200) });
			}
		}
	}

	/** One turn of the backstop loop: who is attached, who is closed out, then the heartbeat write. */
	async function tick() {
		await refreshAttached();
		await clearStaleMarks();
		const settled = [];
		for (const record of records.values()) {
			// Everything that would otherwise show a row, so clearing a tick brings it back.
			if (record.status !== STATUS.IDLE) settled.push(record.id);
		}
		await refreshClosedOut(settled);
		flush();
	}

	// The route. Plugin routes are NOT cookie-gated (measured: a plugin route answers 200 where
	// `/api/*` answers 401), so this is readable from outside the app — which is what makes it
	// useful for debugging and for a client half.
	ctx.effect(() =>
		ctx.webServer.register({
			kind: "exact",
			path: routePath,
			handler: (req, res) => {
				if (req.method !== "GET") {
					res.writeHead(405, { "content-type": "application/json; charset=utf-8" });
					res.end(JSON.stringify({ ok: false, error: "GET only" }));
					return;
				}
				const state = currentState();
				res.writeHead(200, { "content-type": "application/json; charset=utf-8" });
				res.end(JSON.stringify(state));
			},
		})
	);

	/**
	 * The Settings pane's door.
	 *
	 * GET  -> what is in force, what the row says, what the pane says, and the defaults.
	 * POST -> any of { scale, screen, anchor, maxRows, hideFullscreen, opacity, motion, showProject,
	 *         showDone }, or { reset: true }. Unknown keys are refused by name rather than silently
	 *         dropped, so a typo shows up immediately instead of doing nothing.
	 *
	 * A plugin route is not cookie-gated (measured), so this is same-origin but not secret: it writes one
	 * small JSON file beside the state and nothing else. `flush()` after the write is what makes the
	 * notch change without a restart — the window reads state.json on its own tick.
	 */
	ctx.effect(() =>
		ctx.webServer.register({
			kind: "exact",
			path: settingsPath,
			handler: (req, res) => {
				const send = (status, body) => {
					res.writeHead(status, { "content-type": "application/json; charset=utf-8", "cache-control": "no-store" });
					res.end(JSON.stringify(body));
				};
				if (req.method === "GET") {
					send(200, { ok: true, settings: currentSettings(), row: settingsOf(config), stored: paneOf(), defaults: DEFAULTS });
					return;
				}
				if (req.method !== "POST") {
					send(405, { ok: false, error: "GET or POST" });
					return;
				}
				let body = "";
				req.setEncoding("utf8");
				req.on("data", (chunk) => {
					body += chunk;
					if (body.length > 8192) req.destroy();
				});
				req.on("end", () => {
					let patch;
					try {
						patch = JSON.parse(body === "" ? "{}" : body);
					} catch {
						send(400, { ok: false, error: "the body is not JSON" });
						return;
					}
					if (patch === null || typeof patch !== "object" || Array.isArray(patch)) {
						send(400, { ok: false, error: "expected an object of settings" });
						return;
					}
					if (patch.reset === true) {
						if (!clearStore(panePath)) {
							// A Reset the pane reports as done must actually have happened; the notch is
							// still showing the old values, and saying "ok" would hide that.
							report("settings-reset-failed", { path: panePath });
							send(500, { ok: false, error: "could not clear the settings file" });
							return;
						}
						paneCache = { at: -1, settings: {} };
						flush();
						send(200, { ok: true, settings: currentSettings(), stored: {} });
						return;
					}
					const unknown = Object.keys(patch).filter((key) => !SETTINGS_KEYS.includes(key));
					if (unknown.length > 0) {
						send(400, { ok: false, error: `unknown setting(s): ${unknown.join(", ")}` });
						return;
					}
					let written;
					try {
						written = writeStore(panePath, patch, settingsOf(config));
					} catch (error) {
						// The write can fail for reasons this process does not control — on Windows the
						// rename EPERMs while anything holds the target open (the same lock the state
						// publisher already retries around), or the disk is full. Without this catch the
						// throw lands in an event handler and takes the whole harness process down, from
						// one click in Settings.
						const reason = String(error?.message ?? error).slice(0, 200);
						report("settings-write-failed", { reason });
						send(500, { ok: false, error: `could not write the settings: ${reason}` });
						return;
					}
					paneCache = { at: -1, settings: {} };
					flush();
					send(200, { ok: true, settings: currentSettings(), stored: written.stored });
				});
				req.on("error", () => send(400, { ok: false, error: "the request failed" }));
			},
		})
	);

	/** The tray-quit marker the window writes beside the state file. The host deletes nothing. */
	function readQuit() {
		try {
			if (!existsSync(quitPath)) return null;
			const parsed = JSON.parse(readFileSync(quitPath, "utf8"));
			const wallMs = Number(parsed?.wallMs);
			return Number.isFinite(wallMs) && wallMs > 0 ? { wallMs } : null;
		} catch {
			// A torn marker is not a quit: the human's dismissal must not be invented.
			return null;
		}
	}

	/**
	 * Is a window already up? The exe's own named mutex keeps two windows off the screen, but
	 * without this check the plugin would keep spawning a process that exits immediately — the
	 * window writes its pid beside the state file, and a live pid means "someone else owns it".
	 */
	function windowAlreadyRunning() {
		try {
			const file = join(stateDir, "window.json");
			if (!existsSync(file)) return false;
			const parsed = JSON.parse(readFileSync(file, "utf8"));
			const pid = Number(parsed?.pid);
			if (!Number.isInteger(pid) || pid <= 0) return false;
			try {
				process.kill(pid, 0); // signal 0 asks "is it there" without touching it
				return true;
			} catch (error) {
				return error?.code === "EPERM"; // alive, but owned by somebody else
			}
		} catch {
			return false;
		}
	}

	function scheduleSpawn(delayMs) {
		if (stopped) return;
		if (spawnTimer !== null) clearTimeout(spawnTimer);
		spawnTimer = setTimeout(() => {
			spawnTimer = null;
			startWindow();
		}, Math.max(250, delayMs));
	}

	/**
	 * "There is no window and I cannot get one" — throttled to once a minute, with the retry that still
	 * finds a build somebody is making while this runs.
	 *
	 * @param {{ tried: Array<{ source: string, path: string }> }} found
	 */
	function reportMissing(found) {
		const now = Date.now();
		if (lastMissingAt === 0 || now - lastMissingAt >= MISSING_REPORT_MS) {
			lastMissingAt = now;
			report("window-missing", {
				fix: missingExeFix(found),
				tried: found.tried.map((candidate) => candidate.path),
				autoDownload: config.autoDownload !== false,
			});
		}
		scheduleSpawn(MISSING_RETRY_MS);
	}

	/**
	 * Fetch this version's release asset, once, and let the supervisor pick it up when it lands.
	 *
	 * The URL comes from the manifest (`repository`) and the version, so nothing here is a constant
	 * somebody has to remember to change; a package with no GitHub repository says so instead of
	 * inventing a URL. A failed fetch is reported and then treated exactly like an absent binary: the
	 * board keeps serving, and `reportMissing` prints the by-hand command.
	 *
	 * @param {{ tried: Array<{ source: string, path: string }> }} found
	 */
	async function fetchWindow(found, exeRoot) {
		// The release TAG is this package's version — that is the tag that was published. The
		// DESTINATION has to be the place `findExe` will look, which is derived from `exeRoot`.
		// Measured in the end-to-end test: they disagreed by one override, the download landed in
		// `window/0.1.1/` while the lookup asked for `window/0.0.0/`, and the only symptom was a
		// `window-missing` line immediately after a successful `window-fetched`.
		const releaseVersion = packageVersion(PACKAGE_ROOT);
		const target = installedExePath({ version: packageVersion(exeRoot) });
		// `downloadUrl` lets a mirror or a local test stand in for the GitHub release; without it the
		// URL is derived from the manifest and the version, so a release needs no code change.
		const url = typeof config.downloadUrl === "string" && config.downloadUrl.trim() !== ""
			? config.downloadUrl.trim()
			: releaseAssetUrl({ version: releaseVersion, repository: packageRepository(PACKAGE_ROOT) });
		if (url === null) {
			report("window-fetch-skipped", { reason: "package.json names no GitHub repository", version: releaseVersion });
			reportMissing(found);
			return;
		}
		report("window-fetch", { url, dest: target });
		const result = await downloadExe({ url, dest: target });
		if (!result.ok) {
			report("window-fetch-failed", { url, reason: result.reason });
			reportMissing(found);
			return;
		}
		report("window-fetched", { dest: target, bytes: result.bytes, verified: result.verified === true });
		scheduleSpawn(250);
	}
	/** A child that died. Respawn per `nextSpawn`: 15 s for the first quick deaths, then every 5 minutes. */
	function childGone(which) {
		if (child !== which) return;   // `error` and `exit` both fire; count the death once
		child = null;
		if (stopped) return;
		const next = nextSpawn({ lived: Date.now() - childStartedAt, respawns });
		respawns = next.respawns;
		// Said once per streak, not on every slow retry.
		if (next.gaveUp && respawns === MAX_RESPAWNS + 1) {
			report("window-giveup", { tries: MAX_RESPAWNS, retryEveryMs: next.delayMs });
		}
		scheduleSpawn(next.delayMs);
	}

	/**
	 * The window's own last words, when it died of an exception: it appends them to
	 * `window-crash.log` beside the state file. Only a line written during THIS child's life counts.
	 */
	function crashOf(startedAt) {
		try {
			const file = join(stateDir, "window-crash.log");
			if (!existsSync(file) || statSync(file).mtimeMs < startedAt) return null;
			const lines = readFileSync(file, "utf8").trim().split(/\r?\n/);
			return lines[lines.length - 1].slice(0, 300);
		} catch {
			return null;
		}
	}

	/**
	 * Spawn the window — or explain, once, why there is none.
	 *
	 * Three ways this ends without a window, and all three keep the host serving state (the plugin
	 * is never silently absent, and the board can always tell the human what is wrong):
	 *
	 *   - the human dismissed it from the tray (`quit.json`, newer than this boot and under an hour
	 *     old): logged ONCE as `window-quit`, then retried exactly when the hour is up;
	 *   - the binary is not installed: `window-missing` at most once a minute with the exact fix,
	 *     retried every 15 s because a missing exe is usually a `dotnet build` in progress;
	 *   - another process already owns the window (`window.json` holds a live pid).
	 */
	function startWindow() {
		if (!spawnWindow || stopped) return;

		const quit = readQuit();
		if (quit !== null && quitHold({ quitWallMs: quit.wallMs, bootedAt })) {
			if (quitLoggedFor !== quit.wallMs) {
				quitLoggedFor = quit.wallMs;
				report("window-quit", { wallMs: quit.wallMs, retryAt: quit.wallMs + QUIT_HOLD_MS });
			}
			// Do not spawn for an hour — but do come back when that hour is over, rather than
			// leaving the notch dead until the harness restarts.
			scheduleSpawn(quit.wallMs + QUIT_HOLD_MS - Date.now() + 250);
			return;
		}

		// `exeRoot` is where the developer and published builds are looked for. It defaults to this
		// package, and it exists so a test (or a wrapper that ships the binary elsewhere) does not
		// have to depend on what happens to be sitting in the checkout: measured, a developer who had
		// run `dotnet build` made the "no window anywhere" tests find that build and spawn it.
		const exeRoot = typeof config.exeRoot === "string" && config.exeRoot.trim() !== "" ? config.exeRoot : PACKAGE_ROOT;
		const found = findExe({
			exePath: config.exePath ?? "",
			env: process.env,
			version: packageVersion(exeRoot),
			root: exeRoot,
		});
		if (found.path === null) {
			// No window anywhere. Before reporting that, FETCH it: installing this plugin should be
			// one command like every other DSH plugin, and the 65 MB binary is the only thing that
			// cannot ride in the tarball. One attempt per process — a version that is not on the
			// release, or a machine with no network, must not become a download loop.
			if (config.autoDownload !== false && !downloadTried) {
				downloadTried = true;
				void fetchWindow(found, exeRoot);
				return;
			}
			reportMissing(found);
			return;
		}
		if (windowAlreadyRunning()) {
			report("window-already-running");
			return;
		}
		try {
			// A single-file .NET app extracts its native libraries BEFORE `Main` runs, into
			// `%TEMP%\.net\<name>\` unless told otherwise — and `%TEMP%` is not reliably writable.
			// Measured on this machine: the published exe died with "Failed to create directory
			// C:\…\Temp\.net\AgentsNotch\ for extracting bundled files" (error 5), which from the
			// outside is indistinguishable from "the notch never appears", with nothing on screen to
			// explain it. Pointing the extractor inside our own state directory makes the launch
			// independent of the machine's temp policy, and an uninstall removes it with the rest.
			const bundleDir = join(stateDir, "bundle");
			try {
				mkdirSync(bundleDir, { recursive: true });
			} catch {
				// Leaving it unset is no better: let the spawn fail and be reported with its code.
			}
			childStartedAt = Date.now();
			const spawned = spawn(found.path, ["--state", statePath, "--parent-pid", String(process.pid)], {
				stdio: "ignore",
				windowsHide: true,
				env: { ...process.env, DOTNET_BUNDLE_EXTRACT_BASE_DIR: bundleDir },
			});
			child = spawned;
			spawned.on("error", (error) => {
				report("window-error", { reason: String(error?.message ?? error).slice(0, 200) });
				childGone(spawned);
			});
			const startedAt = childStartedAt;
			spawned.on("exit", (code) => {
				const crash = code === 0 ? null : crashOf(startedAt);
				report("window-exit", crash === null ? { code } : { code, crash });
				childGone(spawned);
			});
			report("window-started", { pid: spawned.pid, exePath: found.path, source: found.source });
		} catch (error) {
			child = null;
			report("window-spawn-failed", { reason: String(error?.message ?? error).slice(0, 200) });
			scheduleSpawn(RESPAWN_MS);
		}
	}

	ctx.effect(() => {
		const poll = setInterval(() => void tick(), pollMs);
		const sweepTimer = setInterval(() => void sweep(), sweepMs);
		report("boot", {
			pid: process.pid,
			statePath,
			routePath,
			finishedRoutePath,
			settings,
			budget: contextOf(config).budget,
		});
		void tick();
		void sweep();
		startWindow();
		return () => {
			stopped = true;
			clearInterval(poll);
			clearInterval(sweepTimer);
			if (writeTimer !== null) clearTimeout(writeTimer);
			if (spawnTimer !== null) clearTimeout(spawnTimer);
			if (child !== null) {
				try { child.kill(); } catch { /* already gone */ }
				child = null;
			}
		};
	});
}
