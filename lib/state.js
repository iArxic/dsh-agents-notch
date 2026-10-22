/**
 * The state machine behind the overlay board — pure, so it can be tested without a harness.
 *
 * What a session's state IS, and what it is not:
 *
 *   - NOT "the session exists". This machine has 40 sessions in a week and at most a handful are
 *     doing anything; a board of all of them is noise. `live` in the harness means ATTACHED, not
 *     busy, and the harness keeps a session attached long after you switch away from it.
 *   - A session earns a row when it has a live reason to be there: the agent is working, it is
 *     blocked on a human, it failed, or it finished and you have not closed it out yet.
 *
 * The four sources, and nothing else:
 *
 *   working   | a turn is open (`turn/start`), or a tool is being called
 *   attention | an approval was asked and not yet decided, or the agent asked a question
 *   done      | the last turn ended `completed`, and you have not closed the session out
 *   error     | the last turn ended `error`, `max-tokens`, `blocked` or `interrupted`
 *
 * WHY THIS FILE EXISTS SEPARATELY FROM v2 (measured 2026-10-20, the bug iArxic reported as "if an
 * agent finished right now he disappears completely off the notch"):
 *
 *   `turn/end` does NOT carry a string reason. It carries a DISCRIMINATED OBJECT —
 *   `reason: { kind: "completed" }` — because the harness types it as `TurnEndReason`, a member of
 *   `TurnEndReasonMap` (`{ kind: 'completed' } | { kind: 'aborted', reason } | { kind: 'blocked' }
 *   | { kind: 'error', error } | { kind: 'max-tokens' } | { kind: 'interrupted' } | { kind:
 *   'forked' }`). The harness's own loop builds exactly that: `turnEnds = { kind: "completed" }`,
 *   then `session.append("turn/end", { turn, reason: turnEnds })`.
 *
 *   v2 read `typeof data.reason === "string" ? data.reason : ""`, so the reason was ALWAYS `""`
 *   and every single turn end — completed, errored, anything — fell through to IDLE. The row was
 *   dropped in the same millisecond the agent finished, which is exactly what was reported. Its
 *   unit tests passed because the fixtures fed it strings the harness never emits, so the one
 *   shape that matters was the one shape never tested.
 *
 * The lesson is written into the tests: fixtures are taken from the harness's real event shape.
 *
 * WHY THIS FILE EXISTS SEPARATELY FROM v3 (measured 2026-10-21):
 *
 *   A row carried no way to tell a session the human opened from a subagent somebody delegated to,
 *   and the board is mostly subagents while a team is working — six rows, all reading "You are the
 *   … teammate". The harness's own session header says which is which:
 *
 *   `{"type":"session","id":"4bd2f6ab-…","parentSession":"session-fe19fbd0-…",
 *     "origin":"subagent","delegationDepth":1}` — while a session opened in the app has
 *   `"delegationDepth":0` and NO `origin` and NO `parentSession` at all (read out of two real logs
 *   on this machine, not inferred). So both fields are copied through as `origin` and
 *   `parentSession`, and everything about how they are DRAWN stays in the window.
 *
 * WHY THIS FILE EXISTS SEPARATELY FROM v4/v5 (measured 2026-10-21):
 *
 *   Three facts the board wanted and did not have, all already on the live feed:
 *
 *   1. CONTEXT — and v5 got this one WRONG, which is the whole of v6. `assistant/message` carries
 *      `data.usage = { inputTokens, outputTokens, cacheReadTokens, reasoningTokens, totalTokens }`,
 *      and the arithmetic is exact on every usage record of seven real sessions on this machine:
 *
 *          totalTokens === inputTokens + cacheReadTokens + outputTokens
 *
 *      So `inputTokens` is NOT the prompt: it is the part of the prompt that MISSED the cache.
 *      After a cache hit it collapses to a few hundred tokens — 228, 865, 3,182 in those logs —
 *      while the prompt the model was actually sent was 99,428, and v5 put the small number on the
 *      board. That is why the readout looked like an output-token count. v6 stores the whole step;
 *      see `fullContextTokens`. The last message still wins and is not summed, because a sum only
 *      ever grows and means nothing.
 *   2. NO FAILED-TOOL COUNT. v5 counted `tool/result` records with `data.message.isError === true`
 *      and put "2 tools failed" on the row. iArxic had it removed outright on 2026-10-21: a per-turn
 *      count is not a moment in time, so it outlived the mistake it reported, and a retry that is
 *      being worked around is still just work. A turn that really ended badly is an `error` row,
 *      which the board already draws with its own badge and ring — that is where a failure belongs.
 *
 * @module agent-overlay/state
 */
