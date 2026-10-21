/**
 * Fetching the window binary from a release, so that installing the plugin is ONE command.
 *
 * THE PROBLEM THIS SOLVES, in the ecosystem's own terms: every other DSH plugin installs with
 * `dsh plugin --profile <p> add <name>` and nothing else, because everything it needs is JavaScript
 * in the tarball. This one needs a 65 MB native window. Shipping that inside the npm package works
 * but is rude — every version bump re-uploads 65 MB, and a plugin update downloads it again. Making
 * the user run a PowerShell script afterwards works but breaks the expectation the other plugins set,
 * which is what a human noticed first.
 *
 * So: the package stays ~30 KB, and the HOST fetches the matching release asset the first time it
 * needs a window it cannot find. The release is already versioned by the package (`v<version>`), the
 * hash is fetched next to the exe and verified before the file is given its real name, and a failure
 * is reported and then ignored — the host keeps serving state, exactly as it does when the binary is
 * simply absent. `autoDownload: false` in the row turns it off; `tools/install-window.ps1` remains
 * the offline way to do the same thing by hand.
 *
 * @module dsh-agents-notch/fetch-exe
 */

import { createWriteStream } from "node:fs";
import { mkdir, rename, rm, stat } from "node:fs/promises";
import { dirname } from "node:path";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";

import { EXE_NAME } from "./exe.js";

/** One download per process is enough; the host keeps the answer in memory. */
const TIMEOUT_MS = 120_000;

/**
 * `{owner, name}` from a `repository` field, or null.
 *
 * Accepts the shapes npm actually stores: `"git+https://github.com/a/b.git"`, `"https://github.com/a/b"`,
 * `"github:a/b"`, and the object form `{ url: … }`. A repository that is not GitHub has no release
 * asset this code can name, and guessing a URL is worse than saying "I cannot".
 *
 * @param {unknown} repository
 * @returns {{ owner: string, name: string } | null}
 */
export function githubRepoOf(repository) {
	const raw = typeof repository === "string" ? repository
		: repository !== null && typeof repository === "object" && typeof repository.url === "string" ? repository.url
		: "";
	const text = raw.trim();
	if (text === "") return null;
	const shorthand = /^github:([^/]+)\/([^/#]+?)(?:\.git)?$/i.exec(text);
	if (shorthand) return { owner: shorthand[1], name: shorthand[2] };
	const match = /github\.com[/:]([^/]+)\/([^/#]+?)(?:\.git)?(?:#.*)?$/i.exec(text);
	return match ? { owner: match[1], name: match[2] } : null;
}

/**
 * Where this version's window binary lives. `null` when the manifest names no GitHub repository,
 * because an invented URL would 404 in a way that reads like "the plugin is broken".
 *
 * @param {{ version?: string, repository?: unknown, asset?: string }} [options]
 * @returns {string | null}
 */
export function releaseAssetUrl({ version, repository, asset = EXE_NAME } = {}) {
	const repo = githubRepoOf(repository);
	const tag = typeof version === "string" ? version.trim() : "";
	if (repo === null || tag === "" || tag === "0.0.0") return null;
	return `https://github.com/${repo.owner}/${repo.name}/releases/download/v${tag}/${asset}`;
}

/**
 * Download `url` (and `<url>.sha256` when the release publishes one) to `dest`.
 *
 * Atomic on purpose: the bytes land in `<dest>.part`, are hashed, and only then take their real name.
 * A half-downloaded executable that looks installed is worse than no executable — the host would keep
 * spawning a file that dies before it draws, which is the exact failure mode the bundle-extract fix
 * already cost a day to diagnose.
 *
 * @param {{ url: string, dest: string, fetchImpl?: typeof fetch, timeoutMs?: number }} options
 * @returns {Promise<{ ok: boolean, reason?: string, bytes?: number, verified?: boolean }>}
 */
export async function downloadExe({ url, dest, fetchImpl = globalThis.fetch, timeoutMs = TIMEOUT_MS }) {
	if (typeof fetchImpl !== "function") return { ok: false, reason: "no fetch in this runtime" };
	if (typeof url !== "string" || url === "") return { ok: false, reason: "no release url for this version" };

	const part = `${dest}.part`;
	const signal = typeof AbortSignal?.timeout === "function" ? AbortSignal.timeout(timeoutMs) : undefined;
	try {
		await mkdir(dirname(dest), { recursive: true });
		const response = await fetchImpl(url, { signal, redirect: "follow" });
		if (!response.ok) return { ok: false, reason: `HTTP ${response.status} for ${url}` };
		await pipeline(Readable.fromWeb(response.body), createWriteStream(part));

		// The hash, if the release has one. A release that publishes an exe without a hash is not
		// refused (there is nothing to check it against) — but it is reported, once.
		let verified = false;
		try {
			const hashResponse = await fetchImpl(`${url}.sha256`, { signal, redirect: "follow" });
			if (hashResponse.ok) {
				const text = (await hashResponse.text()).trim();
				const expected = (text.split(/\s+/)[0] || "").toLowerCase();
				const { createHash } = await import("node:crypto");
				const { readFile } = await import("node:fs/promises");
				const actual = createHash("sha256").update(await readFile(part)).digest("hex");
				if (!/^[0-9a-f]{64}$/.test(expected)) return { ok: false, reason: "the release's .sha256 is not a hash" };
				if (expected !== actual) {
					await rm(part, { force: true });
					return { ok: false, reason: `sha256 mismatch (expected ${expected.slice(0, 12)}…, got ${actual.slice(0, 12)}…)` };
				}
				verified = true;
			}
		} catch {
			// No hash asset, or a network blip while asking for it: continue unverified.
		}

		await rename(part, dest);
		const { size } = await stat(dest);
		return { ok: true, bytes: size, verified };
	} catch (error) {
		await rm(part, { force: true }).catch(() => {});
		return { ok: false, reason: String(error?.message ?? error).slice(0, 200) };
	}
}
