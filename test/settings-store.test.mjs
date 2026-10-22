/**
 * The pane's layer: where a setting comes from, and what happens when the file is wrong.
 *
 * The rule under test is the one that keeps three layers honest — the pane's value must beat the row,
 * but a value the pane got wrong must fall back to the ROW, not to the default, or editing one slider
 * would quietly undo a hand-written config.
 *
 * Run: node test/settings-store.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import {
	ANCHORS,
	DEFAULTS,
	SETTINGS_KEYS,
	clampSettings,
	clearStore,
	mergeSettings,
	readStore,
	screenOf,
	settingsFileIn,
	writeStore,
} from "../lib/settings-store.js";

test("every value is clamped, and a wrong one falls back silently", () => {
	assert.equal(clampSettings({ scale: 9 }).scale, 2);
	assert.equal(clampSettings({ scale: 0.1 }).scale, 0.75);
	assert.equal(clampSettings({ opacity: 0.01 }).opacity, 0.35, "the floor exists so it stays readable");
	assert.equal(clampSettings({ opacity: 3 }).opacity, 1);
	assert.equal(clampSettings({ maxRows: 99 }).maxRows, 10);
	assert.equal(clampSettings({ maxRows: 1 }).maxRows, 3);
	assert.equal(clampSettings({ motion: "OFF" }).motion, "off", "a human writes Off in YAML");
	assert.equal(clampSettings({ motion: "sometimes" }).motion, "auto");
	assert.equal(clampSettings({ anchor: "middle" }).anchor, DEFAULTS.anchor);
	assert.equal(clampSettings({ showProject: "no" }).showProject, true, "only a real boolean counts");
	// A numeric display is a 1-based Windows number, and 0 is not one.
	assert.equal(screenOf(2), "2");
	assert.equal(screenOf("0"), "primary");
	assert.equal(screenOf(0), "primary");
	assert.equal(screenOf("cursor"), "cursor");
});

test("the pane beats the row, and a bad pane value falls back to the ROW, not the default", () => {
	const row = clampSettings({ scale: 1.5, motion: "on", opacity: 0.8, anchor: "top-left" });
	const merged = mergeSettings(row, { scale: 0.9, opacity: 0.5 });
	assert.equal(merged.scale, 0.9, "the pane wins");
	assert.equal(merged.opacity, 0.5);
	assert.equal(merged.motion, "on", "untouched keys keep the row's value");
	assert.equal(merged.anchor, "top-left");
	// A garbage value in the pane must not silently reset the row's choice.
	const damaged = mergeSettings(row, { scale: "banana", motion: "wobble", anchor: "middle" });
	assert.equal(damaged.scale, 1.5, "falls back to the row, not to 1");
	assert.equal(damaged.motion, "on");
	assert.equal(damaged.anchor, "top-left");
});

test("the file is read defensively and only known keys survive", () => {
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-settings-"));
	const file = settingsFileIn(dir);
	assert.deepEqual(readStore(file), {}, "no file is an empty layer, not an error");

	writeFileSync(file, "{ this is not json", "utf8");
	assert.deepEqual(readStore(file), {}, "a torn file is an empty layer too");

	writeFileSync(file, JSON.stringify({ scale: 1.25, bogus: 1, opacity: 0.6 }), "utf8");
	assert.deepEqual(readStore(file), { scale: 1.25, opacity: 0.6 }, "a misspelled key is dropped, not carried");

	writeFileSync(file, "[1,2,3]", "utf8");
	assert.deepEqual(readStore(file), {}, "an array is not a settings object");
});

test("writing stores the CLAMPED values, merges patches, and reset clears the layer", () => {
	const dir = mkdtempSync(join(tmpdir(), "agents-notch-settings-"));
	const file = settingsFileIn(dir);
	const row = clampSettings({ opacity: 0.9, scale: 1.1 });

	const first = writeStore(file, { opacity: 5 }, row);      // absurd on purpose
	assert.equal(first.settings.opacity, 1, "the answer is what the window will use");
	assert.equal(JSON.parse(readFileSync(file, "utf8")).opacity, 1, "and the file says the same, not 5");

	const second = writeStore(file, { motion: "off" }, row);
	assert.equal(second.settings.motion, "off");
	assert.equal(second.settings.opacity, 1, "a patch does not drop what an earlier patch set");
	assert.equal(second.settings.scale, 1.1, "keys the pane never touched still come from the row");

	// The file is an OVERRIDE layer, not a snapshot: it holds only the keys the pane changed.
	// Storing all nine (the 0.1.6 behaviour) froze the row's other values into this layer, so a later
	// edit to `cordis.patch.yml` silently stopped taking effect.
	const stored = JSON.parse(readFileSync(file, "utf8"));
	assert.deepEqual(Object.keys(stored).sort(), ["motion", "opacity"]);
	assert.ok(Object.keys(stored).every((key) => SETTINGS_KEYS.includes(key)), "and only known keys");
	const edited = clampSettings({ opacity: 0.9, scale: 1.6 });
	assert.equal(mergeSettings(edited, stored).scale, 1.6, "a later row edit still lands");
	assert.equal(mergeSettings(edited, stored).opacity, 1, "the pane's own value still wins");

	// A value that does not parse is refused rather than stored as its fallback: a stored fallback
	// would freeze the row's value it fell back to — the same bug in a smaller form.
	writeStore(file, { scale: "banana", anchor: "middle" }, row);
	assert.deepEqual(Object.keys(JSON.parse(readFileSync(file, "utf8"))).sort(), ["motion", "opacity"]);
	assert.equal(mergeSettings(edited, readStore(file)).scale, 1.6, "the row's scale still decides");

	assert.equal(clearStore(file), true, "a cleared layer reports success");
	assert.deepEqual(readStore(file), {});
	const after = mergeSettings(row, readStore(file));
	assert.equal(after.opacity, 0.9, "with the layer cleared, the row decides again");
	assert.equal(after.anchor, ANCHORS[0]);
});