/** The five states a session can be in. `idle` is the absence of a reason to be shown. */
import { homedir } from "node:os";

/**
 * A tool call that means "the agent is waiting for the human". `ask_user_question` is the one the
 * harness ships; the suffix test keeps a renamed or additional one from becoming invisible.
 */
const QUESTION_TOOLS = new Set(["ask_user_question", "ask_follow_up_question"]);
export function isQuestionTool(name) {
	const text = typeof name === "string" ? name.trim().toLowerCase() : "";
	return QUESTION_TOOLS.has(text) || text.endsWith("_question");
}

export const STATUS = Object.freeze({
	WORKING: "working",
	ATTENTION: "attention",
	DONE: "done",
	ERROR: "error",
	IDLE: "idle",
});

/** The identity palette — one stable hue per session, independent of its state. */
export const IDENTITY_HUES = Object.freeze(["teal", "violet", "coral", "pink", "lime"]);

/**
 * What the board shows first.
 *
 * `done` is LAST, and that ordering is load-bearing now that a finished row no longer expires by
 * itself: it stays until the human closes the session out, so a fortnight of unticked finished
 * sessions would otherwise sit above the agents that are actually working and push them off a
 * six-row board. Live work outranks history; history is still there, below it.
 */
const ORDER = { attention: 0, error: 1, working: 2, done: 3 };

/** Turn-end reasons that mean the work did not finish. */
const FAILURE_REASONS = new Set(["error", "max-tokens", "blocked", "interrupted"]);

/**
 * The `kind` of a turn-end reason.
 *
 * The harness emits `{ kind: "completed" }`; a durable log written by an older build, or a
 * hand-written test fixture, may carry the bare string. Both are accepted, because guessing wrong
 * here is silent and total: every row disappears at the moment it matters.
 *
 * @param {unknown} reason
 * @returns {string} the kind, or "" when there is none
 */
export function reasonKind(reason) {
	if (typeof reason === "string") return reason;
	if (reason !== null && typeof reason === "object" && typeof reason.kind === "string") return reason.kind;
	return "";
}

/**
 * When an event happened, in ms — the envelope's own clock, or NaN when it does not say.
 *
 * The durable envelope carries `{ type, seq, time, data }`, and `time` is a wall clock in the same
 * units as `Date.now()`. The reducer used to ignore it and stamp everything with the ARRIVAL time,
 * which is right for a live event and badly wrong for a replay: `sweep()` re-reads a session's whole
 * log when the plugin mounts, so a turn that ended yesterday was published as `since: <now>` — the
 * row said "finished 0s ago", it had the newest `since` on the board so it took the big bot and
 * replayed the finish squint, and under `retire: "timer"` it was handed a fresh 15 minutes on every
 * restart instead of already being history.
 *
 * A value that is not a plausible epoch-ms instant is treated as ABSENT (arrival time): the envelope
 * is not this plugin's contract, and a monotonic or session-relative clock read as UTC would be
 * worse than no answer at all.
 *
 * @param {{ time?: unknown } | null | undefined} event
 * @returns {number} epoch ms, or NaN
 */
export function eventTime(event) {
	const value = Number(event?.time ?? Number.NaN);
	if (!Number.isFinite(value) || value <= 0) return Number.NaN;
	const now = Date.now();
	// Today's epoch ms is ~1.7e12; an order of magnitude below that is a different clock. A value
	// far in the future is a clock that jumped, not an event that has not happened.
	if (value < 1e12 || value > now + 86_400_000) return Number.NaN;
	return value;
}

/**
 * A stable hue for a session id. FNV-1a over the whole id, so two sessions of the same project
 * still get different colours and the same session always gets the same one.
 *
 * @param {string} sessionId
 * @returns {string} one of {@link IDENTITY_HUES}
 */
