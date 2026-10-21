/**
 * The reducer's tests — the rule that decides which sessions earn a row.
 *
 * These are the assertions that must not drift: a session that merely EXISTS must never get a
 * row, a `user/message` that the harness wrote itself must never look like the human, a session
 * waiting on an approval must outrank every other row, and a session that has just finished must
 * KEEP its row until the human ticks it closed.
 *
 * THE FIXTURES ARE THE POINT. v2 passed every test while the overlay dropped every finished
 * session the instant it finished, because the fixtures fed `turn/end` a string reason and the
 * harness sends `{ kind: "completed" }`. Every `turn/end` here is built from the real shape now,
 * and one test asserts the string shape too, so the legacy path cannot rot either.
 *
 * Run: node --test "plugins/agent-overlay/test/*.test.mjs"   (the glob, not the directory)
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
	IDENTITY_HUES,
	SETTLED_FALLBACK_MS,
	STATUS,
	applyEvent,
	createRecord,
	fullContextTokens,
	identityOf,
	projectOf,
	reasonKind,
	retiredRows,
	snapshot,
	summarise,
	titleFromMessage,
} from "../lib/state.js";

/** Fold a list of events into a record, the way the host does. */
function fold(events, id = "session-test", now = 1_000) {
	const record = createRecord(id, now);
	for (const event of events) applyEvent(record, event, now);
	return record;
}

/** The event the harness actually appends: `session.append("turn/end", { turn, reason: { kind } })`. */
function turnEnd(kind) {
	return { type: "turn/end", data: { turn: 1, reason: { kind } } };
}

test("a session that merely exists earns no row", () => {
	const record = createRecord("session-idle");
	assert.equal(record.status, STATUS.IDLE);
	assert.deepEqual(snapshot([record]), []);
});

test("a finished turn is DONE — the harness sends an object reason, not a string", () => {
	// The bug Kuba reported: `reason: { kind: "completed" }` was read as a string, so the reason
	// was always "" and EVERY turn end fell through to idle. The row vanished the instant the
	// agent finished, which is exactly "he disappears completely off the notch".
	assert.equal(reasonKind({ kind: "completed" }), "completed");
	assert.equal(reasonKind("completed"), "completed");
	assert.equal(reasonKind(undefined), "");
	assert.equal(reasonKind(null), "");
	assert.equal(reasonKind({}), "");

	const record = fold([{ type: "turn/start", data: { turn: 1 } }, turnEnd("completed")]);
	assert.equal(record.status, STATUS.DONE);
	assert.equal(snapshot([record]).length, 1, "a finished session must keep its row");
});

test("a turn in flight is working, and its end reason decides what comes next", () => {
	assert.equal(fold([{ type: "turn/start" }]).status, STATUS.WORKING);
	assert.equal(fold([{ type: "turn/start" }, turnEnd("completed")]).status, STATUS.DONE);
	assert.equal(fold([{ type: "turn/start" }, turnEnd("error")]).status, STATUS.ERROR);
	assert.equal(fold([{ type: "turn/start" }, turnEnd("max-tokens")]).status, STATUS.ERROR);
	assert.equal(fold([{ type: "turn/start" }, turnEnd("blocked")]).status, STATUS.ERROR);
	assert.equal(fold([{ type: "turn/start" }, turnEnd("interrupted")]).status, STATUS.ERROR);
	// An aborted turn is the human interrupting, not a failure: it goes back to idle.
	assert.equal(fold([{ type: "turn/start" }, turnEnd("aborted")]).status, STATUS.IDLE);
	assert.equal(fold([{ type: "turn/start" }, turnEnd("forked")]).status, STATUS.IDLE);
	// A bare string still works: an older log, or a fixture from before this was understood.
	assert.equal(fold([{ type: "turn/start" }, { type: "turn/end", data: { reason: "completed" } }]).status, STATUS.DONE);
});

test("an approval waiting on the human is attention, and deciding it goes back to work", () => {
	const asked = fold([
		{ type: "turn/start" },
		{ type: "approval/asked", data: { toolName: "pwsh", reason: "needs the sandbox lifted" } },
	]);
	assert.equal(asked.status, STATUS.ATTENTION);
	assert.equal(asked.approval.toolName, "pwsh");
	assert.equal(asked.approval.reason, "needs the sandbox lifted");

	const decided = applyEvent(asked, { type: "approval/decided", data: {} });
	assert.equal(decided.status, STATUS.WORKING);
	assert.equal(decided.approval, null);
});

