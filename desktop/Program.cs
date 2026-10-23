// AgentsNotch — the always-on-top notch.
//
//   AgentsNotch.exe [--state <file>] [--log <file>] [--shot <file>] [--open]
//                   [--probe-rect] [--demo] [--force-show] [--no-tray] [--capture-exclude]
//                   [--rule any|games] [--hide-tab-fullscreen] [--parent-pid <pid>]
//                   [--pin <n>] [--dismiss <n>] [--freeze <ms>] [--fps <n>] [--no-effects]
//                   [--ring <0..1>] [--arrive <0..1>] [--fold <0..1>]
//                   [--record <dir>] [--record-ms <ms>] [--open-at <ms>] [--close-at <ms>]
//                   [--face <name>] [--island x1|x4|d1]
//
// The shape the human approved: a slim tab hangs from the top of the screen at all times and
// carries the summary; the board expands under it on hover, with an animation. The window sits
// at the top of the monitor chosen by `settings.screen` and is NOT draggable (it was — you
// dragged it by accident).
//
// The window reads exactly one file — `--state` — and draws what it says. That file's shape is
// docs/CONTRACT.md (schema 3), frozen; the rules it enforces on this side are:
//
//   · `context.budget` is a number or null. null means the host does NOT know the route's window,
//     and then this window shows the token count and NO bar, NO percentage and NO amber. A
//     percentage against a guessed denominator is a lie that looks like information.
//   · `wallMs` is the heartbeat. More than 4 s behind this machine's clock is the host being gone
//     or wedged, and the tab says so instead of repeating the last summary as though it were live.
//   · `settings` are the host's. This window applies them, clamps them, and remembers nothing.
//   · `retire` is a fact this window does not draw: "timer" (settled rows age out because the
//     harness's close-out verdict is unavailable) only appears in the log.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AgentsNotch;

internal static class Log
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    static string? _file;

    public static void UseFile(string path)
    {
        try { _file = path; File.WriteAllText(path, "[agents-notch] start" + Environment.NewLine); } catch { }
    }

    public static void Attach()
    {
        // Only borrow the parent console when stderr was not already redirected — AttachConsole
        // replaces the standard handles, so attaching to a redirected stream silently eats the log.
        try { if (!Console.IsErrorRedirected) AttachConsole(-1); } catch { /* no parent console */ }
    }

    public static void Write(string message)
    {
        var line = "[agents-notch] " + message + Environment.NewLine;
        try { Console.Error.Write(line); Console.Error.Flush(); } catch { }
        if (_file is not null) { try { File.AppendAllText(_file, line); } catch { } }
    }
}

internal sealed class Options
{
    public string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "state", "agents-notch", "state.json");
    public string HideRule = "any";        // any | games
    public bool CaptureExclude;
    public bool Tray = true;
    public bool Demo;                       // ignore the state file and draw the demo board
    public bool ForceShow;                 // ignore the full-screen hide (demo / debugging)
    public bool Open;                      // start with the board unfolded, so it can be inspected
    public int DesiredFps;                 // --fps N: cap the motion's frame rate (0 = the default rule)
    public bool NoEffects;                 // --no-effects: measurement knob for the cost of the glows
    public double FreezeMs = -1;           // pin the motion at this many ms, for inspection
    public bool HideTabFullscreen;         // hide EVERYTHING while a full-screen app is up (overrides settings)
    public string? LogPath;
    public string? ShotPath;                // render what the window draws to a PNG, then exit
    public bool ProbeRect;                  // print the resolved placement as one JSON line, then exit
    /// <summary>
    /// `--parent-pid <pid>`: the harness process that spawned this window. When it goes away — by
    /// quitting, or by being hard-killed, which is the case this exists for — the notch exits
    /// instead of outliving its host on screen. 0 means "no watch", and an unparsable value is 0.
    /// </summary>
    public int ParentPid;
    public int PinIndex = -1;               // pin the Nth row at startup, as a click would
    public int DismissIndex = -1;           // remove the Nth row at startup, as the ✕ would
    // the three motions, pinned at a fraction of their run so a still frame can prove them
    public double Ring = -1;                // ring sweep 0..1 on every tab bot
    public double Arrive = -1;              // bot arrival 0..1
    public double Fold = -1;                // row fold 0..1 (needs --dismiss)
    /// <summary>
    /// `--record <dir>`: every frame the motion draws, as `f0000.png`… plus `frames.tsv` (frame,
    /// ms since start), then exit after `--record-ms`. The README's GIF is cut from these, so it
    /// shows the window's own motion rather than a re-enactment of it.
    /// </summary>
    public string? RecordDir;
    public double RecordMs = 6000;
    // the unfold a hover would cause, on a schedule — a recording has no pointer to hover with
    public double OpenAtMs = -1;
    public double CloseAtMs = -1;
    /// <summary>`--face <name>`: the big bot wears this face whatever the state says — for stills.</summary>
    public Face? ForceFace;
    /// <summary>`--island x1|x4|d1`: hold the tab expanded as that alert, for stills.</summary>
    public string? Island;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--state": if (i + 1 < args.Length) o.StatePath = args[++i]; break;
                case "--rule": if (i + 1 < args.Length) o.HideRule = args[++i]; break;
                case "--capture-exclude": o.CaptureExclude = true; break;
                case "--no-tray": o.Tray = false; break;
                case "--demo": o.Demo = true; break;
                case "--force-show": o.ForceShow = true; break;
                case "--open": o.Open = true; break;
                case "--probe-rect": o.ProbeRect = true; break;
                case "--parent-pid": if (i + 1 < args.Length && int.TryParse(args[++i], out var pp) && pp > 0) o.ParentPid = pp; break;
                case "--freeze": if (i + 1 < args.Length && double.TryParse(args[++i], out var fz)) o.FreezeMs = fz; break;
                case "--ring": if (i + 1 < args.Length && double.TryParse(args[++i], out var rg)) o.Ring = rg; break;
                case "--arrive": if (i + 1 < args.Length && double.TryParse(args[++i], out var ar)) o.Arrive = ar; break;
                case "--fold": if (i + 1 < args.Length && double.TryParse(args[++i], out var fd)) o.Fold = fd; break;
                case "--hide-tab-fullscreen": o.HideTabFullscreen = true; break;
                case "--log": if (i + 1 < args.Length) o.LogPath = args[++i]; break;
                case "--fps": if (i + 1 < args.Length && int.TryParse(args[++i], out var fps)) o.DesiredFps = fps; break;
                case "--no-effects": o.NoEffects = true; break;
                case "--shot": if (i + 1 < args.Length) o.ShotPath = args[++i]; break;
                case "--record": if (i + 1 < args.Length) o.RecordDir = args[++i]; break;
                case "--record-ms": if (i + 1 < args.Length && double.TryParse(args[++i], out var rms)) o.RecordMs = rms; break;
                case "--open-at": if (i + 1 < args.Length && double.TryParse(args[++i], out var oa)) o.OpenAtMs = oa; break;
                case "--close-at": if (i + 1 < args.Length && double.TryParse(args[++i], out var ca)) o.CloseAtMs = ca; break;
                case "--face":
                    if (i + 1 >= args.Length) { Log.Write("--face: needs a face name"); break; }
                    if (FaceArt.TryParse(args[++i], out var face)) o.ForceFace = face;
                    else Log.Write("--face: unknown face " + args[i]);
                    break;
                case "--island": if (i + 1 < args.Length) o.Island = args[++i].ToLowerInvariant(); break;
                case "--pin": if (i + 1 < args.Length && int.TryParse(args[++i], out var pin)) o.PinIndex = pin; break;
                case "--dismiss": if (i + 1 < args.Length && int.TryParse(args[++i], out var dis)) o.DismissIndex = dis; break;
                default: Log.Write("unknown argument: " + args[i]); break;
            }
        }
        return o;
    }
}

/// <summary>
/// One row under the board's headline (A1, round 12): every session except the one on the big bot.
/// `Right` is what it is doing in a word — its tool, `waiting`, `done`, `failed` — and `Age` is kept
/// out of the content key, because it changes every second and must not rebuild the rows.
/// </summary>
internal sealed record RowItem(string Id, string Title, string Status, string Right, bool RightMono, string Age,
    Color Identity, Face Face, bool Teammate, string Tail);

/// <summary>
/// The headline's third line: what the front session is doing, in the state's own colour.
/// `Kind` is attention | tool | thinking | working | error | done | stale | idle; `Lead` is the
/// coloured part, `Rest` the quiet part, `Action` the one thing you can do from here.
/// </summary>
internal sealed record HeadLine(string Kind, string Lead, string Rest, string Action);

/// <summary>One session's row, exactly as the host plugin wrote it.</summary>
internal sealed record SessionRow(
    string Id,
    string Title,
    string Project,
    string Cwd,
    string Status,
    long Since,
    string? ApprovalTool,
    string? ApprovalReason,
    string? ActivityKind,
    string? ActivityLabel,
    int Steps,
    string Identity,
    /// <summary>`"subagent"` when the harness delegated this session, `""` when the human opened it.</summary>
    string Origin = "",
    /// <summary>The session that delegated it — empty for a top-level session.</summary>
    string Parent = "",
    /// <summary>Everything the session's last step put through its context, in tokens.</summary>
    int ContextTokens = 0,
    /// <summary>Schema 4: tool results in a row that came back as errors (0 on a schema-3 host).</summary>
    int ErrorStreak = 0,
    /// <summary>Schema 4: when the human last allowed an approval, ms; 0 when never or unknown.</summary>
    long ApprovedAt = 0);

/// <summary>The board's own summary: the worst state among the rows, and how many are working.</summary>
internal sealed class SummaryRow
{
    public string Status = "idle";
    public int Working, Attention, Done, Error, Total;
}

/// <summary>What the state file said, parsed — docs/CONTRACT.md schema 3, and nothing invented.</summary>
internal sealed class StateFile
{
    public SummaryRow Summary = new();
    public List<SessionRow> Sessions = new();
    public long WallMs;
    public int HostPid;

    /// <summary>
    /// `"route"` | `"timer"` — how a settled row goes away. `"timer"` means the harness's own
    /// close-out verdict is unavailable, so settled rows age out instead. NOTHING is drawn
    /// differently (CONTRACT: `retire` is a fact, not a visual mode); the log names it.
    /// </summary>
    public string Retire = "route";

    /// <summary>
    /// The human's settings, from the plugin row's config. The window applies them, clamps them and
    /// remembers nothing — see Settings.cs.
    /// </summary>
    public Settings Settings = new();

    /// <summary>
    /// The route's context window in tokens, or **null** when the host cannot establish it.
    ///
    /// Null is not "unknown, use a sensible number": it is the honesty rule (CONTRACT #1). A
    /// denominator this process guessed would make a percentage that reads exactly like
    /// information and means nothing, so with null the readout is the token count and no bar.
    /// </summary>
    public int? ContextBudget;
    public double ContextHotAt = .5;

    /// <summary>
    /// Absolute URL of the tick route, published by the host. The window POSTs here when the human
    /// removes a row — the same call the sidebar's ✓ makes, so there is one record of "finished".
    /// Null when the host does not publish it (schema 3 does not require it), and then the ✕ falls
    /// back to writing the same mark into the human's own store.
    /// </summary>
    public string? FinishedUrl;

    /// <summary>The host's "open this session" door (schema 4, optional); null when not published.</summary>
    public string? OpenUrl;
}

internal sealed class BoardData
{
    // the overall tone — the summary
    public string Status = "idle";
    public int Working;
    public int Attention;
    public int Done;
    public int Error;

    /// <summary>
    /// True when the host has stopped answering: `wallMs` is more than 4 s behind this machine's
    /// clock (CONTRACT #3), or no state has ever been read. The tab keeps its position and says the
    /// host is unknown — it never repeats the last summary as though it were current.
    /// </summary>
    public bool Stale;

    // the front session — the big orb
    public string FrontStatus = "idle";
    /// <summary>
    /// The identity of the front session: id AND status. It — not the status alone — is what the
    /// motion keys on, because switching to a DIFFERENT session in the SAME state is a transition too:
    /// click from one finished session to another and the finish motion plays again, which is what a
    /// human means by "show me that one". Keying on the status made every same-state switch silent.
    /// </summary>
    public string FrontKey = "idle";
    public Color Badge = Draw.Grey;
    public Color Identity = Draw.Teal;

    public string Title = "";
    public string Sub = "";
    public List<RowItem> Rows = new();
    public HeadLine Head = new("idle", "", "", "");

    // the shape of the live parts
    public int SessionCount;                      // how many rows the state file carried
    public List<Color> MiniStates = new();        // the tab's bots: each session's state colour
    public List<Color> MiniIdentities = new();    // …and each session's identity hue
    public List<string> MiniStatus = new();       // …and the state itself, which the ring keys on
    public List<Face> MiniFaces = new();          // …and the face it rests in (Live.FaceOf)

    /// <summary>The front session's row as the host wrote it — null when nothing is on the board.</summary>
    public SessionRow? Front;
    /// <summary>The face the front session rests in; the motion plays over it.</summary>
    public Face FrontFace = Face.Idle;
    /// <summary>Is any session running a tool right now? The tab's shimmer (L2) says so.</summary>
    public bool AnyTool;
    public int MiniOverflow;                     // sessions past the fourth

    // ── the front session's context, drawn under its bot (round 10, D1 + D6) ────
    //
    // Rows are silent by default, so this is the only place a token figure appears. The number is the
    // WHOLE last step — prompt (cached and uncached) plus answer — which is how full the context is
    // and the one figure on the board that predicts a compaction. `usage.inputTokens` is NOT that
    // number: it is only the slice that missed the cache.
    public int ContextTokens;

    /// <summary>
    /// The budget the bar is drawn against, and where it turns amber. **Null** when the host does not
    /// know the route's window — and then there is no denominator, so there is no bar, no percentage
    /// and no amber: just the token count. There is deliberately no legacy constant here any more.
    /// </summary>
    public int? ContextBudget;
    public double ContextHotAt = .5;

    /// <summary>Does this board have a denominator to draw a bar against? No bar without one.</summary>
    public bool HasBudget => ContextBudget is int budget && budget > 0;

    public double ContextFill => !HasBudget || ContextTokens <= 0
        ? 0
        : Math.Clamp(ContextTokens / (double)ContextBudget!.Value, 0, 1);

    /// <summary>Amber means "this session is about to lose its history" — so it needs a budget.</summary>
    public bool ContextHot => HasBudget && ContextFill >= ContextHotAt;

    /// <summary>
    /// A percentage when, and only when, there is a real denominator: `23%`. With no budget the
    /// honest figure is the count itself — `412k` — which needs nothing guessed.
    /// </summary>
    public string ContextText => ContextTokens <= 0
        ? ""
        : HasBudget ? $"{Math.Round(ContextFill * 100)}%" : Tokens(ContextTokens);

    /// <summary>`412034` → `412k`; under a thousand, the number itself.</summary>
    internal static string Tokens(int tokens) =>
        tokens < 1000 ? tokens.ToString() : $"{Math.Round(tokens / 1000.0)}k";

    /// <summary>Changes when the ELEMENT STRUCTURE changes — the whole board is rebuilt.</summary>
    public string ShapeKey = "";

    /// <summary>Changes when the STATIC CONTENT changes — the task lines and the chips.</summary>
    public string ContentKey = "";

    /// <summary>How bright the rim breathes: idle is calm, attention is a flare.</summary>
    public double RimBase => Status switch
    {
        "attention" => 1.0,
        "error" => 1.0,
        "working" => .7,
        "done" => .45,
        _ => .3,
    };

