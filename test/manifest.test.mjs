/**
 * The manifest is a runtime contract, and one of its failures is silent.
 *
 * Measured 2026-10-21: package.json ended up with TWO `exports` blocks. JSON keeps the last one, which
 * had no `./client`, so `dsh-client-modules` could not resolve the browser half — and the symptom was
 * not an error. The package installed, the plugin mounted, the host route answered, and the Settings
 * pane simply never appeared. npm packed it happily, `npm publish` reported success, and no test
 * looked at the file.
 *
 * So this file checks the things a consumer reads, textually where JSON would hide the mistake:
 *   - exactly one `exports` block, and a `./client` entry that names a file that exists;
 *   - `dsh.client.platform`, `main`, the bundle patch and the installer all resolve;
 *   - the version is a plain x.y.z (the release tag is derived from it by `tools/publish-release.ps1`).
 *
 * Run: node test/manifest.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, "..");
const text = readFileSync(join(root, "package.json"), "utf8");
const manifest = JSON.parse(text);

test("there is exactly ONE exports block — a duplicate silently deletes the browser half", () => {
	const blocks = (text.match(/"exports"\s*:/g) ?? []).length;
	assert.equal(blocks, 1, `package.json declares "exports" ${blocks} times; JSON keeps the last, so every key in the earlier one is lost`);
});

test("the browser half is declared, and the file it names exists", () => {
	assert.equal(manifest.dsh?.client?.platform, "web", "dsh.client.platform is what makes dsh-client-modules look for a browser half");
	assert.equal(manifest.exports?.["./client"]?.default, "./lib/client.js");
	assert.equal(existsSync(join(root, "lib/client.js")), true, "exports[./client] points at a file that does not exist");
	// Both halves must be importable by name from the package root.
	assert.equal(manifest.exports?.["."]?.default, manifest.main, "the package root export and `main` disagree");
});

test("the entry the row mounts is the one this package publishes", () => {
	assert.equal(typeof manifest.main, "string");
	assert.equal(existsSync(join(root, manifest.main)), true, `main ${manifest.main} does not exist`);
	assert.equal(existsSync(join(root, "lib/host.js")), true, "the stable import path (lib/host.js) is missing");
});

test("every file a consumer is promised is really shipped", () => {
	// `files` is a whitelist: anything not matched by it is silently absent from the tarball, so each
	// path that the docs or the runtime depend on is named explicitly here.
	const promised = [
		"lib/client.js",
		"lib/settings-store.js",
		"lib/fetch-exe.js",
		"lib/exe.js",
		"lib/state.js",
		"cordis.patch.yml",
		"tools/install-window.ps1",
		"docs/CONTRACT.md",
		"docs/hero.png",
		"CHANGELOG.md",
		"README.md",
		"LICENSE",
	];
	for (const file of promised) assert.equal(existsSync(join(root, file)), true, `${file} is promised but missing`);
	assert.ok(manifest.files.includes("lib"), "the whole lib/ must ship, or a rename breaks an install");
	assert.ok(manifest.files.includes("cordis.patch.yml"), "the bundle patch is what mounts the plugin");
});

test("the version is a plain x.y.z — the release tag is derived from it", () => {
	assert.match(manifest.version, /^\d+\.\d+\.\d+$/, "tools/publish-release.ps1 tags v<version>, so anything else cannot be released");
	// A prerelease would need a different tag shape and would make `npm view` disagree with the asset.
	const csproj = readFileSync(join(root, "desktop", "AgentsNotch.csproj"), "utf8");
	assert.match(csproj, new RegExp(`<Version>${manifest.version.replaceAll(".", "\\.")}</Version>`), "the window's csproj version drifted from the package");
});
