/**
 * Where the window binary can be — the ONE place that knows, in order, first hit wins.
 *
 * No path in this package may name a machine (contract, "Rules of the road"), so nothing here is
 * absolute except what the developer's own build produced. The five steps exist because there are
 * five real situations, and each one has to be testable on its own:
 *
 *   1. `exePath` in the plugin row      — a developer pointing at something unusual, wins over all.
 *   2. `AGENTS_NOTCH_EXE`               — the same, without editing YAML (a shell that owns a build).
 *   3. the installed release            — `$DSH_HOME/state/agents-notch/window/<version>/`,
 *                                         where `tools/install-window.ps1` puts a release. The
 *                                         version comes from THIS package's `package.json`, so the
 *                                         host and the installer agree without a shared constant.
 *   4. the developer build              — `<package root>/desktop/bin/Release/net9.0-windows/`,
 *                                         which is what `dotnet build desktop/AgentsNotch.csproj
 *                                         -c Release` leaves behind.
 *   5. the published build              — `<package root>/dist/win-x64/`, which is what
 *                                         `npm run window:publish` leaves behind and what the
 *                                         release flow ships. Without it, a published-but-not-yet-
 *                                         installed build would only be reachable through config.
 *
 * The installed location is derived from `DSH_HOME`, NOT from a `stateDir` override: the installer
 * only ever knows `$DSH_HOME`, and a lookup that followed the config would silently stop finding an
 * installation the moment someone moved the state file. So an unusual `stateDir` still needs
 * `exePath` / `AGENTS_NOTCH_EXE` for an unusual window — and the `window-missing` report names every
 * path it tried, which is what makes that a one-line fix rather than a hunt.
 *
 * Everything here is pure except the default `existsSync`, so the whole order can be driven in a
 * test with a fake filesystem.
 *
 * @module dsh-agents-notch/exe
 */

