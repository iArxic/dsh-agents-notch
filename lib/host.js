/**
 * The stable import path. Tests and readers use this name; the plugin ROW mounts host-0.2.1.js
 * through package.json's main, because the module loader caches an imported file by its URL — a fix
 * that must land without restarting the app therefore needs a new file name, and the row follows it.
 * host-0.2.0.js (the published 0.2.0 bytes) and host-0.1.9.js are kept beside it as rollback
 * entries; nothing imports them.
 */

export * from "./host-0.2.1.js";