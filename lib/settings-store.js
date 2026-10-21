/**
 * Where a notch setting actually comes from, and how the pane writes one down.
 *
 * THREE LAYERS, in this order (later wins):
 *
 *   1. the built-in defaults        — what the window does with no configuration at all;
 *   2. the plugin row's `config:`   — `cordis.patch.yml`, the file a human edits by hand;
 *   3. `<stateDir>/settings.json`   — what the Settings pane writes, one click at a time.
 *
 * The third layer exists because **a browser pane cannot safely rewrite YAML**: it would have to
 * round-trip a file it does not own, which other sessions also edit, and a single indentation
 * mistake takes the whole machine's configuration down. So the pane keeps its own small JSON file and
 * the row stays the human's file. Both are clamped by the same rules, so a value that arrives through
 * either door behaves the same, and a stale key in the JSON cannot resurrect a setting that no longer
 * exists.
 *
 * @module dsh-agents-notch/settings-store
 */

import { existsSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { join } from "node:path";

/** Every key the pane may write, and the only ones read back out of the file. */
export const SETTINGS_KEYS = Object.freeze([
	"scale",
	"screen",
	"anchor",
	"maxRows",
	"hideFullscreen",
	"opacity",
	"motion",
	"showProject",
	"showDone",
]);

export const ANCHORS = Object.freeze(["top-center", "top-left", "top-right"]);
export const MOTIONS = Object.freeze(["auto", "on", "off"]);
export const SCREENS = Object.freeze(["primary", "cursor"]);

export const DEFAULTS = Object.freeze({
	scale: 1,
	screen: "primary",
	anchor: "top-center",
	maxRows: 6,
	hideFullscreen: true,
	opacity: 1,
	motion: "auto",
	showProject: true,
	showDone: true,
});

/** `auto` / `on` / `off`, case-insensitive; anything else is `auto`. */
export function motionOf(motion) {
	const text = typeof motion === "string" ? motion.trim().toLowerCase() : "";
	return MOTIONS.includes(text) ? text : MOTIONS[0];
}

/**
 * `primary`, `cursor`, or a 1-based display number as a string. Windows numbers displays from 1, so
 * `"1"` is the primary one and `"0"` is not a display.
 */
export function screenOf(screen, fallback = DEFAULTS.screen) {
	if (typeof screen === "number" && Number.isInteger(screen) && screen >= 1) return String(screen);
	const text = typeof screen === "string" ? screen.trim().toLowerCase() : "";
	if (SCREENS.includes(text)) return text;
	if (/^[1-9]\d*$/.test(text)) return text;
	return fallback;
}

function numberOr(value, fallback) {
	if (typeof value === "number" && Number.isFinite(value)) return value;
	if (typeof value === "string" && value.trim() !== "" && Number.isFinite(Number(value))) return Number(value);
	return fallback;
}

/**
 * Clamp one candidate set of settings onto a fallback set. Invalid values fall back SILENTLY: a typo
 * in a config file, or a hand-edited JSON file, must not take the plugin down.
 *
 * @param {Record<string, any>} [raw]
 * @param {Record<string, any>} [fallback] what an absent or invalid value resolves to
 * @returns {{ scale: number, screen: string, anchor: string, maxRows: number, hideFullscreen: boolean,
 *             opacity: number, motion: string, showProject: boolean, showDone: boolean }}
 */
export function clampSettings(raw = {}, fallback = DEFAULTS) {
	const base = fallback ?? DEFAULTS;
	return {
		scale: Math.min(2, Math.max(0.75, numberOr(raw.scale, base.scale))),
		screen: screenOf(raw.screen ?? base.screen, screenOf(base.screen)),
		anchor: ANCHORS.includes(raw.anchor) ? raw.anchor : base.anchor,
		maxRows: Math.min(10, Math.max(3, Math.round(numberOr(raw.maxRows, base.maxRows)))),
		hideFullscreen: typeof raw.hideFullscreen === "boolean" ? raw.hideFullscreen : base.hideFullscreen,
		// How much of the desktop the notch sits in front of. The floor is 0.35: below that it is a
		// ghost you cannot read at the moment you need it, which defeats the product.
		opacity: Math.min(1, Math.max(0.35, numberOr(raw.opacity, base.opacity))),
		// Case-insensitive like `screen`, because a human writes `Off` in a YAML file.
		// A junk string must fall back to the layer BENEATH this one, not to `auto`: otherwise editing
		// one slider would undo a hand-written `motion: on` in the row.
		motion: MOTIONS.includes(String(raw.motion ?? "").trim().toLowerCase())
			? String(raw.motion).trim().toLowerCase()
			: MOTIONS.includes(base.motion) ? base.motion : MOTIONS[0],
		showProject: typeof raw.showProject === "boolean" ? raw.showProject : base.showProject,
		showDone: typeof raw.showDone === "boolean" ? raw.showDone : base.showDone,
	};
}

/** The path the pane's layer lives at — beside `state.json`, so it is machine state, not config. */
export function settingsFileIn(stateDir) {
	return join(stateDir, "settings.json");
}

/**
 * What the pane last wrote, with unknown or misspelled keys dropped. A torn or missing file is an
 * empty layer, never an exception: the notch works with no settings file at all.
 *
 * @param {string} file
 * @returns {Record<string, any>}
 */
export function readStore(file) {
	try {
		if (!existsSync(file)) return {};
		const parsed = JSON.parse(readFileSync(file, "utf8"));
		if (parsed === null || typeof parsed !== "object" || Array.isArray(parsed)) return {};
		const out = {};
		for (const key of SETTINGS_KEYS) if (Object.hasOwn(parsed, key)) out[key] = parsed[key];
		return out;
	} catch {
		return {};
	}
}

/**
 * The settings in force: the row's (already clamped) values with the pane's layer clamped on top.
 *
 * @param {Record<string, any>} fromRow
 * @param {Record<string, any>} fromPane
 * @returns {ReturnType<typeof clampSettings>}
 */
export function mergeSettings(fromRow, fromPane) {
	const base = clampSettings(fromRow, DEFAULTS);
	return clampSettings({ ...base, ...fromPane }, base);
}

/**
 * Apply a pane patch and write it back atomically. Only known keys are stored, and a value that does
 * not survive clamping is written in its clamped form — so the file always says what the window does.
 *
 * @param {string} file
 * @param {Record<string, any>} patch
 * @param {Record<string, any>} fromRow
 * @returns {{ stored: Record<string, any>, settings: ReturnType<typeof clampSettings> }}
 */
export function writeStore(file, patch, fromRow) {
	const stored = { ...readStore(file) };
	for (const key of SETTINGS_KEYS) {
		if (patch !== null && typeof patch === "object" && Object.hasOwn(patch, key)) stored[key] = patch[key];
	}
	const settings = mergeSettings(fromRow, stored);
	// Store the clamped values, not the raw ones: a file that disagrees with the window is a lie
	// somebody has to debug later.
	const normalised = {};
	for (const key of SETTINGS_KEYS) normalised[key] = settings[key];
	const tmp = `${file}.tmp`;
	writeFileSync(tmp, `${JSON.stringify(normalised, null, "\t")}\n`, "utf8");
	renameSync(tmp, file);
	return { stored: normalised, settings };
}

/** Clear the pane's layer — every setting falls back to the row and then to the defaults. */
export function clearStore(file) {
	try {
		writeFileSync(`${file}.tmp`, "{}\n", "utf8");
		renameSync(`${file}.tmp`, file);
	} catch {
		// Nothing to clear is not a failure.
	}
}