import { existsSync, readFileSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

/** The binary's name. The .NET project is `desktop/AgentsNotch.csproj`, so this is a fact, not a guess. */
export const EXE_NAME = "AgentsNotch.exe";

/** This package's root: `lib/exe.js` -> `<root>/lib` -> `<root>`. */
export const PACKAGE_ROOT = dirname(dirname(fileURLToPath(import.meta.url)));

/** Where the product keeps its state, relative to the harness home. */
const STATE_SEGMENTS = ["state", "agents-notch"];

/**
 * `$DSH_HOME`, or `~/.dsh` when the environment does not set it.
 *
 * @param {Record<string, string | undefined>} [env]
 * @returns {string}
 */
export function dshHome(env = process.env) {
	const configured = typeof env?.DSH_HOME === "string" ? env.DSH_HOME.trim() : "";
	return configured !== "" ? configured : join(homedir(), ".dsh");
}

/**
 * The state directory the host uses by default — `$DSH_HOME/state/agents-notch`.
 *
 * @param {Record<string, string | undefined>} [env]
 * @returns {string}
 */
export function defaultStateDir(env = process.env) {
	return join(dshHome(env), ...STATE_SEGMENTS);
}

/**
 * This package's version, read from its own `package.json`.
 *
 * A version is only a directory name here, so an unreadable manifest must not stop the host: an
 * empty/`0.0.0` answer simply misses the installed location and falls through to the dev build.
 *
 * @param {string} [root]
 * @returns {string}
 */
export function packageVersion(root = PACKAGE_ROOT) {
	try {
		const manifest = JSON.parse(readFileSync(join(root, "package.json"), "utf8"));
		const version = typeof manifest?.version === "string" ? manifest.version.trim() : "";
		return version !== "" ? version : "0.0.0";
	} catch {
		return "0.0.0";
	}
}

/**
 * The installed-release location: `$DSH_HOME/state/agents-notch/window/<version>/AgentsNotch.exe`.
 *
 * @param {{ dshHome?: string, version?: string }} [options]
 * @returns {string}
 */
export function installedExePath({ dshHome: home = dshHome(), version = packageVersion() } = {}) {
	return join(home, ...STATE_SEGMENTS, "window", version, EXE_NAME);
}

/**
 * The developer-build location: `<package root>/desktop/bin/Release/net9.0-windows/AgentsNotch.exe`.
 *
 * @param {string} [root]
 * @returns {string}
 */
export function devExePath(root = PACKAGE_ROOT) {
	return join(root, "desktop", "bin", "Release", "net9.0-windows", EXE_NAME);
}

/**
 * The publisher's location: `<package root>/dist/win-x64/AgentsNotch.exe` — what
 * `npm run window:publish` produces, and what the release flow ships.
 *
 * @param {string} [root]
 * @returns {string}
 */
export function publishExePath(root = PACKAGE_ROOT) {
	return join(root, "dist", "win-x64", EXE_NAME);
}

/**
 * Every place the window can be, in the order it is looked for. Always five entries at most, each
 * naming the source that produced it so a report can say WHICH step answered.
 *
 * @param {{ exePath?: unknown, env?: Record<string, string | undefined>, dshHome?: string,
 *           version?: string, root?: string }} [options]
 * @returns {Array<{ source: "config" | "env" | "installed" | "dev" | "publish", path: string }>}
 */
export function exeCandidates({ exePath = "", env = process.env, dshHome: home = dshHome(env), version, root = PACKAGE_ROOT } = {}) {
	const candidates = [];

	const configured = typeof exePath === "string" ? exePath.trim() : "";
	if (configured !== "") candidates.push({ source: "config", path: configured });

	const fromEnv = typeof env?.AGENTS_NOTCH_EXE === "string" ? env.AGENTS_NOTCH_EXE.trim() : "";
	if (fromEnv !== "") candidates.push({ source: "env", path: fromEnv });

	candidates.push({ source: "installed", path: installedExePath({ dshHome: home, version: version ?? packageVersion(root) }) });
	candidates.push({ source: "dev", path: devExePath(root) });
	candidates.push({ source: "publish", path: publishExePath(root) });

	return candidates;
}

/**
 * The first candidate that exists, or `null` with everything that was tried.
 *
 * @param {{ exePath?: unknown, env?: Record<string, string | undefined>, dshHome?: string,
 *           version?: string, root?: string, exists?: (path: string) => boolean }} [options]
 * @returns {{ path: string | null, source: string, tried: Array<{ source: string, path: string }> }}
 */
export function findExe(options = {}) {
	const candidates = exeCandidates(options);
	const exists = typeof options.exists === "function" ? options.exists : existsSync;
	const tried = [];
	for (const candidate of candidates) {
		tried.push(candidate);
		let there = false;
		try {
			there = exists(candidate.path) === true;
		} catch {
			// A path we cannot even ask about is not a path we have.
			there = false;
		}
		if (there) return { path: candidate.path, source: candidate.source, tried };
	}
	return { path: null, source: "", tried };
}

/**
 * The one sentence that fixes a missing window, for the `window-missing` report line.
 *
 * It names the installed path as the thing to make exist (that is the normal install), the two
 * commands that produce a runnable copy, and both overrides. Nothing is guessed and nothing is
 * abbreviated: a human reading `host-report.jsonl` should not need to read this file too.
 *
 * @param {{ tried?: Array<{ source: string, path: string }> }} found
 * @returns {string}
 */
export function missingExeFix(found = { tried: [] }) {
	const tried = Array.isArray(found.tried) ? found.tried : [];
	const installed = tried.find((candidate) => candidate.source === "installed")?.path ?? installedExePath();
	// The installer's own path, spelled out. A human reading `host-report.jsonl` should be able to
	// copy the command, not go looking for where the script lives — it ships inside this package,
	// and "<profile>\node_modules\dsh-agents-notch\tools\install-window.ps1" is not something
	// anybody guesses.
	const installer = join(PACKAGE_ROOT, "tools", "install-window.ps1");
	return [
		`no ${EXE_NAME}: install the window so that ${installed} exists —`,
		`\`pwsh -File "${installer}" -Url <release asset>\`,`,
		"or build it with `dotnet build desktop/AgentsNotch.csproj -c Release`,",
		"or publish it with `npm run window:publish` (dist/win-x64),",
		`or set \`exePath\` in the plugin row / the AGENTS_NOTCH_EXE environment variable.`,
		`Looked for: ${tried.map((candidate) => `${candidate.path} (${candidate.source})`).join(", ")}`,
	].join(" ");
}
