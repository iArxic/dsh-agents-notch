// AgentsNotch — the always-on-top notch.
//
//   AgentsNotch.exe [--state <file>] [--log <file>] [--shot <file>] [--open]
//                   [--probe-rect] [--demo] [--force-show] [--no-tray] [--capture-exclude]
//                   [--rule any|games] [--hide-tab-fullscreen] [--parent-pid <pid>]
//                   [--pin <n>] [--dismiss <n>] [--freeze <ms>] [--fps <n>] [--no-effects]
//                   [--ring <0..1>] [--arrive <0..1>] [--fold <0..1>]
//                   [--record <dir>] [--record-ms <ms>] [--open-at <ms>] [--close-at <ms>]
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
//   · `retire: "timer"` changes one thing: the board's tooltip says settled rows are ageing out on
//     a timer rather than on the harness's close-out verdict.

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
                case "--pin": if (i + 1 < args.Length && int.TryParse(args[++i], out var pin)) o.PinIndex = pin; break;
                case "--dismiss": if (i + 1 < args.Length && int.TryParse(args[++i], out var dis)) o.DismissIndex = dis; break;
                default: Log.Write("unknown argument: " + args[i]); break;
            }
        }
        return o;
    }
}

internal sealed record TaskItem(string Text, string Kind, string Id = "", bool Teammate = false, string Tail = "");
internal sealed record ChipItem(string Label, Color State, Color Identity);

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
    int ContextTokens = 0);

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
    /// close-out verdict is unavailable, so settled rows age out instead; the board's tooltip says
    /// so and NOTHING is drawn differently (CONTRACT: `retire` is a fact, not a visual mode).
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
    public List<TaskItem> Tasks = new();
    public List<ChipItem> Chips = new();

    // the shape of the live parts
    public int SessionCount;                      // how many rows the state file carried
    public int Running;                          // sessions that are working — the orb stack
    public List<Color> BackIdentities = new();   // the OTHER working sessions' identity hues
    public List<Color> MiniStates = new();        // the tab's bots: each session's state colour
    public List<Color> MiniIdentities = new();    // …and each session's identity hue
    public List<string> MiniStatus = new();       // …and the state itself, which the face wears
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

    /// <summary>
    /// The overall tone — the summary. `retire` is carried onto the board because it is the one
    /// contract field whose entire effect is a line of tooltip (CONTRACT: `"timer"` changes nothing
    /// visual).
    /// </summary>
    public bool RetireTimer;

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
            if (Stale) return [new TabRun("host offline", 10, Draw.Mute, FontWeights.SemiBold)];
            if (Attention > 0) return [new TabRun("needs you", 10, Draw.TabAmber, FontWeights.SemiBold)];
            if (Error > 0)
                return [new TabRun(Error == 1 ? "failed" : $"{Error} failed", 10, Draw.TabRed, FontWeights.SemiBold)];

            if (Working > 0 && Done > 0) return
            [
                new($"{Working}", 11, Draw.TabInk, FontWeights.SemiBold),
                new(" working", 10, Draw.TabWord, FontWeights.Normal),
                new("  ·  ", 10, Draw.TabDot, FontWeights.Normal),
                new(Draw.Tick, 9, Draw.TabGreen, FontWeights.Normal, Glyph: true),
                new($" {Done}", 11, Draw.TabInk, FontWeights.SemiBold),
            ];

            if (Working > 0) return
            [
                new($"{Working}", 11, Draw.TabInk, FontWeights.SemiBold),
                new(" working", 10, Draw.TabWord, FontWeights.Normal),
            ];

            if (Done > 0) return
            [
                new(Draw.Tick, 9, Draw.TabGreen, FontWeights.Normal, Glyph: true),
                new($" {Done}", 11, Draw.TabInk, FontWeights.SemiBold),
            ];

            return [new TabRun("idle", 10, Draw.TabFlat, FontWeights.Normal)];
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

    /// <summary>The demo board — what the window drew before the plugin fed it.</summary>
    public static BoardData Demo()
    {
        var d = new BoardData
        {
            Status = "working",
            Working = 3,
            FrontStatus = "working",
            Badge = Draw.Blue,
            Identity = Draw.Teal,
            Title = "Argon 0/4 on laptop — fixed",
            Sub = "blindKnifeArgon · working 12m",
            Running = 3,
        };

        d.Tasks.Add(new TaskItem("Verify Argon 0/4 on the laptop", "active"));
        d.Tasks.Add(new TaskItem("Write the overlay plan", "open"));
        d.Tasks.Add(new TaskItem("Commit and push the plugin", "open"));
        d.Chips.Add(new ChipItem("uiAnimation", Draw.Blue, Draw.Violet));
        d.Chips.Add(new ChipItem("swordsGo", Draw.Green, Draw.Coral));
        d.Chips.Add(new ChipItem(".dsh", Draw.Amber, Draw.Pink));
        d.Chips.Add(new ChipItem("homepage", Draw.Blue, Draw.Lime));

        // the tab's bots and the back orbs used to be built from the chips — same colours, same order
        foreach (var c in d.Chips)
        {
            d.MiniStates.Add(c.State);
            d.MiniIdentities.Add(c.Identity);
            d.MiniStatus.Add(c.State == Draw.Amber ? "attention" : c.State == Draw.Green ? "done" : "working");
        }
        foreach (var c in d.Chips) d.BackIdentities.Add(c.Identity);

        d.ShapeKey = ShapeOf(d);
        d.ContentKey = ContentOf(d);
        return d;
    }

    /// <summary>
    /// What has to be rebuilt from scratch: the bots, the orb stack, the panel.
    ///
    /// The ROW COUNT is in here because the card is sized by the rows it carries (`settings.maxRows`
    /// up to 10), and so is the presence of a budget, because "a bar or no bar" is a structure and
    /// not a colour: a board that gained a denominator must grow the bar, and one that lost it must
    /// not keep a stale one.
    /// </summary>
    public static string ShapeOf(BoardData d)
    {
        int backs = d.Running > 1 ? Math.Min(3, d.Running - 1) : 0;
        return $"{d.MiniStates.Count}|{d.MiniOverflow}|{(d.Running > 1 ? 1 : 0)}|{backs}|{(d.Running > 4 ? 1 : 0)}" +
               $"|{(d.ContextTokens > 0 ? 1 : 0)}|{(d.HasBudget ? 1 : 0)}|{d.Tasks.Count}|{(d.Stale ? 1 : 0)}";
    }

    /// <summary>What has to be rebuilt inside the board: the task lines and the chips.</summary>
    public static string ContentOf(BoardData d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in d.Tasks)
            sb.Append(t.Kind).Append(':').Append(t.Text)
              .Append(t.Teammate ? '\u0002' : '\u0001').Append(t.Tail)
              .Append('\u0001');
        foreach (var c in d.Chips) sb.Append(c.Label).Append(':').Append(c.State).Append('\u0001');
        return sb.ToString();
    }
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
            Str(el, "origin"), Str(el, "parentSession"), Int(el, "contextTokens"));
    }

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
            RetireTimer = state.Retire == "timer",
        };
        var sessions = state.Sessions;
        d.SessionCount = sessions.Count;

        // the tab's bots ARE the sessions — up to four, then a "+N"
        for (int i = 0; i < Math.Min(4, sessions.Count); i++)
        {
            d.MiniStates.Add(ToneOf(sessions[i].Status));
            d.MiniIdentities.Add(IdentityOf(sessions[i].Identity));
            d.MiniStatus.Add(sessions[i].Status);
        }
        d.MiniOverflow = Math.Max(0, sessions.Count - 4);

        // the orb stack — one bubble per working session
        d.Running = sessions.Count(s => s.Status == "working");

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
        }
        else
        {
            d.Badge = ToneOf(front.Status);
            d.Identity = IdentityOf(front.Identity);
            d.FrontStatus = front.Status;
        d.FrontKey = front.Id + "|" + front.Status;
            d.Title = front.Title.Length > 0 ? front.Title : ProjectOf(front);
            d.Sub = showProject
                ? $"{ProjectOf(front)} · {Phrase(front.Status, Age(front.Since))}"
                : Phrase(front.Status, Age(front.Since));
            d.ContextTokens = front.ContextTokens;
        }

        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.Status == "working" && i != frontIndex) d.BackIdentities.Add(IdentityOf(s.Identity));
        }

        // the task lines — prose, not a table: attention first, then error, then done, then working.
        // One line per row the host sent: the board is where you go to see the sessions the tab can
        // only summarise, so cutting it at three hid half of a busy machine. `settings.maxRows`
        // (3…10, clamped) is the only ceiling, and it is the same ceiling the host applies.
        //
        // A delegated session is a TEAMMATE, and the board says so the way the human picked in round
        // 08 (S6): the row steps back to the dimmer ink and the session that delegated it carries how
        // many it has. The count is a fact about that session, not a grouping claim — the rows are
        // ordered by urgency, so a teammate's row is rarely next to its parent's.
        var teammates = sessions
            .Where(s => s.Origin == "subagent" && s.Parent.Length > 0)
            .GroupBy(s => s.Parent)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var s in sessions.OrderBy(s => Urgency(s.Status)).ThenByDescending(s => s.Since)
                                  .Take(Math.Clamp(maxRows, Settings.RowsMin, Settings.RowsMax)))
        {
            var text = s.Title.Length > 0 ? s.Title : ProjectOf(s);
            if (!string.IsNullOrEmpty(s.ApprovalTool)) text = $"{s.ApprovalTool}: {text}";
            bool teammate = s.Origin == "subagent";
            // R6 — a row says nothing while it is healthy. The one thing it may say is, for a session
            // and not a teammate, how many sessions it has delegated.
            //
            // A broken tool used to add "· 2 tools failed" here, in alarm red. Kuba had it removed
            // outright on 2026-10-21 (*"i dont think it expires just clear tools failed from it
            // entirely"*): a per-turn count is not a moment in time, so it outlived the mistake it was
            // reporting, and a worse-than-half-finished retry is still just work. A turn that really
            // ended badly is an `error` row, with its own badge and ring — that is where a failure
            // belongs, and nothing on this board counts anything else.
            string tail = "";
            if (!teammate && teammates.TryGetValue(s.Id, out int kids))
            {
                tail = kids == 1 ? "1 teammate" : $"{kids} teammates";
            }
            d.Tasks.Add(new TaskItem(Trim(text, 34), KindOf(s.Status), s.Id, teammate, tail));
        }

        // the chips — one per PROJECT, coloured by its most urgent session
        var chipOrder = new List<string>();
        var projects = new Dictionary<string, (int Urgency, Color State, Color Identity)>();
        foreach (var s in sessions)
        {
            var label = ProjectOf(s);
            var u = Urgency(s.Status);
            if (!projects.TryGetValue(label, out var cur))
            {
                chipOrder.Add(label);      // the order the projects were first seen in
                projects[label] = (u, ToneOf(s.Status), IdentityOf(s.Identity));
            }
            else if (u < cur.Urgency)
            {
                projects[label] = (u, ToneOf(s.Status), IdentityOf(s.Identity));
            }
        }
        foreach (var label in chipOrder.OrderBy(l => projects[l].Urgency).Take(4))
            d.Chips.Add(new ChipItem(Trim(label, 22), projects[label].State, projects[label].Identity));

        // CONTRACT #3 — a stale feed shows the UNKNOWN state, never the last summary dressed up as
        // live. The rows stay (they are still what the host last said, and the ✕ on them still
        // works) but every indicator of "this is happening now" goes: the orb's state colour, the
        // aura, the tab's bots. Recovering is instant, because this is recomputed on every read.
        if (stale)
        {
            d.Stale = true;
            d.Badge = Draw.Grey;
            d.FrontStatus = "idle";
            d.Running = 0;
            d.MiniStates.Clear();
            d.MiniIdentities.Clear();
            d.MiniStatus.Clear();
            d.MiniOverflow = 0;
            d.BackIdentities.Clear();
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

    /// <summary>A state → its tone. These are the five the tab, the orb and the chips wear.</summary>
    public static Color ToneOf(string? status) => status switch
    {
        "working" => Draw.Blue,
        "attention" => Draw.Amber,
        "done" => Draw.Green,
        "error" => Draw.Red,
        _ => Draw.Grey,
    };

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
    const double TabW = 220, TabH = 30, TabX = (WinW - 220) / 2, TabY = 0;
    // The card carries as many rows as the host will send (settings.maxRows: 3…10). It used to be
    // 188 for THREE rows, which was never a height limit — just an unexamined cap, so the rows that
    // did not fit existed only as "+N" in the tab and could not be seen anywhere.
    const double CardW = 560, CardX = (WinW - 560) / 2, CardY = 20;
    /// <summary>The card's height for six rows — the approved shape, and the floor for `_cardH`.</summary>
    const double CardBaseH = 198;
    /// <summary>Room under the card for its drop shadow (the window is bigger than what it draws).</summary>
    const double CardFoot = 82;
    const double RowTop = 68, RowPitch = 22;

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
    BoardData _data = BoardData.Demo();

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
    /// <summary>The card's height for the rows it currently carries — 198 at six rows, more above.</summary>
    double _cardH = CardBaseH;
    /// <summary>The window's height in design units; it grows with the card.</summary>
    double _designH = WinH;
    /// <summary>Where the last `Place.Resolve` put the window — logged, and what `--probe-rect` prints.</summary>
    Placement? _placement;

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

    Canvas? _tabHost, _cardHost, _orbHost, _headHost, _linesHost, _chipHost;
    readonly List<MiniBotView> _miniBots = new();
    readonly List<Color> _miniStates = new();
    readonly List<string> _miniStatus = new();
    // the three motions: a ring that draws itself, a bot that arrives, a row that folds away
    readonly double[] _miniBorn = new double[4];     // when each tab bot appeared (-1 = it was there)
    readonly double[] _miniX = new double[4];        // its resting x in the tab
    readonly double[] _sweepNow = new double[4];     // how much of its ring is drawn
    readonly List<Canvas> _rowViews = new();
    int _miniSeen;                                   // how many bots the last build had
    int _foldIndex = -1;
    double _foldAt = -1;
    readonly List<OrbView> _backOrbs = new();
    // the front session's context readout, under its bot (round 10)
    Canvas? _ctxBar;
    /// <summary>Where the readout sits while the ring is invisible; `PositionContext` slides from here.</summary>
    double _ctxRestTop;

    // frame statistics (only collected with --log): see the tail of `Tick`
    int _statFrames;
    double _statGapSum, _statMaxGap, _statMaxWork, _statAt, _statOver;
    System.Windows.Shapes.Rectangle? _ctxFill;
    TextBlock? _ctxLabel;
    OrbView? _orb;
    RunLine? _tabLine;
    TextBlock? _titleText, _subText;
    string _tabKey = "";
    string? _pinnedId;               // a row the human clicked — the orb wears it until unpinned
    StateFile? _lastState;           // so a click can re-answer without waiting for the next poll
    bool _dismissed;                 // --dismiss fires once
    Border? _tabBg;
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
        _root.MouseLeave += (_, _) => { _want = false; _collapseAt = _t + CollapseDelay; };

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
        int n = Math.Clamp(rows, 0, Settings.RowsMax);
        _cardH = Math.Max(CardBaseH, RowTop + Math.Max(0, n - 1) * RowPitch + 18);
        _designH = CardY + _cardH + CardFoot;
    }

    // ── the board ───────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        // The card is sized by the rows it carries (settings.maxRows, 3…10), so the window's height
        // is decided here — before anything is positioned inside it.
        FitRows(_data.Tasks.Count);
        ResizeWindow();

        _root.Children.Clear();
        var d = _data;

        // the slim tab — always visible, the summary of everything
        _tabHost = new Canvas { Width = TabW, Height = TabH };
        _tabHost.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) { e.Handled = true; OpenApp(); } };
        var bg = Draw.Panel(TabW, TabH, Draw.C("#08080A"), 0);
        bg.CornerRadius = new CornerRadius(0, 0, 15, 15);
        // The tab's shadow stays an Effect on purpose: `LayoutTab` resizes the tab to its text, so a
        // fixed bitmap cannot follow it (a first attempt left a stray blob beside the tab). The card
        // below is a fixed 560×198 and takes the pre-rasterised shadow, which is where the frames were.
        bg.Effect = Draw.Shadow(34, 8, .5);
        _tabBg = bg;
        _tabHost.Children.Add(bg);

        // the bots themselves, small: each one wears its session's colour and its state aura, and —
        // since round 08 (M1) — the big bot's state RING once it has settled. The ring needs room
        // outside the face, so the pitch went from 21 to 28 and each bot is placed by its padding
        // (7) rather than by its face.
        _miniBots.Clear();
        _miniStates.Clear();
        _miniStatus.Clear();
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
            _miniX[i] = x - MiniOff;
            // a bot for a session that was not in the tab before slides in; the rest are already there
            _miniBorn[i] = i >= seenBefore ? _t : -1;
            bool settled = _miniStatus[i] is "done" or "error" or "attention";
            _sweepNow[i] = settled ? 1 : 0;      // a rebuild shows a state, it does not replay it
            mini.SetSweep(_sweepNow[i]);
            mini.Opacity = _miniBorn[i] < 0 ? 1 : 0;
            _tabHost.Add(mini, _miniX[i], 9 - MiniOff);
            x += MiniPitch;
        }
        if (miniCount == 0)
        {
            // nothing at all is running — one dot, wearing the summary's tone. It used to sit at
            // x=15 with the label starting at x=17, so it landed on top of the "i" in "idle".
            _tabHost.Add(Draw.Dot(6, d.TabTone, .9), 13, 12);
            x += 11;
        }
        if (d.MiniOverflow > 0)
        {
            _tabHost.Add(Draw.Label($"+{d.MiniOverflow}", 9.5, Draw.Mute, FontWeights.SemiBold), x - 1, 10);
            x += 13;
        }
        _tabLine = new RunLine();
        _tabLine.Set(d.TabRuns);
        _tabKey = BoardData.TabKey(d.TabRuns);
        _tabHost.Add(_tabLine, x + 1, 7);
        _tabChevron = Draw.Chevron(7, Draw.Mute);
        _tabHost.Add(_tabChevron, 190, 13);
        _root.Add(_tabHost, TabX, TabY);
        LayoutTab();

        // the board — appears under the tab on hover
        _cardHost = new Canvas { Width = CardW, Height = _cardH, Opacity = 0, IsHitTestVisible = false };
        _cardHost.RenderTransformOrigin = new Point(.5, 0);
        // `ApplyProgress` caches both of these against the card it last saw. Left over from the old
        // card, the new one stayed unhittable while open: a click that rebuilt the board (pinning a
        // session with no context count) put the pointer over nothing, `_root` got MouseLeave, and the
        // whole board folded away under the cursor.
        _cardLive = false;
        _cardSlide = null;
        _cardScale = null;
        var card = Draw.Panel(CardW, _cardH, Draw.PanelBg, 26, .985, shadow: Draw.Shadow());
        // CONTRACT `retire`: "timer" means the harness's own close-out verdict is unavailable, so
        // settled rows age out instead. Nothing changes visually — this line of tooltip is all it is.
        _cardHost.ToolTip = d.RetireTimer
            ? "Settled rows are ageing out on a timer — the harness's close-out verdict is unavailable."
            : "Settled rows stay until they are closed out in the app.";
        // clicking the board's own background lets go of a pinned session
        card.MouseLeftButtonDown += (_, _) => { if (_pinnedId is not null) TogglePin(_pinnedId); };
        _cardHost.Children.Add(card);
        _cardHost.Add(Draw.CatchLight(CardW - 64), 32, 0);

        // the board in parts: the orb and the headline are live, the lines and the chips are static
        _orbHost = new Canvas { Width = CardW, Height = _cardH };
        _headHost = new Canvas { Width = CardW, Height = _cardH };
        _linesHost = new Canvas { Width = CardW, Height = _cardH };
        _chipHost = new Canvas { Width = CardW, Height = _cardH };
        _cardHost.Children.Add(_orbHost);
        _cardHost.Children.Add(_headHost);
        _cardHost.Children.Add(_linesHost);
        _cardHost.Children.Add(_chipHost);

        double textX = d.Running > 1 ? 132 : 110;
        double textW = (CardW - 22 - 178) - textX - 12;   // the chips start here — stop before them
        _titleText = Draw.Text(d.Title, 13, Draw.Ink, FontWeights.SemiBold);
        _titleText.Width = textW;
        _headHost.Add(_titleText, textX, 16);
        _subText = Draw.Text(d.Sub, 10.5, Draw.Mute);
        _subText.Width = textW;
        _headHost.Add(_subText, textX, 38);

        _backOrbs.Clear();
        if (d.Running > 1)
        {
            var stack = new Canvas { Width = 96, Height = 74 };
            int backs = Math.Min(3, d.Running - 1);
            double[] ys = backs switch { 1 => [24], 2 => [8, 42], _ => [2, 24, 46] };
            bool overflow = d.Running > 4;
            for (int i = 0; i < backs; i++)
            {
                if (overflow && i == backs - 1) continue;
                var rim = i < d.BackIdentities.Count ? d.BackIdentities[i] : Draw.Teal;
                var back = new OrbView(26, Draw.Grey, rim);
                back.Set(1, 0, 0, .95);
                back.RenderTransformOrigin = new Point(.5, .5);
                ((System.Windows.Shapes.Ellipse)back.Children[1]).Effect = new System.Windows.Media.Effects.DropShadowEffect
                { Color = Colors.Black, BlurRadius = 12, ShadowDepth = 0, Opacity = .55 };
                back.Opacity = .96;
                _backOrbs.Add(back);
                stack.Add(back, 10, ys[i]);
            }
            if (overflow)
            {
                stack.Add(Draw.Panel(26, 26, Draw.C("#232327"), 13, 1, Draw.Line), 10, ys[^1]);
                stack.Add(Draw.Label($"+{d.Running - 3}", 9.5, Draw.Ink2, FontWeights.SemiBold,
                    HorizontalAlignment.Center, width: 26, height: 24), 10, ys[^1] + 2);
            }
            _orb = new OrbView(64, d.Badge, d.Identity);
            _orb.RenderTransformOrigin = new Point(.5, .5);
            stack.Add(_orb, 26, 4);
            _orbHost.Add(stack, 20, 16);
            _orbX = CardX + 20 + 26; _orbY = CardY + 16 + 4; _orbCenter = _orb.CenterOffset;
            BuildContext(faceX: 46 + 2.5, faceY: 20 + 2.5, orbSize: 64);
        }
        else
        {
            _orb = new OrbView(72, d.Badge, d.Identity);
            _orb.RenderTransformOrigin = new Point(.5, .5);
            _orbHost.Add(_orb, 22, 20);
            _orbX = CardX + 22; _orbY = CardY + 20; _orbCenter = _orb.CenterOffset;
            BuildContext(faceX: 22 + 2.5, faceY: 20 + 2.5, orbSize: 72);
        }

        RebuildStatic();
        _root.Add(_cardHost, CardX, CardY);
        ApplyProgress();
        Log.Write($"board drawn — tab \"{d.TabText}\" · title \"{d.Title}\" · sub \"{d.Sub}\"" +
                  $" · {d.Tasks.Count} line(s) · {d.Chips.Count} chip(s) · {d.MiniStates.Count} bot(s) in the tab");
        // the measurement knob has to re-run after every rebuild, or the tree it measured is gone
        if (_opt.NoEffects) StripEffects(_root);
    }

    /// <summary>
    /// The front session's context, hung under its bot — round 10's pick: D1's bar and number. The
    /// number is ALWAYS a percentage of the route's window, which is the only way a token count says
    /// anything (the earlier rule — a count, then a percentage once it was close — read like an
    /// output-token count, because the count it had was the uncached slice of the prompt).
    ///
    /// This is the ONLY token figure on the board. The rows are silent by default (R6) because six
    /// numbers in a column is precisely what this design is not; the session on the bot is the one
    /// the headline, the sub line and the orb already describe, so its context belongs with them.
    /// </summary>
    private void BuildContext(double faceX, double faceY, double orbSize)
    {
        if (_orbHost is null || _data.ContextTokens <= 0) return;
        const double barW = 56, barH = 3, boxW = 74;
        double cx = faceX + orbSize / 2;
        // where it sits while the session is simply working — close under the bot, no ring to clear
        _ctxRestTop = faceY + orbSize + 9;
        double top = _ctxRestTop;

        _ctxBar = new Canvas { Width = boxW, Height = 26 };
        _ctxFill = null;                       // a rebuild decides again whether there is a bar
        _ctxLabel = Draw.Text("", 9.5, Draw.Mute);
        _ctxLabel.Width = boxW;
        _ctxLabel.TextAlignment = TextAlignment.Center;

        // A BAR NEEDS A DENOMINATOR. `context.budget: null` is the host saying it cannot establish
        // the route's window, and then the token count is the whole readout: no track, no fill, no
        // percentage and no amber. A percentage against a guessed denominator is a lie that looks
        // like information (CONTRACT #1), and `hotAt` means nothing without one.
        if (_data.HasBudget)
        {
            var track = new System.Windows.Shapes.Rectangle
            {
                Width = barW, Height = barH, RadiusX = barH / 2, RadiusY = barH / 2,
                Fill = Draw.B(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            };
            _ctxFill = new System.Windows.Shapes.Rectangle
            {
                Height = barH, RadiusX = barH / 2, RadiusY = barH / 2, Fill = Draw.Cached(Draw.Mute),
            };
            _ctxBar.Add(track, (boxW - barW) / 2, 0);
            _ctxBar.Add(_ctxFill, (boxW - barW) / 2, 0);
            _ctxBar.Add(_ctxLabel, 0, 5);
        }
        else
        {
            _ctxBar.Add(_ctxLabel, 0, 0);      // nothing above it, so nothing to sit below
        }
        _orbHost.Add(_ctxBar, cx - boxW / 2, top);
        UpdateContext();
    }

    /// <summary>The bar's fill and the number under it, without rebuilding anything.</summary>
    private void UpdateContext()
    {
        if (_ctxLabel is null) return;
        var d = _data;
        var tone = d.ContextHot ? Draw.Amber : Draw.C("#8A8A92");
        if (_ctxFill is not null)
        {
            _ctxFill.Width = Math.Max(0, 56 * d.ContextFill);
            _ctxFill.Fill = Draw.Cached(tone);
        }
        string text = d.ContextText;
        if (_ctxLabel.Text != text) _ctxLabel.Text = text;
        _ctxLabel.Foreground = Draw.Cached(tone);
        PositionContext();
    }

    /// <summary>
    /// Hang the readout under the bot, out of the state ring's way.
    ///
    /// A settled session grows a ring that reaches ~27 px below the face at this bot size — much
    /// further than the 9 px the readout sits at while the bot is working. Parking it low enough for
    /// the ring at all times would leave it floating whenever there is no ring, which is most of the
    /// time; so it slides down exactly as far as the ring has faded in, and the ring pushes it aside
    /// over the same 250 ms. (Kuba, 2026-10-21: the green ring ran straight through the bar and the
    /// number on a finished session.)
    /// </summary>
    private void PositionContext()
    {
        if (_ctxBar is null) return;
        double reach = _orb is null ? 0 : _orb.RingReach + 4;
        Canvas.SetTop(_ctxBar, _ctxRestTop + reach * Math.Clamp(_ringAlpha, 0, 1));
    }

    /// <summary>
    /// Size the tab to what it says, and centre its line.
    ///
    /// It used to be a fixed 220 px, which was fine while the text was always "N working" and
    /// stopped being fine the moment a finished count joined it. Measuring the runs is cheaper than
    /// guessing at a width and being wrong in one direction (a truncated summary) or the other
    /// (a pill with a hole in it). The tab stays centred on the screen's top edge.
    ///
    /// The line is positioned by its own measured height so its optical centre stays where a single
    /// 10 px TextBlock put it (y≈13.7) whatever the tallest run happens to be.
    /// </summary>
    private void LayoutTab()
    {
        if (_tabHost is null || _tabLine is null || _tabBg is null || _tabChevron is null) return;
        double lineW = _tabLine.LineWidth();
        Canvas.SetTop(_tabLine, 13.7 - _tabLine.LineHeight() / 2);

        double textX = Canvas.GetLeft(_tabLine);
        // runs, a breath, the chevron, and room before the rounded corner
        double w = Math.Min(WinW - 24, Math.Max(128, textX + lineW + 34));
        if (Math.Abs(w - _tabHost.Width) < 0.5) return;
        _tabHost.Width = w;
        _tabBg.Width = w;
        Canvas.SetLeft(_tabChevron, w - 24);
        Canvas.SetLeft(_tabHost, (WinW - w) / 2);
    }

    // ── the live data ───────────────────────────────────────────────────────────

    /// <summary>The static part of the board: the task lines and the chips.</summary>
    private void RebuildStatic()
    {
        if (_linesHost is null || _chipHost is null) return;
        var d = _data;
        _linesHost.Children.Clear();
        _chipHost.Children.Clear();
        _rowViews.Clear();
        _foldIndex = -1;                 // a rebuild cancels a fold: the row it was folding is gone
        _foldAt = -1;

        double textX = d.Running > 1 ? 132 : 110;
        for (int i = 0; i < d.Tasks.Count; i++)
        {
            var row = d.Tasks[i];
            var line = Draw.TaskLine(row.Text, row.Kind, 220, row.Teammate, row.Tail);

            // a row is a button: it asks the orb to wear that session (see Build's pinnedId).
            // The window is WS_EX_NOACTIVATE, so the click does not take the focus off whatever
            // the human is actually doing — it is a poke at the display, not at the desktop.
            if (row.Id.Length > 0)
            {
                string id = row.Id;
                line.Cursor = Cursors.Hand;
                line.ToolTip = "Show this session on the bot · double-click to open DeepSeek Harness";
                line.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    // the first click of a double-click already pinned the row; leave it pinned
                    if (e.ClickCount >= 2) OpenApp();
                    else TogglePin(id);
                };

                // A — the ✕ sits at the row's right edge, and only while THIS row is selected and
                // settled. A working or waiting row has nothing to retire: the host keeps those
                // whatever the tick says, so a ✕ there would be a button that does nothing.
                if (id == _pinnedId && row.Kind is "done" or "error")
                {
                    var x = Draw.DismissMark();
                    x.MouseLeftButtonDown += (_, e) => { e.Handled = true; Dismiss(id); };
                    _linesHost.Add(x, textX + 206, RowTop + i * RowPitch + 3);
                }
            }
            _linesHost.Add(line, textX, RowTop + i * RowPitch);
            _rowViews.Add(line);
        }
        for (int i = 0; i < Math.Min(4, d.Chips.Count); i++)
            _chipHost.Add(Draw.ChipName(d.Chips[i].Label, d.Chips[i].State, 178, 30), CardW - 22 - 178, 20 + i * 36);
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
        RebuildStatic();          // the ✕ appears and disappears with the selection
    }

    /// <summary>
    /// Bring DeepSeek Harness to the front through its own `dsh://open` link — the one deep link the
    /// app registers. It focuses the app's window; the app has no link that opens one session, so the
    /// row the human double-clicked stays pinned here instead.
    /// </summary>
    private void OpenApp()
    {
        try
        {
            Native.LendForeground();
            Process.Start(new ProcessStartInfo("dsh://open") { UseShellExecute = true });
            Log.Write("open app — dsh://open");
        }
        catch (Exception e) { Log.Write("open app failed: " + e.Message); }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Start the row's fold before the write goes out: the leave should look like something the
    /// human did, not like the row blinking out at the next poll. If the write fails the poll
    /// rebuilds the row where it was.
    /// </summary>
    private void StartFold(string id)
    {
        int index = _data.Tasks.FindIndex(row => row.Id == id);
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
            if (_pinnedId == id) _pinnedId = null;
            PollState();
        }
        catch (Exception e)
        {
            Log.Write("dismiss failed: " + e.Message);
        }
    }

    /// <summary>
    /// The fallback while the running host has not published `finishedUrl` (host@5 and earlier).
    ///
    /// It writes the SAME file and the SAME value the sidebar's ✓ writes — `manual.json`, the
    /// human's own marks — which this feature already reads, so it is not a second opinion. The
    /// overlay's host re-reads that file on every write cycle, so the row goes within a tick. The
    /// difference is only that `plugins/session-finished` keeps its own copy in memory until it
    /// next applies, so the app's sidebar tick can lag behind until then.
    /// </summary>
    private void WriteManualMark(string id)
    {
        try
        {
            // The host's own default, not a path derived from --state: the state file may be a
            // fixture, and the mark always belongs to the machine's real store.
            string stateDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "state");
            string file = Path.Combine(stateDir, "session-finished", "manual.json");
            var marks = File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new()
                : new Dictionary<string, string>();
            marks[id] = "finished";
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
        for (int i = 0; i < _backOrbs.Count; i++)
            _backOrbs[i].SetColours(Draw.Grey, i < d.BackIdentities.Count ? d.BackIdentities[i] : Draw.Teal);
        for (int i = 0; i < _miniBots.Count; i++)
        {
            _miniBots[i].SetColours(d.MiniStates[i], d.MiniIdentities[i]);
            _miniStates[i] = d.MiniStates[i];
            _miniStatus[i] = i < d.MiniStatus.Count ? d.MiniStatus[i] : "working";
        }
        UpdateContext();
    }

    /// <summary>The words that move on their own: the tab's summary, the headline and the sub.</summary>
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
    }

    /// <summary>Take a new board: rebuild what changed, re-colour what did not.</summary>
    private void ApplyBoard(BoardData next)
    {
        string before = _data.Status;
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
        string logKey = $"{board.SessionCount}|{board.MiniStates.Count}|{board.Title}|{board.ContentKey}|{board.Stale}";
        if (logKey != _lastLoggedKey)
        {
            _lastLoggedKey = logKey;
            Log.Write($"state — {board.SessionCount} session(s), {board.MiniStates.Count} in the tab" +
                      (board.MiniOverflow > 0 ? $" (+{board.MiniOverflow})" : "") +
                      $", status {(board.Stale ? "stale" : board.Status)}, retire {state.Retire}" +
                      $", context {(board.HasBudget ? $"{board.ContextTokens}/{board.ContextBudget}" : "no budget")}");
            Log.Write($"  tab \"{board.TabText}\" · title \"{board.Title}\" · sub \"{board.Sub}\"");
            Log.Write($"  lines {string.Join(" | ", board.Tasks.Select(t => $"[{t.Kind}] {t.Text}"))}");
            Log.Write($"  chips {string.Join(" | ", board.Chips.Select(c => $"{c.Label}:{c.State}"))}");
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
        bool mayOpen = _forceOpen || _want || t < _collapseAt;
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

        bool arc = false;
        double bend = 0, spread = 1, tilt = 0, ringTarget = 0;

        if (frontState == "done")
        {
            ringTarget = 1;
            double et = _freezeMs >= 0 ? _freezeMs : poseFrom(_finishStart);
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
            double k = Ease(Math.Min(1, poseFrom(_errorStart) / 700.0));
            open = 1 - .6 * k;
            tilt = 15 * k;
            ly += k;
        }

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
        if (!_ringSeeded) { _ringSeeded = true; _ringAlpha = ringTarget; }
        _ringAlpha += (ringTarget - _ringAlpha) * Math.Min(1, dt / 0.25);
        double ringPulse = frontState == "attention" ? .72 + .28 * Math.Abs(Math.Sin(2 * Math.PI * st / 1.2)) : 1;
        _orb?.SetRing(_ringAlpha, ringPulse);
        PositionContext();

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
            _miniBots[i].Set(miniBlink, miniGlow, status);

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
                    Canvas.SetTop(view, RowTop + j * RowPitch - RowPitch * e);
                }
            }
            if (e >= 1) { _foldIndex = -1; _foldAt = -1; }
        }

        // breathe — 3.2 s sine, ±1.5 %
        double s = 1 + 0.015 * Math.Sin(2 * Math.PI * t / 3.2);
        if (_orb is not null)
        {
            if (_orb.RenderTransform is not ScaleTransform sc)
            { sc = new ScaleTransform(); _orb.RenderTransform = sc; }
            sc.ScaleX = s; sc.ScaleY = s;
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
        int quns = Native.UserNotificationState();
        bool geoFull = Native.ForegroundIsFullscreen();
        bool qunsHide = quns is 1 or 3 or 4;      // 1 = session locked, 3 = D3D fullscreen, 4 = presenting
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
