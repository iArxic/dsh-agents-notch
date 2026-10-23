/**
 * Browser half: the "Agents Notch" page in the app's Settings list.
 *
 * WHY THIS FILE EXISTS. Every entry in that sidebar (Roblox & Argon, Notify me, Side card) is a
 * client bundle filling the `settings.section` seat. The notch had none, so its settings could only be
 * the plugin row's `config:` in `cordis.patch.yml` — which is fine for a human who edits YAML and
 * useless for one who expects a pane. This registers the pane; `lib/settings-store.js` on the host
 * owns where the value lives and how it is clamped.
 *
 * THE FORMAT is the module-loader's bundle shape (the same one a built client bundle has): the shell's
 * combo script calls `window.__ModuleLoader__.load`, which registers a factory; `require` resolves
 * against the frozen platform table, so `react` is the shell's own React instance and nothing is
 * bundled twice.
 *
 * NO PRIMITIVES, DELIBERATELY. `@deepseek-ai/dsh-client-ui-primitives` is loaded optionally elsewhere;
 * this pane uses plain elements and inherited colours so it reads correctly in the dark theme and in a
 * light one, without depending on a token catalogue that could rename itself.
 *
 * THE THREE SEATS ARE FIXED: `slots.inject` waits for the declaration, `slots.register` contributes to
 * it, and the disposer is handed to `ctx.effect` so unloading the plugin takes the pane out again.
 */