test("a message the harness wrote is not the human", () => {
	// The harness writes time-context, agent-instructions, runtime-context and skill-catalog into
	// the same stream, and it auto-invokes a skill whose name appears in a message. Only
	// kind "user" is the human.
	const record = fold([
		{ type: "turn/start" },
		{ type: "user/message", data: { content: "the agent is thinking", source: { kind: "time-context" } } },
	]);
	assert.equal(record.status, STATUS.WORKING);
	assert.equal(record.title, "");

	const typed = fold([
		{ type: "turn/start" },
		{ type: "user/message", data: { content: [{ text: "build the overlay" }], source: { kind: "user" } } },
	]);
	assert.equal(typed.title, "build the overlay");
});

test("the human replying clears a finished row and puts the session back to work", () => {
	const done = fold([{ type: "turn/start" }, turnEnd("completed")]);
	assert.equal(done.status, STATUS.DONE);
	applyEvent(done, { type: "user/message", data: { content: [{ text: "more" }], source: { kind: "user" } } });
	assert.equal(done.status, STATUS.WORKING);
});

test("the tool being called is the activity, and the step count moves", () => {
	const record = fold([
		{ type: "turn/start" },
		{ type: "tool/call", data: { name: "roblox_verify" } },
	]);
	assert.deepEqual(record.activity, { kind: "tool", label: "roblox_verify" });
	assert.equal(record.steps, 1);
});

test("rows are ordered by what the human must do first, and history comes last", () => {
	const rows = snapshot(
		[
			fold([{ type: "turn/start" }], "a"),
			fold([{ type: "turn/start" }, turnEnd("completed")], "b"),
			fold([{ type: "turn/start" }, { type: "approval/asked", data: {} }], "c"),
			fold([{ type: "turn/start" }, turnEnd("error")], "d"),
		],
		{}
	);
	// A finished row used to sit above a working one. It cannot any more: it never expires, so a
	// fortnight of unticked finished sessions would push live work off a six-row board.
	assert.deepEqual(rows.map((row) => row.status), [STATUS.ATTENTION, STATUS.ERROR, STATUS.WORKING, STATUS.DONE]);
});

test("a finished row stays until it is ticked closed — there is no timer", () => {
	const done = fold([{ type: "turn/start" }, turnEnd("completed")], "a", 0);
	assert.equal(snapshot([done]).length, 1);
	// The old behaviour dropped it after 30 minutes whether or not anybody had looked at it.
	assert.equal(snapshot([done], { finished: new Set() }).length, 1);
	// The tick is the only thing that retires it.
	assert.equal(snapshot([done], { finished: new Set(["a"]) }).length, 0);
});

test("a tick hides history but never hides live work", () => {
	// Closing a session out and then resuming it must put it back on the board: the mark is about
	// the last turn, not a permanent gag.
	const working = fold([{ type: "turn/start" }], "a");
	assert.equal(snapshot([working], { finished: new Set(["a"]) }).length, 1);
	const asked = fold([{ type: "turn/start" }, { type: "approval/asked", data: {} }], "b");
	assert.equal(snapshot([asked], { finished: new Set(["b"]) }).length, 1);
});

test("idle sessions never get a row, ticked or not", () => {
	const idle = createRecord("b");
	assert.deepEqual(snapshot([idle]), []);
	assert.deepEqual(snapshot([idle], { finished: new Set(["b"]) }), []);
});

test("the board is capped", () => {
	const many = [];
	for (let i = 0; i < 12; i++) many.push(fold([{ type: "turn/start" }], `session-${i}`));
	assert.equal(snapshot(many).length, 6);
	assert.equal(snapshot(many, { maxRows: 3 }).length, 3);
});

test("a session's hue is stable and different sessions get different ones", () => {
	assert.equal(identityOf("session-abc"), identityOf("session-abc"));
	assert.ok(IDENTITY_HUES.includes(identityOf("session-abc")));
	const hues = new Set(["a", "b", "c", "d", "e", "f", "g", "h"].map((id) => identityOf(id)));
	assert.ok(hues.size > 1, "the hue must actually spread");
});

test("the project is the last segment of the working directory", () => {
	assert.equal(projectOf("C:\\Users\\kubav\\Projects\\blindKnifeArgon"), "blindKnifeArgon");
	assert.equal(projectOf("C:\\Users\\kubav\\.dsh"), ".dsh");
	assert.equal(projectOf("C:\\Users\\kubav"), "home");
	assert.equal(projectOf(""), "");
	assert.equal(projectOf(undefined), "");
});

test("a title is the first line of the message, trimmed to what a board can show", () => {
	assert.equal(titleFromMessage([{ text: "first line\nsecond line" }]), "first line");
	assert.equal(titleFromMessage("plain string"), "plain string");
	assert.equal(titleFromMessage([]), "");
	assert.equal(titleFromMessage([{ text: "x".repeat(200) }]).length, 68);
});