export function identityOf(sessionId) {
	let hash = 2166136261;
	for (const ch of String(sessionId ?? "")) {
		hash ^= ch.codePointAt(0);
		hash = Math.imul(hash, 16777619) >>> 0;
	}
	return IDENTITY_HUES[hash % IDENTITY_HUES.length];
}

/**
 * The text of a message's content blocks, or "". A durable log stores content as a block array,
 * but a live event may carry a plain string.
 *
 * @param {unknown} content
 * @returns {string}
 */
export function textOf(content) {
	if (typeof content === "string") return content;
	if (!Array.isArray(content)) return "";
	return content
		.filter((block) => block !== null && typeof block === "object" && typeof block.text === "string")
		.map((block) => block.text)
		.join("\n");
}

/**
 * The harness's own boilerplate, which arrives as a message and would otherwise become a
 * session's title. Measured on this machine: the first `user/message` of a fresh session is the
 * runtime context ("You are working on the …"), not the human's prompt.
 */
const BOILERPLATE = [
	"you are working on the",
	"current working directory",
	"current dsh file policy",
	"current runtime context",
	"current deepseek harness",
	"approval policy",
	"the team lead named",
];

/**
 * Is this text the harness talking to itself?
 *
 * @param {string} text
 * @returns {boolean}
 */
export function isBoilerplate(text) {
	const head = String(text ?? "").trim().toLowerCase();
	if (head === "") return true;
	return BOILERPLATE.some((marker) => head.startsWith(marker));
}

/**
 * A title from the human's first message: the first line, trimmed to something a board can show.
 * Used only when the harness has no title of its own, and never from the harness's boilerplate.
 *
 * @param {unknown} content
 * @returns {string}
 */
export function titleFromMessage(content) {
	const line = textOf(content).trim().split("\n")[0]?.trim() ?? "";
	if (line === "" || isBoilerplate(line)) return "";
	return line.length > 70 ? `${line.slice(0, 67)}…` : line;
}

/**
 * The project a session belongs to: the last segment of its working directory. A session in the
 * home directory is "home", not the username.
 *
 * @param {string | null | undefined} cwd
 * @returns {string}
 */
export function projectOf(cwd) {
	if (typeof cwd !== "string" || cwd.trim() === "") return "";
	const trimmed = cwd.replace(/[\\/]+$/, "");
	const home = homedir().replace(/[\\/]+$/, "");
	if (trimmed.toLowerCase() === home.toLowerCase()) return "home";
	const parts = trimmed.split(/[\\/]+/).filter(Boolean);
	const last = parts[parts.length - 1] ?? "";
	return last === "" ? "" : last;
}

/**
 * How many tokens the session's last step actually put through its context window.
 *
 * `usage.inputTokens` is NOT that number, and reading it as one was v5's bug: the fields sum
 * exactly — `totalTokens = inputTokens + cacheReadTokens + outputTokens` — so `inputTokens` is only
 * the slice of the prompt that MISSED the cache. Measured on real logs (2026-10-21): a step whose
 * prompt was 99,428 tokens reports `inputTokens: 228, cacheReadTokens: 99,200, outputTokens: 90`.
 * The small number looks exactly like an output-token count, which is what it was mistaken for.
 *
 * `totalTokens` is the provider's own sum and is used when it is there; the three parts are the
 * fallback, because one record in that sample carried them and no `totalTokens` at all.
 *
 * Prompt plus answer, not prompt alone: the answer is history on the very next request, so the whole
 * step is the honest "how full is this session" figure.
 *
 * @param {{ inputTokens?: unknown, cacheReadTokens?: unknown, outputTokens?: unknown, totalTokens?: unknown } | null | undefined} usage
 * @returns {number} tokens, or NaN when the record carries nothing usable
 */
export function fullContextTokens(usage) {
	if (usage === null || typeof usage !== "object") return Number.NaN;
	const count = (value) => {
		const number = Number(value ?? 0);
		return Number.isFinite(number) ? number : Number.NaN;
	};
	const total = count(usage.totalTokens);
	if (Number.isFinite(total) && total > 0) return total;
	// No total: the prompt is what makes the record usable at all. An answer alone says nothing
	// about how full the context is.
	const prompt = count(usage.inputTokens) + count(usage.cacheReadTokens);
	if (!Number.isFinite(prompt) || prompt <= 0) return Number.NaN;
	return prompt + count(usage.outputTokens);
}