    /// <summary>
    /// What the slim tab says — as runs, because it is not one colour or one weight.
    ///
    /// The shape is the approved G1 design: the count is heavy and the word labelling it steps back,
    /// "finished" is the tick the rest of this design already uses for settled, and an URGENT line
    /// (needs you / failed) takes its alarm colour in the one weight that reads at 10 px.
    ///
    /// A pending approval always wins, because it is the one thing the human can act on; a failure
    /// is next. After that the COUNT OF WORKING SESSIONS leads — the tab is a glance, and a finished
    /// agent must not hide three that are still running. But a finished agent is still NAMED, not
    /// just left to its little green bot: with the row dropped on the spot the tab went straight back
    /// to "3 working" and the session that had just finished was invisible everywhere.
    /// </summary>
    public IReadOnlyList<TabRun> TabRuns
    {
        get
        {
            // R1 (round 12): the urgent word still leads in its alarm colour, and the counts follow it
            // instead of disappearing — "needs you · 1 working · ✓ 1". Every run keeps the exact face
            // the tab has always used; only the composition grew.
            if (Stale) return [new TabRun("host offline", 10, Draw.Mute, FontWeights.SemiBold)];
            var groups = new List<TabRun[]>();
            if (Attention > 0) groups.Add([new TabRun("needs you", 10, Draw.TabAmber, FontWeights.SemiBold)]);
            if (Error > 0) groups.Add([new TabRun(Error == 1 ? "failed" : $"{Error} failed", 10, Draw.TabRed, FontWeights.SemiBold)]);
            if (Working > 0) groups.Add(
            [
                new($"{Working}", 11, Draw.TabInk, FontWeights.SemiBold),
                new(" working", 10, Draw.TabWord, FontWeights.Normal),
            ]);
            if (Done > 0) groups.Add(
            [
                new(Draw.Tick, 9, Draw.TabGreen, FontWeights.Normal, Glyph: true),
                new($" {Done}", 11, Draw.TabInk, FontWeights.SemiBold),
            ]);
            if (groups.Count == 0) return [new TabRun("idle", 10, Draw.TabFlat, FontWeights.Normal)];

            var runs = new List<TabRun>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (i > 0) runs.Add(new TabRun("  ·  ", 10, Draw.TabDot, FontWeights.Normal));
                runs.AddRange(groups[i]);
            }
            return runs;
        }
    }

    /// <summary>The same line as plain words — for the log, and anything that cannot draw.</summary>
    public string TabText => string.Join("", TabRuns.Select(r => r.Text));

    /// <summary>Changes only when the line's wording does; that is what re-sets the runs.</summary>
    public static string TabKey(IReadOnlyList<TabRun> runs) => string.Join("\u0001", runs.Select(r => r.Text));

    public Color TabTone => Stale ? Draw.Grey : Status switch
    {
        "attention" => Draw.Amber,
        "error" => Draw.Red,
        "done" => Draw.Green,
        "working" => Draw.Blue,
        _ => Draw.Grey,
    };

    /// <summary>
    /// The fabricated demo board — **only** for `--demo`, where drawing something with no host is the
    /// point. It must never be a fallback: a window that cannot read a state file says "host offline"
    /// (see <see cref="Live.Unavailable"/>), because an invented board is the one thing the contract's
    /// honesty rules exist to forbid.
    /// </summary>
    public static BoardData Demo()
    {
        // Built through the same `Live.Build` as a real board, from a made-up state file, so the demo
        // can never drift from what the window actually draws.
        long now = Live.NowMs();
        var state = new StateFile { WallMs = now, ContextBudget = 1_000_000 };
        state.Summary = new SummaryRow { Status = "attention", Working = 2, Attention = 1, Done = 1, Total = 4 };
        state.Sessions.Add(new SessionRow("demo-1", "Rename the plugin and publish the window", "dsh-agents-notch", "", "attention",
            now - 180_000, "pwsh", "wants to delete 14 files under src/", "tool", "pwsh", 12, "violet", ContextTokens: 410_000));
        state.Sessions.Add(new SessionRow("demo-2", "Argon 0/4 on laptop — fixed", "blindKnifeArgon", "", "working",
            now - 720_000, null, null, "tool", "roblox_verify", 7, "teal", ContextTokens: 220_000));
        state.Sessions.Add(new SessionRow("demo-3", "Homepage hero copy", "homepage", "", "working",
            now - 60_000, null, null, "thinking", "web_fetch", 3, "lime"));
        state.Sessions.Add(new SessionRow("demo-4", "Fix the seek bug in the knife trail", "swordsGo", "", "done",
            now - 240_000, null, null, "tool", "pwsh", 4, "coral"));
        return Live.Build(state);
    }

    /// <summary>
    /// What has to be rebuilt from scratch: the tab's bots, the card's height. The ROW COUNT is in
    /// here because the card is exactly as tall as its rows.
    /// </summary>
    public static string ShapeOf(BoardData d) =>
        $"{d.MiniStates.Count}|{d.MiniOverflow}|{d.Rows.Count}|{(d.Stale ? 1 : 0)}";

    /// <summary>What has to be rebuilt inside the board: the rows (their ages are updated in place).</summary>
    public static string ContentOf(BoardData d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in d.Rows)
            sb.Append(r.Id).Append('\u0001').Append(r.Title).Append('\u0001').Append(r.Status).Append('\u0001')
              .Append(r.Right).Append('\u0001').Append(r.Face).Append(r.Teammate ? '\u0002' : '\u0001').Append(r.Tail)
              .Append('\u0001').Append(r.Identity).Append('\u0003');
        return sb.ToString();
    }

    /// <summary>What has to be rebuilt in the headline's third line.</summary>
    public static string HeadOf(BoardData d) =>
        $"{d.Front?.Id}\u0001{d.Head.Kind}\u0001{d.Head.Lead}\u0001{d.Head.Rest}\u0001{d.Head.Action}\u0001{(d.HasBudget ? 1 : 0)}";
}

/// <summary>
/// The live data: the state file's rows, parsed and mapped onto the board.
///
/// The mapping is the approved design and is not redesigned here:
///   · the summary is the overall tone, the front session is the big orb,
///   · the sessions are the tab's bots and the task lines, the PROJECTS are the chips.
/// </summary>
internal static class Live
{
    /// <summary>
    /// The state file, parsed against docs/CONTRACT.md schema 3.
    ///
    /// Every field is optional here on purpose: a file written by a host that is mid-restart, by a
    /// newer schema, or by a hand-written fixture must degrade to "not known yet" (CONTRACT #5), not
    /// to an exception. `context.budget` is the one place where null has a MEANING rather than being
    /// absent, so it is read as `int?` and passed through untouched.
    /// </summary>
    public static StateFile? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var state = new StateFile();
        if (root.TryGetProperty("wallMs", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt64(out var wall))
            state.WallMs = wall;
        if (root.TryGetProperty("hostPid", out var hp) && hp.ValueKind == JsonValueKind.Number && hp.TryGetInt32(out var pid))
            state.HostPid = pid;
        if (root.TryGetProperty("retire", out var rt) && rt.ValueKind == JsonValueKind.String)
            state.Retire = rt.GetString() ?? "route";
        if (root.TryGetProperty("finishedUrl", out var fu) && fu.ValueKind == JsonValueKind.String)
            state.FinishedUrl = fu.GetString();
        if (root.TryGetProperty("openUrl", out var ou) && ou.ValueKind == JsonValueKind.String)
            state.OpenUrl = ou.GetString();

        // the human's settings — applied, clamped, never persisted
        state.Settings = Settings.Parse(root);

        // the denominator lives under `context`, and may be null. A null budget is a fact, not an
        // absence: nothing is drawn against it.
        if (root.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Object)
        {
            if (ctx.TryGetProperty("budget", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var budget))
                state.ContextBudget = budget > 0 ? budget : null;   // 0 is not a denominator either
            if (ctx.TryGetProperty("hotAt", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetDouble(out var hot)
                && double.IsFinite(hot) && hot > 0 && hot <= 1)
                state.ContextHotAt = hot;
        }

        if (root.TryGetProperty("summary", out var sum) && sum.ValueKind == JsonValueKind.Object)
        {
            var s = state.Summary;
            if (sum.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String)
                s.Status = st.GetString() ?? "idle";
            s.Working = Int(sum, "working");
            s.Attention = Int(sum, "attention");
            s.Done = Int(sum, "done");
            s.Error = Int(sum, "error");
            s.Total = Int(sum, "total");
        }

        if (root.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in sessions.EnumerateArray())
            {
                var row = ParseSession(el);
                if (row is not null) state.Sessions.Add(row);
            }
        }
        return state;
    }

    private static SessionRow? ParseSession(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string? approvalTool = null, approvalReason = null;
        if (el.TryGetProperty("approval", out var ap) && ap.ValueKind == JsonValueKind.Object)
        {
            if (ap.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String) approvalTool = tn.GetString();
            if (ap.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String) approvalReason = rs.GetString();
        }

        string? activityKind = null, activityLabel = null;
        if (el.TryGetProperty("activity", out var act) && act.ValueKind == JsonValueKind.Object)
        {
            if (act.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String) activityKind = k.GetString();
            if (act.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String) activityLabel = l.GetString();
        }

        return new SessionRow(
            Str(el, "id"), Str(el, "title"), Str(el, "project"), Str(el, "cwd"), Str(el, "status"), Num(el, "since"),
            approvalTool, approvalReason, activityKind, activityLabel, Int(el, "steps"), Str(el, "identity"),
            Str(el, "origin"), Str(el, "parentSession"), Int(el, "contextTokens"),
            Int(el, "errorStreak"), Num(el, "approvedAt"));
    }

    /// <summary>
    /// The board for "no state file has ever parsed" — the honest empty answer.
    ///
    /// This exists because the constructor used to seed `_data` from <see cref="Demo"/>: with the
    /// state file missing (a wrong `--state`, a host that died before its first write, a deleted
    /// state dir, a mistyped fixture in `--shot`) the window drew three invented working sessions
    /// and four bots, and NOTHING ever replaced them — the stale path is gated on a previous
    /// state, so it never fires with `_lastState == null`. That is the one lie the contract exists
    /// to prevent, so the empty board is built through the same `Build` every other board uses,
    /// with `stale: true` and a zero `wallMs`: the tab says "host offline", the sub line says
    /// "no state file", and there is not a bot or a row on it.
    /// </summary>
    public static BoardData Unavailable() => Build(new StateFile { WallMs = 0 }, stale: true);

    /// <summary>
    /// The board the live rows describe.
    ///
    /// <paramref name="pinnedId"/> is the session the human clicked in the card: while it is set
    /// the orb wears THAT session, whatever the recency or urgency rules would have chosen. A
    /// second click on the same row clears it, and so does clicking the card's background — the
    /// pin is a question ("show me this one"), not a mode.
    /// </summary>
    public static BoardData Build(StateFile state, string? pinnedId = null, int maxRows = 6, bool stale = false, bool showProject = true)
    {
        var d = new BoardData
        {
            Status = state.Summary.Status,
            Working = state.Summary.Working,
            Attention = state.Summary.Attention,
            Done = state.Summary.Done,
            Error = state.Summary.Error,
            // the two numbers the token count is meaningless without — see StateFile.ContextBudget.
            // `null` is carried through as null: no denominator, no bar, no percentage.
            ContextBudget = state.ContextBudget,
            ContextHotAt = state.ContextHotAt > 0 ? state.ContextHotAt : .5,
        };
        var sessions = state.Sessions;
        d.SessionCount = sessions.Count;

        // the tab's bots ARE the sessions — up to four, then a "+N"
        int waitingSeen = 0;
        for (int i = 0; i < Math.Min(4, sessions.Count); i++)
        {
            d.MiniStates.Add(ToneOf(sessions[i].Status));
            d.MiniIdentities.Add(IdentityOf(sessions[i].Identity));
            d.MiniStatus.Add(sessions[i].Status);
            // the first bot that needs you pleads; any after it peeks, waiting its turn
            var face = FaceOf(sessions[i]);
            if (face == Face.Pleading && waitingSeen++ > 0) face = Face.Peeking;
            d.MiniFaces.Add(face);
        }
        d.AnyTool = sessions.Any(s => s.Status == "working" && s.ActivityKind == "tool");
        d.MiniOverflow = Math.Max(0, sessions.Count - 4);

        // The front session is the one that CHANGED most recently — not merely one that is working.
        //
        // It used to prefer "the most recent session that is working", and the consequence was
        // invisible until it mattered: the moment a session finished it stopped qualifying, so the
        // orb jumped straight to some other row and the finish motion — the shut, the `^ ^` squint,
        // the green ring — could essentially never play while anything else was still running.
        // Measured 2026-10-20 with four rows `done`: the orb was showing a working session.
        //
        // A settled row holds the orb only while the change is fresh. After that the most urgent
        // row takes it back, so the bot is not left celebrating something that finished an hour ago.
        int frontIndex = 0;
        if (sessions.Count > 0)
        {
            int pinned = pinnedId is null ? -1 : sessions.FindIndex(s => s.Id == pinnedId);
            if (pinned >= 0)
            {
                frontIndex = pinned;
            }
            else
            {
                int newest = 0;
                for (int i = 1; i < sessions.Count; i++)
                    if (Later(sessions[i], sessions[newest])) newest = i;
                frontIndex = NowMs() - sessions[newest].Since <= SpotlightMs ? newest : 0;
            }
        }
        SessionRow? front = sessions.Count > 0 ? sessions[frontIndex] : null;

        if (front is null)
        {
            d.Badge = Draw.Grey;
            d.Identity = Draw.Teal;
            d.FrontStatus = "idle";
            d.Title = "Nothing running";
            d.Sub = "no active sessions";
            d.Head = new HeadLine("idle", "", "", "");
        }
        else
        {
            d.Badge = ToneOf(front.Status);
            d.Identity = IdentityOf(front.Identity);
            d.FrontStatus = front.Status;
            d.FrontKey = front.Id + "|" + front.Status;
            d.Front = front;
            d.FrontFace = FaceOf(front);
            d.Title = front.Title.Length > 0 ? front.Title : ProjectOf(front);
            d.ContextTokens = front.ContextTokens;
            // the context lives in the sub line now: a percentage only against a real budget, the
            // honest token count without one, nothing at all before the session has answered
            var parts = new List<string>();
            if (showProject) parts.Add(ProjectOf(front));
            parts.Add(Phrase(front.Status, Age(front.Since)));
            if (d.ContextText.Length > 0) parts.Add(d.HasBudget ? $"{d.ContextText} context" : $"{d.ContextText} tokens");
            d.Sub = string.Join(" · ", parts);
            d.Head = HeadOf(front);
        }

        // THE ROWS (A1): every session but the one on the bot, most urgent first. One list — the chip
        // column that repeated these same sessions by project is gone. `settings.maxRows` counts the
        // front session too, so the board never carries more than the host was asked to send.
        //
        // A delegated session is a TEAMMATE (round 08, S6): its row steps back to the dimmer ink, and
        // the session that delegated carries how many it has.
        var teammates = sessions
            .Where(s => s.Origin == "subagent" && s.Parent.Length > 0)
            .GroupBy(s => s.Parent)
            .ToDictionary(g => g.Key, g => g.Count());

        // Every session the host sent gets a row (round 13): `maxRows` is how many are VISIBLE, and the
        // list scrolls past that — it used to be a hard cap, so the ninth agent existed only as "+N".
        foreach (var s in sessions.Where(s => !ReferenceEquals(s, front))
                                  .OrderBy(s => Urgency(s.Status)).ThenByDescending(s => s.Since))
        {
            bool teammate = s.Origin == "subagent";
            string tail = !teammate && teammates.TryGetValue(s.Id, out int kids)
                ? (kids == 1 ? "1 teammate" : $"{kids} teammates") : "";
            var (right, mono) = s.Status switch
            {
                "attention" => ("waiting", false),
                "done" => ("done", false),
                "error" => ("failed", false),
                "working" when s.ActivityKind == "tool" && s.ActivityLabel is { Length: > 0 } tool => (tool, true),
                "working" when s.ActivityKind == "thinking" => ("thinking", false),
                _ => ("", false),
            };
            d.Rows.Add(new RowItem(s.Id, s.Title.Length > 0 ? s.Title : ProjectOf(s), s.Status, right, mono,
                Age(s.Since), IdentityOf(s.Identity), FaceOf(s), teammate, tail));
        }

        // CONTRACT #3 — a stale feed shows the UNKNOWN state, never the last summary dressed up as
        // live. The rows stay (they are still what the host last said, and the ✕ on them still
        // works) but every indicator of "this is happening now" goes: the orb's state colour, the
        // aura, the tab's bots. Recovering is instant, because this is recomputed on every read.
        if (stale)
        {
            d.Stale = true;
            d.Badge = Draw.Grey;
            d.FrontStatus = "idle";
            d.MiniStates.Clear();
            d.MiniIdentities.Clear();
            d.MiniStatus.Clear();
            d.MiniFaces.Clear();
            d.MiniOverflow = 0;
            d.AnyTool = false;
            d.FrontFace = Face.Asleep;
            d.Head = new HeadLine("stale", "", "the notch wakes up when DeepSeek Harness does", "");
            // a row that was working is not known to be working any more: no tool, no spinner, asleep
            d.Rows = d.Rows.Select(r => r.Status == "working"
                ? r with { Status = "idle", Right = "", RightMono = false, Face = Face.Asleep }
                : r).ToList();
            // The rows stay, but the sub line stops claiming the state is current: "waiting 9m"
            // about a host that went quiet three hours ago is the one sentence on this board that
            // would be believed and be wrong. The age is the host's last heartbeat, not the row's.
            d.Sub = state.WallMs > 0
                ? $"host offline · last seen {Age(state.WallMs)} ago"
                : "host offline · no state file";
        }

        d.ShapeKey = BoardData.ShapeOf(d);
        d.ContentKey = BoardData.ContentOf(d);
        return d;
    }

    /// <summary>
    /// The headline's third line for the front session. This is the one place the board says WHY a
    /// session waits (`approval.reason`, in full) and WHAT a working one is doing (`activity`) — the
    /// second was parsed and never drawn until round 12. Nothing is made up for a missing field: no
    /// reason falls back to the tool's own name, no activity draws nothing.
    /// </summary>
    public static HeadLine HeadOf(SessionRow s)
    {
        string steps = s.Steps > 0 ? $"step {s.Steps}" : "";
        switch (s.Status)
        {
            case "attention":
            {
                string tool = s.ApprovalTool ?? "";
                string lead = tool == "question"
                    ? s.ApprovalReason is { Length: > 0 } q ? q : "has a question for you"
                    : s.ApprovalReason is { Length: > 0 } r ? $"{tool} {r}".Trim() : tool.Length > 0 ? $"{tool} wants your approval" : "wants your approval";
                return new HeadLine("attention", lead, "", "Open \u2197");
            }
            case "working":
                if (s.ActivityKind == "tool" && s.ActivityLabel is { Length: > 0 } label) return new HeadLine("tool", label, steps, "");
                if (s.ActivityKind == "thinking")
                    return new HeadLine("thinking", "thinking",
                        string.Join(" · ", new[] { s.ActivityLabel is { Length: > 0 } after ? $"after {after}" : "", steps }.Where(x => x.Length > 0)), "");
                return new HeadLine("working", "", steps, "");
            case "error":
                return new HeadLine("error", "failed", s.ActivityLabel is { Length: > 0 } last ? $"last tool {last}" : "", "\u2715 close");
            case "done":
                return new HeadLine("done", "done", s.Steps > 0 ? (s.Steps == 1 ? "1 step" : $"{s.Steps} steps") : "", "\u2715 close");
            default:
                return new HeadLine("idle", "", "", "");
        }
    }

    /// <summary>A state → its tone. These are the five the tab, the orb and the rows wear.</summary>
    public static Color ToneOf(string? status) => status switch
    {
        "working" => Draw.Blue,
        "attention" => Draw.Amber,
        "done" => Draw.Green,
        "error" => Draw.Red,
        _ => Draw.Grey,
    };