window.__ModuleLoader__.load({
	id: "dsh-agents-notch",
	factory: (require) => {
		var module = { exports: {} };
		var exports = module.exports;

		const React = require("react");
		const h = React.createElement;

		/** Where the host keeps this pane's door. Same origin: a plugin route, not a cookie-gated API. */
		const ROUTE = "/agents-notch/settings";
		/** Where the notch leaves "open this session" for this half to take (host: the open door). */
		const OPEN_ROUTE = "/agents-notch/open";
		/** How often the open door is asked. A click should feel immediate; a local GET is cheap. */
		const OPEN_POLL_MS = 400;

		/** The controls, in the order they appear. `kind` decides the widget, `note` the one-liner. */
		const FIELDS = [
			{ key: "scale", label: "Size", kind: "range", min: 0.75, max: 2, step: 0.05, note: "0.75× to 2×, the whole surface." },
			{ key: "anchor", label: "Anchor", kind: "choice", options: ["top-center", "top-left", "top-right"], note: "Top centre is the design; the other two are for a busy second monitor." },
			{ key: "screen", label: "Display", kind: "choice", options: ["primary", "cursor", "1", "2", "3"], note: "Windows numbers displays from 1. `cursor` follows the pointer." },
			{ key: "maxRows", label: "Rows", kind: "number", min: 3, max: 10, step: 1, note: "3 to 10 sessions visible; more scroll." },
			{ key: "opacity", label: "Opacity", kind: "range", min: 0.35, max: 1, step: 0.05, note: "0.35 is the floor: below that it is a ghost you cannot read when it matters." },
			{ key: "motion", label: "Motion", kind: "choice", options: ["auto", "on", "off"], note: "`auto` follows Windows' reduce-motion setting. `off` draws each state's settled face and nothing else." },
			{ key: "hideFullscreen", label: "Hide under a full-screen app", kind: "check" },
			{ key: "showProject", label: "Project name in the sub line", kind: "check" },
			{ key: "showDone", label: "Show finished sessions", kind: "check", note: "Applied by the host, so the tab's count always agrees with the rows." },
		];

		function note(text) {
			return text ? h("div", { style: { opacity: 0.6, fontSize: 12, marginTop: 2 } }, text) : null;
		}

		function row(field, value, onChange) {
			const control = (() => {
				if (field.kind === "check") {
					return h("input", {
						type: "checkbox",
						checked: value === true,
						onChange: (event) => onChange(field.key, event.target.checked),
					});
				}
				if (field.kind === "choice") {
					return h(
						"select",
						{
							value: String(value),
							onChange: (event) => onChange(field.key, event.target.value),
							style: { background: "transparent", color: "inherit", border: "1px solid rgba(127,127,127,.35)", borderRadius: 6, padding: "2px 6px" },
						},
						...field.options.map((option) => h("option", { key: option, value: option }, option)),
					);
				}
				if (field.kind === "number") {
					return h("input", {
						type: "number",
						value: Number(value),
						min: field.min,
						max: field.max,
						step: field.step,
						onChange: (event) => onChange(field.key, Number(event.target.value)),
						style: { width: 64, background: "transparent", color: "inherit", border: "1px solid rgba(127,127,127,.35)", borderRadius: 6, padding: "2px 6px" },
					});
				}
				return h("input", {
					type: "range",
					value: Number(value),
					min: field.min,
					max: field.max,
					step: field.step,
					onChange: (event) => onChange(field.key, Number(event.target.value)),
					style: { width: 160, accentColor: "currentColor" },
				});
			})();
			// A checkbox reads left-to-right; everything else right-aligns its control so the labels
			// form a column.
			return h(
				"label",
				{
					key: field.key,
					style: { display: "flex", alignItems: "center", justifyContent: "space-between", gap: 16, padding: "7px 0", borderBottom: "1px solid rgba(127,127,127,.14)" },
				},
				h("div", { style: { display: "flex", flexDirection: "column" } }, h("span", {}, field.label), note(field.note)),
				control,
			);
		}

		function SettingsPage() {
			const [settings, setSettings] = React.useState(null);
			const [error, setError] = React.useState("");
			const [saved, setSaved] = React.useState("");
			const timer = React.useRef(null);

			const load = React.useCallback(() => {
				fetch(ROUTE, { headers: { accept: "application/json" } })
					.then((response) => (response.ok ? response.json() : Promise.reject(new Error(`HTTP ${response.status}`))))
					.then((body) => {
						setSettings(body.settings);
						setError("");
					})
					.catch((failure) => setError(`could not read the settings: ${failure.message}`));
			}, []);

			React.useEffect(() => {
				load();
				return () => {
					if (timer.current) clearTimeout(timer.current);
				};
			}, [load]);

			// One request per settled change rather than one per pixel of a slider drag.
			const change = React.useCallback((key, value) => {
				setSettings((previous) => (previous ? { ...previous, [key]: value } : previous));
				if (timer.current) clearTimeout(timer.current);
				timer.current = setTimeout(() => {
					fetch(ROUTE, {
						method: "POST",
						headers: { "content-type": "application/json" },
						body: JSON.stringify({ [key]: value }),
					})
						.then((response) => (response.ok ? response.json() : response.json().then((body) => Promise.reject(new Error(body.error ?? `HTTP ${response.status}`)))))
						.then((body) => {
							// The host answers with what it actually stored — clamped. Trusting the echo is
							// what keeps the pane from showing a value the window is not using.
							setSettings(body.settings);
							setError("");
							setSaved("saved");
							setTimeout(() => setSaved(""), 1200);
						})
						.catch((failure) => setError(failure.message));
				}, 160);
			}, []);

			const reset = React.useCallback(() => {
				fetch(ROUTE, { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify({ reset: true }) })
					.then((response) => (response.ok ? response.json() : Promise.reject(new Error(`HTTP ${response.status}`))))
					.then((body) => {
						setSettings(body.settings);
						setSaved("reset to the row's values");
						setTimeout(() => setSaved(""), 1600);
					})
					.catch((failure) => setError(failure.message));
			}, []);

			if (error !== "" && settings === null) {
				return h("div", { style: { padding: 16, fontSize: 13 } }, error);
			}
			if (settings === null) {
				return h("div", { style: { padding: 16, fontSize: 13, opacity: 0.6 } }, "Reading the notch's settings…");
			}

			return h(
				"div",
				{ style: { padding: "4px 2px 16px", fontSize: 13, maxWidth: 560 } },
				h(
					"p",
					{ style: { opacity: 0.7, margin: "0 0 10px" } },
					"An always-on-top notch showing which DeepSeek Harness sessions are working, which need you, and which are done. Changes apply immediately — the window re-reads them on its next tick, no restart.",
				),
				...FIELDS.map((field) => row(field, settings[field.key], change)),
				h(
					"div",
					{ style: { display: "flex", alignItems: "center", gap: 12, marginTop: 12 } },
					h(
						"button",
						{
							type: "button",
							onClick: reset,
							style: { background: "transparent", color: "inherit", border: "1px solid rgba(127,127,127,.35)", borderRadius: 6, padding: "4px 10px", cursor: "pointer" },
						},
						"Reset",
					),
					h("span", { style: { opacity: 0.6, fontSize: 12 } }, saved),
					error ? h("span", { style: { color: "#e08a7a", fontSize: 12 } }, error) : null,
				),
				h(
					"p",
					{ style: { opacity: 0.5, fontSize: 12, marginTop: 12 } },
					`Saved to state/agents-notch/settings.json, which overrides the plugin row's config in cordis.patch.yml. Reset clears that file so the row — and then the defaults — decide again.`,
				),
			);
		}

		function apply(ctx) {
			const slots = ctx.get("slots");
			if (!slots || typeof slots.inject !== "function") return;
			const disposers = [];
			// `slots.inject` waits for the declaration, so this needs no package-order edge on the
			// settings plugin; with no settings panel composed, the registration simply never mounts.
			disposers.push(
				slots.inject("settings.section", () =>
					slots.register(
						{
							name: "settings.section",
							id: "dsh-agents-notch:settings",
							order: 40,
							label: "Agents Notch",
							registrant: "dsh-agents-notch",
						},
						() => h(SettingsPage, {}),
					),
				),
			);
			// "Open" on the notch: the window cannot reach the renderer, so it leaves the session id with
			// the host, and this half — which lives in the app — takes it and switches to that session the
			// way the app's own modules do. Polled, because a plugin route has no push to the page.
			//
			// `uiWorkspace` arrives through `ctx.inject`, the way the app's own client modules receive it
			// (ui-agent-preset, ui-subagent). The first version read `ctx.get("uiWorkspace")`, the intent was
			// taken and nothing switched (2026-10-23) — so every outcome is now reported back to the host,
			// where `host-report.jsonl` shows it as `open-result`.
			let workspace = null;
			if (typeof ctx.inject === "function") {
				ctx.inject(["uiWorkspace"], (scope) => {
					workspace = scope?.uiWorkspace ?? null;
				});
			}
			const tell = (sessionId, result) =>
				fetch(OPEN_ROUTE, {
					method: "POST",
					headers: { "content-type": "application/json" },
					body: JSON.stringify({ sessionId, result: String(result).slice(0, 200) }),
				}).catch(() => {});
			let busy = false;
			const timer = setInterval(() => {
				if (busy) return;
				busy = true;
				fetch(OPEN_ROUTE, { headers: { accept: "application/json" }, cache: "no-store" })
					.then((response) => (response.ok ? response.json() : null))
					.then((body) => {
						const id = typeof body?.sessionId === "string" ? body.sessionId : "";
						if (id === "") return;
						const target = workspace ?? (typeof ctx.get === "function" ? ctx.get("uiWorkspace") : null) ?? ctx.uiWorkspace;
						if (!target || typeof target.openSession !== "function") return tell(id, "no uiWorkspace in this context");
						try {
							target.openSession(id);
							return tell(id, workspace ? "opened" : "opened (through ctx.get)");
						} catch (error) {
							return tell(id, `openSession threw: ${error?.message ?? error}`);
						}
					})
					.catch(() => {})
					.finally(() => {
						busy = false;
					});
			}, OPEN_POLL_MS);
			disposers.push(() => clearInterval(timer));
			if (typeof ctx.effect === "function") {
				for (const dispose of disposers) ctx.effect(() => dispose, "dsh-agents-notch: settings section");
			}
		}

		exports.apply = apply;
		exports.inject = ["slots"];
		// For a test that wants to render without the shell's slot runtime.
		exports.internals = { FIELDS, ROUTE, SettingsPage };
		return module.exports;
	},
});