/**
 * A fresh record for a session. It starts idle: a session is shown because something HAPPENED to
 * it, never because it exists.
 *
 * @param {string} id
 * @param {number} [now]
 */
export function createRecord(id, now = Date.now()) {
	return {
		id,
		title: "",
		project: "",
		cwd: "",
		status: STATUS.IDLE,
		since: now,
		activity: null,
		approval: null,
		steps: 0,
		lastEventAt: 0,
		/**
		 * The `callId` of a `ask_user_question` tool call that has not answered yet — "" when there is
		 * no question outstanding. Not published: the row carries the CONSEQUENCE (status + approval),
		 * and this is only what tells the reducer when to take it back.
		 */
		questionCallId: "",
		identity: identityOf(id),
		/** `"subagent"` when the harness delegated this session; `""` for one the human opened. */
		origin: "",
		/** The session that delegated it, from the same header. Empty for a top-level session. */
		parentSession: "",
		/**
		 * Everything this session's LAST step put through its context, in tokens — prompt (cached and
		 * uncached) plus the answer. It is how full the context is, and the one number on the board
		 * that predicts a compaction. 0 until the session has answered once.
		 */
		contextTokens: 0,
	};
}

/**
 * Fold one session event into a record.
 *
 * The event vocabulary is the one the harness emits: `turn/start`, `turn/end` (with a `reason`
 * OBJECT — see the module header), `approval/asked`, `approval/decided`, `tool/call`,
 * `session/title` and `user/message`.
 *
 * Two rules that are easy to get wrong:
 *
 *   - A `user/message` is NOT proof the human typed anything. The harness writes `time-context`,
 *     `agent-instructions`, `runtime-context`, `skill-catalog` and more into the same stream, and
 *     it even auto-invokes a skill whose name appears in a message. Only `data.source.kind ===
 *     "user"` is the human — measured over this machine's corpus, 351 of 1,544 such records.
 *   - `turn/end` with reason `aborted` is NOT an error: the human interrupted it, or the session
 *     forked. It goes back to idle rather than raising an error row.
 *
 * `now` is the ARRIVAL clock and is only a fallback: an event that carries a usable `time` (every
 * durable one does) is stamped with when it happened, which is what makes a log replay honest. See
 * {@link eventTime}.
 *
 * @param {ReturnType<typeof createRecord>} record
 * @param {{ type?: string, time?: unknown, data?: Record<string, any> }} event
 * @param {number} [now] arrival time, used when the event carries no usable one
 */