    /// <summary>
    /// The face a session RESTS in — the state, read closer (round 12). The motion in `Tick` plays
    /// over it, and the one-shot faces (surprised, love, wink, glances, starry) are the window's.
    ///
    /// A working session's face is its most pressing fact: a streak of tool errors first (it is
    /// struggling), then a very long run, then a busy one, and only then whether it is in a model turn
    /// or running a tool. Each threshold is a fact the host wrote, never a guess.
    /// </summary>
    public static Face FaceOf(SessionRow s) => s.Status switch
    {
        "attention" => Face.Pleading,
        "error" => Face.Xx,
        "done" => Face.Done,
        "working" when s.ErrorStreak >= 2 => Face.Dizzy,
        "working" when s.ErrorStreak == 1 => Face.Confused,
        "working" when s.Since > 0 && NowMs() - s.Since >= TiredMs => Face.Tired,
        "working" when s.Steps >= DeterminedSteps => Face.Determined,
        "working" when s.ActivityKind == "thinking" => Face.Thinking,
        "working" => Face.Focused,
        _ => Face.Idle,
    };

    /// <summary>A working turn this busy has its brows set.</summary>
    public const int DeterminedSteps = 25;
    /// <summary>Working this long — half an hour — and the bot gets tired.</summary>
    public const long TiredMs = 30 * 60_000;

    /// <summary>The identity palette — one stable hue per session, independent of its state.</summary>
    public static Color IdentityOf(string? identity) => identity switch
    {
        "violet" => Draw.Violet,
        "coral" => Draw.Coral,
        "pink" => Draw.Pink,
        "lime" => Draw.Lime,
        _ => Draw.Teal,
    };

    /// <summary>How urgent a state is — what the board puts first.</summary>
    public static int Urgency(string? status) => status switch
    {
        "attention" => 0,
        "error" => 1,
        "done" => 2,
        "working" => 3,
        _ => 4,
    };

    /// <summary>
    /// How long a just-changed session keeps the orb before the most urgent row takes it back.
    /// Long enough to carry the finish motion (~1.65 s of it), short enough that the bot is never
    /// left sitting on something that happened minutes ago.
    /// </summary>
    public const long SpotlightMs = 8000;

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Is <paramref name="a"/> a more recent change than <paramref name="b"/>? Urgency breaks a tie.</summary>
    public static bool Later(SessionRow a, SessionRow b) =>
        a.Since != b.Since ? a.Since > b.Since : Urgency(a.Status) < Urgency(b.Status);

    /// <summary>The dot a task line wears.</summary>
    public static string KindOf(string? status) => status switch
    {
        "attention" => "attention",
        "error" => "error",
        "done" => "done",
        "working" => "active",
        _ => "open",
    };

    /// <summary>The phrase the sub line carries.</summary>
    public static string Phrase(string? status, string age) => status switch
    {
        "working" => $"working {age}",
        "attention" => $"waiting {age}",
        "done" => $"finished {age} ago",
        "error" => $"failed {age} ago",
        _ => age,
    };

    /// <summary>How long ago, as Ns / Nm / Nh.</summary>
    public static string Age(long sinceMs)
    {
        if (sinceMs <= 0) return "0s";
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sinceMs;
        if (ms < 0) ms = 0;
        long s = ms / 1000;
        if (s < 60) return $"{s}s";
        long m = s / 60;
        if (m < 60) return $"{m}m";
        long h = m / 60;
        // Days past a full day: "717h" is a number nobody can read, and it is what a machine that
        // was asleep overnight produces for yesterday's work.
        return h < 24 ? $"{h}h" : $"{h / 24}d";
    }

    /// <summary>The project a session belongs to — what its chip is named.</summary>
    public static string ProjectOf(SessionRow s)
    {
        if (s.Project.Length > 0) return s.Project;
        if (s.Cwd.Length > 0)
        {
            var parts = s.Cwd.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return parts[^1];
        }
        return "unknown";
    }

    private static string Trim(string text, int max)
    {
        text = text.Trim();
        if (text.Length <= max) return text;
        return text[..(max - 1)].TrimEnd() + "…";
    }

