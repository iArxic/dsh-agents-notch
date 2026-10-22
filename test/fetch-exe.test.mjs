/**
 * The auto-fetch, tested without a network.
 *
 * This is the code that makes installing the plugin ONE command, so it has to behave like a careful
 * installer: derive the URL from the manifest rather than a constant, refuse to invent one, verify the
 * hash when the release publishes it, and leave nothing behind when it fails — a half-written exe
 * whose name says "installed" is worse than no exe at all.
 *
 * Run: node test/fetch-exe.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { downloadExe, githubRepoOf, releaseAssetUrl } from "../lib/fetch-exe.js";

test("the release URL comes from the manifest, never from a hard-coded owner", () => {
	assert.deepEqual(githubRepoOf("git+https://github.com/someone/dsh-agents-notch.git"), { owner: "someone", name: "dsh-agents-notch" });
	assert.deepEqual(githubRepoOf("https://github.com/someone/dsh-agents-notch"), { owner: "someone", name: "dsh-agents-notch" });
	assert.deepEqual(githubRepoOf("github:someone/dsh-agents-notch"), { owner: "someone", name: "dsh-agents-notch" });
	assert.deepEqual(githubRepoOf({ type: "git", url: "git+https://github.com/someone/dsh-agents-notch.git" }), { owner: "someone", name: "dsh-agents-notch" });
	// Anything that is not GitHub cannot become a release asset this code can name.
	assert.equal(githubRepoOf("https://gitlab.com/someone/dsh-agents-notch"), null);
	assert.equal(githubRepoOf(""), null);
	assert.equal(githubRepoOf(null), null);

	assert.equal(
		releaseAssetUrl({ version: "0.1.2", repository: "git+https://github.com/someone/dsh-agents-notch.git" }),
		"https://github.com/someone/dsh-agents-notch/releases/download/v0.1.2/AgentsNotch.exe",
	);
	// No repository, no version, or the "could not read a manifest" sentinel: no URL, rather than a 404.
	assert.equal(releaseAssetUrl({ version: "0.1.2", repository: null }), null);
	assert.equal(releaseAssetUrl({ version: "", repository: "github:someone/dsh-agents-notch" }), null);
	assert.equal(releaseAssetUrl({ version: "0.0.0", repository: "github:someone/dsh-agents-notch" }), null);
});

test("a download that verifies is renamed into place; one that does not verify leaves no file", async () => {
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-fetch-"));
	const bytes = Buffer.from("MZ pretend this is a 65 MB executable");
	const hash = createHash("sha256").update(bytes).digest("hex");

	// The happy path: the exe and its sibling .sha256, exactly as a release publishes them.
	const ok = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "ok.exe"),
		fetchImpl: async (url) => (url.endsWith(".sha256") ? new Response(`${hash}  AgentsNotch.exe\n`) : new Response(bytes)),
	});
	assert.equal(ok.ok, true);
	assert.equal(ok.verified, true);
	assert.equal(ok.bytes, bytes.length);
	assert.equal(readFileSync(join(dir, "ok.exe"), "utf8"), bytes.toString());
	assert.equal(existsSync(join(dir, "ok.exe.part")), false, "the partial file must not survive a success");

	// A hash that does not match: nothing installed, and the reason names both hashes.
	const bad = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "bad.exe"),
		fetchImpl: async (url) => (url.endsWith(".sha256") ? new Response("0".repeat(64)) : new Response(bytes)),
	});
	assert.equal(bad.ok, false);
	assert.match(bad.reason, /sha256 mismatch/);
	assert.equal(existsSync(join(dir, "bad.exe")), false);
	assert.equal(existsSync(join(dir, "bad.exe.part")), false);

	// A release with no hash asset at all: install it, and say it was not verified.
	const unverified = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "plain.exe"),
		fetchImpl: async (url) => (url.endsWith(".sha256") ? new Response("nope", { status: 404 }) : new Response(bytes)),
	});
	assert.equal(unverified.ok, true);
	assert.equal(unverified.verified, false);

	// A 404 for the exe itself: no file, no throw, a reason a log line can carry.
	const missing = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "missing.exe"),
		fetchImpl: async () => new Response("not found", { status: 404 }),
	});
	assert.equal(missing.ok, false);
	assert.match(missing.reason, /HTTP 404/);
	assert.equal(existsSync(join(dir, "missing.exe")), false);

	// No URL at all (a package with no repository) is a refusal, not an empty download.
	const nothing = await downloadExe({ url: "", dest: join(dir, "none.exe"), fetchImpl: async () => new Response("") });
	assert.equal(nothing.ok, false);
	assert.match(nothing.reason, /no release url/);
});

test("the hash gets its own deadline, and a hash that cannot be fetched refuses the install", async () => {
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-fetch-"));
	const bytes = Buffer.from("MZ pretend this is a 65 MB executable");
	const hash = createHash("sha256").update(bytes).digest("hex");

	// ONE DEADLINE PER REQUEST. These used to share a single 120 s `AbortSignal`: a slow 65 MB line
	// spent it, the `.sha256` request aborted on its first tick, the catch swallowed that, and the exe
	// was installed UNVERIFIED — the guarantee this module exists for, dropped when the line was slow.
	const signals = [];
	const ok = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "ok.exe"),
		fetchImpl: async (url, init) => {
			signals.push(init?.signal);
			return url.endsWith(".sha256") ? new Response(`${hash}  AgentsNotch.exe\n`) : new Response(bytes);
		},
	});
	assert.equal(signals.length, 2, "one request for the exe, one for its hash");
	assert.notEqual(signals[0], signals[1], "the hash must not inherit the download's deadline");
	assert.equal(ok.ok, true);
	assert.equal(ok.verified, true);

	// A hash asset that answers, but not with 200 or 404: refuse, and leave nothing installed.
	const refused = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "refused.exe"),
		fetchImpl: async (url) => (url.endsWith(".sha256") ? new Response("gateway", { status: 502 }) : new Response(bytes)),
	});
	assert.equal(refused.ok, false);
	assert.match(refused.reason, /HTTP 502/);
	assert.equal(existsSync(join(dir, "refused.exe")), false);

	// A hash asset that cannot be reached at all: same answer, and the reason says which step failed.
	const unreachable = await downloadExe({
		url: "https://example.test/AgentsNotch.exe",
		dest: join(dir, "unreachable.exe"),
		fetchImpl: async (url) => {
			if (url.endsWith(".sha256")) throw new Error("socket hang up");
			return new Response(bytes);
		},
	});
	assert.equal(unreachable.ok, false);
	assert.match(unreachable.reason, /could not be fetched/);
	assert.equal(existsSync(join(dir, "unreachable.exe")), false);
});