export function applyEvent(record, event, now = Date.now()) {
	// The event's own clock wins over the arrival clock. For a live event the two are milliseconds
	// apart; for a REPLAYED one they are the difference between "finished 0s ago" and the truth.
	const at = eventTime(event);
	if (Number.isFinite(at)) now = at;
	const type = event?.type;
	const data = event?.data ?? {};
	switch (type) {
		case "turn/start":
			record.status = STATUS.WORKING;
			record.since = now;
			record.approval = null;
			record.steps = 0;
			break;
		case "turn/end": {
			const kind = reasonKind(data.reason);
			if (kind === "completed") {
				record.status = STATUS.DONE;
				record.since = now;
			} else if (FAILURE_REASONS.has(kind)) {
				record.status = STATUS.ERROR;
				record.since = now;
			} else {
				// aborted / forked / unknown: the human stopped it, so there is nothing to look at.
				record.status = STATUS.IDLE;
				record.since = now;
			}
			record.approval = null;
			break;
		}
		case "approval/asked":
			record.status = STATUS.ATTENTION;
			record.since = now;
			record.approval = {
				toolName: typeof data.toolName === "string" ? data.toolName : typeof data.name === "string" ? data.name : "an approval",
				reason: typeof data.reason === "string" ? data.reason : null,
			};
			break;
		case "approval/decided":
			record.approval = null;
			if (record.status === STATUS.ATTENTION) {
				record.status = STATUS.WORKING;
				record.since = now;
			}
			break;
		case "tool/call": {
			const name = typeof data.name === "string" ? data.name : typeof data.toolName === "string" ? data.toolName : "";
			record.activity = { kind: "tool", label: name !== "" ? name : "a tool" };
			record.steps += 1;
			// A QUESTION IS THE LOUDEST THING A SESSION CAN DO, and it is NOT an `approval/asked`.
			// Measured 2026-10-21 in a real session log: `ask_user_question` arrives as an ordinary
			// `tool/call`, and no question-typed event exists at all (`question-ish event types seen:
			// none`). A reducer that only listens for approvals therefore left the notch saying
			// "working" while the agent sat waiting for the human — which is exactly the one state the
			// notch exists to show.
			//
			// The clear is the next tool call, which can only happen once the human answered.
			if (isQuestionTool(name)) {
				record.questionCallId = typeof data.callId === "string" ? data.callId : "";
				record.status = STATUS.ATTENTION;
				record.since = now;
				record.approval = { toolName: "question", reason: null };
			} else if (record.questionCallId !== "") {
				record.questionCallId = "";
				if (record.status === STATUS.ATTENTION && record.approval?.toolName === "question") {
					record.status = STATUS.WORKING;
					record.since = now;
					record.approval = null;
				}
			}
			break;
		}
		case "tool/result": {
			// The answer itself: `data.toolCallId` (also mirrored at `data.message.toolCallId`) names
			// the call that has just come back.
			const callId = typeof data.toolCallId === "string" ? data.toolCallId
				: typeof data.message?.toolCallId === "string" ? data.message.toolCallId : "";
			if (callId !== "" && callId === record.questionCallId) {
				record.questionCallId = "";
				if (record.status === STATUS.ATTENTION && record.approval?.toolName === "question") {
					record.status = STATUS.WORKING;
					record.since = now;
					record.approval = null;
				}
			}
			break;
		}
		case "assistant/message": {
			// The ONLY place a session's context size appears. Measured shape (2026-10-21):
			// `data.usage = { inputTokens, outputTokens, cacheReadTokens, reasoningTokens,
			// totalTokens }`, summing exactly as `totalTokens = input + cacheRead + output` — so
			// `inputTokens` alone is the uncached slice, not the prompt. See `fullContextTokens`.
			const full = fullContextTokens(data?.usage);
			if (Number.isFinite(full) && full > 0) record.contextTokens = full;
			break;
		}
		case "session/title":
			// The harness generates a session's own title from its first message, and the first
			// message of a fresh session is the runtime context — so the title can be boilerplate.
			if (typeof data.title === "string" && data.title.trim() !== "" && !isBoilerplate(data.title)) {
				record.title = data.title.trim();
			}
			break;
		case "user/message": {
			if (data?.source?.kind !== "user") break;
			// The human is back: whatever it was waiting for is answered.
			record.approval = null;
			if (record.status === STATUS.DONE || record.status === STATUS.ATTENTION) {
				record.status = STATUS.WORKING;
				record.since = now;
			}
			if (record.title === "") record.title = titleFromMessage(data.content);
			break;
		}
		default:
			break;
	}
	record.lastEventAt = now;
	return record;
}



/**
 * The rows the board shows.
 *
 * A finished or failed session KEEPS its row. There is no timer here on purpose: the only thing
 * that retires one is the human closing it out, which arrives as `finished` — the session-finished
 * verdict, i.e. the tick in the sidebar (`plugins/session-finished`). A time limit was the old
 * behaviour and it was the wrong shape of answer: it hid work nobody had looked at yet, and it hid
 * a session that had only been quiet for half an hour.
 *
 * `working` and `attention` ignore the verdict: a session that was closed out and then resumed is
 * working again, and a question it asks now still needs an answer.
 *
 * NO TIMER LIVES HERE. Every field on a row is a property of the session, not an event with a shelf
 * life: a tail that decays would be the one thing on this board that changes without anything having
 * happened, and a count that never decays (the old "2 tools failed") reads as a bug. Both were tried;
 * the human's answer was to keep the row about the session.
 *
 * @param {Iterable<ReturnType<typeof createRecord>>} records
 * @param {{ maxRows?: number, finished?: Set<string> }} [options]
 * @returns {Array<Record<string, any>>}
 */