    private static long Num(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : 0;

    private static int Int(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;

    private static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

internal sealed class OverlayWindow : Window
{
    // The design's own coordinate space — everything below is drawn in these units and the whole
    // surface is scaled once, by `settings.scale`, through a LayoutTransform on `_host`. That is why
    // the numbers here never mention the scale: a scaled notch is the same drawing, bigger.
    const double WinW = 660, WinH = 300;
    const double TabH = 30;
    // A1 (round 12): the board is one surface with the tab — it hangs from the tab's lower edge and
    // shares its fill, with the same concave shoulders a real notch has where it meets the screen.
    const double SheetW = 500, SheetX = (WinW - 500) / 2, SheetY = TabH - 1;
    /// <summary>The headline's height: the 60 px bot with its ring, and three lines beside it.</summary>
    const double HeadH = 92;
    /// <summary>Where the rows start, and how far apart they are, inside the sheet.</summary>
    const double RowsTop = 98, RowPitch = 28;
    /// <summary>The text column: the title, the sub line, the third line and the rows all start here.</summary>
    const double TextX = 96, TextW = SheetW - 96 - 20;
    /// <summary>Room under the card for its drop shadow (the window is bigger than what it draws).</summary>
    const double CardFoot = 82;

    /// <summary>One sweep of the tool light along the tab's edge, start to finish.</summary>
    const double ShimmerSweepSeconds = 1.4;

    /// <summary>
    /// How many rows the board shows before its list scrolls: `settings.maxRows` counts the session on
    /// the big bot too, so the rows under it are one fewer.
    /// </summary>
    int VisibleRows => Math.Max(1, Math.Clamp(_settings.MaxRows, Settings.RowsMin, Settings.RowsMax) - 1);

    /// <summary>The working spinner's speed: once round every 2.4 s, the same for every bot.</summary>
    const double SpinDegreesPerSecond = 150;

    // the island (round 12): how long an alert holds the tab open, and how fast it opens and folds
    const double IslandAlertSeconds = 6.0, IslandDoneSeconds = 2.0, IslandEaseSeconds = 0.18;

    // the three motions, in seconds (round 09 C: 360 / 200 / 140 ms)
    const double MiniSweepSeconds = 0.36;  // a settled bot draws its ring
    const double MiniArriveSeconds = 0.20; // a new session's bot slides into the tab
    const double FoldSeconds = 0.14;       // a retired row folds up

    const double ExpandSeconds = 0.16;     // the board's unfold
    const double CollapseDelay = 0.25;     // a beat before it folds away again

    /// <summary>
    /// CONTRACT #3: more than this much lag between the host's `wallMs` and this machine's clock
    /// means the feed is not current. 4 s is 12 missed writes of the host's 340 ms cycle.
    /// </summary>
    const double StaleMs = 4000;

    readonly Options _opt;
    /// <summary>
    /// What the board currently shows. Assigned in the constructor and never left unassigned: only
    /// `--demo` gets the fabricated sheet, because drawing something with no host is its whole point.
    /// Every other start begins on <see cref="Live.Unavailable"/> — the honest empty board — and stays
    /// there until a state file parses. A window that cannot read a state file must say so, not invent
    /// three sessions (see `Unavailable`).
    /// </summary>
    BoardData _data;

    /// <summary>The settings in force: parsed from the state file, clamped by Settings, never saved.</summary>
    Settings _settings = new();
    /// <summary>`settings.scale`, as the LayoutTransform on `_host` uses it.</summary>
    double _scale = 1.0;
    /// <summary>
    /// `settings.motion` resolved: true means the notch draws and nothing moves. `auto` defers to
    /// Windows' own answer (`SystemParameters.ClientAreaAnimation`, the "animate controls" lever that
    /// the Reduce motion / Show animations setting drives), because a person who turned that off for
    /// Windows did not mean "except for this".
    /// </summary>
    bool _motionOff;
    /// <summary>
    /// The clock the IDLE motion reads while `_motionOff`. Zero on purpose: at t=0 the blink phase is
    /// at its start (eyes open), the breath is mid-way, and every pulse sits at a fixed value, so a
    /// still notch looks like a held breath rather than a frozen frame of a blink.
    /// </summary>
    const double StillTime = 0.0;
    /// <summary>The card's height for the rows it currently carries.</summary>
    double _cardH = HeadH;
    /// <summary>The window's height in design units; it grows with the card.</summary>
    double _designH = WinH;
    /// <summary>Where the last `Place.Resolve` put the window — logged, and what `--probe-rect` prints.</summary>
    Placement? _placement;
    /// <summary>
    /// The monitor the notch itself sits on. The full-screen test compares the foreground window's
    /// monitor against this one, so a game on display 1 cannot hide the notch on display 2. Recaptured
    /// on every placement, because `settings.screen` can move it at runtime.
    /// </summary>
    IntPtr _ownMonitor;

    readonly Canvas _root = new();
    /// <summary>
    /// The one place the scale is applied. `_host` is exactly the design size and the LayoutTransform
    /// scales it up, so the window's own Width/Height (`WinW × scale`) frame the same drawing bigger
    /// — tab, board, fonts, orbs, rings and shadows all at once, because they are all inside it.
    /// </summary>
    readonly Grid _host = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    /// <summary>The last compositor frame the motion ran for — `Rendering` can fire twice for one.</summary>
    TimeSpan _lastRenderTime = TimeSpan.MinValue;
    /// <summary>Frames per second cap for the motion; 0 = run at the compositor's rate.</summary>
    int _fpsCap;
    double _lastTickMs;
    // 150 ms, not 300: the host now writes the moment an event lands, so this poll is the other
    // half of the delay between "the agent needs you" and the tab saying so.
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly DispatcherTimer _topmost = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    /// <summary>Is the harness still there? (CONTRACT lifecycle: the child is killed with the parent.)</summary>
    readonly DispatcherTimer _parent = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly Stopwatch _sw = Stopwatch.StartNew();

    Canvas? _tabHost, _cardHost, _orbHost, _headHost, _linesHost, _restLayer, _islandLayer;
    StackPanel? _headLine;
    System.Windows.Shapes.Path? _tabShape;
    System.Windows.Shapes.Path? _shimmer, _shimmerBase;
    TranslateTransform? _shimmerMove;
    double _shimmerAlpha, _shimmerPhase;
    bool _shimmerRunning;                          // a sweep is in flight and will be finished
    double _tabW = -1, _tabHNow = -1;             // the size the tab's shape was last built for
    double _restW = -1;                           // the resting tab's width, measured from its words
    string _headKey = "";
    readonly List<TextBlock> _ageTexts = new();
    readonly List<MiniBotView> _rowBots = new();
    readonly List<(string Status, Face Face)> _rowBotState = new();
    // the island
    string _islandKind = "";                      // "" | "x1" | "x4" | "d1"
    string _islandId = "";
    double _islandStart = -1, _islandUntil = -1, _islandEase;
    double _islandW, _islandH;
    bool _holdShut;                               // Open on the island was pressed: keep the board folded
    OrbView? _islandOrb;
    readonly Dictionary<string, string> _statusById = new();
    readonly List<MiniBotView> _miniBots = new();
    readonly List<Color> _miniStates = new();
    readonly List<string> _miniStatus = new();
    readonly List<Face> _miniFaces = new();
    // the three motions: a ring that draws itself, a bot that arrives, a row that folds away
    readonly double[] _miniBorn = new double[4];     // when each tab bot appeared (-1 = it was there)
    readonly double[] _miniX = new double[4];        // its resting x in the tab
    readonly double[] _sweepNow = new double[4];     // how much of its ring is drawn
    readonly List<Canvas> _rowViews = new();
    // the rows' scrolling list (round 13): a clipped viewport, a panel that moves, a thin thumb
    Canvas? _rowsViewport;
    TranslateTransform? _rowsScroll;
    System.Windows.Shapes.Rectangle? _rowsThumb;
    LinearGradientBrush? _rowsFade;
    double _scroll, _scrollTarget;
    int _miniSeen;                                   // how many bots the last build had
    int _foldIndex = -1;
    double _foldAt = -1;

    // frame statistics (only collected with --log): see the tail of `Tick`
    int _statFrames;
    double _statGapSum, _statMaxGap, _statMaxWork, _statAt, _statOver;
    OrbView? _orb;
    RunLine? _tabLine;
    TextBlock? _titleText, _subText;
    string _tabKey = "";
    string? _pinnedId;               // a row the human clicked — the orb wears it until unpinned
    StateFile? _lastState;           // so a click can re-answer without waiting for the next poll
    bool _dismissed;                 // --dismiss fires once
    // fully qualified: `using System.Windows.Shapes` is deliberately absent, because its `Path`
    // collides with System.IO.Path (CS0104) and the shapes are one-off here.
    System.Windows.Shapes.Polyline? _tabChevron;
    double _lastGoodState;          // the clock reading of the last state file that parsed
    string _lastLoggedKey = "";
    double _orbX, _orbY, _orbCenter;
    double _t, _lastT = -1;
    string _frontWas = "";        // the front session's last state, to spot a transition
    double _finishStart = -1;     // when the front session finished · starts its one-shot motion
    double _alertStart = -1;      // …when it started asking for you
    double _errorStart = -1;      // …when it failed
    double _ringAlpha;            // the ring fades in on a settled session, out on a working one
    double _spinAlpha;            // the working spinner (F1d) fades the same way
    // the one-shot faces the window plays on its own (round 12)
    double _glanceUntil = -1;     // a row was added below
    double _winkUntil = -1;       // the human pressed the ✕
    double _staleSince = -1e9;    // when the feed went quiet — the falling-asleep strip reads it
    TranslateTransform? _orbMove; // the hop and the shake
    ScaleTransform? _orbBreath;
        bool _ringSeeded;            // the first frame snaps the ring instead of fading from 0
    double _lookX, _lookY;
    double _flare;
    double _p;                    // 0 = folded (tab only), 1 = open (the board)
    bool _want;                   // the intent: is the pointer over the tab?
    double _collapseAt;           // when the fold may start
    bool _cardLive;               // is the (invisible) card allowed to take the mouse?
    bool _forceHidden, _hidden, _forceShow, _forceOpen, _fullscreen;
    double _freezeMs = -1;
    System.Windows.Forms.NotifyIcon? _tray;

    public OverlayWindow(Options opt)
    {
        _opt = opt;
        // The ONLY producer of the demo sheet. Every other start — including one whose `--state`
        // does not exist yet — begins on the honest empty board, and `SeedFromState` below replaces it
        // the moment a real file parses.
        _data = opt.Demo ? BoardData.Demo() : Live.Unavailable();
        _forceShow = opt.ForceShow;
        _forceOpen = opt.Open;
        _freezeMs = opt.FreezeMs;
        Title = "Agents Notch";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Width = WinW;
        Height = WinH;
        SizeToContent = SizeToContent.Manual;

        // The scale is a LayoutTransform on ONE element, so the window's size and the notch's size
        // cannot disagree: `_host` is always the design size, and the transform is the only thing
        // that knows about `settings.scale`.
        _root.Background = null;
        _host.Width = WinW;
        _host.Height = WinH;
        _host.Children.Add(_root);
        Content = _host;

        // Read the state file BEFORE the window is shown: the settings in it decide where this
        // window goes and how big it is, and a notch that appears at the wrong size and then jumps
        // is a worse first impression than a 20 ms slower start.
        SeedFromState();

        // No background on the root: a transparent WPF window lets clicks through the empty
        // parts, so the board only takes the mouse where it is actually drawn.
        _root.MouseEnter += (_, _) => { _want = true; _collapseAt = double.MaxValue; };
        _root.MouseLeave += (_, _) => { _want = false; _collapseAt = _t + CollapseDelay; _holdShut = false; };

        SourceInitialized += (_, _) =>
        {
            Log.Write("source initialized — hwnd created");
            Native.MakeTopmostAndInert(this);
            if (_opt.CaptureExclude) Log.Write("capture exclusion: " + Native.ExcludeFromCapture(this));
        };

        Loaded += (_, _) =>
        {
            Log.Write("loaded — window shown");
        Log.Write($"render tier={RenderCapability.Tier >> 16} mode={RenderOptions.ProcessRenderMode}" +
                  $" refresh={Native.RefreshHz()}Hz");
            PlaceWindow();
            Rebuild();
            _lastGoodState = _sw.Elapsed.TotalSeconds;
            _poll.Tick += (_, _) => PollState();
            _topmost.Tick += (_, _) => Native.ReassertTopmost(this);
            // The MOTION is driven by the compositor, not by a timer — see `OnRendering`.
            // `--fps N` caps the motion's own frame rate (0 = compositor rate); a knob, not a setting.
            _fpsCap = _opt.DesiredFps;
            if (_opt.NoEffects) StripEffects(_root);
            CompositionTarget.Rendering += OnRendering;
            _poll.Start(); _topmost.Start();
            // `--parent-pid`: a hard-killed harness leaves no chance to close this window, so the
            // window watches the pid itself. 2 s is fast enough that a crash never leaves a notch
            // talking to a dead feed, and slow enough to cost nothing.
            if (_opt.ParentPid > 0)
            {
                Log.Write($"watching parent pid {_opt.ParentPid}");
                _parent.Tick += (_, _) =>
                {
                    if (ParentAlive(_opt.ParentPid)) return;
                    _parent.Stop();
                    Log.Write($"parent {_opt.ParentPid} is gone — exiting");
                    Close();
                };
                _parent.Start();
            }
            if (_opt.Tray) { try { BuildTray(); } catch (Exception e) { Log.Write("tray failed: " + e.Message); } }
            WritePidFile();
            Log.Write($"up — pid {Environment.ProcessId}, rule {_opt.HideRule}, {_settings}");
            if (_opt.ShotPath is not null) ArmShot();
        };

        // A drag to another monitor changes the DPI under us; per-monitor-v2 means WPF re-scales the
        // window, and the notch has to go back to the anchor it is anchored to rather than have its
        // top-left stay put (which would leave it half off the screen on a narrower display).
        DpiChanged += (_, e) =>
        {
            Log.Write($"dpi changed — {e.OldDpi.DpiScaleX:0.##} → {e.NewDpi.DpiScaleX:0.##}");
            PlaceWindow();
        };

        Closed += (_, _) =>
        {
            CompositionTarget.Rendering -= OnRendering;
            _parent.Stop();
            _tray?.Dispose();
            _tray = null;
            DeletePidFile();
        };
    }

    // ── placement: fixed, top centre, never dragged ─────────────────────────────

    /// <summary>
    /// Measurement knob (`--no-effects`): drop every `Effect` in the tree, so the cost of the glows
    /// can be told apart from the cost of the frames. Not reachable from the plugin's own spawn.
    /// </summary>
    private static void StripEffects(DependencyObject node)
    {
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is UIElement el) el.Effect = null;
            StripEffects(child);
        }
    }

    /// <summary>
    /// The plugin looks for this file to learn whether a window is already up: the exe's own
    /// named mutex keeps two windows off the screen, but without this the plugin would keep
    /// spawning a process that exits immediately.
    /// </summary>
    private string PidFilePath => Path.Combine(Path.GetDirectoryName(_opt.StatePath) ?? ".", "window.json");

    private void WritePidFile()
    {
        try
        {
            File.WriteAllText(PidFilePath,
                $"{{\"pid\":{Environment.ProcessId},\"at\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
        }
        catch (Exception e) { Log.Write("pid file write failed: " + e.Message); }
    }

    private void DeletePidFile()
    {
        try { File.Delete(PidFilePath); } catch { /* the file may not exist */ }
    }

    /// <summary>
    /// Is the harness process still there? "No such process" is the ONLY answer that ends this
    /// window: an access-denied or a transient failure means "I could not ask", and a display that
    /// disappears because it could not ask a question is a worse bug than the one being fixed.
    /// </summary>
    private static bool ParentAlive(int pid)
    {
        if (pid <= 0) return true;                       // nothing to watch
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }      // no process with that id
        catch (InvalidOperationException) { return false; }
        catch { return true; }
    }

    /// <summary>
    /// --shot: wait for the first state to land, render what the window DRAWS, write it, quit.
    ///
    /// A screen grab cannot be trusted for this window: it is layered and always-on-top, and on a
    /// blanked or locked display GDI capture and the UIA screenshot both come back as wallpaper
    /// while the window is still perfectly alive (measured 2026-10-20 — the tab was in the UIA tree
    /// with correct run bounds and absent from every grab). Rendering the content tree is what
    /// answers "what does the tab actually draw", and it is what the design rounds are judged on.
    /// </summary>
    private void ArmShot()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                // Pose the frame BEFORE rendering it. The motion is driven by the compositor now
                // (`OnRendering`), and a shot window has no guaranteed compositor frames to spare —
                // without this the card photographs at opacity 0 and every round of stills silently
                // shows a closed board (found 2026-10-21, from a round that came out tab-only).
                Tick();
                var (pw, ph) = RenderPng(_opt.ShotPath!, 4);
                Log.Write($"shot written — {_opt.ShotPath} ({pw}×{ph})" +
                          $" scale={_scale:0.##} tab=\"{_data.TabText}\" open={_p:0.##}");
            }
            catch (Exception e) { Log.Write("shot failed: " + e); }
            Close();
        };
        timer.Start();
    }

    /// <summary>
    /// `zoom`× the window it would have been on screen — scale included, because `_host` is the
    /// element that carries the LayoutTransform. Measuring and arranging it at the window's own DIP
    /// size is what makes a scale-1.5 still show the scaled notch rather than the design drawn small
    /// in a big bitmap.
    /// </summary>
    private (int W, int H) RenderPng(string path, double zoom)
    {
        double w = double.IsFinite(Width) && Width > 0 ? Width : WinW;
        double h = double.IsFinite(Height) && Height > 0 ? Height : _designH;
        _host.Measure(new Size(w, h));
        _host.Arrange(new Rect(0, 0, w, h));
        _host.UpdateLayout();
        var rtb = new RenderTargetBitmap(
            (int)Math.Round(w * zoom), (int)Math.Round(h * zoom), 96 * zoom, 96 * zoom, PixelFormats.Pbgra32);
        rtb.Render(_host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path)) enc.Save(fs);
        return (rtb.PixelWidth, rtb.PixelHeight);
    }

    int _recordFrame;
    double _recordT = -1;           // the motion's clock while recording, in seconds; -1 = real time

    /// <summary>
    /// --record: one PNG per frame at `--fps` (default 30), on a clock that advances one frame per
    /// frame written. Rendering the board with its glows at 2× takes ~0.5 s, so on the real clock a
    /// recording was 2 fps and starved the poll timer; on this one the motion is exactly the motion,
    /// however slow the renders are. The state is read every frame for the same reason, and
    /// `frames.tsv` stamps each frame in motion-clock ms so a driver can schedule against it.
    /// </summary>
    private void RecordFrame()
    {
        double step = 1.0 / (_opt.DesiredFps > 0 ? _opt.DesiredFps : 30);
        if (_recordT < 0)
        {
            _recordT = _sw.Elapsed.TotalSeconds;
            Directory.CreateDirectory(_opt.RecordDir!);
            File.WriteAllText(Path.Combine(_opt.RecordDir!, "frames.tsv"), "frame\tms\n");
        }
        if (_recordFrame * step * 1000 > _opt.RecordMs)
        {
            Log.Write($"record done — {_recordFrame} frames in {_opt.RecordDir}");
            CompositionTarget.Rendering -= OnRendering;
            Close();
            return;
        }
        try
        {
            string name = $"f{_recordFrame:D4}.png";
            RenderPng(Path.Combine(_opt.RecordDir!, name), 2);
            File.AppendAllText(Path.Combine(_opt.RecordDir!, "frames.tsv"), $"{name}\t{_recordT * 1000:0}\n");
            _recordFrame++;
            _recordT += step;
            PollState();
        }
        catch (Exception e) { Log.Write("record failed: " + e.Message); Close(); }
    }

    /// <summary>
    /// Put the window where `settings.screen` + `settings.anchor` say, in PHYSICAL pixels.
    ///
    /// Position goes to `SetWindowPos` in physical px while the window's size is in DIP, and on a
    /// mixed-DPI desk those are not the same number — that is how a top-centre notch ends up
    /// off-centre on the second monitor. `Place.Resolve` does that arithmetic in one place, and
    /// `--probe-rect` prints its answer so the placement can be checked without seeing the desk.
    /// </summary>
    private void PlaceWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;          // before the window exists; `Loaded` places it
        var p = Place.Resolve(_settings, Width, Height);
        _placement = p;
        Native.Move(h, p.WindowPxX, p.WindowPxY);
        _ownMonitor = Native.MonitorOf(h);
        Log.Write($"placed — {p.Monitor} screen={_settings.Screen} anchor={_settings.Anchor}" +
                  $" dip={Width:0}×{Height:0} px={p.WindowPxX},{p.WindowPxY} {p.WindowPxW}×{p.WindowPxH}" +
                  $" dpi={p.DpiMonitor}(sys {p.DpiSystem}) scale={_scale:0.##}");
    }

    /// <summary>
    /// Read the state file once, before the window exists, so the FIRST frame is already at the
    /// right size and place. Everything after this is the poll.
    /// </summary>
    private void SeedFromState()
    {
        if (_opt.Demo) return;
        try
        {
            if (!File.Exists(_opt.StatePath)) return;
            using var fs = new FileStream(_opt.StatePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var state = Live.Parse(doc.RootElement);
            if (state is null) return;
            _lastState = state;
            _settings = state.Settings;
            _data = Live.Build(state, null, _settings.MaxRows, IsStale(state), _settings.ShowProject);
            ApplySettings(_settings);
            // Name the RESOLVED motion as well as the requested one: `auto` depending on a Windows
        // setting is right, and it is also the first thing somebody asks when nothing moves.
        Log.Write($"seeded — {_settings} motion={(_motionOff ? "off" : "on")}" +
                  $" (auto follows Windows: {System.Windows.SystemParameters.ClientAreaAnimation})");
        }
        catch (Exception e) { Log.Write("seed failed (will poll): " + e.Message); }
    }

    /// <summary>
    /// CONTRACT #3 — is this feed current? `wallMs` is the host's clock at write time, compared with
    /// THIS machine's clock: more than 4 s behind is the host gone or wedged. A file with no
    /// `wallMs` at all cannot claim to be current either, so 0 counts as stale.
    /// </summary>
    private static bool IsStale(StateFile state) =>
        state.WallMs <= 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - state.WallMs > (long)StaleMs;

    /// <summary>
    /// Apply the settings block. Called at startup (from the seed read) and again whenever the block
    /// changes in the state file; remembered nowhere on disk (CONTRACT #4).
    /// </summary>
    private void ApplySettings(Settings next)
    {
        bool changed = !_settings.SameAs(next);
        _settings = next;
        _scale = Settings.ClampScale(next.Scale);
        _host.LayoutTransform = Math.Abs(_scale - 1.0) < 1e-4
            ? Transform.Identity
            : new ScaleTransform(_scale, _scale);
        // How much of the desktop the notch sits in front of. Set on `_root` so the card's own reveal
        // (which animates `_cardHost.Opacity` from 0) still works: the two multiply.
        _root.Opacity = next.Opacity;
        _motionOff = next.Motion == Settings.MotionOff
            || (next.Motion == Settings.MotionAuto && !System.Windows.SystemParameters.ClientAreaAnimation);
        ResizeWindow();
        if (changed) PlaceWindow();
    }

    /// <summary>The window is the design rectangle times the scale; the card grows with its rows.</summary>
    private void ResizeWindow()
    {
        Width = WinW * _scale;
        Height = _designH * _scale;
        _host.Width = WinW;          // the design size — the LayoutTransform is what makes it bigger
        _host.Height = _designH;
        _root.Width = WinW;
        _root.Height = _designH;
    }

    /// <summary>How much room `rows` rows need, and therefore how tall the window is.</summary>
    private void FitRows(int rows)
    {
        int n = Math.Min(rows, VisibleRows);
        _cardH = n > 0 ? RowsTop + n * RowPitch + 10 : HeadH;
        _designH = SheetY + _cardH + CardFoot;
    }

    // ── the board ───────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        // The card is exactly as tall as its rows, so the window's height is decided here — before
        // anything is positioned inside it.
        FitRows(_data.Rows.Count);
        ResizeWindow();

        _root.Children.Clear();
        var d = _data;

        // ── the board, first, so the tab draws over its top edge ─────────────────────────────────
        _cardHost = new Canvas { Width = SheetW, Height = _cardH, Opacity = 0, IsHitTestVisible = false };
        _cardHost.RenderTransformOrigin = new Point(.5, 0);
        // `ApplyProgress` caches both of these against the card it last saw. Left over from the old
        // card, the new one stayed unhittable while open, and the board folded away under the cursor.
        _cardLive = false;
        _cardSlide = null;
        _cardScale = null;
        // The sheet runs up behind the tab to the screen's own top edge, so its shoulders flare out of
        // the bezel the way the tab's do and the two read as one notch — not a card on a shelf.
        var sheet = new System.Windows.Shapes.Path
        {
            Data = NotchShape(SheetW, _cardH + SheetY, 16, 24),
            RenderTransform = new TranslateTransform(0, -SheetY),
            Fill = Draw.B(Draw.C("#08080A")),
            Effect = Draw.Shadow(),
            // the sheet never changes shape while it lives, so its blurred shadow is rasterised once
            CacheMode = new BitmapCache(),
        };
        // the wheel scrolls the rows when there are more than fit
        _cardHost.MouseWheel += (_, e) =>
        {
            if (_rowsViewport is null) return;
            double max = Math.Max(0, _data.Rows.Count * RowPitch - _rowsViewport.Height);
            if (max <= 0) return;
            _scrollTarget = Math.Clamp(_scrollTarget - e.Delta / 120.0 * RowPitch, 0, max);
            e.Handled = true;
        };
        // clicking the board's own background lets go of a pinned session
        sheet.MouseLeftButtonDown += (_, _) => { if (_pinnedId is not null) TogglePin(_pinnedId); };
        _cardHost.Children.Add(sheet);

        _orbHost = new Canvas { Width = SheetW, Height = _cardH };
        _headHost = new Canvas { Width = SheetW, Height = _cardH };
        _linesHost = new Canvas { Width = SheetW, Height = _cardH };
        _cardHost.Children.Add(_orbHost);
        _cardHost.Children.Add(_headHost);
        _cardHost.Children.Add(_linesHost);

        _orb = new OrbView(60, d.Badge, d.Identity);
        _orb.RenderTransformOrigin = new Point(.5, .5);
        _orbHost.Add(_orb, 20, 16);
        _orbX = SheetX + 20; _orbY = SheetY + 16; _orbCenter = _orb.CenterOffset;

        _titleText = Draw.Text(d.Title, 14, Draw.Ink, FontWeights.SemiBold);
        _titleText.Width = TextW;
        _headHost.Add(_titleText, TextX, 17);
        _subText = Draw.Text(d.Sub, 11, Draw.Mute);
        _subText.Width = TextW;
        _headHost.Add(_subText, TextX, 40);
        _headKey = "";
        BuildHeadLine();
        _root.Add(_cardHost, SheetX, SheetY);

        // ── the tab — always visible, the summary of everything, and the island when it speaks ──
        _tabHost = new Canvas { Width = 128, Height = TabH };
        _tabHost.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { e.Handled = true; OpenApp(IslandUp ? _islandId : _data.Front?.Id); return; }
            // a click on an alert folds it, and the board the pointer is already over opens
            if (IslandUp) { e.Handled = true; FoldIsland(); }
        };
        _tabShape = new System.Windows.Shapes.Path { Fill = Draw.B(Draw.C("#08080A")), Effect = Draw.Shadow(34, 8, .5) };
        _tabW = -1; _tabHNow = -1;
        _tabHost.Children.Add(_tabShape);

        _restLayer = new Canvas { Height = TabH };
        _restW = -1;
        _tabHost.Children.Add(_restLayer);
        // the bots themselves, small: each wears its session's colour, its state's ring and face, and
        // — while it works — the spinner. The ring needs room outside the face, hence the 28 pitch.
        _miniBots.Clear();
        _miniStates.Clear();
        _miniStatus.Clear();
        _miniFaces.Clear();
        const double MiniOff = 7, MiniPitch = 28;
        double x = 12;
        int miniCount = Math.Min(4, d.MiniStates.Count);
        int seenBefore = _miniSeen;
        _miniSeen = miniCount;
        for (int i = 0; i < miniCount; i++)
        {
            var mini = new MiniBotView(16, d.MiniStates[i], d.MiniIdentities[i]);
            _miniBots.Add(mini);
            _miniStates.Add(d.MiniStates[i]);
            _miniStatus.Add(i < d.MiniStatus.Count ? d.MiniStatus[i] : "working");
            _miniFaces.Add(i < d.MiniFaces.Count ? d.MiniFaces[i] : Face.Idle);
            _miniX[i] = x - MiniOff;
            // a bot for a session that was not in the tab before slides in; the rest are already there
            _miniBorn[i] = i >= seenBefore ? _t : -1;
            bool settled = _miniStatus[i] is "done" or "error" or "attention";
            _sweepNow[i] = settled ? 1 : 0;      // a rebuild shows a state, it does not replay it
            mini.SetSweep(_sweepNow[i]);
            mini.Opacity = _miniBorn[i] < 0 ? 1 : 0;
            _restLayer.Add(mini, _miniX[i], 9 - MiniOff);
            x += MiniPitch;
        }
        if (miniCount == 0)
        {
            // nothing at all is running — one dot, wearing the summary's tone
            _restLayer.Add(Draw.Dot(6, d.TabTone, .9), 13, 12);
            x += 11;
        }
        if (d.MiniOverflow > 0)
        {
            _restLayer.Add(Draw.Label($"+{d.MiniOverflow}", 9.5, Draw.Mute, FontWeights.SemiBold), x - 1, 10);
            x += 13;
        }
        _tabLine = new RunLine();
        _tabLine.Set(d.TabRuns);
        _tabKey = BoardData.TabKey(d.TabRuns);
        _restLayer.Add(_tabLine, x + 1, 7);
        _tabChevron = Draw.Chevron(7, Draw.Mute);
        _restLayer.Add(_tabChevron, 190, 13);

        // L2 (round 12): light runs along the tab's lower edge while any session is running a tool.
        // It traces the edge itself — up into both rounded corners, fading out where they turn — so it
        // reads as the notch's own rim catching light, not as a bar laid across it (round 13; the flat
        // 2 px bar was cut off square at both ends). A faint base line under a travelling highlight.
        _shimmerMove = new TranslateTransform();
        var shine = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0), RelativeTransform = _shimmerMove };
        shine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x6A, 0xA8, 0xFF), .3));
        shine.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0xCF, 0xE1, 0xFF), .5));
        shine.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x6A, 0xA8, 0xFF), .7));
        var ends = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        ends.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        ends.GradientStops.Add(new GradientStop(Colors.Black, .1));
        ends.GradientStops.Add(new GradientStop(Colors.Black, .9));
        ends.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        _shimmerBase = new System.Windows.Shapes.Path
        {
            Stroke = Draw.B(Color.FromArgb(0x4C, 0x6A, 0xA8, 0xFF)), StrokeThickness = 1.4, OpacityMask = ends,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0, IsHitTestVisible = false,
        };
        _shimmer = new System.Windows.Shapes.Path
        {
            Stroke = shine, StrokeThickness = 1.6, OpacityMask = ends,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0, IsHitTestVisible = false,
        };
        _tabHost.Add(_shimmerBase, 0, 0);
        _tabHost.Add(_shimmer, 0, 0);

        _islandLayer = new Canvas { Opacity = 0, IsHitTestVisible = false };
        _tabHost.Children.Add(_islandLayer);
        if (_islandKind.Length > 0) BuildIsland();

        _root.Add(_tabHost, (WinW - 128) / 2, 0);
        LayoutTab();

        RebuildStatic();
        ApplyProgress();
        Log.Write($"board drawn — tab \"{d.TabText}\" · title \"{d.Title}\" · sub \"{d.Sub}\"" +
                  $" · line [{d.Head.Kind}] \"{d.Head.Lead}\" \"{d.Head.Rest}\" · {d.Rows.Count} row(s) · {d.MiniStates.Count} bot(s) in the tab");
        // the measurement knob has to re-run after every rebuild, or the tree it measured is gone
        if (_opt.NoEffects) StripEffects(_root);
    }

    /// <summary>
    /// The notch's outline: a body with rounded lower corners that meets the top edge through two
    /// concave shoulders, the way a hardware notch flares into the bezel. The tab and the board both
    /// wear it, so with the board open they read as one surface hanging from the screen's edge.
    /// </summary>
    internal static Geometry NotchShape(double w, double h, double shoulder, double radius)
    {
        radius = Math.Min(radius, Math.Min(w, h) / 2);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(-shoulder, 0), true, true);
            c.ArcTo(new Point(0, shoulder), new Size(shoulder, shoulder), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(0, h - radius), true, false);
            c.ArcTo(new Point(radius, h), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w - radius, h), true, false);
            c.ArcTo(new Point(w, h - radius), new Size(radius, radius), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w, shoulder), true, false);
            c.ArcTo(new Point(w + shoulder, 0), new Size(shoulder, shoulder), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    /// <summary>
    /// The headline's third line (A1): why it waits, what it runs, how it ended — in the state's
    /// colour — and the one action that makes sense from here, flush right.
    /// </summary>
    private void BuildHeadLine()
    {
        if (_headHost is null) return;
        string key = BoardData.HeadOf(_data);
        if (key == _headKey && _headLine is not null) return;
        _headKey = key;
        if (_headLine is not null) _headHost.Children.Remove(_headLine);
        foreach (var old in _headHost.Children.OfType<FrameworkElement>().Where(e => Equals(e.Tag, "head-action")).ToList())
            _headHost.Children.Remove(old);

        var h = _data.Head;
        var line = new StackPanel { Orientation = Orientation.Horizontal, Height = 18 };
        double actionW = 0;
        if (h.Action.Length > 0)
        {
            bool open = h.Action.StartsWith("Open");
            var action = Draw.Text(h.Action, 11.5, open ? Draw.Amber : Draw.Mute, FontWeights.SemiBold);
            action.Tag = "head-action";
            action.Cursor = Cursors.Hand;
            action.Background = Brushes.Transparent;
            action.ToolTip = open ? "Open DeepSeek Harness" : "Close it out — the same as ticking it in the app";
            string? id = _data.Front?.Id;
            action.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (open) OpenApp(id);
                else if (id is not null) Dismiss(id);
            };
            actionW = Draw.TextWidth(h.Action, 11.5, FontWeights.SemiBold) + 2;
            _headHost.Add(action, TextX + TextW - actionW, 61);
        }

        var (tone, weight, mono) = h.Kind switch
        {
            "attention" => (Draw.Amber, FontWeights.SemiBold, false),
            "error" => (Draw.Red, FontWeights.SemiBold, false),
            "done" => (Draw.Green, FontWeights.SemiBold, false),
            "tool" => (Draw.C("#8FB9FF"), FontWeights.Normal, true),
            "thinking" => (Draw.C("#8FB9FF"), FontWeights.Normal, false),
            _ => (Draw.Mute, FontWeights.Normal, false),
        };
        // Everything on the line shares one room, left of the action: the lead first, then the quiet
        // rest, then the bar — each trimmed or dropped rather than running under the action.
        double room = TextW - actionW - (actionW > 0 ? 12 : 0);
        if (h.Lead.Length > 0)
        {
            var lead = Draw.Text(h.Lead, mono ? 11 : 12, tone, weight);
            if (mono) lead.FontFamily = Mono;
            lead.MaxWidth = room;
            line.Children.Add(lead);
            room -= Math.Min(room, Draw.TextWidth(h.Lead, mono ? 11 : 12, weight, mono ? Mono : null) + 2);
        }
        if (h.Rest.Length > 0 && room > 24)
        {
            var rest = Draw.Text(h.Rest, 11.5, Draw.Mute);
            double gap = h.Lead.Length > 0 ? 8 : 0;
            rest.Margin = new Thickness(gap, 0, 0, 0);
            rest.MaxWidth = room - gap;
            line.Children.Add(rest);
            room -= Math.Min(room, gap + Draw.TextWidth(h.Rest, 11.5) + 2);
        }
        // the context bar rides on the working line — only against a real budget (rule 1)
        if (h.Kind is "tool" or "thinking" or "working" && _data.HasBudget && _data.ContextTokens > 0 && room >= 70)
        {
            var bar = new Canvas { Width = 60, Height = 3, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new System.Windows.Shapes.Rectangle { Width = 60, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Draw.B(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)) });
            bar.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(2, 60 * _data.ContextFill), Height = 3, RadiusX = 1.5, RadiusY = 1.5,
                Fill = Draw.Cached(_data.ContextHot ? Draw.Amber : Draw.Blue),
            });
            line.Children.Add(bar);
        }
        _headLine = line;
        _headHost.Add(line, TextX, 61);
    }

    static readonly FontFamily Mono = new("Consolas");

    /// <summary>
    /// Size the tab to what it says — or to the alert it is showing — and centre it.
    ///
    /// The resting width is measured from the runs (a fixed width was wrong in one direction or the
    /// other). While the island is up, the tab eases between that and the island's own size, so the
    /// alert grows OUT of the tab rather than appearing over it.
    /// </summary>
    private void LayoutTab(bool measure = true)
    {
        if (_tabHost is null || _tabLine is null || _tabShape is null || _tabChevron is null || _restLayer is null) return;
        // measuring the runs is a layout pass: done when the words change, not on every frame
        if (measure || _restW <= 0)
        {
            double lineW = _tabLine.LineWidth();
            Canvas.SetTop(_tabLine, 13.7 - _tabLine.LineHeight() / 2);
            double textX = Canvas.GetLeft(_tabLine);
            // runs, a breath, the chevron, and room before the rounded corner
            _restW = Math.Min(WinW - 24, Math.Max(128, textX + lineW + 34));
            Canvas.SetLeft(_tabChevron, _restW - 24);
            _restLayer.Width = _restW;
            _tabW = -1;
        }
        double restW = _restW;

        double e = Ease(_islandEase);
        double w = restW + (Math.Max(restW, _islandW) - restW) * e;
        double h = TabH + (Math.Max(TabH, _islandH) - TabH) * e;
        if (Math.Abs(w - _tabW) > 0.25 || Math.Abs(h - _tabHNow) > 0.25)
        {
            _tabW = w; _tabHNow = h;
            _tabShape.Data = NotchShape(w, h, 12, h <= TabH + 0.5 ? 15 : h * .48);
            _tabHost.Width = w;
            _tabHost.Height = h;
            Canvas.SetLeft(_tabHost, (WinW - w) / 2);
            if (_shimmer is not null && _shimmerBase is not null)
            {
                var edge = LowerEdge(restW, TabH, 15);
                _shimmer.Data = edge;
                _shimmerBase.Data = edge;
            }
        }
        Canvas.SetLeft(_restLayer, (w - restW) / 2);
        if (_shimmer is not null) Canvas.SetLeft(_shimmer, (w - restW) / 2);
        if (_shimmerBase is not null) Canvas.SetLeft(_shimmerBase, (w - restW) / 2);
        if (_islandLayer is not null)
        {
            double left = (w - Math.Max(restW, _islandW)) / 2;
            Canvas.SetLeft(_islandLayer, left);
            // the alert is clipped to the shape it is in, so a folding alert never draws past its black
            if (_islandEase > 0 && _islandEase < 1)
                _islandLayer.Clip = new RectangleGeometry(new Rect(-left, 0, w, h), Math.Min(15, h / 2), Math.Min(15, h / 2));
            else if (_islandLayer.Clip is not null)
                _islandLayer.Clip = null;
        }
    }

    /// <summary>
    /// The tab's lower rim as an open path: down the last stretch of the left side, round the corner,
    /// along the bottom, round the other corner and up — just inside the fill, so a stroke on it lies on
    /// the edge. `radius` is the tab's own lower-corner radius (NotchShape).
    /// </summary>
    static Geometry LowerEdge(double w, double h, double radius)
    {
        const double inset = .9;
        double r = Math.Min(radius, Math.Min(w, h) / 2) - inset;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(inset, h - inset - r - 6), false, false);
            c.LineTo(new Point(inset, h - inset - r), true, false);
            c.ArcTo(new Point(inset + r, h - inset), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w - inset - r, h - inset), true, false);
            c.ArcTo(new Point(w - inset, h - inset - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(w - inset, h - inset - r - 6), true, false);
        }
        g.Freeze();
        return g;
    }

    // ── the island ──────────────────────────────────────────────────────────────

    /// <summary>Is an alert holding the tab open right now?</summary>
    bool IslandUp => _islandKind.Length > 0 && _t < _islandUntil;

    /// <summary>
    /// Spot the moments the island exists for: a session that has JUST started waiting on you (X1),
    /// just failed (X4), or just finished (D1). "Just" is the row's own `since`, under 10 s old, so a
    /// window the host respawns does not re-announce a question that has been waiting for an hour.
    /// An alert outranks a finish, and a newer event of the same rank replaces an older one.
    /// </summary>
    private void CheckIsland(StateFile state, bool stale)
    {
        if (_opt.Island is { Length: > 0 } forced)
        {
            if (_islandKind.Length == 0)
            {
                string want = forced switch { "x1" => "attention", "x4" => "error", _ => "done" };
                var row = state.Sessions.FirstOrDefault(r => r.Status == want) ?? state.Sessions.FirstOrDefault();
                if (row is not null) ShowIsland(forced is "x1" or "x4" ? forced : "d1", row, hold: true);
            }
            return;
        }
        SessionRow? pick = null;
        string pickKind = "";
        // only the sessions on the board now are remembered, so the map cannot outgrow it
        foreach (var gone in _statusById.Keys.Where(id => !state.Sessions.Any(r => r.Id == id)).ToList())
            _statusById.Remove(gone);
        foreach (var row in state.Sessions)
        {
            _statusById.TryGetValue(row.Id, out var was);
            _statusById[row.Id] = row.Status;
            if (was == row.Status || stale || row.Since <= 0 || Live.NowMs() - row.Since > 10_000) continue;
            string kind = row.Status switch { "attention" => "x1", "error" => "x4", "done" => "d1", _ => "" };
            if (kind.Length == 0) continue;
            if (pick is null || Rank(kind) > Rank(pickKind) || (Rank(kind) == Rank(pickKind) && row.Since > pick.Since))
            {
                pick = row; pickKind = kind;
            }
        }
        // an open board already says it in its headline; an island there would fold it under the pointer
        if (pick is not null && _p < .5 && (!IslandUp || Rank(pickKind) >= Rank(_islandKind))) ShowIsland(pickKind, pick, hold: false);
        // the session it was announcing moved on (answered, resumed): fold it rather than lie
        if (IslandUp && !state.Sessions.Any(r => r.Id == _islandId && KindFor(r.Status) == _islandKind)) FoldIsland();
    }

    static int Rank(string kind) => kind is "x1" or "x4" ? 2 : kind == "d1" ? 1 : 0;
    static string KindFor(string status) => status switch { "attention" => "x1", "error" => "x4", "done" => "d1", _ => "" };

    private void ShowIsland(string kind, SessionRow row, bool hold)
    {
        _islandKind = kind;
        _islandId = row.Id;
        _islandRow = row;
        _islandStart = _t;
        _islandUntil = hold ? double.MaxValue : _t + (kind == "d1" ? IslandDoneSeconds : IslandAlertSeconds);
        Log.Write($"island — {kind} for {row.Id} \"{row.Title}\"");
        BuildIsland();
    }

    private void FoldIsland()
    {
        if (_islandKind.Length == 0) return;
        _islandUntil = Math.Min(_islandUntil, _t);
        Log.Write($"island — {_islandKind} folded");
    }

    SessionRow? _islandRow;

    /// <summary>
    /// The island's content (X1 / X4 / D1, round 12): a small bot in the session's colours wearing the
    /// state's face, one sentence, and — for an alert — the one button that answers it.
    /// </summary>
    private void BuildIsland()
    {
        if (_islandLayer is null || _islandRow is null) return;
        _islandLayer.Children.Clear();
        var row = _islandRow;
        bool done = _islandKind == "d1";
        double botSize = done ? 28 : 32;
        _islandH = done ? 44 : 50;
        var tone = Live.ToneOf(row.Status);
        _islandOrb = new OrbView(botSize, tone, Live.IdentityOf(row.Identity));
        _islandOrb.SetFace(_islandKind switch { "x1" => Face.Pleading, "x4" => Face.Xx, _ => Face.Starry });
        _islandOrb.SetRing(1);
        double botBox = botSize + 5;
        _islandLayer.Add(_islandOrb, 10, (_islandH - botBox) / 2);

        var text = new TextBlock { FontFamily = Draw.Ui, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        string title = row.Title.Length > 0 ? row.Title : Live.ProjectOf(row);
        switch (_islandKind)
        {
            case "x1":
            {
                var head = Live.HeadOf(row);
                string tool = row.ApprovalTool ?? "";
                if (tool == "question")
                {
                    // a question reads as who asks, then the question itself
                    text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, Foreground = Draw.Cached(Draw.Ink) });
                    text.Inlines.Add(new System.Windows.Documents.Run(row.ApprovalReason is { Length: > 0 } q ? " · " + q : " has a question for you")
                        { Foreground = Draw.Cached(Draw.Ink2) });
                }
                else if (tool.Length > 0 && head.Lead.StartsWith(tool))
                {
                    text.Inlines.Add(new System.Windows.Documents.Run(tool) { FontWeight = FontWeights.SemiBold, Foreground = Draw.Cached(Draw.Ink) });
                    text.Inlines.Add(new System.Windows.Documents.Run(head.Lead[tool.Length..]) { Foreground = Draw.Cached(Draw.Ink2) });
                }
                else
                {
                    text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, Foreground = Draw.Cached(Draw.Ink) });
                    text.Inlines.Add(new System.Windows.Documents.Run(" " + head.Lead) { Foreground = Draw.Cached(Draw.Ink2) });
                }
                break;
            }
            case "x4":
                text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, Foreground = Draw.Cached(Draw.Ink) });
                text.Inlines.Add(new System.Windows.Documents.Run(" · failed") { Foreground = Draw.Cached(Draw.Red) });
                if (row.ActivityLabel is { Length: > 0 } last)
                    text.Inlines.Add(new System.Windows.Documents.Run($" · last tool {last}") { Foreground = Draw.Cached(Draw.Mute) });
                break;
            default:
                text.Inlines.Add(new System.Windows.Documents.Run(title) { FontWeight = FontWeights.SemiBold, Foreground = Draw.Cached(Draw.Ink) });
                text.Inlines.Add(new System.Windows.Documents.Run(" done") { Foreground = Draw.Cached(Draw.Green) });
                if (row.Steps > 0)
                    text.Inlines.Add(new System.Windows.Documents.Run(row.Steps == 1 ? " · 1 step" : $" · {row.Steps} steps") { Foreground = Draw.Cached(Draw.Mute) });
                break;
        }
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = Math.Min(330, text.DesiredSize.Width);
        text.Width = textW;
        double textX = 10 + botBox + 10;
        _islandLayer.Add(text, textX, (_islandH - text.DesiredSize.Height) / 2);

        double w = textX + textW + 16;
        if (!done)
        {
            bool alert = _islandKind == "x1";
            var label = new TextBlock
            {
                Text = "Open", FontFamily = Draw.Ui, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                Foreground = Draw.Cached(alert ? Draw.C("#1A1405") : Draw.Ink),
            };
            var pill = new Border
            {
                Child = label, CornerRadius = new CornerRadius(99), Padding = new Thickness(12, 4, 12, 5),
                Background = alert ? Draw.B(Draw.Amber) : Draw.B(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
                Cursor = Cursors.Hand, ToolTip = "Open DeepSeek Harness",
            };
            // Open answers the alert and nothing else: the board does not unfold under the pointer that
            // pressed it (iArxic, 2026-10-23) — it stays folded until the pointer has left the notch.
            pill.MouseLeftButtonDown += (_, e) => { e.Handled = true; _holdShut = true; OpenApp(_islandId); FoldIsland(); };
            pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // a capsule: WPF does not clamp a corner radius larger than half the height — 99 drew a
            // lemon-shaped oval (iArxic, 2026-10-23: "a weird shape") — so the radius is exactly half
            pill.CornerRadius = new CornerRadius(pill.DesiredSize.Height / 2);
            _islandLayer.Add(pill, w - 4, (_islandH - pill.DesiredSize.Height) / 2);
            w += pill.DesiredSize.Width + 8;
        }
        _islandW = w;
        _islandLayer.Width = w;
        _islandLayer.Height = _islandH;
    }

    // ── the live data ───────────────────────────────────────────────────────────

    /// <summary>
    /// The rows under the headline (A1): a small bot in the session's colours wearing its face, the
    /// title, what it is doing in a word, and how long it has been in that state.
    /// </summary>
    private void RebuildStatic()
    {
        if (_linesHost is null) return;
        var d = _data;
        _linesHost.Children.Clear();
        _rowViews.Clear();
        _ageTexts.Clear();
        _rowBots.Clear();
        _rowBotState.Clear();
        _foldIndex = -1;                 // a rebuild cancels a fold: the row it was folding is gone
        _foldAt = -1;
        if (d.Rows.Count == 0) return;

        _linesHost.Add(new System.Windows.Shapes.Rectangle { Width = TextW, Height = 1, Fill = Draw.B(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)) },
            TextX, RowsTop - 5);
        const double rowX = TextX - 10, rowW = TextW + 10;
        // More rows than fit scroll inside a clipped viewport, with the wheel; the edges fade where
        // there is more to see, and a thin thumb says where you are.
        int visible = Math.Min(d.Rows.Count, VisibleRows);
        bool scrolls = d.Rows.Count > visible;
        var panel = new Canvas { Width = rowW, Height = d.Rows.Count * RowPitch };
        _rowsScroll = new TranslateTransform();
        panel.RenderTransform = _rowsScroll;
        _rowsViewport = new Canvas { Width = rowW, Height = visible * RowPitch, ClipToBounds = true, Background = Brushes.Transparent };
        _rowsViewport.Children.Add(panel);
        _rowsFade = null;
        _rowsThumb = null;
        _scrollTarget = Math.Clamp(_scrollTarget, 0, Math.Max(0, (d.Rows.Count - visible) * RowPitch));
        if (scrolls)
        {
            _rowsFade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            foreach (var (stop, offset) in new[] { (Colors.Black, 0.0), (Colors.Black, .12), (Colors.Black, .88), (Colors.Black, 1.0) })
                _rowsFade.GradientStops.Add(new GradientStop(stop, offset));
            _rowsViewport.OpacityMask = _rowsFade;
            double thumbH = Math.Max(14, visible * RowPitch * visible / d.Rows.Count);
            _rowsThumb = new System.Windows.Shapes.Rectangle
            {
                Width = 3, Height = thumbH, RadiusX = 1.5, RadiusY = 1.5,
                Fill = Draw.B(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false,
            };
            _linesHost.Add(_rowsThumb, rowX + rowW + 4, RowsTop);
        }
        _linesHost.Add(_rowsViewport, rowX, RowsTop);
        for (int i = 0; i < d.Rows.Count; i++)
        {
            var r = d.Rows[i];
            var row = new Canvas { Width = rowW, Height = RowPitch, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var hover = new Border { Width = rowW, Height = RowPitch - 2, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent };
            row.Add(hover, 0, 1);

            var bot = new MiniBotView(14, Live.ToneOf(r.Status), r.Identity);
            bot.SetSweep(r.Status is "done" or "error" or "attention" ? 1 : 0);
            row.Add(bot, 10 - 7 + 1, (RowPitch - 28) / 2);
            _rowBots.Add(bot);
            _rowBotState.Add((r.Status, r.Face));

            const double ageW = 34, xW = 16;
            double rightW = r.Right.Length > 0 ? Draw.TextWidth(r.Right, r.RightMono ? 10.5 : 11, font: r.RightMono ? Mono : null) + 6 : 0;
            double titleX = 36, titleW = rowW - titleX - rightW - ageW - xW - 6;

            var title = new TextBlock { FontFamily = Draw.Ui, FontSize = 12, Width = Math.Max(20, titleW), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            title.Inlines.Add(new System.Windows.Documents.Run(r.Title) { Foreground = Draw.Cached(r.Teammate ? Draw.Ink2 : Draw.Ink) });
            if (r.Tail.Length > 0)
                title.Inlines.Add(new System.Windows.Documents.Run(" · " + r.Tail) { FontSize = 10.5, Foreground = Draw.Cached(Draw.RowTail) });
            row.Add(title, titleX, 6);

            if (r.Right.Length > 0)
            {
                var right = Draw.Text(r.Right, r.RightMono ? 10.5 : 11, r.Status switch
                {
                    "attention" => Draw.Amber, "done" => Draw.Green, "error" => Draw.Red, _ => Draw.C("#8FB9FF"),
                });
                if (r.RightMono) right.FontFamily = Mono;
                right.Width = rightW;
                right.TextAlignment = TextAlignment.Right;
                row.Add(right, rowW - xW - ageW - rightW, 7);
            }
            var age = Draw.Text(r.Age, 11, Draw.Mute);
            age.Width = ageW;
            age.TextAlignment = TextAlignment.Right;
            row.Add(age, rowW - xW - ageW, 7);
            _ageTexts.Add(age);

            // a settled row can be closed out from here; the ✕ shows while the pointer is on the row
            UIElement? mark = null;
            if (r.Status is "done" or "error")
            {
                string closeId = r.Id;
                var x = Draw.DismissMark();
                x.Opacity = 0;
                x.MouseLeftButtonDown += (_, e) => { e.Handled = true; Dismiss(closeId); };
                row.Add(x, rowW - xW + 1, (RowPitch - 14) / 2);
                mark = x;
            }
            row.MouseEnter += (_, _) => { hover.Background = Draw.B(Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)); if (mark is not null) mark.Opacity = 1; };
            row.MouseLeave += (_, _) => { hover.Background = Brushes.Transparent; if (mark is not null) mark.Opacity = 0; };

            // a row is a button: it asks the big bot to wear that session. The window is
            // WS_EX_NOACTIVATE, so the click does not take the focus off whatever you are doing.
            string id = r.Id;
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (e.ClickCount >= 2) OpenApp(id);
                else TogglePin(id);
            };
            panel.Add(row, 0, i * RowPitch);
            _rowViews.Add(row);
        }
    }

    /// <summary>
    /// Clicking a row pins the orb to that session; clicking it again (or the card's background)
    /// lets go. The board is rebuilt from the last state rather than waiting up to 150 ms for the
    /// next poll, so the answer is immediate.
    /// </summary>
    private void TogglePin(string id)
    {
        if (string.IsNullOrEmpty(id)) return;
        _pinnedId = _pinnedId == id ? null : id;
        Log.Write($"pinned — {(_pinnedId is null ? "(none)" : _pinnedId)}");
        if (_lastState is not null) ApplyBoard(Live.Build(_lastState, _pinnedId, _settings.MaxRows, _data.Stale, _settings.ShowProject));
    }

    /// <summary>
    /// Bring DeepSeek Harness to the front.
    ///
    /// The app's `dsh://open` link alone did not do it on Windows (iArxic, 2026-10-23: "pressing open
    /// doesnt work"): the link starts a second instance that hands off to the running one, whose
    /// `focus()` is refused by the foreground lock — neither process received the click, so the app
    /// only flashed its taskbar button, and when it was already on screen nothing changed at all.
    /// This process DID receive the click, so it raises the app's window itself, and falls back to the
    /// link only when there is no visible window to raise (not running, or hidden in the tray). The
    /// app has no link that opens one session, so the row the human picked stays pinned here.
    /// </summary>
    private void OpenApp(string? sessionId = null)
    {
        // Ask the app to switch to the session first (the host holds it for the in-app half to take),
        // then bring the app forward — both, because the app may be behind and on another session.
        if (!string.IsNullOrEmpty(sessionId)) RequestOpenSession(sessionId);
        try
        {
            var window = Native.FindAppWindow(out int seen, out bool fromTray);
            if (window != IntPtr.Zero)
            {
                Native.Raise(window);
                Log.Write($"open app — raised its window{(fromTray ? " out of the tray" : "")}");
            }
            else
            {
                Log.Write($"open app — no window titled \"… DeepSeek Harness\" among {seen} top-level windows");
                WakeApp();
            }
            // Raising is checked, not assumed: if the app is not in front half a second later, the app
            // is woken through its own link (which also recreates a window it closed) and raised again.
            AfterMs(600, () =>
            {
                var again = Native.FindAppWindow(out _, out _);
                if (again != IntPtr.Zero && Native.IsInFront(again)) { Log.Write("open app — it is in front"); return; }
                WakeApp();
                AfterMs(900, () =>
                {
                    var late = Native.FindAppWindow(out _, out _);
                    if (late != IntPtr.Zero) Native.Raise(late);
                    Log.Write(late != IntPtr.Zero && Native.IsInFront(late) ? "open app — in front after waking it" : "open app — it did not come to the front");
                });
            });
        }
        catch (Exception e) { Log.Write("open app failed: " + e.Message); }
    }

    /// <summary>
    /// The app's own `dsh://open`, handed to Explorer: it un-minimizes the window and recreates one the
    /// app had closed. Started from this process instead, the app's second instance did not reach the
    /// running app; from the shell it does (measured 2026-10-23).
    /// </summary>
    private static void WakeApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "dsh://open") { UseShellExecute = false });
            Log.Write("open app — dsh://open through Explorer");
        }
        catch (Exception e) { Log.Write("open app — dsh://open failed: " + e.Message); }
    }

    /// <summary>Run once on the UI thread after `ms` milliseconds.</summary>
    private static void AfterMs(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>POST `{ sessionId }` to the host's open door; the app's in-app half takes it.</summary>
    private async void RequestOpenSession(string sessionId)
    {
        string? url = _lastState?.OpenUrl;
        if (string.IsNullOrEmpty(url))
        {
            Log.Write("open session — the host publishes no openUrl (schema 3, or no port yet)");
            return;
        }
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, string> { ["sessionId"] = sessionId });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content);
            Log.Write($"open session — {sessionId}: {(int)response.StatusCode}");
        }
        catch (Exception e) { Log.Write("open session failed: " + e.Message); }
    }

    /// <summary>
    /// Start the row's fold before the write goes out: the leave should look like something the
    /// human did, not like the row blinking out at the next poll. If the write fails the poll
    /// rebuilds the row where it was.
    /// </summary>
    private void StartFold(string id)
    {
        int index = _data.Rows.FindIndex(row => row.Id == id);
        if (index < 0 || index >= _rowViews.Count) return;
        _foldIndex = index;
        _foldAt = _t;
    }

    /// <summary>
    /// Remove a settled session from the list.
    ///
    /// This is the SAME write the sidebar's ✓ makes — `POST { sessionId, state: "finished" }` to
    /// `plugins/session-finished` — so the row leaves the overlay and the app's own tick agrees with
    /// it. The overlay keeps no "dismissed" list of its own; a second store could only ever
    /// disagree with the first. The host publishes the route's address in the state file because
    /// this process has no `ctx` and no way to discover the port.
    /// </summary>
    private async void Dismiss(string id)
    {
        StartFold(id);
        _winkUntil = _t + .9;
        string? url = _lastState?.FinishedUrl;
        if (string.IsNullOrEmpty(url))
        {
            WriteManualMark(id);
            return;
        }
        try
        {
            string json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["sessionId"] = id,
                ["state"] = "finished",
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Http.PostAsync(url, content);
            string body = await response.Content.ReadAsStringAsync();
            Log.Write($"dismiss — {(int)response.StatusCode} {body}");
            if (!response.IsSuccessStatusCode)
            {
                // The route answered and refused (it validates its input, and it is 404 when
                // `plugins/session-finished` is not mounted). A ✕ that visibly does nothing is worse
                // than the fallback, so make the same mark the honest way and say which store took it.
                Log.Write("dismiss — the route refused it, falling back to the manual mark");
                WriteManualMark(id);
                return;
            }
            if (_pinnedId == id) _pinnedId = null;
            PollState();
        }
        catch (Exception e)
        {
            Log.Write("dismiss failed: " + e.Message);
        }
    }

    /// <summary>
    /// The fallback for a host that has not published `finishedUrl` (no port yet, or host@5 and
    /// earlier).
    ///
    /// It writes the SAME file and the SAME value the sidebar's ✓ writes — `manual.json`, the
    /// human's own marks — which this feature already reads, so it is not a second opinion. The
    /// difference is only that `plugins/session-finished` keeps its own copy in memory until it
    /// next applies, so the app's sidebar tick can lag behind until then.
    ///
    /// The home is `DSH_HOME` when the environment sets it, because that is what the host itself
    /// resolves (`lib/exe.js`, `dshHome`); `%USERPROFILE%\.dsh` is only the default when it does not.
    /// It used to be hardcoded, which made the ✕ write a file nobody reads on any machine with a
    /// custom home — the row stayed, the fold played, and nothing logged a failure because the write
    /// itself succeeded.
    /// </summary>
    private void WriteManualMark(string id)
    {
        try
        {
            // The host's own default, not a path derived from --state: the state file may be a
            // fixture, and the mark always belongs to the machine's real store.
            string home = Environment.GetEnvironmentVariable("DSH_HOME")?.Trim() ?? "";
            if (home.Length == 0)
                home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            string file = Path.Combine(home, "state", "session-finished", "manual.json");
            var marks = File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new()
                : new Dictionary<string, string>();
            marks[id] = "finished";
            // The store belongs to another plugin, so the directory may not exist yet on a machine
            // that never installed it; the mark is still the human's and still means the same thing.
            string? storeDir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(storeDir)) Directory.CreateDirectory(storeDir);
            string tmp = file + ".overlay.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(marks, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(tmp, file, overwrite: true);
            Log.Write($"dismiss — marked {id} finished in {file} (no finishedUrl published)");
            if (_pinnedId == id) _pinnedId = null;
            PollState();
        }
        catch (Exception e)
        {
            Log.Write("dismiss (manual mark) failed: " + e.Message);
        }
    }

    /// <summary>The live parts keep their elements — only their colours change.</summary>
    private void UpdateLive()
    {
        var d = _data;
        _orb?.SetColours(d.Badge, d.Identity);
        for (int i = 0; i < _miniBots.Count; i++)
        {
            _miniBots[i].SetColours(d.MiniStates[i], d.MiniIdentities[i]);
            _miniStates[i] = d.MiniStates[i];
            _miniStatus[i] = i < d.MiniStatus.Count ? d.MiniStatus[i] : "working";
            _miniFaces[i] = i < d.MiniFaces.Count ? d.MiniFaces[i] : Face.Idle;
        }
        BuildHeadLine();
    }

    /// <summary>The words that move on their own: the tab's summary, the headline, the sub, the ages.</summary>
    private void UpdateText()
    {
        var d = _data;
        if (_tabLine is not null)
        {
            var runs = d.TabRuns;
            string key = BoardData.TabKey(runs);
            if (key != _tabKey)
            {
                _tabKey = key;
                _tabLine.Set(runs);
                LayoutTab();          // the tab is sized by its own words
            }
        }
        if (_titleText is not null && _titleText.Text != d.Title) _titleText.Text = d.Title;
        if (_subText is not null && _subText.Text != d.Sub) _subText.Text = d.Sub;
        for (int i = 0; i < _ageTexts.Count && i < d.Rows.Count; i++)
            if (_ageTexts[i].Text != d.Rows[i].Age) _ageTexts[i].Text = d.Rows[i].Age;
    }

    /// <summary>Take a new board: rebuild what changed, re-colour what did not.</summary>
    private void ApplyBoard(BoardData next)
    {
        string before = _data.Status;
        // the falling-asleep strip plays only when a LIVE feed goes quiet, never on a cold start
        if (next.Stale && !_data.Stale && _lastT >= 0) _staleSince = _t;
        if (next.SessionCount > _data.SessionCount && _data.SessionCount > 0) _glanceUntil = _t + .8;
        bool shape = next.ShapeKey != _data.ShapeKey;
        bool content = next.ContentKey != _data.ContentKey;
        _data = next;
        if (shape) Rebuild();
        else if (content) RebuildStatic();
        // --fold <p> is the still-frame proof of the fold: pin a row half-folded with no write
        if (_opt.Fold >= 0 && _foldIndex < 0 && _rowViews.Count > 0) { _foldIndex = 0; _foldAt = _t; }
        UpdateLive();
        UpdateText();
        if (next.Status != before) _flare = 1;
    }

    /// <summary>
    /// Read the state file's payload and put it on the board.
    ///
    /// A file that does not parse as an object is ignored, and the previous board stays until the
    /// heartbeat says the feed is stale — inventing a board from a file that said nothing would be
    /// exactly the "fill the field with a guess" this contract forbids. The old flat
    /// `{title, sub, status}` body that the P0 rig used is gone with it: schema 3 always has rows.
    /// </summary>
    private void ApplyState(JsonElement root)
    {
        var state = Live.Parse(root);
        if (state is null) return;
        _lastGoodState = _sw.Elapsed.TotalSeconds;
        _lastState = state;

        // `settings` are the host's and are applied whenever they change — this is the poll that
        // notices, so a scale/anchor/screen/maxRows change in the file takes effect without a restart.
        ApplySettings(state.Settings);

        // --pin <n>: hold the Nth row as if it had been clicked. A click cannot be simulated
        // while another app owns the pointer (a game with the cursor locked recentres it), so
        // this is how the pin is checked from the outside.
        if (_opt.PinIndex >= 0 && _pinnedId is null && _opt.PinIndex < state.Sessions.Count)
        {
            _pinnedId = state.Sessions[_opt.PinIndex].Id;
            Log.Write($"pin #{_opt.PinIndex} — {_pinnedId}");
        }

        // --dismiss <n>: press the ✕ on the Nth row. Same path the click takes, because a click
        // cannot be simulated while another app owns the pointer.
        if (_opt.DismissIndex >= 0 && !_dismissed && _opt.DismissIndex < state.Sessions.Count)
        {
            _dismissed = true;
            Dismiss(state.Sessions[_opt.DismissIndex].Id);
        }

        var board = Live.Build(state, _pinnedId, _settings.MaxRows, IsStale(state), _settings.ShowProject);
        ApplyBoard(board);
        CheckIsland(state, board.Stale);
        string logKey = $"{board.SessionCount}|{board.MiniStates.Count}|{board.Title}|{board.ContentKey}|{board.Stale}";
        if (logKey != _lastLoggedKey)
        {
            _lastLoggedKey = logKey;
            Log.Write($"state — {board.SessionCount} session(s), {board.MiniStates.Count} in the tab" +
                      (board.MiniOverflow > 0 ? $" (+{board.MiniOverflow})" : "") +
                      $", status {(board.Stale ? "stale" : board.Status)}, retire {state.Retire}" +
                      $", context {(board.HasBudget ? $"{board.ContextTokens}/{board.ContextBudget}" : "no budget")}");
            Log.Write($"  tab \"{board.TabText}\" · title \"{board.Title}\" · sub \"{board.Sub}\"");
            Log.Write($"  line [{board.Head.Kind}] \"{board.Head.Lead}\" \"{board.Head.Rest}\" {board.Head.Action}");
            Log.Write($"  rows {string.Join(" | ", board.Rows.Select(r => $"[{r.Status}/{r.Face}] {r.Title} · {r.Right} · {r.Age}"))}");
            Log.Write($"  faces front {board.FrontFace} · tab {string.Join(",", board.MiniFaces)}");
        }
    }

    // ── motion ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// One motion step per compositor frame.
    ///
    /// This used to be a `DispatcherTimer` at 16 ms, and it was the reason the unfold looked like low
    /// fps: measured on 2026-10-21 (laptop, 144 Hz panel, `render tier=2`), the timer delivered every
    /// **25–29 ms — 35–40 fps with jitter up to 48 ms** — while the work inside `Tick` took **0.2 ms**.
    /// A `DispatcherTimer` is serviced at `DispatcherPriority.Background` by default, so it queues
    /// behind layout and the render pass; at 16 ms it was already fighting a 6.9 ms frame. The
    /// compositor's own `Rendering` event fires once per frame the window is about to draw, which is
    /// exactly the cadence an animation wants, and needs no interval to tune.
    ///
    /// WPF can raise it more than once for one frame, hence the `RenderingTime` guard.
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs args)
        {
            if (args.RenderingTime == _lastRenderTime) return;
            _lastRenderTime = args.RenderingTime;
        }
        // An optional cap (--fps, or the adaptive rule below): the compositor's cadence is not always
        // what the work deserves, and the idle motion of a blink and a breath does not need 144 Hz.
        if (_fpsCap > 0)
        {
            double nowMs = _sw.Elapsed.TotalMilliseconds;
            if (nowMs - _lastTickMs < 1000.0 / _fpsCap - 0.5) return;
            _lastTickMs = nowMs;
        }
        Tick();
        if (_opt.RecordDir is not null) RecordFrame();
    }

    private void Tick()
    {
        double t = _recordT >= 0 ? _recordT : _sw.Elapsed.TotalSeconds;
        double rawGapMs = _lastT < 0 ? 0 : (t - _lastT) * 1000;
        double dt = Math.Min(0.1, t - _lastT);
        _lastT = t; _t = t;
        double workStart = _sw.Elapsed.TotalMilliseconds;

        // MOTION OFF: the IDLE motion reads a clock that does not run. `t` stays real for everything
        // that has to finish — a session arriving, a retired row folding, a ring fading — because a
        // person who asked for no motion still expects a new session to appear. What freezes is the
        // breath, the blink, the pulses and a state's transition, which is exactly the part that
        // draws the eye away from the work.
        double st = _motionOff ? StillTime : t;

        // the board unfolds on hover and folds away when the pointer leaves
        if (_opt.OpenAtMs >= 0)
        {
            double sinceStart = t * 1000;
            _forceOpen = sinceStart >= _opt.OpenAtMs && (_opt.CloseAtMs < 0 || sinceStart < _opt.CloseAtMs);
        }
        // While an alert holds the tab open the board stays folded: the pointer on the island is there
        // to read it or press Open. A click on it folds it, and then the board opens as usual.
        // a board forced open (--open, a film's --open-at) answers the alert itself: fold the island
        if (_forceOpen && IslandUp && _opt.Island is null) FoldIsland();
        bool islandUp = IslandUp;
        if (islandUp && _want) _islandUntil = Math.Max(_islandUntil, t + 1.0);
        bool mayOpen = _forceOpen || ((_want || t < _collapseAt) && !islandUp && !_holdShut);
        if (mayOpen && _fullscreen && !_forceShow) mayOpen = false;
        double target = mayOpen ? 1 : 0;
        if (_motionOff) _p = target;
        else if (Math.Abs(target - _p) > 0.0005) _p += (target - _p) * Math.Min(1, dt / ExpandSeconds);
        else _p = target;
        ApplyProgress();

        // the eyes follow the pointer — eased over ~220 ms. Asked of `_root` rather than of the
        // window: the orb's coordinates are DESIGN units, and `_root` is the element the scale is
        // applied to, so it is the only one that can turn them into the right screen point.
        var cursor = Native.CursorPos();
        var centre = _root.PointToScreen(new Point(_orbX + _orbCenter, _orbY + _orbCenter));
        double dx = cursor.X - centre.X, dy = cursor.Y - centre.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        const double reach = 320;
        double tx = dist < reach ? dx / reach : 0;
        double ty = dist < reach ? dy / reach : 0;
        if (_motionOff) { _lookX = 0; _lookY = 0; }          // centred eyes, not eyes that follow
        else
        {
            _lookX += (tx - _lookX) * Math.Min(1, dt / 0.22 * 3);
            _lookY += (ty - _lookY) * Math.Min(1, dt / 0.22 * 3);
        }

        // ── the front session's face ─────────────────────────────────────────────
        //
        // The big bot wears the FRONT session, so that session's state drives the motion. A
        // transition into a settled state starts that state's motion ONCE, and the motion end in a
        // rest pose instead of looping — an overlay that keeps celebrating a session that finished
        // an hour ago is noise.
        string front = _data.FrontKey;          // identity|status — what the TRANSITION keys on
        // …and the state alone, which is what the POSE keys on. They are two different questions: a
        // switch between two sessions in the same state is a transition, but the face to draw is the
        // state. Comparing the identity to "done" silently matched nothing and every settled state lost
        // its ring and its eyes.
        string frontState = _data.FrontStatus;
        if (front != _frontWas)
        {
            // Logged because "the animation did not play" is otherwise unanswerable from outside: this
            // line says which transition the window actually saw, and when.
            if (_frontWas != "") Log.Write($"pose — front {_frontWas} -> {front}");
            _frontWas = front;
            _finishStart = frontState == "done" ? t : -1;
            _alertStart = frontState == "attention" ? t : -1;
            _errorStart = frontState == "error" ? t : -1;
        }
        // …and with motion off every transition is told it finished 20 s ago, which lands each state
        // on its SETTLED pose: a finished session's rest face, the wide eyes of one waiting on you,
        // the frown of a failure. The state is still drawn; only the performance is skipped.
        if (_motionOff) _finishStart = _alertStart = _errorStart = st - 20;

        // CONTRACT: the last transition's anchor is the clock the pose reads, so a still notch shows
        // where the session ENDED UP rather than the first frame of the motion.
        double poseFrom(double start) => start < 0 ? 1e9 : (st - start) * 1000;

        // idle life: a slow wander UNDER the pointer-following, so it is never perfectly still
        double lx = Math.Clamp(_lookX + .18 * Math.Sin(t / 0.9 * 2 * Math.PI) + .07 * Math.Sin(t / 0.31), -1, 1);
        double ly = Math.Clamp(_lookY + .06 * Math.Sin(t / 1.4 * 2 * Math.PI + 1.1), -1, 1);

        // blink: every 1.9 s, and every other one comes twice — measured against the mock-up's
        // 50 fps motion, where a single 160 ms blink read as thin
        double ms = st * 1000, blinkPhase = ms % 1900;
        int cycle = (int)(ms / 1900);
        double open = blinkPhase < 160 ? 1 - Math.Sin(Math.PI * blinkPhase / 160) : 1;
        if (cycle % 2 == 1)
        {
            double second = blinkPhase - 320;
            if (second is > 0 and < 160) open = Math.Min(open, 1 - Math.Sin(Math.PI * second / 160));
        }
        // a still is a pose, not a moment of a blink: --freeze holds the eyes open
        if (_freezeMs >= 0) open = 1;

        bool arc = false;
        double bend = 0, spread = 1, tilt = 0, ringTarget = 0;
        double moveX = 0, moveY = 0;
        Face face = _data.FrontFace;

        if (frontState == "done")
        {
            ringTarget = 1;
            double et = _freezeMs >= 0 ? _freezeMs : poseFrom(_finishStart);
            // bars until the eyes have shut; the `^^` (and the proud face's smile) only after that
            if (et < 80) face = Face.Idle;
            if (et < 80) open = 1 - Ease(et / 80.0);                                   // the eyes shut
            else if (et < 180) { arc = true; open = 0; bend = Ease((et - 80) / 100.0); }   // the squint rises
            else if (et < 820)
            {
                arc = true; open = 0;
                double u = (et - 180) / 640.0;
                bend = 1 + .10 * Math.Sin(u * 2 * Math.PI);                            // the hold breathes
            }
            else if (et < 920) { arc = true; open = 0; bend = 1 - Ease((et - 820) / 100.0); }  // shut again
            // …and it RESTS in the `^^` face. The approved design has a finished session's face BE the
            // arc; the half-lidded reopen it used to settle into read as "working, but tired" and made a
            // still frame look like nothing had happened. The arc settles and keeps a slow breath.
            else if (et < 1650) { open = 0; arc = true; bend = .55 + .45 * Ease((et - 920) / 730.0); }
            else { open = 0; arc = true; bend = 1.0 + .06 * Math.Sin(et / 700.0); ly += .12; }
        }
        else if (frontState == "attention")
        {
            ringTarget = 1;
            double et = _freezeMs >= 0 ? _freezeMs : poseFrom(_alertStart);
            // it hops once and looks at you, surprised, before it settles into pleading
            // it hops once to say so; the "o" face that came with the hop was removed (2026-10-23)
            if (et < 260) moveY = -5 * Math.Sin(Math.PI * et / 260.0);
            double widen = Ease(Math.Min(1, et / 260.0));
            open = 1 + .34 * widen;
            spread = 1 + .14 * widen;
            // it keeps blinking, just slowly: wide eyes that never blink read as a frozen image
            double bt = et - 700;
            if (bt > 0) { double c = bt % 1300; if (c < 200) open *= 1 - Math.Sin(Math.PI * c / 200.0); }
        }
        else if (frontState == "error")
        {
            ringTarget = 1;
            double et = _freezeMs >= 0 ? _freezeMs : poseFrom(_errorStart);
            // a small shake, then the ✕ ✕
            if (et < 400) moveX = 3 * Math.Sin(et / 40.0 * Math.PI) * (1 - et / 400.0);
        }
        else if (frontState == "working" && !_data.Stale)
        {
            // the one-shot faces, newest intent first; each is a moment and then the rest face returns
            var row = _data.Front;
            if (t < _winkUntil) face = Face.Wink;
            else if (row is not null && row.ApprovedAt > 0 && Live.NowMs() - row.ApprovedAt is >= 0 and < 3000) face = Face.Love;
            else if (t < _glanceUntil) face = Face.Glances;
            if (face == Face.Thinking) { lx = Math.Clamp(lx + .4 * Math.Sin(st / 1.2), -1, 1); ly -= .5; }
        }

        if (_data.Stale)
        {
            // falling asleep: the lids drop, it peeks once more, then the z's
            double asleepFor = t - _staleSince;
            face = asleepFor < 2 ? Face.Tired : asleepFor < 4 ? Face.Peeking : Face.Asleep;
        }
        if (_opt.ForceFace is Face forced) face = forced;

        // the rim breathes; attention flares
        double wave = (Math.Sin(2 * Math.PI * st / 2.4) + 1) / 2;
        double rim = _data.RimBase * (0.8 + 0.2 * wave);
        if (_flare > 0) { rim += 0.35 * _flare; _flare = Math.Max(0, _flare - dt / 1.2); }

        // the status aura behind the main bot — it wears the FRONT session's state
        double glow = 0.18 + 0.12 * wave;
        if (_data.Badge == Draw.Amber) glow = 0.35 + 0.35 * Math.Abs(Math.Sin(2 * Math.PI * st / 1.2));
        if (_data.Badge == Draw.Red) glow = 0.30 + 0.25 * Math.Abs(Math.Sin(2 * Math.PI * st / 1.6));
        if (_flare > 0) glow += 0.30 * _flare;

        // the ring fades rather than snapping, and pulses only while it is asking for you
        // The first frame snaps the ring to its target. A fade that starts at 0 means the first painted
        // frame of a settled session looks ringless — in a still, and for the ~250 ms before it arrives.
        double spinTarget = frontState == "working" && !_data.Stale ? 1 : 0;
        if (!_ringSeeded) { _ringSeeded = true; _ringAlpha = ringTarget; _spinAlpha = spinTarget; }
        _ringAlpha += (ringTarget - _ringAlpha) * Math.Min(1, dt / 0.25);
        _spinAlpha += (spinTarget - _spinAlpha) * Math.Min(1, dt / 0.25);
        // F1d: every spinner on the board shares ONE angle, once round every 2.4 s — bots turning at
        // their own phases and speed read as noise (iArxic, 2026-10-23). Motion off holds it still.
        double spinAngle = _motionOff ? -30 : st * SpinDegreesPerSecond % 360;
        _orb?.SetSpin(_spinAlpha, spinAngle);
        double ringPulse = frontState == "attention" ? .72 + .28 * Math.Abs(Math.Sin(2 * Math.PI * st / 1.2)) : 1;
        _orb?.SetRing(_ringAlpha, ringPulse);

        if (_orb is not null)
        {
            _orb.SetFace(face);
            _orb.SetDim(_data.Stale);
        }
        _orb?.Set(open, lx, ly, rim, glow, arc, bend, spread, tilt);

        // the little bots in the tab: each blinks on its own phase (unless it has settled, where the
        // face IS the state), and each aura breathes
        for (int i = 0; i < _miniBots.Count; i++)
        {
            string status = i < _miniStatus.Count ? _miniStatus[i] : "working";
            double phase = (st + i * 1.9) % 4.6;
            double miniBlink = phase <= 0.14 ? Math.Sin(Math.PI * phase / 0.14) : 0;
            // The AURA is the state and the RING is the identity, so the aura has to be the thing
            // you see first — measured on a 4x zoom of the live tab, a working bot's halo sat at
            // roughly 20 % and the ring read as the only colour. The floors went up; a finished
            // bot now glows green enough to be obvious at a glance, which is the whole point of
            // it staying on the tab until it is ticked.
            double miniGlow = _miniStates[i] switch
            {
                var c when c == Draw.Amber => 0.40 + 0.34 * Math.Abs(Math.Sin(2 * Math.PI * st / 1.2)),
                var c when c == Draw.Red => 0.34 + 0.26 * Math.Abs(Math.Sin(2 * Math.PI * st / 1.6)),
                var c when c == Draw.Blue => 0.30 + 0.20 * wave,
                var c when c == Draw.Green => 0.26 + 0.10 * Math.Abs(Math.Sin(2 * Math.PI * st / 3.2)),
                _ => 0.14,
            };
            var miniFace = _opt.ForceFace ?? (i < _miniFaces.Count ? _miniFaces[i] : Face.Idle);
            _miniBots[i].Set(miniBlink, miniGlow, status, miniFace);
            _miniBots[i].SetSpin(status == "working" ? 1 : 0, spinAngle);

            // A — the ring DRAWS itself when a bot settles (360 ms). A ring that appears is a fact;
            // a ring that closes is the finish the human was waiting for.
            bool settled = status is "done" or "error" or "attention";
            double miniRingTarget = settled ? 1 : 0;
            double sweep = _opt.Ring >= 0
                ? Math.Clamp(_opt.Ring, 0, 1)
                : _sweepNow[i] + Math.Sign(miniRingTarget - _sweepNow[i]) * Math.Min(
                    Math.Abs(miniRingTarget - _sweepNow[i]), dt / MiniSweepSeconds);
            _sweepNow[i] = sweep;
            _miniBots[i].SetSweep(sweep);

            // C — a session that appears slides into the tab (200 ms) instead of popping in
            double arrive = _opt.Arrive >= 0
                ? Math.Clamp(_opt.Arrive, 0, 1)
                : _miniBorn[i] < 0 ? 1 : Math.Min(1, (t - _miniBorn[i]) / MiniArriveSeconds);
            if (arrive < 1 || _miniBots[i].Opacity < 1)
            {
                _miniBots[i].Opacity = arrive;
                Canvas.SetLeft(_miniBots[i], _miniX[i] + (1 - arrive) * 12);
            }
        }

        // the island grows out of the tab and folds back into it
        double islandTarget = IslandUp ? 1 : 0;
        if (_motionOff) _islandEase = islandTarget;
        else _islandEase += (islandTarget - _islandEase) * Math.Min(1, dt / IslandEaseSeconds);
        if (Math.Abs(_islandEase - islandTarget) < 0.002) _islandEase = islandTarget;
        if (_islandLayer is not null && _restLayer is not null)
        {
            // The two contents never share the morph: the tab's words leave in its first half and the
            // alert's arrive in its second, so mid-way there is only the shape changing — not two lines
            // of text cross-fading on top of each other (round 13, seen in the README film).
            _islandLayer.Opacity = Ease(Math.Clamp((_islandEase - .45) / .55, 0, 1));
            _islandLayer.IsHitTestVisible = _islandEase > .5;
            _restLayer.Opacity = 1 - Ease(Math.Clamp(_islandEase / .45, 0, 1));
            LayoutTab(measure: false);
        }
        if (_islandOrb is not null && _islandEase > 0)
        {
            if (_islandKind == "d1") _islandOrb.SetFace(t - _islandStart < 1.2 ? Face.Starry : Face.Done);
            _islandOrb.Set(_islandKind == "x1" ? 1.2 : 1, 0, 0, .9, _islandKind == "d1" ? .25 : .45);
        }
        if (_islandEase == 0 && !IslandUp && _islandKind.Length > 0)
        {
            _islandKind = "";
            _islandOrb = null;
            _islandLayer?.Children.Clear();
            _islandW = _islandH = 0;
        }

        // L2: light runs along the tab's lower edge while any session is in a tool. A sweep that has
        // started always FINISHES — a tool that returns in 50 ms used to make it blink out mid-edge —
        // and only then does the light fade (round 13).
        if (_shimmer is not null && _shimmerBase is not null && _shimmerMove is not null)
        {
            bool tool = _data.AnyTool;
            if (tool) _shimmerRunning = true;
            if (_shimmerRunning && !_motionOff)
            {
                _shimmerPhase += dt / ShimmerSweepSeconds;
                if (_shimmerPhase >= 1)
                {
                    _shimmerPhase -= 1;
                    if (!tool) { _shimmerRunning = false; _shimmerPhase = 0; }
                }
            }
            else if (_motionOff) _shimmerRunning = tool;
            double lit = _shimmerRunning ? 1 : 0;
            _shimmerAlpha += (lit - _shimmerAlpha) * Math.Min(1, dt / .2);
            if (_shimmerAlpha < .01 && !_shimmerRunning) _shimmerAlpha = 0;
            double shown = _shimmerAlpha * (1 - _islandEase);
            _shimmerBase.Opacity = shown;
            _shimmer.Opacity = _motionOff ? 0 : shown;
            // the highlight enters at the left corner and leaves past the right one
            _shimmerMove.X = -.75 + 1.5 * _shimmerPhase;
        }

        // the rows' small bots wear their faces and spin while they work — only while the board shows
        for (int i = 0; i < _rowBots.Count && _p > 0.01; i++)
        {
            var (rowStatus, rowFace) = _rowBotState[i];
            double rowPhase = (st + i * 1.3 + .7) % 4.6;
            double rowBlink = rowPhase <= 0.14 ? Math.Sin(Math.PI * rowPhase / 0.14) : 0;
            _rowBots[i].Set(rowBlink, .2, rowStatus, rowFace);
            _rowBots[i].SetSpin(rowStatus == "working" ? 1 : 0, spinAngle);
        }

        // the rows' list follows the wheel, eased; its edges fade only where there is more to see
        if (_rowsScroll is not null)
        {
            _scroll += (_scrollTarget - _scroll) * Math.Min(1, dt / .12);
            if (Math.Abs(_scrollTarget - _scroll) < .3) _scroll = _scrollTarget;
            _rowsScroll.Y = -_scroll;
            if (_rowsFade is not null && _rowsThumb is not null && _rowsViewport is not null)
            {
                double max = Math.Max(1, _data.Rows.Count * RowPitch - _rowsViewport.Height);
                _rowsFade.GradientStops[0].Color = _scroll > .5 ? Colors.Transparent : Colors.Black;
                _rowsFade.GradientStops[3].Color = _scroll < max - .5 ? Colors.Transparent : Colors.Black;
                Canvas.SetTop(_rowsThumb, RowsTop + (_rowsViewport.Height - _rowsThumb.Height) * Math.Clamp(_scroll / max, 0, 1));
            }
        }

        // B — a retired row folds up and the rows below close the gap (140 ms)
        if (_foldIndex >= 0 && _foldAt >= 0)
        {
            double e = _opt.Fold >= 0 ? Math.Clamp(_opt.Fold, 0, 1) : Math.Min(1, (t - _foldAt) / FoldSeconds);
            for (int j = 0; j < _rowViews.Count; j++)
            {
                var view = _rowViews[j];
                if (j == _foldIndex)
                {
                    view.RenderTransformOrigin = new Point(0, 0);
                    view.RenderTransform = new ScaleTransform(1, Math.Max(0.01, 1 - e));
                    view.Opacity = 1 - e;
                }
                else if (j > _foldIndex)
                {
                    Canvas.SetTop(view, j * RowPitch - RowPitch * e);
                }
            }
            if (e >= 1) { _foldIndex = -1; _foldAt = -1; }
        }

        // breathe — 3.2 s sine, ±1.5 %
        double s = 1 + 0.015 * Math.Sin(2 * Math.PI * t / 3.2);
        if (_orb is not null)
        {
            // one group, built once per orb, whose two transforms are mutated: the breath and the move
            if (_orbBreath is null || _orb.RenderTransform is not TransformGroup)
            {
                _orbBreath = new ScaleTransform();
                _orbMove = new TranslateTransform();
                _orb.RenderTransform = new TransformGroup { Children = { _orbBreath, _orbMove } };
            }
            _orbBreath.ScaleX = s; _orbBreath.ScaleY = s;
            _orbMove!.X = moveX; _orbMove.Y = moveY;
        }

        // ── frame statistics ────────────────────────────────────────────────────
        //
        // The overlay has to feel like the thing it reports on, so its own smoothness is measurable
        // rather than a matter of taste: one line per second with the frame count, the gap between
        // ticks and the time spent inside this method. Only when `--log` is given, because the live
        // window's stderr is a pipe the host ignores.
        if (_opt.LogPath is not null)
        {
            double workMs = _sw.Elapsed.TotalMilliseconds - workStart;
            _statFrames++;
            _statGapSum += rawGapMs;
            if (rawGapMs > _statMaxGap) _statMaxGap = rawGapMs;
            if (workMs > _statMaxWork) _statMaxWork = workMs;
            if (rawGapMs > 25) _statOver++;
            if (t - _statAt >= 1.0)
            {
                Log.Write($"frame fps={_statFrames} avgGap={_statGapSum / Math.Max(1, _statFrames):F1}ms" +
                          $" maxGap={_statMaxGap:F1}ms maxWork={_statMaxWork:F1}ms over25={_statOver}" +
                          $" reveal={_p:F2} visible={(_cardHost?.Opacity ?? 0):F2}");
                _statAt = t; _statFrames = 0; _statGapSum = 0; _statMaxGap = 0; _statMaxWork = 0; _statOver = 0;
            }
        }
    }

    /// <summary>cubic ease-in-out — the same curve the design frames were sampled with.</summary>
    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < .5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }

    /// <summary>
    /// The unfold: the board fades in, slides down a little and grows into place.
    ///
    /// The transforms are kept and their values mutated, never rebuilt: a fresh `TransformGroup` per
    /// frame — which this did until 2026-10-21 — re-assigns `RenderTransform`, and that invalidates the
    /// whole card, effects and all, on every frame of an animation that only changes two numbers.
    /// </summary>
    private void ApplyProgress()
    {
        if (_cardHost is null) return;
        double e = 1 - Math.Pow(1 - _p, 3);          // cubic ease-out
        _cardHost.Opacity = e;
        if (_cardSlide is null)
        {
            _cardSlide = new TranslateTransform();
            _cardScale = new ScaleTransform();
            var group = new TransformGroup();
            group.Children.Add(_cardSlide);
            group.Children.Add(_cardScale!);
            _cardHost.RenderTransform = group;
        }
        _cardSlide!.Y = (1 - e) * -12;
        _cardScale!.ScaleX = _cardScale.ScaleY = 0.94 + 0.06 * e;

        bool live = _p > 0.02;
        if (live != _cardLive)
        {
            _cardLive = live;
            _cardHost.IsHitTestVisible = live;
        }
    }

    TranslateTransform? _cardSlide;
    ScaleTransform? _cardScale;

    // ── hiding, and the heartbeat ───────────────────────────────────────────────

    /// <summary>
    /// Read the feed and keep the window honest about whether it is live.
    ///
    /// CONTRACT #3 is the reason this method is not just "parse the file": `wallMs` is the host's
    /// clock at write time and it is compared with THIS machine's clock, so a file that has not been
    /// rewritten — a fixture, or a host that wedged with a perfectly readable file on disk — shows
    /// the unknown state instead of repeating the last summary as though it were current. Recovering
    /// is instant, because there is no latch: it is recomputed from the file on every tick.
    /// </summary>
    private void PollState()
    {
        bool hide = _forceHidden;
        try
        {
            if (!_opt.Demo && File.Exists(_opt.StatePath))
            {
                // FileShare.Delete is load-bearing. The host replaces the state file with an
                // atomic rename, and on Windows that rename FAILS while anybody holds the target
                // open without FILE_SHARE_DELETE — which is exactly what File.OpenRead does. At a
                // 150 ms poll that turned into `write-failed: EPERM` in the host's log every few
                // seconds, so the board was silently running on stale data.
                using var fs = new FileStream(_opt.StatePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                if (root.TryGetProperty("hidden", out var h) && h.ValueKind == JsonValueKind.True) hide = true;
                if (root.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True) hide = true;
                if (root.TryGetProperty("forceShow", out var fs2) && fs2.ValueKind == JsonValueKind.True) _forceShow = true;
                ApplyState(root);
            }
        }
        catch { /* a half-written file is normal; try again next tick */ }

        _forceHidden = hide;

        // An always-on display that has quietly stopped updating is worse than one that is off: it
        // shows yesterday's work as if it were live. Two ways to be not-current, and both are the
        // same fact to the human: the file's own heartbeat is old, or no file has been readable for
        // as long as the heartbeat would have taken to age out.
        if (!_opt.Demo)
        {
            bool quiet = _sw.Elapsed.TotalSeconds - _lastGoodState > StaleMs / 1000.0;
            bool stale = quiet || (_lastState is not null && IsStale(_lastState));
            if (stale != _data.Stale && _lastState is not null)
            {
                var board = Live.Build(_lastState, _pinnedId, _settings.MaxRows, stale, _settings.ShowProject);
                ApplyBoard(board);
                Log.Write(stale ? "feed is stale — the tab says host offline" : "feed is live again");
            }
        }

        // QUNS 2 is NOT trusted: measured on this machine with nothing full-screen running, it
        // still answered 2 (BUSY) — the foreground app was OBS. Hiding on it would hide the pill
        // always. The geometric test — does the foreground window cover its whole monitor — is
        // the honest signal, and it catches both an F11 browser and an exclusive-fullscreen game.
        // It excludes the shell window (Win+D shows the desktop, whose bounds ARE the monitor rect)
        // and answers per monitor, so it only speaks about the display the notch is on.
        int quns = Native.UserNotificationState();
        bool geoFull = Native.ForegroundIsFullscreen(_ownMonitor);
        // Session lock (1) and presentation mode (4) are machine-wide by definition. D3D fullscreen
        // (3) belongs to the display the game is on, so it only hides the notch when that is ours.
        bool qunsHide = quns is 1 or 4 || (quns == 3 && Native.ForegroundOnOurMonitor(_ownMonitor));
        _fullscreen = geoFull || qunsHide;

        // `settings.hideFullscreen` (default true) is the switchable form of the old
        // `--hide-tab-fullscreen`: the whole notch goes while a full-screen app has the display, and
        // it keeps polling, so it comes back the moment the game loses the display.
        bool shouldHide = _forceHidden
            || ((_settings.HideFullscreen || _opt.HideTabFullscreen) && _fullscreen && !_forceShow);

        if (shouldHide != _hidden)
        {
            _hidden = shouldHide;
            Visibility = shouldHide ? Visibility.Hidden : Visibility.Visible;
            Log.Write(shouldHide ? $"hidden (quns={quns} geoFull={geoFull})" : $"shown (quns={quns} geoFull={geoFull})");
        }
    }

    // ── tray ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The tray. Its Quit is the one thing this window says back to the host: it writes
    /// `quit.json` — `{"wallMs": …}`, next to the state file — BEFORE it exits, because otherwise
    /// the host's supervisor treats the exit as a crash and puts the window back within seconds,
    /// which is exactly the bug that made Quit look broken (CONTRACT §Who writes what).
    /// </summary>
    private void BuildTray()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var br = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 0x6A, 0xA8, 0xFF));
            g.FillEllipse(br, 4, 4, 24, 24);
        }
        var icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open DeepSeek Harness", null, (_, _) => OpenApp());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Hide the notch", null, (_, _) => { _forceHidden = true; });
        menu.Items.Add("Show it again", null, (_, _) => { _forceHidden = false; });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) =>
        {
            WriteQuitMarker();
            System.Windows.Application.Current.Shutdown();
        });

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Agents Notch",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    /// <summary>
    /// `quit.json`, next to the state file: the one thing the window writes. Written to a temp name
    /// and renamed into place — the host reads it on its next tick, and a half-written marker would
    /// read as a corrupt one — with the same 4 × 4 ms retry the publisher needs, because the host may
    /// be holding the target open at that instant.
    /// </summary>
    private void WriteQuitMarker()
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(_opt.StatePath)) ?? ".";
        string path = Path.Combine(dir, "quit.json");
        string tmp = path + ".tmp";
        string body = $"{{\"wallMs\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, body);
            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, path, overwrite: true); break; }
                catch (IOException) when (attempt < 3) { System.Threading.Thread.Sleep(4); }
            }
            Log.Write($"quit marker written — {path} {body}");
        }
        catch (Exception e) { Log.Write("quit marker FAILED: " + e.Message); }
    }
}

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Attach();
        var opt = Options.Parse(args);
        if (opt.LogPath is not null) Log.UseFile(opt.LogPath);
        // The live window (the host gives it no --log and ignores its stdio) keeps its own account in
        // `window.log` beside the state file — diagnosis only, nothing reads it. Without it a click that
        // "does nothing" (Open, 2026-10-23) left no trace of which step failed. Stills and films keep
        // writing only where they are told to, so a fixture's folder does not collect a log.
        else if (opt.ShotPath is null && opt.RecordDir is null && !opt.ProbeRect)
            Log.UseFile(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opt.StatePath)) ?? ".", "window.log"));

        // `--probe-rect` is a dev flag: it prints where the notch WOULD go and exits, without ever
        // creating a window. It is how placement gets verified on a desk the reviewer cannot see, so
        // it must not depend on a window existing — the DPI and monitor geometry come from the
        // machine, and the settings come from the state file.
        if (opt.ProbeRect)
        {
            ProbePlacement(opt);
            return 0;
        }

        Log.Write($"starting — pid {Environment.ProcessId}");

        // one notch per machine — two harness processes must not fight over the screen
        bool created;
        using var mutex = new System.Threading.Mutex(true, @"Local\AgentsNotch.SingleInstance", out created);
        if (!created)
        {
            Log.Write("another instance already owns the notch — exiting");
            return 0;
        }

        // The host starts this window inside its job; a job's UI limits decide whether Open can reach
        // the app's window at all, so they are the first thing the log says.
        var limits = Native.JobUiLimits();
        Log.Write(limits is uint l ? $"job ui limits — 0x{l:X}" : "job ui limits — none (no job, or not readable)");
        Native.EnablePerMonitorDpi();
        Log.Write($"dpi — manifest/call aware, system={Native.SystemDpi()}");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) => { Log.Write("UNHANDLED: " + e.Exception); WriteCrash(opt, e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(opt, e.ExceptionObject);
        var window = new OverlayWindow(opt);
        app.MainWindow = window;
        Log.Write("window constructed — showing");
        window.Show();
        Log.Write("show() returned");
        app.Run();
        Log.Write("run loop ended");
        return 0;
    }

    /// <summary>
    /// One line in `window-crash.log` beside the state file. The host spawns the window with no
    /// `--log` and its stdio ignored, so without this a crash is only an exit code in the host's report.
    /// </summary>
    private static void WriteCrash(Options opt, object? error)
    {
        try
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(opt.StatePath)) ?? ".";
            string text = (error?.ToString() ?? "unknown").ReplaceLineEndings(" | ");
            File.AppendAllText(Path.Combine(dir, "window-crash.log"),
                $"{DateTimeOffset.UtcNow:O} pid {Environment.ProcessId}: {text}{Environment.NewLine}");
        }
        catch { /* dying already; nothing better to do */ }
    }

    /// <summary>
    /// One line of JSON, then exit: the resolved screen rect (device name + px), the window's DIP
    /// rect, the scale, and both DPIs. Every number here is what the real window would use — the
    /// settings come from the same `--state` file, through the same parser, resolved by the same
    /// `Place.Resolve` — so a mismatch between this and the screen is a real placement bug and not a
    /// difference between the probe and the window.
    /// </summary>
    private static void ProbePlacement(Options opt)
    {
        var settings = new Settings();
        var source = "defaults";
        try
        {
            if (File.Exists(opt.StatePath))
            {
                using var fs = new FileStream(opt.StatePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                settings = Settings.Parse(doc.RootElement);
                source = opt.StatePath;
            }
        }
        catch (Exception e) { Log.Write("probe: could not read the state file — " + e.Message); }

        // The design's base window: 660×300 DIP at scale 1. The card's height for ten rows is the
        // tallest it gets; the probe reports the base rectangle, because that is the one the tab and
        // the six-row board live in.
        double dipW = 660 * settings.Scale;
        double dipH = 300 * settings.Scale;
        var p = Place.Resolve(settings, dipW, dipH);

        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["monitor"] = p.Monitor,
            ["screen"] = RectOf(p.ScreenPx),
            ["work"] = RectOf(p.WorkPx),
            ["windowDip"] = new Dictionary<string, object?>
            {
                ["x"] = p.WindowPxX, ["y"] = p.WindowPxY,
                ["w"] = Math.Round(dipW, 2), ["h"] = Math.Round(dipH, 2),
            },
            ["windowPx"] = new Dictionary<string, object?>
            {
                ["x"] = p.WindowPxX, ["y"] = p.WindowPxY, ["w"] = p.WindowPxW, ["h"] = p.WindowPxH,
            },
            ["scale"] = settings.Scale,
            ["anchor"] = settings.Anchor,
            ["screenSetting"] = settings.Screen,
            ["dpiSys"] = p.DpiSystem,
            ["dpiMon"] = p.DpiMonitor,
            ["units"] = "screen/work/windowPx are physical px; windowDip is DIP",
            ["from"] = source,
        });

        // `windowDip.x/y` is deliberately the same physical number as `windowPx` — position is only
        // ever set in px (SetWindowPos). The DIP rect's job is to say how BIG the window is in DIP.
        //
        // A WinExe has no console of its own, so a run from a terminal would otherwise print nothing.
        // Attach to the parent's console only when stdout is NOT redirected: with a pipe or a file on
        // the other end, AttachConsole replaces that handle and swallows the very line asked for.
        if (!Console.IsOutputRedirected) { try { AttachConsole(-1); } catch { /* no parent console */ } }
        Console.Out.WriteLine(line);
        Console.Out.Flush();
        Log.Write("probe-rect " + line);
    }

    private static Dictionary<string, object?> RectOf(Native.RECT r) => new()
    {
        ["x"] = r.Left, ["y"] = r.Top, ["w"] = r.Right - r.Left, ["h"] = r.Bottom - r.Top,
    };
}