test("the harness's own boilerplate never becomes a title", () => {
	// The first message of a fresh session is the runtime context, and the harness generates a
	// session title from it — so both the message path and the `session/title` path have to
	// refuse it. Measured: the board showed "You are working on the" as a session's name.
	assert.equal(titleFromMessage([{ text: "You are working on the Roblox project" }]), "");
	assert.equal(titleFromMessage([{ text: "Current runtime context. This snapshot supersedes…" }]), "");
	const record = fold([{ type: "session/title", data: { title: "You are working on the" } }]);
	assert.equal(record.title, "");
	const good = fold([{ type: "session/title", data: { title: "Argon 0/4 on the laptop" } }]);
	assert.equal(good.title, "Argon 0/4 on the laptop");
});

test("the summary counts the states and takes the most urgent one", () => {
	const rows = snapshot([
		fold([{ type: "turn/start" }], "a"),
		fold([{ type: "turn/start" }, { type: "approval/asked", data: {} }], "b"),
	]);
	const summary = summarise(rows);
	assert.equal(summary.total, 2);
	assert.equal(summary.working, 1);
	assert.equal(summary.attention, 1);
	assert.equal(summary.status, STATUS.ATTENTION);
});

test("a finished row does not outrank live work in the summary", () => {
	// The tab is a glance. "finished" while two agents are still working is the misleading answer:
	// the tab must carry the working count, and the finished row is on the board, one hover away.
	const rows = snapshot([
		fold([{ type: "turn/start" }], "a"),
		fold([{ type: "turn/start" }], "b"),
		fold([{ type: "turn/start" }, turnEnd("completed")], "c"),
	]);
	const summary = summarise(rows);
	assert.equal(summary.working, 2);
	assert.equal(summary.done, 1);
	assert.equal(summary.status, STATUS.WORKING);
});

test("a delegated session is marked, and its parent's id travels with the row", () => {
	// The header the harness wrote for a real subagent on this machine, 2026-10-21. There is no
	// event that carries this — it is read off `sessionQuery.listSessions()[].header` and copied
	// onto the record, so the row is the only place a test can catch it going missing.
	const sub = createRecord("4bd2f6ab-96d3-4808-a2fc-a1186848cfd9", 1_000);
	sub.origin = "subagent";
	sub.parentSession = "session-fe19fbd0-a805-4cb7-88a3-2a5a10dafbad";
	applyEvent(sub, { type: "turn/start" }, 1_500);
	const [row] = snapshot([sub]);
	assert.equal(row.origin, "subagent");
	assert.equal(row.parentSession, "session-fe19fbd0-a805-4cb7-88a3-2a5a10dafbad");
});

test("a session the human opened has no delegation edge at all", () => {
	// Its header carries `delegationDepth: 0` and neither key, so "" is the honest answer — the
	// board must not read a missing field as "unknown parent".
	const mine = createRecord("session-62af21af-397b-4e44-b154-f1f0423afc09", 1_000);
	applyEvent(mine, { type: "turn/start" }, 1_500);
	const [row] = snapshot([mine]);
	assert.equal(row.origin, "");
	assert.equal(row.parentSession, "");
});

test("the context is the WHOLE last step, not the uncached slice of its prompt", () => {
	// The bug the human caught: the board read like an output-token count. Measured 2026-10-21 on a
	// real record — a step whose prompt was 99,428 tokens reports
	// `inputTokens: 228, cacheReadTokens: 99_200, outputTokens: 90, totalTokens: 99_518`,
	// because `inputTokens` is only the part that MISSED the cache. v5 stored 228.
	const record = createRecord("session-ctx", 1_000);
	applyEvent(record, { type: "turn/start" }, 1_000);
	applyEvent(record, { type: "assistant/message", data: { usage: { inputTokens: 40_000, outputTokens: 200 } } }, 1_100);
	applyEvent(
		record,
		{
			type: "assistant/message",
			data: { usage: { inputTokens: 228, cacheReadTokens: 99_200, outputTokens: 90, reasoningTokens: 0, totalTokens: 99_518 } },
		},
		1_200,
	);
	const [row] = snapshot([record]);
	assert.equal(row.contextTokens, 99_518);
});

test("a usage record with no arithmetic still yields the figure it can prove", () => {
	// `totalTokens` was missing on one record in the measured sample, and a fixture may carry only
	// the parts; a percentage of nothing is worse than the count it would replace.
	assert.equal(fullContextTokens({ inputTokens: 1_000, cacheReadTokens: 90_000, outputTokens: 500 }), 91_500);
	assert.equal(fullContextTokens({ inputTokens: 1_000, cacheReadTokens: 90_000, outputTokens: 500, totalTokens: 91_500 }), 91_500);
	assert.equal(fullContextTokens({ inputTokens: 1_000, totalTokens: 88_000 }), 88_000);
	assert.equal(fullContextTokens({ inputTokens: 1_000 }), 1_000);
	assert.ok(Number.isNaN(fullContextTokens({})));
	assert.ok(Number.isNaN(fullContextTokens(undefined)));
	assert.ok(Number.isNaN(fullContextTokens({ outputTokens: 500 })));
});