export function snapshot(records, { maxRows = 6, finished = new Set() } = {}) {
	const rows = [];
	for (const record of records) {
		if (record.status === STATUS.IDLE) continue;
		const settled = record.status === STATUS.DONE || record.status === STATUS.ERROR;
		if (settled && finished.has(record.id)) continue;
		rows.push({
			id: record.id,
			title: record.title,
			project: record.project,
			cwd: record.cwd,
			status: record.status,
			since: record.since,
			activity: record.activity,
			approval: record.approval,
			steps: record.steps,
			identity: record.identity,
			// WHICH SESSION THIS IS, not what it is doing: the board counts a parent's children and
			// draws a teammate's row a little quieter, and both need the delegation edge.
			origin: record.origin,
			parentSession: record.parentSession,
			// how full this session's context is
			contextTokens: record.contextTokens,
		});
	}
	rows.sort((a, b) => (ORDER[a.status] ?? 9) - (ORDER[b.status] ?? 9) || b.since - a.since);
	return rows.slice(0, maxRows);
}

/**
 * How long a settled row may sit on the board when the harness's own close-out verdict is
 * UNAVAILABLE — the `retire: "timer"` fallback, 15 minutes.
 *
 * This is not a liking for timers. Rule 2 of the contract says a settled row goes away when the
 * human closes it out, and the thing that knows that is `plugins/session-finished`. That plugin is
 * optional on a stranger's machine: with it absent, the verdict is never asked successfully, and a
 * board of every session that ever finished fills up and the working agents fall off it. So the
 * fallback is time, it is labelled as such in the published `retire`, and it is only ever used
 * after the route has actually failed (see the host's `finishedFailures`).
 */
export const SETTLED_FALLBACK_MS = 900_000;

/**
 * Which sessions are retired — i.e. whose settled row is dropped from the board.
 *
 * Two sources, and the second only exists because the first can be unavailable:
 *
 *   - `finished`: the close-out verdict (the sidebar's ✓, `plugins/session-finished`, or the human's
 *     `manual.json`), handed in by the host. Always honoured, in both `retire` modes.
 *   - time: ONLY under `retire: "timer"`, and ONLY for a row that is already settled (`done` /
 *     `error`). A `working` or `attention` row is never retired by either source — a session you
 *     closed out and then resumed is working again, and a question it asks now still needs an
 *     answer. That rule is `snapshot`'s, and this function must not undermine it by naming live
 *     rows here.
 *
 * `since` is when the row entered its current status, so "settled 15 minutes ago" is exactly
 * `now - since`. Nothing else on a row decays.
 *
 * @param {Iterable<{ id: string, status: string, since: number }>} records
 * @param {{ finished?: Set<string>, retire?: "route" | "timer", now?: number, fallbackMs?: number }} [options]
 * @returns {Set<string>} ids whose settled row must be dropped
 */
export function retiredRows(records, { finished = new Set(), retire = "route", now = Date.now(), fallbackMs = SETTLED_FALLBACK_MS } = {}) {
	const retired = new Set(finished);
	if (retire !== "timer") return retired;
	for (const record of records) {
		if (record.status !== STATUS.DONE && record.status !== STATUS.ERROR) continue;
		if (now - record.since >= fallbackMs) retired.add(record.id);
	}
	return retired;
}

/**
 * The board's own summary: the worst state among the rows, and how many are in each.
 *
 * The precedence matches the tab's: a question for the human, then a failure, then live work, then
 * history. `done` is deliberately not allowed to outrank `working` — the tab is a glance, and
 * "finished" while three agents are still working is the misleading summary.
 *
 * @param {Array<{ status: string }>} rows
 */
export function summarise(rows) {
	const count = (status) => rows.filter((row) => row.status === status).length;
	const attention = count(STATUS.ATTENTION);
	const error = count(STATUS.ERROR);
	const working = count(STATUS.WORKING);
	const done = count(STATUS.DONE);
	return {
		status: attention > 0 ? STATUS.ATTENTION
			: error > 0 ? STATUS.ERROR
			: working > 0 ? STATUS.WORKING
			: done > 0 ? STATUS.DONE
			: STATUS.IDLE,
		working,
		attention,
		done,
		error,
		total: rows.length,
	};
}
