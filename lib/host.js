/**
 * The stable import path. Tests and readers use this name; the plugin ROW mounts `host-0.1.8.js`
 * through package.json's `main`, because the module loader caches an imported file by its URL — a fix
 * that must land without restarting the app therefore needs a new file name, and the row follows it.
 */

export * from "./host-0.1.8.js";