test("a failed tool leaves NO trace on the row — the human had the tail removed", () => {
	// Kuba, 2026-10-21: "still shows 18 tools failed i dont think it expires just clear tools failed
	// from it entirely". The count is gone from the reducer, so a row is identical whether the turn
	// hit a broken tool or not — a turn that really ended badly is an `error` row instead.
	const record = createRecord("session-tools", 1_000);
	applyEvent(record, { type: "turn/start" }, 1_000);
	applyEvent(record, { type: "tool/result", data: { message: { isError: true }, error: { name: "FsError" } } }, 1_100);
	applyEvent(record, { type: "tool/result", data: { message: { isError: true, content: [{ type: "text", text: "Error: not found" }] } } }, 1_150);
	const [row] = snapshot([record]);
	assert.equal(row.toolFailures, undefined);
	assert.equal("toolFailures" in row, false);
	assert.equal(row.status, "working");
});

test("a turn that ENDED badly is an error row, which is where a failure belongs", () => {
	const record = createRecord("session-tools-ended", 1_000);
	applyEvent(record, { type: "turn/start" }, 1_000);
	applyEvent(record, { type: "tool/result", data: { message: { isError: true } } }, 1_100);
	applyEvent(record, { type: "turn/end", data: { reason: { kind: "error" } } }, 1_200);
	const [row] = snapshot([record]);
	assert.equal(row.status, "error");
	assert.equal("toolFailures" in row, false);
});

test("under `route` a settled row never ages out — the verdict is the only retirement", () => {
	// The contract's rule 2: nothing on a row decays on a timer. While `plugins/session-finished`
	// is answering, "has enough time passed" is not the question and must not be asked.
	const done = fold([{ type: "turn/start" }, turnEnd("completed")], "done", 1_000);
	const failed = fold([{ type: "turn/start" }, turnEnd("error")], "error", 1_000);
	const aYearLater = 1_000 + 365 * 24 * 60 * 60 * 1000;
	const retired = retiredRows([done, failed], { retire: "route", now: aYearLater, fallbackMs: SETTLED_FALLBACK_MS });
	assert.deepEqual([...retired], []);
	assert.equal(snapshot([done, failed], { finished: retired }).length, 2);
});

test("under `timer` a settled row ages out after the fallback, and live work still does not", () => {
	// The fallback exists only because the close-out plugin is optional on a stranger's machine:
	// without it, the board cannot be emptied at all. It is labelled in the payload (`retire`) so
	// the window can say so — but it must not outlive its own rules: a working row is never retired
	// by time, and the age is counted from when the row SETTLED, not from when the session started.
	const done = fold([{ type: "turn/start" }, turnEnd("completed")], "done", 1_000);
	const failed = fold([{ type: "turn/start" }, turnEnd("error")], "error", 1_000);
	// It finished at 1_000+... `since` is the fold's `now`, which is 1_000 here.
	const working = fold([{ type: "turn/start" }], "working", 1_000);
	const asked = fold([{ type: "turn/start" }, { type: "approval/asked", data: {} }], "asked", 1_000);

	const justInside = retiredRows([done, failed, working, asked], { retire: "timer", now: 1_000 + SETTLED_FALLBACK_MS - 1 });
	assert.deepEqual([...justInside], [], "a row that settled 14:59 ago is still on the board");

	const justOutside = retiredRows([done, failed, working, asked], { retire: "timer", now: 1_000 + SETTLED_FALLBACK_MS });
	assert.deepEqual([...justOutside].sort(), ["done", "error"], "only the settled rows age out, never live work");
	// And the aged rows really do leave the board.
	assert.deepEqual(snapshot([done, failed, working, asked], { finished: justOutside }).map((row) => row.id).sort(), ["asked", "working"]);
});

test("the human's verdict is honoured in both retire modes, and `working` ignores it", () => {
	// Closing a session out and then resuming it must put it back: the mark is about the last turn,
	// not a permanent gag. Under `timer` an old settled row is retired whether or not it is marked —
	// which is the point of the fallback.
	const done = fold([{ type: "turn/start" }, turnEnd("completed")], "done", 1_000);
	const working = fold([{ type: "turn/start" }], "working", 1_000);
	for (const retire of ["route", "timer"]) {
		const marked = retiredRows([done, working], { retire, finished: new Set(["done", "working"]), now: 1_000 });
		assert.deepEqual([...marked].sort(), ["done", "working"], `the verdict must be carried through in ${retire}`);
		assert.deepEqual(snapshot([done, working], { finished: marked }).map((row) => row.id), ["working"]);
	}
});
